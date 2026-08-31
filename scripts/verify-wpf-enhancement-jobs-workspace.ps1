param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-enhancement-jobs-workspace.json"),
    [string]$DotnetPath = "",
    [string]$TargetFrameworkOverride = "",
    [switch]$NoRestore,
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\ContractBundles.ps1')
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        'dotnet.exe'
    }
}
$DotnetPath = (Get-Command $DotnetPath -ErrorAction Stop).Source
$installedSdks = @(& $DotnetPath --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match '^10\.' })) {
    throw "The focused Jobs verifier requires a .NET 10 SDK host: $DotnetPath"
}
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$mainWindowXamlPath = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\MainWindow.xaml"
$jobsSourcePath = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs"
$queueContractPath = Join-Path $repoRoot "contracts\enhancement-queue-order-v1.json"
$healthContractPath = Join-Path $repoRoot "contracts\enhancement-health-v1.json"
$healthFixturePath = Join-Path $repoRoot "contracts\fixtures\enhancement-health-working-v1.json"
$jobsSqliteContractPath = Join-Path $repoRoot "contracts\enhancement-jobs-sqlite-v1.json"
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
    $mainWindowXaml = Get-Content -LiteralPath $mainWindowXamlPath -Raw
    $jobsSource = Get-Content -LiteralPath $jobsSourcePath -Raw
    $jobsPresentationChecks = @(
        $mainWindowXaml.Contains('<TextBlock Text="Jobs" Foreground="{StaticResource TextPrimary}"')
        $mainWindowXaml.Contains('<TextBlock Text="FILTERS" Foreground="{StaticResource TextTertiary}"')
        $mainWindowXaml.Contains('<TextBlock Text="LOCAL AI" Foreground="{StaticResource TextSecondary}"')
        $mainWindowXaml.Contains('<TextBlock Text="Prompt / Settings" Foreground="{StaticResource TextSecondary}"')
        $mainWindowXaml.Contains('Content="待機中を現在設定へ"')
        $mainWindowXaml.Contains('Visibility="{Binding ShowProgressBar, Converter={StaticResource BoolToVis}}"')
        $mainWindowXaml.Contains('Visibility="{Binding ShowDetailText, Converter={StaticResource BoolToVis}}"')
        (-not $mainWindowXaml.Contains('AI処理版は元画像とは別に保存しています。'))
        (-not $mainWindowXaml.Contains('AI処理結果は別ファイルに保存します。'))
        $jobsSource.Contains('$"待機中 · {QueuePosition} / {QueueCount}"')
        $jobsSource.Contains('$"キュー順で表示中 · {counts.Total:N0}件')
        $jobsSource.Contains('先頭の実写化はエンジン準備待ちです。後続の動画化は開始しません。')
        $jobsSource.Contains('"設定を更新"')
        $jobsSource.Contains('queuedKreaPhotorealSettingsUpdateV1')
        (-not $jobsSource.Contains('実行順で表示中'))
        $jobsSource.Contains('public bool ShowProgressBar => Status == "running";')
        $jobsSource.Contains('RequestDetailsExpanded ? "閉じる" : "Prompt";')
        (-not $jobsSource.Contains('反映中  ·  '))
    )
    if ($jobsPresentationChecks -contains $false) {
        throw "Jobs presentation hierarchy checks failed."
    }
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
        ($queueContract.pauseControl.field -eq "queuePaused")
        ($queueContract.pauseControl.missingValue -eq $false)
        ($queueContract.workerRules.pauseAndClaimShareOneLock -eq $true)
        ($queueContract.workerRules.currentJobFinishesWhenPaused -eq $true)
        ($queueContract.workerRules.resumePreservesFifo -eq $true)
        ($queueContract.queuedPhotorealPromptUpdate.route -eq
            "POST /api/enhance/jobs/:id/prompts")
        ($queueContract.queuedPhotorealPromptUpdate.appliesTo -eq
            "queued photoreal jobs using comfyui-flux2-photoreal only")
        ($queueContract.queuedPhotorealPromptUpdate.sharesClaimLock -eq $true)
        ($queueContract.queuedPhotorealPromptUpdate.wakesWorker -eq $false)
        (($queueContract.queuedPhotorealSettingsUpdateV1.adapterIds -join ",") -eq
            "comfyui-flux2-photoreal,comfyui-krea2-anything2real-v3-photoreal,comfyui-krea2-anime-to-real-edit-v1-photoreal")
        ($queueContract.queuedPhotorealSettingsUpdateV1.readerCapabilityGate -like
            "*queuedKreaPhotorealSettingsUpdateV1 exactly true*")
        ($queueContract.queuedPhotorealSettingsUpdateV1.preserves -contains
            "adapter identity")
        ($queueContract.queuedPhotorealSettingsUpdateV1.adapterSemantics.'comfyui-krea2-anything2real-v3-photoreal' -like
            "*strength 1 and kvCache true*")
        ($queueContract.queuedPhotorealSettingsUpdateV1.adapterSemantics.'comfyui-krea2-anime-to-real-edit-v1-photoreal' -like
            "*denoise 100, steps 8, and cfgScale 1*")
        ($queueContract.queuedPhotorealSettingsUpdateV1.invalidAdapterSettings -like
            "*return conflict and write nothing*")
        ($queueContract.workerRules.pumpAfterRunningCancel -eq $true)
        ($queueContract.workerRules.pumpAfterStartupRecovery -eq $true)
        ($queueContract.strictVisibleGlobalFifoV1.authority -like
            "*single visible global order*")
        ($queueContract.strictVisibleGlobalFifoV1.claim -like
            "*without operation-family alternation*")
        (-not ($queueContract.PSObject.Properties.Name -contains
            "fairGpuFamilyDispatchV1"))
        (($queueContract.kreaPhotorealStrictFifoBarrierV1.adapterIds -join ",") -eq
            "comfyui-krea2-anything2real-v3-photoreal,comfyui-krea2-anime-to-real-edit-v1-photoreal")
        ($queueContract.kreaPhotorealStrictFifoBarrierV1.unavailableQueueHead -like
            "*claim no later job including video*")
        (($queueContract.deferredBackendSkipV1.excludedStrictFifoBarrierAdapters -join ",") -eq
            "comfyui-krea2-anything2real-v3-photoreal,comfyui-krea2-anime-to-real-edit-v1-photoreal")
        ($queueContract.deferredBackendSkipV1.recognizedOptionalAdapterIds.Count -eq 0)
        ($queueContract.deferredBackendSkipV1.currentClassification -like
            "*classifies no adapter as deferrable*")
        ($queueContract.deferredBackendSkipV1.appliesTo -like
            "*recognized optional non-Krea*")
        ($queueContract.kreaPhotorealStrictFifoBarrierV1.healthIssue -like
            "*does not by itself report queued-without-pump*")
        ($queueContract.workerRules.claim -like
            "*claim no later job when that row is an unavailable recognized Krea*")
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
    $healthFixture = Get-Content -LiteralPath $healthFixturePath -Raw | ConvertFrom-Json
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
        ($healthFixture.forContractId -eq $healthContract.contractId)
        ($healthFixture.workingFixture.expectedDisplay.state -eq "Working")
        ($healthFixture.workingFixture.expectedDisplay.detail -eq "1 running / 4 queued")
        ($healthFixture.workingFixture.expectedDisplay.sourceRevisionPrefix -eq "69684954")
        ($healthFixture.workingFixture.payload.worker.paused -eq $false)
        (-not ($healthContract.requiredFields -contains
            "backendAvailability.kreaPhotorealV1.queueHeadBlocked"))
        ($healthFixture.workingFixture.payload.backendAvailability.kreaPhotorealV1.queueHeadBlocked -eq
            $false)
        ($healthFixture.workingFixture.payload.capabilities.deferredBackendSkipV1 -eq
            $false)
        (-not ($healthContract.readerRules.PSObject.Properties.Name -contains
            "fairGpuFamilyDispatchV1"))
        (-not ($healthFixture.workingFixture.payload.capabilities.PSObject.Properties.Name -contains
            "fairGpuFamilyDispatchV1"))
        ($healthContract.readerRules.deferredBackendSkipV1 -like
            "*optional non-Krea*")
        ($healthContract.readerRules.kreaPhotorealQueueHeadBlockedV1 -like
            "*true only when the first queued row*")
        ($healthContract.readerRules.kreaPhotorealQueueHeadBlockedPumpV1 -like
            "*only when its cached visible queue head is an exact recognized Krea*")
        ($healthContract.readerRules.kreaPhotorealQueueHeadBlockedDisplayV1 -like
            "*loopback trust-boundary issue, takes display priority*")
        ($healthContract.readerRules.invalidKreaPhotorealQueueHeadBlockedV1 -like
            "*without making health, Jobs, or unrelated capabilities unavailable*")
        ($healthFixture.workingFixture.payload.capabilities.queuedPhotorealPromptUpdate -eq $true)
        ($healthFixture.workingFixture.payload.capabilities.queuedKreaPhotorealSettingsUpdateV1 -eq $true)
        ($healthContract.readerRules.missingQueuedKreaPhotorealSettingsUpdateV1 -like
            "*compatible FLUX current-settings action readable*")
        ($healthContract.readerRules.inventoryRevision -like
            "*progress or heartbeat may advance it without a full Jobs reload*")
        ($healthContract.readerRules.inventoryRevision -like
            "*throttled passive fallback*")
        ($healthContract.readerRules.catalogRevision -like
            "*authoritative for WPF-visible terminal, new-row*")
        ($healthContract.readerRules.catalogRevision -like
            "*invalidates that snapshot by the next poll*")
        ($healthContract.readerRules.missingQueuedPhotorealPromptUpdate -eq
            "keep health and jobs readable but hide the queued prompt-update action")
        ($healthContract.readerRules.missingTerminalHistoryBatchDismissV1 -eq
            "keep health and jobs readable and fall back to protected per-job terminal history dismissal")
        ($healthFixture.workingFixture.payload.capabilities.terminalHistoryBatchDismissV1 -eq $true)
        ($healthContract.readerRules.missingQueuedJobsBatchCancelV1 -eq
            "keep health and jobs readable and fall back to protected per-job queued cancellation")
        ($healthContract.readerRules.missingTerminalHistoryTargetsV1 -eq
            "keep health and jobs readable and never redefine all as only the loaded history window")
        ($healthContract.readerRules.terminalHistoryTargetsV1Dependency -like
            "*terminalHistoryBatchDismissV1*")
        ($healthFixture.workingFixture.payload.capabilities.queuedJobsBatchCancelV1 -eq $true)
        ($healthFixture.workingFixture.payload.capabilities.terminalHistoryTargetsV1 -eq $true)
    )
    if ($healthContractChecks -contains $false) {
        throw "Enhancement health contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $jobsSqliteContractPath -PathType Leaf)) {
        throw "Enhancement Jobs SQLite contract was not found: $jobsSqliteContractPath"
    }
    $jobsSqliteContract = Get-Content -LiteralPath $jobsSqliteContractPath -Raw |
        ConvertFrom-Json
    $jobsSqliteContractChecks = @(
        ($jobsSqliteContract.schemaVersion -eq 1)
        ($jobsSqliteContract.contractId -eq "PV-ENHANCE-JOBS-SQLITE-001")
        ($jobsSqliteContract.protocol -eq "aibos.enhancement-jobs-sqlite/v1")
        ($jobsSqliteContract.jobsWorkspaceSurface.historyWindow.default -eq 500)
        (($jobsSqliteContract.jobsWorkspaceSurface.historyWindow.allowed -join ",") -eq
            "100,500,1000")
        ($null -ne $jobsSqliteContract.jobsWorkspaceSurface.enhancement_jobs.requiredColumns.updated_at)
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.activeRows.Contains(
            "queued rows retain lifecycle value zero"))
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.activeRows.Contains(
            "no progress bar"))
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.activeRows.Contains(
            "running rows alone render a determinate value"))
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.durableFieldMeaning.Contains(
            "completed adapter execution stages"))
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.terminalRows.Contains(
            "retain lifecycle value 100"))
        ($jobsSqliteContract.jobsWorkspaceSurface.progressPresentation.terminalRows.Contains(
            "no progress bar"))
        ($jobsSqliteContract.jobsWorkspaceSurface.bulkScope.Contains(
            "display limits never redefine"))
    )
    if ($jobsSqliteContractChecks -contains $false) {
        throw "Enhancement Jobs SQLite history-window contract fields are invalid."
    }
    $videoContract = Get-AibosVideoV1Bundle $repoRoot
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
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        $buildArguments = @(
            'build',
            $project,
            '-c',
            $Configuration,
            "-p:OutputPath=$buildOutput",
            '-p:UseSharedCompilation=false',
            '--nologo',
            '--disable-build-servers',
            '-v:minimal'
        )
        if ($NoRestore) { $buildArguments += '--no-restore' }
        & $DotnetPath @buildArguments
    }
    else {
        $msbuildArguments = @(
            'msbuild',
            $project,
            "-property:TargetFramework=$TargetFrameworkOverride",
            "-property:OutputPath=$buildOutput",
            "-property:Configuration=$Configuration",
            '-property:UseSharedCompilation=false',
            '-nodeReuse:false',
            '-nologo',
            '-verbosity:minimal'
        )
        if (-not $NoRestore) { $msbuildArguments += '-restore' }
        & $DotnetPath @msbuildArguments
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
    [pscustomobject]@{
        ok = $result.ok
        passiveOpen = $result.passiveOpen
        kreaQueueHeadBlockedVisible = $result.kreaQueueHeadBlockedVisible
        intentionalKreaQueueWaitNotRecovery = $result.intentionalKreaQueueWaitNotRecovery
        kreaQueueHeadDoesNotHideTrustIssue = $result.kreaQueueHeadDoesNotHideTrustIssue
        mismatchedKreaQueueHeadDoesNotSuppressRecovery = $result.mismatchedKreaQueueHeadDoesNotSuppressRecovery
        malformedKreaQueueHeadBlockedCompatible = $result.malformedKreaQueueHeadBlockedCompatible
        missingKreaQueueHeadBlockedCompatible = $result.missingKreaQueueHeadBlockedCompatible
        duplicateKreaQueueHeadBlockedCompatible = $result.duplicateKreaQueueHeadBlockedCompatible
        progressOnlyInventoryRevisionAvoidedFullRead = $result.progressOnlyInventoryRevisionAvoidedFullRead
        legacyInventoryFallbackThrottled = $result.legacyInventoryFallbackThrottled
        catalogRevisionTerminalHandoffRefreshBounded = $result.catalogRevisionTerminalHandoffRefreshBounded
        kreaCurrentSettingsMutationContract = $result.kreaCurrentSettingsMutationContract
        stableJobViews = $result.stableJobViews
        completedElapsedVisible = $result.completedElapsedVisible
        queueInventoryOrdered = $result.queueInventoryOrdered
        sourceUnchanged = $result.sourceUnchanged
        storesUnchanged = $result.storesUnchanged
    } | ConvertTo-Json -Depth 3
    $required = @(
        'passiveOpen',
        'activeProgressIsTruthful',
        'historyWindowReaderContract',
        'healthVisible',
        'healthProvenance',
        'healthPassive',
        'duplicateJobsSnapshotRejected',
        'malformedJobsSnapshotsRejected',
        'invalidJobsSnapshotRecovery',
        'legacyPromptUpdateCapabilitySafe',
        'legacyPauseCapabilitySafe',
        'legacyHealthFallback',
        'futureHealthFallback',
        'unknownIssueSafe',
        'kreaQueueHeadBlockedVisible',
        'kreaQueueHeadBlockedPassive',
        'intentionalKreaQueueWaitNotRecovery',
        'kreaQueueHeadDoesNotHideTrustIssue',
        'mismatchedKreaQueueHeadDoesNotSuppressRecovery',
        'malformedKreaQueueHeadBlockedCompatible',
        'missingKreaQueueHeadBlockedCompatible',
        'duplicateKreaQueueHeadBlockedCompatible',
        'healthRecovered',
        'queuePauseContract',
        'routesOk',
        'outputOpened',
        'sourceOpenedInViewer',
        'queueInventoryOrdered',
        'jobsFilterLayoutContract',
        'jobsScrollTopControl',
        'thumbnailViewportLoadBounded',
        'operationLabelsVisible',
        'videoActionsEnabled',
        'currentSourceIndependentQueueReorder',
        'reorderLabelsVisible',
        'savedPromptDetailsContract',
        'photorealPromptDetailsContract',
        'legacyVideoMutationSafe',
        'deliveryVideoMutationSafe',
        'activeVideoOutputDependencyContract',
        'readerOnlyVideoSafe',
        'malformedProvenanceVideoSafe',
        'videoCancelPendingSafe',
        'videoCancelSettled',
        'videoOutputRevealed',
        'videoThumbnailOpened',
        'videoThumbnailAutoplay',
        'videoDeleteIssued',
        'videoOutputDeleted',
        'videoSiblingPreserved',
        'unknownOperationSafe',
        'legacyMissingOperation',
        'legacyPhotorealPromptUpdateSafe',
        'photorealTerminalCurrentSettingsActions',
        'completedElapsedVisible',
        'unsupportedNoMutation',
        'imageVersionsExcludeVideo',
        'stableJobViews',
        'failedCancelIssued',
        'moveNextIssued',
        'moveReflectedImmediately',
        'moveAvoidedFullInventoryReload',
        'queuedOrderBatchContract',
        'healthOnlyPollAvoidedFullInventory',
        'queueOrderRevisionRefreshesSameCountInventory',
        'catalogRevisionRefreshesSameCountInventory',
        'progressOnlyInventoryRevisionAvoidedFullRead',
        'legacyInventoryFallbackThrottled',
        'catalogRevisionTerminalHandoffRefreshBounded',
        'mutationRefreshDebtContract',
        'cachedReopenAvoidedFullInventory',
        'staleQueueRefreshSuppressed',
        'healthAfterInventoryReorderSuppressed',
        'canceledRetryIssued',
        'rerunSettingsContract',
        'rerunSeedContract',
        'queuedPromptUpdateContract',
        'bulkQueuedPromptUpdateContract',
        'legacyKreaSettingsCapabilitySafe',
        'malformedKreaSettingsCapabilitySafe',
        'kreaAnythingCurrentSettingsActionsVisible',
        'kreaAnimeCurrentSettingsActionsVisible',
        'kreaCurrentSettingsAdapterMatchFailClosed',
        'kreaCurrentSettingsMutationContract',
        'clearQueuedIssued',
        'queuedJobsBatchCancelContract',
        'terminalHistoryBatchRetryContract',
        'terminalHistoryTargetPlanContract',
        'jobsRestoredAfterViewerClose',
        'jobsViewportRestoredAfterViewerClose',
        'jobsThumbMinimumVisible',
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
