param(
    [Parameter(Mandatory = $true)]
    [string]$H25Repository,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$H25Commit,

    [string]$Configuration = 'Release',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-CompanionReady {
    param([int]$Port, [Diagnostics.Process]$Process)
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "H25 companion exited before readiness with code $($Process.ExitCode)."
        }
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/api/runtime" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'H25 companion did not become ready within 45 seconds.'
}

function Stop-OwnedProcess {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try { $Process.Refresh() } catch { return }
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit(10000) | Out-Null
    }
}

function Read-SmokeResult {
    param([string]$Path, [int]$ExpectedExitCode)
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Smoke result is missing: $Path"
    $result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-True ($ExpectedExitCode -eq 0) "WPF smoke exited with $ExpectedExitCode."
    Assert-True ($result.ok -eq $true) "WPF smoke failed: $($result.message)"
    return $result
}

function Invoke-WpfPhase {
    param(
        [string]$WpfDll,
        [string]$ResultPath,
        [string]$FixtureRoot,
        [string]$Phase,
        [string]$SourceName,
        [string]$WorkingDirectory,
        [string]$LogRoot,
        [int]$TimeoutMilliseconds = 60000
    )

    $stdout = Join-Path $LogRoot ("wpf-$Phase.stdout.log")
    $stderr = Join-Path $LogRoot ("wpf-$Phase.stderr.log")
    $process = Start-Process -FilePath 'dotnet.exe' `
        -ArgumentList @($WpfDll, '--h25-enhancement-companion-smoke', $ResultPath, '--fixture-root', $FixtureRoot, '--phase', $Phase, '--source-name', $SourceName) `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        Stop-OwnedProcess -Process $process
        throw "WPF companion phase '$Phase' exceeded $TimeoutMilliseconds ms."
    }
    return $process.ExitCode
}

