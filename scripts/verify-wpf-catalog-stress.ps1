param(
    [string]$Configuration = "Release",
    [string]$ExecutablePath = "",
    [switch]$SkipBuild,
    [int]$Count = 20000,
    [int]$FolderCount = 1,
    [string]$OutputPath = (Join-Path $env:TEMP ("photoviewer-wpf-catalog-stress-" + [guid]::NewGuid().ToString('N') + ".json")),
    [int]$UnresponsiveStreakLimitMs = 750,
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
if ($Count -lt 2) { throw 'Count must be at least 2.' }
if ($FolderCount -lt 1 -or $FolderCount -gt $Count) { throw 'FolderCount must be between 1 and Count.' }
if ($OutputPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }
if ($UnresponsiveStreakLimitMs -lt 1) { throw 'UnresponsiveStreakLimitMs must be positive.' }
if ($OverallTimeoutSeconds -lt 1) { throw 'OverallTimeoutSeconds must be positive.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$defaultExe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$exe = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { $defaultExe } else { [IO.Path]::GetFullPath($ExecutablePath) }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempRootPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $outputFullPath.StartsWith($tempRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF executable was not found: $exe" }

Remove-Item -LiteralPath $outputFullPath -Force -ErrorAction SilentlyContinue
if (-not ('PhotoViewerWmNullProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PhotoViewerWmNullProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
'@
}

$process = $null
$spawnedProcessId = $null
$processExitCode = $null
$processTerminatedByVerifier = $false
$monitorError = $null
$timedOut = $false
$resultObservedBeforeExit = $false
$overallWatch = [Diagnostics.Stopwatch]::new()
$probeWatch = [Diagnostics.Stopwatch]::new()
$windowHandleSeenMs = $null
$probeCount = 0
$probeTimeoutCount = 0
$currentUnresponsiveStartMs = $null
$longestUnresponsiveStreakMs = 0L
$unresponsiveSegments = [Collections.Generic.List[object]]::new()
$overallTimeoutMs = [int64]$OverallTimeoutSeconds * 1000
try {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--catalog-stress-smoke', ('"{0}"' -f $outputFullPath), '--count', $Count.ToString(), '--folder-count', $FolderCount.ToString()) `
        -WindowStyle Hidden -PassThru
    $spawnedProcessId = $process.Id
    $overallWatch.Start()
    $probeWatch.Start()

    while ($true) {
        $process.Refresh()
        $hasExited = $process.HasExited
        if (Test-Path -LiteralPath $outputFullPath) {
            $resultObservedBeforeExit = -not $hasExited
            break
        }
        if ($hasExited) {
            break
        }
        if ($overallWatch.ElapsedMilliseconds -ge $overallTimeoutMs) {
            $timedOut = $true
            break
        }

        $windowHandle = $process.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero) {
            if ($null -eq $windowHandleSeenMs) {
                $windowHandleSeenMs = $probeWatch.ElapsedMilliseconds
            }

            $probeStartMs = $probeWatch.ElapsedMilliseconds
            $messageResult = [IntPtr]::Zero
            # WM_NULL + SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT.
            $responsive = [PhotoViewerWmNullProbe]::SendMessageTimeout(
                $windowHandle,
                0,
                [IntPtr]::Zero,
                [IntPtr]::Zero,
                0x23,
                100,
                [ref]$messageResult)
            $probeEndMs = $probeWatch.ElapsedMilliseconds
            $probeCount++
            if ($responsive -eq [IntPtr]::Zero) {
                $probeTimeoutCount++
                if ($null -eq $currentUnresponsiveStartMs) {
                    $currentUnresponsiveStartMs = $probeStartMs
                }
                $longestUnresponsiveStreakMs = [Math]::Max($longestUnresponsiveStreakMs, $probeEndMs - $currentUnresponsiveStartMs)
            }
            else {
                if ($null -ne $currentUnresponsiveStartMs) {
                    $segmentDurationMs = $probeStartMs - $currentUnresponsiveStartMs
                    $unresponsiveSegments.Add([pscustomobject]@{
                        startMs = $currentUnresponsiveStartMs
                        endMs = $probeStartMs
                        durationMs = $segmentDurationMs
                    })
                    $longestUnresponsiveStreakMs = [Math]::Max($longestUnresponsiveStreakMs, $segmentDurationMs)
                }
                $currentUnresponsiveStartMs = $null
            }
        }

        Start-Sleep -Milliseconds 15
    }

    # The app writes its result before deleting the isolated 20k fixture. Stop
    # measuring UI liveness at that contract boundary, then bound cleanup by the
    # same overall deadline.
    if ($probeWatch.IsRunning) {
        $probeWatch.Stop()
    }
    $process.Refresh()
    if (-not $process.HasExited -and -not $timedOut) {
        $remainingMs = [Math]::Max(0, $overallTimeoutMs - $overallWatch.ElapsedMilliseconds)
        if ($remainingMs -le 0 -or -not $process.WaitForExit([int][Math]::Min([int]::MaxValue, $remainingMs))) {
            $timedOut = $true
        }
    }
}
catch {
    $monitorError = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
}
finally {
    if ($probeWatch.IsRunning) {
        $probeWatch.Stop()
    }
    if ($null -ne $currentUnresponsiveStartMs) {
        $segmentEndMs = $probeWatch.ElapsedMilliseconds
        $unresponsiveSegments.Add([pscustomobject]@{
            startMs = $currentUnresponsiveStartMs
            endMs = $segmentEndMs
            durationMs = $segmentEndMs - $currentUnresponsiveStartMs
        })
        $longestUnresponsiveStreakMs = [Math]::Max($longestUnresponsiveStreakMs, $segmentEndMs - $currentUnresponsiveStartMs)
    }

    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $processTerminatedByVerifier = $true
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            [void]$process.WaitForExit(5000)
            $process.Refresh()
        }
        if ($process.HasExited) {
            $processExitCode = $process.ExitCode
        }
    }
    if ($overallWatch.IsRunning) {
        $overallWatch.Stop()
    }
}

