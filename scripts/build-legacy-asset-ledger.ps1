[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LegacyRepo,
    [string]$GitHubRepository = 'a9ui/tools-h000025-photoviewer',
    [string]$GhPath = 'gh',
    [string]$PythonPath = 'python',
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$AibosBaseSha = '',
    [ValidatePattern('^[A-Za-z0-9._-]{1,96}$')]
    [string]$CutoffId = '',
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$')]
    [string]$CapturedAtUtc = '',
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$legacyRoot = [IO.Path]::GetFullPath($LegacyRepo)
$outputRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'docs\legacy-ledger'))
}
$repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ledger output must stay inside the Aibos repository.'
}
if ($outputRoot.StartsWith(
    $legacyRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ledger output must never target the H25 source repository.'
}

if (-not $CapturedAtUtc) {
    $CapturedAtUtc = [DateTime]::UtcNow.ToString(
        'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture)
}
if (-not $CutoffId) {
    $CutoffId = 'M1-' + [DateTime]::ParseExact(
        $CapturedAtUtc,
        'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal
    ).ToUniversalTime().ToString(
        'yyyyMMddTHHmmssZ',
        [Globalization.CultureInfo]::InvariantCulture)
}
if (-not $AibosBaseSha) {
    $AibosBaseSha = (& git -C $repoRoot rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the accepted Aibos main SHA.' }
}
$AibosBaseSha = $AibosBaseSha.ToLowerInvariant()

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempBase (
    'aibos-m1-ledger-' + [guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create ledger scratch data outside TEMP.'
}
$primaryRoot = Join-Path $runRoot 'powershell'
$independentRoot = Join-Path $runRoot 'python'
$primaryScript = Join-Path $PSScriptRoot 'capture-legacy-asset-ledger.ps1'
$independentScript = Join-Path $PSScriptRoot 'capture_legacy_asset_ledger.py'

function Get-Sha256Ascii([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.ASCIIEncoding]::new().GetBytes($Value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function ConvertTo-SortedMap($Value) {
    $ordered = [ordered]@{}
    [string[]]$names = @($Value.PSObject.Properties.Name)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        $ordered[$name] = $Value.$name
    }
    return $ordered
}

function Assert-MapEqual($Left, $Right, [string]$Name) {
    $leftJson = (ConvertTo-SortedMap $Left) | ConvertTo-Json -Compress -Depth 5
    $rightJson = (ConvertTo-SortedMap $Right) | ConvertTo-Json -Compress -Depth 5
    if ($leftJson -cne $rightJson) {
        throw "Independent $Name evidence did not match."
    }
}

function Assert-Equal($Left, $Right, [string]$Name) {
    if (([string]$Left) -cne ([string]$Right)) {
        throw "Independent $Name evidence did not match."
    }
}

function Get-ItemLinesByKey(
    [object[]]$Items,
    [string]$KeyName,
    [hashtable]$LineById,
    [string[]]$ExpectedKeys
) {
    $groups = @{}
    foreach ($expectedKey in $ExpectedKeys) {
        $groups[$expectedKey] = [Collections.Generic.List[string]]::new()
    }
    foreach ($item in $Items) {
        $key = [string]$item.$KeyName
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = [Collections.Generic.List[string]]::new()
        }
        $groups[$key].Add($LineById[[string]$item.assetId]) | Out-Null
    }
    $result = [ordered]@{}
    [string[]]$keys = @($groups.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    foreach ($key in $keys) {
        $result[$key] = $groups[$key].Count
    }
    return $result
}

function Test-ManifestPrivacy([string]$Text) {
    $patterns = @(
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
    foreach ($pattern in $patterns) {
        if ($Text -match $pattern) { return $false }
    }
    return $true
}

$completed = $false
try {
    [IO.Directory]::CreateDirectory($primaryRoot) | Out-Null
    [IO.Directory]::CreateDirectory($independentRoot) | Out-Null

    & powershell -NoProfile -ExecutionPolicy Bypass -File $primaryScript `
        -LegacyRepo $legacyRoot `
        -GitHubRepository $GitHubRepository `
        -GhPath $GhPath `
        -CutoffId $CutoffId `
        -CapturedAtUtc $CapturedAtUtc `
        -AibosBaseSha $AibosBaseSha `
        -OutputDirectory $primaryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Primary ledger capture failed.' }

    & $PythonPath -B $independentScript `
        --legacy-repo $legacyRoot `
        --github-repository $GitHubRepository `
        --gh-path $GhPath `
        --cutoff-id $CutoffId `
        --captured-at-utc $CapturedAtUtc `
        --aibos-base-sha $AibosBaseSha `
        --output-directory $independentRoot
    if ($LASTEXITCODE -ne 0) { throw 'Independent ledger capture failed.' }

    $primaryManifestPath = Join-Path $primaryRoot 'manifest.json'
    $independentManifestPath = Join-Path $independentRoot 'manifest.json'
    $primaryCapture = Get-Content -Raw -LiteralPath (Join-Path $primaryRoot 'capture.json') |
        ConvertFrom-Json
    $independentCapture = Get-Content -Raw -LiteralPath (Join-Path $independentRoot 'capture.json') |
        ConvertFrom-Json

    foreach ($name in @(
        'aibosBaseSha',
        'capturedAtUtc',
        'cutoffId',
        'githubStateSha256',
        'h25DefaultSha',
        'hashDomain',
        'localStateSha256',
        'manifestSha256',
        'manifestVersion',
        'recordCount',
        'sourceSetSha256',
        'sourceStateAfterSha256',
        'sourceStateBeforeSha256',
        'sourceStateSha256',
        'sourceUnchanged'
    )) {
        Assert-Equal $primaryCapture.$name $independentCapture.$name $name
    }
    foreach ($name in @(
        'categoryCounts',
        'categorySha256',
        'ownershipCounts',
        'ownershipSha256'
    )) {
        Assert-MapEqual $primaryCapture.$name $independentCapture.$name $name
    }
    if ($primaryCapture.sourceUnchanged -ne $true -or
        $independentCapture.sourceUnchanged -ne $true) {
        throw 'A source capture did not preserve its before/after state.'
    }

    $primaryBytes = [IO.File]::ReadAllBytes($primaryManifestPath)
    $independentBytes = [IO.File]::ReadAllBytes($independentManifestPath)
    if ($primaryBytes.Length -ne $independentBytes.Length) {
        throw 'Independent manifest byte lengths did not match.'
    }
    $byteMismatch = $false
    for ($index = 0; $index -lt $primaryBytes.Length; $index++) {
        if ($primaryBytes[$index] -ne $independentBytes[$index]) {
            $byteMismatch = $true
            break
        }
    }
    if ($byteMismatch) { throw 'Independent manifest bytes did not match.' }

    $manifestText = [Text.ASCIIEncoding]::new().GetString($primaryBytes)
    if (-not (Test-ManifestPrivacy $manifestText)) {
        throw 'The public manifest contains a forbidden private-surface pattern.'
    }
    $manifest = $manifestText | ConvertFrom-Json
    $items = @($manifest.items)
    if ($items.Count -ne [int]$primaryCapture.recordCount) {
        throw 'Manifest item count does not match the capture.'
    }
    if (@($items | Group-Object assetId).Count -ne $items.Count) {
        throw 'Manifest asset IDs are not unique.'
    }
    if (@($items | Group-Object identitySha256).Count -ne $items.Count) {
        throw 'Manifest identity hashes are not unique.'
    }

    $allowedKinds = @(
        'gh_branch', 'gh_issue', 'gh_pr', 'gh_tag', 'local_ref',
        'stash', 'worktree', 'staged_path', 'unstaged_path', 'untracked_path'
    )
    $allowedOwners = 1..10 | ForEach-Object { 'M1-SU-' + $_.ToString('D3') }
    $lineById = @{}
    $manifestLines = $manifestText -split "`n"
    foreach ($line in $manifestLines) {
        if ($line -match '^\s+\{"assetId":"(?<id>M1A-\d{6})"') {
            $lineById[$Matches.id] = $line.Trim().TrimEnd(',')
        }
    }
    for ($index = 0; $index -lt $items.Count; $index++) {
        $item = $items[$index]
        $expectedId = 'M1A-' + ($index + 1).ToString('D6')
        if ($item.assetId -cne $expectedId) { throw 'Manifest asset IDs are not canonical.' }
        if ($item.kind -notin $allowedKinds) { throw 'Manifest has an unknown category.' }
        if ($item.ownerSemanticUnit -notin $allowedOwners) {
            throw 'Manifest has an unknown semantic-unit owner.'
        }
        if ([string]$item.identitySha256 -notmatch '^[0-9a-f]{64}$' -or
            [string]$item.fingerprintSha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'Manifest has a malformed SHA-256.'
        }
        if ([string]$item.publicSha -and
            [string]$item.publicSha -notmatch '^[0-9a-f]{40}$') {
            throw 'Manifest has a malformed public SHA.'
        }
        if ([string]$item.publicNumber -and
            [string]$item.publicNumber -notmatch '^\d+$') {
            throw 'Manifest has a malformed public numeric identifier.'
        }
        if ($item.disposition -cne 'PENDING_M2') {
            throw 'M1 must not claim a disposition.'
        }
        if ($item.evidenceRef -cne ('AIBOS-ISSUE-' + $item.ownerIssue)) {
            throw 'Manifest evidence reference does not match its owner.'
        }
        if (-not $lineById.ContainsKey([string]$item.assetId)) {
            throw 'Could not recover a canonical manifest item line.'
        }
    }

    $expectedKinds = @(
        'gh_branch', 'gh_issue', 'gh_pr', 'gh_tag', 'local_ref',
        'stash', 'worktree', 'staged_path', 'unstaged_path', 'untracked_path'
    )
    $expectedOwners = 1..10 | ForEach-Object { 'M1-SU-' + $_.ToString('D3') }
    $categoryCounts = Get-ItemLinesByKey `
        $items 'kind' $lineById $expectedKinds
    $ownershipCounts = Get-ItemLinesByKey `
        $items 'ownerSemanticUnit' $lineById $expectedOwners
    Assert-MapEqual ([pscustomobject]$categoryCounts) $primaryCapture.categoryCounts 'recounted category'
    Assert-MapEqual ([pscustomobject]$ownershipCounts) $primaryCapture.ownershipCounts 'recounted ownership'
    if (@($categoryCounts.Keys).Count -ne 10) {
        throw 'Manifest does not represent all ten required asset categories.'
    }
    if (@($ownershipCounts.Keys).Count -ne 10) {
        throw 'Manifest does not assign all ten semantic-unit owners.'
    }

    $categorySummaryLines = @(
        $categoryCounts.Keys | ForEach-Object {
            "$_=$($categoryCounts[$_]):$($primaryCapture.categorySha256.$_)"
        }
    )
    $ownershipSummaryLines = @(
        $ownershipCounts.Keys | ForEach-Object {
            "$_=$($ownershipCounts[$_]):$($primaryCapture.ownershipSha256.$_)"
        }
    )
    $summary = [ordered]@{
        manifestVersion = [int]$primaryCapture.manifestVersion
        hashDomain = [string]$primaryCapture.hashDomain
        cutoffId = [string]$primaryCapture.cutoffId
        capturedAtUtc = [string]$primaryCapture.capturedAtUtc
        aibosBaseSha = [string]$primaryCapture.aibosBaseSha
        h25DefaultSha = [string]$primaryCapture.h25DefaultSha
        recordCount = [int]$primaryCapture.recordCount
        manifestSha256 = [string]$primaryCapture.manifestSha256
        sourceSetSha256 = [string]$primaryCapture.sourceSetSha256
        sourceStateSha256 = [string]$primaryCapture.sourceStateSha256
        githubStateSha256 = [string]$primaryCapture.githubStateSha256
        localStateSha256 = [string]$primaryCapture.localStateSha256
        categoryCounts = ConvertTo-SortedMap $primaryCapture.categoryCounts
        categorySha256 = ConvertTo-SortedMap $primaryCapture.categorySha256
        categorySummarySha256 = Get-Sha256Ascii (
            (@($categorySummaryLines) -join "`n") + "`n")
        ownershipCounts = ConvertTo-SortedMap $primaryCapture.ownershipCounts
        ownershipSha256 = ConvertTo-SortedMap $primaryCapture.ownershipSha256
        ownershipSummarySha256 = Get-Sha256Ascii (
            (@($ownershipSummaryLines) -join "`n") + "`n")
        sourceUnchanged = [ordered]@{
            powershellBeforeAfterMatch = $true
            pythonBeforeAfterMatch = $true
            crossImplementationSourceMatch = $true
        }
        independentRecomputation = [ordered]@{
            status = 'MATCH'
            primary = 'powershell-standard-library-v1'
            independent = 'python-standard-library-v1'
            manifestBytesMatch = $true
            identitySetMatch = $true
            ownershipMatch = $true
            categoryMatch = $true
        }
        privacy = [ordered]@{
            opaqueLocalIdentities = $true
            rawPathsPresent = $false
            issueOrPullTitlesPresent = $false
            dirtyContentPresent = $false
            credentialsPresent = $false
        }
    }

    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $outputRoot 'manifest-v3.json'), $primaryBytes)
    $summaryJson = ($summary | ConvertTo-Json -Depth 12).Replace("`r`n", "`n")
    [IO.File]::WriteAllText(
        (Join-Path $outputRoot 'summary-v3.json'),
        ($summaryJson + "`n"),
        [Text.UTF8Encoding]::new($false))

    [ordered]@{
        ok = $true
        cutoffId = $summary.cutoffId
        h25DefaultSha = $summary.h25DefaultSha
        recordCount = $summary.recordCount
        manifestSha256 = $summary.manifestSha256
        ownershipSummarySha256 = $summary.ownershipSummarySha256
        sourceUnchanged = $true
        independentRecomputation = 'MATCH'
        publicOutput = @(
            'docs/legacy-ledger/manifest-v3.json',
            'docs/legacy-ledger/summary-v3.json'
        )
    } | ConvertTo-Json -Depth 5
    $completed = $true
}
finally {
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if (-not $resolvedRunRoot.StartsWith(
        $tempPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean ledger scratch data outside TEMP.'
    }
    if (Test-Path -LiteralPath $resolvedRunRoot) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    if (-not $completed -and (Test-Path -LiteralPath $resolvedRunRoot)) {
        Write-Warning 'Ledger scratch data remains under the validated TEMP root.'
    }
}
