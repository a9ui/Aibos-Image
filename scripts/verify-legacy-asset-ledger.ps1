[CmdletBinding()]
param(
    [string]$ManifestPath = '',
    [string]$SummaryPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestFile = if ($ManifestPath) {
    [IO.Path]::GetFullPath($ManifestPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-ledger\manifest-v3.json'
}
$summaryFile = if ($SummaryPath) {
    [IO.Path]::GetFullPath($SummaryPath)
}
else {
    Join-Path $repoRoot 'docs\legacy-ledger\summary-v3.json'
}
$allowedKinds = @(
    'gh_branch', 'gh_issue', 'gh_pr', 'gh_tag', 'local_ref',
    'stash', 'worktree', 'staged_path', 'unstaged_path', 'untracked_path'
)
$ownerIssues = @{
    'M1-SU-001' = 8
    'M1-SU-002' = 9
    'M1-SU-003' = 10
    'M1-SU-004' = 11
    'M1-SU-005' = 12
    'M1-SU-006' = 13
    'M1-SU-007' = 14
    'M1-SU-008' = 15
    'M1-SU-009' = 16
    'M1-SU-010' = 17
}
$focalOwners = @{
    'gh_issue:33' = 'M1-SU-001'
    'gh_issue:318' = 'M1-SU-001'
    'gh_pr:325' = 'M1-SU-001'
    'gh_pr:326' = 'M1-SU-001'
    'gh_issue:329' = 'M1-SU-002'
    'gh_issue:330' = 'M1-SU-002'
    'gh_pr:24' = 'M1-SU-002'
    'gh_pr:331' = 'M1-SU-002'
    'gh_issue:97' = 'M1-SU-003'
    'gh_pr:29' = 'M1-SU-003'
    'gh_pr:160' = 'M1-SU-003'
    'gh_issue:105' = 'M1-SU-004'
    'gh_issue:106' = 'M1-SU-004'
    'gh_issue:321' = 'M1-SU-004'
    'gh_pr:96' = 'M1-SU-004'
    'gh_pr:134' = 'M1-SU-005'
    'gh_pr:147' = 'M1-SU-005'
    'gh_pr:152' = 'M1-SU-005'
    'gh_pr:154' = 'M1-SU-005'
    'gh_pr:156' = 'M1-SU-005'
    'gh_pr:158' = 'M1-SU-005'
    'gh_issue:320' = 'M1-SU-006'
    'gh_issue:323' = 'M1-SU-006'
    'gh_pr:319' = 'M1-SU-006'
    'gh_issue:316' = 'M1-SU-007'
    'gh_issue:328' = 'M1-SU-007'
    'gh_pr:317' = 'M1-SU-007'
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

function Get-Sha256Ascii([string]$Text) {
    return Get-Sha256 ([Text.ASCIIEncoding]::new().GetBytes($Text))
}

function ConvertTo-SortedMap($Value) {
    $result = [ordered]@{}
    [string[]]$names = @($Value.PSObject.Properties.Name)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) { $result[$name] = $Value.$name }
    return $result
}

function Assert-MapEqual($Actual, $Expected, [string]$Name) {
    $actualJson = (ConvertTo-SortedMap $Actual) | ConvertTo-Json -Compress
    $expectedJson = (ConvertTo-SortedMap $Expected) | ConvertTo-Json -Compress
    if ($actualJson -cne $expectedJson) {
        throw "$Name does not match the checked-in manifest."
    }
}

function Get-ExpectedOwner([string]$Kind, [string]$PublicNumber) {
    $key = "$Kind`:$PublicNumber"
    if ($focalOwners.ContainsKey($key)) { return $focalOwners[$key] }
    if ($Kind -in @('gh_issue', 'gh_pr')) { return 'M1-SU-008' }
    if ($Kind -in @('gh_branch', 'gh_tag', 'local_ref')) { return 'M1-SU-009' }
    return 'M1-SU-010'
}

if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf) -or
    -not (Test-Path -LiteralPath $summaryFile -PathType Leaf)) {
    throw 'The checked-in M1 ledger artifacts are missing.'
}
$manifestBytes = [IO.File]::ReadAllBytes($manifestFile)
$manifestText = [Text.ASCIIEncoding]::new().GetString($manifestBytes)
$summaryText = Get-Content -Raw -Encoding UTF8 -LiteralPath $summaryFile
$manifest = $manifestText | ConvertFrom-Json
$summary = $summaryText | ConvertFrom-Json

