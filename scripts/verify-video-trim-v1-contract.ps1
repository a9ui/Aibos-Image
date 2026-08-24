[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$indexPath = Join-Path $repoRoot 'contracts\index.json'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-trim-v1.json'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-trim-v1-reader-v1.json'

function Read-ExactJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Video Trim v1 artifact was not found: $Path"
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

function Assert-ExactPropertySet {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    Assert-ExactSet @($Value.PSObject.Properties.Name) $Expected $Name
}

function Get-SequentialPtsSha256 {
    param([Parameter(Mandatory = $true)][int64]$Count)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [byte[]]::new(8)
        for ($pts = 0L; $pts -lt $Count; $pts++) {
            [Buffers.Binary.BinaryPrimitives]::WriteInt64BigEndian($bytes, $pts)
            $null = $sha256.TransformBlock($bytes, 0, 8, $null, 0)
        }
        $null = $sha256.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return ([BitConverter]::ToString($sha256.Hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

$index = Read-ExactJson $indexPath
$contract = Read-ExactJson $contractPath
$fixture = Read-ExactJson $fixturePath
$contractSha256 = Get-LowerSha256 $contractPath
$fixtureSha256 = Get-LowerSha256 $fixturePath

$contractEntries = @($index.contracts | Where-Object {
    $_.contractId -ceq 'PV-ENHANCE-VIDEO-TRIM-001'
})
$fixtureEntries = @($index.fixtures | Where-Object {
    $_.fixtureId -ceq 'PV-ENHANCE-VIDEO-TRIM-001-READER-FIXTURES'
})
$bundleEntries = @($index.verificationBundles | Where-Object {
    $_.bundleId -ceq 'enhancement-video-trim-v1'
})
if ($contractEntries.Count -ne 1 -or
    $fixtureEntries.Count -ne 1 -or
    $bundleEntries.Count -ne 1 -or
    $contractEntries[0].path -cne 'contracts/enhancement-video-trim-v1.json' -or
    $fixtureEntries[0].path -cne 'contracts/fixtures/enhancement-video-trim-v1-reader-v1.json' -or
    $bundleEntries[0].core -cne 'contracts/enhancement-video-trim-v1.json' -or
    @($bundleEntries[0].fixtures).Count -ne 1 -or
    [string]@($bundleEntries[0].fixtures)[0] -cne
        'contracts/fixtures/enhancement-video-trim-v1-reader-v1.json' -or
    $contractEntries[0].sha256 -cne $contractSha256 -or
    $fixtureEntries[0].sha256 -cne $fixtureSha256 -or
    $fixture.compatibility.contractSha256 -cne $contractSha256) {
    throw 'Video Trim v1 index paths, hashes, bundle, or fixture compatibility do not match.'
}

if ($contract.schemaVersion -ne 1 -or
    $contract.contractId -cne 'PV-ENHANCE-VIDEO-TRIM-001' -or
    $contract.protocol -cne 'aibos-enhancement-video-trim-v1' -or
    $contract.operation -cne 'video' -or
    $contract.mediaKind -cne 'video' -or
    $contract.wire.topLevelClaim -cne 'videoTrim' -or
    $fixture.schemaVersion -ne 1 -or
    $fixture.fixtureId -cne 'PV-ENHANCE-VIDEO-TRIM-001-READER-FIXTURES' -or
    $fixture.forContractId -cne $contract.contractId) {
    throw 'Video Trim v1 identity or top-level wire discriminator is incomplete.'
}

Assert-ExactSet @($contract.wire.exactEnvelopeFields) @(
    'operation', 'mediaKind', 'videoTrim'
) 'wire envelope fields'
Assert-ExactSet @($contract.request.exactVideoTrimFields) @(
    'schemaVersion', 'source', 'selection', 'audioPolicy'
) 'videoTrim request fields'
Assert-ExactSet @($contract.request.sourceSelector.exactKinds) @(
    'managed-video-job', 'displayed-file'
) 'source selector kinds'
Assert-ExactSet @($contract.request.sourceSelector.'managed-video-job'.exactFields) @(
    'kind', 'sourceVideoJobId'
) 'managed source selector fields'
Assert-ExactSet @($contract.request.sourceSelector.'displayed-file'.exactFields) @(
    'kind', 'path', 'size', 'mtimeMs', 'sha256'
) 'displayed-file selector fields'
Assert-ExactSet @($contract.request.selection.exactFields) @(
    'startFrame', 'endFrameExclusive'
) 'selection fields'
Assert-ExactSet @($contract.request.audioPolicy.exactValues) @(
    'preserve', 'mute'
) 'audio policy values'
Assert-ExactSet @($contract.request.clientForbiddenFields) @(
    'prompt',
    'instructionJa',
    'compiled',
    'compiler',
    'style',
    'styleId',
    'steps',
    'seed',
    'backend',
    'backendId',
    'model',
    'modelId',
    'gpu',
    'gpuLease',
    'maximumPixelArea',
    'strength',
    'denoise'
) 'forbidden AI request fields'

$source = $contract.validation.source
if ($source.maximumBytes -ne 536870912 -or
    $source.maximumDurationMs -ne 300000 -or
    $source.maximumWidth -ne 1920 -or
    $source.maximumHeight -ne 1080 -or
    $source.maximumPixelArea -ne 2073600 -or
    $source.maximumFrames -ne 18000 -or
    $source.container -cne 'mp4' -or
    $source.videoCodec -cne 'h264' -or
    $source.pixelFormat -cne 'yuv420p' -or
    $source.bitDepth -ne 8 -or
    $source.dynamicRange -cne 'SDR' -or
    $source.videoStreamCount -ne 1 -or
    $source.maximumAudioStreamCount -ne 1 -or
    $source.extraStreamCount -ne 0) {
    throw 'Video Trim v1 source bounds or exact source stream policy changed.'
}
Assert-ExactSet @($source.allowedFps) @('24/1', '30/1', '60/1') 'allowed source fps'

$selection = $contract.validation.selection
if ($selection.interval -cne 'half-open [startFrame,endFrameExclusive)' -or
    $selection.minimumFrames -ne 1 -or
    $selection.maximumFrames -ne 18000 -or
    $selection.maximumDurationMs -ne 300000 -or
    $selection.allowsSelectionsLongerThan15Seconds -ne $true -or
    $selection.requiresSourceContainment -ne $true) {
    throw 'Video Trim v1 exact half-open selection bounds changed.'
}

$output = $contract.output
if ($output.kind -cne 'new-managed-child' -or
    $output.container -cne 'mp4' -or
    $output.videoCodec -cne 'h264' -or
    $output.pixelFormat -cne 'yuv420p' -or
    $output.bitDepth -ne 8 -or
    $output.dynamicRange -cne 'SDR' -or
    $output.videoStreamCount -ne 1 -or
    $output.maximumAudioStreamCount -ne 1 -or
    $output.extraStreamCount -ne 0 -or
    $output.maximumBytes -ne 536870912 -or
    $output.sourceMutationAllowed -ne $false -or
    $output.video.frameCount -cne 'endFrameExclusive - startFrame' -or
    $output.video.fps -cne 'exact source rational fps' -or
    $output.video.firstRelativePts -ne 0 -or
    $output.video.ptsDigestAlgorithm -cne 'SHA-256 over ordered signed int64 big-endian PTS values' -or
    $output.video.requiresVideoReencode -ne $true -or
    $output.video.keyframeOnlyStreamCopyAllowed -ne $false -or
    $output.audio.preserve.codec -cne 'aac' -or
    $output.audio.preserve.requiresReencode -ne $true -or
    $output.audio.preserve.packetBitIdentityClaimed -ne $false -or
    $output.audio.preserve.sampleExactClaimed -ne $false -or
    $output.audio.mute.audioStreamCount -ne 0) {
    throw 'Video Trim v1 exact child MP4, frame delivery, or audio policy changed.'
}
Assert-ExactSet @($output.forbiddenStreamTypes) @(
    'subtitle', 'data', 'attachment', 'unknown'
) 'forbidden output stream types'

$resource = $contract.execution.resourceBounds
if ($contract.execution.lane -cne 'cpu-video' -or
    $resource.maximumConcurrentJobs -ne 1 -or
    $resource.maximumGpuLeases -ne 0 -or
    $resource.maximumModelMounts -ne 0 -or
    $resource.maximumArgvEntries -ne 96 -or
    $resource.maximumArgvEntryBytes -ne 4096 -or
    $resource.maximumArgvBytes -ne 32768 -or
    $resource.maximumCapturedStderrBytes -ne 1048576 -or
    $resource.maximumHostRamBytes -ne 4294967296 -or
    $resource.maximumScratchBytes -ne 2147483648 -or
    $resource.maximumOutputBytes -ne 536870912 -or
    $resource.processTimeoutMs -ne 3600000 -or
    $resource.cancelGraceMs -ne 30000 -or
    $contract.execution.process.argv -cne 'bounded argument array; never shell text' -or
    $contract.execution.process.cancel -notmatch 'proven process identity') {
    throw 'Video Trim v1 CPU lane or bounded process resources changed.'
}
Assert-ExactSet @($contract.execution.process.sealedExecutables) @(
    'ffmpeg', 'ffprobe'
) 'sealed process executable roles'

$passive = $contract.capability.passiveRead
foreach ($property in $passive.PSObject.Properties) {
    if ($property.Value -ne $false) {
        throw "Passive Video Trim read side effect became enabled: $($property.Name)"
    }
}
$current = $contract.capability.current
Assert-ExactPropertySet $current @(
    'contractId', 'protocol', 'readerReady', 'writerEnabled',
    'runtimeVerified', 'ready', 'state', 'reasonCode'
) 'current capability fields'
if ($current.readerReady -ne $true -or
    $current.writerEnabled -ne $false -or
    $current.runtimeVerified -ne $false -or
    $current.ready -ne $false -or
    $current.state -cne 'disabled' -or
    $contract.productionGate.productionWriterEnabled -ne $false -or
    $contract.productionGate.readyVariantAdvertised -ne $false -or
    $contract.productionGate.liveReceiptsPresent -ne $false -or
    $contract.productionGate.qualityAsserted -ne $false) {
    throw 'Current Video Trim writer, runtime, ready, receipt, or quality gate opened.'
}

$future = $contract.capability.futureReadyVariant
Assert-ExactSet @($future.exactFields) @(
    'contractId', 'protocol', 'readerReady', 'writerEnabled',
    'runtimeVerified', 'ready', 'state', 'reasonCode',
    'capabilityRevision', 'resolvedRuntime', 'receipts',
    'resourceBounds', 'outputPolicy'
) 'future ready capability fields'
Assert-ExactSet @($future.receipts.exactFields) @(
    'ffmpegReceiptId', 'ffprobeReceiptId', 'runtimeReceiptId',
    'qualityReceiptId', 'resourceReceiptId', 'cancelReceiptId',
    'recoveryReceiptId', 'outputReceiptId', 'receiptSetSha256'
) 'future ready receipt fields'
Assert-ExactSet @($future.activation.requireAll) @(
    'pairedPublicPrivateRevision',
    'sealedFfmpegReceipt',
    'sealedFfprobeReceipt',
    'sealedRuntimeReceipt',
    'qualityCanaryReceipt',
    'resourceCanaryReceipt',
    'cancelCanaryReceipt',
    'recoveryCanaryReceipt',
    'outputValidatorReceipt'
) 'future ready activation conjunction'

$lifecycle = $contract.durableLifecycle
if ($lifecycle.idempotency -notmatch 'exact committed request' -or
    $lifecycle.sourceOwnership -notmatch 'source is never') {
    throw 'Video Trim idempotency or source ownership rule is incomplete.'
}
Assert-ExactSet @($lifecycle.attemptJournal.states) @(
    'reserved', 'source-validated', 'process-started', 'cancel-requested',
    'process-exited', 'output-validating', 'output-validated', 'publishing',
    'published', 'cleanup-pending', 'complete', 'failed'
) 'trim attempt journal states'
foreach ($name in @('retry', 'cancel', 'delete', 'recovery', 'publish')) {
    if ([string]::IsNullOrWhiteSpace([string]$lifecycle.$name)) {
        throw "Video Trim durable lifecycle rule is missing: $name"
    }
}
if ($contract.compatibility.unknownMalformedFuture -notmatch 'reader-only' -or
    $contract.compatibility.videoToolsV2.kindTrimAccepted -ne $false -or
    $contract.compatibility.videoToolsV2.rule -notmatch 'reject') {
    throw 'Unknown/future read-only behavior or Video Tools v2 trim rejection changed.'
}

$currentFixture = $fixture.currentHealth.capabilities.videoTrimV1
Assert-ExactPropertySet $currentFixture @(
    'contractId', 'protocol', 'readerReady', 'writerEnabled',
    'runtimeVerified', 'ready', 'state', 'reasonCode'
) 'fixture current capability fields'
if (($currentFixture | ConvertTo-Json -Depth 20 -Compress) -cne
    ($current | ConvertTo-Json -Depth 20 -Compress) -or
    $fixture.currentHealth.expectedPassiveMutationRequests -ne 0) {
    throw 'Current health fixture drifted from the disabled passive capability.'
}

$readyFixture = $fixture.futureReadyShape.capability
Assert-ExactPropertySet $readyFixture @(
    'contractId', 'protocol', 'readerReady', 'writerEnabled',
    'runtimeVerified', 'ready', 'state', 'reasonCode',
    'capabilityRevision', 'resolvedRuntime', 'receipts',
    'resourceBounds', 'outputPolicy'
) 'fixture future ready fields'
if ($fixture.futureReadyShape.receiptResolutionSucceeded -ne $false -or
    $fixture.futureReadyShape.pairedProductionGateEnabled -ne $false -or
    $fixture.futureReadyShape.expectedReady -ne $false -or
    $fixture.futureReadyShape.expectedEnqueue -ne $false) {
    throw 'Synthetic future ready shape must not assert production activation.'
}

$long = $fixture.longSelectionVector
Assert-ExactPropertySet $long.request @('operation', 'mediaKind', 'videoTrim') 'long vector envelope fields'
Assert-ExactPropertySet $long.request.videoTrim @(
    'schemaVersion', 'source', 'selection', 'audioPolicy'
) 'long vector videoTrim fields'
Assert-ExactPropertySet $long.request.videoTrim.selection @(
    'startFrame', 'endFrameExclusive'
) 'long vector selection fields'
$selectedFrames = [int64]$long.request.videoTrim.selection.endFrameExclusive -
    [int64]$long.request.videoTrim.selection.startFrame
$expectedPtsSha256 = Get-SequentialPtsSha256 $selectedFrames
if ($long.sourceProbe.frameCount -ne 18000 -or
    $long.sourceProbe.fps -cne '60/1' -or
    $long.sourceProbe.durationMs -ne 300000 -or
    $long.request.videoTrim.selection.startFrame -ne 600 -or
    $long.request.videoTrim.selection.endFrameExclusive -ne 18000 -or
    $selectedFrames -ne 17400 -or
    $long.expectedDelivery.selectedDurationMs -ne 290000 -or
    $long.expectedDelivery.selectedDurationMs -le 15000 -or
    $long.expectedDelivery.frameCount -ne $selectedFrames -or
    $long.expectedDelivery.fps -cne '60/1' -or
    $long.expectedDelivery.firstRelativePts -ne 0 -or
    $long.expectedDelivery.lastRelativePts -ne 17399 -or
    $long.expectedDelivery.ptsSha256 -cne $expectedPtsSha256 -or
    $long.expectedDelivery.audioCodec -cne 'aac' -or
    $long.expectedDelivery.packetBitIdentityClaimed -ne $false -or
    $long.expectedDelivery.sampleExactClaimed -ne $false -or
    $long.expectedMutationRequests -ne 0) {
    throw 'The synthetic long-selection vector no longer proves a bounded >15 second exact-frame trim.'
}

if ($fixture.negativeVectors.videoToolsV2KindTrim.expectedAccepted -ne $false -or
    $fixture.negativeVectors.videoToolsV2KindTrim.expectedWriterAction -cne 'reject' -or
    $fixture.negativeVectors.malformedSelection.expectedWriterAction -cne 'reject' -or
    $fixture.negativeVectors.futureSchema.expectedMode -cne 'reader-only' -or
    $fixture.negativeVectors.forbiddenAiFields.expectedWriterAction -cne 'reject') {
    throw 'Video Trim v1 negative reader/writer vectors changed.'
}

[pscustomobject][ordered]@{
    ok = $true
    contractId = $contract.contractId
    contractSha256 = $contractSha256
    fixtureSha256 = $fixtureSha256
    selectedFrames = $selectedFrames
    selectedDurationMs = $long.expectedDelivery.selectedDurationMs
    currentWriterEnabled = $current.writerEnabled
    currentReady = $current.ready
    passiveMutationRequests = $fixture.currentHealth.expectedPassiveMutationRequests
} | ConvertTo-Json -Depth 5
