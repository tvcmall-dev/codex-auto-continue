[CmdletBinding()]
param(
    [string]$ReleaseExe = (Join-Path $PSScriptRoot '..\src\CodexAutoContinue\bin\Release\net48\CodexAutoContinue.exe'),
    [switch]$KeepArtifacts
)

# Build Release first, then run in 64-bit Windows PowerShell with the app closed.
# All app data and executables are private fixtures; cleanup checks PID, start time and path.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Run with 64-bit Windows PowerShell 5.1: powershell.exe -NoProfile -File tests\Verify-UpdateIntegration.ps1'
}

$release = (Resolve-Path -LiteralPath $ReleaseExe).Path
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($release).ProductVersion.Split('+')[0]
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') { throw 'The release must have a semantic product version.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 C# compiler was not found.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('CodexAutoContinue-update-integration-' + [Guid]::NewGuid().ToString('N'))
$owned = @{}
$runStarted = [DateTime]::UtcNow
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Register-OwnedProcess([Diagnostics.Process]$Process, [string]$Path) {
    $Process.Refresh()
    if (-not $Process.HasExited) {
        Assert-True ($Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) 'Refusing to register a process outside this fixture.'
        $owned[$Process.Id] = @{ Ticks = $Process.StartTime.ToUniversalTime().Ticks; Path = $Path }
    }
}
function Start-OwnedProcess([string]$Path, [string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($Path)) -WindowStyle Hidden -PassThru
    Register-OwnedProcess $process $Path
    return $process
}
function Find-OwnedChildren {
    # Match both parent identity and this run's private executable directory.
    foreach ($candidate in @(Get-CimInstance Win32_Process -Filter "Name='CodexAutoContinue.exe' OR Name='updater.exe' OR Name='FixtureTools.exe'")) {
        if ($owned.ContainsKey([int]$candidate.ParentProcessId) -and $candidate.ExecutablePath -and
            $candidate.ExecutablePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            try {
                $process = Get-Process -Id $candidate.ProcessId -ErrorAction Stop
                if ($process.StartTime.ToUniversalTime() -ge $runStarted) { Register-OwnedProcess $process $candidate.ExecutablePath }
                $process.Dispose()
            }
            catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
        }
    }
}
function Stop-OwnedProcesses {
    Find-OwnedChildren
    foreach ($processId in @($owned.Keys)) {
        $process = $null
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            $identity = $owned[$processId]
            if ($process.StartTime.ToUniversalTime().Ticks -eq $identity.Ticks -and
                [string]::Equals($process.MainModule.FileName, $identity.Path, [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
        finally { if ($null -ne $process) { $process.Dispose() } }
    }
}
function Wait-For([scriptblock]$Condition, [int]$Seconds, [string]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Find-OwnedChildren
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Compile-Fixture([string]$Source, [string]$Destination, [string[]]$References = @()) {
    $sourcePath = $Destination + '.cs'
    [IO.File]::WriteAllText($sourcePath, $Source, $utf8)
    $arguments = @('/nologo', '/target:winexe', '/platform:x64', ('/out:' + $Destination), '/r:System.Web.Extensions.dll')
    $arguments += $References | ForEach-Object { '/r:' + $_ }
    & $compiler @arguments $sourcePath
    if ($LASTEXITCODE -ne 0) { throw "Fixture compilation failed: $Destination" }
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-DataPreserved([string]$Directory, [string]$SettingsHash, [string]$DatabaseHash) {
    Assert-True ((Get-Hash (Join-Path $Directory 'settings.json')) -eq $SettingsHash) 'The settings file changed during replacement.'
    Assert-True ((Get-Hash (Join-Path $Directory 'monitor.db')) -eq $DatabaseHash) 'The SQLite fixture changed during replacement.'
}

$fixtureSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]
static class Fixture {
    static string Value(string[] args, string key) { var i = Array.IndexOf(args, key); return i >= 0 ? args[i + 1] : null; }
    static void Write(string path) {
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(new { ProcessId = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks }));
    }
    static int Main(string[] args) {
        var directory = Value(args, "--data-dir");
        var parent = Array.IndexOf(args, "--fixture-parent") >= 0;
        var name = "Local\\CodexAutoContinue-" + WindowsIdentity.GetCurrent().User.Value;
        using (var mutex = new Mutex(false, name)) {
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 31;
            try {
                Write(Path.Combine(directory, parent ? "parent-ready.json" : "restarted.json"));
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(directory, parent ? "fixture-exit.signal" : "fixture-stop.signal"))) Thread.Sleep(50);
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
'@
$failureSource = @'
using System.Reflection;
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyFileVersion("0.0.0.0")]
[assembly: AssemblyInformationalVersion("__VERSION__")]
static class FailedStartup { static int Main() { return 42; } }
'@
$toolsSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Updating;
static class Tools {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main(string[] args) {
        try {
            var directory = args[1];
            if (args[0] == "seed") {
                var settings = new AppSettings {
                    CodexHome = Path.Combine(directory, "empty-codex"), GlobalMonitoring = false,
                    AutoCheckUpdates = false, AutoInstallUpdates = false, StartWithWindows = false,
                    CloseToTray = true, PollSeconds = 60, ReplyMode = "custom", CustomMessage = "fixture-continue-token"
                };
                Directory.CreateDirectory(settings.CodexHome);
                new SettingsStore(directory).Save(settings);
                new StateStore(directory);
                using (var db = new SqliteDatabase(Path.Combine(directory, "monitor.db"))) {
                    db.Execute("CREATE TABLE fixture_guard(value TEXT NOT NULL)");
                    db.Execute("INSERT INTO fixture_guard(value) VALUES(?)", "preserve-this-fixture-value");
                }
            } else {
                var path = Path.Combine(directory, "updates", "failure.json");
                var original = File.ReadAllBytes(path);
                var failure = Json.Deserialize<UpdateFailure>(File.ReadAllText(path));
                Require(failure.Version == args[2], "Failure journal has wrong version.");
                Require(failure.TimeUtc <= DateTime.UtcNow.AddMinutes(1) && failure.TimeUtc >= DateTime.UtcNow.AddMinutes(-2), "Failure journal has wrong timestamp.");
                Require(failure.Message.Contains("24"), "Failure journal does not explain the cooldown.");
                try {
                    VerifyCooldown(directory, failure.Version, false);
                    failure.TimeUtc = DateTime.UtcNow.AddHours(-23).AddMinutes(-59);
                    File.WriteAllText(path, Json.Serialize(failure));
                    VerifyCooldown(directory, failure.Version, false);
                    failure.TimeUtc = DateTime.UtcNow.AddHours(-24).AddMinutes(-1);
                    File.WriteAllText(path, Json.Serialize(failure));
                    VerifyCooldown(directory, failure.Version, true);
                } finally { File.WriteAllBytes(path, original); }
            }
            return 0;
        } catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "tools-error.txt"), error.ToString()); return 1; }
    }
    static void VerifyCooldown(string directory, string version, bool expectDownload) {
        var client = new Client { Version = version };
        using (var updates = new UpdateService(new Application(directory), new Host(), client, "0.0.0")) {
            updates.CheckAsync(false).GetAwaiter().GetResult();
            Require(client.Checks == 1 && client.Downloads == (expectDownload ? 1 : 0), "24-hour automatic download suppression is incorrect.");
        }
    }
    sealed class Client : IReleaseClient {
        public string Version; public int Checks, Downloads;
        public Task<ReleaseInfo> FindUpdateAsync(string current, bool preview, CancellationToken token) { Checks++; return Task.FromResult(new ReleaseInfo { Version = Version }); }
        public Task DownloadAsync(ReleaseInfo release, string destination, IProgress<int> progress, CancellationToken token) { Downloads++; throw new IOException("Fixture deliberately avoids downloading."); }
        public void Dispose() { }
    }
    sealed class Host : IUpdateHost { public Task<bool> TryPauseForUpdateAsync() { return Task.FromResult(false); } public void ResumeAfterUpdateFailure() { } }
    sealed class Application : IApplicationService {
        public Application(string directory) { DataDirectory = directory; }
        public event Action Changed { add { } remove { } }
        public IReadOnlyList<SessionRecord> Sessions { get { return new List<SessionRecord>(); } }
        public IReadOnlyList<MonitorEvent> Events { get { return new List<MonitorEvent>(); } }
        public AppSettings Settings { get { return new AppSettings(); } }
        public string ConnectionStatus { get { return "fixture"; } }
        public bool IsConnected { get { return false; } }
        public string NotificationStatus { get { return "fixture"; } }
        public string DataDirectory { get; private set; }
        public Task RefreshAsync() { return Task.CompletedTask; }
        public Task ContinueAsync(string id) { throw new NotSupportedException(); }
        public void SetSessionMonitoring(string id, bool enabled) { throw new NotSupportedException(); }
        public void SetGlobalMonitoring(bool enabled) { throw new NotSupportedException(); }
        public Task SaveSettingsAsync(AppSettings settings) { throw new NotSupportedException(); }
        public Task<string> TestWebhookAsync(AppSettings settings) { throw new NotSupportedException(); }
        public void Dispose() { }
    }
}
'@

try {
    $mutexName = 'Local\CodexAutoContinue-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $probe = New-Object Threading.Mutex($false, $mutexName)
    try {
        $acquired = $false
        try { $acquired = $probe.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        Assert-True $acquired 'An application instance is already running. Integration verification requires an idle desktop; no tests ran.'
        $probe.ReleaseMutex()
    }
    finally { $probe.Dispose() }

    New-Item -ItemType Directory -Path $root | Out-Null
    $tools = Join-Path $root 'tools'
    $old = Join-Path $root 'old'
    $failed = Join-Path $root 'failed'
    New-Item -ItemType Directory -Path $tools,$old,$failed | Out-Null
    Copy-Item -LiteralPath $release -Destination (Join-Path $tools 'CodexAutoContinue.exe')
    $toolsExe = Join-Path $tools 'FixtureTools.exe'
    Compile-Fixture $toolsSource $toolsExe @((Join-Path $tools 'CodexAutoContinue.exe'))
    Compile-Fixture $fixtureSource (Join-Path $old 'CodexAutoContinue.exe')
    Compile-Fixture ($failureSource.Replace('__VERSION__', $version)) (Join-Path $failed 'CodexAutoContinue.exe')
    $releaseHash = Get-Hash $release

    foreach ($scenario in @('success', 'rollback')) {
        $case = Join-Path $root $scenario
        $data = Join-Path $case 'data'
        $stage = Join-Path $data ('updates\' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        $target = Join-Path $case 'CodexAutoContinue.exe'
        Copy-Item -LiteralPath (Join-Path $old 'CodexAutoContinue.exe') -Destination $target
        $oldHash = Get-Hash $target
        $seed = Start-OwnedProcess $toolsExe ('seed ' + (Quote-Argument $data))
        Assert-True ($seed.WaitForExit(10000) -and $seed.ExitCode -eq 0) ('Fixture data setup failed: ' + $data)
        $seed.Dispose()
        $settingsHash = Get-Hash (Join-Path $data 'settings.json')
        $databaseHash = Get-Hash (Join-Path $data 'monitor.db')
        $parent = Start-OwnedProcess $target ('--fixture-parent --data-dir ' + (Quote-Argument $data))
        Wait-For { Test-Path -LiteralPath (Join-Path $data 'parent-ready.json') } 10 'Fixture parent did not acquire the application mutex.'
        $parentStart = $parent.StartTime.ToUniversalTime().Ticks
        $payload = Join-Path $stage 'payload.exe'
        $payloadSource = if ($scenario -eq 'success') { $release } else { Join-Path $failed 'CodexAutoContinue.exe' }
        Copy-Item -LiteralPath $payloadSource -Destination $payload
        Copy-Item -LiteralPath $release -Destination (Join-Path $stage 'updater.exe')
        $nonce = [Guid]::NewGuid().ToString('N')
        $manifest = [ordered]@{
            TargetPath = $target; ExpectedVersion = $version; Sha256 = (Get-Hash $payload).ToLowerInvariant()
            ParentProcessId = $parent.Id; ParentStartTicks = $parentStart; DataDirectory = $data
            StartInTray = $true; Nonce = $nonce
        }
        $manifestPath = Join-Path $stage 'update.json'
        [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), $utf8)
        $worker = Start-OwnedProcess (Join-Path $stage 'updater.exe') ('--apply-update ' + (Quote-Argument $manifestPath))
        [IO.File]::WriteAllText((Join-Path $data 'fixture-exit.signal'), 'exit', $utf8)
        Assert-True ($parent.WaitForExit(10000) -and $parent.ExitCode -eq 0) 'Fixture parent did not exit cleanly.'
        $parent.Dispose()
        Wait-For { $worker.Refresh(); $worker.HasExited } 55 ('Update worker exceeded its deadline: ' + $scenario)
        $worker.WaitForExit()
        $workerCode = $worker.ExitCode
        $worker.Dispose()
        Find-OwnedChildren

        if ($scenario -eq 'success') {
            $receiptPath = Join-Path $stage 'health.json'
            $failurePath = Join-Path $data 'updates\failure.json'
            $failureDetail = if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath -Raw -Encoding UTF8 } else { 'No failure journal.' }
            Assert-True ($workerCode -eq 0 -and (Test-Path -LiteralPath $receiptPath)) ("Success upgrade failed (worker exit $workerCode). $failureDetail")
            $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert-True ($receipt.Nonce -eq $nonce -and $receipt.Version -eq $version) 'Startup receipt does not match the staged update.'
            Assert-True ($owned.ContainsKey([int]$receipt.ProcessId)) 'Health receipt came from an untracked process.'
            $replacement = Get-Process -Id $receipt.ProcessId -ErrorAction Stop
            Assert-True (-not $replacement.HasExited -and [string]::Equals($replacement.MainModule.FileName, $target, [StringComparison]::OrdinalIgnoreCase)) 'Replacement application is not running from the installation target.'
            $replacement.Dispose()
            Assert-True ((Get-Hash $target) -eq $releaseHash) 'Installed EXE does not match the release payload.'
            Assert-True ((Get-Hash ($target + '.previous')) -eq $oldHash) 'Previous installation was not retained as backup.'
            Assert-True (-not (Test-Path -LiteralPath $failurePath)) 'Successful upgrade left a failure journal.'
        }
        else {
            Assert-True ($workerCode -ne 0) 'Failed startup was reported as successful.'
            Assert-True ((Get-Hash $target) -eq $oldHash) 'Failed startup did not restore the previous EXE.'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $stage 'health.json'))) 'Failed payload unexpectedly wrote a healthy receipt.'
            Wait-For { Test-Path -LiteralPath (Join-Path $data 'restarted.json') } 10 'The restored fixture application did not restart.'
            $restart = Get-Content -LiteralPath (Join-Path $data 'restarted.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $restored = Get-Process -Id $restart.ProcessId -ErrorAction Stop
            Assert-True (-not $restored.HasExited -and $restored.StartTime.ToUniversalTime().Ticks -eq $restart.StartTicks) 'Restored fixture process is not alive.'
            $restored.Dispose()
            $cooldown = Start-OwnedProcess $toolsExe ('cooldown ' + (Quote-Argument $data) + ' ' + (Quote-Argument $version))
            Assert-True ($cooldown.WaitForExit(10000) -and $cooldown.ExitCode -eq 0) ('24-hour failure suppression failed; inspect ' + (Join-Path $data 'tools-error.txt'))
            $cooldown.Dispose()
        }
        Assert-DataPreserved $data $settingsHash $databaseHash
        Write-Output ("PASS $scenario`: real worker, separate parent/child, EXE hashes, settings and SQLite unchanged" + $(if ($scenario -eq 'success') { ', live WPF health receipt and backup' } else { ', previous process restarted and 24-hour suppression boundary' }))
        Stop-OwnedProcesses
    }
    Write-Output ('PASS integration: release ' + $version + ', SHA256 ' + $releaseHash)
}
finally {
    Stop-OwnedProcesses
    if ($KeepArtifacts) { Write-Output ('Fixture artifacts: ' + $root) }
    elseif (Test-Path -LiteralPath $root) {
        $resolvedRoot = [IO.Path]::GetFullPath($root)
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        Assert-True ([string]::Equals([IO.Path]::GetDirectoryName($resolvedRoot), $expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedRoot).StartsWith('CodexAutoContinue-update-integration-')) 'Refusing to remove an unexpected fixture directory.'
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
