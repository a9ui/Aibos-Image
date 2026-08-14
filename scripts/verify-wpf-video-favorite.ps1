[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$appXamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml'
$mainXamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$mainCodePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$videoCodePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.Video.cs'
$dropCodePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.ExternalFileDrop.cs'
$japaneseResourcesPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-video-favorite-' + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-video-favorite-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$buildRoot = Join-Path $runRoot 'build'
$storesRoot = Join-Path $runRoot 'stores'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$previousEnvironment = @{}
$environmentPaths = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $storesRoot 'state.json')
    PHOTOVIEWER_WPF_FAVORITES_PATH = (Join-Path $storesRoot 'favorites.json')
    PHOTOVIEWER_WPF_SEEN_PATH = (Join-Path $storesRoot 'seen.json')
    PHOTOVIEWER_WPF_RECENT_PATH = (Join-Path $storesRoot 'recent.json')
    PHOTOVIEWER_WPF_SETTINGS_PATH = (Join-Path $storesRoot 'settings.json')
    PHOTOVIEWER_WPF_ALBUMS_PATH = (Join-Path $storesRoot 'albums.json')
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = (Join-Path $storesRoot 'search.json')
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = (Join-Path $storesRoot 'enhance\jobs.json')
    PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY = (Join-Path $storesRoot 'metadata-index')
    AIBOS_SHARED_ROOT_LOCATOR_PATH = (Join-Path $storesRoot 'shared-root.v1.json')
}

