param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [string]$TargetFrameworkOverride = '',
    [string]$VideoFixturePath = ''
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-enhancement-operation-filter-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

$buildRoot = Join-Path $runRoot 'build'
$fixtureRoot = Join-Path $runRoot 'images'
$resultPath = Join-Path $runRoot 'result.json'
$resolvedVideoFixture = ''
if (-not [string]::IsNullOrWhiteSpace($VideoFixturePath)) {
    $resolvedVideoFixture = [IO.Path]::GetFullPath($VideoFixturePath)
    Assert-True (Test-Path -LiteralPath $resolvedVideoFixture -PathType Leaf) "Video fixture was not found: $resolvedVideoFixture"
    Assert-True ([string]::Equals([IO.Path]::GetExtension($resolvedVideoFixture), '.mp4', [StringComparison]::OrdinalIgnoreCase)) 'Video fixture must be an MP4 file.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot, $fixtureRoot -Force | Out-Null
    $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7l3agAAAAFElEQVR4nGPkEpFjQAJMDKiAVD4AANMABQ+5f2QAAAAASUVORK5CYII=')
    1..3 | ForEach-Object {
        [IO.File]::WriteAllBytes((Join-Path $fixtureRoot ("source-{0}.png" -f $_)), $pngBytes)
    }

    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration --nologo -v:minimal "-p:OutputPath=$buildOutput"
    }
    else {
        & $DotnetPath msbuild $project -restore "-property:TargetFramework=$TargetFrameworkOverride" "-property:OutputPath=$buildOutput" "-property:Configuration=$Configuration" -nologo -verbosity:minimal
    }
    Assert-True ($LASTEXITCODE -eq 0) "WPF build failed with exit code $LASTEXITCODE."

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF build output was not found: $dll"
    $appArgs = @(
        $dll,
        '--enhanced-filter-smoke',
        $resultPath,
        '--folder',
        $fixtureRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($resolvedVideoFixture)) {
        $appArgs += @('--video-fixture', $resolvedVideoFixture)
    }
    & $DotnetPath @appArgs
    $childExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Enhancement operation filter smoke did not produce JSON.'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-True ($childExitCode -eq 0) "Enhancement operation filter smoke exited with $childExitCode."
    foreach ($propertyName in @(
        'ok',
        'selectedUpscaleBadge',
        'selectedPhotorealBadge',
        'upscaleVisible',
        'photorealVisible',
        'videoVisible',
        'selectedVideoBadge',
        'videoModalOpened',
        'videoMediaOpened',
        'videoNaturalDuration',
        'videoPlaybackProgress',
        'videoAutoplay',
        'videoVersionInventory',
        'videoPaused',
        'videoPauseSettled',
        'olderVideoMediaOpened',
        'olderVideoPlaybackProgress',
        'olderVideoSelected',
        'ordinaryModalStayedImage',
        'manualVideoMediaOpened',
        'manualVideoPlaybackProgress',
        'manualVideoStarted',
        'ordinaryNeighborNavigated',
        'ordinaryNeighborStayedImage',
        'videoHandlesReleased',
        'videoDefaults',
        'videoBoardOpened',
        'videoSurface',
        'videoQueueSucceeded',
        'videoRequestExact',
        'reloadPhotorealVisible',
        'reloadVideoVisible',
        'reloadVideoSettings',
        'enhancementStateUnchanged',
        'readOk'
    )) {
        Assert-True ($result.$propertyName -eq $true) "Enhancement operation invariant failed: $propertyName"
    }
    Assert-True ($result.upscaleFilteredCount -eq 1) 'Upscale-only filter did not isolate one source.'
    Assert-True ($result.photorealFilteredCount -eq 1) 'Photoreal-only filter did not isolate one source.'
    Assert-True ($result.videoCandidateCount -eq 1) 'Managed-video reader did not isolate one source.'
    Assert-True ($result.videoFilteredCount -eq 1) 'Video-only filter did not isolate one source.'
    Assert-True ($result.reloadVideoFilteredCount -eq 1) 'Video-only filter did not survive a clean reload.'
    Assert-True ($result.intersectionFilteredCount -eq 0) 'Combined filters did not use intersection semantics.'
    if ([string]::IsNullOrWhiteSpace($resolvedVideoFixture)) {
        Assert-True ($result.realVideoFixtureUsed -eq $false) 'Stub smoke unexpectedly reported a real video fixture.'
        Assert-True ($result.videoTransport -eq 'smoke-stub') 'Stub smoke did not use the isolated transport stub.'
    }
    else {
        Assert-True ($result.realVideoFixtureUsed -eq $true) 'Real MP4 smoke did not report the supplied fixture.'
        Assert-True ($result.videoTransport -eq 'media-foundation') 'Real MP4 smoke did not use Media Foundation.'
        Assert-True ([string]::IsNullOrWhiteSpace($result.videoMediaFailure)) "Media Foundation failure: $($result.videoMediaFailure)"
    }
    $result | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        Assert-True $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Refusing cleanup outside TEMP.'
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