if ((Get-Sha256 $manifestBytes) -cne [string]$summary.manifestSha256) {
    throw 'The manifest SHA-256 does not match its summary.'
}
foreach ($name in @(
    'manifestVersion',
    'hashDomain',
    'cutoffId',
    'capturedAtUtc',
    'aibosBaseSha',
    'h25DefaultSha',
    'sourceStateSha256'
)) {
    if (([string]$manifest.$name) -cne ([string]$summary.$name)) {
        throw "Manifest header $name does not match its summary."
    }
}
if ([int]$manifest.manifestVersion -ne 3 -or
    [string]$manifest.hashDomain -cne 'aibos-m1-ledger/v3') {
    throw 'The ledger version or hash domain is unsupported.'
}
if ([string]$manifest.aibosBaseSha -notmatch '^[0-9a-f]{40}$' -or
    [string]$manifest.h25DefaultSha -notmatch '^[0-9a-f]{40}$' -or
    [string]$manifest.sourceStateSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'The ledger header contains a malformed source identity.'
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
foreach ($pattern in $privatePatterns) {
    if ($manifestText -match $pattern -or $summaryText -match $pattern) {
        throw 'The ledger contains a forbidden private-surface pattern.'
    }
}

$items = @($manifest.items)
if ($items.Count -ne [int]$summary.recordCount) {
    throw 'The manifest record count does not match its summary.'
}
if (@($items | Group-Object assetId).Count -ne $items.Count -or
    @($items | Group-Object identitySha256).Count -ne $items.Count) {
    throw 'The ledger has duplicate asset or identity identifiers.'
}

$lineById = @{}
foreach ($line in ($manifestText -split "`n")) {
    if ($line -match '^\s+\{"assetId":"(?<id>M1A-\d{6})"') {
        $lineById[$Matches.id] = $line.Trim().TrimEnd(',')
    }
}
$categoryLines = @{}
foreach ($kind in $allowedKinds) {
    $categoryLines[$kind] = [Collections.Generic.List[string]]::new()
}
$ownerLines = @{}
foreach ($owner in $ownerIssues.Keys) {
    $ownerLines[$owner] = [Collections.Generic.List[string]]::new()
}
for ($index = 0; $index -lt $items.Count; $index++) {
    $item = $items[$index]
    $expectedId = 'M1A-' + ($index + 1).ToString('D6')
    if ([string]$item.assetId -cne $expectedId) {
        throw 'Asset IDs are not a canonical contiguous sequence.'
    }
    if ([string]$item.kind -notin $allowedKinds) {
        throw 'The ledger has an unsupported asset category.'
    }
    if (-not $ownerIssues.ContainsKey([string]$item.ownerSemanticUnit)) {
        throw 'The ledger has an unsupported semantic-unit owner.'
    }
    if ([int]$item.ownerIssue -ne [int]$ownerIssues[[string]$item.ownerSemanticUnit]) {
        throw 'A semantic-unit owner points at the wrong Issue.'
    }
    $expectedOwner = Get-ExpectedOwner ([string]$item.kind) ([string]$item.publicNumber)
    if ([string]$item.ownerSemanticUnit -cne $expectedOwner) {
        throw 'A record violates the exactly-one ownership predicates.'
    }
    if ([string]$item.identitySha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]$item.fingerprintSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'A ledger item has a malformed SHA-256.'
    }
    if ([string]$item.publicSha -and [string]$item.publicSha -notmatch '^[0-9a-f]{40}$') {
        throw 'A ledger item has a malformed public SHA.'
    }
    if ([string]$item.publicNumber -and [string]$item.publicNumber -notmatch '^\d+$') {
        throw 'A ledger item has a malformed public numeric identifier.'
    }
    if ([string]$item.kind -in @('gh_issue', 'gh_pr')) {
        if (-not [string]$item.publicNumber) {
            throw 'A GitHub Issue or pull request lacks its public numeric identifier.'
        }
    }
    elseif ([string]$item.publicNumber) {
        throw 'A private/local identity leaked through the public-number field.'
    }
    if ([string]$item.disposition -cne 'PENDING_M2') {
        throw 'M1 must not claim a semantic disposition.'
    }
    if ([string]$item.evidenceRef -cne ('AIBOS-ISSUE-' + [int]$item.ownerIssue)) {
        throw 'A ledger item evidence reference does not match its owner.'
    }
    if (-not $lineById.ContainsKey([string]$item.assetId)) {
        throw 'A canonical item line is missing.'
    }
    $kind = [string]$item.kind
    if (-not $categoryLines.ContainsKey($kind)) {
        $categoryLines[$kind] = [Collections.Generic.List[string]]::new()
    }
    $categoryLines[$kind].Add($lineById[[string]$item.assetId]) | Out-Null
    $owner = [string]$item.ownerSemanticUnit
    if (-not $ownerLines.ContainsKey($owner)) {
        $ownerLines[$owner] = [Collections.Generic.List[string]]::new()
    }
    $ownerLines[$owner].Add($lineById[[string]$item.assetId]) | Out-Null
}

