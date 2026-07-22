[CmdletBinding()]
param(
    [switch]$FullTree,
    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Test-IsTextCandidate([string]$Path) {
    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    return $extension -notin @(
        '.png', '.jpg', '.jpeg', '.gif', '.webp', '.avif', '.bmp', '.ico',
        '.pdf', '.zip', '.7z', '.gz', '.bin', '.exe', '.dll', '.pdb',
        '.woff', '.woff2', '.ttf', '.otf', '.pvmi'
    )
}

function Test-IsSyntheticPathLine([string]$Line) {
    return $Line -match '(?i)C:[\\/]Users[\\/]private(?:[\\/]|[''"])'
}

$findings = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()

function Add-Finding([string]$Rule, [string]$Path, [int]$Line, [string]$Message) {
    $script:findings.Add([pscustomobject]@{
        rule = $Rule
        path = $Path
        line = $Line
        message = $Message
    }) | Out-Null
}

Push-Location $repoRoot
try {
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }

    $publicSurface = @(
        'AGENTS.md',
        'CLAUDE.md',
        'README.md',
        'SECURITY.md',
        'docs/product-contract.md',
        'start_aibos.bat',
        'start_viewer.bat',
        'start_wpf.bat'
    )
    $paths = if ($FullTree) {
        $tracked
    }
    else {
        @($tracked | Where-Object { $_ -in $publicSurface -or $_ -like '.github/*' })
    }

    $patterns = [ordered]@{
        'windows-user-home' = '(?i)[A-Z]:[\\/]Users[\\/][^\\/\r\n]+'
        'mac-user-home' = '(?i)(^|[/\\])Users[/\\][^/\\\s]+'
        'linux-user-home' = '(?i)(^|[/\\])home[/\\][^/\\\s]+'
        'private-tools-root' = '(?i)Desktop[/\\]+Tools'
        'private-chat-project' = '(?i)https://chatgpt\.com/g/g-p-'
        'pem-private-key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        'github-token' = '(?i)(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]+'
        'aws-access-key' = '(?<![A-Z0-9])AKIA[0-9A-Z]{16}(?![A-Z0-9])'
        'openai-key' = '(?i)(?<![A-Za-z0-9])sk-(?:proj-)?[A-Za-z0-9_-]{20,}'
        'email-address' = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    }

    foreach ($relativePath in $paths) {
        if ($relativePath -eq 'scripts/verify-public-surface.ps1') { continue }
        if (-not (Test-IsTextCandidate $relativePath)) { continue }
        $absolutePath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) { continue }

        $lineNumber = 0
        Get-Content -LiteralPath $absolutePath -Encoding UTF8 | ForEach-Object {
            $lineNumber++
            $line = [string]$_
            if (Test-IsSyntheticPathLine $line) { return }
            if ($line -match '(?i)@example\.(?:com|org|net)\b') { return }
            if ($line -match '(?i)@users\.noreply\.github\.com\b') { return }
            foreach ($entry in $patterns.GetEnumerator()) {
                if ($line -match $entry.Value) {
                    Add-Finding $entry.Key $relativePath $lineNumber `
                        'Potential credential, personal identity, home path, or private workspace reference. Matched content is intentionally not echoed.'
                }
            }
        }
    }

    $workflowFiles = @($tracked | Where-Object { $_ -match '^\.github/workflows/[^/]+\.ya?ml$' })
    if ($workflowFiles.Count -eq 0) {
        Add-Finding 'missing-workflow' '.github/workflows' 0 'At least one verification workflow is required.'
    }

    foreach ($workflowPath in $workflowFiles) {
        $workflow = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot $workflowPath)
        if ($workflow -notmatch '(?ms)^permissions:\s*\r?\n\s+contents:\s*read\s*$') {
            Add-Finding 'workflow-permissions' $workflowPath 1 'Workflow must declare top-level contents: read.'
        }
        if ($workflow -match '(?m)^\s*pull_request_target\s*:') {
            Add-Finding 'pull-request-target' $workflowPath 1 'Public fork verification must not use pull_request_target.'
        }

        foreach ($line in ($workflow -split "`r?`n")) {
            if ($line -notmatch '^\s*-?\s*uses:\s*([^\s#]+)') { continue }
            $reference = $Matches[1]
            if ($reference -notmatch '^actions/[A-Za-z0-9_.-]+@[0-9a-fA-F]{40}$') {
                Add-Finding 'action-policy' $workflowPath 1 'Every action must be GitHub-owned and pinned to a full commit SHA.'
            }
        }

        if ($workflow -match 'actions/checkout@' -and
            $workflow -notmatch '(?ms)uses:\s*actions/checkout@[0-9a-fA-F]{40}.*?persist-credentials:\s*false') {
            Add-Finding 'checkout-credentials' $workflowPath 1 'Checkout must disable persisted credentials.'
        }
    }

    $package = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'package.json') | ConvertFrom-Json
    foreach ($scriptName in @('dev', 'start')) {
        if ([string]$package.scripts.$scriptName -notmatch '127\.0\.0\.1') {
            Add-Finding 'loopback-script' 'package.json' 1 "Script '$scriptName' must bind to 127.0.0.1."
        }
    }
    if ($package.private -ne $true) {
        Add-Finding 'npm-publication' 'package.json' 1 'package.json must remain private.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'LICENSE') -PathType Leaf)) {
        $warnings.Add([pscustomobject]@{
            rule = 'missing-license'
            message = 'No LICENSE exists; source is publicly visible but reuse rights are not granted.'
        }) | Out-Null
    }

    $result = [pscustomobject]@{
        ok = $findings.Count -eq 0
        mode = if ($FullTree) { 'full-tree' } else { 'public-surfaces' }
        filesChecked = @($paths).Count
        workflowFilesChecked = $workflowFiles.Count
        findings = @($findings)
        warnings = @($warnings)
    }
    $json = $result | ConvertTo-Json -Depth 8
    if ($OutputPath) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
    if (-not $result.ok) { throw 'Public surface verification failed.' }
}
finally {
    Pop-Location
}
