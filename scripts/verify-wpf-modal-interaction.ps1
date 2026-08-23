param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "photoviewer-wpf-modal-interaction.json"),
    [string]$DotnetPath = "dotnet",
    [string]$TargetFrameworkOverride = "",
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-modal-interaction-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    }
    else {
        & $DotnetPath msbuild $project -restore "-property:TargetFramework=$TargetFrameworkOverride" "-property:OutputPath=$buildOutput" "-property:Configuration=$Configuration" -nologo -verbosity:minimal
    }
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--modal-interaction-smoke', ('"{0}"' -f $fullOutputPath)) `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited -and
        -not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf) -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf) -and
        -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Modal interaction smoke timed out after $OverallTimeoutSeconds seconds."
    }

    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "Modal interaction smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $forcedVerifierTeardown = $false
    $processExitCode = $null
    if ($process.HasExited -or $process.WaitForExit(5000)) {
        $processExitCode = $process.ExitCode
    }
    else {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit()
        $forcedVerifierTeardown = $true
    }
    $process.Dispose()
    $process = $null
    $result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 8
    if ($forcedVerifierTeardown) {
        Write-Host "Stopped the owned verifier process after its complete result was written."
    }
    $required = @(
        'fullMotionMode',
        'accessibility',
        'lightweightGlass',
        'actualPixelsControl',
        'favoriteLevelReadout',
        'favoriteNeutralSurface',
        'favoriteActiveSurface',
        'adaptiveToolbar',
        'positionCounter',
        'fullCanvasInteraction',
        'nativeMaximizeWorkArea',
        'overlayDoesNotReduceImageArea',
        'fullScreenContract',
        'fullScreenRestoredWindow',
        'fullScreenButtonRoute',
        'fullScreenSwipeUpRoute',
        'fullScreenPreservedAfterModalClose',
        'settingsBackdropDismissedOnly',
        'browserSharedEnhancedReloaded',
        'enhancedToolbarClarity',
        'enhancementLastKnownGood',
        'enhancementDeleteDependencyFailClosed',
        'enhancementDependencyRefreshFailClosed',
        'enhancementLargeCatalogRefreshBounded',
        'enhancementRefreshDoesNotBlockModal',
        'enhancementBackgroundRefreshCompleted',
        'windowCaptionControls',
        'edgeChrome',
        'edgePercentageSetting',
        'edgeImageIntersection',
        'zoomIndicator',
        'filmstripLayout',
        'filmstripButtonStableGeometry',
        'contextMenuAction',
        'manualVisiblePersistent',
        'imageClickImmediateHide',
        'imageClickImmediateShow',
        'chromeHidden',
        'favoritePulseWhileHidden',
        'transientReveal',
        'transientRevealMotion',
        'transientExpired',
        'pointerStateContract',
        'controlAutoHideSuspended',
        'panningSuppressesChrome',
        'hiddenZoomPersistence',
        'filmstripOverlay',
        'filmstripOverlayStableGeometry',
        'chromeShown',
        'detailsOverlayStableGeometry',
        'detailsTransformStable',
        'filmstripOffSuppressesHover',
        'focusedButtonShortcuts',
        'nativeButtonKeys',
        'textInputIsolated',
        'hiddenEnhancedPersistence',
        'hiddenNavigationPersistence',
        'lastDisplayPreferenceRestored',
        'hiddenDeletePersistence',
        'swipeNext',
        'swipeDownClosed',
        'zoomedSwipeBlocked',
        'emptySurfaceClosed',
        'escapeClosed'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if (($null -ne $processExitCode -and $processExitCode -ne 0) -or
        $result.ok -ne $true -or
        $missing.Count -gt 0) {
        throw "Modal interaction contract failed (exit $processExitCode): $($missing -join ', ')"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        for ($cleanupAttempt = 1; $cleanupAttempt -le 10; $cleanupAttempt++) {
            try {
                Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
                break
            }
            catch {
                if ($cleanupAttempt -eq 10) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
