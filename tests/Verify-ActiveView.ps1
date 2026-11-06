[CmdletBinding()]
param(
    [string]$ReleaseExe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use 64-bit Windows PowerShell: powershell.exe -STA -NoProfile -File tests\Verify-ActiveView.ps1'
}

if (-not $ReleaseExe) { $ReleaseExe = Join-Path $PSScriptRoot '..\src\CodexAutoContinue\bin\Release\net48\CodexAutoContinue.exe' }
$release = (Resolve-Path -LiteralPath $ReleaseExe).Path
$reference = Join-Path ([IO.Path]::GetTempPath()) ('CodexAutoContinue-active-view-' + [Guid]::NewGuid().ToString('N') + '.dll')
Copy-Item -LiteralPath $release -Destination $reference
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    $reference
    (Join-Path $framework 'WPF\PresentationFramework.dll')
    (Join-Path $framework 'WPF\PresentationCore.dll')
    (Join-Path $framework 'WPF\WindowsBase.dll')
    (Join-Path $framework 'System.Xaml.dll')
    (Join-Path $framework 'System.Windows.Forms.dll')
    (Join-Path $framework 'System.Drawing.dll')
    (Join-Path $framework 'System.Core.dll')
)
$source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAutoContinue.Core;
using CodexAutoContinue.Desktop;
using CodexAutoContinue.Infrastructure;

