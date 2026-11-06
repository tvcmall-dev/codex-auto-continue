using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Integration;

namespace CodexAutoContinue.Updating
{
    public sealed class UpdateService : IUpdateService
    {
        private readonly IApplicationService application;
        private readonly IUpdateHost host;
        private readonly IReleaseClient client;
        private readonly string installationPath;
        private readonly Func<UpdateManifest, string, bool> launchWorker;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private DateTime nextCheckUtc = DateTime.MinValue, installRetryUtc = DateTime.MinValue;
        private ReleaseInfo ready;
        private string stage;
        private bool disposed;
        public event Action Changed;
        public string CurrentVersion { get; private set; }
        public string AvailableVersion { get; private set; } = "";
        public string StatusText { get; private set; } = "自动检查更新已就绪。";
        public int Progress { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsReady { get { return ready != null; } }
        public Func<bool> IsUiIdle { get; set; } = () => false;
        public Func<bool> IsWindowVisible { get; set; } = () => true;
        public Action RestartRequested { get; set; }
        public UpdateService(IApplicationService application, IUpdateHost host, IReleaseClient client = null, string currentVersion = null, string installationPath = null, Func<UpdateManifest, string, bool> launchWorker = null)
        {
            this.application = application; this.host = host; this.client = client ?? new GithubReleaseClient();
            CurrentVersion = currentVersion ?? UpdateVersion.Current;
            this.installationPath = installationPath ?? Assembly.GetExecutingAssembly().Location;
            this.launchWorker = launchWorker ?? LaunchWorker;
            var previous = ReadFailure(); if (previous != null) StatusText = previous.Message;
        }
        public async Task TickAsync()
        {
            if (disposed) return;
            if (application.Settings.AutoCheckUpdates && DateTime.UtcNow >= nextCheckUtc) await CheckAsync(false);
            if (IsReady && application.Settings.AutoInstallUpdates && DateTime.UtcNow >= installRetryUtc) await InstallCoreAsync(false);
        }
        public async Task CheckAsync(bool manual = true)
        {
            if (disposed || !manual && !application.Settings.AutoCheckUpdates || !await gate.WaitAsync(0)) return;
            try
            {
                nextCheckUtc = DateTime.UtcNow.AddHours(6); IsBusy = true; Progress = 0; SetStatus("正在检查 GitHub 新版本…");
                var release = await client.FindUpdateAsync(CurrentVersion, application.Settings.IncludePrereleaseUpdates, cancellation.Token);
                if (release == null) { SetStatus("当前已是所选通道的最新版本。"); return; }
                AvailableVersion = release.Version;
                var failure = ReadFailure();
                if (!manual && failure != null && failure.Version == release.Version && failure.TimeUtc > DateTime.UtcNow.AddHours(-24))
                { SetStatus(failure.Message); return; }
                if (ready != null && ready.Version == release.Version) { SetStatus("新版本已下载，等待程序空闲后安装。"); return; }
                var updates = Path.Combine(application.DataDirectory, "updates");
                var newStage = Path.Combine(updates, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(newStage);
                SetStatus("正在下载 " + release.Version + "…");
                var payload = Path.Combine(newStage, "payload.exe");
                await client.DownloadAsync(release, payload, new Progress<int>(value => { Progress = Math.Max(0, Math.Min(100, value)); Changed?.Invoke(); }), cancellation.Token);
                UpdateFiles.ValidatePayload(payload, release.Version, release.Sha256);
                SemanticVersion selected;
                if (!application.Settings.IncludePrereleaseUpdates && SemanticVersion.TryParse(release.Version, out selected) && selected.IsPrerelease)
                { SetStatus("版本通道已切换，已取消预览版安装。"); return; }
                stage = newStage; ready = release; Progress = 100; installRetryUtc = DateTime.MinValue;
                SetStatus("新版本 " + release.Version + " 已校验，等待程序空闲后安装。");
            }
            catch (OperationCanceledException) { if (!disposed) SetStatus("检查或下载更新超时，下次检查时重试。"); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException || ex is System.Net.Http.HttpRequestException)
            { SetStatus("检查或下载更新失败，监控继续运行。可稍后手动重试。"); }
            finally
            {
                if (ready != null) { AvailableVersion = ready.Version; Progress = 100; }
                IsBusy = false; gate.Release(); Changed?.Invoke();
            }
        }
        public Task InstallAsync() { return InstallCoreAsync(true); }
        private async Task InstallCoreAsync(bool manual)
        {
            if (disposed || !IsReady || !await gate.WaitAsync(0)) return;
            var paused = false;
            try
            {
                if (!manual && !application.Settings.AutoInstallUpdates) return;
                if (RestartRequested == null) { SetStatus("新版本已就绪，等待重启入口初始化。"); return; }
                if (IsUiIdle == null || !IsUiIdle()) { SetStatus("新版本已就绪，等待关闭设置或当前操作结束。"); return; }
                SemanticVersion selected;
                if (!application.Settings.IncludePrereleaseUpdates && SemanticVersion.TryParse(ready.Version, out selected) && selected.IsPrerelease)
                { ready = null; SetStatus("已切换为正式版通道，预览版安装已取消。"); return; }
                UpdateFiles.ValidatePayload(Path.Combine(stage, "payload.exe"), ready.Version, ready.Sha256);
                if (!string.Equals(Path.GetFileName(installationPath), "CodexAutoContinue.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("非标准文件名。");
                var probe = installationPath + ".probe-" + Guid.NewGuid().ToString("N");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                paused = await host.TryPauseForUpdateAsync();
                if (!paused) { SetStatus("新版本已就绪，等待续接、通知或重试计划结束。"); return; }
                IsBusy = true; SetStatus("正在准备自动重启升级…");
                using (var current = Process.GetCurrentProcess())
                {
                    var manifest = new UpdateManifest { TargetPath = Path.GetFullPath(installationPath), ExpectedVersion = ready.Version, Sha256 = ready.Sha256, ParentProcessId = current.Id, ParentStartTicks = current.StartTime.ToUniversalTime().Ticks, DataDirectory = application.DataDirectory, StartInTray = IsWindowVisible == null || !IsWindowVisible(), Nonce = Guid.NewGuid().ToString("N") };
                    File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(stage, "updater.exe"), true);
                    var path = Path.Combine(stage, "update.json"); File.WriteAllText(path, Json.Serialize(manifest), new UTF8Encoding(false));
                    if (!launchWorker(manifest, path)) throw new IOException("无法启动更新进程。");
                }
                RestartRequested(); paused = false;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception)
            { installRetryUtc = DateTime.UtcNow.AddHours(6); SetStatus("自动安装失败，监控已恢复；请检查程序目录权限，或手动下载安装。"); }
            finally { if (paused) host.ResumeAfterUpdateFailure(); IsBusy = false; gate.Release(); Changed?.Invoke(); }
        }
        private static bool LaunchWorker(UpdateManifest manifest, string path)
        {
            var process = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(path), "updater.exe"), "--apply-update " + CliSender.Quote(path)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) return false; process.Dispose(); return true;
        }
        private UpdateFailure ReadFailure()
        {
            try
            {
                var path = Path.Combine(application.DataDirectory, "updates", "failure.json");
                return File.Exists(path) && new FileInfo(path).Length < 16384 ? Json.Deserialize<UpdateFailure>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is UnauthorizedAccessException) { return null; }
        }
        private void SetStatus(string value) { StatusText = value; Changed?.Invoke(); }
        public void Dispose() { disposed = true; cancellation.Cancel(); client.Dispose(); }
    }
}