$categoryCounts = [ordered]@{}
$categoryHashes = [ordered]@{}
[string[]]$categoryKeys = @($categoryLines.Keys)
[Array]::Sort($categoryKeys, [StringComparer]::Ordinal)
foreach ($key in $categoryKeys) {
    $categoryCounts[$key] = $categoryLines[$key].Count
    $categoryHashes[$key] = if ($categoryLines[$key].Count -eq 0) {
        Get-Sha256Ascii ''
    }
    else {
        Get-Sha256Ascii ((@($categoryLines[$key]) -join "`n") + "`n")
    }
}
$ownerCounts = [ordered]@{}
$ownerHashes = [ordered]@{}
[string[]]$ownerKeys = @($ownerLines.Keys)
[Array]::Sort($ownerKeys, [StringComparer]::Ordinal)
foreach ($key in $ownerKeys) {
    $ownerCounts[$key] = $ownerLines[$key].Count
    $ownerHashes[$key] = if ($ownerLines[$key].Count -eq 0) {
        Get-Sha256Ascii ''
    }
    else {
        Get-Sha256Ascii ((@($ownerLines[$key]) -join "`n") + "`n")
    }
}
Assert-MapEqual ([pscustomobject]$categoryCounts) $summary.categoryCounts 'Category count'
Assert-MapEqual ([pscustomobject]$categoryHashes) $summary.categorySha256 'Category SHA-256'
Assert-MapEqual ([pscustomobject]$ownerCounts) $summary.ownershipCounts 'Ownership count'
Assert-MapEqual ([pscustomobject]$ownerHashes) $summary.ownershipSha256 'Ownership SHA-256'
if ($categoryKeys.Count -ne 10 -or $ownerKeys.Count -ne 10) {
    throw 'All ten categories and all ten owners must be represented.'
}

$categorySummary = (@(
    $categoryKeys | ForEach-Object {
        "$_=$($categoryCounts[$_]):$($categoryHashes[$_])"
    }
) -join "`n") + "`n"
$ownerSummary = (@(
    $ownerKeys | ForEach-Object {
        "$_=$($ownerCounts[$_]):$($ownerHashes[$_])"
    }
) -join "`n") + "`n"
if ((Get-Sha256Ascii $categorySummary) -cne [string]$summary.categorySummarySha256 -or
    (Get-Sha256Ascii $ownerSummary) -cne [string]$summary.ownershipSummarySha256) {
    throw 'The ledger summary hashes are inconsistent.'
}

if ([string]$summary.independentRecomputation.status -cne 'MATCH' -or
    $summary.independentRecomputation.manifestBytesMatch -ne $true -or
    $summary.independentRecomputation.identitySetMatch -ne $true -or
    $summary.independentRecomputation.ownershipMatch -ne $true -or
    $summary.independentRecomputation.categoryMatch -ne $true -or
    $summary.sourceUnchanged.powershellBeforeAfterMatch -ne $true -or
    $summary.sourceUnchanged.pythonBeforeAfterMatch -ne $true -or
    $summary.sourceUnchanged.crossImplementationSourceMatch -ne $true) {
    throw 'Independent recomputation or source-stability evidence is incomplete.'
}
if ($summary.privacy.opaqueLocalIdentities -ne $true -or
    $summary.privacy.rawPathsPresent -ne $false -or
    $summary.privacy.issueOrPullTitlesPresent -ne $false -or
    $summary.privacy.dirtyContentPresent -ne $false -or
    $summary.privacy.credentialsPresent -ne $false) {
    throw 'The ledger privacy declaration is not fail-closed.'
}

[ordered]@{
    ok = $true
    cutoffId = [string]$summary.cutoffId
    recordCount = $items.Count
    categoryCount = $categoryKeys.Count
    ownerCount = $ownerKeys.Count
    manifestSha256 = [string]$summary.manifestSha256
    ownershipSummarySha256 = [string]$summary.ownershipSummarySha256
    independentRecomputation = 'MATCH'
    privateSurfaceFindings = 0
} | ConvertTo-Json -Depth 4
