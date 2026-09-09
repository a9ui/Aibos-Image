[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('aibos-launch-verifier-' + [guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $runRoot 'local-native\PhotoViewer.Wpf'
$targetRoot = Join-Path $projectRoot 'bin\Release\net10.0-windows'
$fixtureScripts = Join-Path $runRoot 'scripts'
New-Item -ItemType Directory -Path $targetRoot, $fixtureScripts -Force | Out-Null
$checker = Join-Path $fixtureScripts 'check-wpf-launch-target.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'check-wpf-launch-target.ps1') -Destination $checker
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $projectRoot 'PhotoViewer.Wpf.csproj'), '<Project />', $utf8)
$sourcePath = Join-Path $projectRoot 'App.cs'
[IO.File]::WriteAllText($sourcePath, '// synthetic source', $utf8)
$artifactNames = @('PhotoViewer.Wpf.exe', 'PhotoViewer.Wpf.dll', 'PhotoViewer.Wpf.deps.json', 'PhotoViewer.Wpf.runtimeconfig.json')
foreach ($name in $artifactNames) {
    [IO.File]::WriteAllText((Join-Path $targetRoot $name), 'synthetic ' + $name, $utf8)
}
& git -C $runRoot init -q
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the synthetic repository.' }
& git -C $runRoot add -- local-native/PhotoViewer.Wpf/App.cs local-native/PhotoViewer.Wpf/PhotoViewer.Wpf.csproj
if ($LASTEXITCODE -ne 0) { throw 'Could not stage the synthetic sources.' }
& git -C $runRoot -c user.name=Fixture -c user.email=fixture@example.com -c commit.gpgsign=false -c core.hooksPath=NUL commit -qm fixture
if ($LASTEXITCODE -ne 0) { throw 'Could not commit the synthetic sources.' }

$checks = [Collections.Generic.List[string]]::new()
function Invoke-Check([int]$ExpectedExit, [string]$ExpectedReason, [switch]$Record) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $checker, '-Json')
    if ($Record) { $arguments += '-Record' }
    $output = & powershell @arguments
    $code = $LASTEXITCODE
    if ($code -ne $ExpectedExit) { throw "Expected exit $ExpectedExit, got $code. Output: $output" }
    $result = ($output -join "`n") | ConvertFrom-Json
    if ($result.reason -ne $ExpectedReason) { throw "Expected $ExpectedReason, got $($result.reason)." }
    $checks.Add($ExpectedReason)
}

Invoke-Check 0 'build-provenance-written' -Record
Invoke-Check 0 'provenance-match'
foreach ($name in $artifactNames) {
    $path = Join-Path $targetRoot $name
    $bytes = [IO.File]::ReadAllBytes($path)
    $timestamp = [IO.File]::GetLastWriteTimeUtc($path)
    try {
        [IO.File]::WriteAllText($path, 'replaced ' + $name, $utf8)
        [IO.File]::SetLastWriteTimeUtc($path, $timestamp)
        Invoke-Check 10 'target-hash-mismatch'
    }
    finally { [IO.File]::WriteAllBytes($path, $bytes) }
}
$dllPath = Join-Path $targetRoot 'PhotoViewer.Wpf.dll'
$savedDll = $dllPath + '.saved'
Move-Item -LiteralPath $dllPath -Destination $savedDll
try { Invoke-Check 10 'target-missing' }
finally { Move-Item -LiteralPath $savedDll -Destination $dllPath }

$stampPath = Join-Path $targetRoot 'PhotoViewer.Wpf.exe.launch.json'
$savedStamp = [IO.File]::ReadAllText($stampPath)
try {
    [IO.File]::WriteAllText($stampPath, '{"schemaVersion":1}', $utf8)
    Invoke-Check 10 'provenance-schema'
    [IO.File]::WriteAllText($stampPath, '{invalid', $utf8)
    Invoke-Check 10 'provenance-invalid'
}
finally { [IO.File]::WriteAllText($stampPath, $savedStamp, $utf8) }
[IO.File]::WriteAllText($sourcePath, '// changed source', $utf8)
Invoke-Check 10 'source-content-mismatch'
# Exercise the actual launcher repair path with real MSBuild, not dummy hashes.
Copy-Item -LiteralPath (Join-Path $repoRoot 'start_wpf.bat') -Destination $runRoot
[IO.File]::WriteAllText((Join-Path $projectRoot 'PhotoViewer.Wpf.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>', $utf8)
[IO.File]::WriteAllText($sourcePath, 'System.Console.WriteLine("Synthetic launch succeeded.");', $utf8)
$savedDesktop = $env:AIBOS_DESKTOP_LAUNCH
$savedRebuild = $env:PHOTOVIEWER_WPF_REBUILD
$savedRun = $env:PHOTOVIEWER_WPF_DOTNET_RUN
$savedScratch = $env:NUGET_SCRATCH
try {
    $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
    $env:AIBOS_DESKTOP_LAUNCH = '1'
    $env:PHOTOVIEWER_WPF_REBUILD = '0'
    $env:PHOTOVIEWER_WPF_DOTNET_RUN = '0'
    & (Join-Path $runRoot 'start_wpf.bat')
    if ($LASTEXITCODE -ne 0) { throw 'Initial real fixture build/launch failed.' }
    Invoke-Check 0 'provenance-match'
    $runtimeConfig = Join-Path $targetRoot 'PhotoViewer.Wpf.runtimeconfig.json'
    $correct = [IO.File]::ReadAllText($runtimeConfig)
    $timestamp = [IO.File]::GetLastWriteTimeUtc($runtimeConfig)
    [IO.File]::WriteAllText($runtimeConfig, $correct.Replace('Microsoft.NETCore.App', 'Microsoft.BADCore.App'), $utf8)
    [IO.File]::SetLastWriteTimeUtc($runtimeConfig, $timestamp)
    Invoke-Check 10 'target-hash-mismatch'
    & (Join-Path $runRoot 'start_wpf.bat')
    if ($LASTEXITCODE -ne 0) { throw 'Real repair build/launch failed.' }
    if ([IO.File]::ReadAllText($runtimeConfig) -cne $correct) { throw 'Repair did not restore runtime configuration bytes.' }
    Invoke-Check 0 'provenance-match'
    $checks.Add('real-build-restores-runtimeconfig')
    $beforeFailedBuild = [IO.File]::ReadAllText($stampPath)
    [IO.File]::WriteAllText($sourcePath, 'intentional compiler error;', $utf8)
    & (Join-Path $runRoot 'start_wpf.bat')
    if ($LASTEXITCODE -eq 0) { throw 'Invalid fixture source unexpectedly built.' }
    if ([IO.File]::ReadAllText($stampPath) -cne $beforeFailedBuild) { throw 'Failed build replaced the provenance stamp.' }
    $checks.Add('failed-build-does-not-record')
}
finally {
    $env:AIBOS_DESKTOP_LAUNCH = $savedDesktop
    $env:PHOTOVIEWER_WPF_REBUILD = $savedRebuild
    $env:PHOTOVIEWER_WPF_DOTNET_RUN = $savedRun
    $env:NUGET_SCRATCH = $savedScratch
}
[pscustomobject]@{ ok = $true; checks = @($checks); syntheticOnly = $true; fixtureRoot = $runRoot } | ConvertTo-Json -Depth 4
