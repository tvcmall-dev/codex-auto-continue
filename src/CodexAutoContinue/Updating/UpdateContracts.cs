using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAutoContinue.Updating
{
    public sealed class ReleaseInfo
    {
        public string Version { get; set; }
        public string Notes { get; set; }
        public string PageUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public interface IReleaseClient : IDisposable
    {
        Task<ReleaseInfo> FindUpdateAsync(string currentVersion, bool includePrerelease, CancellationToken cancellationToken);
        Task DownloadAsync(ReleaseInfo release, string destination, IProgress<int> progress, CancellationToken cancellationToken);
    }
    public interface IUpdateHost
    {
        Task<bool> TryPauseForUpdateAsync();
        void ResumeAfterUpdateFailure();
    }
    public interface IUpdateService : IDisposable
    {
        event Action Changed;
        string CurrentVersion { get; }
        string AvailableVersion { get; }
        string StatusText { get; }
        int Progress { get; }
        bool IsBusy { get; }
        bool IsReady { get; }
        Func<bool> IsUiIdle { get; set; }
        Func<bool> IsWindowVisible { get; set; }
        Action RestartRequested { get; set; }
        Task CheckAsync(bool manual = true);
        Task TickAsync();
        Task InstallAsync();
    }
    public sealed class UpdateManifest
    {
        public string TargetPath { get; set; }
        public string ExpectedVersion { get; set; }
        public string Sha256 { get; set; }
        public int ParentProcessId { get; set; }
        public long ParentStartTicks { get; set; }
        public string DataDirectory { get; set; }
        public bool StartInTray { get; set; }
        public string Nonce { get; set; }
    }
    public sealed class UpdateReceipt
    {
        public string Nonce { get; set; }
        public int ProcessId { get; set; }
        public string Version { get; set; }
    }
    public sealed class UpdateFailure
    {
        public string Version { get; set; }
        public DateTime TimeUtc { get; set; }
        public string Message { get; set; }
    }
}
