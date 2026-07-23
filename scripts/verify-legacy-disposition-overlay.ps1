[CmdletBinding()]
param(
    [string]$ManifestPath = '',
    [string]$ManifestSummaryPath = '',
    [string]$OverlayPath = '',
    [string]$OverlaySummaryPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedM1Cutoff = 'M1-20260723T172645Z'
$expectedM1ManifestSha256 = 'd265e7559f42784121d464843398b59221158a546fe8fdd209e1f9d421653b09'
$expectedM1SummarySha256 = '22385ac029cf37159abb6cc7f8059118d1ee2aef649013ffd33944c0c9fe9699'
$expectedM1SourceStateSha256 = '1889a84bc760a649762cf35bb3998c0f56db8bec66e465dffec30e3094556f58'
$expectedAibosCommit = 'de955891932540aa275de701a205b2fce668b478'
$expectedAibosTree = '0504e7a9c160692910185e1eaf51fbf4d3020b33'
$expectedH25Commit = '0d451cadfe47433fdb45f00bc78168965955fe2d'
$expectedH25Tree = '025503c573661d8d8a474268d24cb2596a453c33'
$expectedAibosSemanticBindings = @{
    'M1A-000489' = @{
        Path = 'local-native/PhotoViewer.Wpf/App.xaml'
        Blob = '7c366a4b849040c9f07dfe720e9b72b8a445031a'
    }
    'M1A-000490' = @{
        Path = 'local-native/PhotoViewer.Wpf/MainWindow.xaml'
        Blob = '228b5897b60bf12031f53297286e005658e28b94'
    }
    'M1A-000497' = @{
        Path = 'local-native/PhotoViewer.Wpf/MainWindow.xaml.cs'
        Blob = '92809cb1fc46957eef265bd268e87c862fb3e33e'
    }
    'M1A-000499' = @{
        Path = 'local-native/PhotoViewer.Wpf/App.xaml.cs'
        Blob = 'fc2357a7092c2699061c6d889bca5545094b51f1'
    }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestFile = if ($ManifestPath) {
    [IO.Path]::GetFullPath($ManifestPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-ledger\manifest-v3.json'
}
$manifestSummaryFile = if ($ManifestSummaryPath) {
    [IO.Path]::GetFullPath($ManifestSummaryPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-ledger\summary-v3.json'
}
$overlayFile = if ($OverlayPath) {
    [IO.Path]::GetFullPath($OverlayPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-disposition\overlay-v1.json'
}
$overlaySummaryFile = if ($OverlaySummaryPath) {
    [IO.Path]::GetFullPath($OverlaySummaryPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-disposition\summary-v1.json'
}

$requiredFiles = @(
    $manifestFile,
    $manifestSummaryFile,
    $overlayFile,
    $overlaySummaryFile
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required disposition input is missing: $([IO.Path]::GetFileName($path))"
    }
}

function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-MapEqual($Actual, $Expected, [string]$Name) {
    $actualMap = @{}
    foreach ($property in $Actual.PSObject.Properties) {
        $actualMap[[string]$property.Name] = [int]$property.Value
    }
    $expectedMap = @{}
    foreach ($property in $Expected.PSObject.Properties) {
        $expectedMap[[string]$property.Name] = [int]$property.Value
    }
    $actualKeys = @($actualMap.Keys | Sort-Object)
    $expectedKeys = @($expectedMap.Keys | Sort-Object)
    if (($actualKeys -join "`n") -cne ($expectedKeys -join "`n")) {
        throw "$Name keys do not match."
    }
    foreach ($key in $actualKeys) {
        if ($actualMap[$key] -ne $expectedMap[$key]) {
            throw "$Name count for $key does not match."
        }
    }
}

$manifestBytes = [IO.File]::ReadAllBytes($manifestFile)
$manifestSummaryBytes = [IO.File]::ReadAllBytes($manifestSummaryFile)
$overlayBytes = [IO.File]::ReadAllBytes($overlayFile)
$manifestText = [Text.ASCIIEncoding]::new().GetString($manifestBytes)
$manifestSummaryText = [Text.UTF8Encoding]::new($false, $true).GetString($manifestSummaryBytes)
$overlayText = [IO.File]::ReadAllText($overlayFile, [Text.UTF8Encoding]::new($false, $true))
$overlaySummaryText = [IO.File]::ReadAllText($overlaySummaryFile, [Text.UTF8Encoding]::new($false, $true))
$manifest = $manifestText | ConvertFrom-Json
$manifestSummary = $manifestSummaryText | ConvertFrom-Json
$overlay = $overlayText | ConvertFrom-Json
$overlaySummary = $overlaySummaryText | ConvertFrom-Json

$manifestSha256 = Get-Sha256 $manifestBytes
$manifestSummarySha256 = Get-Sha256 $manifestSummaryBytes
$overlaySha256 = Get-Sha256 $overlayBytes
if ($manifestSha256 -cne $expectedM1ManifestSha256 -or
    $manifestSummarySha256 -cne $expectedM1SummarySha256 -or
    [string]$manifest.cutoffId -cne $expectedM1Cutoff -or
    [string]$manifest.sourceStateSha256 -cne $expectedM1SourceStateSha256 -or
    [int]$manifest.manifestVersion -ne 3 -or
    [string]$manifest.hashDomain -cne 'aibos-m1-ledger/v3') {
    throw 'The immutable M1 identity does not match the accepted cutoff.'
}
if ($manifestSha256 -cne [string]$manifestSummary.manifestSha256 -or
    [string]$manifestSummary.cutoffId -cne $expectedM1Cutoff -or
    [string]$manifestSummary.sourceStateSha256 -cne $expectedM1SourceStateSha256) {
    throw 'The immutable M1 manifest no longer matches its M1 summary.'
}
if ([string]$overlay.m1ManifestSha256 -cne $manifestSha256 -or
    [string]$overlaySummary.m1ManifestSha256 -cne $manifestSha256) {
    throw 'The M2 artifacts are not bound to the exact immutable M1 manifest.'
}
if ([string]$overlay.m1CutoffId -cne [string]$manifest.cutoffId -or
    [string]$overlaySummary.m1CutoffId -cne [string]$manifest.cutoffId) {
    throw 'The M2 artifacts are not bound to the exact M1 cutoff.'
}
if ([int]$overlay.overlayVersion -ne 1 -or
    [string]$overlay.hashDomain -cne 'aibos-m2-disposition/v1') {
    throw 'The M2 overlay version or hash domain is unsupported.'
}
if ([int]$overlaySummary.summaryVersion -ne 1 -or
    [string]$overlaySummary.hashDomain -cne 'aibos-m2-disposition-summary/v1') {
    throw 'The M2 summary version or hash domain is unsupported.'
}
if ([string]$overlaySummary.overlaySha256 -cne $overlaySha256) {
    throw 'The M2 overlay SHA-256 does not match its summary.'
}
$expectedOverlayFields = @(
    'aibosEvidenceCommit',
    'aibosEvidenceTree',
    'h25EvidenceCommit',
    'h25EvidenceTree',
    'hashDomain',
    'items',
    'm1CutoffId',
    'm1ManifestSha256',
    'm1SourceStateSha256',
    'overlayVersion'
) | Sort-Object
$expectedSummaryFields = @(
    'dispositionCounts',
    'duplicateAssetIds',
    'hashDomain',
    'm1CutoffId',
    'm1ManifestSha256',
    'missingAssets',
    'orphanRows',
    'overlaySha256',
    'ownerCounts',
    'privateSurfaceFindings',
    'rowCount',
    'su008PublicEvidenceCounts',
    'summaryVersion',
    'targetSemanticUnitCounts',
    'terminalCount',
    'transientCount',
    'verification'
) | Sort-Object
$actualOverlayFields = @($overlay.PSObject.Properties.Name | Sort-Object)
$actualSummaryFields = @($overlaySummary.PSObject.Properties.Name | Sort-Object)
if (($actualOverlayFields -join "`n") -cne ($expectedOverlayFields -join "`n") -or
    ($actualSummaryFields -join "`n") -cne ($expectedSummaryFields -join "`n")) {
    throw 'The M2 artifact top-level schema has missing or unexpected fields.'
}
if ([string]$overlay.m1SourceStateSha256 -cne $expectedM1SourceStateSha256 -or
    [string]$overlay.aibosEvidenceCommit -cne $expectedAibosCommit -or
    [string]$overlay.aibosEvidenceTree -cne $expectedAibosTree -or
    [string]$overlay.h25EvidenceCommit -cne $expectedH25Commit -or
    [string]$overlay.h25EvidenceTree -cne $expectedH25Tree) {
    throw 'The M2 overlay public evidence identity does not match the accepted exact pair.'
}

$privatePatterns = @(
    '(?i)[A-Z]:[\\/]',
    '(?i)(?:^|["''])/(?:Users|home)/',
    '(?i)Desktop[\\/]+Tools',
    '(?i)https://chatgpt\.com/g/g-p-',
    '(?i)(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}',
    '(?i)github_pat_[A-Za-z0-9_]+',
    '(?i)(?<![A-Za-z0-9])sk-(?:proj-)?[A-Za-z0-9_-]{20,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
)
$publicDirectory = Split-Path -Parent $overlayFile
$publicEntries = @(Get-ChildItem -LiteralPath $publicDirectory -Force)
if (@($publicEntries | Where-Object {
    $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
}).Count -ne 0) {
    throw 'The M2 public directory contains a nested or redirected entry.'
}
$publicFiles = @($publicEntries | Where-Object { -not $_.PSIsContainer } | Sort-Object Name)
$expectedPublicNames = @('overlay-v1.json', 'README.md', 'summary-v1.json')
if (($publicFiles.Name -join "`n") -cne ($expectedPublicNames -join "`n")) {
    throw 'The M2 public directory contains a missing or unexpected file.'
}
foreach ($publicFile in $publicFiles) {
    $publicText = [Text.UTF8Encoding]::new($false, $true).GetString(
        [IO.File]::ReadAllBytes($publicFile.FullName)
    )
    foreach ($pattern in $privatePatterns) {
        if ($publicText -match $pattern) {
            throw 'The M2 disposition artifacts contain a forbidden private-surface pattern.'
        }
    }
}

$terminal = @(
    'ADOPT_BASELINE',
    'ADOPT_FOUNDATION',
    'KEEP_H25',
    'MERGED_H25',
    'DEFER_M6',
    'CLOSE_SUPERSEDED',
    'HISTORICAL_PROVENANCE',
    'REJECT'
)
$transient = @('PENDING_M2', 'UNKNOWN', 'NOT_VERIFIED', 'NEEDS_OWNER')
$requiredRowFields = @(
    'asset_id',
    'decision_reason',
    'evidence_sha_or_issue',
    'm1_cutoff_id',
    'm1_manifest_sha256',
    'm1_owner',
    'm2_disposition',
    'target_semantic_unit'
) | Sort-Object

$manifestItems = @($manifest.items)
$overlayItems = @($overlay.items)
if ($manifestItems.Count -ne 507 -or $overlayItems.Count -ne $manifestItems.Count) {
    throw 'The M2 overlay must contain exactly the 507 immutable M1 assets.'
}

$manifestById = @{}
foreach ($item in $manifestItems) {
    $manifestById[[string]$item.assetId] = $item
}
$seen = @{}
$dispositionCounts = [ordered]@{}
foreach ($name in $terminal) {
    $dispositionCounts[$name] = 0
}
$ownerCounts = [ordered]@{}
$targetCounts = [ordered]@{}
$transientCount = 0

for ($index = 0; $index -lt $overlayItems.Count; $index++) {
    $row = $overlayItems[$index]
    $actualFields = @($row.PSObject.Properties.Name | Sort-Object)
    if (($actualFields -join "`n") -cne ($requiredRowFields -join "`n")) {
        throw 'An M2 row has missing or unexpected fields.'
    }
    $assetId = [string]$row.asset_id
    if ($assetId -cne [string]$manifestItems[$index].assetId) {
        throw 'M2 rows are not in canonical M1 asset order.'
    }
    if ($seen.ContainsKey($assetId)) {
        throw 'The M2 overlay contains a duplicate asset ID.'
    }
    $seen[$assetId] = $true
    if (-not $manifestById.ContainsKey($assetId)) {
        throw 'The M2 overlay contains an orphan asset ID.'
    }
    $m1 = $manifestById[$assetId]
    if ([string]$row.m1_cutoff_id -cne [string]$manifest.cutoffId -or
        [string]$row.m1_manifest_sha256 -cne $manifestSha256 -or
        [string]$row.m1_owner -cne [string]$m1.ownerSemanticUnit) {
        throw 'An M2 row is not bound to its exact M1 owner and evidence.'
    }
    $disposition = [string]$row.m2_disposition
    if ($disposition -in $transient) {
        $transientCount++
    }
    if ($disposition -notin $terminal) {
        throw 'An M2 row has a non-terminal or unsupported disposition.'
    }
    $evidence = [string]$row.evidence_sha_or_issue
    $reason = [string]$row.decision_reason
    $target = [string]$row.target_semantic_unit
    if ([string]::IsNullOrWhiteSpace($evidence) -or $evidence.Length -gt 512 -or
        $evidence -match '[^\x20-\x7e]') {
        throw 'An M2 row has missing, oversized, or non-ASCII evidence.'
    }
    if ([string]::IsNullOrWhiteSpace($reason) -or $reason.Length -gt 512 -or
        $reason -match '[^\x20-\x7e]') {
        throw 'An M2 row has missing, oversized, or non-ASCII reasoning.'
    }
    $blobMatches = [regex]::Matches(
        $evidence,
        'AIBOS-BLOB:(?<sha>[0-9a-f]{40})@(?<path>[A-Za-z0-9._/-]+)'
    )
    if ([regex]::Matches($evidence, 'AIBOS-BLOB:').Count -ne $blobMatches.Count) {
        throw 'An M2 row contains an unvalidated Aibos blob token.'
    }
    if ($expectedAibosSemanticBindings.ContainsKey($assetId)) {
        $binding = $expectedAibosSemanticBindings[$assetId]
        if ($disposition -cne 'ADOPT_BASELINE' -or
            $blobMatches.Count -ne 1 -or
            [string]$blobMatches[0].Groups['path'].Value -cne [string]$binding.Path -or
            [string]$blobMatches[0].Groups['sha'].Value -cne [string]$binding.Blob) {
            throw 'An M2 semantic adoption lacks its exact public blob binding.'
        }
    }
    elseif ($blobMatches.Count -ne 0) {
        throw 'An M2 row contains unexpected Aibos blob evidence.'
    }
    if ($target -notmatch '^(?:M1-SU-0(?:0[1-9]|10)|M3-WPF-PARITY)$') {
        throw 'An M2 row has an unsupported target semantic unit.'
    }
    $dispositionCounts[$disposition] = [int]$dispositionCounts[$disposition] + 1
    $owner = [string]$row.m1_owner
    if (-not $ownerCounts.Contains($owner)) { $ownerCounts[$owner] = 0 }
    $ownerCounts[$owner] = [int]$ownerCounts[$owner] + 1
    if (-not $targetCounts.Contains($target)) { $targetCounts[$target] = 0 }
    $targetCounts[$target] = [int]$targetCounts[$target] + 1
}

$duplicateCount = $overlayItems.Count - @($overlayItems | Group-Object asset_id).Count
$missingCount = @($manifestItems | Where-Object { -not $seen.ContainsKey([string]$_.assetId) }).Count
$orphanCount = @($overlayItems | Where-Object { -not $manifestById.ContainsKey([string]$_.asset_id) }).Count
if ($duplicateCount -ne 0 -or $missingCount -ne 0 -or $orphanCount -ne 0 -or
    $transientCount -ne 0) {
    throw 'The M2 overlay has duplicates, missing assets, orphans, or transient rows.'
}

if ([int]$overlaySummary.rowCount -ne 507 -or
    [int]$overlaySummary.terminalCount -ne 507 -or
    [int]$overlaySummary.transientCount -ne 0 -or
    [int]$overlaySummary.duplicateAssetIds -ne 0 -or
    [int]$overlaySummary.orphanRows -ne 0 -or
    [int]$overlaySummary.missingAssets -ne 0 -or
    [int]$overlaySummary.privateSurfaceFindings -ne 0) {
    throw 'The M2 summary does not declare a complete fail-closed reconciliation.'
}
Assert-MapEqual ([pscustomobject]$dispositionCounts) $overlaySummary.dispositionCounts 'Disposition'
Assert-MapEqual ([pscustomobject]$ownerCounts) $overlaySummary.ownerCounts 'Owner'
Assert-MapEqual ([pscustomobject]$targetCounts) $overlaySummary.targetSemanticUnitCounts 'Target semantic unit'

[ordered]@{
    ok = $true
    implementation = 'powershell'
    cutoffId = [string]$manifest.cutoffId
    manifestSha256 = $manifestSha256
    overlaySha256 = $overlaySha256
    rowCount = $overlayItems.Count
    terminalCount = $overlayItems.Count
    transientCount = $transientCount
    duplicateAssetIds = $duplicateCount
    orphanRows = $orphanCount
    missingAssets = $missingCount
    privateSurfaceFindings = 0
} | ConvertTo-Json -Depth 4
