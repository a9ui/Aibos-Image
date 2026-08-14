param(
    [string]$Configuration = 'Release',
    [ValidateSet('net8.0-windows', 'net10.0-windows')]
    [string]$TargetFramework = 'net10.0-windows',
    [string]$DotnetPath = 'dotnet',
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
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\$TargetFramework\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$progressPath = $outputFullPath + '.progress'
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}

if (-not $SkipBuild) {
    if ($TargetFramework -eq 'net10.0-windows') {
        & $DotnetPath build $project -c $Configuration --nologo
    }
    else {
        & $DotnetPath msbuild $project -restore `
            "-property:TargetFramework=$TargetFramework" `
            "-property:Configuration=$Configuration" `
            -nologo -verbosity:minimal
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF executable was not found: $exe" }

$dotNetExecutable = (Get-Command $DotnetPath -ErrorAction Stop).Source
$dotNetRoot = Split-Path -Parent $dotNetExecutable
$previousDotNetRoot = $env:DOTNET_ROOT
$previousDotNetRootX64 = $env:DOTNET_ROOT_X64
try {
    $env:DOTNET_ROOT = $dotNetRoot
    $env:DOTNET_ROOT_X64 = $dotNetRoot
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
# Keep the product and hosted targets fixed. Only one 25 ms hosted scheduler
# measurement window is tolerated; larger regressions remain blocking.
if ($result.searchProductTargetMs -ne 250 `
    -or $result.filterProductTargetMs -ne 250 `
    -or $result.sortProductTargetMs -ne 550 `
    -or $result.searchHostedAcceptanceMs -ne 350 `
    -or $result.filterHostedAcceptanceMs -ne 350 `
    -or $result.sortHostedAcceptanceMs -ne 650 `
    -or $result.hostedMeasurementToleranceMs -ne 25) {
    $failures.Add('interaction product-target/hosted-acceptance contract was missing')
}
elseif ($result.searchP95Ms -gt ($result.searchHostedAcceptanceMs + $result.hostedMeasurementToleranceMs) `
    -or $result.filterP95Ms -gt ($result.filterHostedAcceptanceMs + $result.hostedMeasurementToleranceMs) `
    -or $result.sortP95Ms -gt ($result.sortHostedAcceptanceMs + $result.hostedMeasurementToleranceMs)) {
    $failures.Add("interaction p95 exceeded its budget (search/filter/sort $($result.searchP95Ms)/$($result.filterP95Ms)/$($result.sortP95Ms))")
}
if ($result.searchProductTargetMet -ne $true `
    -or $result.filterProductTargetMet -ne $true `
    -or $result.sortProductTargetMet -ne $true) {
    Write-Warning "Product interaction target exceeded but remained within hosted acceptance (search/filter/sort $($result.searchP95Ms)/$($result.filterP95Ms)/$($result.sortP95Ms))."
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
if ($result.favoriteEvictionBudgetMs -ne 125 -or $result.favoriteEvictionElapsedMs -gt $result.favoriteEvictionBudgetMs) {
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
if ($result.catalogProjectionSingleContainerDetachHostedAcceptanceMs -ne 32) {
    $failures.Add(
        "single-container reset hosted acceptance was " +
        "$($result.catalogProjectionSingleContainerDetachHostedAcceptanceMs) ms")
}
$detachWithinHostedAcceptance =
    $detachBudgetValue -le $result.catalogProjectionSingleContainerDetachHostedAcceptanceMs
if ($result.catalogProjectionDetachWithinHostedAcceptance `
    -ne $detachWithinHostedAcceptance) {
    $failures.Add('single-container reset hosted-acceptance classification was inconsistent')
}
if (-not $detachWithinHostedAcceptance) {
    $failures.Add(
        "single-container reset exceeded: $detachBudgetValue ms on $detachBudgetBasis " +
        "(product budget $($result.catalogProjectionSingleContainerDetachBudgetMs) ms; " +
        "hosted acceptance $($result.catalogProjectionSingleContainerDetachHostedAcceptanceMs) ms; " +
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
elseif ($detachBudgetValue -gt $result.catalogProjectionSingleContainerDetachBudgetMs) {
    Write-Warning (
        "Single-container reset exceeded the product target but remained within " +
        "hosted acceptance ($detachBudgetValue ms > " +
        "$($result.catalogProjectionSingleContainerDetachBudgetMs) ms).")
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
    Write-Warning (
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
        -gt $result.catalogProjectionDiagnosticSliceTargetMs) {
    Write-Warning (
        "pre-Reset WPF selection release diagnostic exceeded " +
        "(wall $($result.catalogProjectionMaxPreResetSelectionReleaseMs)/" +
        "$($result.catalogProjectionDiagnosticSliceTargetMs) ms)")
}
if ($result.catalogProjectionPreResetSelectionClearedExact -ne $true `
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
# Keep the product heartbeat target at 50 ms. A hosted exception requires
# positive independent-control consensus for the same heartbeat interval;
# zero CPU or an await callback alone is never attribution.
$heartbeatMeasurementToleranceMs = 25.0
$heartbeatAbsoluteCeilingMs = 2500.0
$heartbeatAttributionToleranceMs = 0.5
$heartbeatAcceptanceLimitMs =
    [double]$result.dispatcherHeartbeatBudgetMs + $heartbeatMeasurementToleranceMs
$heartbeatHostedOutliers = @(
    $dispatcherDiagnostic.overBudgetHeartbeats | Where-Object {
        [double]$_.productGapMs -gt $heartbeatAcceptanceLimitMs
    })
$controlSamplesByHeartbeatTag = @{}
$duplicateControlHeartbeatTags = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($controlSample in @($controlConsensus.heartbeatSamples)) {
    $tagKey = [string][long]$controlSample.heartbeatTag
    if ($controlSamplesByHeartbeatTag.ContainsKey($tagKey)) {
        $null = $duplicateControlHeartbeatTags.Add($tagKey)
        continue
    }
    $controlSamplesByHeartbeatTag[$tagKey] = $controlSample
}
$invalidHeartbeatHostedOutliers = @(
    $heartbeatHostedOutliers | Where-Object {
        $heartbeat = $_
        $tagKey = [string][long]$heartbeat.heartbeatTag
        $control = if ($controlSamplesByHeartbeatTag.ContainsKey($tagKey) `
                -and -not $duplicateControlHeartbeatTags.Contains($tagKey)) {
            $controlSamplesByHeartbeatTag[$tagKey]
        } else {
            $null
        }
        $activeOperations = @($_.activeOperations)
        $panelPhases = @($_.panelPhases)
        $longOperations = @($activeOperations | Where-Object {
            [double]$_.executionWallMs -gt $heartbeatAcceptanceLimitMs
        })
        $longAwaitOperations = @($longOperations | Where-Object {
            [double]$_.executionUiCpuMs -eq 0 `
                -and $_.callbackName -like `
                    'System.Threading.Tasks.SynchronizationContextAwaitTaskContinuation+*'
        })
        $positiveControlConsensus = $null -ne $control `
            -and $control.classification -eq 'CONTROL_CONSENSUS_OVERLAP' `
            -and [Math]::Abs(
                [double]$control.rawGapMs - [double]$heartbeat.rawGapMs) `
                -le $heartbeatAttributionToleranceMs
        $controlOverlapExplainsExcess = $positiveControlConsensus `
            -and [double]$control.controlConsensusOverlapMs `
                + $heartbeatAttributionToleranceMs `
                -ge [double]$heartbeat.productGapMs `
                    - $heartbeatAcceptanceLimitMs
        $_.classification -ne 'ACTIVE_OPERATION_DIAGNOSTIC' `
            -or [double]$_.uiThreadCpuMs -ne 0 `
            -or [double]$_.heartbeatQueueUiCpuMs -ne 0 `
            -or [double]$_.excessQueueUiCpuMs -gt 0 `
            -or [double]$_.heartbeatQueueWallMs -le $heartbeatAcceptanceLimitMs `
            -or [double]$_.panelPhaseOverlapMs -gt 1 `
            -or $activeOperations.Count -eq 0 `
            -or @($activeOperations | Where-Object {
                [double]$_.executionUiCpuMs -ne 0
            }).Count -gt 0 `
            -or @($panelPhases | Where-Object {
                [double]$_.uiCpuMs -ne 0
            }).Count -gt 0 `
            -or $longOperations.Count -eq 0 `
            -or $longAwaitOperations.Count -ne $longOperations.Count `
            -or -not $positiveControlConsensus `
            -or $control.gcTimingAmbiguous -eq $true `
            -or [double]$control.unexplainedGapMs `
                -gt $heartbeatAcceptanceLimitMs `
            -or -not $controlOverlapExplainsExcess `
            -or [double]$heartbeat.rawGapMs -gt $heartbeatAbsoluteCeilingMs `
            -or [double]$heartbeat.productGapMs -gt $heartbeatAbsoluteCeilingMs
    })
$heartbeatHostedPreemptionAccepted =
    $heartbeatHostedOutliers.Count -gt 0 `
    -and $invalidHeartbeatHostedOutliers.Count -eq 0
$heartbeatWithinHostedAcceptance =
    [double]$dispatcherDiagnostic.maxProductGapMs -le $heartbeatAcceptanceLimitMs `
    -or $heartbeatHostedPreemptionAccepted
if ($result.dispatcherHeartbeatBudgetMs -ne 50 `
    -or $result.dispatcherHeartbeatMeasurementToleranceMs -ne $heartbeatMeasurementToleranceMs `
    -or $result.dispatcherHeartbeatHostedPreemptionAbsoluteCeilingMs `
        -ne $heartbeatAbsoluteCeilingMs `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.exact -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.zeroCpuContinuationWithoutControlRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.zeroCpuSynchronousWaitWithoutControlRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.positiveControlConsensusAccepted -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.absoluteCeilingExceededRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.activeCpuRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.panelCpuRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.gcAmbiguityRejected -ne $true `
    -or $result.dispatcherHeartbeatHostedPreemptionPolicySelfTest.inconclusiveRejected -ne $true `
    -or $null -eq $controlConsensus `
    -or $null -eq $dispatcherDiagnostic) {
    $failures.Add('dispatcher heartbeat diagnostic contract was missing')
}
elseif ($result.dispatcherHeartbeatHostedPreemptionAccepted `
    -ne $heartbeatHostedPreemptionAccepted `
    -or $result.dispatcherHeartbeatWithinHostedAcceptance `
        -ne $heartbeatWithinHostedAcceptance) {
    $failures.Add('dispatcher heartbeat hosted-acceptance classification was inconsistent')
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
if ($result.dispatcherHeartbeatProductTargetMet -ne $true) {
    if ($heartbeatHostedPreemptionAccepted) {
        Write-Warning "Product heartbeat target exceeded only during positively attributed host preemption ($($dispatcherDiagnostic.maxProductGapMs) ms > $($result.dispatcherHeartbeatBudgetMs) ms)."
    }
    else {
        Write-Warning "Product heartbeat target exceeded but remained eligible for hosted acceptance ($($dispatcherDiagnostic.maxProductGapMs) ms > $($result.dispatcherHeartbeatBudgetMs) ms)."
    }
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
        -or -not $heartbeatWithinHostedAcceptance `
        -or $dispatcherDiagnostic.inconclusiveCount -gt 0)) {
    $failures.Add(
        "raw dispatcher heartbeat gap was $($result.dispatcherHeartbeatMaxGapMs) ms " +
        "(budget $($result.dispatcherHeartbeatBudgetMs) ms; " +
        "measurement tolerance $heartbeatMeasurementToleranceMs ms; " +
        "product max $($dispatcherDiagnostic.maxProductGapMs) ms; " +
        "strict queue max $($dispatcherDiagnostic.maxStrictSchedulerQueueDelayMs) ms; " +
        "queue/active/inconclusive " +
        "$($dispatcherDiagnostic.schedulerQueueDelayCount)/" +
        "$($dispatcherDiagnostic.activeOperationDiagnosticCount)/" +
        "$($dispatcherDiagnostic.inconclusiveCount))")
}
if ($result.mixedLatestWins -ne $true -or $result.mixedStaleSearchDiscarded -ne $true -or $result.mixedStaleFilterDiscarded -ne $true) {
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
}
finally {
    $env:DOTNET_ROOT = $previousDotNetRoot
    $env:DOTNET_ROOT_X64 = $previousDotNetRootX64
}
