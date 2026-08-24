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
$fixture = Read-ExactJson $fixturePath
$index = Read-ExactJson $indexPath
$v2Sha256 = Get-LowerSha256 $v2Path
$fixtureSha256 = Get-LowerSha256 $fixturePath

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
    'steps',
    'strength',
    'maximumPixelArea'
) 'edit request fields'
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
Assert-ExactSet @($compiler.candidate.exactFields) @(
    'backendPrompt', 'summaryJa', 'compilerRevision', 'contextDigest'
) 'prompt compiler candidate fields'
if ($compiler.labelJa -cne '指示を整える' -or
    $compiler.action -cne 'explicit authenticated Edit action' -or
    $compiler.previewProbe.labelJa -cne 'フレームを読み込む' -or
    $compiler.previewProbe.action -cne
        'explicit authenticated transient source-preview action' -or
    $compiler.previewProbe.effects.createsJob -ne $false -or
    $compiler.previewProbe.effects.publishesDurableInbox -ne $false -or
    $compiler.previewProbe.effects.wakesQueue -ne $false -or
    $compiler.previewProbe.effects.stagesSource -ne $false -or
    $compiler.previewProbe.effects.createsOutput -ne $false -or
    $compiler.previewProbe.gate -notmatch 'stay disabled' -or
    $compiler.previewProbe.fpsRule -notmatch 'Never assume 24 fps' -or
    $compiler.previewProbe.managedVideoJob -notmatch 'persisted source probe' -or
    $compiler.effects.mayBindCanonicalizeHashAndProbeSelectedSource -ne $true -or
    $compiler.effects.mayDecodeBoundedPreviewFrames -ne $true -or
    $compiler.effects.mayInvokeLocalCompiler -ne $true -or
    $compiler.effects.stagesSource -ne $false -or
    $compiler.effects.createsJob -ne $false -or
    $compiler.effects.publishesDurableInbox -ne $false -or
    $compiler.effects.wakesQueue -ne $false -or
    $compiler.effects.createsOutput -ne $false -or
    $compiler.candidate.transient -ne $true -or
    $compiler.candidate.durableBeforeStart -ne $false -or
    $compiler.reviewUnchecked -notmatch 'separate explicit Start' -or
    $compiler.reviewSkipChecked -notmatch 'single-use authorization' -or
    $compiler.reviewSkipChecked -notmatch 'response by itself never' -or
    $compiler.startRevalidation -notmatch 'no durable inbox publication' -or
    $compiler.passive -notmatch 'never bind' -or
    $v2.featureIsolation.nonAiTrim -notmatch 'separately versioned' -or
    $v2.featureIsolation.nonAiTrim -notmatch 'not an Edit or Finish kind') {
    throw 'The explicit transient prompt-compiler flow or separate non-AI trim boundary changed.'
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

$editCandidates = $v2.editBackendCandidates
Assert-ExactSet @($v2.persistedSnapshot.stableIds.editCandidateBackendIds) @(
    'wan-vace-1.3b-edit-candidate-v1',
    'bernini-r-1.3b-edit-candidate-v1',
    'minimax-h3-masked-edit-research-v1'
) 'persisted Edit candidate backend IDs'
if ($editCandidates.status -cne 'candidate-canary-required' -or
    $editCandidates.candidatePaths.'semantic-v2v'.backendId -cne
        'bernini-r-1.3b-edit-candidate-v1' -or
    $editCandidates.candidatePaths.'precise-mask'.backendId -cne
        'wan-vace-1.3b-edit-candidate-v1' -or
    $editCandidates.candidatePaths.'precise-mask'.readyWithoutMaskContract -ne $false -or
    $editCandidates.candidatePaths.research.productionCandidate -ne $false -or
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
Assert-ExactSet @($faithfulCandidate.serverQualityValues) @('MEDIUM', 'HIGH', 'ULTRA') 'NVIDIA faithful candidate values'
if ($finishCandidates.status -cne 'candidate-canary-required' -or
    $faithfulCandidate.backendId -cne 'nvidia-vfx-vsr-1.2-candidate-v1' -or
    $faithfulCandidate.package -cne 'nvidia-vfx 0.1.0.1' -or
    $faithfulCandidate.internalSdkVersion -cne '1.2.0.0' -or
    $faithfulCandidate.frameIndependentAsserted -ne $false -or
    @($faithfulCandidate.supportedScalesBeforeCanary).Count -ne 0 -or
    $detailCandidate.backendId -cne 'seedvr2-3b-detail-candidate-v1' -or
    $detailCandidate.semanticRole -cne 'generative-detail' -or
    @($detailCandidate.supportedScalesBeforeCanary).Count -ne 0 -or
    $lightCandidate.backendId -cne 'nanovsr-1.7m-4x-candidate-v1' -or
    $lightCandidate.nativeScale -ne 4 -or
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