public static class ActiveViewVerification {
    static int failed;
    public static void Run() {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexAutoContinue;component/Desktop/Theme.xaml", UriKind.Relative) });
        try {
            VerifyFiltersAndRefresh();
            VerifyEmptyFilter();
            Measure(0); Measure(100);
        } finally { app.Shutdown(); }
        if (failed != 0) throw new Exception(failed + " active-view checks failed.");
        Console.WriteLine("PASS active view: default filter, transitions, snapshot reuse, refresh coalescing and closed-window guard.");
    }
    static void Check(bool condition, string message) {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) failed++;
    }
    static void Invoke(MainWindow window, string method, params object[] args) {
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);
    }
    static void DrainDispatcher() {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static string Text(MainWindow window, string name) { return ((TextBlock)window.FindName(name)).Text; }
    static string[] VisibleIds(MainWindow window) {
        return ((ListBox)window.FindName("SessionList")).Items.Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("ThreadId").GetValue(item, null)).OrderBy(id => id).ToArray();
    }
    static void VerifyFiltersAndRefresh() {
        var service = new Fake(new[] { SessionState.Running, SessionState.InternalRetry, SessionState.Completed, SessionState.Unknown, SessionState.NetworkFailure }, 0);
        var window = new MainWindow(service, null);
        try {
            Check(Text(window, "PageTitle") == "执行中" && VisibleIds(window).SequenceEqual(new[] { "fixture-0", "fixture-1" }), "startup shows running and internal-retry sessions only");
            Check(((Button)window.FindName("ActiveFilter")).Background.ToString() == "#FF466249", "startup selects the active filter style");
            Invoke(window, "AllClick", null, null);
            Check(Text(window, "PageTitle") == "会话总览" && VisibleIds(window).Length == 5, "all filter restores completed and unknown sessions");
            Invoke(window, "AttentionClick", null, null);
            Check(Text(window, "PageTitle") == "需关注" && VisibleIds(window).SequenceEqual(new[] { "fixture-4" }), "attention filter shows the failing session");
            Invoke(window, "ActiveClick", null, null);
            Check(Text(window, "PageTitle") == "执行中" && VisibleIds(window).SequenceEqual(new[] { "fixture-0", "fixture-1" }), "returning to active matches the startup view");

            ((ListBox)window.FindName("SessionList")).SelectedIndex = 0;
            service.Copies = 0;
            Invoke(window, "UpdateDetails");
            Check(service.Copies == 0, "details reuse the cached snapshot (copies=" + service.Copies + ")");
            service.Copies = 0;
            Invoke(window, "UpdateView");
            Check(service.Copies == 1, "one view update takes one snapshot (copies=" + service.Copies + ")");
            DrainDispatcher(); service.Copies = 0;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            DrainDispatcher();
            Check(service.Copies == 1, "refresh completion and Changed render once (copies=" + service.Copies + ")");

            service.Copies = 0;
            Task.Run(() => { service.Emit(); service.Emit(); service.Emit(); }).GetAwaiter().GetResult();
            window.RefreshNowAsync().GetAwaiter().GetResult();
            DrainDispatcher();
            Check(service.Copies == 1, "background Changed bursts coalesce safely (copies=" + service.Copies + ")");

            service.Copies = 0; service.Emit(); window.ExitApplication(); DrainDispatcher();
            Check(service.Copies == 0, "queued refresh does not touch a closed window (copies=" + service.Copies + ")");
        } finally { if (window.IsLoaded || !window.Dispatcher.HasShutdownStarted) window.ExitApplication(); }
    }
    static void VerifyEmptyFilter() {
        var window = new MainWindow(new Fake(new[] { SessionState.Completed, SessionState.Unknown }, 0), null);
        try {
            Check(VisibleIds(window).Length == 0 && Text(window, "EmptyText") == "当前没有执行中的会话", "active view has a specific empty-state message");
            ((TextBox)window.FindName("SearchBox")).Text = "no-fixture-match";
            Check(Text(window, "EmptyText") == "没有符合条件的会话", "search empty state remains distinct");
        } finally { window.ExitApplication(); }
    }
    static void Measure(int eventCount) {
        var service = new Fake(Enumerable.Repeat(SessionState.Running, 120).ToArray(), eventCount);
        var window = new MainWindow(service, null);
        try {
            Invoke(window, "UpdateView"); Invoke(window, "UpdateDetails");
            Report(service, window, eventCount, "UpdateView", 5);
            Report(service, window, eventCount, "UpdateDetails", 10);
        } finally { window.ExitApplication(); }
    }
    static void Report(Fake service, MainWindow window, int events, string method, int repeats) {
        service.Copies = 0; var watch = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++) Invoke(window, method);
        watch.Stop();
        Console.WriteLine("MEASURE sessions=120 events=" + events + " " + method + " snapshots_per_call=" + ((double)service.Copies / repeats).ToString("F1") + " avg_ms=" + (watch.Elapsed.TotalMilliseconds / repeats).ToString("F2"));
    }
    sealed class Fake : IApplicationService {
        readonly List<SessionRecord> records = new List<SessionRecord>();
        public int Copies;
        public Fake(SessionState[] states, int events) {
            for (var i = 0; i < states.Length; i++) {
                var record = new SessionRecord { ThreadId = "fixture-" + i, Title = "Fixture session " + i, Workspace = "C:\\fixture", State = states[i], LastActivityUtc = DateTime.UtcNow.AddSeconds(-i) };
                for (var j = 0; j < events; j++) record.Events.Add(new MonitorEvent { ThreadId = record.ThreadId, Kind = "fixture", Message = "Fixture event message for isolated rendering performance verification." });
                records.Add(record);
            }
        }
        public IReadOnlyList<SessionRecord> Sessions { get { Copies++; return records.OrderByDescending(x => x.LastActivityUtc).Select(Json.Clone).ToList(); } }
        public AppSettings Settings { get { return new AppSettings { CloseToTray = false }; } }
        public IReadOnlyList<MonitorEvent> Events { get { return new List<MonitorEvent>(); } }
        public event Action Changed;
        public void Emit() { var handler = Changed; if (handler != null) handler(); }
        public string DataDirectory { get { return "fixture"; } }
        public string ConnectionStatus { get { return "fixture"; } }
        public string NotificationStatus { get { return "fixture"; } }
        public bool IsConnected { get { return true; } }
        public Task RefreshAsync() { Emit(); return Task.CompletedTask; }
        public Task ContinueAsync(string id) { Emit(); return Task.CompletedTask; }
        public void SetSessionMonitoring(string id, bool enabled) { Emit(); }
        public void SetGlobalMonitoring(bool enabled) { Emit(); }
        public Task SaveSettingsAsync(AppSettings settings) { Emit(); return Task.CompletedTask; }
        public Task<string> TestWebhookAsync(AppSettings settings) { return Task.FromResult("fixture"); }
        public void Dispose() { }
    }
}
'@

try {
    [void][Reflection.Assembly]::LoadFrom($release)
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references
    [ActiveViewVerification]::Run()
}
finally {
    if (Test-Path -LiteralPath $reference) { Remove-Item -LiteralPath $reference }
}
