using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Updating;

namespace CodexAutoContinue.Tests
{
    public static class UpdateClientTests
    {
        private const string Repository = "https://github.com/tvcmall-dev/codex-auto-continue";
        private static int checks;
        public static void Run()
        {
            checks = 0;
            Versions();
            ReleaseSelection();
            ChecksumFallback();
            DownloadIntegrity();
            DownloadOrigins();
            Console.WriteLine("Update client checks passed: " + checks);
        }
        private static void Versions()
        {
            SemanticVersion value;
            Check(SemanticVersion.TryParse("v1.2.3-preview.1+commit", out value), "valid version parse");
            Check(value.IsPrerelease && value.ToString() == "1.2.3-preview.1+commit", "version normalized value");
            var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1" };
            for (var i = 1; i < ordered.Length; i++) Check(SemanticVersion.Parse(ordered[i - 1]).CompareTo(SemanticVersion.Parse(ordered[i])) < 0, "SemVer ordering " + ordered[i]);
            Check(SemanticVersion.Parse("1.0.0+a").CompareTo(SemanticVersion.Parse("1.0.0+b")) == 0, "build metadata does not change precedence");
            Check(SemanticVersion.Parse("2.0.0-preview.99999999999999999999").CompareTo(SemanticVersion.Parse("2.0.0-preview.2")) > 0, "long numeric prerelease identifiers");
            foreach (var bad in new[] { "1.2", "01.2.3", "1.2.3-01", "1.2.3-", "1.2.3+a..b", "1.2.3/../../x", "999999999999999.0.0", "1.2.3\n", null })
                Check(!SemanticVersion.TryParse(bad, out value), "invalid version rejected");
        }
        private static void ReleaseSelection()
        {
            var digest = Hash(Encoding.UTF8.GetBytes("payload"));
            var handler = new Handler(request => JsonResponse(new[] {
                Release("v1.0.1", digest), Release("v2.0.0-preview.1", digest, prerelease:true),
                Release("v9.0.0", digest, draft:true), Release("v1.0.2", digest)
            }));
            using (var client = new GithubReleaseClient(handler))
            {
                var stable = client.FindUpdateAsync("1.0.0", false, CancellationToken.None).GetAwaiter().GetResult();
                Check(stable.Version == "1.0.2" && stable.Sha256 == digest, "highest stable rather than release order");
                var preview = client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult();
                Check(preview.Version == "2.0.0-preview.1", "preview channel enabled");
                Check(client.FindUpdateAsync("10.0.0", true, CancellationToken.None).GetAwaiter().GetResult() == null, "no downgrade");
                Check(handler.LastUrl == "https://api.github.com/repos/tvcmall-dev/codex-auto-continue/releases?per_page=100", "fixed release origin");
                Check(handler.NoCredentials, "public checks contain no auth/cookies");
            }
            using (var client = new GithubReleaseClient(new Handler(request => JsonResponse(new[] { Release("v1.1.0", digest, url:"https://github.com/other/project/releases/download/v1.1.0/CodexAutoContinue.exe") }))))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "different repository rejected");
            using (var client = new GithubReleaseClient(new Handler(request => JsonResponse(new[] { Release("v1.1.0", "bad-digest") }))))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "malformed digest rejected");
            using (var client = new GithubReleaseClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.Forbidden))))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "API limit handled as error");
        }
        private static void ChecksumFallback()
        {
            var digest = Hash(Encoding.UTF8.GetBytes("payload"));
            var release = Release("v1.1.0", null, checksum:true);
            using (var client = new GithubReleaseClient(new Handler(request => request.RequestUri.Host == "api.github.com" ? JsonResponse(new[] { release }) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(digest + "  CodexAutoContinue.exe\n" + digest + "  other.zip\n") })))
                Check(client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult().Sha256 == digest, "exact filename checksum fallback");
            using (var client = new GithubReleaseClient(new Handler(request => request.RequestUri.Host == "api.github.com" ? JsonResponse(new[] { release }) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(digest + "  wrong.exe\n") })))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "wrong checksum filename rejected");
            using (var client = new GithubReleaseClient(new Handler(request => request.RequestUri.Host == "api.github.com" ? JsonResponse(new[] { release }) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(digest + "  CodexAutoContinue.exe\n" + new string('a',64) + "  CodexAutoContinue.exe\n") })))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "ambiguous checksums rejected");
            using (var client = new GithubReleaseClient(new Handler(request => JsonResponse(new[] { Release("v1.1.0", null) }))))
                Reject(() => client.FindUpdateAsync("1.0.0", true, CancellationToken.None).GetAwaiter().GetResult(), "missing checksum fails closed");
        }
        private static void DownloadIntegrity()
        {
            var folder = Path.Combine(Path.GetTempPath(), "CodexUpdateClientTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "payload.exe");
            var bytes = Encoding.UTF8.GetBytes("fixture payload, never executed");
            var release = new ReleaseInfo { Version = "1.1.0", DownloadUrl = Repository + "/releases/download/v1.1.0/CodexAutoContinue.exe", Sha256 = Hash(bytes), Size = bytes.Length };
            try
            {
                using (var client = new GithubReleaseClient(new Handler(request => BytesResponse(bytes))))
                {
                    client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(File.ReadAllText(path) == Encoding.UTF8.GetString(bytes), "verified download persists exact bytes");
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "existing destination is not overwritten");
                    Check(File.ReadAllText(path) == Encoding.UTF8.GetString(bytes), "existing file preserved on failure");
                    File.Delete(path);
                    release.Sha256 = new string('a',64);
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "digest mismatch rejected");
                    Check(!File.Exists(path), "hash mismatch cleans partial payload");
                    release.Sha256 = Hash(bytes); release.Size = bytes.Length - 1;
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "size mismatch rejected");
                    Check(!File.Exists(path), "size mismatch cleans partial payload");
                    release.Size = 30L * 1024 * 1024 + 1;
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "oversized metadata rejected");
                    release.Size = bytes.Length;
                    var cancelled = new CancellationToken(true);
                    try { client.DownloadAsync(release, path, null, cancelled).GetAwaiter().GetResult(); throw new Exception("canceled download accepted"); } catch (OperationCanceledException) { checks++; }
                    Check(!File.Exists(path), "canceled download leaves no executable");
                }
                using (var client = new GithubReleaseClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContentFixture(null) })))
                {
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "stream acquisition failure reported");
                    Check(!File.Exists(path), "stream acquisition failure cleans created destination");
                }
                using (var client = new GithubReleaseClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContentFixture(bytes) })))
                {
                    release.Size = bytes.Length - 1;
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "chunked overflow rejected without Content-Length");
                    Check(!File.Exists(path), "chunked overflow cleans payload");
                    release.Size = bytes.Length + 1;
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "chunked truncation rejected without Content-Length");
                    Check(!File.Exists(path), "chunked truncation cleans payload");
                }
            }
            finally { Directory.Delete(folder, true); }
        }
        private static void DownloadOrigins()
        {
            var folder = Path.Combine(Path.GetTempPath(), "CodexUpdateOrigins-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "payload.exe");
            var bytes = new byte[] { 1, 2, 3 };
            var release = new ReleaseInfo { Version = "1.1.0", DownloadUrl = Repository + "/releases/download/v1.1.0/CodexAutoContinue.exe", Sha256 = Hash(bytes), Size = bytes.Length };
            try
            {
                using (var client = new GithubReleaseClient(new Handler(request => request.RequestUri.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/asset?signature=public-test") : BytesResponse(bytes))))
                {
                    client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(File.Exists(path), "approved GitHub asset redirect"); File.Delete(path);
                }
                foreach (var url in new[] { "https://evil.example/payload.exe", "http://release-assets.githubusercontent.com/payload", "https://github.com.evil.example/payload", "https://user:secret@github.com/payload" })
                    using (var client = new GithubReleaseClient(new Handler(request => Redirect(url))))
                    {
                        Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "untrusted redirect rejected");
                        Check(!File.Exists(path), "redirect failure leaves no payload");
                    }
                release.DownloadUrl = "https://github.com/other/project/releases/download/v1.1.0/CodexAutoContinue.exe";
                using (var client = new GithubReleaseClient(new Handler(request => BytesResponse(bytes))))
                    Reject(() => client.DownloadAsync(release, path, null, CancellationToken.None).GetAwaiter().GetResult(), "download cannot bypass pinned repository");
            }
            finally { Directory.Delete(folder, true); }
        }
        private static object Release(string tag, string digest, bool prerelease = false, bool draft = false, string url = null, bool checksum = false)
        {
            var assets = new List<object> { new { name = "CodexAutoContinue.exe", state = "uploaded", size = 7, digest = digest == null ? null : "sha256:" + digest, browser_download_url = url ?? Repository + "/releases/download/" + tag + "/CodexAutoContinue.exe" } };
            if (checksum) assets.Add(new { name = "SHA256SUMS.txt", state = "uploaded", size = 200, digest = (string)null, browser_download_url = Repository + "/releases/download/" + tag + "/SHA256SUMS.txt" });
            return new { tag_name = tag, prerelease, draft, body = "版本说明", assets };
        }
        private static HttpResponseMessage JsonResponse(object body) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json") }; }
        private static HttpResponseMessage BytesResponse(byte[] bytes) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
        private static HttpResponseMessage Redirect(string uri) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(uri); return response; }
        private static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("Update client: " + message); }
        private static void Reject(Action action, string message) { try { action(); } catch (Exception e) when (e is InvalidOperationException || e is IOException || e is ArgumentException || e is HttpRequestException) { checks++; return; } throw new Exception("Update client accepted invalid input: " + message); }
        private sealed class StreamContentFixture : HttpContent
        {
            private readonly byte[] bytes;
            public StreamContentFixture(byte[] bytes) { this.bytes = bytes; }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) { return Task.FromException(new IOException("Fixture stream failure")); }
            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return bytes == null ? Task.FromException<Stream>(new IOException("Fixture stream failure")) : Task.FromResult<Stream>(new MemoryStream(bytes));
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;
            public string LastUrl;
            public bool NoCredentials = true;
            public Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) { this.responder = responder; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                LastUrl = request.RequestUri.AbsoluteUri;
                NoCredentials &= request.Headers.Authorization == null && !request.Headers.Contains("Cookie");
                return Task.FromResult(responder(request));
            }
        }
    }
}
