[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$indexPath = Join-Path $repoRoot 'contracts\index.json'
$v1Path = Join-Path $repoRoot 'contracts\enhancement-video-tools-v1.json'
$v2Path = Join-Path $repoRoot 'contracts\enhancement-video-tools-v2.json'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-tools-v2-reader-v1.json'

function Read-ExactJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Video Tools contract artifact was not found: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-LowerSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ExactSet {
    param(
        [Parameter(Mandatory = $true)][object[]]$Actual,
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $actualStrings = @($Actual | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedStrings = @($Expected | ForEach-Object { [string]$_ } | Sort-Object)
    if (($actualStrings -join "`n") -cne ($expectedStrings -join "`n")) {
        throw "$Name is not exact. actual=$($actualStrings -join ',') expected=$($expectedStrings -join ',')"
    }
}

$v1Sha256 = Get-LowerSha256 $v1Path
if ($v1Sha256 -cne '484b2ab0160bc420bc6e1f29b52e8780ec9260f48023bd7a9e33945bacb53207') {
    throw "Video Tools v1 changed; it must remain the exact reader-only legacy contract. sha256=$v1Sha256"
}
$v1 = Read-ExactJson $v1Path
if ($v1.contractId -cne 'PV-ENHANCE-VIDEO-TOOLS-001' -or
    $v1.protocol -cne 'aibos-enhancement-video-tools-v1' -or
    $v1.request.retake.kind -cne 'retake' -or
    $v1.pairedRollout.productionWriterEnabled -ne $false) {
    throw 'Video Tools v1 legacy identity, Retake discriminator, or closed writer changed.'
}

$v2Raw = Get-Content -LiteralPath $v2Path -Raw -Encoding UTF8 -ErrorAction Stop
if ($v2Raw -match '(?i)firstAnchor|lastAnchor|two[- ]anchor') {
    throw 'Video Tools v2 must not contain an anchor-based Retake contract.'
}
$v2 = $v2Raw | ConvertFrom-Json
$fixtureRaw = Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 -ErrorAction Stop
$fixture = $fixtureRaw | ConvertFrom-Json
$index = Read-ExactJson $indexPath
$v2Sha256 = Get-LowerSha256 $v2Path
$fixtureSha256 = Get-LowerSha256 $fixturePath
if ($fixtureRaw -notmatch '"queueOrder"\s*:\s*2\.0000000000000001') {
    throw 'The Video Tools v2 lossy-number reader vector is missing its exact raw token.'
}

if ($v2.schemaVersion -ne 2 -or
    $v2.contractId -cne 'PV-ENHANCE-VIDEO-TOOLS-002' -or
    $v2.protocol -cne 'aibos-enhancement-video-tools-v2' -or
    $v2.operation -cne 'video' -or
    $v2.mediaKind -cne 'video' -or
    $v2.pairedRollout.productionWriterEnabled -ne $false -or
    $v2.legacyV1.productionWriterEnabled -ne $false -or
    $v2.legacyV1.writerKind -cne 'retake' -or
    $v2.legacyV1.v2ReinterpretationAllowed -ne $false) {
    throw 'Video Tools v2 identity, closed writer, or v1 compatibility rule is incomplete.'
}

Assert-ExactSet @($v2.request.kinds) @('edit', 'finish') 'request.kinds'
$durableReader = $v2.lifecycle.durableReader
Assert-ExactSet @($durableReader.supportedStates) @(
    'queued', 'running', 'succeeded', 'failed', 'canceled', 'deleted'
) 'durable reader states'
Assert-ExactSet @($durableReader.sourceEnvelope.exactRequiredFields) @(
    'sourcePath', 'sourceSignature', 'sourceSha256'
) 'durable reader source envelope fields'
Assert-ExactSet @($durableReader.sourceEnvelope.forbiddenConflicts) @(
    'sourceProducerJobId', 'sourceManagedOutputPath',
    'sourceRecoveredAdapterId', 'sourceRecoveredSignature',
    'sourceRecoveredSha256', 'videoTrim'
) 'durable reader source conflicts'
Assert-ExactSet @($durableReader.presetEnvelope.exactFields) @(
    'id', 'label', 'modelFamily', 'modelName', 'scale', 'outputFormat',
    'denoise', 'sharpen', 'detail', 'smoothness', 'colorBrightness',
    'colorContrast', 'colorSaturation', 'options'
) 'durable reader preset fields'
Assert-ExactSet @($durableReader.presetEnvelope.exactOptionFields) @(
    'backendId', 'protocol', 'kind', 'container'
) 'durable reader preset option fields'
Assert-ExactSet @($durableReader.knownLifecycleFields) @(
    'status', 'progress', 'cancelRequested', 'queueOrder', 'createdAt',
    'updatedAt', 'startedAt', 'finishedAt', 'runId', 'workerInstanceId',
    'lastHeartbeatAt', 'lastProgressAt', 'externalPromptId',
    'externalProcessId', 'diagnostics', 'outputPath', 'outputSha256',
    'outputBytes', 'errorCode', 'errorMessage'
) 'durable reader known lifecycle fields'
if ($durableReader.states.queued.exactValues.progress -ne 0 -or
    $durableReader.states.queued.exactValues.cancelRequested -ne $false -or
    $durableReader.states.running.valueRules.progress -cne 'integer 1 through 99' -or
    $durableReader.states.running.valueRules.cancelRequested -ne $false -or
    $durableReader.states.succeeded.exactValues.progress -ne 100 -or
    $durableReader.states.succeeded.exactValues.cancelRequested -ne $false -or
    $durableReader.states.succeeded.outputIdentityRule -notmatch '536870912' -or
    $durableReader.states.failed.valueRules.progress -cne 'integer 0 through 99' -or
    $durableReader.states.failed.valueRules.cancelRequested -ne $false -or
    $durableReader.states.canceled.valueRules.cancelRequested -ne $true -or
    $durableReader.states.deleted.exactValues.progress -ne 100 -or
    $durableReader.states.deleted.exactValues.cancelRequested -ne $false -or
    $durableReader.runningScalarRules.technicalTextRule -notmatch 'U\+0021 through U\+007E' -or
    $durableReader.runningScalarRules.externalProcessIdRule -notmatch '2147483647' -or
    $durableReader.runningScalarRules.timestampRule -notmatch 'four decimal year digits' -or
    $durableReader.numberTokenRule -notmatch 'mathematically identical' -or
    $durableReader.numberTokenRule -notmatch '2e0' -or
    $durableReader.sourceEnvelope.pathScalarRule -notmatch 'fully-qualified Windows' -or
    $durableReader.runningCancellationTransient.publicReader -notmatch 'reader-only' -or
    $durableReader.runningCancellationTransient.privateWorker -notmatch 'compare-and-swap' -or
    $durableReader.unknownAndMalformedRule -notmatch 'zero filesystem') {
    throw 'The exact Video Tools v2 durable lifecycle state envelope is incomplete.'
}
Assert-ExactSet @($v2.request.sourceSelector.exactKinds) @('managed-video-job', 'displayed-file') 'source selector kinds'
Assert-ExactSet @($v2.request.sourceSelector.'managed-video-job'.exactFields) @(
    'kind', 'sourceVideoJobId'
) 'managed source selector fields'
Assert-ExactSet @($v2.request.sourceSelector.'displayed-file'.exactFields) @(
    'kind', 'path', 'size', 'mtimeMs', 'sha256'
) 'displayed-file selector fields'
Assert-ExactSet @($v2.request.edit.exactVideoToolsFields) @(
    'schemaVersion',
    'kind',
    'source',
    'selection',
    'instructionJa',
    'compiled',
    'audioPolicy',
    'steps',
    'strength',
    'maximumPixelArea'
) 'edit request fields'
Assert-ExactSet @($v2.request.edit.audioPolicy) @(
    'preserve', 'mute'
) 'Edit audio policy values'
Assert-ExactSet @($v2.request.finish.exactVideoToolsFields) @(
    'schemaVersion',
    'kind',
    'source',
    'mode',
    'scale'
) 'finish request fields'
Assert-ExactSet @($v2.request.clientForbiddenFields) @(
    'seed',
    'backendId',
    'runtimeReceiptId',
    'workflowReceiptId',
    'modelReceiptId',
    'contextWindow',
    'actualAffectedRange',
    'actualMask',
    'denoise',
    'backendFps',
    'backendFrameCount',
    'alignmentPadding',
    'deliveryCrop',
    'delivery',
    'canonicalSourcePath',
    'stagingPath',
    'sourceSignature',
    'sourceSha256',
    'sourceProbe'
) 'client-forbidden fields'

$compiler = $v2.promptCompiler
$compilerTransport = $compiler.transport
Assert-ExactSet @($compilerTransport.exactActions) @(
    'probe', 'preview', 'compile'
) 'prompt compiler transport actions'
if ($compilerTransport.route -cne '/api/enhance/video-prompts/v2/edit/compile' -or
    $compilerTransport.method -cne 'POST' -or
    $compilerTransport.maximumRequestBytes -ne 131072 -or
    $compilerTransport.maximumResponseBytesByAction.probe -ne 131072 -or
    $compilerTransport.maximumResponseBytesByAction.preview -ne 2113536 -or
    $compilerTransport.maximumResponseBytesByAction.compile -ne 131072 -or
    $compilerTransport.routing -notmatch '404' -or
    $compilerTransport.routing -notmatch '405' -or
    $compilerTransport.routing -notmatch 'Allow: POST' -or
    $compilerTransport.passiveReads -notmatch 'never dispatch') {
    throw 'The authenticated exact Video Edit route or its action-specific byte limits changed.'
}
Assert-ExactSet @($compiler.previewIdentities.exactRoles) @(
    'start', 'middle', 'end'
) 'prompt compiler preview roles'
Assert-ExactSet @($compiler.previewIdentities.exactFields) @(
    'role', 'sourceFrame', 'sourcePts', 'decodedPixelSha256', 'decoderRevision'
) 'prompt compiler preview identity fields'
Assert-ExactSet @($compiler.previewProbe.exactSourceSummaryFields) @(
    'frameCount',
    'fpsNumerator',
    'fpsDenominator',
    'durationMs',
    'width',
    'height'
) 'transient preview-probe source summary fields'
Assert-ExactSet @($compiler.previewProbe.exactPreviewFields) @(
    'role',
    'sourceFrame',
    'sourcePts',
    'decodedPixelSha256',
    'decoderRevision',
    'mime',
    'width',
    'height',
    'encodedBytes',
    'encodedSha256',
    'base64'
) 'transient preview payload fields'
Assert-ExactSet @($compiler.previewProbe.thumbnail.orderedRoles) @(
    'start', 'middle', 'end'
) 'transient preview thumbnail roles'
if ($compiler.previewProbe.thumbnail.mime -cne 'image/png' -or
    $compiler.previewProbe.thumbnail.exactCount -ne 3 -or
    $compiler.previewProbe.thumbnail.maximumEdge -ne 384 -or
    $compiler.previewProbe.thumbnail.maximumPixels -ne 147456 -or
    $compiler.previewProbe.thumbnail.maximumEncodedBytesEach -ne 524288 -or
    $compiler.previewProbe.thumbnail.maximumEncodedBytesTotal -ne 1572864 -or
    $compiler.previewProbe.thumbnail.base64 -notmatch 'no data-URL prefix' -or
    $compiler.previewProbe.thumbnail.encodedSha256 -notmatch 'separate from decodedPixelSha256' -or
    $compiler.previewProbe.thumbnail.decodedPixelIdentity -notmatch 'full-resolution source-frame RGB24' -or
    $compiler.previewProbe.thumbnail.runtime -notmatch 'shell=false' -or
    $compiler.previewProbe.thumbnail.sourceLease -notmatch 'pre/post source SHA-256') {
    throw 'The exact bounded PNG thumbnail payload or immutable source/runtime boundary changed.'
}
Assert-ExactSet @($compiler.candidate.exactFields) @(
    'backendPrompt', 'summaryJa', 'compilerRevision', 'contextDigest'
) 'prompt compiler candidate fields'
$expectedCompilerLabel = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('5oyH56S644KS5pW044GI44KL'))
$expectedPreviewLabel = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('44OV44Os44O844Og44KS6Kqt44G/6L6844KA'))
$compilerFlowChecks = [ordered]@{
    label = $compiler.labelJa -cne $expectedCompilerLabel
    action = $compiler.action -cne 'explicit authenticated Edit action'
    previewLabel = $compiler.previewProbe.labelJa -cne $expectedPreviewLabel
    previewAction = $compiler.previewProbe.action -cne 'explicit authenticated transient source-preview action'
    previewCreatesJob = $compiler.previewProbe.effects.createsJob -ne $false
    previewPublishesInbox = $compiler.previewProbe.effects.publishesDurableInbox -ne $false
    previewWakesQueue = $compiler.previewProbe.effects.wakesQueue -ne $false
    previewStagesSource = $compiler.previewProbe.effects.stagesSource -ne $false
    previewCreatesOutput = $compiler.previewProbe.effects.createsOutput -ne $false
    previewGate = $compiler.previewProbe.gate -notmatch 'stay disabled'
    previewFps = $compiler.previewProbe.fpsRule -notmatch 'Never assume 24 fps'
    previewManaged = $compiler.previewProbe.managedVideoJob -notmatch 'persisted source probe'
    bindsSource = $compiler.effects.mayBindCanonicalizeHashAndProbeSelectedSource -ne $true
    decodesPreview = $compiler.effects.mayDecodeBoundedPreviewFrames -ne $true
    invokesCompiler = $compiler.effects.mayInvokeLocalCompiler -ne $true
    stagesSource = $compiler.effects.stagesSource -ne $false
    createsJob = $compiler.effects.createsJob -ne $false
    publishesInbox = $compiler.effects.publishesDurableInbox -ne $false
    wakesQueue = $compiler.effects.wakesQueue -ne $false
    createsOutput = $compiler.effects.createsOutput -ne $false
    candidateTransient = $compiler.candidate.transient -ne $true
    candidateDurable = $compiler.candidate.durableBeforeStart -ne $false
    review = $compiler.reviewUnchecked -notmatch 'separate explicit Start'
    skipAuthorization = $compiler.reviewSkipChecked -notmatch 'single-use authorization'
    skipResponse = $compiler.reviewSkipChecked -notmatch 'response by itself never'
    revalidation = $compiler.startRevalidation -notmatch 'no durable inbox publication'
    passive = $compiler.passive -notmatch 'never bind'
    nonAiVersion = $v2.featureIsolation.nonAiTrim -notmatch 'separately versioned'
    nonAiKind = $v2.featureIsolation.nonAiTrim -notmatch 'not an Edit or Finish kind'
}
$compilerFlowFailures = @($compilerFlowChecks.GetEnumerator() |
    Where-Object { $_.Value } | ForEach-Object { $_.Key })
if ($compilerFlowFailures.Count -ne 0) {
    throw "The explicit transient prompt-compiler flow or separate non-AI trim boundary changed: $($compilerFlowFailures -join ', ')."
}

$source = $v2.source
if ($source.container -cne 'mp4' -or
    $source.regularFileRequired -ne $true -or
    $source.maximumBytes -ne 536870912 -or
    $source.maximumDurationMs -ne 300000 -or
    $source.maximumVideoStreams -ne 1 -or
    $source.maximumAudioStreams -ne 1 -or
    $source.maximumWidth -ne 1920 -or
    $source.maximumHeight -ne 1080 -or
    $source.maximumPixelArea -ne 2073600 -or
    $source.maximumFrames -ne 18000 -or
    $source.externalSourceDeletion -cne 'never') {
    throw 'The common v2 source bounds or external-source ownership rule changed.'
}
Assert-ExactSet @($v2.persistedSnapshot.exactKeys.source.probe) @(
    'container',
    'width',
    'height',
    'frameCount',
    'fpsNumerator',
    'fpsDenominator',
    'durationMs',
    'videoStreamCount',
    'audioStreamCount',
    'videoTimeBaseNumerator',
    'videoTimeBaseDenominator',
    'videoStartTimestamp',
    'videoPtsSha256',
    'bitDepth',
    'dynamicRange',
    'audio'
) 'persisted source probe fields'
if ($source.videoPtsProbe.bounds -notmatch '18000' -or
    $source.audioProbe.maximumPacketEntries -ne 65536 -or
    $source.audioProbe.maximumProbeMetadataBytes -ne 8388608 -or
    $source.audioProbe.packetMetadataTimeoutMs -ne 30000) {
    throw 'The bounded five-minute video PTS or audio packet probe is incomplete.'
}
$editFps = @($v2.editPlan.allowedSourceFps | ForEach-Object {
    '{0}/{1}' -f $_.numerator, $_.denominator
})
Assert-ExactSet $editFps @('24/1', '30/1', '60/1') 'Edit source fps'
if ($v2.editPlan.maximumSelectedDurationMs -ne 5000 -or
    $v2.editPlan.maximumSelectedFrames -ne 300) {
    throw 'The initial Edit child-clip selection bound is not exact.'
}
Assert-ExactSet @($v2.request.edit.maximumPixelArea) @(230400, 307200, 414720) 'Edit pixel tiers'
if (@($v2.request.edit.exactVideoToolsFields) -notcontains 'maximumPixelArea' -or
    $fixture.requestFixtures.editManaged.videoTools.maximumPixelArea -ne 414720 -or
    $fixture.readerFixtures.edit.video.requested.maximumPixelArea -ne 414720) {
    throw 'Edit maximumPixelArea must remain a required request and persisted-request field.'
}
Assert-ExactSet @($v2.persistedSnapshot.exactKeys.edit.requested) @(
    'schemaVersion', 'kind', 'source', 'selection', 'instructionJa', 'compiled',
    'audioPolicy', 'steps', 'strength', 'maximumPixelArea'
) 'persisted Edit requested keys'
Assert-ExactSet @($v2.persistedSnapshot.exactKeys.edit.plan) @(
    'selected', 'sourceToBackendMap', 'backendWindow', 'deliveryCrop',
    'strengthMapping', 'modelCanvas', 'audioPlan'
) 'persisted Edit plan keys'
Assert-ExactSet @($v2.persistedSnapshot.exactKeys.edit.delivery) @(
    'width', 'height', 'frameCount', 'fpsNumerator', 'fpsDenominator',
    'durationMs', 'videoPtsSha256', 'childClip', 'fullSourceSplice',
    'discardGeneratedAudio', 'audioDelivery'
) 'persisted Edit delivery keys'

$audioPlanVariants = $v2.persistedSnapshot.exactKeys.edit.audioPlan.variants
Assert-ExactSet @($audioPlanVariants.'preserve-packets'.exactKeys) @(
    'kind', 'policy', 'packetStartIndex', 'packetEndIndexExclusive',
    'editListRevision'
) 'Edit preserve-packets plan keys'
Assert-ExactSet @($audioPlanVariants.'preserve-no-packets'.exactKeys) @(
    'kind', 'policy'
) 'Edit preserve-no-packets plan keys'
Assert-ExactSet @($audioPlanVariants.mute.exactKeys) @(
    'kind', 'policy'
) 'Edit mute plan keys'
$audioDeliveryVariants = $v2.persistedSnapshot.exactKeys.edit.audioDelivery.variants
Assert-ExactSet @($audioDeliveryVariants.'remuxed-source-packets'.exactKeys) @(
    'kind', 'policy', 'audioStreamCount', 'sourcePacketStartIndex',
    'sourcePacketEndIndexExclusive', 'sourcePacketPayloadSha256'
) 'Edit remuxed-source-packets delivery keys'
Assert-ExactSet @($audioDeliveryVariants.'no-audio-preserve-empty'.exactKeys) @(
    'kind', 'policy', 'audioStreamCount'
) 'Edit no-audio-preserve-empty delivery keys'
Assert-ExactSet @($audioDeliveryVariants.'no-audio-muted'.exactKeys) @(
    'kind', 'policy', 'audioStreamCount'
) 'Edit no-audio-muted delivery keys'
if ($v2.persistedSnapshot.exactKeys.edit.audioPlan.discriminator -cne 'kind' -or
    $v2.persistedSnapshot.exactKeys.edit.audioDelivery.discriminator -cne 'kind' -or
    $audioPlanVariants.mute.policy -cne 'mute' -or
    $audioDeliveryVariants.'no-audio-muted'.policy -cne 'mute' -or
    $audioDeliveryVariants.'no-audio-muted'.rule -notmatch 'no packet range' -or
    $audioDeliveryVariants.'no-audio-muted'.rule -notmatch 'zero-range placeholder' -or
    $v2.editDelivery.audio.mute -notmatch 'emit no audio stream') {
    throw 'Edit audio plan/delivery discrimination or mute no-audio semantics changed.'
}

$editCandidates = $v2.editBackendCandidates
Assert-ExactSet @($v2.persistedSnapshot.stableIds.editCandidateBackendIds) @(
    'wan-vace-1.3b-edit-candidate-v1',
    'bernini-r-1.3b-edit-candidate-v1',
    'minimax-h3-masked-edit-research-v1'
) 'persisted Edit candidate backend IDs'
if ($editCandidates.status -cne 'candidate-canary-required' -or
    $editCandidates.productionWriterEnabled -ne $false -or
    $editCandidates.candidatePaths.'semantic-v2v'.backendId -cne
        'bernini-r-1.3b-edit-candidate-v1' -or
    $editCandidates.candidatePaths.'precise-mask'.backendId -cne
        'wan-vace-1.3b-edit-candidate-v1' -or
    $editCandidates.candidatePaths.'precise-mask'.readyWithoutMaskContract -ne $false -or
    $editCandidates.candidatePaths.'precise-mask'.qualityAsserted -ne $false -or
    $editCandidates.candidatePaths.'semantic-v2v'.qualityAsserted -ne $false -or
    $editCandidates.candidatePaths.research.productionCandidate -ne $false -or
    $editCandidates.candidatePaths.research.qualityAsserted -ne $false -or
    $editCandidates.firstSemanticCanaryBackendId -cne
        'bernini-r-1.3b-edit-candidate-v1' -or
    $editCandidates.standardWinnerSelected -ne $false -or
    $editCandidates.noDownloadCanary.downloadsModels -ne $false -or
    $editCandidates.separateFeatureNames.rule -notmatch 'not aliases' -or
    $v2.editPlan.spatialMaskRequestInV2 -ne $false -or
    $v2.editDelivery.outputKind -cne 'non-destructive managed child clip' -or
    $v2.editDelivery.fullSourceSplice -ne $false -or
    $v2.editDelivery.sourceOverwrite -ne $false -or
    $v2.editDelivery.discardGeneratedAudio -ne $true) {
    throw 'The backend-independent Edit candidate, mask gate, or child-clip delivery semantics changed.'
}

Assert-ExactSet @($v2.request.finish.mode) @('fast', 'standard', 'quality') 'Finish modes'
Assert-ExactSet @($v2.request.finish.scale) @(2, 4) 'Finish scales'
$finishCandidates = $v2.finishBackendCandidates
$faithfulCandidate = $finishCandidates.candidateFamilies.faithful
$detailCandidate = $finishCandidates.candidateFamilies.'generative-detail'
$lightCandidate = $finishCandidates.candidateFamilies.'lightweight-4x'
Assert-ExactSet @($v2.persistedSnapshot.stableIds.finishCandidateBackendIds) @(
    'nvidia-vfx-vsr-1.2-candidate-v1',
    'seedvr2-3b-detail-candidate-v1',
    'nanovsr-1.7m-4x-candidate-v1'
) 'persisted Finish candidate backend IDs'
Assert-ExactSet @($faithfulCandidate.serverQualityValues) @('LOW', 'MEDIUM', 'HIGH', 'ULTRA') 'NVIDIA faithful candidate values'
if ($finishCandidates.status -cne 'candidate-canary-required' -or
    $finishCandidates.productionWriterEnabled -ne $false -or
    $faithfulCandidate.backendId -cne 'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $faithfulCandidate.package -cne 'nvidia-vfx 0.1.0.1' -or
    $faithfulCandidate.internalSdkVersion -cne '1.2.0.0' -or
    $faithfulCandidate.frameIndependentAsserted -ne $false -or
    $faithfulCandidate.qualityAsserted -ne $false -or
    @($faithfulCandidate.supportedScalesBeforeCanary).Count -ne 0 -or
    $detailCandidate.backendId -cne 'seedvr2-3b-detail-candidate-v1' -or
    $detailCandidate.semanticRole -cne 'generative-detail' -or
    $detailCandidate.qualityAsserted -ne $false -or
    @($detailCandidate.supportedScalesBeforeCanary).Count -ne 0 -or
    $lightCandidate.backendId -cne 'nanovsr-1.7m-4x-candidate-v1' -or
    $lightCandidate.nativeScale -ne 4 -or
    $lightCandidate.qualityAsserted -ne $false -or
    $lightCandidate.chunkSeamCanaryRequired -ne $true -or
    @($lightCandidate.supportedScalesBeforeCanary).Count -ne 0 -or
    $v2.finishPlan.backendMapping.silentBackendFallback -ne $false -or
    $v2.finishPlan.backendMapping.silentModeFallback -ne $false -or
    $v2.finishPlan.maximumOutputWidth -ne 3840 -or
    $v2.finishPlan.maximumOutputHeight -ne 2160 -or
    $v2.finishPlan.maximumOutputPixelArea -ne 8294400 -or
    $v2.finishPlan.frameInterpolation -ne $false -or
    $v2.finishPlan.implicitCrop -ne $false -or
    $v2.finishPlan.initialBitDepth -ne 8 -or
    $v2.finishPlan.initialDynamicRange -cne 'SDR' -or
    $v2.finishPlan.sceneCutResetCanaryRequired -ne $true -or
    $v2.healthCapability.exactShape.finishModes.quality.reasonCode -cne
        'VIDEO_TOOLS_V2_FINISH_QUALITY_MODE_MAPPING_CANARY_REQUIRED') {
    throw 'The separate Finish candidate, output bounds, or preservation semantics changed.'
}

$health = $v2.healthCapability.exactShape
if ($v2.healthCapability.property -cne 'capabilities.videoToolsV2' -or
    $health.readerReady -ne $true -or
    $health.edit.writerEnabled -ne $false -or
    $health.edit.backendConfigured -ne $false -or
    $health.edit.runtimeVerified -ne $false -or
    $health.edit.ready -ne $false -or
    $health.finish.writerEnabled -ne $false -or
    $health.finish.backendConfigured -ne $false -or
    $health.finish.runtimeVerified -ne $false -or
    $health.finish.ready -ne $false -or
    $health.finishModes.fast.writerEnabled -ne $false -or
    $health.finishModes.fast.backendConfigured -ne $false -or
    $health.finishModes.fast.runtimeVerified -ne $false -or
    $health.finishModes.fast.ready -ne $false -or
    $health.finishModes.standard.writerEnabled -ne $false -or
    $health.finishModes.standard.backendConfigured -ne $false -or
    $health.finishModes.standard.runtimeVerified -ne $false -or
    $health.finishModes.standard.ready -ne $false -or
    $health.finishModes.quality.writerEnabled -ne $false -or
    $health.finishModes.quality.backendConfigured -ne $false -or
    $health.finishModes.quality.runtimeVerified -ne $false -or
    $health.finishModes.quality.ready -ne $false) {
    throw 'Video Tools v2 health must remain reader-ready with both production writers closed.'
}
$editCapability = $v2.healthCapability.editCapabilityVariants
Assert-ExactSet @($editCapability.currentDisabledCompatibility.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode'
) 'current disabled Edit capability keys'
Assert-ExactSet @($health.edit.PSObject.Properties.Name) @(
    $editCapability.currentDisabledCompatibility.exactKeys
) 'current disabled Edit health shape'
Assert-ExactSet @($editCapability.ready.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode', 'capabilityRevision', 'resolvedBackend', 'receipts',
    'resourceBounds', 'outputPolicy'
) 'future ready Edit capability keys'
Assert-ExactSet @($editCapability.ready.resolvedBackend.exactKeys) @(
    'backendId', 'semanticRole', 'conditioningKind',
    'genuineSourceVideoConditioning', 'imageGuideRetake', 'modelRevision',
    'workflowRevision', 'promptCompilerRevision', 'timelineMappingRevision',
    'deliveryMappingRevision'
) 'future ready Edit resolved backend keys'
Assert-ExactSet @($editCapability.ready.receipts.exactKeys) @(
    'runtimeReceiptId', 'modelReceiptId', 'workflowReceiptId',
    'promptCompilerReceiptId', 'timelineMapperReceiptId',
    'audioDeliveryReceiptId', 'qualityCanaryReceiptId',
    'resourceCanaryReceiptId', 'cancelCanaryReceiptId',
    'recoveryCanaryReceiptId', 'outputValidatorReceiptId', 'receiptSetSha256'
) 'future ready Edit receipt keys'
Assert-ExactSet @($editCapability.ready.resourceBounds.exactKeys) @(
    'maximumSourceBytes', 'maximumSourceDurationMs', 'maximumSourceWidth',
    'maximumSourceHeight', 'maximumSourcePixelArea', 'maximumSourceFrames',
    'allowedSourceFps', 'maximumSelectedDurationMs', 'maximumSelectedFrames',
    'supportedMaximumPixelAreas', 'minimumSteps', 'maximumSteps',
    'minimumStrength', 'maximumStrength', 'maximumConcurrentExecutions',
    'maximumGpuVramBytes', 'maximumHostRamBytes', 'maximumScratchBytes',
    'maximumOutputBytes', 'processTimeoutMs', 'cancelGraceMs'
) 'future ready Edit resource bound keys'
Assert-ExactSet @($editCapability.ready.outputPolicy.exactKeys) @(
    'revision', 'container', 'videoCodec', 'pixelFormat', 'bitDepth',
    'dynamicRange', 'videoStreamCount', 'maximumAudioStreamCount',
    'subtitleStreamCount', 'dataStreamCount', 'attachmentStreamCount',
    'maximumBytes'
) 'future ready Edit output policy keys'
$readyFixed = $editCapability.ready.fixedValues
$readyBackend = $editCapability.ready.resolvedBackend.firstActivation
$readyResource = $editCapability.ready.resourceBounds.fixedRequestBounds
$readyOutput = $editCapability.ready.outputPolicy.fixedValues
if ($editCapability.discriminator -cne 'state' -or
    $readyFixed.writerEnabled -ne $true -or
    $readyFixed.backendConfigured -ne $true -or
    $readyFixed.runtimeVerified -ne $true -or
    $readyFixed.ready -ne $true -or
    $readyFixed.state -cne 'ready' -or
    $null -ne $readyFixed.reasonCode -or
    $readyFixed.capabilityRevision -cne 'aibos-video-edit-ready-v1' -or
    $readyBackend.backendId -cne 'bernini-r-1.3b-edit-candidate-v1' -or
    $readyBackend.semanticRole -cne 'semantic-v2v' -or
    $readyBackend.conditioningKind -cne 'source-video-conditioned-semantic-v2v' -or
    $readyBackend.genuineSourceVideoConditioning -ne $true -or
    $readyBackend.imageGuideRetake -ne $false -or
    $editCapability.ready.resolvedBackend.rule -notmatch 'FL2VA' -or
    $readyResource.maximumSourceBytes -ne 536870912 -or
    $readyResource.maximumSourceDurationMs -ne 300000 -or
    $readyResource.maximumSelectedDurationMs -ne 5000 -or
    $readyResource.maximumSelectedFrames -ne 300 -or
    $readyResource.maximumConcurrentExecutions -ne 1 -or
    (@($readyResource.allowedSourceFps) -join ',') -cne '24/1,30/1,60/1' -or
    (@($readyResource.supportedMaximumPixelAreas) -join ',') -cne
        '230400,307200,414720' -or
    $readyOutput.revision -cne 'aibos-video-edit-child-mp4-validator-v1' -or
    $readyOutput.container -cne 'mp4' -or
    $readyOutput.videoCodec -cne 'h264' -or
    $readyOutput.pixelFormat -cne 'yuv420p' -or
    $readyOutput.bitDepth -ne 8 -or
    $readyOutput.videoStreamCount -ne 1 -or
    $readyOutput.maximumAudioStreamCount -ne 1 -or
    $editCapability.currentEvidence.readyVariantAdvertised -ne $false -or
    $editCapability.currentEvidence.liveCanaryReceiptsPresent -ne $false -or
    $editCapability.currentEvidence.productionWriterEnabled -ne $false -or
    $editCapability.currentEvidence.qualityAsserted -ne $false -or
    $v2.editDurableWriter.productionWriterEnabled -ne $false -or
    $v2.editOutputValidator.productionValidated -ne $false -or
    $v2.editOutputValidator.validatorCanaryVerified -ne $false -or
    $v2.productionGate.currentEdit.productionWriterEnabled -ne $false -or
    $v2.productionGate.currentEdit.qualityAsserted -ne $false) {
    throw 'The future exact genuine-V2V ready capability or current closed evidence changed.'
}

$finishCapability = $v2.healthCapability.finishCapabilityVariants
Assert-ExactSet @($finishCapability.currentDisabledCompatibility.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode'
) 'current disabled Finish capability keys'
Assert-ExactSet @($health.finish.PSObject.Properties.Name) @(
    $finishCapability.currentDisabledCompatibility.exactKeys
) 'current disabled Finish health shape'
Assert-ExactSet @($finishCapability.ready.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode', 'capabilityRevision', 'resolvedBackend', 'receipts',
    'resourceBounds', 'streamingPolicy', 'outputPolicy'
) 'future ready Finish capability keys'
Assert-ExactSet @($finishCapability.ready.resolvedBackend.exactKeys) @(
    'backendId', 'semanticRole', 'backendFamily', 'package',
    'internalSdkVersion', 'runnerRevision', 'outputValidatorRevision',
    'attemptJournalRevision'
) 'future ready Finish resolved backend keys'
Assert-ExactSet @($finishCapability.ready.receipts.exactKeys) @(
    'runtimeReceiptId', 'packageReceiptId', 'sdkReceiptId',
    'driverGpuReceiptId', 'runnerReceiptId', 'frameStreamingReceiptId',
    'timelinePreservationReceiptId', 'audioPacketCopyReceiptId',
    'sceneCutReceiptId', 'resourceCanaryReceiptId', 'cancelCanaryReceiptId',
    'recoveryCanaryReceiptId', 'journalReceiptId',
    'outputValidatorReceiptId', 'receiptSetSha256'
) 'future ready Finish receipt keys'
Assert-ExactSet @($finishCapability.ready.resourceBounds.exactKeys) @(
    'maximumSourceBytes', 'maximumSourceDurationMs', 'maximumSourceWidth',
    'maximumSourceHeight', 'maximumSourcePixelArea', 'maximumSourceFrames',
    'allowedSourceFps', 'supportedScales', 'maximumOutputWidth',
    'maximumOutputHeight', 'maximumOutputPixelArea',
    'maximumConcurrentGpuJobs', 'maximumBufferedFrames',
    'maximumDecodedFrameBytes', 'maximumGpuVramBytes',
    'maximumHostRamBytes', 'maximumScratchBytes', 'maximumOutputBytes',
    'processTimeoutMs', 'cancelGraceMs'
) 'future ready Finish resource bound keys'
Assert-ExactSet @($finishCapability.ready.streamingPolicy.exactKeys) @(
    'revision', 'boundedFrameStreaming', 'sourceLengthDependentRetention',
    'maximumConcurrentGpuJobs', 'frameCountPreserved',
    'rationalFpsPreserved', 'fullVideoPtsSequencePreserved',
    'durationPreserved', 'frameInterpolation', 'frameRateConversion',
    'generatedAudioAllowed', 'sourceAudioPacketIdentityPreserved'
) 'future ready Finish streaming policy keys'
Assert-ExactSet @($finishCapability.ready.outputPolicy.exactKeys) @(
    'revision', 'container', 'videoCodec', 'pixelFormat', 'bitDepth',
    'dynamicRange', 'videoStreamCount', 'maximumAudioStreamCount',
    'subtitleStreamCount', 'dataStreamCount', 'attachmentStreamCount',
    'implicitCrop', 'maximumWidth', 'maximumHeight', 'maximumPixelArea',
    'maximumBytes'
) 'future ready Finish output policy keys'
$finishReadyFixed = $finishCapability.ready.fixedValues
$finishReadyBackend = $finishCapability.ready.resolvedBackend.firstActivation
$finishReadyResource = $finishCapability.ready.resourceBounds.fixedRequestBounds
$finishStreaming = $finishCapability.ready.streamingPolicy.fixedValues
$finishOutputPolicy = $finishCapability.ready.outputPolicy.fixedValues
if ($finishCapability.discriminator -cne 'state' -or
    $finishReadyFixed.writerEnabled -ne $true -or
    $finishReadyFixed.backendConfigured -ne $true -or
    $finishReadyFixed.runtimeVerified -ne $true -or
    $finishReadyFixed.ready -ne $true -or
    $finishReadyFixed.state -cne 'ready' -or
    $null -ne $finishReadyFixed.reasonCode -or
    $finishReadyFixed.capabilityRevision -cne 'aibos-video-finish-ready-v1' -or
    $finishReadyBackend.backendId -cne 'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $finishReadyBackend.semanticRole -cne 'faithful' -or
    $finishReadyBackend.package -cne 'nvidia-vfx 0.1.0.1' -or
    $finishReadyBackend.internalSdkVersion -cne '1.2.0.0' -or
    $finishReadyBackend.outputValidatorRevision -cne
        'aibos-video-finish-mp4-validator-v1' -or
    $finishReadyBackend.attemptJournalRevision -cne
        'aibos-video-finish-attempt-journal-v1' -or
    $finishReadyResource.maximumSourceBytes -ne 536870912 -or
    $finishReadyResource.maximumSourceDurationMs -ne 300000 -or
    $finishReadyResource.maximumSourceWidth -ne 1920 -or
    $finishReadyResource.maximumSourceHeight -ne 1080 -or
    $finishReadyResource.maximumSourcePixelArea -ne 2073600 -or
    $finishReadyResource.maximumSourceFrames -ne 18000 -or
    (@($finishReadyResource.allowedSourceFps) -join ',') -cne
        '24/1,30/1,60/1' -or
    (@($finishReadyResource.supportedScales) -join ',') -cne '2,4' -or
    $finishReadyResource.maximumOutputWidth -ne 3840 -or
    $finishReadyResource.maximumOutputHeight -ne 2160 -or
    $finishReadyResource.maximumOutputPixelArea -ne 8294400 -or
    $finishReadyResource.maximumConcurrentGpuJobs -ne 1 -or
    $finishStreaming.boundedFrameStreaming -ne $true -or
    $finishStreaming.sourceLengthDependentRetention -ne $false -or
    $finishStreaming.frameCountPreserved -ne $true -or
    $finishStreaming.rationalFpsPreserved -ne $true -or
    $finishStreaming.fullVideoPtsSequencePreserved -ne $true -or
    $finishStreaming.frameInterpolation -ne $false -or
    $finishStreaming.generatedAudioAllowed -ne $false -or
    $finishOutputPolicy.container -cne 'mp4' -or
    $finishOutputPolicy.videoCodec -cne 'h264' -or
    $finishOutputPolicy.pixelFormat -cne 'yuv420p' -or
    $finishOutputPolicy.bitDepth -ne 8 -or
    $finishOutputPolicy.dynamicRange -cne 'SDR' -or
    $finishOutputPolicy.maximumWidth -ne 3840 -or
    $finishOutputPolicy.maximumHeight -ne 2160 -or
    $finishOutputPolicy.maximumPixelArea -ne 8294400 -or
    $finishCapability.currentEvidence.readyVariantAdvertised -ne $false -or
    $finishCapability.currentEvidence.liveCanaryReceiptsPresent -ne $false -or
    $finishCapability.currentEvidence.productionWriterEnabled -ne $false -or
    $finishCapability.currentEvidence.qualityAsserted -ne $false -or
    $finishCapability.currentEvidence.firstFaithfulCandidateBackendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $finishCapability.ready.resolvedBackend.rule -notmatch 'NanoVSR' -or
    $finishCapability.editIsolation -notmatch 'does not add') {
    throw 'The future exact faithful Finish capability or current closed evidence changed.'
}

$finishModesCapability = $v2.healthCapability.finishModeCapabilityVariants
Assert-ExactSet @($finishModesCapability.currentDisabledCompatibility.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode'
) 'current disabled Finish mode capability keys'
foreach ($mode in @('fast', 'standard', 'quality')) {
    Assert-ExactSet @($health.finishModes.$mode.PSObject.Properties.Name) @(
        $finishModesCapability.currentDisabledCompatibility.exactKeys
    ) "current disabled Finish $mode health shape"
}
Assert-ExactSet @($finishModesCapability.ready.exactKeys) @(
    'writerEnabled', 'backendConfigured', 'runtimeVerified', 'ready', 'state',
    'reasonCode', 'modeCapabilityRevision', 'mode', 'resolvedBackend',
    'receipts', 'sourceBounds', 'supportedScales', 'deliveryPolicy'
) 'future ready Finish mode capability keys'
Assert-ExactSet @($finishModesCapability.ready.resolvedBackend.exactKeys) @(
    'backendId', 'semanticRole', 'backendSetting', 'modeMappingRevision',
    'deliveryMappingRevision', 'sceneCutPolicyRevision'
) 'future ready Finish mode backend keys'
Assert-ExactSet @($finishModesCapability.ready.receipts.exactKeys) @(
    'modeMappingReceiptId', 'qualityCanaryReceiptId',
    'sourceBoundCanaryReceiptId', 'scale2CanaryReceiptId',
    'scale4CanaryReceiptId', 'deliveryMappingReceiptId',
    'sceneCutCanaryReceiptId', 'modeReceiptSetSha256'
) 'future ready Finish mode receipt keys'
Assert-ExactSet @($finishModesCapability.ready.sourceBounds.exactKeys) @(
    'maximumSourceBytes', 'maximumSourceDurationMs', 'maximumSourceWidth',
    'maximumSourceHeight', 'maximumSourcePixelArea', 'maximumSourceFrames',
    'allowedSourceFps'
) 'future ready Finish mode source bound keys'
Assert-ExactSet @($finishModesCapability.ready.deliveryPolicy.exactKeys) @(
    'revision', 'explicitScale4Required', 'scale4OutputBoundsRequired',
    'silentScaleFallback', 'silentModeFallback', 'frameCountPreserved',
    'rationalFpsPreserved', 'fullVideoPtsSequencePreserved',
    'frameInterpolation', 'frameRateConversion', 'implicitCrop',
    'sourceAudioPacketIdentityPreserved'
) 'future ready Finish mode delivery policy keys'
$modeFixed = $finishModesCapability.ready.fixedValues
$modeMapping = $finishModesCapability.ready.resolvedBackend.modeMappings
$modeDelivery = $finishModesCapability.ready.deliveryPolicy.fixedValues
if ($finishModesCapability.discriminator -cne 'state' -or
    $modeFixed.writerEnabled -ne $true -or
    $modeFixed.backendConfigured -ne $true -or
    $modeFixed.runtimeVerified -ne $true -or
    $modeFixed.ready -ne $true -or
    $modeFixed.state -cne 'ready' -or
    $null -ne $modeFixed.reasonCode -or
    $modeFixed.modeCapabilityRevision -cne
        'aibos-video-finish-mode-ready-v1' -or
    $modeMapping.fast.backendId -cne 'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $modeMapping.fast.semanticRole -cne 'faithful' -or
    (@($modeMapping.fast.allowedBackendSettings) -join ',') -cne 'LOW,MEDIUM' -or
    $modeMapping.standard.backendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $modeMapping.standard.semanticRole -cne 'faithful' -or
    $modeMapping.standard.fixedBackendSetting -cne 'HIGH' -or
    $modeMapping.quality.backendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $modeMapping.quality.semanticRole -cne 'faithful' -or
    $modeMapping.quality.fixedBackendSetting -cne 'ULTRA' -or
    (@($finishModesCapability.ready.supportedScales) -join ',') -cne '2,4' -or
    $modeDelivery.explicitScale4Required -ne $true -or
    $modeDelivery.scale4OutputBoundsRequired -ne $true -or
    $modeDelivery.silentScaleFallback -ne $false -or
    $modeDelivery.silentModeFallback -ne $false -or
    $modeDelivery.frameCountPreserved -ne $true -or
    $modeDelivery.fullVideoPtsSequencePreserved -ne $true -or
    $modeDelivery.frameInterpolation -ne $false -or
    $modeDelivery.implicitCrop -ne $false -or
    $finishModesCapability.requestedModeRule -notmatch
        'Finish overall' -or
    $finishModesCapability.requestedModeRule -notmatch
        'requested mode' -or
    $finishModesCapability.currentEvidence.readyVariantsAdvertised.fast -ne
        $false -or
    $finishModesCapability.currentEvidence.readyVariantsAdvertised.standard -ne
        $false -or
    $finishModesCapability.currentEvidence.readyVariantsAdvertised.quality -ne
        $false -or
    $finishModesCapability.currentEvidence.liveCanaryReceiptsPresent -ne
        $false -or
    $finishModesCapability.currentEvidence.productionWriterEnabled -ne $false -or
    $finishModesCapability.currentEvidence.qualityAsserted -ne $false) {
    throw 'The future exact per-mode Finish mapping, scale, or conjunction changed.'
}

$outputValidator = $v2.editOutputValidator
if ($outputValidator.file.container -cne 'mp4' -or
    $outputValidator.file.hardMaximumBytes -ne 536870912 -or
    $outputValidator.streams.videoStreamCount -ne 1 -or
    $outputValidator.streams.maximumAudioStreamCount -ne 1 -or
    $outputValidator.streams.subtitleStreamCount -ne 0 -or
    $outputValidator.streams.dataStreamCount -ne 0 -or
    $outputValidator.streams.attachmentStreamCount -ne 0 -or
    $outputValidator.video.codec -cne 'h264' -or
    $outputValidator.video.pixelFormat -cne 'yuv420p' -or
    $outputValidator.video.bitDepth -ne 8 -or
    $outputValidator.video.frameCount -notmatch 'selected source frame count' -or
    $outputValidator.pts.startTimestamp -ne 0 -or
    $outputValidator.pts.digest -notmatch 'delivery.videoPtsSha256' -or
    $outputValidator.audio.generatedAudioAllowed -ne $false -or
    $outputValidator.audio.muteOrEmpty -notmatch 'no audio stream' -or
    (@($outputValidator.validationAndPublicationOrder) -join ' ') -notmatch
        'output validator receipt' -or
    (@($outputValidator.validationAndPublicationOrder) -join ' ') -notmatch
        'reopen and revalidate') {
    throw 'The final Edit child MP4 container, stream, PTS, pixel, audio, byte, or publication policy changed.'
}

$finishOutputValidator = $v2.finishOutputValidator
if ($finishOutputValidator.policyRevision -cne
        'aibos-video-finish-mp4-validator-v1' -or
    $finishOutputValidator.productionValidated -ne $false -or
    $finishOutputValidator.validatorCanaryVerified -ne $false -or
    $finishOutputValidator.file.container -cne 'mp4' -or
    $finishOutputValidator.file.hardMaximumBytes -ne 536870912 -or
    $finishOutputValidator.streams.videoStreamCount -ne 1 -or
    $finishOutputValidator.streams.maximumAudioStreamCount -ne 1 -or
    $finishOutputValidator.streams.subtitleStreamCount -ne 0 -or
    $finishOutputValidator.streams.dataStreamCount -ne 0 -or
    $finishOutputValidator.streams.attachmentStreamCount -ne 0 -or
    $finishOutputValidator.video.codec -cne 'h264' -or
    $finishOutputValidator.video.pixelFormat -cne 'yuv420p' -or
    $finishOutputValidator.video.bitDepth -ne 8 -or
    $finishOutputValidator.video.dynamicRange -cne 'SDR' -or
    $finishOutputValidator.video.frameCount -notmatch
        'complete source video frame count' -or
    $finishOutputValidator.video.frameInterpolation -ne $false -or
    $finishOutputValidator.video.frameRateConversion -ne $false -or
    $finishOutputValidator.pts.sequence -notmatch 'full' -or
    $finishOutputValidator.pts.sequence -notmatch 'preserved exactly' -or
    $finishOutputValidator.pts.digest -notmatch
        'source.probe.videoPtsSha256' -or
    $finishOutputValidator.pts.digest -notmatch 'delivery.videoPtsSha256' -or
    $finishOutputValidator.audio.generatedAudioAllowed -ne $false -or
    $finishOutputValidator.audio.packetCopy -notmatch
        'packet payload SHA-256' -or
    $finishOutputValidator.audio.videoTruncationToAudioAllowed -ne $false -or
    (@($finishOutputValidator.validationAndPublicationOrder) -join ' ') -notmatch
        'Finish output-validator receipt' -or
    (@($finishOutputValidator.validationAndPublicationOrder) -join ' ') -notmatch
        'reopen and revalidate') {
    throw 'The separate Finish MP4, full timeline, audio packet-copy, byte, or publication validator changed.'
}

$durableWriter = $v2.editDurableWriter
Assert-ExactSet @($durableWriter.attemptJournal.states) @(
    'reserved', 'source-validated', 'process-started', 'cancel-requested',
    'process-exited', 'output-validating', 'output-validated', 'publishing',
    'published', 'cleanup-pending', 'complete', 'failed'
) 'Edit attempt journal states'
Assert-ExactSet @($durableWriter.attemptJournal.requiredIdentity) @(
    'journalRevision', 'jobId', 'attemptId', 'presetHash', 'backendId',
    'receiptSetSha256', 'sourceIdentityDigest', 'dependencyClosureDigest',
    'scratchOwnershipDigest', 'temporaryOutputIdentity', 'state'
) 'Edit attempt journal identity'
if ($v2.durableDependencies.nestedManagedSource.maximumDepth -ne 64 -or
    $v2.durableDependencies.nestedManagedSource.maximumVisitedJobs -ne 64 -or
    $v2.durableDependencies.nestedManagedSource.walk -notmatch 'cycle' -or
    $v2.durableDependencies.nestedManagedSource.durableReservation -notmatch
        'before a descendant inbox item becomes visible' -or
    $v2.durableDependencies.nestedManagedSource.deleteGuard -notmatch
        'ancestor output fails closed' -or
    $durableWriter.displayedStaging.retry -notmatch 'Never reopen the external path' -or
    $durableWriter.displayedStaging.delete -notmatch 'exact stage ownership' -or
    $durableWriter.attemptJournal.revision -cne
        'aibos-video-edit-attempt-journal-v1' -or
    $durableWriter.attemptJournal.processOwnership -notmatch
        'PID alone is never ownership proof' -or
    $durableWriter.recoveryInvariants.retry -notmatch 'new attemptId' -or
    $durableWriter.recoveryInvariants.cancel -notmatch 'publish' -or
    $durableWriter.recoveryInvariants.delete -notmatch 'managed descendant' -or
    $v2.productionGate.writerRule -notmatch 'boolean-only flip') {
    throw 'Displayed staging, nested managed dependency, journal, retry, cancel, recovery, or delete invariants changed.'
}
$finishDurableWriter = $v2.finishDurableWriter
Assert-ExactSet @($finishDurableWriter.attemptJournal.states) @(
    'reserved', 'source-validated', 'process-started', 'cancel-requested',
    'process-exited', 'output-validating', 'output-validated', 'publishing',
    'published', 'cleanup-pending', 'complete', 'failed'
) 'Finish attempt journal states'
Assert-ExactSet @($finishDurableWriter.attemptJournal.requiredIdentity) @(
    'journalRevision', 'jobId', 'attemptId', 'presetHash', 'mode', 'scale',
    'backendId', 'backendSetting', 'receiptSetSha256', 'modeReceiptSetSha256',
    'sourceIdentityDigest', 'dependencyClosureDigest',
    'scratchOwnershipDigest', 'temporaryOutputIdentity', 'state'
) 'Finish attempt journal identity'
if ($finishDurableWriter.productionWriterEnabled -ne $false -or
    $finishDurableWriter.attemptJournal.revision -cne
        'aibos-video-finish-attempt-journal-v1' -or
    $finishDurableWriter.attemptJournal.processOwnership -notmatch
        'PID alone is never ownership proof' -or
    $finishDurableWriter.attemptJournal.transitionRule -notmatch
        'mode/setting/scale' -or
    $finishDurableWriter.attemptJournal.publication -notmatch
        'full source PTS digest' -or
    $finishDurableWriter.displayedStaging.retry -notmatch
        'Never reopen the external path' -or
    $finishDurableWriter.recoveryInvariants.retry -notmatch 'new attemptId' -or
    $finishDurableWriter.recoveryInvariants.retry -notmatch
        'never changes mode or scale' -or
    $finishDurableWriter.recoveryInvariants.cancel -notmatch
        'advertised grace interval' -or
    $finishDurableWriter.recoveryInvariants.startupRecovery -notmatch
        'Passive health and Jobs reads never start recovery' -or
    $finishDurableWriter.recoveryInvariants.delete -notmatch
        'managed descendant') {
    throw 'The independent Finish journal, staging, retry, cancel, recovery, or delete invariants changed.'
}
if ($v2.productionGate.currentFinish.productionWriterEnabled -ne $false -or
    $v2.productionGate.currentFinish.readyVariantAdvertised -ne $false -or
    $v2.productionGate.currentFinish.anyModeReadyVariantAdvertised -ne $false -or
    $v2.productionGate.currentFinish.liveCanaryReceiptsPresent -ne $false -or
    $v2.productionGate.currentFinish.qualityAsserted -ne $false -or
    $v2.productionGate.currentFinish.firstFaithfulCandidateBackendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    (@($v2.productionGate.finishActivationConjunction) -join ' ') -notmatch
        'requested fast, standard, or quality mode' -or
    (@($v2.productionGate.finishActivationConjunction) -join ' ') -notmatch
        'bounded O\(1\) frame-streaming' -or
    $v2.productionGate.writerRule -notmatch 'both Edit and Finish' -or
    $v2.productionGate.writerRule -notmatch 'boolean-only flip') {
    throw 'The current closed Finish evidence or future independent activation conjunction changed.'
}
$passive = $v2.healthCapability.passiveRead
foreach ($property in @(
    'canonicalizesSource',
    'opensSource',
    'hashesSource',
    'probesMedia',
    'stagesSource',
    'createsJobs',
    'mutatesJobs',
    'startsProcesses',
    'mountsModels',
    'startsWorker',
    'wakesQueue',
    'claimsJobs',
    'retriesJobs'
)) {
    if ($passive.$property -ne $false) {
        throw "Passive health/Jobs reads may not perform $property."
    }
}
if ($v2.compatibility.unknownMalformedFuture -notmatch 'reader-only' -or
    $v2.lifecycle.retry.reusePersistedSeed -ne $true -or
    $v2.lifecycle.retry.revalidateSourceHashes -ne $true -or
    $v2.lifecycle.cancel.ownershipFailure -cne 'fail-closed' -or
    $v2.lifecycle.delete.ownershipFailure -cne 'fail-closed' -or
    $v2.lifecycle.publish.ownershipFailure -cne 'fail-closed') {
    throw 'Forward compatibility, exact retry, or lifecycle ownership rules are incomplete.'
}
$pinOrder = @($v2.source.explicitActionPinOrder | ForEach-Object { [string]$_ })
$publishIndex = [Array]::FindIndex(
    [string[]]$pinOrder,
    [Predicate[string]] { param($item) $item -match 'atomically publish one durable inbox item' })
$claimIndex = [Array]::FindIndex(
    [string[]]$pinOrder,
    [Predicate[string]] { param($item) $item -match 'Companion atomically claims' })
if ($v2.durableInbox.contractId -cne 'PV-ENHANCE-ENQUEUE-INBOX-001' -or
    $publishIndex -lt 0 -or
    $claimIndex -le $publishIndex -or
    $v2.durableInbox.definitiveRejection -notmatch 'needs-action' -or
    $v2.durableInbox.definitiveRejection -notmatch 'creates no Job' -or
    $v2.durableInbox.needsActionRedelivery -notmatch 'new explicit Start' -or
    $v2.durableInbox.passiveRead -notmatch 'never creates directories') {
    throw 'Durable publication must precede Companion source validation and preserve definitive rejections in needs-action.'
}

$contractEntries = @($index.contracts | Where-Object {
    $_.contractId -ceq 'PV-ENHANCE-VIDEO-TOOLS-002'
})
$fixtureEntries = @($index.fixtures | Where-Object {
    $_.fixtureId -ceq 'PV-ENHANCE-VIDEO-TOOLS-002-READER-FIXTURES'
})
$bundles = @($index.verificationBundles | Where-Object {
    $_.bundleId -ceq 'enhancement-video-tools-v2'
})
if ($contractEntries.Count -ne 1 -or
    $contractEntries[0].path -cne 'contracts/enhancement-video-tools-v2.json' -or
    $contractEntries[0].sha256 -cne $v2Sha256 -or
    $fixtureEntries.Count -ne 1 -or
    $fixtureEntries[0].forContractId -cne 'PV-ENHANCE-VIDEO-TOOLS-002' -or
    $fixtureEntries[0].path -cne 'contracts/fixtures/enhancement-video-tools-v2-reader-v1.json' -or
    $fixtureEntries[0].sha256 -cne $fixtureSha256 -or
    $bundles.Count -ne 1 -or
    $bundles[0].core -cne 'contracts/enhancement-video-tools-v2.json' -or
    @($bundles[0].fixtures).Count -ne 1 -or
    @($bundles[0].fixtures)[0] -cne 'contracts/fixtures/enhancement-video-tools-v2-reader-v1.json') {
    throw 'The exact v2 contract/fixture index entries, hashes, or verification bundle are missing.'
}
if ($fixture.schemaVersion -ne 1 -or
    $fixture.fixtureId -cne 'PV-ENHANCE-VIDEO-TOOLS-002-READER-FIXTURES' -or
    $fixture.forContractId -cne 'PV-ENHANCE-VIDEO-TOOLS-002' -or
    $fixture.compatibility.contractSha256 -cne $v2Sha256 -or
    @($fixture.compatibility.consumers).Count -ne 2 -or
    @($fixture.compatibility.consumers) -notcontains 'public-wpf' -or
    @($fixture.compatibility.consumers) -notcontains 'private-companion' -or
    $fixture.durableInboxVectors.displayedFileDriftAfterPublication.stateAfterDispatch -cne 'needs-action' -or
    $fixture.durableInboxVectors.displayedFileDriftAfterPublication.jobCreated -ne $false -or
    $fixture.durableInboxVectors.displayedFileDriftAfterPublication.oldItemRewritten -ne $false -or
    $fixture.durableInboxVectors.passiveRead.claimsEnvelope -ne $false -or
    $fixture.durableInboxVectors.passiveRead.opensSource -ne $false -or
    $fixture.readerFixtures.edit.job.adapterId -cne
        'bernini-r-1.3b-edit-candidate-v1' -or
    $fixture.requestFixtures.editManaged.videoTools.audioPolicy -cne 'preserve' -or
    $fixture.readerFixtures.edit.video.requested.audioPolicy -cne 'preserve' -or
    $fixture.readerFixtures.edit.video.plan.audioPlan.kind -cne
        'preserve-packets' -or
    $fixture.readerFixtures.edit.video.plan.audioPlan.policy -cne 'preserve' -or
    $fixture.readerFixtures.edit.video.plan.audioPlan.packetStartIndex -ge
        $fixture.readerFixtures.edit.video.plan.audioPlan.packetEndIndexExclusive -or
    $fixture.readerFixtures.edit.video.delivery.audioDelivery.kind -cne
        'remuxed-source-packets' -or
    $fixture.readerFixtures.edit.video.delivery.audioDelivery.policy -cne
        'preserve' -or
    $fixture.readerFixtures.edit.video.delivery.audioDelivery.audioStreamCount -ne 1 -or
    $fixture.readerFixtures.edit.video.delivery.audioDelivery.sourcePacketStartIndex -ge
        $fixture.readerFixtures.edit.video.delivery.audioDelivery.sourcePacketEndIndexExclusive -or
    $fixture.readerFixtures.edit.video.delivery.audioDelivery.sourcePacketPayloadSha256 -notmatch
        '^[0-9a-f]{64}$' -or
    $fixture.readerFixtures.edit.video.delivery.childClip -ne $true -or
    $fixture.readerFixtures.edit.video.delivery.fullSourceSplice -ne $false -or
    $fixture.readerFixtures.edit.video.delivery.frameCount -ne 30 -or
    $fixture.readerFixtures.edit.video.delivery.durationMs -ne 1250 -or
    $fixture.readerFixtures.finish.job.adapterId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $fixture.readerFixtures.finish.video.plan.inputWidth -ne 960 -or
    $fixture.readerFixtures.finish.video.plan.inputHeight -ne 540 -or
    $fixture.readerFixtures.finish.video.plan.outputWidth -ne 1920 -or
    $fixture.readerFixtures.finish.video.plan.outputHeight -ne 1080 -or
    $fixture.readerFixtures.finish.video.plan.backendDeliveryRevision -cne
        'nvidia-vfx-vsr-2x-v1') {
    throw 'The paired public/private v2 reader fixture identity is incomplete.'
}
$stateVectors = $fixture.durableReaderStateVectors
$validStateNames = @('queued', 'running', 'succeeded', 'failed', 'canceled', 'deleted')
$readerOnlyStateNames = @(
    'runningCancellationTransient',
    'succeededMissingOutputSha256',
    'futureSnapshotVersion'
)
foreach ($kind in @('edit', 'finish')) {
    $kindVectors = $stateVectors.$kind
    Assert-ExactSet @($kindVectors.valid.PSObject.Properties.Name) `
        $validStateNames "fixture $kind valid lifecycle vectors"
    $expectedReaderOnly = if ($kind -ceq 'edit') {
        @(
            'runningCancellationTransient',
            'succeededMissingOutputSha256',
            'runningUnicodeRunId',
            'runningInternalWhitespaceRunId',
            'runningExternalPromptId129',
            'runningExternalProcessIdOverflow',
            'extendedYearTimestamp',
            'lossyQueueOrderToken',
            'succeededRelativeOutputPath',
            'succeededWrongJobOutputPath',
            'futureSnapshotVersion'
        )
    }
    else { $readerOnlyStateNames }
    Assert-ExactSet @($kindVectors.readerOnly.PSObject.Properties.Name) `
        $expectedReaderOnly "fixture $kind reader-only lifecycle vectors"
    if ($kindVectors.valid.queued.status -cne 'queued' -or
        $kindVectors.valid.queued.progress -ne 0 -or
        $kindVectors.valid.queued.cancelRequested -ne $false -or
        $kindVectors.valid.running.status -cne 'running' -or
        $kindVectors.valid.running.progress -ne 50 -or
        $kindVectors.valid.running.cancelRequested -ne $false -or
        $kindVectors.valid.succeeded.status -cne 'succeeded' -or
        $kindVectors.valid.succeeded.progress -ne 100 -or
        $kindVectors.valid.succeeded.cancelRequested -ne $false -or
        $kindVectors.valid.succeeded.outputSha256 -notmatch '^[0-9a-f]{64}$' -or
        $kindVectors.valid.succeeded.outputBytes -le 0 -or
        $kindVectors.valid.failed.status -cne 'failed' -or
        $kindVectors.valid.failed.errorCode -notmatch '^[A-Z0-9_]{1,128}$' -or
        $kindVectors.valid.canceled.status -cne 'canceled' -or
        $kindVectors.valid.canceled.cancelRequested -ne $true -or
        $kindVectors.valid.deleted.status -cne 'deleted' -or
        $kindVectors.valid.deleted.progress -ne 100 -or
        $kindVectors.readerOnly.runningCancellationTransient.status -cne 'running' -or
        $kindVectors.readerOnly.runningCancellationTransient.cancelRequested -ne $true -or
        $null -ne $kindVectors.readerOnly.succeededMissingOutputSha256.PSObject.Properties['outputSha256'] -or
        $kindVectors.readerOnly.futureSnapshotVersion.lifecycle.status -cne 'queued' -or
        $kindVectors.readerOnly.futureSnapshotVersion.videoSchemaVersion -ne 99 -or
        $kindVectors.sourceEnvelopeReaderOnly.topLevelSourcePathTransform -cne 'uppercase') {
        throw "The synthetic $kind durable lifecycle fixture vectors are incomplete."
    }
    if ($kind -ceq 'edit' -and (
        $kindVectors.readerOnly.runningUnicodeRunId.runId -notmatch '[^\x00-\x7f]' -or
        $kindVectors.readerOnly.runningInternalWhitespaceRunId.runId -notmatch ' ' -or
        $kindVectors.readerOnly.runningExternalPromptId129.externalPromptId.Length -ne 129 -or
        $kindVectors.readerOnly.runningExternalProcessIdOverflow.externalProcessId -ne 2147483648 -or
        $kindVectors.readerOnly.extendedYearTimestamp.createdAt -notmatch '^\+010000-' -or
        $kindVectors.readerOnly.lossyQueueOrderToken.queueOrder -ne 2 -or
        $kindVectors.readerOnly.succeededRelativeOutputPath.outputPath -cne 'relative.txt' -or
        $kindVectors.readerOnly.succeededWrongJobOutputPath.outputPath -notmatch '^C:\\AibosSynthetic\\Videos\\' -or
        $kindVectors.sourceEnvelopeReaderOnly.snapshotRelativePath -cne 'relative.mp4' -or
        $kindVectors.sourceEnvelopeReaderOnly.snapshotWrongExtension -notmatch '\.mov$' -or
        $kindVectors.sourceEnvelopeReaderOnly.snapshotControlPath -notmatch "`t")) {
        throw 'The paired Video Tools v2 scalar/path drift vectors are incomplete.'
    }
}
if ($stateVectors.passiveReadExpected.rowVisible -ne $true -or
    $stateVectors.passiveReadExpected.mutable -ne $false -or
    $stateVectors.passiveReadExpected.enqueueCalls -ne 0 -or
    $stateVectors.passiveReadExpected.workerWakeCalls -ne 0 -or
    $stateVectors.passiveReadExpected.filesystemCalls -ne 0 -or
    $stateVectors.passiveReadExpected.durableMutationCalls -ne 0) {
    throw 'The durable lifecycle fixture passive-read expectation is incomplete.'
}
$negative = $fixture.editCapabilityNegativeVectors
$booleanFlip = $negative.booleanFlipOnly
Assert-ExactSet @($booleanFlip.capability.PSObject.Properties.Name) @(
    $editCapability.currentDisabledCompatibility.exactKeys
) 'boolean-only Edit capability keys'
Assert-ExactSet @($booleanFlip.missingReadyKeys) @(
    'capabilityRevision', 'resolvedBackend', 'receipts', 'resourceBounds',
    'outputPolicy'
) 'boolean-only missing ready fields'
$unresolved = $negative.shapeCompleteButUnresolvedReceipts
Assert-ExactSet @($unresolved.capability.PSObject.Properties.Name) @(
    $editCapability.ready.exactKeys
) 'unresolved-receipt ready capability keys'
Assert-ExactSet @($unresolved.capability.resolvedBackend.PSObject.Properties.Name) @(
    $editCapability.ready.resolvedBackend.exactKeys
) 'unresolved-receipt backend keys'
Assert-ExactSet @($unresolved.capability.receipts.PSObject.Properties.Name) @(
    $editCapability.ready.receipts.exactKeys
) 'unresolved-receipt receipt keys'
Assert-ExactSet @($unresolved.capability.resourceBounds.PSObject.Properties.Name) @(
    $editCapability.ready.resourceBounds.exactKeys
) 'unresolved-receipt resource keys'
Assert-ExactSet @($unresolved.capability.outputPolicy.PSObject.Properties.Name) @(
    $editCapability.ready.outputPolicy.exactKeys
) 'unresolved-receipt output policy keys'
Assert-ExactSet @($negative.nonV2vBackend.resolvedBackend.PSObject.Properties.Name) @(
    $editCapability.ready.resolvedBackend.exactKeys
) 'non-V2V resolved backend keys'
if ($booleanFlip.capability.writerEnabled -ne $true -or
    $booleanFlip.capability.backendConfigured -ne $true -or
    $booleanFlip.capability.runtimeVerified -ne $true -or
    $booleanFlip.capability.ready -ne $true -or
    $booleanFlip.capability.state -cne 'ready' -or
    $booleanFlip.expectedReady -ne $false -or
    $booleanFlip.expectedEnqueue -ne $false -or
    $booleanFlip.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_EDIT_READY_SHAPE_INCOMPLETE' -or
    $unresolved.receiptResolutionSucceeded -ne $false -or
    $unresolved.evidence -notmatch 'not measured production evidence' -or
    $unresolved.pairedProductionGateEnabled -ne $false -or
    $unresolved.expectedReady -ne $false -or
    $unresolved.expectedEnqueue -ne $false -or
    $unresolved.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_EDIT_RECEIPT_SET_UNVERIFIED' -or
    $negative.nonV2vBackend.resolvedBackend.genuineSourceVideoConditioning -ne
        $false -or
    $negative.nonV2vBackend.resolvedBackend.imageGuideRetake -ne $true -or
    $negative.nonV2vBackend.expectedReady -ne $false -or
    $negative.nonV2vBackend.expectedEnqueue -ne $false -or
    $negative.nonV2vBackend.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_EDIT_BACKEND_NOT_GENUINE_V2V' -or
    $negative.currentClaims.readyVariantAdvertised -ne $false -or
    $negative.currentClaims.liveCanaryReceiptsPresent -ne $false -or
    $negative.currentClaims.productionWriterEnabled -ne $false -or
    $negative.currentClaims.qualityAsserted -ne $false -or
    $negative.currentClaims.firstSemanticCandidateBackendId -cne
        'bernini-r-1.3b-edit-candidate-v1' -or
    $health.finish.ready -ne $false -or
    $health.finishModes.fast.ready -ne $false -or
    $health.finishModes.standard.ready -ne $false -or
    $health.finishModes.quality.ready -ne $false) {
    throw 'Boolean-only, unresolved-receipt, or non-V2V capability vectors must remain closed; Finish must remain independent and disabled.'
}
$finishNegative = $fixture.finishCapabilityNegativeVectors
$finishBooleanFlip = $finishNegative.booleanFlipOnly
$finishModeBooleanFlip = $finishNegative.modeBooleanFlipOnly
$finishUnresolved = $finishNegative.shapeCompleteButUnresolvedReceipts
Assert-ExactSet @($finishBooleanFlip.capability.PSObject.Properties.Name) @(
    $finishCapability.currentDisabledCompatibility.exactKeys
) 'boolean-only Finish capability keys'
Assert-ExactSet @($finishBooleanFlip.missingReadyKeys) @(
    'capabilityRevision', 'resolvedBackend', 'receipts', 'resourceBounds',
    'streamingPolicy', 'outputPolicy'
) 'boolean-only Finish missing ready fields'
Assert-ExactSet @($finishModeBooleanFlip.capability.PSObject.Properties.Name) @(
    $finishModesCapability.currentDisabledCompatibility.exactKeys
) 'boolean-only Finish mode capability keys'
Assert-ExactSet @($finishModeBooleanFlip.missingReadyKeys) @(
    'modeCapabilityRevision', 'mode', 'resolvedBackend', 'receipts',
    'sourceBounds', 'supportedScales', 'deliveryPolicy'
) 'boolean-only Finish mode missing ready fields'
Assert-ExactSet @($finishUnresolved.overallCapability.PSObject.Properties.Name) @(
    $finishCapability.ready.exactKeys
) 'unresolved Finish overall capability keys'
Assert-ExactSet @($finishUnresolved.overallCapability.resolvedBackend.PSObject.Properties.Name) @(
    $finishCapability.ready.resolvedBackend.exactKeys
) 'unresolved Finish backend keys'
Assert-ExactSet @($finishUnresolved.overallCapability.receipts.PSObject.Properties.Name) @(
    $finishCapability.ready.receipts.exactKeys
) 'unresolved Finish receipt keys'
Assert-ExactSet @($finishUnresolved.overallCapability.resourceBounds.PSObject.Properties.Name) @(
    $finishCapability.ready.resourceBounds.exactKeys
) 'unresolved Finish resource keys'
Assert-ExactSet @($finishUnresolved.overallCapability.streamingPolicy.PSObject.Properties.Name) @(
    $finishCapability.ready.streamingPolicy.exactKeys
) 'unresolved Finish streaming keys'
Assert-ExactSet @($finishUnresolved.overallCapability.outputPolicy.PSObject.Properties.Name) @(
    $finishCapability.ready.outputPolicy.exactKeys
) 'unresolved Finish output policy keys'
Assert-ExactSet @($finishUnresolved.modeCapability.PSObject.Properties.Name) @(
    $finishModesCapability.ready.exactKeys
) 'unresolved requested Finish mode capability keys'
Assert-ExactSet @($finishUnresolved.modeCapability.resolvedBackend.PSObject.Properties.Name) @(
    $finishModesCapability.ready.resolvedBackend.exactKeys
) 'unresolved requested Finish mode backend keys'
Assert-ExactSet @($finishUnresolved.modeCapability.receipts.PSObject.Properties.Name) @(
    $finishModesCapability.ready.receipts.exactKeys
) 'unresolved requested Finish mode receipt keys'
Assert-ExactSet @($finishUnresolved.modeCapability.sourceBounds.PSObject.Properties.Name) @(
    $finishModesCapability.ready.sourceBounds.exactKeys
) 'unresolved requested Finish mode source bound keys'
Assert-ExactSet @($finishUnresolved.modeCapability.deliveryPolicy.PSObject.Properties.Name) @(
    $finishModesCapability.ready.deliveryPolicy.exactKeys
) 'unresolved requested Finish mode delivery keys'
Assert-ExactSet @($finishNegative.overallReadyRequestedModeDisabled.requestedModeCapability.PSObject.Properties.Name) @(
    $finishModesCapability.currentDisabledCompatibility.exactKeys
) 'disabled requested Finish mode vector keys'
Assert-ExactSet @($finishNegative.modeReadyOverallDisabled.overallCapability.PSObject.Properties.Name) @(
    $finishCapability.currentDisabledCompatibility.exactKeys
) 'disabled overall Finish vector keys'
if ($finishBooleanFlip.expectedReady -ne $false -or
    $finishBooleanFlip.expectedEnqueue -ne $false -or
    $finishBooleanFlip.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_FINISH_READY_SHAPE_INCOMPLETE' -or
    $finishModeBooleanFlip.requestedMode -cne 'standard' -or
    $finishModeBooleanFlip.expectedReady -ne $false -or
    $finishModeBooleanFlip.expectedEnqueue -ne $false -or
    $finishModeBooleanFlip.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_FINISH_STANDARD_READY_SHAPE_INCOMPLETE' -or
    $finishUnresolved.evidence -notmatch 'not live canary evidence' -or
    $finishUnresolved.requestedMode -cne 'standard' -or
    $finishUnresolved.overallCapability.resolvedBackend.backendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $finishUnresolved.modeCapability.resolvedBackend.backendSetting -cne
        'HIGH' -or
    (@($finishUnresolved.modeCapability.supportedScales) -join ',') -cne
        '2,4' -or
    $finishUnresolved.overallCapability.streamingPolicy.boundedFrameStreaming -ne
        $true -or
    $finishUnresolved.overallCapability.streamingPolicy.fullVideoPtsSequencePreserved -ne
        $true -or
    $finishUnresolved.overallReceiptResolutionSucceeded -ne $false -or
    $finishUnresolved.modeReceiptResolutionSucceeded -ne $false -or
    $finishUnresolved.pairedProductionGateEnabled -ne $false -or
    $finishUnresolved.expectedReady -ne $false -or
    $finishUnresolved.expectedEnqueue -ne $false -or
    $finishUnresolved.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_FINISH_RECEIPT_SET_UNVERIFIED' -or
    $finishNegative.overallReadyRequestedModeDisabled.requestedMode -cne
        'quality' -or
    $finishNegative.overallReadyRequestedModeDisabled.otherReadyModeVector -notmatch
        'modeCapability' -or
    $null -ne
        $finishNegative.overallReadyRequestedModeDisabled.expectedFallbackMode -or
    $finishNegative.overallReadyRequestedModeDisabled.expectedReady -ne $false -or
    $finishNegative.overallReadyRequestedModeDisabled.expectedEnqueue -ne $false -or
    $finishNegative.overallReadyRequestedModeDisabled.expectedReasonCode -cne
        'VIDEO_TOOLS_V2_FINISH_REQUESTED_MODE_NOT_READY' -or
    $finishNegative.modeReadyOverallDisabled.expectedReady -ne $false -or
    $finishNegative.modeReadyOverallDisabled.expectedEnqueue -ne $false -or
    $finishNegative.currentClaims.readyVariantAdvertised -ne $false -or
    $finishNegative.currentClaims.anyModeReadyVariantAdvertised -ne $false -or
    $finishNegative.currentClaims.liveCanaryReceiptsPresent -ne $false -or
    $finishNegative.currentClaims.productionWriterEnabled -ne $false -or
    $finishNegative.currentClaims.qualityAsserted -ne $false -or
    $finishNegative.currentClaims.firstFaithfulCandidateBackendId -cne
        'nvidia-vfx-vsr-1.2-candidate-v1') {
    throw 'Finish boolean-only, unresolved-receipt, overall/mode conjunction, or no-fallback vectors must remain closed.'
}
$previewVector = $fixture.previewProbeVector
Assert-ExactSet @($previewVector.response.source.PSObject.Properties.Name) @(
    'frameCount',
    'fpsNumerator',
    'fpsDenominator',
    'durationMs',
    'width',
    'height'
) 'preview fixture source summary fields'
if ($previewVector.route -cne $compilerTransport.route -or
    $previewVector.method -cne 'POST' -or
    $previewVector.maximumRequestBytes -ne 131072 -or
    $previewVector.maximumResponseBytes -ne 2113536 -or
    $previewVector.request.action -cne 'preview' -or
    $previewVector.response.action -cne 'preview' -or
    (@($previewVector.response.previews.role) -join ',') -cne 'start,middle,end' -or
    (@($previewVector.response.previews.sourceFrame) -join ',') -cne '24,47,71' -or
    $previewVector.expectedEffects.createsJob -ne $false -or
    $previewVector.expectedEffects.publishesDurableInbox -ne $false -or
    $previewVector.expectedEffects.wakesQueue -ne $false -or
    $previewVector.expectedEffects.stagesSource -ne $false -or
    $previewVector.expectedEffects.createsOutput -ne $false) {
    throw 'The exact synthetic explicit-preview fixture routing, ordering, or passive effect boundary changed.'
}
$previewEncodedTotal = 0
foreach ($preview in @($previewVector.response.previews)) {
    Assert-ExactSet @($preview.PSObject.Properties.Name) @(
        $compiler.previewProbe.exactPreviewFields
    ) "preview fixture $($preview.role) fields"
    if ($preview.mime -cne 'image/png' -or
        $preview.width -lt 1 -or
        $preview.height -lt 1 -or
        $preview.width -gt 384 -or
        $preview.height -gt 384 -or
        ($preview.width * $preview.height) -gt 147456 -or
        $preview.encodedBytes -lt 1 -or
        $preview.encodedBytes -gt 524288 -or
        $preview.decodedPixelSha256 -notmatch '^[0-9a-f]{64}$' -or
        $preview.encodedSha256 -notmatch '^[0-9a-f]{64}$' -or
        $preview.decodedPixelSha256 -ceq $preview.encodedSha256 -or
        $preview.base64 -match '^data:') {
        throw "The $($preview.role) preview fixture thumbnail is not exact and bounded."
    }
    try {
        $encoded = [Convert]::FromBase64String([string]$preview.base64)
    } catch {
        throw "The $($preview.role) preview fixture base64 is invalid."
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $encodedHash = $sha256.ComputeHash($encoded)
    } finally {
        $sha256.Dispose()
    }
    $encodedSha256 = -join @($encodedHash | ForEach-Object {
        $_.ToString('x2')
    })
    if ($encoded.Length -ne $preview.encodedBytes -or
        [Convert]::ToBase64String($encoded) -cne $preview.base64 -or
        $encodedSha256 -cne $preview.encodedSha256 -or
        (-join @($encoded[0..7] | ForEach-Object { $_.ToString('X2') })) -cne
            '89504E470D0A1A0A') {
        throw "The $($preview.role) preview fixture PNG bytes or digest are invalid."
    }
    $previewEncodedTotal += $encoded.Length
}
if ($previewEncodedTotal -gt 1572864) {
    throw 'The preview fixture exceeds the exact total encoded PNG byte limit.'
}
Assert-ExactSet @($fixture.audioPolicyVectors.mute.plan.PSObject.Properties.Name) @(
    $audioPlanVariants.mute.exactKeys
) 'mute fixture plan keys'
Assert-ExactSet @($fixture.audioPolicyVectors.mute.delivery.PSObject.Properties.Name) @(
    $audioDeliveryVariants.'no-audio-muted'.exactKeys
) 'mute fixture delivery keys'
Assert-ExactSet @($fixture.audioPolicyVectors.preserveNoPackets.plan.PSObject.Properties.Name) @(
    $audioPlanVariants.'preserve-no-packets'.exactKeys
) 'preserve-no-packets fixture plan keys'
Assert-ExactSet @($fixture.audioPolicyVectors.preserveNoPackets.delivery.PSObject.Properties.Name) @(
    $audioDeliveryVariants.'no-audio-preserve-empty'.exactKeys
) 'preserve-no-packets fixture delivery keys'
if ($fixture.audioPolicyVectors.mute.requestValue -cne 'mute' -or
    $fixture.audioPolicyVectors.mute.plan.kind -cne 'mute' -or
    $fixture.audioPolicyVectors.mute.delivery.kind -cne 'no-audio-muted' -or
    $fixture.audioPolicyVectors.mute.delivery.audioStreamCount -ne 0 -or
    $fixture.audioPolicyVectors.preserveNoPackets.requestValue -cne 'preserve' -or
    $fixture.audioPolicyVectors.preserveNoPackets.plan.kind -cne
        'preserve-no-packets' -or
    $fixture.audioPolicyVectors.preserveNoPackets.delivery.kind -cne
        'no-audio-preserve-empty' -or
    $fixture.audioPolicyVectors.preserveNoPackets.delivery.audioStreamCount -ne 0) {
    throw 'Mute and preserve-without-packets fixtures must emit no audio and no fabricated packet identity.'
}
$contractHealthJson = $v2.healthCapability.exactShape |
    ConvertTo-Json -Depth 30 -Compress
$fixtureHealthJson = $fixture.health.capabilities.videoToolsV2 |
    ConvertTo-Json -Depth 30 -Compress
if ($contractHealthJson -cne $fixtureHealthJson) {
    throw 'The paired health fixture does not exactly match the v2 health contract.'
}

[pscustomobject]@{
    ok = $true
    contractId = 'PV-ENHANCE-VIDEO-TOOLS-002'
    protocol = 'aibos-enhancement-video-tools-v2'
    v1Sha256 = $v1Sha256
    v2Sha256 = $v2Sha256
    fixtureSha256 = $fixtureSha256
    productionWriterEnabled = $false
    passiveRead = $true
    pairedFixture = $true
} | ConvertTo-Json -Depth 4
