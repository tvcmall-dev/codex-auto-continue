using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;

namespace CodexAutoContinue.Notifications
{
    public sealed class Notice
    {
        public string Event { get; set; }
        public string Title { get; set; }
        public string ThreadId { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public int Attempts { get; set; }
        public DateTime? NextRetryUtc { get; set; }
    }
    public sealed class DeliveryResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int RetryAfterSeconds { get; set; }
    }
    public sealed class WebhookClient : IDisposable
    {
        private readonly HttpClient client;
        public WebhookClient(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 65536 };
        }
        public static void Validate(NotificationSettings settings)
        {
            Uri uri;
            if (settings == null || !Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("请输入有效的 HTTPS Webhook 地址，不含用户名、密码或片段。");
            if (settings.Provider != "DingTalk" && settings.Provider != "Feishu" && settings.Provider != "WeCom" && settings.Provider != "Generic")
                throw new ArgumentException("请选择支持的通知平台。");
        }
        public async Task<DeliveryResult> SendAsync(NotificationSettings settings, Notice notice)
        {
            Validate(settings);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var text = "Codex Auto Continue\n" + EventName(notice.Event) + "\n会话：" + Cut(notice.Title, 160) + "\nID：" + Cut(notice.ThreadId, 80) + "\n状态：" + Cut(notice.State, 60) + "\n原因：" + Cut(notice.Reason, 200) + "\n续接次数：" + notice.Attempts + (notice.NextRetryUtc.HasValue ? "\n下次重试：" + notice.NextRetryUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
            var url = settings.WebhookUrl;
            object payload;
            if (settings.Provider == "Generic")
                payload = new { source = "Codex Auto Continue", eventType = notice.Event, title = Cut(notice.Title, 160), threadId = notice.ThreadId, state = notice.State, reason = Cut(notice.Reason, 200), attempts = notice.Attempts, nextRetryUtc = notice.NextRetryUtc, timestampUtc = DateTime.UtcNow };
            else if (settings.Provider == "Feishu")
            {
                var body = new Dictionary<string, object> { { "msg_type", "text" }, { "content", new { text } } };
                if (!string.IsNullOrEmpty(settings.Secret))
                {
                    var seconds = (timestamp / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    body["timestamp"] = seconds;
                    body["sign"] = Hmac(seconds + "\n" + settings.Secret, "");
                }
                payload = body;
            }
            else
            {
                if (settings.Provider == "DingTalk" && !string.IsNullOrEmpty(settings.Secret))
                {
                    var stamp = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    url += (url.Contains("?") ? "&" : "?") + "timestamp=" + stamp + "&sign=" + Uri.EscapeDataString(Hmac(settings.Secret, stamp + "\n" + settings.Secret));
                }
                payload = new { msgtype = "text", text = new { content = text } };
            }
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json") })
                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    var retry = 0;
                    if (response.Headers.RetryAfter != null)
                    {
                        var delay = response.Headers.RetryAfter.Delta ?? (response.Headers.RetryAfter.Date - DateTimeOffset.UtcNow);
                        if (delay.HasValue) retry = (int)Math.Max(1, Math.Min(86400, Math.Ceiling(delay.Value.TotalSeconds)));
                    }
                    if (!response.IsSuccessStatusCode) return new DeliveryResult { Message = "通知投递失败，HTTP " + (int)response.StatusCode + "。", RetryAfterSeconds = retry };
                    if (settings.Provider != "Generic")
                    {
                        Dictionary<string, object> data;
                        try { data = Json.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false)); }
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return new DeliveryResult { Message = "通知平台返回了无法识别的响应。" }; }
                        object code;
                        var key = settings.Provider == "Feishu" ? "code" : "errcode";
                        if (data == null || !data.TryGetValue(key, out code) || Convert.ToString(code) != "0")
                            return new DeliveryResult { Message = "通知平台未确认成功，请检查机器人配置、关键词及签名。", RetryAfterSeconds = retry };
                    }
                    return new DeliveryResult { Success = true, Message = "通知平台已确认接收。" };
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            { return new DeliveryResult { Message = "通知连接失败或超时，请检查网络和 Webhook 设置。" }; }
        }
        private static string Hmac(string key, string input) { using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key))) return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(input))); }
        private static string Cut(string value, int length)
        {
            value = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"https?://\S+", "[链接]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return value.Length > length ? value.Substring(0, length) : value;
        }
        private static string EventName(string value) { return value == "recovery" ? "会话已恢复" : value == "exhausted" ? "自动续接次数已用尽" : value == "test" ? "测试通知" : "会话异常"; }
        public void Dispose() { client.Dispose(); }
    }
}
