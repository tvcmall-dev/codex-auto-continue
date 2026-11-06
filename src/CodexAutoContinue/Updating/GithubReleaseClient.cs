using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Infrastructure;

namespace CodexAutoContinue.Updating
{
    public sealed class GithubReleaseClient : IReleaseClient
    {
        public const long MaximumAssetBytes = 30L * 1024 * 1024;
        public const string RepositoryUrl = "https://github.com/tvcmall-dev/codex-auto-continue";
        public const string ReleasesApi = "https://api.github.com/repos/tvcmall-dev/codex-auto-continue/releases?per_page=100";
        private static readonly Regex HashPattern = new Regex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex ChecksumLine = new Regex(@"^([a-fA-F0-9]{64})[ \t]+\*?CodexAutoContinue\.exe[ \t]*$", RegexOptions.CultureInvariant);
        private readonly HttpClient client;
        public GithubReleaseClient(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAutoContinue-Updater");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        public async Task<ReleaseInfo> FindUpdateAsync(string currentVersion, bool includePrerelease, CancellationToken cancellationToken)
        {
            var current = SemanticVersion.Parse(currentVersion);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                List<Release> releases;
                using (var response = await GetAsync(new Uri(ReleasesApi), false, timeout.Token).ConfigureAwait(false))
                {
                    var body = await ReadTextAsync(response, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                    try { releases = Json.Deserialize<List<Release>>(body); }
                    catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
                    { throw new InvalidOperationException("GitHub 返回的版本信息格式无效。"); }
                }
                if (releases == null) throw new InvalidOperationException("GitHub 未返回可识别的版本列表。");
                var candidates = new List<Tuple<Release, SemanticVersion>>();
                foreach (var release in releases)
                {
                    SemanticVersion version;
                    if (release == null || release.draft || !SemanticVersion.TryParse(release.tag_name, out version) || version.CompareTo(current) <= 0 ||
                        !includePrerelease && (release.prerelease || version.IsPrerelease)) continue;
                    candidates.Add(Tuple.Create(release, version));
                }
                foreach (var candidate in candidates.OrderByDescending(item => item.Item2))
                {
                    var release = candidate.Item1;
                    var assets = (release.assets ?? new List<Asset>()).Where(item => item != null && item.name == "CodexAutoContinue.exe" && item.state == "uploaded").ToList();
                    if (assets.Count == 0) continue;
                    if (assets.Count != 1) throw new InvalidOperationException("发布包包含重复的程序文件。");
                    var asset = assets[0];
                    ValidateAssetUri(asset.browser_download_url, release.tag_name, "CodexAutoContinue.exe");
                    ValidateSize(asset.size);
                    string digest;
                    if (String.IsNullOrWhiteSpace(asset.digest)) digest = await ReadChecksumAsync(release, timeout.Token).ConfigureAwait(false);
                    else if (asset.digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && HashPattern.IsMatch(asset.digest.Substring(7))) digest = asset.digest.Substring(7).ToLowerInvariant();
                    else throw new InvalidOperationException("发布包缺少有效的 SHA-256 校验值。");
                    var notes = release.body ?? "";
                    return new ReleaseInfo
                    {
                        Version = candidate.Item2.ToString(), Notes = notes.Length > 16000 ? notes.Substring(0, 16000) : notes,
                        PageUrl = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(release.tag_name),
                        DownloadUrl = asset.browser_download_url, Sha256 = digest, Size = asset.size
                    };
                }
                return null;
            }
        }
        public async Task DownloadAsync(ReleaseInfo release, string destination, IProgress<int> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (release == null || !HashPattern.IsMatch(release.Sha256 ?? "")) throw new ArgumentException("更新信息缺少有效的 SHA-256。");
            var expectedVersion = SemanticVersion.Parse(release.Version);
            ValidateSize(release.Size);
            var uri = ValidateDownloadUri(release.DownloadUrl, expectedVersion);
            if (String.IsNullOrWhiteSpace(destination)) throw new ArgumentException("更新下载路径无效。");
            var fullPath = Path.GetFullPath(destination);
            var created = false;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using (var response = await GetAsync(uri, true, timeout.Token).ConfigureAwait(false))
                    {
                        if (response.Content == null || response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != release.Size)
                            throw new InvalidOperationException("发布包大小与版本信息不一致。");
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                        using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true))
                        {
                            created = true;
                            using (var sha = SHA256.Create())
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                progress?.Report(0);
                                var buffer = new byte[16384]; long total = 0; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                                {
                                    timeout.Token.ThrowIfCancellationRequested();
                                    total += count;
                                    if (total > release.Size || total > MaximumAssetBytes) throw new InvalidOperationException("更新下载超出了声明大小。");
                                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                    progress?.Report((int)Math.Min(99, total * 100 / release.Size));
                                }
                                if (total != release.Size) throw new InvalidOperationException("更新下载不完整。");
                                sha.TransformFinalBlock(new byte[0], 0, 0);
                                var digest = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                                if (!String.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新文件 SHA-256 校验失败，已取消安装。");
                                timeout.Token.ThrowIfCancellationRequested();
                                progress?.Report(100);
                            }
                        }
                    }
                }
            }
            catch
            {
                if (created)
                {
                    try { File.Delete(fullPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        private async Task<string> ReadChecksumAsync(Release release, CancellationToken cancellationToken)
        {
            var assets = (release.assets ?? new List<Asset>()).Where(asset => asset != null && asset.name == "SHA256SUMS.txt" && asset.state == "uploaded").ToList();
            if (assets.Count != 1 || assets[0].size <= 0 || assets[0].size > 65536) throw new InvalidOperationException("发布包没有可验证的 SHA-256 清单。");
            var uri = ValidateAssetUri(assets[0].browser_download_url, release.tag_name, "SHA256SUMS.txt");
            using (var response = await GetAsync(uri, true, cancellationToken).ConfigureAwait(false))
            {
                var checksums = await ReadTextAsync(response, 65536, cancellationToken).ConfigureAwait(false);
                var matches = checksums.Split('\n').Select(line => ChecksumLine.Match(line.TrimEnd('\r'))).Where(match => match.Success).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("校验清单未唯一标识 CodexAutoContinue.exe。");
                return matches[0].Groups[1].Value.ToLowerInvariant();
            }
        }
        private async Task<HttpResponseMessage> GetAsync(Uri initial, bool asset, CancellationToken cancellationToken)
        {
            var uri = initial;
            for (var attempt = 0; attempt <= 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.Accept.ParseAdd(asset ? "application/octet-stream" : "application/vnd.github+json");
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        try
                        {
                            var location = response.Headers.Location;
                            if (!asset || attempt == 5 || location == null) throw new InvalidOperationException("更新服务器返回了不支持的重定向。");
                            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                            if (!IsHttps(uri) || uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com")
                                throw new InvalidOperationException("更新下载被重定向到不受信任的地址。");
                        }
                        finally { response.Dispose(); }
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        response.Dispose();
                        throw new InvalidOperationException(status == 403 || status == 429 ? "GitHub 暂时限制更新检查，请稍后重试。" : "无法读取 GitHub 更新文件（HTTP " + status + "）。");
                    }
                    return response;
                }
            }
            throw new InvalidOperationException("更新重定向次数过多。");
        }
        private static async Task<string> ReadTextAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
        {
            if (response.Content == null || response.Content.Headers.ContentLength.GetValueOrDefault() > maximum) throw new InvalidOperationException("更新信息大小超出限制。");
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output.Length + count > maximum) throw new InvalidOperationException("更新信息大小超出限制。");
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
        private static Uri ValidateDownloadUri(string url, SemanticVersion expected)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) throw new ArgumentException("更新下载地址无效。");
            var parts = Uri.UnescapeDataString(uri.AbsolutePath).Split('/');
            SemanticVersion version;
            if (parts.Length != 7 || !SemanticVersion.TryParse(parts[5], out version) || version.ToString() != expected.ToString()) throw new ArgumentException("下载地址的版本与更新信息不一致。");
            return ValidateAssetUri(url, parts[5], "CodexAutoContinue.exe");
        }
        private static Uri ValidateAssetUri(string url, string tag, string filename)
        {
            Uri uri;
            var expectedPath = "/tvcmall-dev/codex-auto-continue/releases/download/" + tag + "/" + filename;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !IsHttps(uri) || uri.Host != "github.com" || uri.Query.Length != 0 || Uri.UnescapeDataString(uri.AbsolutePath) != expectedPath)
                throw new InvalidOperationException("发布包下载地址不属于本项目的固定发布源。");
            return uri;
        }
        private static bool IsHttps(Uri uri) { return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0; }
        private static void ValidateSize(long size) { if (size <= 0 || size > MaximumAssetBytes) throw new InvalidOperationException("发布包大小不符合更新要求。"); }
        public void Dispose() { client.Dispose(); }
        public sealed class Release
        {
            public string tag_name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public string body { get; set; }
            public List<Asset> assets { get; set; }
        }
        public sealed class Asset
        {
            public string name { get; set; }
            public string state { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
    }
}
