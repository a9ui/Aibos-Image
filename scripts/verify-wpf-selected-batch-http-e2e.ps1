param(
    [Parameter(Mandatory = $true)]
    [string]$BrowserRoot,
    [string]$ExpectedBrowserCommit = "0d451cadfe47433fdb45f00bc78168965955fe2d",
    [string]$ExpectedBrowserTree = "025503c573661d8d8a474268d24cb2596a453c33",
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-selected-batch-http-e2e.json"),
    [ValidateRange(60, 300)]
    [int]$OverallTimeoutSeconds = 180,
    [switch]$SkipBuild
)

# Local cross-repository release gate. Aibos GitHub Actions intentionally stay
# WPF-only; this verifier builds the exact Browser archive before an accepted
# Aibos commit is pushed and records both commit/tree identities in its receipt.
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$browserRootFull = [IO.Path]::GetFullPath($BrowserRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-selected-batch-http-' + [guid]::NewGuid().ToString('N'))))
$fixtureRoot = Join-Path $runRoot 'sources'
$storeRoot = Join-Path $runRoot 'stores'
$enhanceRoot = Join-Path $storeRoot 'enhance'
$derivedRoot = Join-Path $runRoot 'derived'
$browserArchivePath = Join-Path $runRoot 'browser-source.zip'
$browserBuildRoot = Join-Path $runRoot 'browser-source'
$serverStdout = Join-Path $runRoot 'browser.stdout.log'
$serverStderr = Join-Path $runRoot 'browser.stderr.log'
$wpfResultPath = Join-Path $runRoot 'wpf-result.json'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$server = $null
$summary = $null
$failure = $null
$serverStopped = $false
$residueRemoved = $false

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "E2E root must stay under TEMP."
}
if (-not (Test-Path -LiteralPath $browserRootFull -PathType Container)) {
    throw "Browser root was not found: $browserRootFull"
}

