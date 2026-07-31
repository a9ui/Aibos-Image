param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-enhancement-jobs-workspace.json"),
    [string]$DotnetPath = "dotnet",
    [string]$TargetFrameworkOverride = "",
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$queueContractPath = Join-Path $repoRoot "contracts\enhancement-queue-order-v1.json"
$healthContractPath = Join-Path $repoRoot "contracts\enhancement-health-v1.json"
$videoContractPath = Join-Path $repoRoot "contracts\enhancement-video-v1.json"
$videoActivationPath = Join-Path $repoRoot "contracts\enhancement-video-writer-activation-v1.json"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-enhancement-jobs-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    if (-not (Test-Path -LiteralPath $queueContractPath -PathType Leaf)) {
        throw "Enhancement queue ordering contract was not found: $queueContractPath"
    }
    $queueContract = Get-Content -LiteralPath $queueContractPath -Raw | ConvertFrom-Json
    $queueContractChecks = @(
        ($queueContract.schemaVersion -eq 1)
        ($queueContract.contractId -eq "PV-ENHANCE-QUEUE-001")
        ($queueContract.protocol -eq "aibos.enhancement-queue-order/v1")
        ($queueContract.queueOrder.field -eq "queueOrder")
        ($queueContract.queueOrder.type -eq "non-negative integer")
        ($queueContract.workerRules.singleWorker -eq $true)
        ($queueContract.workerRules.runningJobIsNeverPreemptedByReorder -eq $true)
        ($queueContract.workerRules.pumpAfterRunningCancel -eq $true)
        ($queueContract.workerRules.pumpAfterStartupRecovery -eq $true)
        (($queueContract.readerFixture.expectedVisibleOrder -join ",") -eq
            "running,ordered-first,ordered-second,legacy-earlier,invalid-negative,legacy-later")
        ($queueContract.readerFixture.expectedClaimedQueuedJob -eq "ordered-first")
    )
    if ($queueContractChecks -contains $false) {
        throw "Enhancement queue ordering contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $healthContractPath -PathType Leaf)) {
        throw "Enhancement health contract was not found: $healthContractPath"
    }
    $healthContract = Get-Content -LiteralPath $healthContractPath -Raw | ConvertFrom-Json
    $healthContractChecks = @(
        ($healthContract.schemaVersion -eq 1)
        ($healthContract.contractId -eq "PV-ENHANCE-HEALTH-001")
        ($healthContract.protocol -eq "aibos.enhancement-health/v1")
        ($healthContract.route.method -eq "GET")
        ($healthContract.route.path -eq "/api/enhance/health")
        ($healthContract.route.cacheControl -eq "no-store")
        ($healthContract.route.loopbackOnly -eq $true)
        ($healthContract.passiveRead.createsJobs -eq $false)
        ($healthContract.passiveRead.startsWorker -eq $false)
        ($healthContract.passiveRead.wakesQueue -eq $false)
        ($healthContract.passiveRead.claimsJobs -eq $false)
        ($healthContract.passiveRead.retriesJobs -eq $false)
        ($healthContract.passiveRead.pollsComfyUi -eq $false)
        (($healthContract.status -join ",") -eq "healthy,working,needs-attention")
        ($healthContract.workingFixture.expectedDisplay.state -eq "Working")
        ($healthContract.workingFixture.expectedDisplay.detail -eq "1 running / 4 queued")
        ($healthContract.workingFixture.expectedDisplay.sourceRevisionPrefix -eq "69684954")
    )
    if ($healthContractChecks -contains $false) {
        throw "Enhancement health contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $videoContractPath -PathType Leaf)) {
        throw "Enhancement video contract was not found: $videoContractPath"
    }
    $videoContract = Get-Content -LiteralPath $videoContractPath -Raw | ConvertFrom-Json
    $videoContractChecks = @(
        ($videoContract.schemaVersion -eq 1)
        ($videoContract.contractId -eq "PV-ENHANCE-VIDEO-001")
        ($videoContract.protocol -eq "aibos.enhancement-video/v1")
        ($videoContract.jobStoreVersion -eq 1)
        (($videoContract.operationEnvelope.acceptedValues -join ",") -eq
            "upscale,photoreal,video")
        ($videoContract.operationEnvelope.legacyMissingOperation -eq "upscale")
        ($videoContract.operationEnvelope.storeVersionChange -eq $false)
        ($videoContract.normalV1.presetId -eq "wan22-ti2v-5b-normal-v1")
        ($videoContract.normalV1.backendId -eq "wan22-ti2v-5b-core-v1")
        ($videoContract.normalV1.nominalDurationSeconds -eq 6)
        ($videoContract.normalV1.playbackFps -eq 16)
        ($videoContract.normalV1.frameCount -eq 97)
        ($videoContract.normalV1.maximumPixelArea -eq 409600)
        ($videoContract.normalV1.alignment -eq 32)
        ($videoContract.normalV1.steps -eq 20)
        ($videoContract.normalV1.cfg -eq 5)
        ($videoContract.normalV1.sampler -eq "uni_pc")
        ($videoContract.normalV1.scheduler -eq "simple")
        ($videoContract.normalV1.shift -eq 8)
        ($videoContract.normalV1.seedRange.maximum -eq 2147483647)
        ($videoContract.normalV1.seedRange.fixedAtEnqueue -eq $true)
        ($videoContract.qualitySelectionV1.requestField -eq "presetId")
        ($videoContract.qualitySelectionV1.defaultPresetId -eq
            "wan22-ti2v-5b-normal-v1")
        (($videoContract.qualitySelectionV1.supportedPresetIds -join ",") -eq
            "wan22-ti2v-5b-normal-v1,wan22-ti2v-5b-high-v1")
        ($videoContract.highV1.presetId -eq "wan22-ti2v-5b-high-v1")
        ($videoContract.highV1.backendId -eq "wan22-ti2v-5b-core-v1")
        ($videoContract.highV1.model -eq
            "wan2.2_ti2v_5B_fp16.safetensors")
        ($videoContract.highV1.steps -eq 40)
        ($videoContract.highV1.maximumPixelArea -eq 409600)
        ($videoContract.highV1.singleWorker -eq $true)
        ($videoContract.highV1.parallelInference -eq $false)
        ($videoContract.highV1.deliveryProfile -eq "deliveryV1")
        ($videoContract.deliveryV1.field -eq "video.delivery")
        ($videoContract.deliveryV1.additiveWithinProtocol -eq $true)
        ($videoContract.deliveryV1.backendId -eq
            "vs-rife-5.7.0-rife-4.25-v1")
        ($videoContract.deliveryV1.model -eq "4.25")
        ($videoContract.deliveryV1.targetFps -eq 30)
        ($videoContract.deliveryV1.frameCountByDuration.'4' -eq 120)
        ($videoContract.deliveryV1.frameCountByDuration.'6' -eq 180)
        ($videoContract.deliveryV1.pixelFormat -eq "yuv420p")
        ($videoContract.deliveryV1.audio -eq $false)
        ($videoContract.sourceSelectionV1.requestField -eq
            "sourceProducerJobId")
        ($videoContract.sourceSelectionV1.absent -eq
            "use sourceId as the indexed Original source")
        ($videoContract.sourceSelectionV1.present -eq
            "the server resolves the id to one succeeded photoreal job; clients never provide its managed output path")
        ($videoContract.sourceSelectionV1.producerRequirements.Count -eq 5)
        ($videoContract.sourceSelectionV1.persistedIdentity.Contains(
            "sourceId remains the Original catalog identity"))
        ($videoContract.sourceSelectionV1.retry.Contains(
            "preserve sourceProducerJobId"))
        ($videoContract.sourceSelectionV1.deletion.Contains(
            "queued or running video job"))
        ($videoContract.sourceSelectionV1.wpf.Contains(
            "currently displayed photoreal version"))
        ($videoContract.mutationSafetyVectors[0].id -eq
            "legacy-delivery-absent-6s")
        ($videoContract.mutationSafetyVectors[0].expectedMutationSafe -eq
            $true)
        ($videoContract.mutationSafetyVectors[0].expectedPlaybackFps -eq 16)
        ($videoContract.mutationSafetyVectors[0].expectedPlaybackFrameCount -eq
            97)
        ($videoContract.mutationSafetyVectors[1].delivery.frameCount -eq 120)
        ($videoContract.mutationSafetyVectors[1].expectedMutationSafe -eq
            $true)
        ($videoContract.mutationSafetyVectors[2].delivery.frameCount -eq 180)
        ($videoContract.mutationSafetyVectors[2].presetId -eq
            "wan22-ti2v-5b-normal-v1")
        ($videoContract.mutationSafetyVectors[2].steps -eq 20)
        ($videoContract.mutationSafetyVectors[2].expectedMutationSafe -eq
            $true)
        ($videoContract.mutationSafetyVectors[3].id -eq
            "current-delivery-high-6s")
        ($videoContract.mutationSafetyVectors[3].presetId -eq
            "wan22-ti2v-5b-high-v1")
        ($videoContract.mutationSafetyVectors[3].steps -eq 40)
        ($videoContract.mutationSafetyVectors[3].delivery.frameCount -eq 180)
        ($videoContract.mutationSafetyVectors[3].expectedMutationSafe -eq
            $true)
        ($videoContract.mutationSafetyVectors[4].id -eq
            "high-preset-with-normal-steps")
        ($videoContract.mutationSafetyVectors[4].presetId -eq
            "wan22-ti2v-5b-high-v1")
        ($videoContract.mutationSafetyVectors[4].steps -eq 20)
        ($videoContract.mutationSafetyVectors[4].expectedMutationSafe -eq
            $false)
        ($videoContract.mutationSafetyVectors[5].delivery.frameCount -eq 179)
        ($videoContract.mutationSafetyVectors[5].expectedMutationSafe -eq
            $false)
        (($videoContract.normalization.resolution -join " ").Contains(
            "relativeAspectError + 0.25 * unusedAreaRatio"))
        ($videoContract.normalization.examples.'bird-975x1614' -eq
            "480x800")
        ($videoContract.normalization.refinementFixture.sourceWidth -eq 975)
        ($videoContract.normalization.refinementFixture.sourceHeight -eq 1614)
        ($videoContract.normalization.refinementFixture.maximumPixelArea -eq
            409600)
        ($videoContract.normalization.refinementFixture.expectedWidth -eq 480)
        ($videoContract.normalization.refinementFixture.expectedHeight -eq
            800)
        ($videoContract.managedOutput.folder -eq "Videos")
        ($videoContract.managedOutput.flat -eq $true)
        ($videoContract.readerFirst.wpfWriterEnabled -eq $false)
        ($videoContract.readerFirst.h25WriterEnabled -eq $false)
        ($videoContract.readerFixture.expectedOperations.'legacy-missing-operation' -eq "upscale")
        ($videoContract.readerFixture.expectedOperations.'explicit-video' -eq "video")
        ($videoContract.readerFixture.expectedOperations.'explicit-video-delivery' -eq "video")
        ($videoContract.readerFixture.expectedOperations.'malformed-video-delivery' -eq "video")
        ($videoContract.readerFixture.expectedOperations.'explicit-video-high-delivery' -eq "video")
        ($videoContract.readerFixture.expectedOperations.'future-operation' -eq "unsupported")
        ($videoContract.readerFixture.expectedOperations.'null-operation' -eq "unsupported")
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video'.fps -eq 16)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video'.frameCount -eq 97)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video-delivery'.fps -eq 30)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video-delivery'.frameCount -eq 180)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video-high-delivery'.fps -eq 30)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'explicit-video-high-delivery'.frameCount -eq 180)
        ($videoContract.readerFixture.expectedPlaybackMetadata.'malformed-video-delivery' -eq "protected")
        (($videoContract.readerFixture.expectedImageVersionIds -join ",") -eq
            "legacy-missing-operation,explicit-upscale,explicit-photoreal")
        (($videoContract.readerFixture.expectedReaderOnlyIds -join ",") -eq
            "explicit-video,explicit-video-delivery,malformed-video-delivery,explicit-video-high-delivery,future-operation,null-operation")
        (($videoContract.readerFixture.expectedMalformedDeliveryIds -join ",") -eq
            "malformed-video-delivery")
        ($videoContract.readerFixture.expectedMutationRequests -eq 0)
    )
    if ($videoContractChecks -contains $false) {
        throw "Enhancement video contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $videoActivationPath -PathType Leaf)) {
        throw "Enhancement video writer activation record was not found: $videoActivationPath"
    }
    $videoActivation = Get-Content -LiteralPath $videoActivationPath -Raw | ConvertFrom-Json
    $videoActivationChecks = @(
        ($videoActivation.schemaVersion -eq 1)
        ($videoActivation.recordId -eq "PV-ENHANCE-VIDEO-WRITER-ACTIVATION-001")
        ($videoActivation.protocolContract -eq "PV-ENHANCE-VIDEO-001")
        ($videoActivation.codeReady.wpfMutationClient -eq $true)
        ($videoActivation.codeReady.h25Wan22Writer -eq $true)
        ($videoActivation.codeReady.h25Commit -eq
            "1bd9673c6f92ba448b99552ed5bb230294bbafad")
        ($videoActivation.deliveryExtension.issue -eq
            "a9ui/tools-h000025-photoviewer#356")
        ($videoActivation.deliveryExtension.backendId -eq
            "vs-rife-5.7.0-rife-4.25-v1")
        ($videoActivation.deliveryExtension.model -eq "4.25")
        ($videoActivation.deliveryExtension.targetFps -eq 30)
        ($videoActivation.deliveryExtension.durationFrameCounts.'4' -eq 120)
        ($videoActivation.deliveryExtension.durationFrameCounts.'6' -eq 180)
        ($videoActivation.deliveryExtension.pixelFormat -eq "yuv420p")
        ($videoActivation.deliveryExtension.audio -eq $false)
        ([string]$videoActivation.deliveryExtension.h25CandidateCommit -ceq
            'e10cc052572da8e5b6e0cb9da06b928a44deb3e7')
        ($videoActivation.liveRuntime.wpfMutationClientEnabled -eq $false)
        ($videoActivation.liveRuntime.h25WriterEnabled -eq $false)
        ($videoActivation.liveRuntime.productionProcessesRestarted -eq $false)
        ($videoActivation.candidateEvidence.durationSeconds -eq 6)
        ($videoActivation.candidateEvidence.playbackFps -eq 16)
        ($videoActivation.candidateEvidence.frameCount -eq 97)
        ($videoActivation.candidateEvidence.width -eq 832)
        ($videoActivation.candidateEvidence.height -eq 480)
        ($videoActivation.candidateEvidence.maximumPixelArea -eq 409600)
        ($videoActivation.candidateEvidence.codec -eq "h264")
        ($videoActivation.candidateEvidence.pixelFormat -eq "yuv420p")
        ($videoActivation.candidateEvidence.delivery.elapsedMilliseconds -eq
            11768)
        ($videoActivation.candidateEvidence.delivery.targetFps -eq 30)
        ($videoActivation.candidateEvidence.delivery.frameCount -eq 180)
        ($videoActivation.candidateEvidence.delivery.pixelFormat -eq
            "yuv420p")
        ($videoActivation.candidateEvidence.delivery.audio -eq $false)
        ($videoActivation.portraitTimingUpperEvidence.width -eq 480)
        ($videoActivation.portraitTimingUpperEvidence.height -eq 832)
        ($videoActivation.portraitTimingUpperEvidence.wanElapsedMilliseconds -eq
            202942)
        ($videoActivation.portraitTimingUpperEvidence.deliveryElapsedMilliseconds -eq
            15318)
        ($videoActivation.portraitTimingUpperEvidence.totalElapsedMilliseconds -eq
            218810)
        ($videoActivation.refinedPortraitCandidateEvidence.width -eq 480)
        ($videoActivation.refinedPortraitCandidateEvidence.height -eq 800)
        ($videoActivation.refinedPortraitCandidateEvidence.totalElapsedMilliseconds -eq
            177458)
        ($videoActivation.refinedPortraitCandidateEvidence.peakVramMiB -eq
            11765)
        ($videoActivation.refinedPortraitCandidateEvidence.qualityAdjudication -eq
            "adopted for anime M1 smoke")
        ($videoActivation.candidateEvidence.mediaFoundationPlayback -eq $true)
        ($videoActivation.candidateEvidence.mediaFoundationInCi -eq $false)
        ($videoActivation.cutover.requiresControlledRestart -eq $true)
        ($videoActivation.cutover.keepSingleWorker -eq $true)
        ($videoActivation.cutover.keepInferenceSerial -eq $true)
        ($videoActivation.cutover.forceHighVram -eq $false)
    )
    if ($videoActivationChecks -contains $false) {
        throw "Enhancement video writer activation fields are invalid."
    }

    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    }
    else {
        & $DotnetPath msbuild $project -restore "-property:TargetFramework=$TargetFrameworkOverride" "-property:OutputPath=$buildOutput" "-property:Configuration=$Configuration" -nologo -verbosity:minimal
    }
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--enhancement-jobs-workspace-smoke', ('"{0}"' -f $fullOutputPath)) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Enhancement jobs workspace smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "Enhancement jobs workspace smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 10
    $required = @(
        'passiveOpen',
        'healthVisible',
        'healthProvenance',
        'healthPassive',
        'legacyHealthFallback',
        'futureHealthFallback',
        'unknownIssueSafe',
        'healthRecovered',
        'routesOk',
        'outputOpened',
        'sourceOpenedInViewer',
        'queueInventoryOrdered',
        'operationLabelsVisible',
        'videoActionsEnabled',
        'legacyVideoMutationSafe',
        'deliveryVideoMutationSafe',
        'readerOnlyVideoSafe',
        'malformedProvenanceVideoSafe',
        'videoCancelPendingSafe',
        'videoCancelSettled',
        'videoOutputOpened',
        'videoOutputOpenedPaused',
        'videoDeleteIssued',
        'videoOutputDeleted',
        'videoSiblingPreserved',
        'unknownOperationSafe',
        'legacyMissingOperation',
        'unsupportedNoMutation',
        'imageVersionsExcludeVideo',
        'stableJobViews',
        'failedCancelIssued',
        'moveNextIssued',
        'canceledRetryIssued',
        'rerunSettingsContract',
        'clearQueuedIssued',
        'jobsRestoredAfterViewerClose',
        'sourceUnchanged',
        'storesUnchanged',
        'outputDeleted'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Enhancement jobs workspace contract failed (exit $processExitCode): $($missing -join ', ')"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        for ($cleanupAttempt = 1; $cleanupAttempt -le 10; $cleanupAttempt++) {
            try {
                Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
                break
            }
            catch {
                if ($cleanupAttempt -eq 10) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
