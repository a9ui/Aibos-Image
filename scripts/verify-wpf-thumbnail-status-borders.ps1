$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
$appXaml = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml'
$mainWindowXaml = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$result = Join-Path ([IO.Path]::GetTempPath()) ("photoviewer-wpf-thumbnail-status-borders-" + [guid]::NewGuid().ToString('N') + '.json')

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

try {
    $xaml = (Get-Content -Raw -LiteralPath $appXaml) + "`n" + (Get-Content -Raw -LiteralPath $mainWindowXaml)
    $staticContracts = @(
        'FavoriteThumbnailStatusBorderBrush',
        'EnhancedThumbnailStatusBorderBrush',
        '<SolidColorBrush x:Key="EnhancedThumbnailStatusBorderBrush" Color="#38BDF8"/>',
        'AutomationProperties.Name="AI-enhanced thumbnail border color"',
        'Visibility="{Binding Fav, Converter={StaticResource CountToVis}}"',
        'Visibility="{Binding Enhanced, Converter={StaticResource BoolToVis}}"',
        'BorderThickness="2"'
    )
    $missing = @($staticContracts | Where-Object { $xaml.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
    if ($missing.Count -gt 0) {
        throw "thumbnail status border XAML contract is incomplete: $($missing -join ', ')"
    }
    $removedRainbowUi = @(
        'EnhancedThumbnailBorderRainbowRadioButton',
        'Content="Rainbow"',
        '<LinearGradientBrush x:Key="EnhancedThumbnailStatusBorderBrush"'
    )
    $stale = @($removedRainbowUi | Where-Object { $xaml.IndexOf($_, [StringComparison]::Ordinal) -ge 0 })
    if ($stale.Count -gt 0) {
        throw "removed rainbow presentation is still present: $($stale -join ', ')"
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--thumbnail-status-borders-smoke', ('"{0}"' -f $result)) `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        $details = if (Test-Path -LiteralPath $result) { Get-Content -Raw -LiteralPath $result } else { 'no result file' }
        throw "thumbnail status borders smoke exited $($process.ExitCode): $details"
    }

    $smoke = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    $required = @(
        'surfaceContract',
        'seededSettingsLoaded',
        'seededResourcesLoaded',
        'firstSaveSucceeded',
        'normalizedAndApplied',
        'legacyRainbowMigratedToSolidCyan',
        'unknownFieldsPreserved',
        'firstPersisted',
        'crossRuntimePreferenceMerge',
        'crossRuntimeStateRestored',
        'reloadPersisted',
        'resetIsDraftOnly',
        'invalidColorProtected',
        'busyWriteProtected',
        'retrySucceeded',
        'retryReloaded',
        'malformedProtected',
        'invalidSchemaProtected',
        'legacyRainbowSchemaMigrated',
        'missingDefaults',
        'missingDefaultsApplied',
        'existingO1StatusBindings',
        'noSettingsResidue'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if ($smoke.ok -ne $true -or $failed.Count -gt 0) {
        throw "thumbnail status border contract failed ($($failed -join ', ')): $(Get-Content -Raw -LiteralPath $result)"
    }

    $smoke | ConvertTo-Json -Depth 8
}
finally {
    Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue
}
