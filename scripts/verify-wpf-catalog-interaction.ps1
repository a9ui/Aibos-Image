param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [int]$Count = 100000,
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-catalog-interaction-' + [guid]::NewGuid().ToString('N') + '.json')),
    [int]$OverallTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if ($Count -lt 10000 -or $Count -gt 250000) { throw 'Count must be between 10,000 and 250,000.' }
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
if ($OverallTimeoutSeconds -lt 1) { throw 'OverallTimeoutSeconds must be positive.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$progressPath = $outputFullPath + '.progress'
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF executable was not found: $exe" }

Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $progressPath -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe `
    -ArgumentList @('--catalog-interaction-smoke', ('"{0}"' -f $outputFullPath), '--count', $Count.ToString()) `
    -WindowStyle Hidden -PassThru
$completed = $process.WaitForExit($OverallTimeoutSeconds * 1000)
if (-not $completed) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $progress = if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
        Get-Content -Raw -LiteralPath $progressPath
    }
    else {
        'unknown'
    }
    Remove-Item -LiteralPath $progressPath -Force -ErrorAction SilentlyContinue
    throw "WPF catalog interaction smoke timed out after $OverallTimeoutSeconds seconds at $progress."
}
Remove-Item -LiteralPath $progressPath -Force -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $outputFullPath -PathType Leaf)) {
    throw "WPF catalog interaction smoke exited without producing $outputFullPath"
}

$result = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()
if ($process.ExitCode -ne 0) { $failures.Add("process exit code $($process.ExitCode)") }
if ($result.ok -ne $true) { $failures.Add('result.ok was false') }
if ($result.requestedCount -ne $Count -or $result.catalogCount -ne $Count -or $result.filteredCount -ne $Count) {
    $failures.Add('logical catalog count was not exact')
}
if ($result.countsExact -ne $true -or $result.searchCompletionsApplied -ne $true) {
    $failures.Add('search/filter result counts or completion ordering failed')
}
if ($result.warmupComplete -ne $true) {
    $failures.Add('catalog interaction warmup did not complete')
}
if ($result.favoriteEvictionExact -ne $true `
    -or $result.favoriteEvictionAutomationExact -ne $true) {
    $failures.Add('favorite-filter eviction did not republish an externally searchable Automation projection')
}
if ($result.searchP95Ms -gt 250 -or $result.filterP95Ms -gt 250 -or $result.sortP95Ms -gt 500) {
    $failures.Add("interaction p95 exceeded its budget (search/filter/sort $($result.searchP95Ms)/$($result.filterP95Ms)/$($result.sortP95Ms))")
}
if ($result.selectionStable -ne $true) { $failures.Add('selection did not survive search/filter/sort churn') }
if ($result.keyboardNavigationExact -ne $true `
    -or $result.keyboardEndExact -ne $true `
    -or $result.keyboardBoundaryEndExact -ne $true `
    -or $result.keyboardLetterIgnored -ne $true `
    -or $result.keyboardHomeExact -ne $true `
    -or $result.keyboardRightExact -ne $true `
    -or $result.keyboardDownExact -ne $true `
    -or $result.keyboardPageDownExact -ne $true) {
    $failures.Add('100k keyboard Home/End/Page/arrow navigation was not exact')
}
if ($result.projectionFocusRestored -ne $true) {
    $failures.Add('logical gallery focus was not restored after projection publication')
}
if ($result.externalAutomationExact -ne $true `
    -or $result.externalAutomationPreservedSelection -ne $true `
    -or $result.automationLookupMaxMs -gt 4) {
    $failures.Add("external AutomationElement ItemContainer lookup failed or exceeded 4 ms ($($result.automationLookupMaxMs) ms provider time)")
}
if ($result.repeatedAutomationRealizeCoalesced -ne $true) {
    $failures.Add('repeated UI Automation Realize requests were not coalesced')
}
if ($result.staleAutomationPeerLifetimeExact -ne $true `
    -or $result.staleAutomationRealizeUnavailable -ne $true `
    -or $result.staleAutomationSelectUnavailable -ne $true) {
    $failures.Add('stale UI Automation peer survived projection removal or mutated selection')
}
if ($result.automationVirtualizedItemExact -ne $true `
    -or $result.automationRealizePreservedSelection -ne $true `
    -or $result.automationSelectionExact -ne $true) {
    $failures.Add('off-screen UI Automation realization/selection contract failed')
}
if ($result.recycledContainerStateReset -ne $true) {
    $failures.Add('recycled gallery container retained stale automation, selection, or focus state')
}
if ($result.keyboardAccessibilityBounded -ne $true `
    -or $result.keyboardAccessibilityMaxRealized -gt $result.gridRealizationLimit) {
    $failures.Add('keyboard/UI Automation navigation exceeded the realized-container bound')
}
if ($result.keyboardSelectionSyncMaxMs -gt 4) {
    $failures.Add("keyboard/UI Automation selection synchronization exceeded 4 ms ($($result.keyboardSelectionSyncMaxMs) ms)")
}
if ($result.gridItemsSourceCount -ne $Count -or $result.gridUsesFullExtentVirtualization -ne $true -or $result.gridRealizedCount -gt $result.gridRealizationLimit) {
    $failures.Add('100k gallery containers were not bounded by full-extent virtualization')
}
if ($result.catalogProjectionMaxApplySliceMs -gt 4) {
    $failures.Add("catalog projection apply slice was $($result.catalogProjectionMaxApplySliceMs) ms")
}
if ($result.dispatcherHeartbeatMaxGapMs -gt 50) { $failures.Add("dispatcher heartbeat gap was $($result.dispatcherHeartbeatMaxGapMs) ms") }
if ($result.mixedLatestWins -ne $true -or $result.mixedStaleSearchDiscarded -ne $true -or $result.mixedStaleFilterDiscarded -ne $true -or $result.mixedDiscardedCount -lt 1) {
    $failures.Add('mixed search/filter/sort did not discard stale generations')
}
if ($result.mixedViewportAnchorPreserved -ne $true) {
    $failures.Add('mixed projection did not preserve the first visible anchor')
}
if ($result.liveManagedMemoryRegressionPercent -gt 15) {
    $failures.Add("live managed-memory regression was $($result.liveManagedMemoryRegressionPercent)%")
}
if ($result.normalizedWorkingSetRegressionPercent -gt 35) {
    $failures.Add("normalized working-set regression was $($result.normalizedWorkingSetRegressionPercent)%")
}

$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) {
    throw ('WPF catalog interaction gate failed: ' + ($failures -join '; '))
}
