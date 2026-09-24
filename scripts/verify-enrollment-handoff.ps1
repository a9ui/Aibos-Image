[CmdletBinding()]
param([string]$DotnetPath = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-handoff-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
foreach ($name in @('MaintenanceEnrollmentHandoff', 'SingleInstanceCoordinator', 'WindowsPathIdentity', 'SharedDataRootLocator')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "local-native\PhotoViewer.Wpf\$name.cs") -Destination $runRoot
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\EnrollmentHandoffProgram.cs') -Destination (Join-Path $runRoot 'Program.cs')
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $runRoot 'Fixture.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>PhotoViewer.Wpf</AssemblyName><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>', $utf8)
$previousScratch = $env:NUGET_SCRATCH
try {
    $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
    & $DotnetPath build (Join-Path $runRoot 'Fixture.csproj') -c Release --nologo --disable-build-servers -p:UseSharedCompilation=false
}
finally { $env:NUGET_SCRATCH = $previousScratch }
if ($LASTEXITCODE -ne 0) { throw 'Handoff fixture build failed.' }
$output = Join-Path $runRoot 'bin\Release\net10.0-windows'
# These four files exercise artifact identity only; this console fixture never loads SQLite.
foreach ($name in @('Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.winsqlite3.dll')) {
    [IO.File]::WriteAllText((Join-Path $output $name), 'synthetic-unloaded-artifact', $utf8)
}
& $DotnetPath (Join-Path $output 'PhotoViewer.Wpf.dll') $runRoot
if ($LASTEXITCODE -ne 0) { throw 'Handoff verification failed.' }