function Remove-DirectoryTreeWithoutFollowingLinks {
    param([string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    $extended = if ($resolved.StartsWith('\\', [StringComparison]::Ordinal)) {
        '\\?\UNC\' + $resolved.Substring(2)
    }
    else {
        '\\?\' + $resolved
    }

    function Remove-Entry {
        param([IO.FileSystemInfo]$Entry)

        $attributes = $Entry.Attributes
        $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if ($isDirectory) { [IO.Directory]::Delete($Entry.FullName, $false) }
            else { [IO.File]::Delete($Entry.FullName) }
            return
        }

        if ($isDirectory) {
            foreach ($child in ([IO.DirectoryInfo]$Entry).EnumerateFileSystemInfos()) {
                Remove-Entry -Entry $child
            }
            [IO.Directory]::Delete($Entry.FullName, $false)
            return
        }

        if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            [IO.File]::SetAttributes($Entry.FullName, $attributes -band (-bnot [IO.FileAttributes]::ReadOnly))
        }
        [IO.File]::Delete($Entry.FullName)
    }

    Remove-Entry -Entry ([IO.DirectoryInfo]::new($extended))
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$h25Root = [IO.Path]::GetFullPath($H25Repository)
Assert-True (Test-Path -LiteralPath (Join-Path $h25Root '.git')) 'H25Repository must be a Git worktree root.'

$resolvedH25Commit = (& git -C $h25Root rev-parse "$H25Commit^{commit}").Trim()
Assert-True ($LASTEXITCODE -eq 0 -and $resolvedH25Commit -eq $H25Commit.ToLowerInvariant()) 'H25Commit could not be resolved exactly.'
$h25Tree = (& git -C $h25Root rev-parse "$H25Commit^{tree}").Trim()
Assert-True ($LASTEXITCODE -eq 0 -and $h25Tree -match '^[0-9a-f]{40}$') 'H25 tree could not be resolved.'

$aibosCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$aibosTree = (& git -C $repoRoot rev-parse 'HEAD^{tree}').Trim()
$aibosStatus = @(& git -C $repoRoot status --porcelain=v1)
Assert-True ($aibosStatus.Count -eq 0) 'The exact live E2E requires a clean committed Aibos candidate.'

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('ahe-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'E2E root must stay under TEMP.'

$h25Source = Join-Path $runRoot 'h'
$fixtureRoot = Join-Path $runRoot 'f'
$sharedRoot = Join-Path $fixtureRoot 's'
$wpfLocalRoot = Join-Path $fixtureRoot 'w'
$enhanceRoot = Join-Path $sharedRoot 'enhance'
$jobsPath = Join-Path $enhanceRoot 'jobs.json'
$outputsRoot = Join-Path $enhanceRoot 'outputs'
$buildRoot = Join-Path $runRoot 'b'
$archivePath = Join-Path $runRoot 'h.tar'
$serverOut = Join-Path $runRoot 'h25.stdout.log'
$serverErr = Join-Path $runRoot 'h25.stderr.log'
$fullResultPath = Join-Path $fixtureRoot 'full-result.json'
$interruptedResultPath = Join-Path $fixtureRoot 'interrupted-result.json'
$recoveryResultPath = Join-Path $fixtureRoot 'recovery-result.json'
$fullSourcePath = Join-Path $fixtureRoot 'images\full-source.png'
$interruptedSourcePath = Join-Path $fixtureRoot 'images\interrupted-source.png'
$metadataDirectory = Join-Path $wpfLocalRoot 'metadata-index'

$sharedFiles = [ordered]@{
    favorites = Join-Path $sharedRoot 'favorites.json'
    seen = Join-Path $sharedRoot 'seen.json'
    recent = Join-Path $sharedRoot 'recent-folders.json'
    settings = Join-Path $sharedRoot 'settings.json'
    albums = Join-Path $sharedRoot 'albums.json'
    searchHistory = Join-Path $sharedRoot 'search-history.json'
}
$environment = [ordered]@{
    PVU_ENHANCE_ROOT = $enhanceRoot
    PVU_FAVORITES_PATH = $sharedFiles.favorites
    PVU_SEEN_PATH = $sharedFiles.seen
    PVU_RECENT_FOLDERS_PATH = $sharedFiles.recent
    PVU_SETTINGS_PATH = $sharedFiles.settings
    PVU_ALBUMS_PATH = $sharedFiles.albums
    PVU_SEARCH_HISTORY_PATH = $sharedFiles.searchHistory
    PV_LEGACY_PHOTOVIEWER_DIR = (Join-Path $fixtureRoot 'empty-legacy-root')
    PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $wpfLocalRoot 'state.json')
    PHOTOVIEWER_WPF_FAVORITES_PATH = $sharedFiles.favorites
    PHOTOVIEWER_WPF_SEEN_PATH = $sharedFiles.seen
    PHOTOVIEWER_WPF_RECENT_PATH = $sharedFiles.recent
    PHOTOVIEWER_WPF_SETTINGS_PATH = $sharedFiles.settings
    PHOTOVIEWER_WPF_ALBUMS_PATH = $sharedFiles.albums
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = $sharedFiles.searchHistory
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = $jobsPath
    PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY = $metadataDirectory
}
$previousEnvironment = @{}
$server = $null
$wpfInterrupted = $null
$failureInFlight = $false
$port = Get-FreeLoopbackPort
$environment.PHOTOVIEWER_BROWSER_BASE_URL = "http://127.0.0.1:$port/"
$environment.PVU_SERVER_HOST = '127.0.0.1'
$environment.PVU_SERVER_PORT = [string]$port
$environment.PVU_SOURCE_REVISION = $resolvedH25Commit
$environment.PVU_SOURCE_DIRTY = 'false'

try {
    New-Item -ItemType Directory -Path $h25Source, $sharedRoot, $enhanceRoot, $outputsRoot, $wpfLocalRoot, $metadataDirectory, $environment.PV_LEGACY_PHOTOVIEWER_DIR -Force | Out-Null
    foreach ($name in $environment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }

    [IO.File]::WriteAllText($sharedFiles.favorites, '{}', [Text.UTF8Encoding]::new($false))
    $seenSeed = [ordered]@{ $fullSourcePath = $true; $interruptedSourcePath = $true } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($sharedFiles.seen, $seenSeed, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($sharedFiles.recent, '{"version":1,"lastFolderSet":[],"recentFolderSets":[],"updatedAtUtc":"2026-07-23T00:00:00.000Z","e2eMarker":"preserve"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($sharedFiles.settings, '{"version":1,"e2eMarker":"preserve"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($sharedFiles.albums, '{"version":1,"revision":0,"updatedAtUtc":"2026-07-23T00:00:00.000Z","albums":[],"recentAlbumIds":[],"e2eMarker":"preserve"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($sharedFiles.searchHistory, '{"version":1,"entries":[],"updatedAtUtc":"2026-07-23T00:00:00.000Z","e2eMarker":"preserve"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($jobsPath, '{"version":1,"jobs":[]}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($environment.PHOTOVIEWER_WPF_STATE_PATH, '{"version":2}', [Text.UTF8Encoding]::new($false))

    $immutableHashesBefore = @{}
    foreach ($entry in $sharedFiles.GetEnumerator()) {
        $immutableHashesBefore[$entry.Key] = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
    }

    & git -C $h25Root archive --format=tar --output=$archivePath $resolvedH25Commit
    Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $archivePath -PathType Leaf)) 'Could not export the exact H25 candidate.'
    & tar -xf $archivePath -C $h25Source
    Assert-True ($LASTEXITCODE -eq 0) 'Could not extract the exact H25 candidate.'
    Push-Location $h25Source
    try {
        & pnpm install --offline --frozen-lockfile --virtual-store-dir .p
        Assert-True ($LASTEXITCODE -eq 0) 'Exact H25 dependency materialization failed.'
        $nextCli = Join-Path $h25Source 'node_modules\next\dist\bin\next'
        & node $nextCli build
        Assert-True ($LASTEXITCODE -eq 0) 'Exact H25 production build failed.'
    }
    finally {
        Pop-Location
    }

    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    Assert-True ($LASTEXITCODE -eq 0) 'Exact Aibos WPF build failed.'
    $wpfDll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $wpfDll -PathType Leaf) 'Aibos WPF DLL is missing.'

    function Start-H25Companion {
        if (Test-Path -LiteralPath $serverOut) { Remove-Item -LiteralPath $serverOut -Force }
        if (Test-Path -LiteralPath $serverErr) { Remove-Item -LiteralPath $serverErr -Force }
        $nextCli = Join-Path $h25Source 'node_modules\next\dist\bin\next'
        $process = Start-Process -FilePath 'node.exe' `
            -ArgumentList @($nextCli, 'start', '--hostname', '127.0.0.1', '--port', [string]$port) `
            -WorkingDirectory $h25Source `
            -RedirectStandardOutput $serverOut `
            -RedirectStandardError $serverErr `
            -WindowStyle Hidden `
            -PassThru
        Wait-CompanionReady -Port $port -Process $process
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop)
        Assert-True ($listeners.Count -ge 1) 'No H25 listener was found.'
        Assert-True (@($listeners | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -eq 0) 'H25 opened a non-127.0.0.1 listener.'
        return $process
    }

    $server = Start-H25Companion
    $fullExit = Invoke-WpfPhase -WpfDll $wpfDll -ResultPath $fullResultPath -FixtureRoot $fixtureRoot -Phase 'full' -SourceName 'full-source.png' -WorkingDirectory $repoRoot -LogRoot $runRoot
    $full = Read-SmokeResult -Path $fullResultPath -ExpectedExitCode $fullExit
    Assert-True ($full.passiveRefresh -eq $true -and $full.loopbackOnly -eq $true -and $full.sourceUnchanged -eq $true) 'Full phase lost passive/loopback/source safety.'

    $wpfInterruptedOut = Join-Path $runRoot 'wpf-interrupted.stdout.log'
    $wpfInterruptedErr = Join-Path $runRoot 'wpf-interrupted.stderr.log'
    $wpfInterrupted = Start-Process -FilePath 'dotnet.exe' `
        -ArgumentList @($wpfDll, '--h25-enhancement-companion-smoke', $interruptedResultPath, '--fixture-root', $fixtureRoot, '--phase', 'start-interrupted', '--source-name', 'interrupted-source.png') `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $wpfInterruptedOut `
        -RedirectStandardError $wpfInterruptedErr `
        -WindowStyle Hidden `
        -PassThru
    $interruptDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $interrupted = $null
    while ([DateTime]::UtcNow -lt $interruptDeadline) {
        if ($wpfInterrupted.HasExited) { break }
        if (Test-Path -LiteralPath $interruptedResultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $interruptedResultPath -Raw | ConvertFrom-Json
                if ($candidate.ok -eq $true -and $candidate.status -in @('queued', 'running')) {
                    $interrupted = $candidate
                    break
                }
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 10
    }
    Assert-True ($null -ne $interrupted) 'WPF did not expose an interruptible real H25 job.'
    Assert-True ($interrupted.ok -eq $true -and $interrupted.status -in @('queued', 'running')) 'Interrupted phase did not reach an active real job.'
    Stop-OwnedProcess -Process $server
    $server = $null
    $wpfInterrupted.WaitForExit(15000) | Out-Null
    Assert-True $wpfInterrupted.HasExited 'Interrupted WPF smoke did not exit after the companion stopped.'
    $wpfInterrupted = $null

    $jobsAfterInterruption = Get-Content -LiteralPath $jobsPath -Raw | ConvertFrom-Json
    $staleJob = @($jobsAfterInterruption.jobs | Where-Object { $_.id -eq $interrupted.jobId })
    Assert-True ($staleJob.Count -eq 1 -and $staleJob[0].status -eq 'running') 'The verifier did not preserve one stale running job across the companion stop.'

    $server = Start-H25Companion
    $recoveryExit = Invoke-WpfPhase -WpfDll $wpfDll -ResultPath $recoveryResultPath -FixtureRoot $fixtureRoot -Phase 'recover' -SourceName 'interrupted-source.png' -WorkingDirectory $repoRoot -LogRoot $runRoot
    $recovery = Read-SmokeResult -Path $recoveryResultPath -ExpectedExitCode $recoveryExit
    Assert-True ($recovery.restartJobObserved -eq $true -and $recovery.canceled -eq $true -and $recovery.retried -eq $true) 'WPF did not recover the stale companion job through explicit Cancel and Retry.'

    Stop-OwnedProcess -Process $server
    $server = $null

    $immutableStoresUnchanged = $true
    foreach ($entry in $sharedFiles.GetEnumerator()) {
        $after = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        if ($after -ne $immutableHashesBefore[$entry.Key]) { $immutableStoresUnchanged = $false }
    }
    Assert-True $immutableStoresUnchanged 'A non-Enhancement shared store changed during the exact companion E2E.'
    Assert-True ((Get-FileHash -LiteralPath $fullSourcePath -Algorithm SHA256).Hash -eq $full.sourceSha256Before) 'Full-phase source bytes changed.'
    Assert-True ((Get-FileHash -LiteralPath $interruptedSourcePath -Algorithm SHA256).Hash -eq $recovery.sourceSha256Before) 'Restart-phase source bytes changed.'
    $managedOutputFiles = @(Get-ChildItem -LiteralPath $outputsRoot -File -Recurse -ErrorAction SilentlyContinue)
    Assert-True ($managedOutputFiles.Count -eq 0) 'Managed outputs remain after explicit WPF output deletion.'

    [pscustomobject]@{
        allPassed = $true
        message = 'Exact Aibos WPF to H25 production HTTP Enhancement companion E2E passed.'
        aibosCommit = $aibosCommit
        aibosTree = $aibosTree
        h25Commit = $resolvedH25Commit
        h25Tree = $h25Tree
        loopbackAddress = '127.0.0.1'
        unindexedCreate = [bool]$full.started
        cancel = [bool]$full.canceled
        retry = [bool]$full.retried
        successOutputAccepted = [bool]$full.outputAccepted
        companionRestartRecovery = [bool]$recovery.restartJobObserved -and [bool]$recovery.canceled -and [bool]$recovery.retried
        outputDelete = [bool]$full.deletedOutput -and [bool]$recovery.deletedOutput
        sourceSha256Unchanged = [bool]$full.sourceUnchanged -and [bool]$recovery.sourceUnchanged
        nonEnhancementStoresUnchanged = $immutableStoresUnchanged
        managedOutputsEmpty = $managedOutputFiles.Count -eq 0
    } | ConvertTo-Json -Depth 5
}
catch {
    $failureInFlight = $true
    throw
}
finally {
    Stop-OwnedProcess -Process $server
    Stop-OwnedProcess -Process $wpfInterrupted
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    try {
        if (-not $KeepArtifacts -and -not $failureInFlight -and (Test-Path -LiteralPath $runRoot)) {
            $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
            if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean an E2E path outside TEMP: $resolvedRunRoot"
            }
            Remove-DirectoryTreeWithoutFollowingLinks -Path $resolvedRunRoot
        }
    }
    catch {
        if (-not $failureInFlight) { throw }
        Write-Warning "Primary E2E failure preserved; TEMP cleanup also failed: $($_.Exception.Message)"
    }
}
