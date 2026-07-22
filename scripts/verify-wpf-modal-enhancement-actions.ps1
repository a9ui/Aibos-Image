param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-modal-enhancement-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'modal-enhancement-actions.json'
$sentinelRoot = Join-Path $runRoot 'caller-store-sentinels'
$storeEnvironment = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = Join-Path $sentinelRoot 'state.json'
    PHOTOVIEWER_WPF_FAVORITES_PATH = Join-Path $sentinelRoot 'favorites.json'
    PHOTOVIEWER_WPF_SEEN_PATH = Join-Path $sentinelRoot 'seen.json'
    PHOTOVIEWER_WPF_RECENT_PATH = Join-Path $sentinelRoot 'recent-folders.json'
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = Join-Path $sentinelRoot 'enhance\jobs.json'
}
$sentinelContents = @{
    PHOTOVIEWER_WPF_STATE_PATH = '{"version":2,"sentinel":"state"}'
    PHOTOVIEWER_WPF_FAVORITES_PATH = '{"C:\\sentinel-favorite.png":3}'
    PHOTOVIEWER_WPF_SEEN_PATH = '{"C:\\sentinel-seen.png":true}'
    PHOTOVIEWER_WPF_RECENT_PATH = '{"version":1,"lastFolderSet":[],"recentFolderSets":[],"updatedAtUtc":"2026-07-18T00:00:00Z","sentinel":"recent"}'
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = '{"version":1,"jobs":[],"sentinel":"jobs"}'
}
$previousEnvironment = @{}
$sentinelHashesBefore = @{}
$childExitCode = $null
$summary = $null
$previousBrowserBaseUrl = [Environment]::GetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', 'Process')
foreach ($name in $storeEnvironment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    foreach ($name in $storeEnvironment.Keys) {
        $path = [IO.Path]::GetFullPath($storeEnvironment[$name])
        Assert-True $path.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) "$name must stay under TEMP."
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, $sentinelContents[$name], [Text.UTF8Encoding]::new($false))
        $sentinelHashesBefore[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [Environment]::SetEnvironmentVariable($name, $path, 'Process')
    }

    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', 'http://127.0.0.1:65534/', 'Process')

    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF build output was not found: $dll"

    & dotnet $dll --modal-enhancement-actions-smoke $resultPath
    $childExitCode = $LASTEXITCODE
    Assert-True ($childExitCode -eq 0) "Modal enhancement smoke exited with $childExitCode."
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Modal enhancement smoke did not produce JSON.'

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $requiredTrue = @(
        'ok',
        'selected',
        'opened',
        'refreshedEmpty',
        'started',
        'queuedUi',
        'canceled',
        'canceledUi',
        'refreshedSucceeded',
        'outputAvailable',
        'toggledEnhanced',
        'deletedOutput',
        'originalPreserved',
        'outputRemoved',
        'createContract',
        'routesOk',
        'navigatedDuringResponse',
        'staleResponseDiscarded',
        'closeCompleted',
        'environmentRestored',
        'pathsIsolated',
        'fingerprintsCaptured',
        'sharedStoresByteIdentical',
        'stateJsonValid',
        'sourceUntouched',
        'residueFree'
    )
    foreach ($propertyName in $requiredTrue) {
        $property = $result.PSObject.Properties[$propertyName]
        Assert-True ($null -ne $property) "Smoke JSON is missing required property: $propertyName"
        Assert-True ($property.Value -eq $true) "Smoke invariant failed: $propertyName"
    }

    $appStorePaths = @($result.storePaths.PSObject.Properties | ForEach-Object { [IO.Path]::GetFullPath([string]$_.Value) })
    Assert-True ($appStorePaths.Count -eq 5) 'Smoke must report state, favorites, seen, recent, and jobs paths.'
    foreach ($path in $appStorePaths) {
        Assert-True $path.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) "App smoke store escaped TEMP: $path"
    }

    foreach ($storeName in @('favorites', 'seen', 'recent', 'jobs')) {
        $before = [string]$result.storeFingerprintsBefore.$storeName
        $after = [string]$result.storeFingerprintsAfter.$storeName
        Assert-True (-not [string]::IsNullOrWhiteSpace($before) -and $before -ne 'missing') "Missing before fingerprint for $storeName."
        Assert-True ($before -eq $after) "$storeName changed during the modal enhancement smoke."
    }

    $stateBefore = [string]$result.storeFingerprintsBefore.state
    $stateAfter = [string]$result.storeFingerprintsAfter.state
    $stateWriteCaptured = -not [string]::IsNullOrWhiteSpace($stateBefore) `
        -and -not [string]::IsNullOrWhiteSpace($stateAfter) `
        -and $stateBefore -ne 'missing' `
        -and $stateAfter -ne 'missing' `
        -and $stateBefore -ne $stateAfter
    Assert-True $stateWriteCaptured 'Expected close-time state write was not captured in the isolated TEMP state file.'

    $callerStoresUnchanged = $true
    $sentinelHashesAfter = @{}
    foreach ($name in $storeEnvironment.Keys) {
        $path = $storeEnvironment[$name]
        $sentinelHashesAfter[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($sentinelHashesBefore[$name] -ne $sentinelHashesAfter[$name]) {
            $callerStoresUnchanged = $false
        }
    }
    Assert-True $callerStoresUnchanged 'The smoke mutated a caller-provided store sentinel.'

    # The app deletes its own isolated fixture before exiting; the verifier's
    # result remains outside that fixture until this script's finally cleanup.
    $appTempStoresRemoved = @($appStorePaths | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0
    Assert-True $appTempStoresRemoved 'The modal enhancement smoke left its internal TEMP stores behind.'

    $allPassed = $requiredTrue.Count -eq 26 `
        -and $callerStoresUnchanged `
        -and $stateWriteCaptured `
        -and $appTempStoresRemoved
    Assert-True $allPassed 'Aggregate modal enhancement verifier did not reach allPassed.'

    $summary = [pscustomobject]@{
        allPassed = $allPassed
        message = 'Modal enhancement actions, TEMP store isolation, stale-response navigation guard, and cleanup passed.'
        childExitCode = $childExitCode
        storesUnchanged = [bool]$result.sharedStoresByteIdentical
        callerStoresUnchanged = $callerStoresUnchanged
        sourceUntouched = [bool]$result.sourceUntouched
        staleResponseDiscarded = [bool]$result.staleResponseDiscarded
        navigationDuringResponse = [bool]$result.navigatedDuringResponse
        closeCompleted = [bool]$result.closeCompleted
        environmentRestored = [bool]$result.environmentRestored
        fingerprintsCaptured = [bool]$result.fingerprintsCaptured
        stateWriteCapturedInTemp = $stateWriteCaptured
        appTempStoresRemoved = $appTempStoresRemoved
        residueFree = [bool]$result.residueFree
    }
    $summary | ConvertTo-Json -Depth 5
}
finally {
    foreach ($name in $storeEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', $previousBrowserBaseUrl, 'Process')

    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
