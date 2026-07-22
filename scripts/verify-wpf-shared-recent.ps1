param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$runRoot = Join-Path $tempBase ('aibos-shared-recent-' + [guid]::NewGuid().ToString('N'))
$buildOutput = Join-Path $runRoot 'build'
$fixture = Join-Path $runRoot 'fixture'
$resultPath = Join-Path $runRoot 'result.json'

try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit $LASTEXITCODE."
    }

    $dll = Join-Path $buildOutput 'PhotoViewer.Wpf.dll'
    & dotnet $dll --shared-recent-smoke $resultPath --folder $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "WPF shared recent smoke failed with exit $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'WPF shared recent smoke produced no result.'
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'importedSharedLastFolder',
        'explicitEmptySharedWins',
        'missingUsesLocalFallback',
        'malformedUsesLocalFallback',
        'futureUsesLocalFallback',
        'wroteLastFolder',
        'writeFolderInRecent',
        'additivePreserved',
        'statePreserved',
        'reloadedSharedStateWins',
        'malformedPreserved',
        'futurePreserved',
        'unreadableExistingProtected',
        'localFallbackTracksSharedAuthority',
        'favoritesUnchanged',
        'seenUnchanged'
    )
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($result.ok -ne $true -or $failed.Count -ne 0) {
        throw "WPF shared recent contract failed: $($failed -join ', ')"
    }

    Write-Host 'WPF shared recent PASS: supported shared state (including explicit empty) is authoritative; missing/protected state uses the local fallback; writes preserve isolation.'
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
