using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Integration;

namespace CodexAutoContinue.Updating
{
    public static class UpdateWorker
    {
        public static bool TryRun(string[] args, out int result)
        {
            result = 0;
            if (args.Length == 0 || args[0] != "--apply-update") return false;
            if (args.Length != 2) { result = 1; return true; }
            try { result = Run(Path.GetFullPath(args[1])); }
            catch { result = 1; }
            return true;
        }
        private static int Run(string manifestPath)
        {
            var stage = Path.GetDirectoryName(manifestPath);
            if (!SamePath(manifestPath, Path.Combine(stage, "update.json")) || !SamePath(Assembly.GetExecutingAssembly().Location, Path.Combine(stage, "updater.exe")))
                throw new InvalidDataException("更新进程位置无效。");
            var manifest = ReadManifest(manifestPath);
            ValidateStage(manifest, stage);
            var failurePath = Path.Combine(Path.GetDirectoryName(stage), "failure.json");
            bool ownsMutex = false; bool replaced = false; bool parentExited = false;
            using (var mutex = new Mutex(false, MutexName))
            {
                try
                {
                    WaitForParent(manifest);
                    parentExited = true;
                    try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) throw new IOException("应用仍在运行，取消升级。");
                    var success = UpdateInstaller.Apply(manifest, stage, () =>
                    {
                        replaced = true;
                        mutex.ReleaseMutex(); ownsMutex = false;
                        var healthy = LaunchAndVerify(manifest, stage);
                        if (!healthy)
                        {
                            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                            catch (AbandonedMutexException) { ownsMutex = true; }
                            if (!ownsMutex) throw new IOException("应用占用中，保留备份等待人工恢复。");
                        }
                        return healthy;
                    });
                    if (success)
                    {
                        try { if (File.Exists(failurePath)) File.Delete(failurePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        return 0;
                    }
                    WriteFailure(failurePath, manifest.ExpectedVersion, "新版本未通过启动检查，已回退到上一版本。该版本 24 小时内不再自动安装。");
                    if (ownsMutex) { mutex.ReleaseMutex(); ownsMutex = false; }
                    RestartPrevious(manifest);
                    return 1;
                }
                catch
                {
                    WriteFailure(failurePath, manifest.ExpectedVersion, "自动升级未完成，已保留原程序或上一版本备份。请检查目录权限或手动安装。");
                    if (ownsMutex) { mutex.ReleaseMutex(); ownsMutex = false; }
                    // A locked target can prevent rollback; never start an unverified replacement.
                    if (!replaced && parentExited) RestartPrevious(manifest);
                    return 1;
                }
                finally { if (ownsMutex) mutex.ReleaseMutex(); }
            }
        }
        public static string MutexName { get { return "Local\\CodexAutoContinue-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName); } }
        public static void WriteStartupReceipt(string receiptPath)
        {
            var stage = Path.GetDirectoryName(Path.GetFullPath(receiptPath));
            if (!SamePath(receiptPath, Path.Combine(stage, "health.json"))) throw new InvalidDataException("启动回执位置无效。");
            var manifest = ReadManifest(Path.Combine(stage, "update.json"));
            ValidateStage(manifest, stage);
            if (!SamePath(manifest.TargetPath, Assembly.GetExecutingAssembly().Location) || manifest.ExpectedVersion.TrimStart('v') != UpdateVersion.Current)
                throw new InvalidDataException("启动回执与当前程序不匹配。");
            UpdateFiles.ValidatePayload(manifest.TargetPath, manifest.ExpectedVersion, manifest.Sha256);
            File.WriteAllText(receiptPath, Json.Serialize(new UpdateReceipt { Nonce = manifest.Nonce, ProcessId = Process.GetCurrentProcess().Id, Version = UpdateVersion.Current }), new UTF8Encoding(false));
        }
        private static bool LaunchAndVerify(UpdateManifest manifest, string stage)
        {
            var receiptPath = Path.Combine(stage, "health.json");
            using (var process = Process.Start(StartInfo(manifest, " --update-health " + CliSender.Quote(receiptPath))))
            {
                if (process == null) return false;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(receiptPath))
                    {
                        try
                        {
                            var receipt = Json.Deserialize<UpdateReceipt>(File.ReadAllText(receiptPath));
                            if (receipt != null && receipt.Nonce == manifest.Nonce && receipt.ProcessId == process.Id && receipt.Version == manifest.ExpectedVersion.TrimStart('v')) return true;
                        }
                        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException) { }
                    }
                    if (process.HasExited) return false;
                    Thread.Sleep(200);
                }
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2000)) { process.Kill(); process.WaitForExit(3000); }
                }
                return false;
            }
        }
        private static ProcessStartInfo StartInfo(UpdateManifest manifest, string extra = "")
        {
            return new ProcessStartInfo(manifest.TargetPath, "--data-dir " + CliSender.Quote(manifest.DataDirectory) + (manifest.StartInTray ? " --tray" : "") + extra)
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(manifest.TargetPath), WindowStyle = manifest.StartInTray ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        }
        private static void RestartPrevious(UpdateManifest manifest)
        {
            try { Process.Start(StartInfo(manifest)); } catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
        }
        private static void WaitForParent(UpdateManifest manifest)
        {
            try
            {
                using (var parent = Process.GetProcessById(manifest.ParentProcessId))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != manifest.ParentStartTicks || !SamePath(parent.MainModule.FileName, manifest.TargetPath))
                        throw new InvalidDataException("升级目标进程已变化。");
                    if (!parent.WaitForExit(30000)) throw new IOException("等待应用退出超时。");
                }
            }
            catch (ArgumentException) { }
        }
        public static UpdateManifest ReadManifest(string path)
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("升级清单过大。");
            var result = Json.Deserialize<UpdateManifest>(File.ReadAllText(path));
            if (result == null || result.ParentProcessId <= 0 || string.IsNullOrWhiteSpace(result.Nonce) || result.Nonce.Length != 32) throw new InvalidDataException("升级清单无效。");
            return result;
        }
        private static void ValidateStage(UpdateManifest manifest, string stage)
        {
            var expectedRoot = Path.Combine(Path.GetFullPath(manifest.DataDirectory), "updates");
            Guid id;
            if (!SamePath(Path.GetDirectoryName(stage), expectedRoot) || !Guid.TryParseExact(Path.GetFileName(stage), "N", out id)) throw new InvalidDataException("升级暂存目录无效。");
            if (!string.Equals(Path.GetFileName(manifest.TargetPath), "CodexAutoContinue.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("升级目标文件名无效。");
        }
        private static bool SamePath(string first, string second) { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        private static void WriteFailure(string path, string version, string message)
        {
            try { File.WriteAllText(path, Json.Serialize(new UpdateFailure { Version = version, TimeUtc = DateTime.UtcNow, Message = message }), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
