param(
    [ValidateRange(1, 10)]
    [int]$Iterations = 3,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ("photoviewer-wpf-crash-lock-" + [guid]::NewGuid().ToString('N'))
$fullRoot = [IO.Path]::GetFullPath($root)
if (-not $fullRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullRoot"
}

$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$pnpm = (Get-Command pnpm.cmd -ErrorAction Stop).Source
$failures = [Collections.Generic.List[string]]::new()

function Start-Wpf([string[]]$Arguments) {
    Start-Process -FilePath $exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
}

function Wait-Wpf([Diagnostics.Process]$Process, [int]$TimeoutMilliseconds = 60000) {
    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        try { $Process.Kill($true) } catch { }
        throw "WPF worker $($Process.Id) timed out."
    }
    $Process.Refresh()
    return $Process.ExitCode
}

function Wait-Ready([Diagnostics.Process]$Process, [string]$ReadyPath, [int]$TimeoutMilliseconds = 5000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if (Test-Path -LiteralPath $ReadyPath) { return }
        if ($Process.HasExited) { throw "WPF worker $($Process.Id) exited before publishing ready evidence." }
        Start-Sleep -Milliseconds 10
    }
    throw "WPF worker $($Process.Id) did not publish ready evidence."
}

function Invoke-Recovery([string]$Kind, [string]$Target, [string]$Key, [string]$Result) {
    Remove-Item -LiteralPath $Result -Force -ErrorAction SilentlyContinue
    $process = Start-Wpf @(
        '--persistence-recovery-smoke', $Result,
        '--target-path', $Target,
        '--kind', $Kind,
        '--key', $Key
    )
    return Wait-Wpf $process
}

function Invoke-PnpmVitest([string]$TestPath) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell converts redirected native stderr into ErrorRecord
        # objects. Keep those as verifier evidence and judge the worker by its
        # real process exit code instead of aborting before $LASTEXITCODE can be
        # inspected.
        $ErrorActionPreference = 'Continue'
        $output = & $pnpm exec vitest run $TestPath --reporter=dot 2>&1
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($output | ForEach-Object { $_.ToString() })
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Fingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'missing' }
    return "{0}:{1}" -f (Get-Item -LiteralPath $Path).Length, (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Metadata-Fingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'missing' }
    $item = Get-Item -LiteralPath $Path
    return "{0}:{1}" -f $item.Length, $item.LastWriteTimeUtc.Ticks
}

function Assert-NoResidue([string]$Directory, [string]$Target) {
    $fileName = [IO.Path]::GetFileName($Target)
    $prefix = [IO.Path]::GetFileNameWithoutExtension($Target) + '-'
    $residue = @(Get-ChildItem -LiteralPath $Directory -File -Force | Where-Object {
        $_.Name -eq ($fileName + '.lock') -or
        ($_.Name.EndsWith('.tmp', [StringComparison]::OrdinalIgnoreCase) -and
            ($_.Name.StartsWith(".$fileName.", [StringComparison]::OrdinalIgnoreCase) -or
             $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)))
    })
    if ($residue.Count -ne 0) {
        $failures.Add("persistence residue for ${fileName}: $($residue.FullName -join ', ')")
    }
}

function Write-Seed([string]$Kind, [string]$Target) {
    switch ($Kind) {
        'favorites' { @{ seedFavorite = 5 } | ConvertTo-Json | Set-Content -LiteralPath $Target -Encoding utf8 }
        'seen' { @{ seedSeen = $true } | ConvertTo-Json | Set-Content -LiteralPath $Target -Encoding utf8 }
        'recent' {
            @{ version = 1; lastFolderSet = @(); recentFolderSets = @(); updatedAtUtc = '2026-07-18T00:00:00.000Z'; futureRecent = @{ keep = $true } } |
                ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Target -Encoding utf8
        }
        'state' {
            @{ Version = 2; SearchQuery = 'seed'; futureState = @{ keep = $true } } |
                ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Target -Encoding utf8
        }
    }
}

