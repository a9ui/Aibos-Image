param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [string]$TargetFrameworkOverride = '',
    [switch]$NoRestore
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
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-key-bindings-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

$buildRoot = Join-Path $runRoot 'build'
$keyRoot = Join-Path $runRoot 'shared-process-state'
$writeResultPath = Join-Path $runRoot 'write.json'
$reloadResultPath = Join-Path $runRoot 'reload.json'
$resolvedDotnetPath = (Get-Command -Name $DotnetPath -ErrorAction Stop).Source
$verifierDotnetRoot = Split-Path -Parent $resolvedDotnetPath
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX64 = $env:DOTNET_ROOT_X64

try {
    $env:DOTNET_ROOT = $verifierDotnetRoot
    $env:DOTNET_ROOT_X64 = $verifierDotnetRoot
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        $buildArgs = @('build', $project, '-c', $Configuration, "-p:OutputPath=$buildOutput", '--nologo', '-v:minimal')
        if ($NoRestore) { $buildArgs += '--no-restore' }
        & $DotnetPath @buildArgs
    }
    else {
        $msbuildArgs = @('msbuild', $project, "-property:TargetFramework=$TargetFrameworkOverride", "-property:OutputPath=$buildOutput", "-property:Configuration=$Configuration", '-nologo', '-verbosity:minimal')
        if (-not $NoRestore) { $msbuildArgs += '-restore' }
        & $DotnetPath @msbuildArgs
    }
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    Assert-True (Test-Path -LiteralPath $exe -PathType Leaf) "WPF executable was not found: $exe"

    $writeProcess = Start-Process -FilePath $exe `
        -ArgumentList @(
            '--key-bindings-smoke', ('"{0}"' -f $writeResultPath),
            '--key-root', ('"{0}"' -f $keyRoot),
            '--key-phase', 'write'
        ) `
        -WindowStyle Hidden -PassThru -Wait
    $writeDetails = if (Test-Path -LiteralPath $writeResultPath) { Get-Content -Raw -LiteralPath $writeResultPath } else { 'no result file' }
    Assert-True ($writeProcess.ExitCode -eq 0) "Key-binding write process exited $($writeProcess.ExitCode): $writeDetails"
    Assert-True (Test-Path -LiteralPath $writeResultPath -PathType Leaf) 'Key-binding write process produced no JSON.'
    $write = $writeDetails | ConvertFrom-Json

    $writeRequired = @(
        'defaultsLoaded', 'persistedInvalidFallback', 'exhaustedMigrationProtected', 'exhaustedMigrationRepairable',
        'sharedCollisionRepaired', 'sharedCollisionRejected', 'newActionsPreserveExistingKeys',
        'surfaceContract', 'settingsWheelSuppressed',
        'modifierRejected', 'reservedRejected', 'winShiftTRejected', 'recordingCanceled',
        'overlappingConflictRejected', 'contextAwareReuseAllowed', 'saved', 'hintsHot', 'sharedBindingsSaved',
        'inputWheelSuppressed', 'inputVisualChildWheelSuppressed', 'buttonVisualChildWheelSuppressed',
        'oldFavoriteDisabled', 'newFavoriteHot', 'exactFavoriteHot',
        'selectAllHot', 'clearSelectionHot', 'staleHiddenSelectionSuppressed',
        'oldNextDisabled', 'nextHot', 'closeHot',
        'modalWheelReady', 'modalMetadataWheelNative', 'modalImageWheelZooms', 'aiBoardShortcutsHot', 'jobsShortcutHot',
        'oldReorderDisabled', 'reorderHot', 'oldReopenDisabled', 'reopenHot',
        'togglePassive', 'nestedUnknownPreservedExactly', 'externalUnknownDeletionPreserved',
        'externalUnknownAdditionPreserved', 'topLevelUnknownPreserved', 'largeSelectionFast',
        'sourceUntouched', 'enhancementPassive', 'residueFree'
    )
    $writeFailed = @($writeRequired | Where-Object { $write.$_ -ne $true })
    Assert-True ($write.ok -eq $true -and $writeFailed.Count -eq 0) "Write/hot-apply invariants failed ($($writeFailed -join ', ')): $writeDetails"

    $reloadProcess = Start-Process -FilePath $exe `
        -ArgumentList @(
            '--key-bindings-smoke', ('"{0}"' -f $reloadResultPath),
            '--key-root', ('"{0}"' -f $keyRoot),
            '--key-phase', 'reload'
        ) `
        -WindowStyle Hidden -PassThru -Wait
    $reloadDetails = if (Test-Path -LiteralPath $reloadResultPath) { Get-Content -Raw -LiteralPath $reloadResultPath } else { 'no result file' }
    Assert-True ($reloadProcess.ExitCode -eq 0) "Key-binding reload process exited $($reloadProcess.ExitCode): $reloadDetails"
    Assert-True (Test-Path -LiteralPath $reloadResultPath -PathType Leaf) 'Key-binding reload process produced no JSON.'
    $reload = $reloadDetails | ConvertFrom-Json

    $reloadRequired = @(
        'persistedBindingsReloaded', 'persistedHintsReloaded', 'reloadHotApplied', 'reloadExactApplied',
        'reloadNextApplied', 'reloadCloseApplied', 'settingsEscapeRescue',
        'deleteOpened', 'deleteWheelSuppressed', 'deleteEscapeRescue', 'resetDraft', 'resetSaved', 'resetHints', 'sharedDefaultsSaved',
        'customKeyDisabledAfterReset', 'defaultKeyHotAfterReset',
        'landingShortcutsSuppressed', 'openLastFolderSetShortcutExact', 'unknownMergeReloaded',
        'sourceUntouched', 'enhancementPassive', 'residueFree'
    )
    $reloadFailed = @($reloadRequired | Where-Object { $reload.$_ -ne $true })
    Assert-True ($reload.ok -eq $true -and $reloadFailed.Count -eq 0) "Separate-process reload/reset invariants failed ($($reloadFailed -join ', ')): $reloadDetails"

    $statePath = Join-Path $keyRoot 'state.json'
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    Assert-True ($state.KeyBindings.favoriteIncrease -eq 'F') 'Reset defaults were not persisted after reload.'
    Assert-True ($state.KeyBindings.nextImage -eq 'Right') 'Default Next binding was not persisted after reset.'
    Assert-True ($state.KeyBindings.photorealizeCurrentImage -eq 'R') 'Default photorealization binding was not persisted after reset.'
    Assert-True ($state.KeyBindings.openEnhancementJobs -eq 'J') 'Default Jobs binding was not persisted after reset.'
    Assert-True ($state.KeyBindings.openI2iEdit -eq 'I') 'Default AI image-edit binding was not persisted after reset.'
    Assert-True ($state.KeyBindings.openVideoGeneration -eq 'V') 'Default video-generation binding was not persisted after reset.'
    Assert-True ($state.KeyBindings.reopenLastClosedPreviewTab -eq 'Ctrl+Shift+T') 'Default reopen-tab binding was not persisted after reset.'
    Assert-True ($null -ne $state.futureTop) 'Top-level unknown state was not preserved.'
    Assert-True ($null -eq $state.KeyBindings.futureAction) 'An externally deleted nested unknown field was resurrected.'
    Assert-True ($state.KeyBindings.externalAdded.mode -eq 'exact' -and $state.KeyBindings.externalAdded.count -eq 7) 'An externally added nested unknown field was not preserved exactly.'
    Assert-True ($null -eq $state.KeyBindings.reopenLastClosedPreviewTabAlternate) 'The unreliable Win+Shift+T alternate is still advertised or persisted as a default.'
    $sharedSettings = Get-Content -Raw -LiteralPath (Join-Path $keyRoot 'settings.json') | ConvertFrom-Json
    Assert-True ($sharedSettings.keyBindings.nextImage -eq 'ArrowRight') 'Shared Browser Next binding was not reset.'
    Assert-True ($sharedSettings.keyBindings.toggleFavorite -eq 'f') 'Shared Browser Favorite binding was not reset.'
    Assert-True ($sharedSettings.keyBindings.enhanceImage -eq 'a') 'Shared Browser Enhancement binding was not preserved.'
    Assert-True ($sharedSettings.keyBindings.futureBrowserAction -eq 'x') 'Unknown future Browser binding was not preserved.'
    Assert-True ($sharedSettings.futureSetting.mode -eq 'preserve') 'Unknown shared settings field was not preserved.'

    [pscustomobject]@{
        allPassed = $true
        message = 'Editable WPF key bindings passed shared Browser settings, existing-key migration, AI board and Jobs routing, wheel isolation, 100k selection, hot-apply, two-process reload, reset, rescue-key, and passive-enhancement checks.'
        writeProcessId = $writeProcess.Id
        reloadProcessId = $reloadProcess.Id
        separateProcesses = $writeProcess.Id -ne $reloadProcess.Id
        write = $write
        reload = $reload
    } | ConvertTo-Json -Depth 8
}
finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
