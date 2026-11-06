using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Integration
{
    public sealed class SendResult
    {
        public bool Accepted { get; set; }
        public string Detail { get; set; }
    }
    public interface ICliSender { Task<SendResult> SendAsync(AppSettings settings, string threadId, string message); }
    public sealed class CliSender : ICliSender
    {
        public static string Discover(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
            {
                var path = Directory.GetDirectories(root).Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (path != null) return path;
            }
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { var path = Path.Combine(directory.Trim('"'), "codex.exe"); if (File.Exists(path)) return path; }
                catch (ArgumentException) { }
            }
            return null;
        }
        public static string BuildArguments(string threadId, string message)
        {
            Guid id;
            if (!Guid.TryParseExact(threadId, "D", out id)) throw new ArgumentException("会话 ID 格式无效。");
            if (string.IsNullOrWhiteSpace(message) || message.Length > 1000) throw new ArgumentException("续接消息长度无效。");
            return "queue --thread " + Quote(threadId) + " --message " + Quote(message) + " --resume";
        }
        public static string Quote(string value)
        {
            var result = new StringBuilder("\""); var slashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append(ch); }
                else { result.Append('\\', slashes); result.Append(ch); }
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public async Task<SendResult> SendAsync(AppSettings settings, string threadId, string message)
        {
            var cli = Discover(settings.CliPath);
            if (cli == null) throw new InvalidOperationException("未找到 Codex CLI，请在设置中指定 codex.exe。");
            var start = new ProcessStartInfo(cli, BuildArguments(threadId, message)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            start.EnvironmentVariables["CODEX_HOME"] = settings.CodexHome;
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                var stdout = ReadBoundedAsync(process.StandardOutput);
                var stderr = ReadBoundedAsync(process.StandardError);
                var exited = await Task.Run(() => process.WaitForExit(20000)).ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return new SendResult { Detail = "CLI 响应超时，投递结果不确定；保留待确认状态，避免重复发送。" };
                }
                var output = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
                if (process.ExitCode != 0) return new SendResult { Detail = "CLI 未确认投递（退出码 " + process.ExitCode + "）；需人工核对，程序不会重复发送。" };
                if (output.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0 || output.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new SendResult { Detail = "消息可能已入队，但桌面恢复请求未确认；请在 Codex 核对，程序继续等待执行证据。" };
                return new SendResult { Accepted = true, Detail = "CLI 已接受续接请求；正在等待同一会话产生新的执行证据。" };
            }
        }
        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var buffer = new char[2048]; var result = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                if (result.Length < 16384) result.Append(buffer, 0, Math.Min(count, 16384 - result.Length));
            return result.ToString();
        }
    }
}