$resultReadError = $null
if (Test-Path -LiteralPath $outputFullPath) {
    try {
        $result = Get-Content -Raw -LiteralPath $outputFullPath | ConvertFrom-Json
    }
    catch {
        $resultReadError = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
        $result = [pscustomobject]@{ ok = $false }
    }
}
else {
    $result = [pscustomobject]@{ ok = $false }
}

$result | Add-Member -Force -NotePropertyName spawnedProcessId -NotePropertyValue $spawnedProcessId
$result | Add-Member -Force -NotePropertyName processExitCode -NotePropertyValue $processExitCode
$result | Add-Member -Force -NotePropertyName processTerminatedByVerifier -NotePropertyValue $processTerminatedByVerifier
$result | Add-Member -Force -NotePropertyName timedOut -NotePropertyValue $timedOut
$result | Add-Member -Force -NotePropertyName overallTimeoutSeconds -NotePropertyValue $OverallTimeoutSeconds
$result | Add-Member -Force -NotePropertyName overallElapsedMs -NotePropertyValue $overallWatch.ElapsedMilliseconds
$result | Add-Member -Force -NotePropertyName resultObservedBeforeExit -NotePropertyValue $resultObservedBeforeExit
$result | Add-Member -Force -NotePropertyName monitorError -NotePropertyValue $monitorError
$result | Add-Member -Force -NotePropertyName resultReadError -NotePropertyValue $resultReadError
$result | Add-Member -Force -NotePropertyName externalProbeElapsedMs -NotePropertyValue $probeWatch.ElapsedMilliseconds
$result | Add-Member -Force -NotePropertyName windowHandleSeenMs -NotePropertyValue $windowHandleSeenMs
$result | Add-Member -Force -NotePropertyName wmNullProbeCount -NotePropertyValue $probeCount
$result | Add-Member -Force -NotePropertyName wmNullTimeoutCount -NotePropertyValue $probeTimeoutCount
$result | Add-Member -Force -NotePropertyName maxUnresponsiveStreakMs -NotePropertyValue $longestUnresponsiveStreakMs
$result | Add-Member -Force -NotePropertyName maxAllowedUnresponsiveStreakMs -NotePropertyValue $UnresponsiveStreakLimitMs
$result | Add-Member -Force -NotePropertyName unresponsiveSegments -NotePropertyValue @($unresponsiveSegments)
$structuralFailures = @()
if ($timedOut) { $structuralFailures += "overall deadline of $OverallTimeoutSeconds seconds expired" }
if ($null -ne $monitorError) { $structuralFailures += "monitor failed: $monitorError" }
if ($null -ne $resultReadError) { $structuralFailures += "result JSON could not be read: $resultReadError" }
if (-not (Test-Path -LiteralPath $outputFullPath)) { $structuralFailures += "process did not produce $outputFullPath" }
if ($processTerminatedByVerifier -and -not $timedOut -and $null -eq $monitorError) { $structuralFailures += 'process required verifier cleanup before the deadline' }
if ($null -ne $processExitCode -and $processExitCode -ne 0 -and -not $processTerminatedByVerifier) { $structuralFailures += "process exit code $processExitCode" }
if ($result.ok -ne $true) { $structuralFailures += 'result.ok was false' }
if ($result.cleanupBoundaryPublished -ne $true) {
    $structuralFailures += 'pre-cleanup liveness boundary was not published'
}
if ($result.cleanupAttempted -ne $true -or $result.cleanupSucceeded -ne $true -or -not [string]::IsNullOrWhiteSpace($result.cleanupError)) {
    $structuralFailures += "temp fixture cleanup did not succeed: $($result.cleanupError)"
}
$cleanupRootFull = $null
try {
    if ([string]::IsNullOrWhiteSpace($result.smokeRoot)) {
        throw 'result smokeRoot was empty'
    }
    $cleanupRootFull = [IO.Path]::GetFullPath([string]$result.smokeRoot)
    $cleanupLeaf = [IO.Path]::GetFileName($cleanupRootFull.TrimEnd('\', '/'))
    $cleanupParent = [IO.Path]::GetDirectoryName($cleanupRootFull.TrimEnd('\', '/'))
    if (-not [string]::Equals($cleanupParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
        -or $cleanupLeaf -notmatch '^photoviewer-wpf-catalog-stress-[0-9a-f]{32}$') {
        throw "cleanup root was not the exact generated TEMP fixture root: $cleanupRootFull"
    }
    if (Test-Path -LiteralPath $cleanupRootFull) {
        throw "cleanup root still exists: $cleanupRootFull"
    }
}
catch {
    $structuralFailures += $_.Exception.Message
}
if ($result.requestedCount -ne $Count) { $structuralFailures += "requested count was $($result.requestedCount)" }
if ($result.requestedFolderCount -ne $FolderCount -or $result.loadedFolderCount -ne $FolderCount) { $structuralFailures += "requested/loaded folder counts were $($result.requestedFolderCount)/$($result.loadedFolderCount)" }
if ($result.fixtureCount -ne $Count) { $structuralFailures += "fixture count was $($result.fixtureCount)" }
if ($result.catalogCount -ne $Count -or $result.filteredCount -ne $Count) { $structuralFailures += "catalog/filtered counts were $($result.catalogCount)/$($result.filteredCount)" }
if ($result.silentTruncateCount -ne 0) { $structuralFailures += "silent truncate count was $($result.silentTruncateCount)" }
if ($result.gridRealized -gt $result.gridMaximum) { $structuralFailures += "grid realization exceeded bound ($($result.gridRealized) > $($result.gridMaximum))" }
if ($result.gridDeferred -ne ($Count - $result.gridRealized)) { $structuralFailures += "grid deferred count was $($result.gridDeferred)" }
if ($result.gridItemsSourceCount -ne $Count -or $result.gridUsesFullExtentVirtualization -ne $true) { $structuralFailures += "grid full-extent source/virtualization was $($result.gridItemsSourceCount)/$($result.gridUsesFullExtentVirtualization)" }
if ($result.gridExtentHeight -le $result.gridViewportHeight -or $result.gridInitialFirstVisible -ne 0) { $structuralFailures += "grid initial extent/visible range was $($result.gridExtentHeight)/$($result.gridViewportHeight), first $($result.gridInitialFirstVisible)" }
if ($result.listBounded -ne $true) { $structuralFailures += 'list realization was not recycling/bounded' }
if ($result.listViewportThumbnailLoaded -ne $true) { $structuralFailures += "List viewport thumbnail scheduler did not decode index $($result.listThumbnailProbeIndex)" }
if ($result.selectedTail -ne $true -or $result.tailCanonicalSelected -ne $true) { $structuralFailures += 'tail canonical selection failed' }
if ($result.tailGridWindowContains -ne $true -or $result.tailCardsSelectedItemsContains -ne $true -or $result.tailGridContainerRealized -ne $true -or $result.tailGridContainerSelected -ne $true) { $structuralFailures += 'tail grid visual selection/container failed' }
if ($result.tailGridLastVisible -ne ($Count - 1)) { $structuralFailures += "tail grid last visible index was $($result.tailGridLastVisible)" }
if ($result.tailListModeRoundTrip -ne $true -or $result.tailListCanonicalSelected -ne $true -or $result.tailRowsSelectedItemsContains -ne $true -or $result.tailListContainerRealized -ne $true -or $result.tailListContainerSelected -ne $true) { $structuralFailures += 'tail List-mode canonical/visual round trip failed' }
if ($result.tailGridModeRoundTrip -ne $true -or $result.tailGridRoundTripCanonicalSelected -ne $true -or $result.tailGridRoundTripWindowContains -ne $true -or $result.tailCardsRoundTripSelectedItemsContains -ne $true -or $result.tailGridRoundTripContainerRealized -ne $true -or $result.tailGridRoundTripContainerSelected -ne $true) { $structuralFailures += 'tail Grid-mode canonical/visual round trip failed' }
if ($result.changedCreatedSort -ne $true -or $result.createdGroupingActive -ne $true) { $structuralFailures += 'Created sort/date-section mode did not activate' }
if ($result.createdItemsSourceCount -ne $Count -or $result.createdUsesFullExtentVirtualization -ne $true) { $structuralFailures += "Created full-extent source/virtualization was $($result.createdItemsSourceCount)/$($result.createdUsesFullExtentVirtualization)" }
if ($result.createdRealized -gt $result.gridMaximum -or $result.createdExtentHeight -le $result.createdViewportHeight) { $structuralFailures += "Created extent/realization was $($result.createdExtentHeight)/$($result.createdViewportHeight), realized $($result.createdRealized)" }
if ($result.createdTailSelected -ne $true -or $result.createdTailCanonicalSelected -ne $true -or $result.createdTailGridWindowContains -ne $true -or $result.createdTailCardsSelectedItemsContains -ne $true -or $result.createdTailContainerRealized -ne $true -or $result.createdTailContainerSelected -ne $true) { $structuralFailures += 'Created exact tail canonical/visual selection failed' }
if ($result.createdTailLastVisible -ne ($Count - 1)) { $structuralFailures += "Created tail last visible index was $($result.createdTailLastVisible)" }
if ($result.modalTail -ne $true -or $result.finalSearchExact -ne $true) { $structuralFailures += 'tail search or modal reachability failed' }
if ($result.flatZoomRoundTrip -ne $true -or $result.createdZoomRoundTrip -ne $true) { $structuralFailures += '100k Grid zoom anchor/width round trip failed' }
if ($result.endpointSidebarRoundTrip -ne $true) { $structuralFailures += '20px/one-column/Sidebar anchor round trip failed' }
if ($result.endpointMinimumWidth -ne 20 -or $result.endpointMaximumWidth -ne 600 -or $result.endpointMaximumColumns -ne 1 -or $result.endpointMaximumForcedSingleColumn -ne $true) { $structuralFailures += "zoom endpoints/columns were $($result.endpointMinimumWidth)/$($result.endpointMaximumWidth)/$($result.endpointMaximumColumns)" }
if ($result.endpointMaximumRealized -lt 1 -or $result.endpointMaximumRealized -gt $result.gridMaximum) { $structuralFailures += "maximum-zoom Grid realization was not bounded ($($result.endpointMaximumRealized)/$($result.gridMaximum))" }
foreach ($driftName in @('endpointMinimumDrift','endpointMaximumDrift','endpointRestoreDrift','endpointSidebarCollapseDrift','endpointSidebarExpandDrift')) {
    if ($null -eq $result.$driftName -or [double]$result.$driftName -gt 8) { $structuralFailures += "$driftName was $($result.$driftName) px" }
}
if ($result.staleCancelled -ne $true -or $result.heartbeatCount -lt 4) { $structuralFailures += 'rapid-query cancellation or dispatcher heartbeat failed' }
if ($result.sourceCountAfter -ne $Count) { $structuralFailures += "source count changed to $($result.sourceCountAfter)" }
if ($result.enhancementJobsRead -ne 0 -or $result.enhancementCandidates -ne 0) { $structuralFailures += 'enhancement state was touched' }
if ($result.browserThumbnailCacheCandidateCount -ne 2 -or $result.browserThumbnailPreciseCandidateCreated -ne $true -or $result.browserThumbnailCacheHits -lt 1) { $structuralFailures += "precise/integer Browser thumbnail cache fallback or fake-cache hit failed ($($result.browserThumbnailCacheCandidateCount)/$($result.browserThumbnailCacheHits))" }
if ($null -eq $result.warm) {
    $structuralFailures += 'warm separate-MainWindow result was missing'
}
else {
    if ($result.warm.ok -ne $true) { $structuralFailures += "warm separate-MainWindow gate failed: $($result.warm.message)" }
    if ($result.warm.loadedFolderCount -ne $FolderCount) { $structuralFailures += "warm loaded folder count was $($result.warm.loadedFolderCount)" }
    if ($result.warm.catalogCount -ne $Count -or $result.warm.filteredCount -ne $Count) { $structuralFailures += "warm catalog/filtered counts were $($result.warm.catalogCount)/$($result.warm.filteredCount)" }
    if ($result.warm.sourceCountAfter -ne $Count) { $structuralFailures += "warm source count changed to $($result.warm.sourceCountAfter)" }
    if ($result.warm.metadataIndexLoadState -ne 'Loaded') { $structuralFailures += "warm metadata index load state was $($result.warm.metadataIndexLoadState)" }
    if ($result.warm.metadataCacheHits -ne $Count -or $result.warm.metadataCacheMisses -ne 0) { $structuralFailures += "warm metadata index hit/miss counts were $($result.warm.metadataCacheHits)/$($result.warm.metadataCacheMisses)" }
    if ($result.warm.metadataIndexSaveSucceeded -ne $true -or $result.warm.metadataIndexWritten -ne $false) { $structuralFailures += "warm metadata index save/written state was $($result.warm.metadataIndexSaveSucceeded)/$($result.warm.metadataIndexWritten): $($result.warm.metadataIndexSaveError)" }
    if ($result.warm.indexPathUnchanged -ne $true -or [string]::IsNullOrWhiteSpace($result.warm.coldIndexPath) -or [string]::IsNullOrWhiteSpace($result.warm.warmIndexPath)) { $structuralFailures += "warm metadata index path changed ($($result.warm.coldIndexPath) -> $($result.warm.warmIndexPath))" }
    if ($null -ne $cleanupRootFull -and -not [string]::IsNullOrWhiteSpace($result.warm.warmIndexPath)) {
        $warmIndexFull = [IO.Path]::GetFullPath([string]$result.warm.warmIndexPath)
        $cleanupRootPrefix = $cleanupRootFull.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $warmIndexFull.StartsWith($cleanupRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $structuralFailures += "warm metadata index escaped the generated TEMP fixture: $warmIndexFull"
        }
    }
    if ($result.warm.indexSha256Unchanged -ne $true -or [string]::IsNullOrWhiteSpace($result.warm.indexSha256Before) -or $result.warm.indexSha256Before -ne $result.warm.indexSha256After) { $structuralFailures += 'warm metadata index SHA-256 changed' }
    if ($result.warm.indexLastWriteUtcUnchanged -ne $true -or $result.warm.indexLastWriteUtcTicksBefore -ne $result.warm.indexLastWriteUtcTicksAfter) { $structuralFailures += "warm metadata index mtime changed ($($result.warm.indexLastWriteUtcTicksBefore) -> $($result.warm.indexLastWriteUtcTicksAfter))" }
    if ($result.warm.gridRealized -gt $result.warm.gridMaximum) { $structuralFailures += "warm grid realization exceeded bound ($($result.warm.gridRealized) > $($result.warm.gridMaximum))" }
    if ($result.warm.gridDeferred -ne ($Count - $result.warm.gridRealized)) { $structuralFailures += "warm grid deferred count was $($result.warm.gridDeferred)" }
    if ($result.warm.gridItemsSourceCount -ne $Count -or $result.warm.gridUsesFullExtentVirtualization -ne $true) { $structuralFailures += "warm grid full-extent source/virtualization was $($result.warm.gridItemsSourceCount)/$($result.warm.gridUsesFullExtentVirtualization)" }
    if ($result.warm.gridExtentHeight -le $result.warm.gridViewportHeight -or $result.warm.gridInitialFirstVisible -ne 0) { $structuralFailures += "warm grid initial extent/visible range was $($result.warm.gridExtentHeight)/$($result.warm.gridViewportHeight), first $($result.warm.gridInitialFirstVisible)" }
    if ($result.warm.listMode -ne $true -or $result.warm.listUsesRecyclingVirtualization -ne $true -or $result.warm.listBounded -ne $true -or $result.warm.listRealized -lt 1 -or $result.warm.listRealized -gt [Math]::Min($Count, 512)) { $structuralFailures += "warm List recycling/realization bound failed ($($result.warm.listRealized))" }
    if ($result.warm.gridModeAfterList -ne $true) { $structuralFailures += 'warm Grid mode was not restored after List virtualization probe' }
    if ($result.warm.selectedTail -ne $true -or $result.warm.tailCanonicalSelected -ne $true -or $result.warm.tailGridWindowContains -ne $true -or $result.warm.tailCardsSelectedItemsContains -ne $true -or $result.warm.tailGridContainerRealized -ne $true -or $result.warm.tailGridContainerSelected -ne $true -or $result.warm.tailGridLastVisible -ne ($Count - 1)) { $structuralFailures += "warm tail visual reachability failed (last visible $($result.warm.tailGridLastVisible))" }
    if ($result.warm.zoomRoundTrip -ne $true -or $result.warm.zoomInAnchorKept -ne $true -or $result.warm.zoomOutAnchorKept -ne $true -or $result.warm.zoomInDrift -gt 8 -or $result.warm.zoomOutDrift -gt 8) { $structuralFailures += "warm Grid zoom anchor round trip failed (drift $($result.warm.zoomInDrift)/$($result.warm.zoomOutDrift))" }
    if ($result.warm.heartbeatCount -lt 4) { $structuralFailures += "warm dispatcher heartbeat count was $($result.warm.heartbeatCount)" }
    if ($null -eq $result.warm.dispatcherHeartbeatMaxGapMs -or $result.warm.dispatcherHeartbeatMaxGapMs -gt $UnresponsiveStreakLimitMs) { $structuralFailures += "warm dispatcher heartbeat gap was $($result.warm.dispatcherHeartbeatMaxGapMs) ms (limit $UnresponsiveStreakLimitMs ms)" }
    if ($result.warm.enhancementJobsRead -ne 0 -or $result.warm.enhancementCandidates -ne 0) { $structuralFailures += 'warm load touched enhancement state' }
    foreach ($timingName in @('loadElapsedMs', 'loadMetricsTotalElapsedMs', 'scanElapsedMs', 'materializeElapsedMs', 'catalogReadyElapsedMs', 'metadataElapsedMs', 'metadataIndexReadMs', 'metadataIndexWriteMs')) {
        if ($null -eq $result.warm.$timingName -or [double]$result.warm.$timingName -lt 0) {
            $structuralFailures += "warm timing $timingName was missing or negative"
        }
    }
}
if ($null -eq $windowHandleSeenMs -or $probeCount -lt 1) { $structuralFailures += 'external WM_NULL probe never observed the WPF window' }
if ($longestUnresponsiveStreakMs -gt $UnresponsiveStreakLimitMs) { $structuralFailures += "external WM_NULL unresponsive streak was $longestUnresponsiveStreakMs ms (limit $UnresponsiveStreakLimitMs ms)" }
if ($null -eq $result.dispatcherHeartbeatMaxGapMs -or $result.dispatcherHeartbeatMaxGapMs -gt $UnresponsiveStreakLimitMs) { $structuralFailures += "dispatcher heartbeat gap was $($result.dispatcherHeartbeatMaxGapMs) ms (limit $UnresponsiveStreakLimitMs ms)" }

$resultJson = $result | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($outputFullPath, $resultJson, [Text.UTF8Encoding]::new($false))
$resultJson
if ($structuralFailures.Count -gt 0) {
    throw ("WPF catalog stress structural gate failed: " + ($structuralFailures -join '; '))
}
