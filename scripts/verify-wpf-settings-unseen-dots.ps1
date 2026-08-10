param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-settings-unseen-dots-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$result = Join-Path $runRoot 'result.json'
$process = $null
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "WPF executable was not found: $exe"
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--settings-unseen-dots-smoke', ('"{0}"' -f $result)) `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        $details = if (Test-Path -LiteralPath $result) { Get-Content -Raw -LiteralPath $result } else { 'no result file' }
        throw "settings unseen-dots smoke exited $($process.ExitCode): $details"
    }

    $smoke = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    $required = @(
        'defaultOff',
        'gridInfoDefaultOn',
        'gridInfoVisualRealized',
        'gridInfoVisibleByDefault',
        'gridStatusVisibleBeforeToggle',
        'favoriteNotificationsDefaultOn',
        'calendarContrast',
        'searchClear',
        'sidebarScroll',
        'defaultSyncedInSettings',
        'gridInfoDefaultSyncedInSettings',
        'sidebarFocused',
        'settingsFocused',
        'accessible',
        'settingsHideGridInfo',
        'sidebarRestoreGridInfo',
        'sidebarHideGridInfo',
        'settingsToSidebar',
        'sidebarToSettings',
        'settingsReopenedSynced',
        'persistedEnabled',
        'gridInfoPersistedOff',
        'favoriteNotificationsDisabled',
        'favoriteNotificationsPersistedOff',
        'reloadSynced',
        'gridInfoReloadedOff',
        'favoriteNotificationsReloadedOff',
        'reloadSettingsFocused',
        'migrationDefaultOff',
        'gridInfoMigrationDefaultOn',
        'favoriteNotificationsMigrationDefaultOn',
        'migrationUnknownPreserved',
        'seenByteIdentical',
        'cacheIsolation',
        'sourceUntouched',
        'isolated',
        'residueFree'
    )
    $failed = @($required | Where-Object { $smoke.$_ -ne $true })
    if ($smoke.ok -ne $true -or $failed.Count -gt 0 -or $smoke.unseenCount -ne 2) {
        throw "settings unseen-dots contract failed ($($failed -join ', ')): $(Get-Content -Raw -LiteralPath $result)"
    }

    $smoke | ConvertTo-Json -Depth 8
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
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
