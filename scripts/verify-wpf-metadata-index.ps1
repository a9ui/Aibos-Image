param(
    [string]$Configuration = 'Release',
    [ValidateRange(32, 2048)]
    [int]$Count = 256,
    [ValidateRange(2, 8)]
    [int]$FolderCount = 4
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-metadata-index-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'
$resultPath = Join-Path $runRoot 'result.json'
$environmentNames = @(
    'PHOTOVIEWER_WPF_STATE_PATH',
    'PHOTOVIEWER_WPF_FAVORITES_PATH',
    'PHOTOVIEWER_WPF_SEEN_PATH',
    'PHOTOVIEWER_WPF_RECENT_PATH',
    'PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH',
    'PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY'
)
$environmentBefore = @{}
foreach ($name in $environmentNames) {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name)
}
$currentDirectoryBefore = [Environment]::CurrentDirectory

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    & dotnet build $project -c $Configuration --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @(
            '--metadata-index-smoke', ('"{0}"' -f $resultPath),
            '--count', $Count,
            '--folder-count', $FolderCount
        ) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'Metadata index smoke exceeded the 120 second timeout.'
    }
    $process.Refresh()

    $details = if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        Get-Content -Raw -LiteralPath $resultPath
    }
    else {
        'no result file'
    }
    Assert-True ($process.ExitCode -eq 0) "Metadata index process exited $($process.ExitCode): $details"
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Metadata index smoke produced no JSON.'

    $result = $details | ConvertFrom-Json
    Assert-True ($result.ok -eq $true) "Metadata index smoke failed: $details"
    Assert-True ($result.count -eq $Count) "Metadata fixture count was $($result.count), expected $Count."
    Assert-True ($result.folderCount -eq $FolderCount) "Metadata folder count was $($result.folderCount), expected $FolderCount."
    Assert-True ($result.isolatedProjectRoot -eq $true) 'Metadata smoke did not resolve its shared project root inside TEMP.'

    Assert-True ($result.cold.passed -eq $true) 'Cold missing-to-write metadata scenario failed.'
    Assert-True ($result.cold.loadState -eq 'Missing') "Cold metadata load state was $($result.cold.loadState), expected Missing."
    Assert-True ($result.cold.cacheHits -eq 0 -and $result.cold.cacheMisses -eq $Count) 'Cold metadata hit/miss accounting was not 0/N.'
    Assert-True ($result.cold.durableEntryCount -eq $Count) 'Cold metadata index did not durably contain every fixture entry.'
    Assert-True ($result.cold.progressWasVisible -eq $true) 'Metadata progress was never visibly exposed during the cold load.'
    Assert-True ($result.cold.progressMonotonic -eq $true) 'Metadata progress regressed during the cold load.'
    Assert-True ($result.cold.progress -eq 100 -and $result.cold.status -eq 'ready') 'Metadata progress did not settle at ready/100%.'

    Assert-True ($result.warm.passed -eq $true) 'Separate-window warm metadata scenario failed.'
    Assert-True ($result.warm.cacheHits -eq $Count -and $result.warm.cacheMisses -eq 0) 'Warm metadata load was not an all-hit reuse.'
    Assert-True ($result.warm.indexHashUnchanged -eq $true -and $result.warm.indexMtimeUnchanged -eq $true) 'Warm metadata reuse rewrote the durable index.'

    Assert-True ($result.partialInvalidation.passed -eq $true) 'Single-file metadata invalidation scenario failed.'
    Assert-True ($result.partialInvalidation.cacheHits -eq ($Count - 1) -and $result.partialInvalidation.cacheMisses -eq 1) 'Single-file mutation did not produce N-1 hits and one miss.'
    Assert-True ($result.partialInvalidation.refreshedPromptReady -eq $true) 'Single-file mutation did not expose the refreshed prompt.'

    Assert-True ($result.corruptionRecovery.passed -eq $true) 'Checksum corruption recovery scenario failed.'
    Assert-True ($result.corruptionRecovery.checksumDetected -eq $true) 'Payload bit flip was not identified as a checksum failure.'
    Assert-True ($result.corruptionRecovery.cacheHits -eq 0 -and $result.corruptionRecovery.cacheMisses -eq $Count) 'Corrupt metadata index did not safely fall back for every source.'
    Assert-True ($result.corruptionRecovery.rebuiltEntryCount -eq $Count) 'Corrupt metadata index was not rebuilt completely.'

    Assert-True ($result.futureVersionProtection.passed -eq $true) 'Future metadata index protection scenario failed.'
    Assert-True ($result.futureVersionProtection.loadState -eq 'Unsupported') 'Future metadata index was not classified as Unsupported.'
    Assert-True ($result.futureVersionProtection.bytesUnchanged -eq $true -and $result.futureVersionProtection.mtimeUnchanged -eq $true) 'Future metadata index bytes or timestamp were overwritten.'

    Assert-True ($result.commitTimeFutureGuard.passed -eq $true) 'Commit-time future-version guard scenario failed.'
    Assert-True ($result.commitTimeFutureGuard.saveOk -eq $true -and $result.commitTimeFutureGuard.written -eq $false) 'Store.Save did not preserve the future-version target after acquiring its writer lock.'
    Assert-True ($result.commitTimeFutureGuard.bytesUnchanged -eq $true -and $result.commitTimeFutureGuard.mtimeUnchanged -eq $true) 'Commit-time future guard changed the protected index.'
    Assert-True ($result.commitTimeFutureGuard.residueFree -eq $true) 'Commit-time future guard left temp/lock residue.'

    Assert-True ($result.checksumValidMalformed.passed -eq $true) 'Checksum-valid bounded-length corruption scenario failed.'
    Assert-True ($result.checksumValidMalformed.loadState -eq 'Invalid') 'Checksum-valid malformed index was not classified as Invalid.'
    Assert-True ($null -eq $result.checksumValidMalformed.escapedException) 'Checksum-valid malformed index escaped MetadataIndexStore.Load as an exception.'
    Assert-True ($result.checksumValidMalformed.fixtureRemoved -eq $true) 'Checksum-valid malformed fixture was not removed.'

    Assert-True ($result.decodeFailurePreservation.passed -eq $true) 'Decode-failure last-complete-index preservation scenario failed.'
    Assert-True ($result.decodeFailurePreservation.cacheHits -eq ($Count - 1) -and $result.decodeFailurePreservation.cacheMisses -eq 1) 'Decode-failure scenario did not produce N-1 hits and one miss.'
    Assert-True ($result.decodeFailurePreservation.oneFailureReported -eq $true) 'Decode-failure scenario did not report one source metadata failure.'
    Assert-True ($result.decodeFailurePreservation.indexHashUnchanged -eq $true -and $result.decodeFailurePreservation.indexMtimeUnchanged -eq $true) 'Decode failure changed the last complete index.'
    Assert-True ($result.decodeFailurePreservation.durableEntryCount -eq $Count) 'Decode failure did not preserve the complete durable entry set.'

    Assert-True ($result.staleEntryPrune.passed -eq $true) 'Deleted-source stale-entry pruning scenario failed.'
    Assert-True ($result.staleEntryPrune.catalogCount -eq ($Count - 1)) 'Deleted-source catalog count was not N-1.'
    Assert-True ($result.staleEntryPrune.cacheHits -eq ($Count - 1) -and $result.staleEntryPrune.cacheMisses -eq 0) 'Deleted-source load was not an N-1 all-hit pass.'
    Assert-True ($result.staleEntryPrune.durableEntryCount -eq ($Count - 1) -and $result.staleEntryPrune.deletedEntryPruned -eq $true) 'Deleted-source durable entry was not pruned.'
    Assert-True ($result.staleEntryPrune.indexHashChanged -eq $true) 'Deleted-source pruning did not commit a reduced durable snapshot.'

    Assert-True ($result.cancellation.passed -eq $true) 'Background metadata cancellation scenario failed.'
    Assert-True ($result.cancellation.backgroundReached -eq $true -and $result.cancellation.cancelAccepted -eq $true) 'Cancellation was not accepted during background metadata.'
    Assert-True ($result.cancellation.indexHashUnchanged -eq $true -and $result.cancellation.indexMtimeUnchanged -eq $true) 'Cancellation changed the last complete metadata index.'
    Assert-True ($result.cancellation.residueFree -eq $true) 'Cancellation left metadata index temp/lock residue.'

    Assert-True ($result.sourceUnchanged -eq $true) 'The source fixture was not byte-identical after controlled mutation restoration.'
    Assert-True ($result.storesUnchanged -eq $true) 'Favorite, Seen, Recent, state, or Enhancement storage changed during metadata indexing.'
    Assert-True ($result.stateUnchanged -eq $true) 'WPF state changed during metadata indexing.'
    Assert-True ($result.favoritesUnchanged -eq $true) 'Favorite state changed during metadata indexing.'
    Assert-True ($result.seenUnchanged -eq $true) 'Seen state changed during metadata indexing.'
    Assert-True ($result.recentUnchanged -eq $true) 'Recent state changed during metadata indexing.'
    Assert-True ($result.enhancementJobsUnchanged -eq $true) 'Enhancement jobs changed during passive metadata indexing.'
    Assert-True ($result.environmentRestored -eq $true) 'The smoke route did not restore its in-process environment overrides.'
    Assert-True ($result.residueFree -eq $true) 'Metadata index verifier left temp/lock residue.'

    $indexPath = [IO.Path]::GetFullPath([string]$result.indexPath)
    $runPrefix = $runRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-True $indexPath.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase) "Metadata index escaped the verifier root: $indexPath"
    Assert-True (-not (Get-ChildItem -LiteralPath $runRoot -Recurse -File -ErrorAction Stop | Where-Object { $_.Name -like '*.tmp' -or $_.Name -like '*.lock' })) 'Verifier root contains temp/lock residue.'

    foreach ($name in $environmentNames) {
        Assert-True ([string]::Equals([Environment]::GetEnvironmentVariable($name), $environmentBefore[$name], [StringComparison]::Ordinal)) "Parent environment variable changed: $name"
    }
    Assert-True ([string]::Equals([Environment]::CurrentDirectory, $currentDirectoryBefore, [StringComparison]::OrdinalIgnoreCase)) 'Parent current directory changed.'

    $crossRoot = Join-Path $runRoot 'cross-process'
    $crossProjectRoot = Join-Path $crossRoot 'project-root'
    $crossIndexDirectory = Join-Path $crossProjectRoot 'metadata-index'
    $crossProcessTemp = Join-Path $crossProjectRoot 'process-temp'
    $crossFolder = Join-Path $runRoot 'fixture\images-00'
    $coldShotPath = Join-Path $crossProjectRoot 'cold.png'
    $coldPerfPath = Join-Path $crossProjectRoot 'cold-perf.json'
    $warmShotPath = Join-Path $crossProjectRoot 'warm.png'
    $warmPerfPath = Join-Path $crossProjectRoot 'warm-perf.json'
    Assert-True (Test-Path -LiteralPath $crossFolder -PathType Container) 'Focused smoke did not leave the isolated single-folder fixture for process-boundary verification.'
    New-Item -ItemType Directory -Path $crossIndexDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $crossProcessTemp -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $crossProjectRoot 'local-native') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $crossProjectRoot 'project.toml'), '# isolated cross-process metadata verifier root')

    $crossEnvironmentNames = @($environmentNames) + @('TEMP', 'TMP')
    $crossEnvironmentBefore = @{}
    foreach ($name in $crossEnvironmentNames) {
        $crossEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $crossEnvironmentValues = @{
        PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $crossProjectRoot 'state.json')
        PHOTOVIEWER_WPF_FAVORITES_PATH = (Join-Path $crossProjectRoot 'favorites.json')
        PHOTOVIEWER_WPF_SEEN_PATH = (Join-Path $crossProjectRoot 'seen.json')
        PHOTOVIEWER_WPF_RECENT_PATH = (Join-Path $crossProjectRoot 'recent.json')
        PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = (Join-Path $crossProjectRoot 'enhance\jobs.json')
        PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY = $crossIndexDirectory
        TEMP = $crossProcessTemp
        TMP = $crossProcessTemp
    }
    $crossEnvironmentRestored = $false
    try {
        foreach ($name in $crossEnvironmentNames) {
            [Environment]::SetEnvironmentVariable($name, $crossEnvironmentValues[$name])
        }

        $sourceManifestBefore = (Get-ChildItem -LiteralPath $crossFolder -Filter '*.png' -File |
            Sort-Object FullName |
            ForEach-Object { '{0}:{1}' -f $_.Name, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
        $crossCount = @(Get-ChildItem -LiteralPath $crossFolder -Filter '*.png' -File).Count
        Assert-True ($crossCount -gt 0) 'Cross-process fixture folder contained no PNG files.'

        $invokeShot = {
            param([string]$ShotPath, [string]$PerfPath)
            $shotProcess = Start-Process -FilePath $exe `
                -WorkingDirectory $crossProjectRoot `
                -ArgumentList @(
                    '--shot', ('"{0}"' -f $ShotPath),
                    '--folder', ('"{0}"' -f $crossFolder),
                    '--perf-log', ('"{0}"' -f $PerfPath),
                    '--screen', 'viewer'
                ) `
                -WindowStyle Hidden -PassThru
            if (-not $shotProcess.WaitForExit(120000)) {
                Stop-Process -Id $shotProcess.Id -Force -ErrorAction SilentlyContinue
                throw "Cross-process metadata shot timed out: $ShotPath"
            }
            $shotProcess.Refresh()
            Assert-True ($shotProcess.ExitCode -eq 0) "Cross-process metadata shot exited $($shotProcess.ExitCode): $ShotPath"
            Assert-True (Test-Path -LiteralPath $ShotPath -PathType Leaf) "Cross-process screenshot was missing: $ShotPath"
            Assert-True ((Get-Item -LiteralPath $ShotPath).Length -gt 0) "Cross-process screenshot was empty: $ShotPath"
            Assert-True (Test-Path -LiteralPath $PerfPath -PathType Leaf) "Cross-process perf log was missing: $PerfPath"
        }

        & $invokeShot $coldShotPath $coldPerfPath
        $coldPerf = Get-Content -Raw -LiteralPath $coldPerfPath | ConvertFrom-Json
        Assert-True ($coldPerf.MetadataIndexLoadState -eq 'Missing') "First process metadata state was $($coldPerf.MetadataIndexLoadState), expected Missing."
        Assert-True ($coldPerf.MetadataCacheHits -eq 0 -and $coldPerf.MetadataCacheMisses -eq $crossCount) 'First process was not a complete cold metadata pass.'
        Assert-True ($coldPerf.MetadataIndexSaveSucceeded -eq $true) 'First process did not save its complete metadata index.'
        $crossIndexFiles = @(Get-ChildItem -LiteralPath $crossIndexDirectory -Filter '*.pvmi' -File)
        Assert-True ($crossIndexFiles.Count -eq 1) "First process produced $($crossIndexFiles.Count) metadata indexes, expected one."
        $crossIndexPath = $crossIndexFiles[0].FullName
        $crossIndexHashBeforeWarm = (Get-FileHash -LiteralPath $crossIndexPath -Algorithm SHA256).Hash
        $crossIndexMtimeBeforeWarm = (Get-Item -LiteralPath $crossIndexPath).LastWriteTimeUtc.Ticks

        & $invokeShot $warmShotPath $warmPerfPath
        $warmPerf = Get-Content -Raw -LiteralPath $warmPerfPath | ConvertFrom-Json
        $crossIndexHashAfterWarm = (Get-FileHash -LiteralPath $crossIndexPath -Algorithm SHA256).Hash
        $crossIndexMtimeAfterWarm = (Get-Item -LiteralPath $crossIndexPath).LastWriteTimeUtc.Ticks
        $sourceManifestAfter = (Get-ChildItem -LiteralPath $crossFolder -Filter '*.png' -File |
            Sort-Object FullName |
            ForEach-Object { '{0}:{1}' -f $_.Name, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
        Assert-True ($warmPerf.MetadataIndexLoadState -eq 'Loaded') "Second process metadata state was $($warmPerf.MetadataIndexLoadState), expected Loaded."
        Assert-True ($warmPerf.MetadataCacheHits -eq $crossCount -and $warmPerf.MetadataCacheMisses -eq 0) 'Second process did not reuse every metadata entry.'
        Assert-True ([string]::Equals($crossIndexHashBeforeWarm, $crossIndexHashAfterWarm, [StringComparison]::Ordinal)) 'Second process rewrote metadata index bytes.'
        Assert-True ($crossIndexMtimeBeforeWarm -eq $crossIndexMtimeAfterWarm) 'Second process rewrote metadata index mtime.'
        Assert-True ([string]::Equals($sourceManifestBefore, $sourceManifestAfter, [StringComparison]::Ordinal)) 'Cross-process metadata loads changed source bytes.'
        Assert-True (-not (Get-ChildItem -LiteralPath $crossIndexDirectory -Recurse -File | Where-Object { $_.Name -like '*.tmp' -or $_.Name -like '*.lock' })) 'Cross-process metadata loads left temp/lock residue.'
    }
    finally {
        foreach ($name in $crossEnvironmentNames) {
            [Environment]::SetEnvironmentVariable($name, $crossEnvironmentBefore[$name])
        }
        $crossEnvironmentRestored = $crossEnvironmentNames | ForEach-Object {
            [string]::Equals(
                [Environment]::GetEnvironmentVariable($_),
                $crossEnvironmentBefore[$_],
                [StringComparison]::Ordinal)
        } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
        $crossEnvironmentRestored = $crossEnvironmentRestored -eq 0
    }
    Assert-True $crossEnvironmentRestored 'Cross-process verifier did not restore TEMP/TMP or WPF storage environment variables.'

    [pscustomobject]@{
        allPassed = $true
        message = 'Persistent WPF metadata index focused gate passed.'
        count = $result.count
        folderCount = $result.folderCount
        coldMisses = $result.cold.cacheMisses
        warmHits = $result.warm.cacheHits
        partialHits = $result.partialInvalidation.cacheHits
        partialMisses = $result.partialInvalidation.cacheMisses
        corruptionFallbacks = $result.corruptionRecovery.cacheMisses
        futureVersionPreserved = $result.futureVersionProtection.bytesUnchanged
        commitTimeFutureGuard = $result.commitTimeFutureGuard.passed
        checksumValidMalformedRejected = $result.checksumValidMalformed.passed
        decodeFailurePreserved = $result.decodeFailurePreservation.indexHashUnchanged
        staleEntryPruned = $result.staleEntryPrune.deletedEntryPruned
        cancellationPreserved = $result.cancellation.indexHashUnchanged
        crossProcessCount = $crossCount
        crossProcessColdMisses = $coldPerf.MetadataCacheMisses
        crossProcessWarmHits = $warmPerf.MetadataCacheHits
        crossProcessIndexHashUnchanged = [string]::Equals($crossIndexHashBeforeWarm, $crossIndexHashAfterWarm, [StringComparison]::Ordinal)
        crossProcessIndexMtimeUnchanged = $crossIndexMtimeBeforeWarm -eq $crossIndexMtimeAfterWarm
        crossProcessEnvironmentRestored = $crossEnvironmentRestored
        sourceUnchanged = $result.sourceUnchanged
        storesUnchanged = $result.storesUnchanged
        residueFree = $result.residueFree
    } | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
