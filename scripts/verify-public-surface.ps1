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

$findings = [Collections.Generic.List[object]]::new()
$warnings = [Collections.Generic.List[object]]::new()

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

    $required = @(
        'AGENTS.md',
        'README.md',
        'SECURITY.md',
        'docs/product-contract.md',
        'docs/legacy-ledger/README.md',
        'docs/legacy-ledger/manifest-v3.json',
        'docs/legacy-ledger/summary-v3.json',
        'contracts/index.json',
        'local-native/PhotoViewer.Wpf/PhotoViewer.Wpf.csproj',
        'start_aibos.bat',
        'start_wpf.bat',
        'scripts/verify-public-surface.ps1',
        'scripts/verify-contract-index.ps1',
        'scripts/verify-shared-state-contracts.ps1',
        'scripts/lib/ContractBundles.ps1',
        'scripts/verify-legacy-asset-ledger.ps1',
        'scripts/build-legacy-asset-ledger.ps1',
        'scripts/capture-legacy-asset-ledger.ps1',
        'scripts/capture_legacy_asset_ledger.py'
    )
    foreach ($path in $required) {
        if ($path -notin $tracked -and -not (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf)) {
            Add-Finding 'missing-wpf-surface' $path 0 'Required WPF public surface is missing.'
        }
    }

    $forbiddenExact = @(
        'package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml',
        'next-env.d.ts', 'next.config.js', 'playwright.config.ts',
        'eslint.config.mjs', 'postcss.config.js', 'tsconfig.json',
        'tsconfig.node.json', 'turbo.json', 'vitest.config.ts',
        'vitest.setup.ts', 'start_viewer.bat'
    )
    foreach ($path in $tracked) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf)) {
            continue
        }
        if ($path -in $forbiddenExact -or
            $path -match '^(src|e2e|config|docs/performance)/' -or
            [IO.Path]::GetExtension($path).ToLowerInvariant() -in @('.js', '.mjs', '.cjs', '.ts', '.tsx', '.mts', '.cts')) {
            Add-Finding 'web-runtime-surface' $path 0 'The WPF repository must not track a web or Node.js runtime.'
        }
    }

    $publicSurface = @(
        'AGENTS.md', 'CLAUDE.md', 'README.md', 'SECURITY.md',
        'docs/product-contract.md', 'start_aibos.bat', 'start_wpf.bat'
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
                        'Potential credential, identity, home path, or private workspace reference. Matched content is not echoed.'
                }
            }
        }
    }

    $workflowFiles = @($tracked | Where-Object { $_ -match '^\.github/workflows/[^/]+\.ya?ml$' })
    if ($workflowFiles.Count -eq 0) {
        Add-Finding 'missing-workflow' '.github/workflows' 0 'At least one WPF verification workflow is required.'
    }
    foreach ($workflowPath in $workflowFiles) {
        $workflow = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot $workflowPath)
        if ($workflow -notmatch '(?ms)^permissions:\s*\r?\n\s+contents:\s*read\s*$') {
            Add-Finding 'workflow-permissions' $workflowPath 1 'Workflow must declare top-level contents: read.'
        }
        if ($workflow -match '(?m)^\s*pull_request_target\s*:') {
            Add-Finding 'pull-request-target' $workflowPath 1 'Public fork verification must not use pull_request_target.'
        }
        if ($workflow -match '(?i)(setup-node|\bcorepack\b|\bpnpm\b|\bnpm\b|\bnode(?:\.exe)?\b)') {
            Add-Finding 'node-workflow' $workflowPath 1 'WPF-only CI must not install or invoke Node.js tooling.'
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

    $launcher = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'start_aibos.bat')
    if ($launcher -notmatch '(?i)call\s+"?\.\\start_wpf\.bat"?' -or $launcher -match '(?i)(start_viewer|pnpm|node)') {
        Add-Finding 'wpf-launcher' 'start_aibos.bat' 1 'The primary launcher must directly delegate to start_wpf.bat only.'
    }
    $wpfLauncher = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'start_wpf.bat')
    if ($wpfLauncher -notmatch '(?i)--disable-build-servers' -or
        $wpfLauncher -notmatch '(?i)(?:-p:|-property:)UseSharedCompilation=false' -or
        $wpfLauncher -match '(?i)build-server\s+shutdown') {
        Add-Finding 'wpf-build-residency' 'start_wpf.bat' 1 'Required launcher builds must avoid persistent shared build servers without shutting down unrelated servers.'
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
        webRuntimeFiles = @($findings | Where-Object { $_.rule -eq 'web-runtime-surface' }).Count
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