try {
    New-Item -ItemType Directory -Path $runRoot, $storesRoot -Force | Out-Null
    $appXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $appXamlPath
    $mainXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainXamlPath
    $mainCode = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainCodePath
    $videoCode = Get-Content -Raw -Encoding UTF8 -LiteralPath $videoCodePath
    $dropCode = Get-Content -Raw -Encoding UTF8 -LiteralPath $dropCodePath
    $japaneseResources = Get-Content -Raw -Encoding UTF8 -LiteralPath $japaneseResourcesPath

    $retiredLabel = ([string][char]0x30D5) + ([char]0x30A1) + ([char]0x30DC)
    if (($mainXaml + $japaneseResources) -match [regex]::Escape($retiredLabel)) {
        throw 'The retired abbreviated Favorite label remains in the WPF UI resources.'
    }
    if ($japaneseResources -notmatch 'x:Key="UiFavoriteFilterGroupTitle"' `
        -or $japaneseResources -notmatch 'x:Key="UiOriginalFavoriteFilterTitle"' `
        -or $japaneseResources -notmatch 'x:Key="UiPhotorealFavoriteFilterTitle"' `
        -or $japaneseResources -notmatch 'x:Key="UiVideoFavoriteFilterTitle"') {
        throw 'The Favorite group does not use the required short Japanese row labels.'
    }
    $pillChecks = [ordered]@{
        style = $mainXaml -match '<Style x:Key="FavoriteLevelPill"[^>]*BasedOn="\{StaticResource Pill\}"'
        retiredSwatchAbsent = $mainXaml -notmatch 'FavoriteLevelSwatch'
        minWidth = $mainXaml -match '<Setter Property="MinWidth" Value="28"/>'
        height = $mainXaml -match '<Setter Property="Height" Value="30"/>'
        originalRow = $mainXaml -match '<Grid x:Name="OriginalFavoriteFilterRow"'
        photorealRow = $mainXaml -match '<Grid x:Name="PhotorealFavoriteFilterRow"'
        videoRow = $mainXaml -match '<Grid x:Name="VideoFavoriteFilterRow"'
        originalPanel = $mainXaml -match 'x:Name="FavoriteLevelFilterPanel" Grid.Row="1" Columns="3"'
        photorealPanel = $mainXaml -match 'x:Name="PhotorealFavoriteLevelFilterPanel" Grid.Row="1" Columns="3"'
        videoPanel = $mainXaml -match 'x:Name="VideoFavoriteLevelFilterPanel" Grid.Row="1" Columns="3"'
        globalUnrated = $mainXaml -match 'x:Name="UnfavoriteOnlyFilter"[^>]*Content="\{DynamicResource UiUnratedOnly\}"'
        originalUnratedCompatibilityHidden = $mainXaml -match 'x:Name="FavoriteLevel0Filter"[^>]*Visibility="Collapsed"[^>]*IsTabStop="False"'
        photorealUnratedCompatibilityHidden = $mainXaml -match 'x:Name="PhotorealFavoriteLevel0Filter"[^>]*Visibility="Collapsed"[^>]*IsTabStop="False"'
        videoUnratedCompatibilityHidden = $mainXaml -match 'x:Name="VideoFavoriteLevel0Filter"[^>]*Visibility="Collapsed"[^>]*IsTabStop="False"'
        videoChangeHandler = $mainXaml -match 'Checked="VideoFavoriteLevelFilter_Changed"'
    }
    $failedPillChecks = @($pillChecks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
    if ($failedPillChecks.Count -gt 0) {
        throw ('The neutral Favorite level surface is incomplete: ' `
            + ($failedPillChecks -join ','))
    }
    foreach ($prefix in @('Favorite', 'PhotorealFavorite', 'VideoFavorite')) {
        foreach ($level in 1..5) {
            $levelLabelPattern = 'x:Name="' + $prefix + 'Level' + $level `
                + 'Filter"[^>]*Content="Lv ' + $level + '"[^>]*Tag="' + $level + '"'
            if ($mainXaml -notmatch $levelLabelPattern) {
                throw "Favorite filter $prefix level $level must show its Lv label."
            }
            $levelTag = [regex]::Match(
                $mainXaml,
                '<CheckBox x:Name="' + $prefix + 'Level' + $level + 'Filter"[^>]*/>')
            if (-not $levelTag.Success -or $levelTag.Value -match '\sForeground=') {
                throw "Favorite filter $prefix level $level must use the neutral pill color."
            }
        }
    }
    foreach ($partName in @(
        'compactVideoFavoriteBadge',
        'videoFavoriteBadge',
        'listVideoFavoriteBadge')) {
        if ($appXaml -notmatch ('x:Name="' + [regex]::Escape($partName) + '"')) {
            throw "Video Favorite badge is missing from a gallery template: $partName"
        }
    }
    if ($appXaml -notmatch '<Color x:Key="PhotorealFavoriteColor">#FF60A5FA</Color>' `
        -or $appXaml -notmatch '<Color x:Key="VideoFavoriteColor">#FFC084FC</Color>' `
        -or $appXaml -notmatch 'Background="\{DynamicResource VideoFavoriteBackground\}"' `
        -or $appXaml -notmatch 'AutomationProperties.Name="\{Binding VideoFavoriteAutomationName\}"') {
        throw 'Blue/ purple Favorite badge tokens or accessible names are incomplete.'
    }
    if ($mainCode -notmatch 'int VideoFavoriteLevelMask' `
        -or $mainCode -notmatch 'BuildFavoriteLevelMask' `
        -or $mainCode -notmatch 'MatchesSelectedFavoriteLevels' `
        -or $mainCode -notmatch 'snapshot\.FavoritesOnly' `
        -or $mainCode -notmatch 'VideoFavoriteFilterLevels \{ get; set; \}' `
        -or $videoCode -notmatch 'TryValidateManagedVideoVersion\(' `
        -or $videoCode -notmatch 'tile.VideoFavoriteLevel = validVersions' `
        -or $dropCode -notmatch 'ApplyTileVideoAvailability\(tile\)') {
        throw 'Video Favorite max/filter/state or transient-drop live-tile wiring is incomplete.'
    }
    if ($DotNetPath -eq 'dotnet') {
        $localDotNet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
        if (Test-Path -LiteralPath $localDotNet10 -PathType Leaf) {
            $DotNetPath = $localDotNet10
        }
    }
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    if (-not $SkipBuild) {
        & $dotNetExecutable build $project `
            -c $Configuration `
            --artifacts-path $buildRoot `
            --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $exe = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.exe' `
        -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($exe) `
        -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The isolated WPF executable was not found.'
    }

    foreach ($entry in $environmentPaths.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $previousEnvironment['DOTNET_ROOT'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    $previousEnvironment['DOTNET_ROOT_X64'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--video-favorite-smoke', ('"{0}"' -f $resultPath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        } else {
            'no stderr'
        }
        $smokeResult = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        } else {
            'no result'
        }
        throw "Video Favorite smoke exited $($process.ExitCode): $stderr`n$smokeResult"
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Video Favorite smoke did not produce its result.'
    }

    $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'missingStateUnselected',
        'maximumAndInvalidExclusion',
        'originalUnratedFilter',
        'categoryOr',
        'modalOutputFavorite',
        'modalPinnedFavoriteSource',
        'optimisticRetryRollback',
        'persistenceReload',
        'layoutNotifications',
        'surfaceContract',
        'badgeVisualContract',
        'contrastContract',
        'highContrastContract',
        'favoriteKeysRetained')
    $failed = @($required | Where-Object { -not $result.$_ })
    if (-not $result.ok -or $failed.Count -gt 0) {
        throw ('Video Favorite smoke failed: ' + ($result | ConvertTo-Json -Compress) `
            + '; failed=' + ($failed -join ','))
    }

    [pscustomobject]@{
        ok = $true
        focusedSmoke = $result
        structural = [pscustomobject]@{
            retiredLabelAbsent = $true
            shortRows = @('original', 'photoreal', 'video')
            levelButtonMinWidthDip = 28
            levelButtonHeightDip = 30
            modalFavoritePinsDisplayedSource = $true
            galleryTemplates = 3
            photorealHeart = '#FF60A5FA'
            videoHeart = '#FFC084FC'
            transientDropUsesLiveVideoHelper = $true
        }
    } | ConvertTo-Json -Depth 5
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
}
