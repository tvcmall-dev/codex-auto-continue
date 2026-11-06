using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Updating;

namespace CodexAutoContinue.Tests
{
    public static class UpdateServiceTests
    {
        public static void Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-update-service-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var target = Path.Combine(directory, "CodexAutoContinue.exe"); File.Copy(typeof(UpdateVersion).Assembly.Location, target);
                using (var app = new ApplicationService(directory))
                {
                    var fake = new Client(); var launches = 0;
                    using (var updater = new UpdateService(app, app, fake, "0.1.0", target, (m, p) => { launches++; return true; }))
                    {
                        updater.IsUiIdle = () => false;
                        updater.CheckAsync().GetAwaiter().GetResult();
                        Check(updater.IsReady && fake.Downloads == 1, "new release automatically downloads and verifies");
                        updater.InstallAsync().GetAwaiter().GetResult();
                        Check(launches == 0, "editing UI defers upgrade");
                        updater.IsUiIdle = () => true;
                        var restarted = false; updater.RestartRequested = () => restarted = true;
                        updater.InstallAsync().GetAwaiter().GetResult();
                        Check(launches == 1 && restarted, "idle installation launches separate worker and requests shutdown");
                        app.ResumeAfterUpdateFailure();
                    }
                    var settings = app.Settings; settings.AutoCheckUpdates = false; app.SaveSettingsAsync(settings).GetAwaiter().GetResult();
                    var disabled = new Client();
                    using (var updater = new UpdateService(app, app, disabled, "0.1.0", target, (m, p) => false))
                    {
                        updater.RestartRequested = () => { };
                        updater.CheckAsync(false).GetAwaiter().GetResult(); Check(disabled.Checks == 0, "disabled background checks make no request");
                        updater.CheckAsync(true).GetAwaiter().GetResult(); Check(disabled.Checks == 1, "manual check remains available");
                        updater.IsUiIdle = () => true;
                        updater.InstallAsync().GetAwaiter().GetResult();
                        Check(updater.StatusText.Contains("失败") || updater.StatusText.Contains("未能"), "worker launch failure is reported");
                        app.SetGlobalMonitoring(false);
                    }
                    settings = app.Settings; settings.AutoCheckUpdates = true; settings.AutoInstallUpdates = false; app.SaveSettingsAsync(settings).GetAwaiter().GetResult();
                    var downloadedOnly = new Client();
                    using (var updater = new UpdateService(app, app, downloadedOnly, "0.1.0", target, (m, p) => { throw new Exception("unexpected restart"); }))
                    {
                        updater.IsUiIdle = () => true;
                        updater.TickAsync().GetAwaiter().GetResult();
                        Check(updater.IsReady, "download-only policy never silently restarts");
                        updater.TickAsync().GetAwaiter().GetResult();
                        Check(downloadedOnly.Checks == 1 && downloadedOnly.Downloads == 1, "background checks respect the six-hour interval");
                    }
                    var refreshFailure = new Client();
                    var preservedLaunches = 0;
                    using (var updater = new UpdateService(app, app, refreshFailure, "0.1.0", target, (m, p) => { preservedLaunches++; return true; }))
                    {
                        updater.IsUiIdle = () => true;
                        updater.RestartRequested = () => { };
                        updater.CheckAsync().GetAwaiter().GetResult();
                        refreshFailure.FailNewerDownload = true;
                        updater.CheckAsync().GetAwaiter().GetResult();
                        Check(updater.IsReady, "failed newer download preserves previously verified release");
                        Check(updater.AvailableVersion == UpdateVersion.Current && updater.Progress == 100, "ready display still identifies the verified release after a failed refresh");
                        updater.InstallAsync().GetAwaiter().GetResult();
                        Check(preservedLaunches == 1, "failed refresh preserves ready release and staged payload together");
                        app.ResumeAfterUpdateFailure();
                    }
                    using (var updater = new UpdateService(app, app, new Client { CorruptPayload = true }, "0.1.0", target))
                    {
                        updater.CheckAsync().GetAwaiter().GetResult();
                        Check(!updater.IsReady && updater.StatusText.Contains("失败"), "invalid payload is reported without escaping into the UI");
                    }
                    var changedPayload = new Client();
                    using (var updater = new UpdateService(app, app, changedPayload, "0.1.0", target, (m, p) => { throw new Exception("unverified worker launch"); }))
                    {
                        updater.IsUiIdle = () => true; updater.RestartRequested = () => { };
                        updater.CheckAsync().GetAwaiter().GetResult();
                        File.WriteAllText(changedPayload.LastDestination, "modified after verification");
                        updater.InstallAsync().GetAwaiter().GetResult();
                        Check(updater.StatusText.Contains("失败"), "installation rechecks staged payload and reports tampering");
                        app.SetGlobalMonitoring(false);
                    }
                    var failurePath = Path.Combine(directory, "updates", "failure.json");
                    File.WriteAllText(failurePath, Json.Serialize(new UpdateFailure { Version = UpdateVersion.Current, TimeUtc = DateTime.UtcNow, Message = "rollback cooldown" }));
                    var quarantined = new Client();
                    using (var updater = new UpdateService(app, app, quarantined, "0.1.0", target))
                    {
                        updater.CheckAsync(false).GetAwaiter().GetResult();
                        Check(quarantined.Downloads == 0 && !updater.IsReady, "failed version is not automatically reinstalled during cooldown");
                        updater.CheckAsync(true).GetAwaiter().GetResult();
                        Check(quarantined.Downloads == 1 && updater.IsReady, "explicit manual check can retry a quarantined version");
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private sealed class Client : IReleaseClient
        {
            public int Downloads, Checks;
            public bool FailNewerDownload;
            public bool CorruptPayload;
            public string LastDestination;
            public Task<ReleaseInfo> FindUpdateAsync(string currentVersion, bool includePrerelease, CancellationToken token)
            {
                Checks++; return Task.FromResult(new ReleaseInfo { Version = FailNewerDownload ? "9.0.0" : UpdateVersion.Current, Sha256 = UpdateFiles.Sha256(typeof(UpdateVersion).Assembly.Location), Size = new FileInfo(typeof(UpdateVersion).Assembly.Location).Length });
            }
            public Task DownloadAsync(ReleaseInfo release, string destination, IProgress<int> progress, CancellationToken token)
            { Downloads++; LastDestination = destination; if (FailNewerDownload) throw new IOException("interrupted download"); File.Copy(typeof(UpdateVersion).Assembly.Location, destination); if (CorruptPayload) File.WriteAllText(destination, "corrupt"); progress?.Report(100); return Task.CompletedTask; }
            public void Dispose() { }
        }
    }
}
