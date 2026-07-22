param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-shared-state-writer-' + [guid]::NewGuid().ToString('N') + '.json')),
    [ValidateRange(10, 300)]
    [int]$TimeoutSeconds = 90,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}
if ($outputFullPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
$preserveOutput = $PSBoundParameters.ContainsKey('OutputPath')

if (-not $NoBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe)) { throw "WPF executable is missing: $exe" }

$process = $null
try {
    Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--shared-state-writer-smoke', ('"{0}"' -f $outputFullPath)) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
        throw "WPF shared-state writer PID $($process.Id) exceeded ${TimeoutSeconds}s and was terminated"
    }
    $process.Refresh()

    if (-not (Test-Path -LiteralPath $outputFullPath)) {
        throw "WPF shared-state writer process exited without producing $outputFullPath"
    }

    $result = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json
    $failures = [Collections.Generic.List[string]]::new()
    if ($process.ExitCode -ne 0) { $failures.Add("process exit code $($process.ExitCode)") }
    if ($result.ok -ne $true) { $failures.Add('result.ok was false') }
    foreach ($required in @(
        'coalesced',
        'durableSuccessStatus',
        'externalFavoritePreserved',
        'staleCompletionSafe',
        'favoriteRolledBack',
        'favoriteFailureBlockedNewAction',
        'favoriteRetry',
        'seenRolledBack',
        'seenFailureBlockedNewAction',
        'seenRetry',
        'protectedRollback',
        'reloadFirstDrainGuarded',
        'reloadSecondDrainGuarded',
        'reloadBarrierRaceSafe',
        'reloadPreparationGuarded',
        'reloadSeenRetryGuarded',
        'reloadFavoriteRetryGuarded',
        'reloadRetryGuarded',
        'closeDuringPreparationForceSaved',
        'dualFailureCompositeRetry',
        'successPreservedCompositeRetry',
        'compositeReopened',
        'failedBatchCloseGuarded',
        'pendingCloseDrained',
        'reopenedGamma',
        'favoriteWriterAdopted',
        'seenWriterAdopted',
        'sourceUntouched',
        'jobsUntouched',
        'residueFree',
        'isolated'
    )) {
        if ($result.$required -ne $true) { $failures.Add("$required was not true") }
    }
    if ($result.coalescedBatches -ne 1) { $failures.Add("coalesced batch count was $($result.coalescedBatches), expected 1") }
    if ($result.favoriteBatchCount -lt 6 -or $result.seenBatchCount -lt 2) {
        $failures.Add('focused writer batch evidence was incomplete')
    }
    if ($result.browserPortUsed -ne $false -or $result.sourceOrUserCacheTouched -ne $false) {
        $failures.Add('isolation declaration was not false/false')
    }
    if ([string]::IsNullOrWhiteSpace($result.smokeRoot) -or (Test-Path -LiteralPath $result.smokeRoot)) {
        $failures.Add('complete smoke root was not removed')
    }

    $result | ConvertTo-Json -Depth 10
    if ($failures.Count -gt 0) {
        throw ('WPF shared-state writer gate failed: ' + ($failures -join '; '))
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
    if (-not $preserveOutput) {
        Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue
    }
}
