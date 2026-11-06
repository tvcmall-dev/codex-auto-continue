param([string]$Version = '0.1.0-preview')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid package version.' }
Push-Location $PSScriptRoot
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
    if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Install a .NET SDK and .NET Framework 4.8 Developer Pack first.' }
    & $dotnet build tests/CodexAutoContinue.Tests/CodexAutoContinue.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & './tests/CodexAutoContinue.Tests/bin/Release/net48/CodexAutoContinue.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Behavioral tests failed.' }
    $packageName = "CodexAutoContinue-$Version-win-x64"
    $destination = Join-Path $PSScriptRoot "dist/$packageName"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath 'src/CodexAutoContinue/bin/Release/net48/CodexAutoContinue.exe' -Destination $destination
    Copy-Item -LiteralPath 'src/CodexAutoContinue/bin/Release/net48/CodexAutoContinue.exe.config' -Destination $destination
    Copy-Item -LiteralPath 'docs/便携包说明.md' -Destination (Join-Path $destination 'README.zh-CN.md')
    Copy-Item -LiteralPath 'docs/使用说明.md' -Destination (Join-Path $destination '使用说明.md')
    $hash = (Get-FileHash -LiteralPath (Join-Path $destination 'CodexAutoContinue.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Value "$hash  CodexAutoContinue.exe" -Encoding ASCII
    $archive = Join-Path $PSScriptRoot "dist/$packageName.zip"
    Compress-Archive -LiteralPath $destination -DestinationPath $archive -Force
    Write-Output "EXE: $(Join-Path $destination 'CodexAutoContinue.exe')"
    Write-Output "ZIP: $archive"
    Write-Output "SHA256: $hash"
}
finally { Pop-Location }
