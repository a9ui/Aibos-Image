param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-external-open-' + [guid]::NewGuid().ToString('N') + '.json')),
    [string]$ExecutablePath = '',
    [switch]$SkipBuild,
    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $fullOutputPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'External-open smoke output must stay under the temp directory.'
}
$OutputPath = $fullOutputPath

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
if (-not $SkipBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
}
$exe = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExecutablePath))

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe.FullName `
    -ArgumentList @('--external-open-smoke', ('"{0}"' -f $OutputPath)) `
    -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "WPF external open smoke timed out after $TimeoutSeconds seconds."
    }
    $processExitCode = $process.ExitCode
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    $process.Dispose()
}

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF external open process exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($processExitCode -ne 0) { $failures += "process exit code $processExitCode" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.selected -ne $true -or $result.modalOpened -ne $true -or $result.modalDecodeSettled -ne $true) {
    $failures += 'temp fixture was not selected and decoded in the modal'
}
if ($result.successfulLaunch -ne $true) { $failures += 'injected successful ShellExecute launch was not exact' }
if ($result.literalFileNameOnly -ne $true) { $failures += 'external Open supplied command arguments instead of one literal FileName' }
$fixtureName = [IO.Path]::GetFileName([string]$result.validPath)
if (-not ($fixtureName.Contains('&') `
    -and $fixtureName.Contains('%') `
    -and $fixtureName.Contains('!') `
    -and $fixtureName.Contains('^') `
    -and $fixtureName.Contains('(') `
    -and $fixtureName.Contains(')') `
    -and $fixtureName.Contains(' ') `
    -and $fixtureName -match '[^\x00-\x7F]')) {
    $failures += 'external Open fixture did not cover metacharacters, spaces, and Unicode'
}
if ($result.enhancedLaunch -ne $true) { $failures += 'displayed Enhanced target or file capacity was not used' }
if ($result.enhancedFallbacks -ne $true) { $failures += 'missing/stale Enhanced output did not fall back to Original target and capacity' }
if ($result.formatterBoundaries -ne $true) { $failures += '0.00MB formatter boundary contract failed' }
if ($result.outsideOwnershipRejected -ne $true) { $failures += 'managed output ownership guard accepted an outside output' }
if ($result.expectedFailuresHandled -ne $true) { $failures += 'expected ShellExecute, I/O, access, or path failure escaped the event boundary' }
if ($result.currentSourceRevalidated -ne $true) { $failures += 'current selected catalog source was not revalidated immediately before launch' }
if ($result.safeTargetBoundary -ne $true) { $failures += 'external Open accepted an unverified command, missing file, directory, or relative path' }
if ($result.interactionStable -ne $true) { $failures += 'focus, selection, modal, or Automation state changed during external open' }
if ($result.sourceUntouched -ne $true -or $result.mutableStateUntouched -ne $true) {
    $failures += 'source, state, favorites, seen, recent, or jobs fingerprint changed'
}
if ($result.passive -ne $true) { $failures += 'external open touched enhancement work' }

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF external open gate failed: ' + ($failures -join '; '))
}