try {
    $browserCommit = (& git -C $browserRootFull rev-parse HEAD).Trim()
    $browserTree = (& git -C $browserRootFull rev-parse 'HEAD^{tree}').Trim()
    $browserStatus = @(& git -C $browserRootFull status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect the Browser worktree." }
    if ($browserCommit -ne $ExpectedBrowserCommit -or $browserTree -ne $ExpectedBrowserTree) {
        throw "Browser exact candidate mismatch: $browserCommit / $browserTree"
    }
    if ($browserStatus.Count -gt 0) {
        throw "Browser exact worktree must be clean for the cross-repository gate."
    }
    if (-not $SkipBuild) {
        & dotnet build $project -c $Configuration --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }
    }
    $wpfExe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
    if (-not (Test-Path -LiteralPath $wpfExe -PathType Leaf)) {
        throw "WPF executable was not found: $wpfExe"
    }

    New-Item -ItemType Directory -Path $fixtureRoot,$enhanceRoot,$derivedRoot -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $colors = @(
        [Drawing.Color]::FromArgb(53, 116, 189),
        [Drawing.Color]::FromArgb(79, 148, 103),
        [Drawing.Color]::FromArgb(188, 113, 69),
        [Drawing.Color]::FromArgb(135, 91, 180)
    )
    for ($index = 0; $index -lt 4; $index++) {
        $bitmap = New-Object Drawing.Bitmap 64,48
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear($colors[$index])
        $sourcePath = Join-Path $fixtureRoot ("source-{0:D2}.png" -f $index)
        $bitmap.Save($sourcePath, [Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $statePath = Join-Path $storeRoot 'state.json'
    $favoritesPath = Join-Path $storeRoot 'favorites.json'
    $seenPath = Join-Path $storeRoot 'seen.json'
    $recentPath = Join-Path $storeRoot 'recent-folders.json'
    $settingsPath = Join-Path $storeRoot 'settings.json'
    $albumsPath = Join-Path $storeRoot 'albums.json'
    $searchHistoryPath = Join-Path $storeRoot 'search-history.json'
    $jobsPath = Join-Path $enhanceRoot 'jobs.json'
    [IO.File]::WriteAllText($statePath, '{"version":2,"smokeMarker":"keep-state"}', $utf8NoBom)
    [IO.File]::WriteAllText($favoritesPath, '{}', $utf8NoBom)
    [IO.File]::WriteAllText($seenPath, '{}', $utf8NoBom)
    [IO.File]::WriteAllText($recentPath, '{"version":1,"lastFolderSet":[],"recentFolderSets":[],"updatedAtUtc":""}', $utf8NoBom)
    [IO.File]::WriteAllText($settingsPath, '{"version":1,"smokeMarker":"keep-settings"}', $utf8NoBom)
    [IO.File]::WriteAllText($albumsPath, '{"version":1,"revision":0,"albums":[],"recentAlbumIds":[]}', $utf8NoBom)
    [IO.File]::WriteAllText($searchHistoryPath, '{"version":1,"entries":[]}', $utf8NoBom)
    [IO.File]::WriteAllText($jobsPath, '{"version":1,"jobs":[]}', $utf8NoBom)

    $port = Get-Random -Minimum 32100 -Maximum 38900
    $baseUrl = "http://127.0.0.1:$port"
    $node = (Get-Command node -ErrorAction Stop).Source
    $previousEnvironment = @{
        PVU_ENHANCE_ROOT = $env:PVU_ENHANCE_ROOT
        PVU_DERIVED_CACHE_ROOT = $env:PVU_DERIVED_CACHE_ROOT
        PVU_FAVORITES_PATH = $env:PVU_FAVORITES_PATH
        PVU_SEEN_PATH = $env:PVU_SEEN_PATH
        PVU_RECENT_FOLDERS_PATH = $env:PVU_RECENT_FOLDERS_PATH
        PVU_SETTINGS_PATH = $env:PVU_SETTINGS_PATH
        PVU_ALBUMS_PATH = $env:PVU_ALBUMS_PATH
        PVU_SEARCH_HISTORY_PATH = $env:PVU_SEARCH_HISTORY_PATH
        PHOTOVIEWER_BROWSER_BASE_URL = $env:PHOTOVIEWER_BROWSER_BASE_URL
    }
    $env:PVU_ENHANCE_ROOT = $enhanceRoot
    $env:PVU_DERIVED_CACHE_ROOT = $derivedRoot
    $env:PVU_FAVORITES_PATH = $favoritesPath
    $env:PVU_SEEN_PATH = $seenPath
    $env:PVU_RECENT_FOLDERS_PATH = $recentPath
    $env:PVU_SETTINGS_PATH = $settingsPath
    $env:PVU_ALBUMS_PATH = $albumsPath
    $env:PVU_SEARCH_HISTORY_PATH = $searchHistoryPath
    $env:PHOTOVIEWER_BROWSER_BASE_URL = $baseUrl

    New-Item -ItemType Directory -Path $browserBuildRoot -Force | Out-Null
    & git -C $browserRootFull archive --format=zip "--output=$browserArchivePath" $browserCommit
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $browserArchivePath -PathType Leaf)) {
        throw "Unable to archive the exact Browser source."
    }
    Expand-Archive -LiteralPath $browserArchivePath -DestinationPath $browserBuildRoot -Force
    $pnpmCommand = (Get-Command pnpm -ErrorAction Stop).Source
    Push-Location $browserBuildRoot
    try {
        & $pnpmCommand install --frozen-lockfile --package-import-method=copy
        if ($LASTEXITCODE -ne 0) { throw "Exact Browser frozen install failed with exit code $LASTEXITCODE." }
        & $pnpmCommand build
        if ($LASTEXITCODE -ne 0) { throw "Exact Browser production build failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
    $nextEntry = Join-Path $browserBuildRoot 'node_modules\next\dist\bin\next'
    $nextEntryExists = Test-Path -LiteralPath $nextEntry -PathType Leaf
    $buildIdExists = Test-Path -LiteralPath (Join-Path $browserBuildRoot '.next\BUILD_ID') -PathType Leaf
    if (-not $nextEntryExists -or -not $buildIdExists) {
        throw "Exact Browser production bundle was not produced from the archived source."
    }

    $server = Start-Process -FilePath $node `
        -ArgumentList @($nextEntry, 'start', '-H', '127.0.0.1', '-p', $port) `
        -WorkingDirectory $browserBuildRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $serverStdout `
        -RedirectStandardError $serverStderr `
        -PassThru

    $serverReady = $false
    for ($attempt = 0; $attempt -lt 120 -and -not $serverReady; $attempt++) {
        if ($server.HasExited) { break }
        try {
            $health = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/api/enhance/jobs" -TimeoutSec 2
            $serverReady = $health.StatusCode -eq 200
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $serverReady) {
        throw "Exact Browser server did not become ready."
    }

    $escapedFolder = [Uri]::EscapeDataString($fixtureRoot)
    $scan = Invoke-WebRequest -UseBasicParsing `
        -Uri "$baseUrl/api/scan?dir=$escapedFolder&full=1" `
        -Headers @{ Accept = 'text/event-stream' } `
        -TimeoutSec 60
    if ($scan.StatusCode -ne 200 -or $scan.Content -notmatch '"type":"complete"') {
        throw "Exact Browser server did not index the isolated fixture."
    }

    $wpf = Start-Process -FilePath $wpfExe `
        -ArgumentList @(
            '--selected-batch-enhancement-http-smoke',
            ('"{0}"' -f $wpfResultPath),
            '--folder',
            ('"{0}"' -f $fixtureRoot),
            '--store-root',
            ('"{0}"' -f $storeRoot)
        ) `
        -WindowStyle Hidden `
        -PassThru
    if (-not $wpf.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $wpf.Id -Force -ErrorAction SilentlyContinue
        throw "WPF real HTTP batch smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $wpfResultPath -PathType Leaf)) {
        throw "WPF real HTTP batch smoke produced no result (exit $($wpf.ExitCode))."
    }

    $wpfExitCode = $wpf.ExitCode
    $wpf.WaitForExit()
    $wpf.Dispose()
    $wpfResult = Get-Content -Raw -LiteralPath $wpfResultPath | ConvertFrom-Json
    $aibosCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $temporaryIndex = Join-Path $runRoot 'aibos-candidate.index'
    $previousGitIndexFile = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $temporaryIndex
        & git -C $repoRoot read-tree HEAD
        if ($LASTEXITCODE -ne 0) { throw "Unable to initialize the candidate tree index." }
        & git -C $repoRoot add -A
        if ($LASTEXITCODE -ne 0) { throw "Unable to populate the candidate tree index." }
        $aibosTree = (& git -C $repoRoot write-tree).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to calculate the candidate tree." }
    }
    finally {
        if ($null -eq $previousGitIndexFile) {
            Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        }
        else {
            $env:GIT_INDEX_FILE = $previousGitIndexFile
        }
    }
    $summary = [ordered]@{
        ok = $wpfExitCode -eq 0 -and $wpfResult.ok -eq $true
        aibosCommit = $aibosCommit
        aibosTree = $aibosTree
        browserCommit = $browserCommit
        browserTree = $browserTree
        wpf = $wpfResult
        browserSourceClean = $browserStatus.Count -eq 0
        browserBundleBuiltFromArchive = $true
        isolatedRootCreated = $true
    }
    if (-not $summary.ok) {
        throw "WPF real HTTP batch behavior failed (exit $wpfExitCode)."
    }
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        try { $server.WaitForExit(10000) | Out-Null } catch { }
    }
    $serverStopped = $null -eq $server -or $server.HasExited
    if (Get-Variable previousEnvironment -ErrorAction SilentlyContinue) {
        foreach ($key in $previousEnvironment.Keys) {
            if ($null -eq $previousEnvironment[$key]) {
                Remove-Item -Path "Env:$key" -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path "Env:$key" -Value $previousEnvironment[$key]
            }
        }
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an E2E path outside TEMP: $resolvedRunRoot"
        }
        $extendedRunRoot = '\\?\' + $resolvedRunRoot
        try {
            $extendedFiles = @([IO.Directory]::EnumerateFiles(
                $extendedRunRoot,
                '*',
                [IO.SearchOption]::AllDirectories))
        }
        catch {
            $extendedFiles = @()
        }
        foreach ($extendedFile in $extendedFiles) {
            try {
                [IO.File]::SetAttributes($extendedFile, [IO.FileAttributes]::Normal)
                [IO.File]::Delete($extendedFile)
            }
            catch {
            }
        }
        try {
            $extendedDirectories = @([IO.Directory]::EnumerateDirectories(
                $extendedRunRoot,
                '*',
                [IO.SearchOption]::AllDirectories)) | Sort-Object Length -Descending
        }
        catch {
            $extendedDirectories = @()
        }
        foreach ($extendedDirectory in $extendedDirectories) {
            try {
                [IO.Directory]::Delete($extendedDirectory, $false)
            }
            catch {
            }
        }
        try {
            [IO.Directory]::Delete($extendedRunRoot, $false)
        }
        catch {
        }
        for ($cleanupAttempt = 1; $cleanupAttempt -le 20; $cleanupAttempt++) {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $resolvedRunRoot)) { break }
            if ($cleanupAttempt -eq 20) {
                throw "Unable to remove the isolated E2E root after bounded retries."
            }
            Start-Sleep -Milliseconds 150
        }
    }
    $residueRemoved = -not (Test-Path -LiteralPath $runRoot)
}

if ($null -eq $summary) {
    $summary = [ordered]@{ ok = $false }
}
$summary.serverStopped = $serverStopped
$summary.isolatedSourceStateOutputResidueRemoved = $residueRemoved
if ($null -ne $failure) {
    $redactedError = $failure.Exception.Message
    foreach ($privateRoot in @($runRoot, $browserRootFull, $repoRoot)) {
        if (-not [string]::IsNullOrWhiteSpace($privateRoot)) {
            $redactedError = $redactedError.Replace($privateRoot, '<private-root>')
        }
    }
    $summary.error = $redactedError
}
$summary.ok = $summary.ok -and $serverStopped -and $residueRemoved
$outputDirectory = [IO.Path]::GetDirectoryName($fullOutputPath)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$outputJson = $summary | ConvertTo-Json -Depth 16
[IO.File]::WriteAllText($fullOutputPath, $outputJson, (New-Object System.Text.UTF8Encoding($false)))
$summary | ConvertTo-Json -Depth 16
if (-not $summary.ok) {
    if ($null -ne $failure) { throw $summary.error }
    throw "Selected batch HTTP E2E failed."
}
