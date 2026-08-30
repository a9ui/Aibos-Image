param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [string]$TargetFrameworkOverride = '',
    [switch]$NoRestore
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
$recoveryResultPath = Join-Path $runRoot 'recovered-enhancement-references.json'
$sentinelRoot = Join-Path $runRoot 'caller-store-sentinels'
$storeEnvironment = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = Join-Path $sentinelRoot 'state.json'
    PHOTOVIEWER_WPF_FAVORITES_PATH = Join-Path $sentinelRoot 'favorites.json'
    PHOTOVIEWER_WPF_SEEN_PATH = Join-Path $sentinelRoot 'seen.json'
    PHOTOVIEWER_WPF_RECENT_PATH = Join-Path $sentinelRoot 'recent-folders.json'
    PHOTOVIEWER_WPF_SETTINGS_PATH = Join-Path $sentinelRoot 'settings.json'
    PHOTOVIEWER_WPF_ALBUMS_PATH = Join-Path $sentinelRoot 'albums.json'
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = Join-Path $sentinelRoot 'search-history.json'
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = Join-Path $sentinelRoot 'enhance\jobs.json'
}
$sentinelContents = @{
    PHOTOVIEWER_WPF_STATE_PATH = '{"version":2,"sentinel":"state"}'
    PHOTOVIEWER_WPF_FAVORITES_PATH = '{"C:\\sentinel-favorite.png":3}'
    PHOTOVIEWER_WPF_SEEN_PATH = '{"C:\\sentinel-seen.png":true}'
    PHOTOVIEWER_WPF_RECENT_PATH = '{"version":1,"lastFolderSet":[],"recentFolderSets":[],"updatedAtUtc":"2026-07-18T00:00:00Z","sentinel":"recent"}'
    PHOTOVIEWER_WPF_SETTINGS_PATH = '{"version":1,"sentinel":"settings"}'
    PHOTOVIEWER_WPF_ALBUMS_PATH = '{"version":1,"revision":0,"updatedAtUtc":"2026-07-23T00:00:00Z","albums":[],"recentAlbumIds":[],"sentinel":"albums"}'
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = '{"version":1,"entries":[],"updatedAtUtc":"2026-07-23T00:00:00Z","sentinel":"search-history"}'
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = '{"version":1,"jobs":[],"sentinel":"jobs"}'
}
$metadataSentinelDirectory = Join-Path $sentinelRoot 'metadata-index'
$metadataSentinelPath = Join-Path $metadataSentinelDirectory 'sentinel.txt'
$authJunctionFixtureRoot = Join-Path $runRoot 'companion-auth-junction-fixtures'
$authApplicationJunctionRoot = Join-Path $authJunctionFixtureRoot 'auth-app-junction'
$authApplicationJunctionOutside = Join-Path $authJunctionFixtureRoot 'auth-app-junction-outside'
$authDirectoryJunctionRoot = Join-Path $authJunctionFixtureRoot 'auth-directory-junction'
$authDirectoryJunctionOutside = Join-Path $authJunctionFixtureRoot 'auth-directory-junction-outside'
$authJunctionSentinel = 'outside-companion-auth-sentinel'
$previousEnvironment = @{}
$sentinelHashesBefore = @{}
$childExitCode = $null
$summary = $null
$previousBrowserBaseUrl = [Environment]::GetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', 'Process')
$previousMetadataIndexDirectory = [Environment]::GetEnvironmentVariable('PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY', 'Process')
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
    New-Item -ItemType Directory -Path $metadataSentinelDirectory -Force | Out-Null
    [IO.File]::WriteAllText($metadataSentinelPath, 'metadata-index-sentinel', [Text.UTF8Encoding]::new($false))
    $metadataSentinelHashBefore = (Get-FileHash -LiteralPath $metadataSentinelPath -Algorithm SHA256).Hash
    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY', $metadataSentinelDirectory, 'Process')

    New-Item -ItemType Directory -Path $authApplicationJunctionRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $authApplicationJunctionOutside -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $authDirectoryJunctionRoot 'PhotoViewer.Wpf') -Force | Out-Null
    New-Item -ItemType Directory -Path $authDirectoryJunctionOutside -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $authApplicationJunctionOutside 'sentinel.bin'),
        $authJunctionSentinel,
        [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText(
        (Join-Path $authDirectoryJunctionOutside 'sentinel.bin'),
        $authJunctionSentinel,
        [Text.Encoding]::ASCII)
    New-Item -ItemType Junction `
        -Path (Join-Path $authApplicationJunctionRoot 'PhotoViewer.Wpf') `
        -Target $authApplicationJunctionOutside `
        -ErrorAction Stop | Out-Null
    New-Item -ItemType Junction `
        -Path (Join-Path $authDirectoryJunctionRoot 'PhotoViewer.Wpf\companion-auth-v1') `
        -Target $authDirectoryJunctionOutside `
        -ErrorAction Stop | Out-Null

    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', 'http://127.0.0.1:65534/', 'Process')

    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        $buildArguments = @('build', $project, '-c', $Configuration, "-p:OutputPath=$buildOutput", '--nologo', '-v:minimal')
        if ($NoRestore) { $buildArguments += '--no-restore' }
        & $DotnetPath @buildArguments
    }
    else {
        $msbuildArguments = @('msbuild', $project, '-target:Rebuild', "-property:TargetFramework=$TargetFrameworkOverride", "-property:OutputPath=$buildOutput", "-property:Configuration=$Configuration", '-nologo', '-verbosity:minimal')
        if (-not $NoRestore) { $msbuildArguments += '-restore' }
        & $DotnetPath @msbuildArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF build output was not found: $dll"

    & $DotnetPath $dll `
        --modal-enhancement-actions-smoke $resultPath `
        --companion-auth-junction-fixture-root $authJunctionFixtureRoot
    $childExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Modal enhancement smoke did not produce JSON.'

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($childExitCode -ne 0) {
        $failureEvidence = $result | ConvertTo-Json -Depth 5 -Compress
        throw "Modal enhancement smoke exited with $childExitCode. Evidence: $failureEvidence"
    }
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
        'sourceIdentityCaseInsensitive',
        'alternateLexicalIdentity',
        'pollIdentityCanonical',
        'restartRecovery',
        'legacyPhotorealRestartRecovery',
        'legacyPhotorealModalRecovery',
        'missingLegacyPhotorealRejected',
        'lexicalOutputRejected',
        'canonicalOutputRejected',
        'navigatedDuringResponse',
        'staleResponseDiscarded',
        'passiveCompanionStartSuppressed',
        'startupCompanionAutoStart',
        'companionIdentityBusySeparated',
        'companionIdentityBusyReconnected',
        'idempotentMutationReconnected',
        'idempotentMutationExactReplay',
        'idempotentMutationNoDuplicate',
        'idempotentMutationRecoverySuppressed',
        'explicitCompanionAutoStart',
        'companionQueueRecoveryAuthenticated',
        'durableListenerHandoffSavedForDelivery',
        'durableRecoveryAfterPublish',
        'durableRecoveryCarriedRequestId',
        'durableBatchDefinitiveFailurePerItem',
        'durableLegacyWakeFallback',
        'durableRecoveryRequestsCoalesced',
        'companionConfiguredRootExact',
        'companionAppBaseAncestor',
        'companionConfiguredAncestorRejected',
        'companionIdentityRejected',
        'companionDedicatedLauncherSelected',
        'companionLegacyRollbackLauncherSelected',
        'nodeExecutableResolved',
        'closeCompleted',
        'environmentRestored',
        'pathsIsolated',
        'fingerprintsCaptured',
        'sharedStoresByteIdentical',
        'stateJsonValid',
        'sourceUntouched',
        'canonicalSourceUntouched',
        'residueFree'
    )
    foreach ($propertyName in $requiredTrue) {
        $property = $result.PSObject.Properties[$propertyName]
        Assert-True ($null -ne $property) "Smoke JSON is missing required property: $propertyName"
        Assert-True ($property.Value -eq $true) "Smoke invariant failed: $propertyName"
    }
    $recoveryBeforePublish = $result.PSObject.Properties['durableRecoveryBeforePublish']
    Assert-True ($null -ne $recoveryBeforePublish) 'Smoke JSON is missing required property: durableRecoveryBeforePublish'
    Assert-True ($recoveryBeforePublish.Value -eq $false) 'Durable enqueue recovered or pumped the queue before publishing its reservation.'
    & $DotnetPath $dll --modal-photoreal-smoke $recoveryResultPath
    $recoveryChildExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $recoveryResultPath -PathType Leaf) 'Recovered Enhancement reference smoke did not produce JSON.'
    $recoveryResult = Get-Content -LiteralPath $recoveryResultPath -Raw | ConvertFrom-Json
    if ($recoveryChildExitCode -ne 0) {
        $failureEvidence = $recoveryResult | ConvertTo-Json -Depth 5 -Compress
        throw "Recovered Enhancement reference smoke exited with $recoveryChildExitCode. Evidence: $failureEvidence"
    }
    $recoveryRequiredTrue = @(
        'recoveredReferenceExact',
        'recoveredReferenceValidEmptyJobs',
        'recoveredReferenceMalformedJobsRejected',
        'recoveredReferenceHashVector',
        'recoveredReferenceHashMismatchRejected',
        'recoveredReferenceAmbiguousRejected',
        'recoveredReferenceKnownJobRejected',
        'recoveredReferenceMutationBlocked',
        'recoveredReferencePollingPreserved',
        'recoveredReferenceReadOnly',
        'recoveredReferenceCacheReuse',
        'recoveredReferenceCacheInvalidation'
    )
    foreach ($propertyName in $recoveryRequiredTrue) {
        $property = $recoveryResult.PSObject.Properties[$propertyName]
        Assert-True ($null -ne $property) "Recovery smoke JSON is missing required property: $propertyName"
        Assert-True ($property.Value -eq $true) "Recovery smoke invariant failed: $propertyName"
    }
    $recoveryAllPassed = $recoveryRequiredTrue.Count -eq 12

    $appStorePaths = @($result.storePaths.PSObject.Properties | ForEach-Object { [IO.Path]::GetFullPath([string]$_.Value) })
    Assert-True ($appStorePaths.Count -eq 8) 'Smoke must report every durable state file used by the enhancement path.'
    foreach ($path in $appStorePaths) {
        Assert-True $path.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) "App smoke store escaped TEMP: $path"
    }

    foreach ($storeName in @('favorites', 'seen', 'recent', 'settings', 'albums', 'searchHistory', 'jobs')) {
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
    $metadataSentinelUnchanged = (Test-Path -LiteralPath $metadataSentinelPath -PathType Leaf) `
        -and $metadataSentinelHashBefore -eq (Get-FileHash -LiteralPath $metadataSentinelPath -Algorithm SHA256).Hash `
        -and @(Get-ChildItem -LiteralPath $metadataSentinelDirectory -Force).Count -eq 1
    Assert-True $metadataSentinelUnchanged 'The smoke mutated the caller-provided metadata cache sentinel.'

    # The app deletes its own isolated fixture before exiting; the verifier's
    # result remains outside that fixture until this script's finally cleanup.
    $appTempStoresRemoved = @($appStorePaths | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0
    Assert-True $appTempStoresRemoved 'The modal enhancement smoke left its internal TEMP stores behind.'

    $allPassed = $requiredTrue.Count -eq 59 `
        -and $recoveryAllPassed `
        -and $callerStoresUnchanged `
        -and $metadataSentinelUnchanged `
        -and $stateWriteCaptured `
        -and $appTempStoresRemoved
    Assert-True $allPassed 'Aggregate modal enhancement verifier did not reach allPassed.'

    $summary = [pscustomobject]@{
        allPassed = $allPassed
        message = 'Modal enhancement actions and read-only recovered output references passed with TEMP isolation.'
        childExitCode = $childExitCode
        recoveryChildExitCode = $recoveryChildExitCode
        storesUnchanged = [bool]$result.sharedStoresByteIdentical
        callerStoresUnchanged = $callerStoresUnchanged
        callerMetadataCacheUnchanged = $metadataSentinelUnchanged
        sourceUntouched = [bool]$result.sourceUntouched
        canonicalSourceUntouched = [bool]$result.canonicalSourceUntouched
        restartRecovery = [bool]$result.restartRecovery
        legacyPhotorealRestartRecovery = [bool]$result.legacyPhotorealRestartRecovery
        legacyPhotorealModalRecovery = [bool]$result.legacyPhotorealModalRecovery
        missingLegacyPhotorealRejected = [bool]$result.missingLegacyPhotorealRejected
        outputOwnership = [bool]$result.lexicalOutputRejected -and [bool]$result.canonicalOutputRejected
        staleResponseDiscarded = [bool]$result.staleResponseDiscarded
        navigationDuringResponse = [bool]$result.navigatedDuringResponse
        idempotentMutationReconnected = [bool]$result.idempotentMutationReconnected
        idempotentMutationExactReplay = [bool]$result.idempotentMutationExactReplay
        idempotentMutationNoDuplicate = [bool]$result.idempotentMutationNoDuplicate
        idempotentMutationRecoverySuppressed = [bool]$result.idempotentMutationRecoverySuppressed
        closeCompleted = [bool]$result.closeCompleted
        environmentRestored = [bool]$result.environmentRestored
        fingerprintsCaptured = [bool]$result.fingerprintsCaptured
        stateWriteCapturedInTemp = $stateWriteCaptured
        appTempStoresRemoved = $appTempStoresRemoved
        residueFree = [bool]$result.residueFree
        recoveredReferences = $recoveryAllPassed
        recoveredReferencePerformance = $recoveryResult.recoveredReferencePerformance
        recoveredReferenceDiagnostic = [string]$recoveryResult.recoveredReferenceDiagnostic
    }
    $summary | ConvertTo-Json -Depth 5
}
finally {
    foreach ($name in $storeEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY', $previousMetadataIndexDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('PHOTOVIEWER_BROWSER_BASE_URL', $previousBrowserBaseUrl, 'Process')

    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
