using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Collections.Generic;
using CodexAutoContinue.Core;
using CodexAutoContinue.Notifications;
using CodexAutoContinue.Infrastructure;

namespace CodexAutoContinue.Tests
{
    public static class WebhookTests
    {
        public static void Run()
        {
            var settings = new NotificationSettings { Provider = "DingTalk", WebhookUrl = "https://example.invalid/robot?access_token=test", Secret = "test-secret" };
            var handler = new Handler();
            using (var client = new WebhookClient(handler))
            {
                var notice = new Notice { Event = "failure", Title = "测试会话", ThreadId = "thread", Reason = "网络连接中断" };
                var result = client.SendAsync(settings, notice).GetAwaiter().GetResult();
                Check(result.Success && handler.Url.Contains("timestamp=") && handler.Url.Contains("sign="), "DingTalk signed send");
                var query = HttpUtility.ParseQueryString(new Uri(handler.Url).Query);
                Check(query["sign"] == Sign(settings.Secret, query["timestamp"] + "\n" + settings.Secret), "DingTalk exact HMAC");
                Check(handler.Body.Contains("Codex Auto Continue") && !handler.Body.Contains(settings.Secret), "payload excludes credential");
                notice.Title = "测试 https://example.invalid/hook?access_token=private-title-token";
                client.SendAsync(settings, notice).GetAwaiter().GetResult();
                Check(!handler.Body.Contains("private-title-token"), "notification title does not forward URL credentials");
                handler.Response = "{\"errcode\":310000}";
                Check(!client.SendAsync(settings, notice).GetAwaiter().GetResult().Success, "HTTP 200 provider error is failure");
                handler.Response = "{}";
                Check(!client.SendAsync(settings, notice).GetAwaiter().GetResult().Success, "provider missing status is failure");
                settings.Provider = "Feishu"; handler.Response = "{\"code\":0}";
                Check(client.SendAsync(settings, notice).GetAwaiter().GetResult().Success && handler.Body.Contains("sign"), "Feishu signed body");
                var signed = Json.Deserialize<Dictionary<string, object>>(handler.Body);
                Check((string)signed["sign"] == Sign((string)signed["timestamp"] + "\n" + settings.Secret, ""), "Feishu exact HMAC");
                settings.Provider = "WeCom"; handler.Response = "{\"errcode\":0}";
                Check(client.SendAsync(settings, notice).GetAwaiter().GetResult().Success && !handler.Body.Contains("sign"), "WeCom body");
                settings.Provider = "Generic"; handler.Response = "accepted";
                Check(client.SendAsync(settings, notice).GetAwaiter().GetResult().Success && handler.Body.Contains("threadId"), "generic JSON body");
                handler.Status = (HttpStatusCode)429;
                var limited = client.SendAsync(settings, notice).GetAwaiter().GetResult();
                Check(!limited.Success && limited.RetryAfterSeconds >= 60, "rate limit backoff");
                settings.WebhookUrl = "http://example.invalid";
                try { WebhookClient.Validate(settings); throw new Exception("HTTP accepted"); } catch (ArgumentException) { }
                settings.WebhookUrl = "https://user:password@example.invalid";
                try { WebhookClient.Validate(settings); throw new Exception("URL user info accepted"); } catch (ArgumentException) { }
            }
        }
        private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        private static string Sign(string key, string input) { using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key))) return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(input))); }
        private sealed class Handler : HttpMessageHandler
        {
            public string Response = "{\"errcode\":0}";
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body, Url;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Url = request.RequestUri.AbsoluteUri;
                Body = await request.Content.ReadAsStringAsync();
                var response = new HttpResponseMessage(Status) { Content = new StringContent(Response) };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return response;
            }
        }
    }
}
