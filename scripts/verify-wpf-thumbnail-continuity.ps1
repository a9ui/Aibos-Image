param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ("photoviewer-wpf-thumbnail-continuity-" + [guid]::NewGuid().ToString('N') + '.json')),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
if ($OutputPath.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) {
    throw 'OutputPath cannot contain quotes or newlines.'
}
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$dotnet = if (
    -not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT) -and
    (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe') -PathType Leaf)
) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

if (-not $SkipBuild) {
    & $dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "WPF executable was not found: $exe"
}

$resultDirectory = Split-Path -Parent $resultPath
if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
}
if (Test-Path -LiteralPath $resultPath) {
    Remove-Item -LiteralPath $resultPath -Force
}

$smokeRoot = $null
$process = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList @(
        '--thumbnail-continuity-smoke', ('"{0}"' -f $resultPath)
    ) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Thumbnail continuity smoke exceeded 30 seconds and was stopped.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $details = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        }
        else {
            'no result file'
        }
        throw "Thumbnail continuity smoke failed with exit code $($process.ExitCode): $details"
    }

    $smoke = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $smokeRoot = [string]$smoke.smokeRoot
    $required = @(
        'transientFailureObserved',
        'lockedRecoveredWithoutReload',
        'lockedFailureStateCleared',
        'corruptBecameTerminal',
        'singleFolderLoad',
        'sourcesUnchanged',
        'burstLatestWins',
        'batchBounded',
        'progressiveSliceObserved',
        'sparseSettled',
        'denseSettled',
        'layoutGenerationAdvanced',
        'finalVisibleCoverage'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if (
        $smoke.ok -ne $true -or
        $failed.Count -gt 0 -or
        [int]$smoke.corruptAttempts -ne 4 -or
        [int]$smoke.finalPlaceholders -ne 0 -or
        [int]$smoke.finalUnrealized -ne 0
    ) {
        throw "Thumbnail continuity contract failed ($($failed -join ', ')): $(Get-Content -Raw -LiteralPath $resultPath)"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    if (-not [string]::IsNullOrWhiteSpace($smokeRoot)) {
        $resolvedSmokeRoot = [IO.Path]::GetFullPath($smokeRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $smokeParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedSmokeRoot)).TrimEnd('\')
        $expectedParent = $resolvedTempRoot.TrimEnd('\')
        $leaf = Split-Path -Leaf $resolvedSmokeRoot
        if (
            $smokeParent.Equals($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
            $leaf -match '^aibos-thumbnail-continuity-[A-Za-z0-9][A-Za-z0-9.]*$'
        ) {
            Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        else {
            throw "Refusing to remove unexpected smoke root: $resolvedSmokeRoot"
        }
    }
}

$tempRootRemoved = [string]::IsNullOrWhiteSpace($smokeRoot) -or -not (Test-Path -LiteralPath $smokeRoot)
[pscustomobject]@{
    ok = $true
    tempRootRemoved = $tempRootRemoved
    resultPath = $resultPath
    smoke = $smoke
} | ConvertTo-Json -Depth 8