# Exercise the real checker, helper and desktop branch with only fixture processes.
$fixtureScripts = Join-Path $runRoot 'scripts'
$fixtureProject = Join-Path $runRoot 'local-native\PhotoViewer.Wpf'
$fixtureTarget = Join-Path $fixtureProject 'bin\Release\net10.0-windows'
New-Item -ItemType Directory -Path $fixtureScripts, $fixtureTarget, (Join-Path $fixtureScripts 'lib') -Force | Out-Null
foreach ($name in @('check-wpf-launch-target.ps1', 'invoke-aibos-enrollment-handoff.ps1', 'start-aibos-desktop.ps1', 'start-aibos-fixed-generation.ps1', 'start-wpf-detached.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixtureScripts
}
Get-ChildItem -LiteralPath $runRoot -Filter '*.cs' -File | Copy-Item -Destination $fixtureProject
Copy-Item -LiteralPath (Join-Path $runRoot 'Fixture.csproj') -Destination (Join-Path $fixtureProject 'PhotoViewer.Wpf.csproj')
Get-ChildItem -LiteralPath $output -File | Copy-Item -Destination $fixtureTarget
$fixtureIdentity = [guid]::NewGuid().ToString('N')
# An accidental ordinary activation must fail the fixture. Startup exclusion uses a unique name.
[IO.File]::WriteAllText((Join-Path $fixtureScripts 'lib\DesktopActivation.ps1'),
    "function Get-AibosDesktopIdentitySuffix { param([string]`$Identity) return '$fixtureIdentity' }`nfunction Send-AibosDesktopActivation { throw 'Ordinary activation was reached.' }", $utf8)
& git -C $runRoot init -q
if ($LASTEXITCODE -ne 0) { throw 'Fixture Git initialization failed.' }
& git -C $runRoot -c user.name=Fixture -c user.email=fixture@example.com -c commit.gpgsign=false -c core.hooksPath=NUL commit --allow-empty -qm fixture
if ($LASTEXITCODE -ne 0) { throw 'Fixture Git checkpoint failed.' }
& (Join-Path $fixtureScripts 'check-wpf-launch-target.ps1') -Record -Json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture launch provenance failed.' }
$recorded = & (Join-Path $fixtureScripts 'check-wpf-launch-target.ps1') -Json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Fixture launch manifest read failed.' }
$fixtureRoot = Join-Path $runRoot 'launcher enhance'
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$savedIdentity = $env:AIBOS_FIXTURE_HANDOFF_IDENTITY
$savedJobs = $env:AIBOS_FIXTURE_HANDOFF_JOBS
function Invoke-FixtureLauncher([string]$Extra = '', [switch]$Fixed) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command powershell -CommandType Application).Source
    $modeArguments = if ($Fixed) { '-PinnedLaunchManifestSha256 ' + $recorded.launchManifestSha256 } else { '-EnrollmentEnhancementRoot "' + $fixtureRoot + '"' }
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" {1} {2}' -f (Join-Path $fixtureScripts 'start-aibos-desktop.ps1'), $modeArguments, $Extra
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { throw 'Fixture launcher timed out.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }; $process.Dispose() }
}
try {
    $env:AIBOS_FIXTURE_HANDOFF_IDENTITY = $fixtureIdentity
    $env:AIBOS_FIXTURE_HANDOFF_JOBS = Join-Path $fixtureRoot 'jobs.json'
    $accepted = Invoke-FixtureLauncher
    if ($accepted.Code -ne 10) { throw "Fixture desktop handoff failed: $($accepted.Error)" }
    $reply = $accepted.Output | ConvertFrom-Json
    if ($reply.handoff -ne 'verified' -or $reply.enrolled -ne $false -or $reply.maintenanceAllowed -ne $false) { throw 'Fixture launcher granted authority.' }
    if ((Invoke-FixtureLauncher '-AutoStartCompanion').Code -ne 2) { throw 'Inspection allowed automatic Companion startup.' }
    $env:AIBOS_FIXTURE_HANDOFF_JOBS = Join-Path $runRoot 'fixed-launch'
    $fixed = Invoke-FixtureLauncher -Fixed
    if ($fixed.Code -ne 0) { throw "Fixed-generation fixture launch failed: $($fixed.Error)" }
    $marker = $env:AIBOS_FIXTURE_HANDOFF_JOBS + '.pinned'
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -cne $recorded.launchManifestSha256) { throw 'Fixed-generation child did not verify its actual artifacts.' }
    if ((Invoke-FixtureLauncher '-AutoStartCompanion' -Fixed).Code -ne 2) { throw 'Fixed startup allowed automatic Companion startup.' }
    if ((Invoke-FixtureLauncher '-EnrollmentEnhancementRoot ignored' -Fixed).Code -ne 2) { throw 'Mixed explicit startup modes were accepted.' }
    foreach ($variable in @('PHOTOVIEWER_WPF_DOTNET_RUN', 'PHOTOVIEWER_WPF_REBUILD')) {
        $saved = [Environment]::GetEnvironmentVariable($variable)
        try {
            [Environment]::SetEnvironmentVariable($variable, '1')
            if ((Invoke-FixtureLauncher -Fixed).Code -ne 2) { throw "Fixed startup allowed $variable." }
        }
        finally { [Environment]::SetEnvironmentVariable($variable, $saved) }
    }
    $expectedManifest = $recorded.launchManifestSha256
    try {
        $recorded.launchManifestSha256 = '0' * 64
        if ((Invoke-FixtureLauncher -Fixed).Code -ne 2) { throw 'Fixed startup accepted another manifest.' }
    }
    finally { $recorded.launchManifestSha256 = $expectedManifest }
    [IO.File]::AppendAllText((Join-Path $fixtureProject 'Program.cs'), "`n// synthetic source change", $utf8)
    if ((Invoke-FixtureLauncher).Code -ne 2) { throw 'Inspection launched an unverified artifact.' }
    if ((Invoke-FixtureLauncher -Fixed).Code -ne 2) { throw 'Fixed startup launched stale artifacts.' }
    if (@(Get-ChildItem -LiteralPath $fixtureRoot -Force).Count -ne 0) { throw 'Launcher inspection created durable state.' }
    Write-Output '{"launcherChecks":11,"realFreshnessChecker":true,"syntheticOnly":true}'
}
finally { $env:AIBOS_FIXTURE_HANDOFF_IDENTITY = $savedIdentity; $env:AIBOS_FIXTURE_HANDOFF_JOBS = $savedJobs }
Write-Output "fixtureRoot=$runRoot"
