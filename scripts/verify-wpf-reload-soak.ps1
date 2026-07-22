param(
    [string]$Configuration = 'Release',
    [ValidateRange(1000, 5000)]
    [int]$Count = 1000,
    [ValidateRange(20, 50)]
    [int]$Cycles = 24,
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-reload-soak-' + [guid]::NewGuid().ToString('N') + '.json'))
)

$ErrorActionPreference = 'Stop'
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Remove-Item -LiteralPath $OutputPath -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--reload-soak-smoke', ('"{0}"' -f $OutputPath), '--count', $Count.ToString(), '--cycles', $Cycles.ToString()) `
    -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "WPF reload soak exited without producing $OutputPath"
}

$result = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
$failures = @()
if ($process.ExitCode -ne 0) { $failures += "process exit code $($process.ExitCode)" }
if ($result.ok -ne $true) { $failures += 'result.ok was false' }
if ($result.requestedCountPerFolder -ne $Count -or $result.requestedCycles -ne $Cycles -or $result.completedCycles -ne $Cycles) {
    $failures += 'requested or completed fixture dimensions did not match'
}
$expectedExplicitCancels = [math]::Floor(($Cycles + 2) / 3)
if ($result.explicitCancelCount -ne $expectedExplicitCancels -or $result.supersededLoadCount -ne ($Cycles - $expectedExplicitCancels)) {
    $failures += 'explicit cancellation or superseded-load coverage count did not match'
}
if ($result.staleCompletionCount -ne 0 -or $result.staleDetails.Count -ne 0) {
    $failures += "stale completions were observed: $($result.staleCompletionCount)"
}
if ($result.stablePreviewCount -ne $Cycles -or $result.stableModalCount -ne $Cycles) {
    $failures += 'delayed preview or modal decode did not remain on the latest folder/selection'
}
$expectedListProbes = [math]::Floor(($Cycles + 3) / 4)
if ($result.boundedListProbeCount -ne $expectedListProbes) {
    $failures += 'Grid/List churn did not keep every sampled List viewport bounded'
}
if ($result.finalCatalogCount -ne $Count -or $result.finalCatalogCurrent -ne $true `
    -or $result.finalSelectionCurrent -ne $true -or $result.finalModalCurrent -ne $true -or $result.finalTabsCurrent -ne $true) {
    $failures += 'final catalog, selection, modal, or preview tabs were not owned only by the last folder'
}
if ($result.ctsBalanced -ne $true -or $result.loadCtsCreated -ne $result.loadCtsRetired) {
    $failures += "load CTS ownership was unbalanced ($($result.loadCtsCreated)/$($result.loadCtsRetired))"
}
if ($result.heartbeatCount -lt $Cycles -or $result.memoryBounded -ne $true `
    -or $result.managedMemoryBounded -ne $true -or $result.workingSetEnvelopeBounded -ne $true) {
    $failures += 'dispatcher heartbeat, forced-GC managed-memory bound, or working-set envelope failed'
}
if ($result.workingSetPlateauDiagnosticOnly -ne $true) {
    $failures += 'working-set plateau must remain a diagnostic rather than a short-window correctness gate'
}
if ($result.enhancementJobsRead -ne 0 -or $result.enhancementCandidates -ne 0) {
    $failures += 'passive soak touched Enhancement state'
}
if ($result.storesByteIdentical -ne $true -or $result.sourceUntouched -ne $true -or $result.isolated -ne $true -or $result.residueFree -ne $true) {
    $failures += 'source/shared-state/temp-only isolation failed'
}

$result | ConvertTo-Json -Depth 10
if ($failures.Count -gt 0) {
    throw ('WPF reload soak gate failed: ' + ($failures -join '; '))
}
