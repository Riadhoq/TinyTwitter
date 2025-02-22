﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TinyTwitter
{
	public class OAuthInfo
	{
		public string ConsumerKey { get; set; }
		public string ConsumerSecret { get; set; }
		public string AccessToken { get; set; }
		public string AccessSecret { get; set; }
	}

	public class Tweet
	{
		public long Id { get; set; }
		public DateTime CreatedAt { get; set; }
		public string UserName { get; set; }
		public string ScreenName { get; set; }
		public string Text { get; set; }
	}

	public class TinyTwitter
	{
		private readonly OAuthInfo oauth;

		public TinyTwitter(OAuthInfo oauth)
		{
			this.oauth = oauth;
		}

		public void UpdateStatus(string message)
		{
			new RequestBuilder(oauth, "POST", "https://api.twitter.com/1.1/statuses/update.json")
				.AddParameter("status", message)
				.Execute();
		}

		public bool SendMessage(string message, string screenName)
		{
		    var userId = GetIdByScreenName(screenName);

		    var eventObj =  new
		    {
			type = "message_create",
			message_create = new
			{
			    target = new
			    {
				recipient_id = userId
			    },
			    message_data = new {text = message}
			},
		    };

		    try
		    {
			new RequestBuilder(oauth, "POST",
				"https://api.twitter.com/1.1/direct_messages/events/new.json")
			    .Execute(true, eventObj);
			return true;
		    }
		    catch
		    {
			return false;
		    }

		}

		public string GetIdByScreenName(string screenName)
		{
		    var response = new RequestBuilder(oauth, "GET", "https://api.twitter.com/1.1/users/show.json")
			.AddParameter("screen_name", screenName)
			.Execute();

		    dynamic deserializedResponse = new JavaScriptSerializer().DeserializeObject(response);

		    if (deserializedResponse != null && deserializedResponse["id_str"] != null)
		    {
			return deserializedResponse["id_str"].ToString();
		    }

		    return null;

		}

		public IEnumerable<Tweet> GetHomeTimeline(long? sinceId = null, long? maxId = null, int? count = 20)
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/home_timeline.json", sinceId, maxId, count, "");
		}

		public IEnumerable<Tweet> GetMentions(long? sinceId = null, long? maxId = null, int? count = 20)
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/mentions.json", sinceId, maxId, count, "");
		}

		public IEnumerable<Tweet> GetUserTimeline(long? sinceId = null, long? maxId = null, int? count = 20, string screenName = "")
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/user_timeline.json", sinceId, maxId, count, screenName);
		}

		private IEnumerable<Tweet> GetTimeline(string url, long? sinceId, long? maxId, int? count, string screenName)
		{
			var builder = new RequestBuilder(oauth, "GET", url);

			if (sinceId.HasValue)
				builder.AddParameter("since_id", sinceId.Value.ToString());

			if (maxId.HasValue)
				builder.AddParameter("max_id", maxId.Value.ToString());

			if (count.HasValue)
				builder.AddParameter("count", count.Value.ToString());

			if (screenName != "")
				builder.AddParameter("screen_name", screenName);

			var responseContent = builder.Execute();

			var serializer = new JavaScriptSerializer();

			var tweets = (object[])serializer.DeserializeObject(responseContent);

			return tweets.Cast<Dictionary<string, object>>().Select(tweet =>
			{
				var user = ((Dictionary<string, object>)tweet["user"]);
				var date = DateTime.ParseExact(tweet["created_at"].ToString(),
					"ddd MMM dd HH:mm:ss zz00 yyyy",
					CultureInfo.InvariantCulture).ToLocalTime();

				return new Tweet
				{
					Id = (long)tweet["id"],
					CreatedAt = date,
					Text = (string)tweet["text"],
					UserName = (string)user["name"],
					ScreenName = (string)user["screen_name"]
				};
			}).ToArray();
		}

		#region RequestBuilder

		public class RequestBuilder
		{
			private const string VERSION = "1.0";
			private const string SIGNATURE_METHOD = "HMAC-SHA1";

			private readonly OAuthInfo oauth;
			private readonly string method;
			private readonly IDictionary<string, string> customParameters;
			private readonly string url;

			public RequestBuilder(OAuthInfo oauth, string method, string url)
			{
				this.oauth = oauth;
				this.method = method;
				this.url = url;
				customParameters = new Dictionary<string, string>();
			}

			public RequestBuilder AddParameter(string name, string value)
			{
				customParameters.Add(name, value.EncodeRFC3986());
				return this;
			}

			public string Execute(bool isSendMessage = false, object eventObj = null)
			    {
				var timespan = GetTimestamp();
				var nonce = CreateNonce();

				var parameters = new Dictionary<string, string>(customParameters);
				AddOAuthParameters(parameters, timespan, nonce);

				var signature = GenerateSignature(parameters);
				var headerValue = GenerateAuthorizationHeaderValue(parameters, signature);

				var request = (HttpWebRequest)WebRequest.Create(GetRequestUrl());
				request.Method = method;
				request.ContentType = isSendMessage ? "application/json": "application/x-www-form-urlencoded";

				request.Headers.Add("Authorization", headerValue);

				if (isSendMessage && eventObj != null)
				{
				    WriteRequestBody(request, eventObj);
				}
				else
				{
				    WriteRequestBody(request);
				}

				// It looks like a bug in HttpWebRequest. It throws random TimeoutExceptions
				// after some requests. Abort the request seems to work. More info:
				// http://stackoverflow.com/questions/2252762/getrequeststream-throws-timeout-exception-randomly

				var response = request.GetResponse();

				string content;

				using (var stream = response.GetResponseStream())
				{
				    using (var reader = new StreamReader(stream))
				    {
					content = reader.ReadToEnd();
				    }
				}

				request.Abort();

				return content;
			    }

			private void WriteRequestBody(HttpWebRequest request)
			{
				if (method == "GET")
					return;

				var requestBody = Encoding.ASCII.GetBytes(GetCustomParametersString());
				using (var stream = request.GetRequestStream())
					stream.Write(requestBody, 0, requestBody.Length);
			}

			public void WriteRequestBody(HttpWebRequest request, object eventObj)
			    {
				using (var streamWriter = new StreamWriter(request.GetRequestStream()))
				{
				    string json = new JavaScriptSerializer().Serialize( new
				    {
					@event = eventObj
				    });

				    streamWriter.Write(json);
				}

			    }

			private string GetRequestUrl()
			{
				if (method != "GET" || customParameters.Count == 0)
					return url;

				return string.Format("{0}?{1}", url, GetCustomParametersString());
			}

			private string GetCustomParametersString()
			{
				return customParameters.Select(x => string.Format("{0}={1}", x.Key, x.Value)).Join("&");
			}

			private string GenerateAuthorizationHeaderValue(IEnumerable<KeyValuePair<string, string>> parameters, string signature)
			{
				return new StringBuilder("OAuth ")
					.Append(parameters.Concat(new KeyValuePair<string, string>("oauth_signature", signature))
								.Where(x => x.Key.StartsWith("oauth_"))
								.Select(x => string.Format("{0}=\"{1}\"", x.Key, x.Value.EncodeRFC3986()))
								.Join(","))
					.ToString();
			}

			private string GenerateSignature(IEnumerable<KeyValuePair<string, string>> parameters)
			{
				var dataToSign = new StringBuilder()
					.Append(method).Append("&")
					.Append(url.EncodeRFC3986()).Append("&")
					.Append(parameters
								.OrderBy(x => x.Key)
								.Select(x => string.Format("{0}={1}", x.Key, x.Value))
								.Join("&")
								.EncodeRFC3986());

				var signatureKey = string.Format("{0}&{1}", oauth.ConsumerSecret.EncodeRFC3986(), oauth.AccessSecret.EncodeRFC3986());
				var sha1 = new HMACSHA1(Encoding.ASCII.GetBytes(signatureKey));

				var signatureBytes = sha1.ComputeHash(Encoding.ASCII.GetBytes(dataToSign.ToString()));
				return Convert.ToBase64String(signatureBytes);
			}

			private void AddOAuthParameters(IDictionary<string, string> parameters, string timestamp, string nonce)
			{
				parameters.Add("oauth_version", VERSION);
				parameters.Add("oauth_consumer_key", oauth.ConsumerKey);
				parameters.Add("oauth_nonce", nonce);
				parameters.Add("oauth_signature_method", SIGNATURE_METHOD);
				parameters.Add("oauth_timestamp", timestamp);
				parameters.Add("oauth_token", oauth.AccessToken);
			}

			private static string GetTimestamp()
			{
				return ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
			}

			private static string CreateNonce()
			{
				return new Random().Next(0x0000000, 0x7fffffff).ToString("X8");
			}
		}

		#endregion
	}

	public static class TinyTwitterHelperExtensions
	{
		public static string Join<T>(this IEnumerable<T> items, string separator)
		{
			return string.Join(separator, items.ToArray());
		}

		public static IEnumerable<T> Concat<T>(this IEnumerable<T> items, T value)
		{
			return items.Concat(new[] { value });
		}

		public static string EncodeRFC3986(this string value)
		{
			// From Twitterizer http://www.twitterizer.net/

			if (string.IsNullOrEmpty(value))
				return string.Empty;

			var encoded = Uri.EscapeDataString(value);

			return Regex
				.Replace(encoded, "(%[0-9a-f][0-9a-f])", c => c.Value.ToUpper())
				.Replace("(", "%28")
				.Replace(")", "%29")
				.Replace("$", "%24")
				.Replace("!", "%21")
				.Replace("*", "%2A")
				.Replace("'", "%27")
				.Replace("%7E", "~");
		}
	}
}