function Verify-RecoveredDocument([string]$Kind, [string]$Target, [string]$Key) {
    $stored = Get-Content -Raw -LiteralPath $Target | ConvertFrom-Json
    switch ($Kind) {
        'favorites' {
            if ($stored.seedFavorite -ne 5 -or $stored.PSObject.Properties[$Key].Value -ne 3) {
                $failures.Add("favorites merge lost the seed or recovered key: $Key")
            }
        }
        'seen' {
            if ($stored.seedSeen -ne $true -or $stored.PSObject.Properties[$Key].Value -ne $true) {
                $failures.Add("seen merge lost the seed or recovered key: $Key")
            }
        }
        'recent' {
            $sets = @($stored.recentFolderSets | ForEach-Object { @($_) -join "`n" })
            if ($stored.futureRecent.keep -ne $true -or $Key -notin $sets -or @($stored.lastFolderSet)[0] -ne $Key) {
                $failures.Add("recent recovery lost unknown fields or recovered marker: $Key")
            }
        }
        'state' {
            if ($stored.futureState.keep -ne $true -or $stored.SearchQuery -ne $Key -or $stored.Version -ne 2) {
                $failures.Add("state recovery lost unknown fields or recovered value: $Key")
            }
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $fullRoot | Out-Null
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing WPF executable: $exe" }

    $staleCrashLocksProtected = 0
    $explicitCrashRecoveries = 0
    foreach ($kind in @('favorites', 'seen', 'recent', 'state')) {
        $storeRoot = Join-Path $fullRoot $kind
        New-Item -ItemType Directory -Force -Path $storeRoot | Out-Null
        $target = Join-Path $storeRoot "$kind.json"
        Write-Seed $kind $target

        $holdReady = Join-Path $storeRoot 'live-owner-ready.json'
        $holdResult = Join-Path $storeRoot 'live-owner-recovery.json'
        $holder = Start-Wpf @('--persistence-hold-smoke', $holdReady, '--target-path', $target, '--hold-ms', 1500)
        Wait-Ready $holder $holdReady
        $holdEvidence = Get-Content -Raw -LiteralPath $holdReady | ConvertFrom-Json
        $targetBeforeLiveContention = Fingerprint $target
        $lockBeforeLiveContention = Metadata-Fingerprint $holdEvidence.lockPath
        $liveExit = Invoke-Recovery $kind $target 'live-owner-must-not-write' $holdResult
        if ($liveExit -eq 0 -or (Fingerprint $target) -ne $targetBeforeLiveContention -or
            (Metadata-Fingerprint $holdEvidence.lockPath) -ne $lockBeforeLiveContention -or $holder.HasExited) {
            $failures.Add("$kind live owner lock was stolen or mutated")
        }
        if ((Wait-Wpf $holder) -ne 0) { $failures.Add("$kind live lock owner failed to release normally") }
        Assert-NoResidue $storeRoot $target

        for ($index = 0; $index -lt $Iterations; $index++) {
            $key = if ($kind -eq 'state') {
                "state-recovered-$index"
            } else {
                [IO.Path]::GetFullPath((Join-Path $storeRoot "$kind-recovered-$index"))
            }
            $ready = Join-Path $storeRoot "crash-$index-ready.json"
            $result = Join-Path $storeRoot "recovery-$index.json"
            $crash = Start-Wpf @('--persistence-crash-smoke', $ready, '--target-path', $target)
            $crashExit = Wait-Wpf $crash
            if ($crashExit -ne 71 -or -not (Test-Path -LiteralPath $ready)) {
                throw "$kind crash worker did not leave its ready evidence (exit $crashExit)."
            }
            $crashEvidence = Get-Content -Raw -LiteralPath $ready | ConvertFrom-Json
            if ($crashEvidence.ok -ne $true -or -not (Test-Path -LiteralPath $crashEvidence.lockPath) -or -not (Test-Path -LiteralPath $crashEvidence.tempPath)) {
                throw "$kind crash worker did not leave a real lock and atomic temp."
            }
            $reportedLockPath = [IO.Path]::GetFullPath([string]$crashEvidence.lockPath)
            $reportedTempPath = [IO.Path]::GetFullPath([string]$crashEvidence.tempPath)
            $expectedLockPath = [IO.Path]::GetFullPath($target + '.lock')
            $expectedTempPrefix = ".{0}." -f [IO.Path]::GetFileName($target)
            if (-not $reportedLockPath.Equals($expectedLockPath, [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.Path]::GetDirectoryName($reportedTempPath).Equals([IO.Path]::GetFullPath($storeRoot), [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.Path]::GetFileName($reportedTempPath).StartsWith($expectedTempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.Path]::GetFileName($reportedTempPath).EndsWith('.tmp', [StringComparison]::OrdinalIgnoreCase)) {
                throw "$kind crash worker reported an artifact outside its exact TEMP-owned target."
            }

            $beforeFreshAttempt = Fingerprint $target
            $lockBeforeFreshAttempt = Fingerprint $reportedLockPath
            $tempBeforeFreshAttempt = Fingerprint $reportedTempPath
            $freshExit = Invoke-Recovery $kind $target $key $result
            if ($freshExit -eq 0 -or (Fingerprint $target) -ne $beforeFreshAttempt -or
                (Fingerprint $reportedLockPath) -ne $lockBeforeFreshAttempt -or
                (Fingerprint $reportedTempPath) -ne $tempBeforeFreshAttempt) {
                $failures.Add("$kind fresh crash lock was not authoritative")
            }

            [IO.File]::SetLastWriteTimeUtc($reportedLockPath, [DateTime]::UtcNow.AddSeconds(-31))
            $beforeStaleAttempt = Fingerprint $target
            $lockBeforeStaleAttempt = Fingerprint $reportedLockPath
            $tempBeforeStaleAttempt = Fingerprint $reportedTempPath
            $staleExit = Invoke-Recovery $kind $target $key $result
            if ($staleExit -eq 0 -or (Fingerprint $target) -ne $beforeStaleAttempt -or
                (Fingerprint $reportedLockPath) -ne $lockBeforeStaleAttempt -or
                (Fingerprint $reportedTempPath) -ne $tempBeforeStaleAttempt) {
                $failures.Add("$kind stale legacy lock was pathname-reaped or shared state changed")
            } else {
                $staleCrashLocksProtected++
            }

            # The verifier has exact TEMP-only ownership of this deliberately
            # crashed process artifact. Explicit operator cleanup is therefore
            # safe here; the product itself must never infer that authority from
            # age and delete a potentially replaced pathname.
            Remove-Item -LiteralPath $reportedLockPath -Force
            $explicitRecoveryExit = Invoke-Recovery $kind $target $key $result
            if ($explicitRecoveryExit -ne 0) {
                $failures.Add("$kind did not recover after explicit crash-lock removal")
            } else {
                Verify-RecoveredDocument $kind $target $key
                $explicitCrashRecoveries++
            }
            Assert-NoResidue $storeRoot $target
        }
    }

    $schemaRefusals = 0
    foreach ($scenario in @(
        @{ kind = 'favorites'; json = '{"broken":{}}' },
        @{ kind = 'seen'; json = '{"broken":[]}' },
        @{ kind = 'recent'; json = '{"version":2,"futureVersion":true}' },
        @{ kind = 'state'; json = '{"Version":999,"futureVersion":true}' },
        @{ kind = 'favorites'; json = '{broken' },
        @{ kind = 'seen'; json = '{broken' },
        @{ kind = 'recent'; json = '{broken' },
        @{ kind = 'state'; json = '{broken' }
    )) {
        $scenarioRoot = Join-Path $fullRoot ("protected-" + $schemaRefusals)
        New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
        $target = Join-Path $scenarioRoot ($scenario.kind + '.json')
        [IO.File]::WriteAllText($target, $scenario.json, [Text.UTF8Encoding]::new($false))
        $before = Fingerprint $target
        $exit = Invoke-Recovery $scenario.kind $target 'must-not-write' (Join-Path $scenarioRoot 'result.json')
        if ($exit -eq 0 -or (Fingerprint $target) -ne $before) {
            $failures.Add("$($scenario.kind) malformed/future schema was overwritten")
        } else {
            $schemaRefusals++
        }
        Assert-NoResidue $scenarioRoot $target
    }

    $sharedRoot = Join-Path $fullRoot 'three-owner-shared-state'
    New-Item -ItemType Directory -Force -Path $sharedRoot | Out-Null
    $favoritesPath = Join-Path $sharedRoot 'favorites.json'
    $seenPath = Join-Path $sharedRoot 'seen.json'
    $wpfAResult = Join-Path $sharedRoot 'wpf-a.json'
    $wpfBResult = Join-Path $sharedRoot 'wpf-b.json'
    $wpfAKeys = Join-Path $sharedRoot 'wpf-a-keys'
    $wpfBKeys = Join-Path $sharedRoot 'wpf-b-keys'
    $browserKeys = Join-Path $sharedRoot 'browser-keys'
    $wpfA = Start-Wpf @('--cross-runtime-shared-state-smoke', $wpfAResult, '--favorites-path', $favoritesPath, '--seen-path', $seenPath, '--key-root', $wpfAKeys, '--iterations', $Iterations)
    $wpfB = Start-Wpf @('--cross-runtime-shared-state-smoke', $wpfBResult, '--favorites-path', $favoritesPath, '--seen-path', $seenPath, '--key-root', $wpfBKeys, '--iterations', $Iterations)
    $env:PVU_FAVORITES_PATH = $favoritesPath
    $env:PVU_SEEN_PATH = $seenPath
    $env:CROSS_RUNTIME_KEY_ROOT = $browserKeys
    $env:CROSS_RUNTIME_ITERATIONS = $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)
    $browserSharedRun = Invoke-PnpmVitest 'src/app/api/crossRuntimeSharedState.worker.test.ts'
    $browserSharedOutput = $browserSharedRun.Output
    $browserSharedExit = $browserSharedRun.ExitCode
    $wpfAExit = Wait-Wpf $wpfA
    $wpfBExit = Wait-Wpf $wpfB
    if ($browserSharedExit -ne 0 -or $wpfAExit -ne 0 -or $wpfBExit -ne 0) {
        throw "three-owner shared-state workers failed: Browser=$browserSharedExit WPF-A=$wpfAExit WPF-B=$wpfBExit $($browserSharedOutput -join [Environment]::NewLine)"
    }
    $favorites = Get-Content -Raw -LiteralPath $favoritesPath | ConvertFrom-Json
    $seen = Get-Content -Raw -LiteralPath $seenPath | ConvertFrom-Json
    if (@($favorites.PSObject.Properties).Count -ne ($Iterations * 3) -or @($seen.PSObject.Properties).Count -ne ($Iterations * 3)) {
        $failures.Add('two WPF processes plus Browser did not preserve every disjoint favorite/seen update')
    }
    Assert-NoResidue $sharedRoot $favoritesPath
    Assert-NoResidue $sharedRoot $seenPath

    $recentRoot = Join-Path $fullRoot 'four-owner-recent'
    New-Item -ItemType Directory -Force -Path $recentRoot | Out-Null
    $recentPath = Join-Path $recentRoot 'recent-folders.json'
    @{ version = 1; lastFolderSet = @(); recentFolderSets = @(); updatedAtUtc = '2026-07-18T00:00:00.000Z'; futureRecent = @{ keep = $true } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recentPath -Encoding utf8
    $recentAKeys = Join-Path $recentRoot 'wpf-a-keys'
    $recentBKeys = Join-Path $recentRoot 'wpf-b-keys'
    $recentBrowserKeys = Join-Path $recentRoot 'browser-keys'
    $recentA = Start-Wpf @('--cross-runtime-recent-smoke', (Join-Path $recentRoot 'wpf-a.json'), '--recent-path', $recentPath, '--key-root', $recentAKeys, '--iterations', $Iterations)
    $recentB = Start-Wpf @('--cross-runtime-recent-smoke', (Join-Path $recentRoot 'wpf-b.json'), '--recent-path', $recentPath, '--key-root', $recentBKeys, '--iterations', $Iterations)
    $env:PVU_RECENT_FOLDERS_PATH = $recentPath
    $env:CROSS_RUNTIME_KEY_ROOT = $recentBrowserKeys
    $browserRecentRun = Invoke-PnpmVitest 'src/app/api/crossRuntimeRecent.worker.test.ts'
    $browserRecentOutput = $browserRecentRun.Output
    $browserRecentExit = $browserRecentRun.ExitCode
    $recentAExit = Wait-Wpf $recentA
    $recentBExit = Wait-Wpf $recentB
    if ($browserRecentExit -ne 0 -or $recentAExit -ne 0 -or $recentBExit -ne 0) {
        throw "four-owner recent workers failed: Browser=$browserRecentExit WPF-A=$recentAExit WPF-B=$recentBExit $($browserRecentOutput -join [Environment]::NewLine)"
    }
    $recent = Get-Content -Raw -LiteralPath $recentPath | ConvertFrom-Json
    $recentSets = @($recent.recentFolderSets | ForEach-Object { @($_) -join "`n" })
    $expectedMarkers = @(
        [IO.Path]::GetFullPath((Join-Path $recentAKeys 'wpf-latest')),
        [IO.Path]::GetFullPath((Join-Path $recentBKeys 'wpf-latest')),
        [IO.Path]::GetFullPath((Join-Path $recentBrowserKeys 'browser-latest')),
        [IO.Path]::GetFullPath((Join-Path $recentBrowserKeys 'third-latest'))
    )
    if ($recent.futureRecent.keep -ne $true -or @($recent.recentFolderSets).Count -gt 12 -or
        @($expectedMarkers | Where-Object { $_ -notin $recentSets }).Count -ne 0) {
        $failures.Add('two WPF processes plus Browser/third writer lost recent owners, cap, or unknown fields')
    }
    Assert-NoResidue $recentRoot $recentPath

    $browserLockRun = Invoke-PnpmVitest 'src/lib/fileWriteLock.test.ts'
    $browserLockOutput = $browserLockRun.Output
    if ($browserLockRun.ExitCode -ne 0) { throw "Browser shared-lock unit gate failed: $($browserLockOutput -join [Environment]::NewLine)" }
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }

    Remove-Item -LiteralPath $fullRoot -Recurse -Force
    [pscustomobject]@{
        ok = $true
        message = 'Actual WPF crash processes proved fresh and stale legacy locks fail closed; explicit TEMP-owned cleanup, residue cleanup, schema protection, and concurrent Browser/WPF writers passed.'
        iterations = $Iterations
        crashRecoveries = $explicitCrashRecoveries
        explicitCrashRecoveries = $explicitCrashRecoveries
        staleCrashLocksProtected = $staleCrashLocksProtected
        schemaRefusals = $schemaRefusals
        sharedStateWriters = 3
        sharedRecentWriters = 4
        liveOwnerLocksProtected = 4
        staleFixtureAgeSeconds = 31
        firstUiWriteRecovered = $false
        stalePathnameRecoveryDisabled = $true
        unknownFieldsPreserved = $true
        malformedAndFutureProtected = $true
        lockResidue = 0
        tempResidue = 0
        tempRootRemoved = -not (Test-Path -LiteralPath $fullRoot)
        browserPortUsed = $false
        sourceOrUserCacheTouched = $false
    } | ConvertTo-Json -Depth 5
}
finally {
    Remove-Item Env:PVU_FAVORITES_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:PVU_SEEN_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:PVU_RECENT_FOLDERS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:CROSS_RUNTIME_KEY_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:CROSS_RUNTIME_ITERATIONS -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $fullRoot) {
        Remove-Item -LiteralPath $fullRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
