[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LegacyRepo,
    [Parameter(Mandatory = $true)]
    [string]$GitHubRepository,
    [string]$GhPath = 'gh',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]{1,96}$')]
    [string]$CutoffId,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$')]
    [string]$CapturedAtUtc,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$AibosBaseSha,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:GIT_OPTIONAL_LOCKS = '0'
$env:GIT_TERMINAL_PROMPT = '0'
$script:ManifestVersion = 3
$script:HashDomain = 'aibos-m1-ledger/v3'
$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)
$script:Ascii = [Text.ASCIIEncoding]::new()
$script:KindOrder = @{
    gh_branch = 0
    gh_issue = 1
    gh_pr = 2
    gh_tag = 3
    local_ref = 4
    stash = 5
    worktree = 6
    staged_path = 7
    unstaged_path = 8
    untracked_path = 9
}
$script:OwnerIssues = @{
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
$script:FocalOwners = @{
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

function ConvertTo-LowerHex([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes)).Replace('-', '').ToLowerInvariant()
}

function ConvertTo-Scalar($Value) {
    if ($null -eq $Value) { return '' }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' }
        return 'false'
    }
    if ($Value -is [IFormattable]) {
        return $Value.ToString($null, [Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

function Get-FieldHash(
    [Parameter(Mandatory = $true)]
    [string]$Domain,
    [object[]]$Fields = @()
) {
    $stream = [IO.MemoryStream]::new()
    try {
        foreach ($value in @($Domain) + @($Fields)) {
            $bytes = $script:Utf8.GetBytes((ConvertTo-Scalar $value))
            $length = $script:Ascii.GetBytes(
                $bytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + ':')
            $stream.Write($length, 0, $length.Length)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            return ConvertTo-LowerHex ($sha.ComputeHash($stream.ToArray()))
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ConvertTo-LowerHex ($sha.ComputeHash($Bytes))
    }
    finally {
        $sha.Dispose()
    }
}

function Get-Sha256Ascii([string]$Value) {
    return Get-Sha256Bytes ($script:Ascii.GetBytes($Value))
}

function Get-PathValue($Value, [string[]]$Path) {
    $current = $Value
    foreach ($name in $Path) {
        if ($null -eq $current) { return $null }
        if ($current -is [Collections.IDictionary]) {
            if (-not $current.Contains($name)) { return $null }
            $current = $current[$name]
            continue
        }
        $property = $current.PSObject.Properties[$name]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    return $current
}

function Get-ValueListHash([string]$Domain, [object[]]$Values) {
    [string[]]$normalized = @($Values | ForEach-Object { ConvertTo-Scalar $_ })
    [Array]::Sort($normalized, [StringComparer]::Ordinal)
    return Get-FieldHash -Domain $Domain -Fields $normalized
}

function Invoke-GhObject([string]$Endpoint) {
    $raw = & $GhPath api $Endpoint
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub API object request failed; output is intentionally redacted.'
    }
    return ($raw | ConvertFrom-Json)
}

function Invoke-GhItems([string]$Endpoint) {
    $lines = @(& $GhPath api --paginate $Endpoint --jq '.[] | @json')
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub API list request failed; output is intentionally redacted.'
    }
    $items = [Collections.Generic.List[object]]::new()
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace([string]$line)) { continue }
        $items.Add(([string]$line | ConvertFrom-Json)) | Out-Null
    }
    return @($items)
}

function Get-Owner([string]$Kind, [Nullable[int]]$PublicNumber) {
    $number = if ($null -ne $PublicNumber) { [int]$PublicNumber } else { -1 }
    $key = "$Kind`:$number"
    if ($script:FocalOwners.ContainsKey($key)) {
        return $script:FocalOwners[$key]
    }
    if ($Kind -in @('gh_issue', 'gh_pr')) { return 'M1-SU-008' }
    if ($Kind -in @('gh_branch', 'gh_tag', 'local_ref')) { return 'M1-SU-009' }
    return 'M1-SU-010'
}

function New-LedgerRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Source,
        [object[]]$IdentityFields,
        [object[]]$FingerprintFields,
        [Nullable[int]]$PublicNumber = $null,
        [string]$PublicSha = ''
    )
    if (-not $script:KindOrder.ContainsKey($Kind)) {
        throw "Unsupported ledger kind: $Kind"
    }
    $owner = Get-Owner $Kind $PublicNumber
    return [pscustomobject]@{
        kind = $Kind
        source = $Source
        publicNumber = if ($null -ne $PublicNumber) {
            ([int]$PublicNumber).ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        else { '' }
        publicSha = $PublicSha.ToLowerInvariant()
        identitySha256 = Get-FieldHash `
            -Domain "$($script:HashDomain)/identity/$Kind" `
            -Fields $IdentityFields
        fingerprintSha256 = Get-FieldHash `
            -Domain "$($script:HashDomain)/fingerprint/$Kind" `
            -Fields $FingerprintFields
        ownerSemanticUnit = $owner
        ownerIssue = [int]$script:OwnerIssues[$owner]
    }
}

function Get-GitHubCapture {
    $repository = Invoke-GhObject "/repos/$GitHubRepository"
    $defaultBranch = ConvertTo-Scalar (Get-PathValue $repository @('default_branch'))
    if (-not $defaultBranch) { throw 'GitHub repository has no default branch.' }
    $defaultCommit = Invoke-GhObject "/repos/$GitHubRepository/commits/$defaultBranch"
    $defaultSha = (ConvertTo-Scalar (Get-PathValue $defaultCommit @('sha'))).ToLowerInvariant()
    if ($defaultSha -notmatch '^[0-9a-f]{40}$') {
        throw 'GitHub default commit is not a SHA-1.'
    }

    $records = [Collections.Generic.List[object]]::new()
    foreach ($issue in (Invoke-GhItems "/repos/$GitHubRepository/issues?state=all&per_page=100")) {
        if ($null -ne (Get-PathValue $issue @('pull_request'))) { continue }
        $number = [int](Get-PathValue $issue @('number'))
        $labels = @(
            @(Get-PathValue $issue @('labels')) |
                ForEach-Object { Get-PathValue $_ @('name') }
        )
        $assignees = @(
            @(Get-PathValue $issue @('assignees')) |
                ForEach-Object { Get-PathValue $_ @('login') }
        )
        $labelsHash = Get-ValueListHash `
            "$($script:HashDomain)/github/issue-labels" $labels
        $assigneesHash = Get-ValueListHash `
            "$($script:HashDomain)/github/issue-assignees" $assignees
        $records.Add((New-LedgerRecord `
            -Kind 'gh_issue' `
            -Source 'H25-GITHUB' `
            -PublicNumber $number `
            -IdentityFields @($GitHubRepository, $number) `
            -FingerprintFields @(
                $GitHubRepository,
                $number,
                (Get-PathValue $issue @('id')),
                (Get-PathValue $issue @('node_id')),
                (Get-PathValue $issue @('state')),
                (Get-PathValue $issue @('state_reason')),
                (Get-PathValue $issue @('locked')),
                (Get-PathValue $issue @('active_lock_reason')),
                (Get-PathValue $issue @('author_association')),
                (Get-PathValue $issue @('user', 'login')),
                (Get-PathValue $issue @('title')),
                (Get-PathValue $issue @('body')),
                (Get-PathValue $issue @('comments')),
                (Get-PathValue $issue @('created_at')),
                (Get-PathValue $issue @('updated_at')),
                (Get-PathValue $issue @('closed_at')),
                (Get-PathValue $issue @('milestone', 'number')),
                (Get-PathValue $issue @('milestone', 'state')),
                (Get-PathValue $issue @('milestone', 'title')),
                $labelsHash,
                $assigneesHash
            ))) | Out-Null
    }

    foreach ($pull in (Invoke-GhItems "/repos/$GitHubRepository/pulls?state=all&per_page=100")) {
        $number = [int](Get-PathValue $pull @('number'))
        $labels = @(
            @(Get-PathValue $pull @('labels')) |
                ForEach-Object { Get-PathValue $_ @('name') }
        )
        $assignees = @(
            @(Get-PathValue $pull @('assignees')) |
                ForEach-Object { Get-PathValue $_ @('login') }
        )
        $reviewers = @(
            @(Get-PathValue $pull @('requested_reviewers')) |
                ForEach-Object { Get-PathValue $_ @('login') }
        )
        $labelsHash = Get-ValueListHash `
            "$($script:HashDomain)/github/pr-labels" $labels
        $assigneesHash = Get-ValueListHash `
            "$($script:HashDomain)/github/pr-assignees" $assignees
        $reviewersHash = Get-ValueListHash `
            "$($script:HashDomain)/github/pr-reviewers" $reviewers
        $headSha = (ConvertTo-Scalar (Get-PathValue $pull @('head', 'sha'))).ToLowerInvariant()
        if ($headSha -notmatch '^[0-9a-f]{40}$') { $headSha = '' }
        $records.Add((New-LedgerRecord `
            -Kind 'gh_pr' `
            -Source 'H25-GITHUB' `
            -PublicNumber $number `
            -PublicSha $headSha `
            -IdentityFields @($GitHubRepository, $number) `
            -FingerprintFields @(
                $GitHubRepository,
                $number,
                (Get-PathValue $pull @('id')),
                (Get-PathValue $pull @('node_id')),
                (Get-PathValue $pull @('state')),
                (Get-PathValue $pull @('locked')),
                (Get-PathValue $pull @('draft')),
                (Get-PathValue $pull @('author_association')),
                (Get-PathValue $pull @('user', 'login')),
                (Get-PathValue $pull @('title')),
                (Get-PathValue $pull @('body')),
                (Get-PathValue $pull @('created_at')),
                (Get-PathValue $pull @('updated_at')),
                (Get-PathValue $pull @('closed_at')),
                (Get-PathValue $pull @('merged_at')),
                (Get-PathValue $pull @('merge_commit_sha')),
                (Get-PathValue $pull @('head', 'sha')),
                (Get-PathValue $pull @('head', 'ref')),
                (Get-PathValue $pull @('head', 'repo', 'full_name')),
                (Get-PathValue $pull @('base', 'sha')),
                (Get-PathValue $pull @('base', 'ref')),
                (Get-PathValue $pull @('base', 'repo', 'full_name')),
                $labelsHash,
                $assigneesHash,
                $reviewersHash
            ))) | Out-Null
    }

    foreach ($branch in (Invoke-GhItems "/repos/$GitHubRepository/branches?per_page=100")) {
        $name = ConvertTo-Scalar (Get-PathValue $branch @('name'))
        $sha = (ConvertTo-Scalar (Get-PathValue $branch @('commit', 'sha'))).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{40}$') { $sha = '' }
        $records.Add((New-LedgerRecord `
            -Kind 'gh_branch' `
            -Source 'H25-GITHUB' `
            -PublicSha $sha `
            -IdentityFields @($GitHubRepository, $name) `
            -FingerprintFields @(
                $GitHubRepository,
                $name,
                (Get-PathValue $branch @('commit', 'sha')),
                (Get-PathValue $branch @('protected'))
            ))) | Out-Null
    }

    foreach ($tag in (Invoke-GhItems "/repos/$GitHubRepository/tags?per_page=100")) {
        $name = ConvertTo-Scalar (Get-PathValue $tag @('name'))
        $sha = (ConvertTo-Scalar (Get-PathValue $tag @('commit', 'sha'))).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{40}$') { $sha = '' }
        $records.Add((New-LedgerRecord `
            -Kind 'gh_tag' `
            -Source 'H25-GITHUB' `
            -PublicSha $sha `
            -IdentityFields @($GitHubRepository, $name) `
            -FingerprintFields @(
                $GitHubRepository,
                $name,
                (Get-PathValue $tag @('commit', 'sha')),
                (Get-PathValue $tag @('node_id'))
            ))) | Out-Null
    }

    return [pscustomobject]@{
        records = @($records)
        defaultSha = $defaultSha
    }
}

function Normalize-WorktreePath([string]$Path) {
    $normalized = $Path.Replace('\', '/')
    if ($normalized.Length -gt 3) { $normalized = $normalized.TrimEnd('/') }
    return $normalized
}

function Invoke-GitTryValue([string]$Repository, [string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git -C $Repository @Arguments 2>$null)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) { return '' }
    return ((@($output) -join "`n").Trim())
}

function Get-StatusRecords([string]$Worktree, [string]$WorktreeIdentity) {
    $output = & git -C $Worktree status --porcelain=v1 -z --untracked-files=all
    if ($LASTEXITCODE -ne 0) {
        throw 'git status failed; output is intentionally redacted.'
    }
    $raw = if ($null -eq $output) { '' } else { [string]$output }
    $parts = $raw.Split(
        [char[]]@([char]0),
        [StringSplitOptions]::None)
    $records = [Collections.Generic.List[object]]::new()
    $index = 0
    while ($index -lt $parts.Length) {
        $entry = $parts[$index]
        $index++
        if (-not $entry) { continue }
        if ($entry.Length -lt 3) {
            throw 'git status returned a malformed porcelain entry.'
        }
        $status = $entry.Substring(0, 2)
        $path = $entry.Substring(3)
        $originalPath = ''
        if ($status[0] -in @('R', 'C') -or $status[1] -in @('R', 'C')) {
            if ($index -ge $parts.Length) {
                throw 'git status rename entry lacks an origin.'
            }
            $originalPath = $parts[$index]
            $index++
        }
        $worktreeOid = Invoke-GitTryValue `
            $Worktree @('hash-object', '--no-filters', '--', $path)

        if ($status -ceq '??') {
            $records.Add((New-LedgerRecord `
                -Kind 'untracked_path' `
                -Source 'H25-LOCAL' `
                -IdentityFields @($WorktreeIdentity, $path) `
                -FingerprintFields @(
                    $WorktreeIdentity,
                    $status,
                    $path,
                    $originalPath,
                    $worktreeOid
                ))) | Out-Null
            continue
        }
        $indexOid = Invoke-GitTryValue `
            $Worktree @('rev-parse', '--verify', ":$path")
        if ($status[0] -notin @(' ', '?', '!')) {
            $records.Add((New-LedgerRecord `
                -Kind 'staged_path' `
                -Source 'H25-LOCAL' `
                -IdentityFields @(
                    $WorktreeIdentity,
                    'staged',
                    $path,
                    $originalPath
                ) `
                -FingerprintFields @(
                    $WorktreeIdentity,
                    $status,
                    $path,
                    $originalPath,
                    $indexOid
                ))) | Out-Null
        }
        if ($status[1] -notin @(' ', '?', '!')) {
            $records.Add((New-LedgerRecord `
                -Kind 'unstaged_path' `
                -Source 'H25-LOCAL' `
                -IdentityFields @(
                    $WorktreeIdentity,
                    'unstaged',
                    $path,
                    $originalPath
                ) `
                -FingerprintFields @(
                    $WorktreeIdentity,
                    $status,
                    $path,
                    $originalPath,
                    $worktreeOid,
                    $indexOid
                ))) | Out-Null
        }
    }
    return @($records)
}

function Get-SortedRecords([object[]]$Records) {
    return @(
        $Records | Sort-Object `
            @{ Expression = { [int]$script:KindOrder[$_.kind] } },
            @{ Expression = {
                if ($_.publicNumber) { [long]$_.publicNumber }
                else { [long]::MaxValue }
            } },
            @{ Expression = { $_.identitySha256 } }
    )
}

function Get-SourceStateHash([object[]]$Records, [string]$Domain) {
    $values = @(
        Get-SortedRecords $Records |
            ForEach-Object {
                @(
                    $_.kind,
                    $_.source,
                    $_.publicNumber,
                    $_.publicSha,
                    $_.identitySha256,
                    $_.fingerprintSha256
                ) -join '|'
            }
    )
    return Get-FieldHash -Domain $Domain -Fields $values
}

function Get-LocalCapture {
    $records = [Collections.Generic.List[object]]::new()
    $refLines = @(& git -C $LegacyRepo for-each-ref `
        --sort=refname `
        '--format=%(refname)%09%(objectname)%09%(objecttype)%09%(*objectname)%09%(upstream)%09%(worktreepath)')
    if ($LASTEXITCODE -ne 0) {
        throw 'git for-each-ref failed; output is intentionally redacted.'
    }
    foreach ($line in $refLines) {
        $fields = @(([string]$line).Split("`t", 6))
        while ($fields.Count -lt 6) { $fields += '' }
        $records.Add((New-LedgerRecord `
            -Kind 'local_ref' `
            -Source 'H25-LOCAL' `
            -IdentityFields @($fields[0]) `
            -FingerprintFields @(
                $fields[0],
                $fields[1],
                $fields[2],
                $fields[3],
                $fields[4],
                (Normalize-WorktreePath $fields[5])
            ))) | Out-Null
    }

    $stashLines = @(& git -C $LegacyRepo stash list `
        '--format=%gd%x09%H%x09%P%x09%gs')
    if ($LASTEXITCODE -ne 0) {
        throw 'git stash list failed; output is intentionally redacted.'
    }
    foreach ($line in $stashLines) {
        $fields = @(([string]$line).Split("`t", 4))
        while ($fields.Count -lt 4) { $fields += '' }
        $records.Add((New-LedgerRecord `
            -Kind 'stash' `
            -Source 'H25-LOCAL' `
            -IdentityFields @($fields[0], $fields[1]) `
            -FingerprintFields @($fields[0], $fields[1], $fields[2], $fields[3])
            )) | Out-Null
    }

    $worktreeOutput = & git -C $LegacyRepo worktree list --porcelain -z
    if ($LASTEXITCODE -ne 0) {
        throw 'git worktree list failed; output is intentionally redacted.'
    }
    $worktreeRaw = if ($null -eq $worktreeOutput) { '' } else { [string]$worktreeOutput }
    $blocks = [regex]::Split($worktreeRaw, ([char]0).ToString() + ([char]0).ToString())
    foreach ($block in $blocks) {
        if (-not $block) { continue }
        $worktree = @{}
        foreach ($line in $block.Split(
            [char[]]@([char]0),
            [StringSplitOptions]::RemoveEmptyEntries)) {
            $separator = $line.IndexOf(' ')
            if ($separator -ge 0) {
                $worktree[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
            }
            else {
                $worktree[$line] = 'true'
            }
        }
        if (-not $worktree.ContainsKey('worktree')) {
            throw 'git worktree porcelain record lacks a path.'
        }
        $rawPath = [string]$worktree['worktree']
        $path = Normalize-WorktreePath $rawPath
        $identity = Get-FieldHash `
            -Domain "$($script:HashDomain)/worktree-path" `
            -Fields @($path)
        $statusRecords = @(Get-StatusRecords $rawPath $identity)
        $statusState = Get-SourceStateHash `
            $statusRecords "$($script:HashDomain)/worktree-status"
        $records.Add((New-LedgerRecord `
            -Kind 'worktree' `
            -Source 'H25-LOCAL' `
            -IdentityFields @($path) `
            -FingerprintFields @(
                $path,
                $(if ($worktree.ContainsKey('HEAD')) { $worktree['HEAD'] } else { '' }),
                $(if ($worktree.ContainsKey('branch')) { $worktree['branch'] } else { '' }),
                $(if ($worktree.ContainsKey('detached')) { $worktree['detached'] } else { '' }),
                $(if ($worktree.ContainsKey('bare')) { $worktree['bare'] } else { '' }),
                $(if ($worktree.ContainsKey('locked')) { $worktree['locked'] } else { '' }),
                $(if ($worktree.ContainsKey('prunable')) { $worktree['prunable'] } else { '' }),
                $statusState
            ))) | Out-Null
        foreach ($statusRecord in $statusRecords) {
            $records.Add($statusRecord) | Out-Null
        }
    }
    return @($records)
}

function Get-Capture {
    $github = Get-GitHubCapture
    $local = @(Get-LocalCapture)
    $all = @($github.records) + @($local)
    return [pscustomobject]@{
        records = $all
        defaultSha = $github.defaultSha
        githubStateSha256 = Get-SourceStateHash `
            @($github.records) "$($script:HashDomain)/github-state"
        localStateSha256 = Get-SourceStateHash `
            $local "$($script:HashDomain)/local-state"
        sourceStateSha256 = Get-SourceStateHash `
            $all "$($script:HashDomain)/source-state"
    }
}

function Get-ItemLine($Item) {
    return '{"assetId":"' + $Item.assetId +
        '","kind":"' + $Item.kind +
        '","source":"' + $Item.source +
        '","publicNumber":"' + $Item.publicNumber +
        '","publicSha":"' + $Item.publicSha +
        '","identitySha256":"' + $Item.identitySha256 +
        '","fingerprintSha256":"' + $Item.fingerprintSha256 +
        '","ownerSemanticUnit":"' + $Item.ownerSemanticUnit +
        '","ownerIssue":' + $Item.ownerIssue.ToString([Globalization.CultureInfo]::InvariantCulture) +
        ',"disposition":"PENDING_M2","evidenceRef":"AIBOS-ISSUE-' +
        $Item.ownerIssue.ToString([Globalization.CultureInfo]::InvariantCulture) + '"}'
}

function Get-GroupedEvidence(
    [object[]]$Items,
    [string[]]$Lines,
    [string]$KeyName,
    [string[]]$ExpectedKeys
) {
    $groups = [ordered]@{}
    foreach ($expectedKey in $ExpectedKeys) {
        $groups[$expectedKey] = [Collections.Generic.List[string]]::new()
    }
    for ($index = 0; $index -lt $Items.Count; $index++) {
        $key = [string]$Items[$index].$KeyName
        if (-not $groups.Contains($key)) {
            $groups[$key] = [Collections.Generic.List[string]]::new()
        }
        $groups[$key].Add($Lines[$index])
    }
    $counts = [ordered]@{}
    $hashes = [ordered]@{}
    [string[]]$keys = @($groups.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    foreach ($key in $keys) {
        $counts[$key] = $groups[$key].Count
        $hashes[$key] = if ($groups[$key].Count -eq 0) {
            Get-Sha256Ascii ''
        }
        else {
            Get-Sha256Ascii ((@($groups[$key]) -join "`n") + "`n")
        }
    }
    return [pscustomobject]@{
        counts = $counts
        hashes = $hashes
    }
}

$resolvedLegacy = [IO.Path]::GetFullPath($LegacyRepo)
$gitDirectory = Invoke-GitTryValue $resolvedLegacy @('rev-parse', '--git-dir')
if (-not $gitDirectory) { throw 'Legacy repository is not a Git worktree.' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$normalizedAibosSha = $AibosBaseSha.ToLowerInvariant()

$before = Get-Capture
$after = Get-Capture
if ($before.sourceStateSha256 -cne $after.sourceStateSha256) {
    throw 'H25 source state changed during PowerShell capture.'
}
if ($before.defaultSha -cne $after.defaultSha) {
    throw 'H25 default SHA changed during PowerShell capture.'
}

$orderedRecords = @(Get-SortedRecords @($before.records))
$items = [Collections.Generic.List[object]]::new()
$lines = [Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $orderedRecords.Count; $index++) {
    $record = $orderedRecords[$index]
    $item = [pscustomobject]@{
        assetId = 'M1A-' + ($index + 1).ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
        kind = $record.kind
        source = $record.source
        publicNumber = $record.publicNumber
        publicSha = $record.publicSha
        identitySha256 = $record.identitySha256
        fingerprintSha256 = $record.fingerprintSha256
        ownerSemanticUnit = $record.ownerSemanticUnit
        ownerIssue = [int]$record.ownerIssue
    }
    $items.Add($item) | Out-Null
    $lines.Add((Get-ItemLine $item)) | Out-Null
}

$manifestLines = [Collections.Generic.List[string]]::new()
$manifestLines.Add('{') | Out-Null
$manifestLines.Add("  `"manifestVersion`": $($script:ManifestVersion),") | Out-Null
$manifestLines.Add("  `"hashDomain`": `"$($script:HashDomain)`",") | Out-Null
$manifestLines.Add("  `"cutoffId`": `"$CutoffId`",") | Out-Null
$manifestLines.Add("  `"capturedAtUtc`": `"$CapturedAtUtc`",") | Out-Null
$manifestLines.Add("  `"aibosBaseSha`": `"$normalizedAibosSha`",") | Out-Null
$manifestLines.Add("  `"h25DefaultSha`": `"$($before.defaultSha)`",") | Out-Null
$manifestLines.Add("  `"sourceStateSha256`": `"$($before.sourceStateSha256)`",") | Out-Null
$manifestLines.Add('  "items": [') | Out-Null
for ($index = 0; $index -lt $lines.Count; $index++) {
    $suffix = if ($index + 1 -lt $lines.Count) { ',' } else { '' }
    $manifestLines.Add("    $($lines[$index])$suffix") | Out-Null
}
$manifestLines.Add('  ]') | Out-Null
$manifestLines.Add('}') | Out-Null
$manifestText = (@($manifestLines) -join "`n") + "`n"
$manifestBytes = $script:Ascii.GetBytes($manifestText)
$manifestSha = Get-Sha256Bytes $manifestBytes

$category = Get-GroupedEvidence `
    @($items) `
    @($lines) `
    'kind' `
    @($script:KindOrder.Keys)
$ownership = Get-GroupedEvidence `
    @($items) `
    @($lines) `
    'ownerSemanticUnit' `
    @($script:OwnerIssues.Keys)
$categoryTotal = ($category.counts.Values | Measure-Object -Sum).Sum
$ownershipTotal = ($ownership.counts.Values | Measure-Object -Sum).Sum
if ($categoryTotal -ne $items.Count) {
    throw 'Category counts do not cover the manifest.'
}
if ($ownershipTotal -ne $items.Count) {
    throw 'Ownership counts do not cover the manifest.'
}
$identityGroups = @($items | Group-Object identitySha256)
if ($identityGroups.Count -ne $items.Count) {
    throw 'Ledger identities are not unique.'
}

$capture = [ordered]@{
    aibosBaseSha = $normalizedAibosSha
    capturedAtUtc = $CapturedAtUtc
    categoryCounts = $category.counts
    categorySha256 = $category.hashes
    cutoffId = $CutoffId
    githubStateSha256 = $before.githubStateSha256
    h25DefaultSha = $before.defaultSha
    hashDomain = $script:HashDomain
    implementation = 'powershell-standard-library-v1'
    localStateSha256 = $before.localStateSha256
    manifestSha256 = $manifestSha
    manifestVersion = $script:ManifestVersion
    ownershipCounts = $ownership.counts
    ownershipSha256 = $ownership.hashes
    recordCount = $items.Count
    sourceSetSha256 = Get-FieldHash `
        -Domain "$($script:HashDomain)/source-set" `
        -Fields @($items | ForEach-Object { $_.identitySha256 })
    sourceStateAfterSha256 = $after.sourceStateSha256
    sourceStateBeforeSha256 = $before.sourceStateSha256
    sourceStateSha256 = $before.sourceStateSha256
    sourceUnchanged = $true
}

[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
[IO.File]::WriteAllBytes((Join-Path $resolvedOutput 'manifest.json'), $manifestBytes)
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'capture.json'),
    (($capture | ConvertTo-Json -Depth 12) + "`n"),
    [Text.UTF8Encoding]::new($false))

[ordered]@{
    ok = $true
    implementation = $capture.implementation
    recordCount = $capture.recordCount
    manifestSha256 = $capture.manifestSha256
    sourceUnchanged = $true
} | ConvertTo-Json -Compress
