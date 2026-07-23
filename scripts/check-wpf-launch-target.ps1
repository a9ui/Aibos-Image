[CmdletBinding()]
param(
    [string]$ProjectPath = 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj',
    [string]$TargetPath = 'local-native\PhotoViewer.Wpf\bin\Release\net10.0-windows\PhotoViewer.Wpf.exe',
    [string]$ProvenancePath = '',
    [switch]$Record,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$buildExtensions = @('.cs', '.xaml', '.csproj', '.manifest', '.resx', '.props', '.targets')

function Resolve-RepoPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-SourceFiles([string]$ProjectRoot) {
    return @(Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File |
        Where-Object {
            $relative = $_.FullName.Substring($ProjectRoot.Length).TrimStart([char[]]@('\', '/'))
            $firstSegment = @($relative.Split([char[]]@('\', '/')))[0]
            $firstSegment -notin @('bin', 'obj') -and
            -not $_.Name.EndsWith('_wpftmp.csproj', [StringComparison]::OrdinalIgnoreCase) -and
            $buildExtensions -contains $_.Extension.ToLowerInvariant()
        } |
        Sort-Object FullName)
}

function Get-SourceFingerprint([string]$ProjectRoot, [object[]]$SourceFiles) {
    $lines = foreach ($file in $SourceFiles) {
        $relative = $file.FullName.Substring($ProjectRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        '{0}|{1}|{2}' -f $relative, $file.Length, $hash
    }
    $manifest = $lines -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest)))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-SourceRevision {
    $revisionOutput = @(& git -C $repoRoot rev-parse HEAD 2>$null)
    $gitExitCode = $LASTEXITCODE
    $revision = @($revisionOutput | Select-Object -First 1)
    if ($gitExitCode -ne 0 -or $revision.Count -ne 1 -or [string]::IsNullOrWhiteSpace($revision[0])) {
        throw "Could not resolve the git source revision for $repoRoot."
    }
    return $revision[0].Trim()
}

function Write-Result([object]$Result) {
    if ($Json) {
        $Result | ConvertTo-Json -Depth 5
    }
    elseif ($Result.status -eq 'current') {
        Write-Host '[Aibos WPF] Release executable matches this project root, source revision, and source content.'
    }
    elseif ($Result.status -eq 'recorded') {
        Write-Host '[Aibos WPF] Recorded Release executable provenance.'
    }
    else {
        Write-Host "[Aibos WPF] Release executable is $($Result.status) ($($Result.reason)); rebuilding before launch."
    }
}

try {
    $project = Resolve-RepoPath $ProjectPath
    $target = Resolve-RepoPath $TargetPath
    $provenance = if ([string]::IsNullOrWhiteSpace($ProvenancePath)) {
        $target + '.launch.json'
    } else {
        Resolve-RepoPath $ProvenancePath
    }

    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "WPF project was not found: $project"
    }

    $projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $project)).TrimEnd('\', '/')
    $sourceFiles = @(Get-SourceFiles $projectRoot)
    $latestInput = $sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $sourceFingerprint = Get-SourceFingerprint $projectRoot $sourceFiles
    $sourceRevision = Get-SourceRevision
    $targetItem = Get-Item -LiteralPath $target -ErrorAction SilentlyContinue

    if ($null -eq $targetItem) {
        $result = [pscustomobject]@{
            status = 'missing'
            reason = 'target-missing'
            target = $target
            provenance = $provenance
            repoRoot = $repoRoot
            sourceRevision = $sourceRevision
            sourceFingerprint = $sourceFingerprint
            newestInput = if ($null -ne $latestInput) { $latestInput.FullName } else { $null }
        }
        Write-Result $result
        exit 10
    }

    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

    if ($Record) {
        $stamp = [ordered]@{
            schemaVersion = 1
            repoRoot = $repoRoot
            projectPath = $project
            targetPath = $target
            sourceRevision = $sourceRevision
            sourceFingerprint = $sourceFingerprint
            targetSha256 = $targetHash
            generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        }
        $parent = Split-Path -Parent $provenance
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $temp = $provenance + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText(
                $temp,
                ($stamp | ConvertTo-Json -Depth 4),
                [Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temp -Destination $provenance -Force
        }
        finally {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        }
        Write-Result ([pscustomobject]@{
            status = 'recorded'
            reason = 'build-provenance-written'
            target = $target
            provenance = $provenance
            repoRoot = $repoRoot
            sourceRevision = $sourceRevision
            sourceFingerprint = $sourceFingerprint
            targetSha256 = $targetHash
        })
        exit 0
    }

    $status = 'current'
    $reason = 'provenance-match'
    $stamp = $null
    if (-not (Test-Path -LiteralPath $provenance -PathType Leaf)) {
        $status = 'unverified'
        $reason = 'provenance-missing'
    }
    else {
        try {
            $stamp = Get-Content -LiteralPath $provenance -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            $status = 'unverified'
            $reason = 'provenance-invalid'
        }
    }

    if ($status -eq 'current' -and [int]$stamp.schemaVersion -ne 1) {
        $status = 'unverified'; $reason = 'provenance-schema'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$stamp.repoRoot, $repoRoot)) {
        $status = 'stale'; $reason = 'repo-root-mismatch'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$stamp.projectPath, $project)) {
        $status = 'stale'; $reason = 'project-path-mismatch'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$stamp.targetPath, $target)) {
        $status = 'stale'; $reason = 'target-path-mismatch'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::Ordinal.Equals([string]$stamp.sourceRevision, $sourceRevision)) {
        $status = 'stale'; $reason = 'source-revision-mismatch'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::Ordinal.Equals([string]$stamp.sourceFingerprint, $sourceFingerprint)) {
        $status = 'stale'; $reason = 'source-content-mismatch'
    }
    elseif ($status -eq 'current' -and -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$stamp.targetSha256, $targetHash)) {
        $status = 'stale'; $reason = 'target-hash-mismatch'
    }

    $result = [pscustomobject]@{
        status = $status
        reason = $reason
        target = $target
        provenance = $provenance
        repoRoot = $repoRoot
        sourceRevision = $sourceRevision
        sourceFingerprint = $sourceFingerprint
        targetSha256 = $targetHash
        targetLastWriteUtc = $targetItem.LastWriteTimeUtc.ToString('o')
        newestInput = if ($null -ne $latestInput) { $latestInput.FullName } else { $null }
        newestInputLastWriteUtc = if ($null -ne $latestInput) { $latestInput.LastWriteTimeUtc.ToString('o') } else { $null }
    }
    Write-Result $result

    if ($status -eq 'current') {
        exit 0
    }
    exit 10
}
catch {
    [Console]::Error.WriteLine("[Aibos WPF] Freshness check failed: $($_.Exception.Message)")
    exit 2
}
