param(
    [string]$Configuration = "Release",
    [string]$DotnetPath = "",
    [switch]$NoRestore,
    [ValidateRange(30, 300)]
    [int]$OverallTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        'dotnet'
    }
}
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-favorite-performance-verifier-' + [guid]::NewGuid().ToString('N'))))
$artifactsRoot = Join-Path $runRoot 'artifacts'
$resultPath = Join-Path $runRoot 'favorite-performance.json'
$process = $null
$previousNugetScratch = $env:NUGET_SCRATCH

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
    if ($NoRestore) {
        $buildOutput = (Join-Path $runRoot 'build').TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        & $DotnetPath build $project `
            -c $Configuration `
            --no-restore `
            "-p:OutputPath=$buildOutput" `
            --nologo `
            -v:minimal
    }
    else {
        & $DotnetPath build $project `
            -c $Configuration `
            --artifacts-path $artifactsRoot `
            --nologo `
            -v:minimal
    }
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = if ($NoRestore) {
        Get-Item -LiteralPath (Join-Path $buildOutput 'PhotoViewer.Wpf.dll')
    }
    else {
        Get-ChildItem -LiteralPath $artifactsRoot `
            -Filter 'PhotoViewer.Wpf.dll' `
            -File `
            -Recurse |
            Where-Object { $_.FullName -match '[\\/]bin[\\/]' } |
            Select-Object -First 1
    }
    if ($null -eq $dll) {
        throw "WPF assembly was not found under the TEMP artifacts root."
    }

    $process = Start-Process `
        -FilePath $DotnetPath `
        -ArgumentList @(
            ('"{0}"' -f $dll.FullName),
            '--favorite-performance-smoke',
            ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Favorite performance smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Favorite performance smoke produced no result. Exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    [pscustomobject]@{
        ok = $result.ok
        favoriteEntryCount = $result.favoriteEntryCount
        activityEntryCount = $result.activityEntryCount
        mutationCallMilliseconds = $result.mutationCallMilliseconds
        dispatcherPingMilliseconds = $result.dispatcherPingMilliseconds
        filterOnCallMilliseconds = $result.filterOnCallMilliseconds
        filterOffCallMilliseconds = $result.filterOffCallMilliseconds
        maxFavoriteCallbackMilliseconds = $result.maxFavoriteCallbackMilliseconds
        maxDerivedVisitedTiles = $result.maxDerivedVisitedTiles
        stateWriteCount = $result.stateWriteCount
        closeDrainExact = $result.closeDrainExact
        closeFailureRecoveryExact = $result.closeFailureRecoveryExact
        closeFailureEvidence = $result.closeFailureEvidence
        completedElapsedVisible = $result.completedElapsedVisible
        restorationExact = $result.restorationExact
        sourceUnchanged = $result.sourceUnchanged
        message = $result.message
    } | ConvertTo-Json -Depth 3
    $required = @(
        'initialDerivedFavorite',
        'filterOnExact',
        'filterOffExact',
        'categoryOrContract',
        'targetedPresentation',
        'boundedUi',
        'writerExact',
        'storesExact',
        'closeDrainExact',
        'closeFailureRecoveryExact',
        'stateExtensionPreserved',
        'completedElapsedVisible',
        'restorationExact',
        'sourceUnchanged'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Favorite performance contract failed (exit $processExitCode): $($missing -join ', ')"
    }
}
finally {
    $env:NUGET_SCRATCH = $previousNugetScratch
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith(
                $tempPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
