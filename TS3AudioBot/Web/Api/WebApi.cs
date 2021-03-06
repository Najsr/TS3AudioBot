// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Web.Api
{
	using CommandSystem;
	using CommandSystem.Ast;
	using CommandSystem.CommandResults;
	using CommandSystem.Commands;
	using Dependency;
	using Helper;
	using Newtonsoft.Json;
	using Sessions;
	using System;
	using System.IO;
	using System.Net;
	using System.Security.Cryptography;
	using System.Security.Principal;
	using System.Text;
	using System.Text.RegularExpressions;

	public sealed class WebApi : WebComponent
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static readonly Regex DigestMatch = new Regex(@"\s*(\w+)\s*=\s*""([^""]*)""\s*,?", Util.DefaultRegexConfig);
		private static readonly MD5 Md5Hash = MD5.Create();

		private const string InfoNonceAdded = "Added a nonce to your request";
		private const string ErrorUnknownRealm = "Unknown realm";
		private const string ErrorNoUserOrToken = "Unknown user or no active token found";
		private const string ErrorAuthFailure = "Authentication failed";
		private const string ErrorAnonymousDisabled = "This bot does not allow anonymous api requests";
		private const string ErrorUnsupportedScheme = "Unsupported authentication scheme";

		public bool AllowAnonymousRequest { get; set; } = true;

		public CoreInjector CoreInjector { get; set; }
		public CommandManager CommandManager { get; set; }
		public TokenManager TokenManager { get; set; }

		public override void DispatchCall(HttpListenerContext context)
		{
			using (var response = context.Response)
			{
				response.AddHeader("Access-Control-Allow-Origin", "*");

				var authResult = Authenticate(context);
				if (!authResult.Ok)
				{
					Log.Debug("Authorization failed!");
					ReturnError(CommandExceptionReason.Unauthorized, authResult.Error, context.Response);
					return;
				}
				if (!AllowAnonymousRequest && authResult.Value.anonymous)
				{
					Log.Debug("Unauthorized request!");
					ReturnError(CommandExceptionReason.Unauthorized, ErrorAnonymousDisabled, context.Response);
					return;
				}
				//AllowAnonymousRequest && invoker == null 

				var requestUrl = new Uri(Dummy, context.Request.RawUrl);
				ProcessApiV1Call(requestUrl, context.Response, authResult.Value.invoker);
			}
		}

		private void ProcessApiV1Call(Uri uri, HttpListenerResponse response, InvokerData invoker)
		{
			string apirequest = uri.AbsolutePath.Substring("/api".Length);
			var ast = CommandParser.ParseCommandRequest(apirequest, '/', '/');
			UnescapeAstTree(ast);

			var command = CommandManager.CommandSystem.AstToCommandResult(ast);

			var execInfo = new ExecutionInformation(CoreInjector.CloneRealm<CoreInjector>());
			execInfo.AddDynamicObject(new CallerInfo(apirequest, true));
			if (invoker != null) execInfo.AddDynamicObject(invoker);
			// todo creating token usersessions is now possible

			try
			{
				var res = command.Execute(execInfo, Array.Empty<ICommand>(), XCommandSystem.ReturnJsonOrNothing);

				if (res.ResultType == CommandResultType.Empty)
				{
					response.StatusCode = (int)HttpStatusCode.NoContent;
				}
				else if (res.ResultType == CommandResultType.Json)
				{
					response.StatusCode = (int)HttpStatusCode.OK;
					var sRes = (JsonCommandResult)res;
					using (var responseStream = new StreamWriter(response.OutputStream))
						responseStream.Write(sRes.JsonObject.Serialize());
				}
			}
			catch (CommandException ex)
			{
				ReturnError(ex, response);
			}
			catch (Exception ex)
			{
				if (ex is NotImplementedException)
					response.StatusCode = (int)HttpStatusCode.NotImplemented;
				else
					response.StatusCode = (int)HttpStatusCode.InternalServerError;
				Log.Error(ex, "Unexpected command error");
				using (var responseStream = new StreamWriter(response.OutputStream))
					responseStream.Write(new JsonError(ex.Message, CommandExceptionReason.Unknown).Serialize());
			}
		}

		private static void ReturnError(CommandException ex, HttpListenerResponse response) => ReturnError(ex.Reason, ex.Message, response);

		private static void ReturnError(CommandExceptionReason reason, string message, HttpListenerResponse response)
		{
			switch (reason)
			{
			case CommandExceptionReason.Unknown:
			case CommandExceptionReason.InternalError:
				response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return;

			case CommandExceptionReason.Unauthorized:
				response.StatusCode = (int)HttpStatusCode.Unauthorized;
				break;

			case CommandExceptionReason.MissingRights:
			case CommandExceptionReason.NotSupported:
				response.StatusCode = (int)HttpStatusCode.Forbidden;
				break;

			case CommandExceptionReason.CommandError:
			case CommandExceptionReason.AmbiguousCall:
			case CommandExceptionReason.MissingParameter:
			case CommandExceptionReason.NoReturnMatch:
			case CommandExceptionReason.MissingContext:
				response.StatusCode = 422; // Unprocessable Entity
				break;

			case CommandExceptionReason.FunctionNotFound:
				response.StatusCode = (int)HttpStatusCode.NotFound;
				break;

			default:
				throw Util.UnhandledDefault(reason);
			}

			using (var responseStream = new StreamWriter(response.OutputStream))
				responseStream.Write(JsonConvert.SerializeObject(new JsonError(message, reason)));
		}

		private static void UnescapeAstTree(AstNode node)
		{
			switch (node.Type)
			{
			case AstType.Command:
				var astCom = (AstCommand)node;
				foreach (var child in astCom.Parameter)
					UnescapeAstTree(child);
				break;
			case AstType.Value:
				var astVal = (AstValue)node;
				astVal.Value = Uri.UnescapeDataString(astVal.Value);
				break;
			case AstType.Error: break;
			default:
				throw Util.UnhandledDefault(node.Type);
			}
		}

		private R<(bool anonymous, InvokerData invoker)> Authenticate(HttpListenerContext context)
		{
			var identity = GetIdentity(context);
			if (identity == null)
				return (true, null);

			var result = TokenManager.GetToken(identity.Name);
			if (!result.Ok)
				return ErrorNoUserOrToken;

			var token = result.Value;
			var invoker = new InvokerData(identity.Name)
			{
				Token = token.Value,
			};

			switch (identity.AuthenticationType)
			{
			case "Basic":
				var identityBasic = (HttpListenerBasicIdentity)identity;

				if (token.Value != identityBasic.Password)
					return ErrorAuthFailure;

				return (false, invoker);

			case "Digest":
				var identityDigest = (HttpListenerDigestIdentity)identity;

				if (!identityDigest.IsAuthenticated)
				{
					var newNonce = token.CreateNonce();
					context.Response.AddHeader("WWW-Authenticate", $"Digest realm=\"{WebManager.WebRealm}\", nonce=\"{newNonce.Value}\"");
					return InfoNonceAdded;
				}

				if (identityDigest.Realm != WebManager.WebRealm)
					return ErrorUnknownRealm;

				if (identityDigest.Uri != context.Request.RawUrl)
					return ErrorAuthFailure;

				//HA1=MD5(username:realm:password)
				//HA2=MD5(method:digestURI)
				//response=MD5(HA1:nonce:HA2)
				var ha1 = HashString($"{identity.Name}:{identityDigest.Realm}:{token.Value}");
				var ha2 = HashString($"{context.Request.HttpMethod}:{identityDigest.Uri}");
				var response = HashString($"{ha1}:{identityDigest.Nonce}:{ha2}");

				if (identityDigest.Hash != response)
					return ErrorAuthFailure;

				ApiNonce nextNonce = token.UseNonce(identityDigest.Nonce);
				if (nextNonce == null)
					return ErrorAuthFailure;
				context.Response.AddHeader("WWW-Authenticate", $"Digest realm=\"{WebManager.WebRealm}\", nonce=\"{nextNonce.Value}\"");

				return (false, invoker);

			default:
				return ErrorUnsupportedScheme;
			}
		}

		private static IIdentity GetIdentity(HttpListenerContext context)
		{
			IIdentity identity = context.User?.Identity;
			if (identity != null)
				return identity;

			var headerVal = context.Request.Headers["Authorization"];
			if (string.IsNullOrEmpty(headerVal))
				return null;

			var authParts = headerVal.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
			if (authParts.Length < 2)
				return null;

			if (string.Equals(authParts[0], "DIGEST", StringComparison.OrdinalIgnoreCase))
			{
				string name = null;
				string hash = null;
				string nonce = null;
				string realm = null;
				string uri = null;

				for (var match = DigestMatch.Match(authParts[1]); match.Success; match = match.NextMatch())
				{
					var value = match.Groups[2].Value;
					switch (match.Groups[1].Value.ToUpper())
					{
					case "USERNAME": name = value; break;
					case "REALM": realm = value; break;
					case "NONCE": nonce = value; break;
					case "RESPONSE": hash = value; break;
					case "URI": uri = value; break;
					}
				}

				return new HttpListenerDigestIdentity(name, nonce, hash, realm, uri);
			}

			return null;
		}

		private static string HashString(string input)
		{
			var bytes = Util.Utf8Encoder.GetBytes(input);
			var hash = Md5Hash.ComputeHash(bytes);

			var result = new StringBuilder(hash.Length * 2);
			for (int i = 0; i < hash.Length; i++)
				result.Append(hash[i].ToString("x2"));
			return result.ToString();
		}
	}
}
