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

function Get-StridedPtsSha256 {
    param(
        [Parameter(Mandatory = $true)][int64]$Count,
        [Parameter(Mandatory = $true)][int64]$Start,
        [Parameter(Mandatory = $true)][int64]$Step
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [byte[]]::new(8)
        for ($index = 0L; $index -lt $Count; $index++) {
            [int64]$pts = $Start + ($index * $Step)
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

function ConvertTo-StableJsonValue {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [pscustomobject]) {
        [string[]]$names = @($Value.PSObject.Properties.Name)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        $ordered = [ordered]@{}
        foreach ($name in $names) {
            $ordered[$name] = ConvertTo-StableJsonValue $Value.$name
        }
        return [pscustomobject]$ordered
    }
    if ($Value -is [Array]) {
        [object[]]$items = @($Value | ForEach-Object {
            ConvertTo-StableJsonValue $_
        })
        return ,$items
    }
    return $Value
}

function Get-StableJsonSha256 {
    param([Parameter(Mandatory = $true)][object]$Value)

    $stable = ConvertTo-StableJsonValue $Value
    $json = $stable | ConvertTo-Json -Depth 100 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
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

$inspection = $contract.sourceInspection
if ($inspection.revision -cne 'aibos-video-trim-source-inspection-v1' -or
    $inspection.transport.route -cne
        '/api/enhance/video-trim/v1/source-inspection' -or
    $inspection.transport.method -cne 'POST' -or
    $inspection.transport.maximumRequestBytes -ne 131072 -or
    $inspection.transport.maximumResponseBytesByAction.probe -ne 131072 -or
    $inspection.transport.maximumResponseBytesByAction.preview -ne 2113536 -or
    $inspection.transport.routing -notmatch 'authenticated encrypted' -or
    $inspection.transport.unknownFields -cne 'reject' -or
    $inspection.aiSeparation -notmatch 'never accepts') {
    throw 'Video Trim v1 explicit authenticated inspection transport is incomplete.'
}
Assert-ExactSet @($inspection.transport.exactActions) @(
    'probe', 'preview'
) 'inspection actions'
Assert-ExactSet @($inspection.transport.error.exactFields) @(
    'code', 'message', 'retryable'
) 'inspection error response fields'
$expectedInspectionErrors = @{
    400 = @('VIDEO_TRIM_V1_INSPECTION_INVALID_REQUEST', $false)
    409 = @('VIDEO_TRIM_V1_INSPECTION_SOURCE_STALE', $false)
    413 = @('VIDEO_TRIM_V1_INSPECTION_REQUEST_TOO_LARGE', $false)
    415 = @('VIDEO_TRIM_V1_INSPECTION_SOURCE_UNSUPPORTED', $false)
    422 = @('VIDEO_TRIM_V1_INSPECTION_SELECTION_INVALID', $false)
    429 = @('VIDEO_TRIM_V1_INSPECTION_BUSY', $true)
    500 = @('VIDEO_TRIM_V1_INSPECTION_FAILED', $true)
    503 = @('VIDEO_TRIM_V1_SOURCE_INSPECTION_NOT_READY', $false)
    504 = @('VIDEO_TRIM_V1_INSPECTION_TIMEOUT', $true)
}
$inspectionErrorMappings = @($inspection.transport.error.exactMappings)
if ($inspection.transport.error.maximumMessageCharacters -ne 512 -or
    $inspectionErrorMappings.Count -ne $expectedInspectionErrors.Count) {
    throw 'Video Trim inspection error bounds or exact mapping count changed.'
}
foreach ($mapping in $inspectionErrorMappings) {
    Assert-ExactPropertySet $mapping @('status', 'code', 'retryable') `
        "inspection error mapping $($mapping.status)"
    if (-not $expectedInspectionErrors.Contains([int]$mapping.status)) {
        throw "Unexpected Video Trim inspection error status: $($mapping.status)"
    }
    $expectedError = $expectedInspectionErrors[[int]$mapping.status]
    if ($mapping.code -cne $expectedError[0] -or
        $mapping.retryable -ne $expectedError[1]) {
        throw "Video Trim inspection error mapping $($mapping.status) drifted."
    }
}
Assert-ExactSet @($inspection.probe.exactRequestFields) @(
    'action', 'source'
) 'inspection probe request fields'
Assert-ExactSet @($inspection.probe.exactResponseFields) @(
    'action', 'protocol', 'inspectionRevision', 'source'
) 'inspection probe response fields'
Assert-ExactSet @($inspection.sourceSummary.exactFields) @(
    'container', 'videoCodec', 'pixelFormat', 'bitDepth', 'dynamicRange',
    'width', 'height', 'frameCount', 'fpsNumerator', 'fpsDenominator',
    'durationMs', 'durationNumerator', 'durationDenominator',
    'videoStreamCount', 'audioStreamCount', 'extraStreamCount',
    'videoTimeBaseNumerator', 'videoTimeBaseDenominator',
    'videoStartTimestamp', 'videoPtsSha256', 'probeDigest',
    'sourceIdentityDigest'
) 'inspection source summary fields'
Assert-ExactSet @($inspection.preview.exactRequestFields) @(
    'action', 'source', 'sourceIdentityDigest', 'selection', 'frames'
) 'inspection preview request fields'
Assert-ExactSet @($inspection.preview.exactResponseFields) @(
    'action', 'protocol', 'inspectionRevision', 'sourceIdentityDigest',
    'selection', 'previews'
) 'inspection preview response fields'
Assert-ExactSet @($inspection.preview.selectionFields) @(
    'startFrame', 'endFrameExclusive'
) 'inspection preview selection fields'
Assert-ExactSet @($inspection.preview.frames.orderedMeaning) @(
    'startFrame', 'middleFrame', 'endFrame'
) 'inspection ordered frame meanings'
Assert-ExactSet @($inspection.preview.exactPreviewFields) @(
    'role', 'sourceFrame', 'sourcePts', 'decodedPixelSha256',
    'decoderRevision', 'mime', 'width', 'height', 'encodedBytes',
    'encodedSha256', 'base64'
) 'inspection preview payload fields'
Assert-ExactSet @($inspection.preview.thumbnail.orderedRoles) @(
    'start', 'middle', 'end'
) 'inspection preview roles'
if ($inspection.preview.frames.exactCount -ne 3 -or
    $inspection.preview.frames.rule -notmatch 'strictly increasing' -or
    $inspection.preview.frames.rule -notmatch 'at least three' -or
    $inspection.preview.thumbnail.exactCount -ne 3 -or
    $inspection.preview.thumbnail.mime -cne 'image/png' -or
    $inspection.preview.thumbnail.maximumEdge -ne 384 -or
    $inspection.preview.thumbnail.maximumPixels -ne 147456 -or
    $inspection.preview.thumbnail.maximumEncodedBytesEach -ne 524288 -or
    $inspection.preview.thumbnail.maximumEncodedBytesTotal -ne 1572864 -or
    $inspection.preview.thumbnail.authority -notmatch 'never replace') {
    throw 'Video Trim v1 selected start/middle/end preview bounds changed.'
}

$inspectionEffectNames = @(
    'createsJobs', 'publishesDurableInbox', 'mutatesInbox', 'stagesSource',
    'mutatesJobs', 'wakesQueue', 'claimsJobs', 'startsWorker',
    'acquiresCpuJobLane', 'acquiresGpuLease', 'mountsModels',
    'createsOutput', 'persistsPreviews'
)
Assert-ExactPropertySet $inspection.effects $inspectionEffectNames `
    'inspection effect fields'
foreach ($property in $inspection.effects.PSObject.Properties) {
    if ($property.Value -ne $false) {
        throw "Video Trim inspection durable effect became enabled: $($property.Name)"
    }
}
if ($inspection.process.maximumConcurrentInspections -ne 1 -or
    $inspection.process.maximumArgvEntries -ne 64 -or
    $inspection.process.maximumArgvEntryBytes -ne 4096 -or
    $inspection.process.maximumArgvBytes -ne 24576 -or
    $inspection.process.maximumCapturedStdoutBytes -ne 8388608 -or
    $inspection.process.maximumCapturedStderrBytes -ne 1048576 -or
    $inspection.process.processTimeoutMs -ne 30000 -or
    $inspection.availability.currentProductionEnabled -ne $false -or
    $inspection.availability.currentResult.status -ne 503 -or
    $inspection.availability.currentResult.code -cne
        'VIDEO_TRIM_V1_SOURCE_INSPECTION_NOT_READY') {
    throw 'Video Trim inspection resource or current disabled gate changed.'
}
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
    $contract.productionGate.sourceInspectionEnabled -ne $false -or
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

$inspectionFixture = $fixture.sourceInspectionVectors
if ($inspectionFixture.currentProduction.enabled -ne $false -or
    $inspectionFixture.currentProduction.status -ne 503 -or
    $inspectionFixture.currentProduction.code -cne
        'VIDEO_TRIM_V1_SOURCE_INSPECTION_NOT_READY' -or
    $inspectionFixture.currentProduction.expectedMutationRequests -ne 0) {
    throw 'The current source-inspection fixture opened production behavior.'
}
$probe = $inspectionFixture.probe
Assert-ExactPropertySet $probe.request @('action', 'source') `
    'inspection probe fixture request'
Assert-ExactPropertySet $probe.response @(
    'action', 'protocol', 'inspectionRevision', 'source'
) 'inspection probe fixture response'
Assert-ExactPropertySet $probe.response.source `
    @($inspection.sourceSummary.exactFields) `
    'inspection probe fixture source summary'
if ($probe.route -cne $inspection.transport.route -or
    $probe.method -cne 'POST' -or
    $probe.maximumRequestBytes -ne 131072 -or
    $probe.maximumResponseBytes -ne 131072 -or
    $probe.request.action -cne 'probe' -or
    $probe.response.action -cne 'probe' -or
    $probe.response.protocol -cne $contract.protocol -or
    $probe.response.inspectionRevision -cne $inspection.revision) {
    throw 'The exact source-inspection probe vector drifted from transport.'
}
$probeSource = $probe.response.source
$expectedSourcePtsSha256 = Get-StridedPtsSha256 240 0 1000
if ($probeSource.frameCount -ne 240 -or
    $probeSource.fpsNumerator -ne 24 -or
    $probeSource.fpsDenominator -ne 1 -or
    $probeSource.durationNumerator -ne 10 -or
    $probeSource.durationDenominator -ne 1 -or
    $probeSource.durationMs -ne 10000 -or
    $probeSource.width -ne 1280 -or
    $probeSource.height -ne 720 -or
    $probeSource.videoTimeBaseNumerator -ne 1 -or
    $probeSource.videoTimeBaseDenominator -ne 24000 -or
    $probeSource.videoStartTimestamp -ne 0 -or
    $probeSource.videoPtsSha256 -cne $expectedSourcePtsSha256 -or
    ([int64]$probeSource.durationNumerator *
        [int64]$probeSource.fpsNumerator) -ne
        ([int64]$probeSource.frameCount *
            [int64]$probeSource.fpsDenominator *
            [int64]$probeSource.durationDenominator)) {
    throw 'The probe fixture no longer proves exact frames, rational timeline, dimensions, or PTS digest.'
}
Assert-ExactPropertySet $probe.expectedEffects $inspectionEffectNames `
    'inspection probe effect fixture'
if (($probe.expectedEffects | ConvertTo-Json -Compress) -cne
    ($inspection.effects | ConvertTo-Json -Compress)) {
    throw 'Inspection probe effects drifted from the zero-durable-effect contract.'
}

$preview = $inspectionFixture.preview
Assert-ExactPropertySet $preview.request @(
    'action', 'source', 'sourceIdentityDigest', 'selection', 'frames'
) 'inspection preview fixture request'
Assert-ExactPropertySet $preview.response @(
    'action', 'protocol', 'inspectionRevision', 'sourceIdentityDigest',
    'selection', 'previews'
) 'inspection preview fixture response'
if ($preview.route -cne $inspection.transport.route -or
    $preview.method -cne 'POST' -or
    $preview.maximumRequestBytes -ne 131072 -or
    $preview.maximumResponseBytes -ne 2113536 -or
    $preview.request.action -cne 'preview' -or
    $preview.response.action -cne 'preview' -or
    $preview.response.protocol -cne $contract.protocol -or
    $preview.response.inspectionRevision -cne $inspection.revision -or
    $preview.request.sourceIdentityDigest -cne
        $probeSource.sourceIdentityDigest -or
    $preview.response.sourceIdentityDigest -cne
        $probeSource.sourceIdentityDigest) {
    throw 'The exact selected-frame preview transport or source binding drifted.'
}
$previewStart = [int64]$preview.request.selection.startFrame
$previewEndExclusive = [int64]$preview.request.selection.endFrameExclusive
$previewMiddle = [Math]::Floor(
    ($previewStart + $previewEndExclusive - 1) / 2)
[int64[]]$expectedPreviewFrames = @(
    $previewStart,
    [int64]$previewMiddle,
    ($previewEndExclusive - 1)
)
[int64[]]$actualPreviewFrames = @($preview.request.frames)
if ($previewEndExclusive - $previewStart -lt 3 -or
    ($actualPreviewFrames -join ',') -cne
        ($expectedPreviewFrames -join ',') -or
    $actualPreviewFrames[0] -ge $actualPreviewFrames[1] -or
    $actualPreviewFrames[1] -ge $actualPreviewFrames[2] -or
    ($preview.response.selection | ConvertTo-Json -Compress) -cne
        ($preview.request.selection | ConvertTo-Json -Compress)) {
    throw 'Preview frames are not three distinct selection-derived in-range frames.'
}
$previewPayloads = @($preview.response.previews)
if ($previewPayloads.Count -ne 3) {
    throw 'The inspection preview response must contain exactly three payloads.'
}
for ($index = 0; $index -lt 3; $index++) {
    $payload = $previewPayloads[$index]
    Assert-ExactPropertySet $payload @($inspection.preview.exactPreviewFields) `
        "inspection preview payload $index"
    [byte[]]$pngBytes = [Convert]::FromBase64String($payload.base64)
    $pngSha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $encodedSha256 = ([BitConverter]::ToString(
            $pngSha256.ComputeHash($pngBytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $pngSha256.Dispose()
    }
    if ($payload.role -cne @('start', 'middle', 'end')[$index] -or
        $payload.sourceFrame -ne $expectedPreviewFrames[$index] -or
        $payload.mime -cne 'image/png' -or
        $payload.width -lt 1 -or $payload.width -gt 384 -or
        $payload.height -lt 1 -or $payload.height -gt 384 -or
        ([int64]$payload.width * [int64]$payload.height) -gt 147456 -or
        $payload.encodedBytes -ne $pngBytes.Length -or
        $payload.encodedBytes -gt 524288 -or
        $payload.encodedSha256 -cne $encodedSha256) {
        throw "Inspection preview payload $index is malformed or over-bound."
    }
}
Assert-ExactPropertySet $preview.expectedEffects $inspectionEffectNames `
    'inspection preview effect fixture'
if (($preview.expectedEffects | ConvertTo-Json -Compress) -cne
    ($inspection.effects | ConvertTo-Json -Compress) -or
    $preview.expectedThumbnailAuthority -ne $false -or
    $inspectionFixture.shortSelection.expectedPreviewAccepted -ne $false -or
    $inspectionFixture.shortSelection.expectedTrimSelectionAccepted -ne $true -or
    $inspectionFixture.shortSelection.expectedMutationRequests -ne 0) {
    throw 'Preview effects, thumbnail authority, or short-selection separation changed.'
}
$inspectionErrorVectors = @($inspectionFixture.errorVectors)
if ($inspectionErrorVectors.Count -ne $expectedInspectionErrors.Count) {
    throw 'The source-inspection error fixture count changed.'
}
foreach ($vector in $inspectionErrorVectors) {
    Assert-ExactPropertySet $vector @('status', 'response') `
        "inspection error vector $($vector.status)"
    Assert-ExactPropertySet $vector.response `
        @($inspection.transport.error.exactFields) `
        "inspection error response $($vector.status)"
    if (-not $expectedInspectionErrors.Contains([int]$vector.status)) {
        throw "Unexpected source-inspection error fixture: $($vector.status)"
    }
    $expectedError = $expectedInspectionErrors[[int]$vector.status]
    if ($vector.response.code -cne $expectedError[0] -or
        $vector.response.retryable -ne $expectedError[1] -or
        [string]::IsNullOrWhiteSpace([string]$vector.response.message) -or
        ([string]$vector.response.message).Length -gt 512 -or
        ([string]$vector.response.message).Contains('${') -or
        ([string]$vector.response.message).Contains('\') -or
        ([string]$vector.response.message).Contains('/')) {
        throw "Source-inspection error response $($vector.status) is unbounded or leaks a path."
    }
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

$durableReader = $contract.durableReader
Assert-ExactSet @($durableReader.jobEnvelope.commonRequiredFields) @(
    'id', 'operation', 'mediaKind', 'sourceId',
    'presetId', 'adapterId', 'presetHash', 'status', 'progress',
    'cancelRequested', 'createdAt', 'updatedAt', 'videoTrim'
) 'durable trim common Job required fields'
Assert-ExactSet @($durableReader.jobEnvelope.managedAdditionalFields) @(
    'sourceVideoJobId'
) 'durable trim managed Job additional fields'
Assert-ExactSet @($durableReader.jobEnvelope.statusValues) @(
    'queued', 'running', 'succeeded', 'failed', 'canceled'
) 'durable trim Job statuses'
Assert-ExactSet @($durableReader.snapshot.exactFields) @(
    'schemaVersion', 'protocol', 'presetId', 'adapterId',
    'receiptSetSha256', 'source', 'requested', 'plan', 'delivery'
) 'durable trim snapshot fields'
Assert-ExactSet @($durableReader.snapshot.managedSourceExactFields) @(
    'kind', 'producerJobId', 'canonicalPath', 'signature', 'sha256',
    'probe', 'probeDigest', 'sourceIdentityDigest', 'dependencyDigest'
) 'durable trim managed source fields'
Assert-ExactSet @($durableReader.snapshot.stagedSourceExactFields) @(
    'kind', 'originalCanonicalPath', 'originalSignature', 'originalSha256',
    'stagingCanonicalPath', 'stagingSignature', 'stagingSha256', 'probe',
    'probeDigest', 'sourceIdentityDigest', 'stagingOwnershipDigest'
) 'durable trim staged source fields'
Assert-ExactSet @($durableReader.snapshot.probeExactFields) @(
    'container', 'videoCodec', 'pixelFormat', 'bitDepth', 'dynamicRange',
    'width', 'height', 'frameCount', 'fpsNumerator', 'fpsDenominator',
    'durationMs', 'durationNumerator', 'durationDenominator',
    'videoStreamCount', 'audioStreamCount', 'extraStreamCount',
    'videoTimeBaseNumerator', 'videoTimeBaseDenominator',
    'videoStartTimestamp', 'videoPtsSha256'
) 'durable trim source probe fields'
Assert-ExactSet @($durableReader.snapshot.requestedExactFields) @(
    'schemaVersion', 'source', 'selection', 'audioPolicy'
) 'durable trim requested fields'
if ($durableReader.snapshot.requestNormalization.'displayed-file' -notmatch
        'do not retain the raw untrusted request path' -or
    $durableReader.snapshot.requestNormalization.authority -notmatch
        'server-owned canonical') {
    throw 'Durable displayed-file request normalization or source authority changed.'
}
Assert-ExactSet @($durableReader.snapshot.planExactFields) @(
    'revision', 'selection', 'startPts', 'endPtsExclusive',
    'selectedFrameCount', 'durationNumerator', 'durationDenominator',
    'audioPlan'
) 'durable trim plan fields'
Assert-ExactSet @($durableReader.delivery.exactFields) @(
    'revision', 'container', 'videoCodec', 'pixelFormat', 'bitDepth',
    'dynamicRange', 'width', 'height', 'frameCount', 'fpsNumerator',
    'fpsDenominator', 'timeBaseNumerator', 'timeBaseDenominator',
    'durationNumerator', 'durationDenominator', 'firstRelativePts',
    'lastRelativePts', 'videoPtsSha256', 'audio'
) 'durable trim delivery fields'

$durableFixture = $fixture.durableJobVectors
$durableJobs = @($durableFixture.jobs)
if ($durableJobs.Count -ne 5 -or
    $durableFixture.expectedCurrentWriterEnabled -ne $false -or
    $durableFixture.expectedReady -ne $false -or
    $durableFixture.expectedMutationRequestsDuringRead -ne 0) {
    throw 'Durable Video Trim Job fixtures must cover five states without activation.'
}
Assert-ExactSet @($durableJobs.status) @(
    'queued', 'running', 'succeeded', 'failed', 'canceled'
) 'durable trim fixture statuses'

$baselineSnapshotJson = $null
$expectedOutputPtsSha256 = Get-SequentialPtsSha256 48
foreach ($job in $durableJobs) {
    $status = [string]$job.status
    $expectedJobFields = @($durableReader.jobEnvelope.commonRequiredFields) +
        @($durableReader.jobEnvelope.managedAdditionalFields) +
        @($durableReader.jobEnvelope.stateFields.$status)
    Assert-ExactPropertySet $job $expectedJobFields `
        "durable trim $status Job fields"
    $createdAt = [DateTimeOffset]::MinValue
    $updatedAt = [DateTimeOffset]::MinValue
    if ($job.operation -cne 'video' -or
        $job.mediaKind -cne 'video' -or
        $job.presetId -cne 'aibos-video-trim-v1' -or
        $job.adapterId -cne 'ffmpeg-video-trim-v1' -or
        $job.sourceVideoJobId -cne
            '11111111-2222-4333-8444-555555555555' -or
        $job.progress -lt 0 -or $job.progress -gt 100 -or
        -not [DateTimeOffset]::TryParse(
            [string]$job.createdAt,
            [ref]$createdAt) -or
        -not [DateTimeOffset]::TryParse(
            [string]$job.updatedAt,
            [ref]$updatedAt) -or
        $updatedAt -lt $createdAt) {
        throw "Durable trim $status Job envelope is malformed."
    }

    $snapshot = $job.videoTrim
    Assert-ExactPropertySet $snapshot @($durableReader.snapshot.exactFields) `
        "durable trim $status snapshot fields"
    Assert-ExactPropertySet $snapshot.source `
        @($durableReader.snapshot.managedSourceExactFields) `
        "durable trim $status source fields"
    Assert-ExactPropertySet $snapshot.source.signature `
        @($durableReader.snapshot.signatureExactFields) `
        "durable trim $status source signature"
    Assert-ExactPropertySet $snapshot.source.probe `
        @($durableReader.snapshot.probeExactFields) `
        "durable trim $status source probe"
    Assert-ExactPropertySet $snapshot.requested `
        @($durableReader.snapshot.requestedExactFields) `
        "durable trim $status requested fields"
    Assert-ExactPropertySet $snapshot.requested.selection @(
        'startFrame', 'endFrameExclusive'
    ) "durable trim $status requested selection"
    Assert-ExactPropertySet $snapshot.plan `
        @($durableReader.snapshot.planExactFields) `
        "durable trim $status plan fields"
    Assert-ExactPropertySet $snapshot.plan.selection @(
        'startFrame', 'endFrameExclusive'
    ) "durable trim $status plan selection"
    Assert-ExactPropertySet $snapshot.delivery `
        @($durableReader.delivery.exactFields) `
        "durable trim $status delivery fields"
    Assert-ExactPropertySet $snapshot.plan.audioPlan `
        @($durableReader.snapshot.planAudioVariants.preserve.exactFields) `
        "durable trim $status audio plan"
    Assert-ExactPropertySet $snapshot.delivery.audio `
        @($durableReader.delivery.audioVariants.preserve.exactFields) `
        "durable trim $status audio delivery"

    $stableSnapshotSha256 = Get-StableJsonSha256 $snapshot
    if ($job.presetHash -cne $stableSnapshotSha256.Substring(0, 12)) {
        throw "Durable trim $status presetHash does not bind the exact snapshot."
    }
    $snapshotJson = $snapshot | ConvertTo-Json -Depth 100 -Compress
    if ($null -eq $baselineSnapshotJson) {
        $baselineSnapshotJson = $snapshotJson
    }
    elseif ($snapshotJson -cne $baselineSnapshotJson) {
        throw 'The immutable durable trim snapshot changed across Job states.'
    }

    $jobProbe = $snapshot.source.probe
    $requestSelection = $snapshot.requested.selection
    $plan = $snapshot.plan
    $delivery = $snapshot.delivery
    if ($snapshot.schemaVersion -ne 1 -or
        $snapshot.protocol -cne $contract.protocol -or
        $snapshot.presetId -cne $job.presetId -or
        $snapshot.adapterId -cne $job.adapterId -or
        $snapshot.source.kind -cne 'managed-video-job' -or
        $snapshot.source.producerJobId -cne $job.sourceVideoJobId -or
        $jobProbe.videoPtsSha256 -cne $expectedSourcePtsSha256 -or
        $snapshot.requested.schemaVersion -ne 1 -or
        $snapshot.requested.source.sourceVideoJobId -cne
            $job.sourceVideoJobId -or
        $snapshot.requested.audioPolicy -cne 'preserve' -or
        $requestSelection.startFrame -ne 24 -or
        $requestSelection.endFrameExclusive -ne 72 -or
        ($plan.selection | ConvertTo-Json -Compress) -cne
            ($requestSelection | ConvertTo-Json -Compress) -or
        $plan.revision -cne 'aibos-video-trim-plan-v1' -or
        $plan.startPts -ne 24000 -or
        $plan.endPtsExclusive -ne 72000 -or
        $plan.selectedFrameCount -ne 48 -or
        $plan.durationNumerator -ne 2 -or
        $plan.durationDenominator -ne 1 -or
        $delivery.revision -cne 'aibos-video-trim-delivery-v1' -or
        $delivery.frameCount -ne $plan.selectedFrameCount -or
        $delivery.fpsNumerator -ne 24 -or
        $delivery.fpsDenominator -ne 1 -or
        $delivery.timeBaseNumerator -ne 1 -or
        $delivery.timeBaseDenominator -ne 24 -or
        $delivery.firstRelativePts -ne 0 -or
        $delivery.lastRelativePts -ne 47 -or
        $delivery.videoPtsSha256 -cne $expectedOutputPtsSha256 -or
        $delivery.audio.kind -cne 'aac-selected-interval' -or
        $delivery.audio.policy -cne 'preserve' -or
        $delivery.audio.audioStreamCount -ne 1 -or
        $delivery.audio.codec -cne 'aac' -or
        $delivery.audio.packetBitIdentityClaimed -ne $false -or
        $delivery.audio.sampleExactClaimed -ne $false) {
        throw "Durable trim $status snapshot, plan, or delivery drifted."
    }

    switch ($status) {
        'queued' {
            if ($job.progress -ne 0 -or $job.cancelRequested -ne $false -or
                $job.queueOrder -ne 0) {
                throw 'Queued trim Job state fields are not exact.'
            }
        }
        'running' {
            if ($job.progress -lt 1 -or $job.progress -gt 99 -or
                $job.cancelRequested -ne $false) {
                throw 'Running trim Job state fields are not exact.'
            }
        }
        'succeeded' {
            if ($job.progress -ne 100 -or $job.cancelRequested -ne $false -or
                -not ([string]$job.outputPath).EndsWith('.mp4', [StringComparison]::OrdinalIgnoreCase) -or
                $job.outputSha256 -notmatch '^[0-9a-f]{64}$' -or
                $job.outputBytes -lt 1 -or $job.outputBytes -gt 536870912) {
                throw 'Succeeded trim Job output identity is not exact.'
            }
        }
        'failed' {
            if ($job.cancelRequested -ne $false -or
                [string]::IsNullOrWhiteSpace([string]$job.errorCode) -or
                [string]::IsNullOrWhiteSpace([string]$job.errorMessage)) {
                throw 'Failed trim Job error identity is not exact.'
            }
        }
        'canceled' {
            if ($job.cancelRequested -ne $true) {
                throw 'Canceled trim Job must retain cancelRequested.'
            }
        }
    }
}

$audioVectors = $durableFixture.audioPolicyVectors
Assert-ExactPropertySet $audioVectors.preserve.plan `
    @($durableReader.snapshot.planAudioVariants.preserve.exactFields) `
    'preserve audio plan vector'
Assert-ExactPropertySet $audioVectors.preserve.delivery `
    @($durableReader.delivery.audioVariants.preserve.exactFields) `
    'preserve audio delivery vector'
Assert-ExactPropertySet $audioVectors.mute.plan `
    @($durableReader.snapshot.planAudioVariants.mute.exactFields) `
    'mute audio plan vector'
Assert-ExactPropertySet $audioVectors.mute.delivery `
    @($durableReader.delivery.audioVariants.mute.exactFields) `
    'mute audio delivery vector'
if ($audioVectors.preserve.requestedValue -cne 'preserve' -or
    $audioVectors.preserve.delivery.packetBitIdentityClaimed -ne $false -or
    $audioVectors.preserve.delivery.sampleExactClaimed -ne $false -or
    $audioVectors.mute.requestedValue -cne 'mute' -or
    $audioVectors.mute.delivery.audioStreamCount -ne 0) {
    throw 'Preserve or mute durable audio fixtures drifted.'
}

if ($fixture.negativeVectors.videoToolsV2KindTrim.expectedAccepted -ne $false -or
    $fixture.negativeVectors.videoToolsV2KindTrim.expectedWriterAction -cne 'reject' -or
    $fixture.negativeVectors.malformedSelection.expectedWriterAction -cne 'reject' -or
    $fixture.negativeVectors.futureSchema.expectedMode -cne 'reader-only' -or
    $fixture.negativeVectors.forbiddenAiFields.expectedWriterAction -cne 'reject' -or
    $fixture.negativeVectors.malformedDurableJob.expectedMode -cne 'reader-only' -or
    $fixture.negativeVectors.malformedDurableJob.expectedPreserveUnknownFields -ne $true -or
    $fixture.negativeVectors.malformedDurableJob.expectedMutationRequests -ne 0 -or
    $fixture.negativeVectors.futureDurableJob.expectedMode -cne 'reader-only' -or
    $fixture.negativeVectors.futureDurableJob.expectedPreserveUnknownFields -ne $true -or
    $fixture.negativeVectors.futureDurableJob.expectedMutationRequests -ne 0) {
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
    sourceInspectionEnabled =
        $contract.productionGate.sourceInspectionEnabled
    inspectionActions = @($inspection.transport.exactActions).Count
    durableJobStates = $durableJobs.Count
    passiveMutationRequests = $fixture.currentHealth.expectedPassiveMutationRequests
} | ConvertTo-Json -Depth 5
