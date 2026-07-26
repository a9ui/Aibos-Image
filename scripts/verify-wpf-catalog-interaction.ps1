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
if ($result.favoritePendingBroadenRaceExact -ne $true) {
    $failures.Add('favorite exclusion retained a stale narrow projection while a broader search was pending')
}
if ($result.favoriteRapidReincludeRaceExact -ne $true) {
    $failures.Add('rapid re-favorite did not cancel an in-flight favorite exclusion projection')
}
if ($result.favoriteSortRaceExact -ne $true) {
    $failures.Add('favorite mutation superseded an interactive sort without preserving its final catalog order')
}
if ($result.favoriteDuplicateEvictionRaceExact -ne $true) {
    $failures.Add('superseded favorite exclusion released a newer generation notification')
}
if ($result.favoriteFilteredFailureStatusExact -ne $true) {
    $failures.Add('favorite-filter write failure did not preserve rollback projection and Retry status')
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
if ($null -eq $result.automationRealizeMaxDispatchMs) {
    $failures.Add('UI Automation realization dispatch duration was not recorded')
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
if ($result.favoriteEvictionExact -ne $true -or $result.favoriteEvictionAutomationExact -ne $true) {
    $failures.Add('favorite-only removal did not evict the target and preserve the exact neighboring selection/UI Automation projection')
}
if ($result.favoriteEvictionBudgetMs -ne 100 -or $result.favoriteEvictionElapsedMs -gt $result.favoriteEvictionBudgetMs) {
    $failures.Add("favorite-only removal was $($result.favoriteEvictionElapsedMs) ms (budget $($result.favoriteEvictionBudgetMs) ms)")
}
if ($result.catalogProjectionDiagnosticSliceTargetMs -ne 4) {
    $failures.Add("catalog projection diagnostic target was $($result.catalogProjectionDiagnosticSliceTargetMs) ms")
}
$hasEffectiveDetachBudget = $null -ne $result.catalogProjectionMaxResetPanelBudgetMs
$detachUsesThreadCpu = $false
$detachBudgetValue = if ($hasEffectiveDetachBudget) {
    $detachUsesThreadCpu = $result.catalogProjectionDetachBudgetBasis -ne 'wall-fallback'
    $result.catalogProjectionMaxResetPanelBudgetMs
}
elseif ($null -ne $result.catalogProjectionMaxResetPanelThreadCpuMs `
    -and $result.catalogProjectionMaxResetPanelThreadCpuMs -ge 0) {
    $detachUsesThreadCpu = $true
    $result.catalogProjectionMaxResetPanelThreadCpuMs
}
else {
    $result.catalogProjectionMaxSingleContainerDetachMs
}
$detachBudgetBasis = if ($hasEffectiveDetachBudget) {
    $result.catalogProjectionDetachBudgetBasis
}
elseif ($detachUsesThreadCpu) {
    'thread-cpu'
}
else {
    'wall-fallback'
}
if ($result.catalogProjectionSingleContainerDetachBudgetMs -ne 12) {
    $failures.Add(
        "single-container reset diagnostic threshold was " +
        "$($result.catalogProjectionSingleContainerDetachBudgetMs) ms")
}
if ($detachBudgetValue -gt $result.catalogProjectionSingleContainerDetachBudgetMs) {
    $failures.Add(
        "single-container reset exceeded: $detachBudgetValue ms on $detachBudgetBasis " +
        "(budget $($result.catalogProjectionSingleContainerDetachBudgetMs) ms; " +
        "effective $($result.catalogProjectionMaxResetPanelBudgetMs) ms; " +
        "code CPU $($result.catalogProjectionMaxResetPanelThreadCpuMs) ms; " +
        "advisory wall $($result.catalogProjectionMaxSingleContainerDetachMs) ms; " +
        "dominant $($result.catalogProjectionDominantResetSubstep); " +
        "generator $($result.catalogProjectionMaxGeneratorRemoveMs) ms; " +
        "deferred-measure $($result.catalogProjectionMaxForgetDeferredMeasureMs) ms; " +
        "visual $($result.catalogProjectionMaxRemoveInternalChildRangeMs) ms; " +
        "visual thread CPU $($result.catalogProjectionMaxRemoveInternalChildRangeThreadCpuMs) ms; " +
        "panel total $($result.catalogProjectionMaxResetPanelTotalMs) ms)")
}
if ($result.catalogProjectionMaxDetachedContainersPerSlice -gt 1) {
    $failures.Add(
        "catalog reset detached $($result.catalogProjectionMaxDetachedContainersPerSlice) " +
        'containers in one Dispatcher turn')
}
if ($result.catalogProjectionInputBoundaryBeforePublicationExact -ne $true `
    -or $result.catalogProjectionInputBoundaryAfterPublicationExact -ne $true) {
    $failures.Add(
        "catalog publication input boundaries were not exact " +
        "(before $($result.catalogProjectionInputBoundaryBeforePublicationExact); " +
        "after $($result.catalogProjectionInputBoundaryAfterPublicationExact))")
}
if ($result.catalogProjectionMaxSingleContainerDetachMs -gt 0 `
    -and ($result.catalogProjectionMaxResetPanelTotalMs -le 0 `
        -or $result.catalogProjectionDominantResetSubstep -eq 'none' `
        -or ($detachUsesThreadCpu `
            -and $result.catalogProjectionMaxRemoveInternalChildRangeThreadCpuMs -lt 0))) {
    $failures.Add('single-container reset sub-step attribution was missing')
}
if ($result.catalogProjectionMaxApplySliceMs -gt $result.catalogProjectionDiagnosticSliceTargetMs) {
    $failures.Add(
        "catalog projection apply slice exceeded: " +
        "$($result.catalogProjectionMaxApplySliceOperation) " +
        "$($result.catalogProjectionMaxApplySliceMs) ms > " +
        "$($result.catalogProjectionDiagnosticSliceTargetMs) ms")
}
if ($result.farTailSelectionReleaseExact -ne $true `
    -or $result.farTailSelectionReleaseCompletion.preResetSelectionCleared -ne $true `
    -or $result.farTailSelectionReleaseCompletion.preResetSelectionReleasedCount -lt 1) {
    $failures.Add(
        "far-tail Extended selection release contract failed " +
        "(exact $($result.farTailSelectionReleaseExact); " +
        "cleared $($result.farTailSelectionReleaseCompletion.preResetSelectionCleared); " +
        "released $($result.farTailSelectionReleaseCompletion.preResetSelectionReleasedCount))")
}
if ($result.secondaryEvictionReleaseExact -ne $true `
    -or $result.secondaryEvictionReleaseCompletion.preResetSelectionCleared -ne $true `
    -or $result.secondaryEvictionReleaseCompletion.preResetSelectionReleasedCount -lt 1) {
    $failures.Add(
        "evicted-secondary Extended selection release contract failed " +
        "(exact $($result.secondaryEvictionReleaseExact); " +
        "cleared $($result.secondaryEvictionReleaseCompletion.preResetSelectionCleared); " +
        "released $($result.secondaryEvictionReleaseCompletion.preResetSelectionReleasedCount))")
}
if ($result.boundarySelectionReleaseExact -ne $true `
    -or $result.boundarySelectionReleaseCompletion.preResetSelectionCleared -ne $true `
    -or $result.boundarySelectionReleaseCompletion.preResetSelectionReleasedCount -lt 1) {
    $failures.Add(
        "23,999-index selection boundary release contract failed " +
        "(exact $($result.boundarySelectionReleaseExact); " +
        "cleared $($result.boundarySelectionReleaseCompletion.preResetSelectionCleared); " +
        "released $($result.boundarySelectionReleaseCompletion.preResetSelectionReleasedCount))")
}
if ($result.catalogProjectionMaxPreResetSelectionReleaseMs `
        -gt $result.catalogProjectionDiagnosticSliceTargetMs `
    -or $result.catalogProjectionPreResetSelectionClearedExact -ne $true `
    -or $result.catalogProjectionPreResetSelectionReleasedCount -le 0) {
    $failures.Add(
        "pre-Reset WPF selection release contract failed " +
        "(wall $($result.catalogProjectionMaxPreResetSelectionReleaseMs)/" +
        "$($result.catalogProjectionDiagnosticSliceTargetMs) ms; " +
        "cleared exact $($result.catalogProjectionPreResetSelectionClearedExact); " +
        "released $($result.catalogProjectionPreResetSelectionReleasedCount))")
}
if ($result.catalogSnapshotResetBudgetMs -ne 12 `
    -or $result.catalogSnapshotResetMaxWallMs -gt $result.catalogSnapshotResetBudgetMs `
    -or $result.catalogSnapshotResetCountExact -ne $true `
    -or $result.catalogSnapshotResetCpuMeasuredExact -ne $true `
    -or $result.catalogSnapshotResetMaxThreadCpuMs -lt 0) {
    $failures.Add(
        "catalog snapshot Reset contract failed " +
        "(wall $($result.catalogSnapshotResetMaxWallMs)/" +
        "$($result.catalogSnapshotResetBudgetMs) ms; " +
        "CPU $($result.catalogSnapshotResetMaxThreadCpuMs) ms; " +
        "count exact $($result.catalogSnapshotResetCountExact); " +
        "CPU exact $($result.catalogSnapshotResetCpuMeasuredExact))")
}
if ($result.coldGalleryFocusWarmupBudgetMs -ne 250 `
    -or $result.coldGalleryFocusWarmupMs -gt $result.coldGalleryFocusWarmupBudgetMs) {
    $failures.Add(
        "cold gallery-focus warmup exceeded: " +
        "$($result.coldGalleryFocusWarmupMs) ms > " +
        "$($result.coldGalleryFocusWarmupBudgetMs) ms")
}
$controlConsensus = $result.schedulerControlConsensus
$dispatcherDiagnostic = $result.dispatcherDiagnostic
if ($result.dispatcherHeartbeatBudgetMs -ne 50 `
    -or $null -eq $controlConsensus `
    -or $null -eq $dispatcherDiagnostic) {
    $failures.Add('dispatcher heartbeat diagnostic contract was missing')
}
elseif ($controlConsensus.sensorValid -ne $true `
    -or $controlConsensus.initialSamplesReady -ne $true `
    -or $controlConsensus.uiThreadPriorityNormal -ne $true `
    -or $controlConsensus.initialPhaseValid -ne $true `
    -or $controlConsensus.aboveNormalProbe.requestedPriority -ne 'AboveNormal' `
    -or $controlConsensus.normalProbe.requestedPriority -ne 'Normal' `
    -or $controlConsensus.aboveNormalProbe.priorityApplied -ne $true `
    -or $controlConsensus.normalProbe.priorityApplied -ne $true `
    -or $controlConsensus.aboveNormalProbe.highResolutionTimerCreated -ne $true `
    -or $controlConsensus.normalProbe.highResolutionTimerCreated -ne $true `
    -or $controlConsensus.aboveNormalProbe.threadStopped -ne $true `
    -or $controlConsensus.normalProbe.threadStopped -ne $true `
    -or $controlConsensus.aboveNormalProbe.bufferOverflow -ne $false `
    -or $controlConsensus.normalProbe.bufferOverflow -ne $false `
    -or $controlConsensus.aboveNormalProbe.timestampsMonotonic -ne $true `
    -or $controlConsensus.normalProbe.timestampsMonotonic -ne $true `
    -or $controlConsensus.aboveNormalProbe.cadenceValid -ne $true `
    -or $controlConsensus.normalProbe.cadenceValid -ne $true `
    -or $controlConsensus.aboveNormalProbe.setTimerError -ne 0 `
    -or $controlConsensus.normalProbe.setTimerError -ne 0 `
    -or $controlConsensus.aboveNormalProbe.waitError -ne 0 `
    -or $controlConsensus.normalProbe.waitError -ne 0 `
    -or $controlConsensus.probePeriodMs -ne 2 `
    -or $controlConsensus.probePhaseOffsetMs -ne 1 `
    -or $controlConsensus.lateIntervalEpsilonMs -ne 0.5 `
    -or $controlConsensus.probeCadenceToleranceMs -ne 0.75 `
    -or $controlConsensus.probeCadenceAgreementToleranceMs -ne 0.25 `
    -or $controlConsensus.probeCadenceAgreementValid -ne $true) {
    $failures.Add('independent high-resolution scheduler probes were invalid or incomplete')
}
if ($null -ne $dispatcherDiagnostic) {
    $classifiedOverBudgetCount =
        [int]$dispatcherDiagnostic.schedulerQueueDelayCount `
        + [int]$dispatcherDiagnostic.activeOperationDiagnosticCount `
        + [int]$dispatcherDiagnostic.inconclusiveCount
    if ($dispatcherDiagnostic.sensorValid -ne $true `
        -or $dispatcherDiagnostic.hooksStarted -ne $true `
        -or $dispatcherDiagnostic.hooksStopped -ne $true `
        -or $dispatcherDiagnostic.ringOverflow -ne $false `
        -or $dispatcherDiagnostic.uiThreadCpuReadFailureCount -ne 0 `
        -or $dispatcherDiagnostic.activeStackOverflowCount -ne 0 `
        -or $dispatcherDiagnostic.activeStackMismatchCount -ne 0 `
        -or $dispatcherDiagnostic.lifecycleTimestampInversionCount -ne 0 `
        -or $dispatcherDiagnostic.startedWithoutTerminalCount -ne 0 `
        -or $dispatcherDiagnostic.heartbeatLifecycleMissingCount -ne 0 `
        -or $dispatcherDiagnostic.panelPhaseOverflow -ne $false `
        -or $dispatcherDiagnostic.panelPhaseInvalidCount -ne 0 `
        -or $classifiedOverBudgetCount -ne $dispatcherDiagnostic.rawOverBudgetCount `
        -or $dispatcherDiagnostic.rawOverBudgetCount -ne @($result.heartbeatGapSamples).Count) {
        $failures.Add(
            "dispatcher diagnostic sensor was invalid or incomplete " +
            "(raw max $($result.dispatcherHeartbeatMaxGapMs) ms; " +
            "queue/active/inconclusive " +
            "$($dispatcherDiagnostic.schedulerQueueDelayCount)/" +
            "$($dispatcherDiagnostic.activeOperationDiagnosticCount)/" +
            "$($dispatcherDiagnostic.inconclusiveCount))")
    }
}
if ($dispatcherDiagnostic.rawOverBudgetCount -gt 0 `
    -and ($null -eq $dispatcherDiagnostic `
        -or $dispatcherDiagnostic.maxProductGapMs -gt $result.dispatcherHeartbeatBudgetMs `
        -or $dispatcherDiagnostic.activeOperationDiagnosticCount -gt 0 `
        -or $dispatcherDiagnostic.inconclusiveCount -gt 0)) {
    $failures.Add(
        "raw dispatcher heartbeat gap was $($result.dispatcherHeartbeatMaxGapMs) ms " +
        "(budget $($result.dispatcherHeartbeatBudgetMs) ms; " +
        "product max $($dispatcherDiagnostic.maxProductGapMs) ms; " +
        "strict queue max $($dispatcherDiagnostic.maxStrictSchedulerQueueDelayMs) ms; " +
        "queue/active/inconclusive " +
        "$($dispatcherDiagnostic.schedulerQueueDelayCount)/" +
        "$($dispatcherDiagnostic.activeOperationDiagnosticCount)/" +
        "$($dispatcherDiagnostic.inconclusiveCount))")
}
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
