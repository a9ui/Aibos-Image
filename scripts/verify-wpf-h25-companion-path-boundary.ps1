param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$dll = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    & dotnet build $project -c $Configuration --nologo
    Assert-True ($LASTEXITCODE -eq 0) 'WPF build failed.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-h25-path-boundary-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Boundary fixture escaped TEMP.'

$fixtureRoot = Join-Path $runRoot 'fixture'
$outsideRoot = Join-Path $runRoot 'outside'
$sharedRoot = Join-Path $fixtureRoot 's'
$wpfLocalRoot = Join-Path $fixtureRoot 'w'
$metadataJunction = Join-Path $wpfLocalRoot 'metadata-index'
$resultPath = Join-Path $fixtureRoot 'full-result.json'
$sourcePath = Join-Path $fixtureRoot 'images\full-source.png'
$jobsPath = Join-Path $sharedRoot 'enhance\jobs.json'

$environment = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = Join-Path $wpfLocalRoot 'state.json'
    PHOTOVIEWER_WPF_FAVORITES_PATH = Join-Path $sharedRoot 'favorites.json'
    PHOTOVIEWER_WPF_SEEN_PATH = Join-Path $sharedRoot 'seen.json'
    PHOTOVIEWER_WPF_RECENT_PATH = Join-Path $sharedRoot 'recent-folders.json'
    PHOTOVIEWER_WPF_SETTINGS_PATH = Join-Path $sharedRoot 'settings.json'
    PHOTOVIEWER_WPF_ALBUMS_PATH = Join-Path $sharedRoot 'albums.json'
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = Join-Path $sharedRoot 'search-history.json'
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = $jobsPath
    PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY = $metadataJunction
    PHOTOVIEWER_BROWSER_BASE_URL = 'http://127.0.0.1:9/'
}
$previousEnvironment = @{}

try {
    New-Item -ItemType Directory -Path $fixtureRoot, $outsideRoot, $sharedRoot, $wpfLocalRoot, (Split-Path -Parent $jobsPath) -Force | Out-Null
    New-Item -ItemType Junction -Path $metadataJunction -Target $outsideRoot | Out-Null

    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_STATE_PATH, '{"version":2}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_FAVORITES_PATH, '{}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_SEEN_PATH, '{}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_RECENT_PATH, '{"version":1,"lastFolderSet":[],"recentFolderSets":[]}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_SETTINGS_PATH, '{"version":1}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_ALBUMS_PATH, '{"version":1,"albums":[]}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH, '{"version":1,"entries":[]}')
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH, '{"version":1,"jobs":[]}')

    foreach ($name in $environment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }

    & dotnet $dll --h25-enhancement-companion-smoke $resultPath --fixture-root $fixtureRoot --phase full --source-name full-source.png 2>$null
    $processExitCode = $LASTEXITCODE
    $outsideEntries = @(Get-ChildItem -LiteralPath $outsideRoot -Force)

    Assert-True ($processExitCode -ne 0) 'Reparse-point fixture was accepted.'
    Assert-True ($outsideEntries.Count -eq 0) 'Companion smoke wrote outside its owned fixture root.'
    Assert-True (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) 'Companion smoke continued after rejecting the path boundary.'
    Write-Host 'PASS: H25 companion smoke rejects final-path escape before any outside write.'
}
finally {
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }

    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $leaf = [IO.Path]::GetFileName($resolvedRunRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) `
            -or $leaf -notmatch '^aibos-h25-path-boundary-[0-9a-f]{32}$') {
            throw "Refusing to clean unexpected boundary fixture: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
