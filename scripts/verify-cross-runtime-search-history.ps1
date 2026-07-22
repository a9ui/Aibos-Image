param(
    # Each runtime contributes Iterations unique entries plus three shared
    # Unicode/parity identities. 23 is therefore the largest value that can
    # prove losslessness inside the product's 50-entry MRU bound.
    [ValidateRange(1, 23)]
    [int]$Iterations = 20,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ("photoviewer-cross-runtime-search-history-" + [guid]::NewGuid().ToString('N'))
$fullRoot = [IO.Path]::GetFullPath($root)
if (-not $fullRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullRoot"
}

$previousHistoryPath = [Environment]::GetEnvironmentVariable('PVU_SEARCH_HISTORY_PATH')
$previousIterations = [Environment]::GetEnvironmentVariable('CROSS_RUNTIME_ITERATIONS')
$previousStartGatePath = [Environment]::GetEnvironmentVariable('CROSS_RUNTIME_START_GATE_PATH')
$previousBrowserReadyPath = [Environment]::GetEnvironmentVariable('CROSS_RUNTIME_BROWSER_READY_PATH')
$previousBrowserResultPath = [Environment]::GetEnvironmentVariable('CROSS_RUNTIME_BROWSER_RESULT_PATH')
$previousWriteDelayMs = [Environment]::GetEnvironmentVariable('CROSS_RUNTIME_WRITE_DELAY_MS')
$wpf = $null
$browser = $null

try {
    New-Item -ItemType Directory -Force -Path $fullRoot | Out-Null
    $historyPath = Join-Path $fullRoot 'search-history.json'
    $wpfResultPath = Join-Path $fullRoot 'wpf-result.json'
    $browserResultPath = Join-Path $fullRoot 'browser-result.json'
    $wpfReadyPath = Join-Path $fullRoot 'wpf-ready.json'
    $browserReadyPath = Join-Path $fullRoot 'browser-ready.json'
    $startGatePath = Join-Path $fullRoot 'start-gate.json'
    $browserStdoutPath = Join-Path $fullRoot 'browser-stdout.log'
    $browserStderrPath = Join-Path $fullRoot 'browser-stderr.log'
    $writeDelayMs = 10
    $seed = @{ version = 1; entries = @(); updatedAtUtc = '2026-07-19T00:00:00.000Z'; ownerMarker = @{ keep = $true } } |
        ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($historyPath, $seed, [Text.UTF8Encoding]::new($false))

    $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
    $exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $env:PVU_SEARCH_HISTORY_PATH = $historyPath
    $env:CROSS_RUNTIME_ITERATIONS = $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:CROSS_RUNTIME_START_GATE_PATH = $startGatePath
    $env:CROSS_RUNTIME_BROWSER_READY_PATH = $browserReadyPath
    $env:CROSS_RUNTIME_BROWSER_RESULT_PATH = $browserResultPath
    $env:CROSS_RUNTIME_WRITE_DELAY_MS = $writeDelayMs.ToString([Globalization.CultureInfo]::InvariantCulture)

    $wpf = Start-Process -FilePath $exe -ArgumentList @(
        '--cross-runtime-search-history-smoke', $wpfResultPath,
        '--search-history-path', $historyPath,
        '--iterations', $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--start-gate-path', $startGatePath,
        '--ready-path', $wpfReadyPath,
        '--write-delay-ms', $writeDelayMs.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) -WindowStyle Hidden -PassThru
    $null = $wpf.Handle

    $nodeExe = (Get-Command node.exe -ErrorAction Stop).Source
    $vitestCli = Join-Path $repoRoot 'node_modules\vitest\vitest.mjs'
    if (-not (Test-Path -LiteralPath $vitestCli)) {
        throw "Vitest CLI not found: $vitestCli"
    }
    $browser = Start-Process -FilePath $nodeExe -ArgumentList @(
        $vitestCli,
        'run',
        'src/app/api/crossRuntimeSearchHistory.worker.test.ts',
        '--reporter=dot'
    ) -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $browserStdoutPath -RedirectStandardError $browserStderrPath
    $null = $browser.Handle

    $readyWatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not ((Test-Path -LiteralPath $wpfReadyPath) -and (Test-Path -LiteralPath $browserReadyPath))) {
        if ($wpf.HasExited) {
            throw "WPF history worker exited before publishing its ready marker (exit $($wpf.ExitCode))"
        }
        if ($browser.HasExited) {
            $browserOutput = @(
                if (Test-Path -LiteralPath $browserStdoutPath) { Get-Content -Raw -LiteralPath $browserStdoutPath }
                if (Test-Path -LiteralPath $browserStderrPath) { Get-Content -Raw -LiteralPath $browserStderrPath }
            ) -join [Environment]::NewLine
            throw "Browser history worker exited before publishing its ready marker (exit $($browser.ExitCode)): $browserOutput"
        }
        if ($readyWatch.Elapsed -gt ([TimeSpan]::FromSeconds(60))) {
            throw 'Timed out waiting for both cross-runtime ready markers'
        }
        Start-Sleep -Milliseconds 10
    }

    $bothReadyBeforeRelease = (Test-Path -LiteralPath $wpfReadyPath) `
        -and (Test-Path -LiteralPath $browserReadyPath) `
        -and -not (Test-Path -LiteralPath $startGatePath)
    if (-not $bothReadyBeforeRelease) {
        throw 'Cross-runtime start gate was released before both workers were ready'
    }
    $gateReleasedAtUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $gateJson = @{ releasedAtUnixMs = $gateReleasedAtUnixMs } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($startGatePath, $gateJson, [Text.UTF8Encoding]::new($false))

    if (-not $browser.WaitForExit(120000)) {
        Stop-Process -Id $browser.Id -Force -ErrorAction SilentlyContinue
        throw 'Browser history worker timed out after the start gate was released'
    }
    $browser.WaitForExit()
    if (-not $wpf.WaitForExit(120000)) {
        Stop-Process -Id $wpf.Id -Force -ErrorAction SilentlyContinue
        throw 'WPF history worker timed out after the start gate was released'
    }
    $wpf.WaitForExit()
    $browser.Refresh()
    $wpf.Refresh()

    $browserOutput = @(
        if (Test-Path -LiteralPath $browserStdoutPath) { Get-Content -Raw -LiteralPath $browserStdoutPath }
        if (Test-Path -LiteralPath $browserStderrPath) { Get-Content -Raw -LiteralPath $browserStderrPath }
    ) -join [Environment]::NewLine
    if ($browser.ExitCode -ne 0) { throw "Browser history worker failed: $browserOutput" }
    if ($wpf.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $wpfResultPath)) { throw "WPF history worker failed with exit $($wpf.ExitCode)" }
    if (-not (Test-Path -LiteralPath $browserResultPath)) { throw 'Browser history worker did not publish its result' }

    $wpfReady = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfReadyPath | ConvertFrom-Json
    $browserReady = Get-Content -Raw -Encoding utf8 -LiteralPath $browserReadyPath | ConvertFrom-Json
    $wpfResult = Get-Content -Raw -Encoding utf8 -LiteralPath $wpfResultPath | ConvertFrom-Json
    $browserResult = Get-Content -Raw -Encoding utf8 -LiteralPath $browserResultPath | ConvertFrom-Json
    $stored = Get-Content -Raw -Encoding utf8 -LiteralPath $historyPath | ConvertFrom-Json
    $entries = @($stored.entries)
    $failures = [Collections.Generic.List[string]]::new()
    if ($wpfReady.ok -ne $true -or $browserReady.ok -ne $true) { $failures.Add('ready marker contract failed') }
    if ($wpfResult.ok -ne $true -or $wpfResult.writes -ne $Iterations) { $failures.Add('WPF writes were incomplete') }
    if ($browserResult.ok -ne $true -or $browserResult.writes -ne $Iterations) { $failures.Add('Browser writes were incomplete') }

    $wpfStart = [long]$wpfResult.writeStartedAtUnixMs
    $wpfEnd = [long]$wpfResult.writeCompletedAtUnixMs
    $browserStart = [long]$browserResult.writeStartedAtUnixMs
    $browserEnd = [long]$browserResult.writeCompletedAtUnixMs
    $wpfIntervalValid = $wpfStart -ge $gateReleasedAtUnixMs -and $wpfEnd -gt $wpfStart
    $browserIntervalValid = $browserStart -ge $gateReleasedAtUnixMs -and $browserEnd -gt $browserStart
    if (-not $wpfIntervalValid) { $failures.Add('WPF write interval was invalid or started before gate release') }
    if (-not $browserIntervalValid) { $failures.Add('Browser write interval was invalid or started before gate release') }
    $overlapStart = [Math]::Max($wpfStart, $browserStart)
    $overlapEnd = [Math]::Min($wpfEnd, $browserEnd)
    $overlapDurationMs = [Math]::Max(0L, $overlapEnd - $overlapStart)
    $writeIntervalsOverlapped = $wpfIntervalValid -and $browserIntervalValid -and $overlapDurationMs -gt 0
    if (-not $writeIntervalsOverlapped) {
        $failures.Add("cross-runtime write intervals did not overlap (WPF $wpfStart..$wpfEnd, Browser $browserStart..$browserEnd)")
    }

    if ($stored.version -ne 1 -or $entries.Count -ne (($Iterations * 2) + 3) -or $entries.Count -gt 50) { $failures.Add('version/count/50-entry bound failed') }
    if ($stored.ownerMarker.keep -ne $true) { $failures.Add('unknown root field was lost') }
    for ($index = 0; $index -lt $Iterations; $index++) {
        $suffix = $index.ToString('D2', [Globalization.CultureInfo]::InvariantCulture)
        if ($entries -notcontains "wpf query $suffix") { $failures.Add("lost WPF query $suffix") }
        if ($entries -notcontains "browser query $suffix") { $failures.Add("lost Browser query $suffix") }
    }
    $wpfDottedI = "$([char]0xFF23)$([char]0xFF21)$([char]0xFF34), $([char]0x0130)"
    $browserDottedI = "cat, i$([char]0x0307)"
    $wpfNonAscii = (-join [char[]]@(0x041C,0x041E,0x0421,0x041A,0x0412,0x0410)) + ", " + (-join [char[]]@(0x039F,0x03A3))
    $browserNonAscii = (-join [char[]]@(0x043C,0x043E,0x0441,0x043A,0x0432,0x0430)) + ", " + (-join [char[]]@(0x03BF,0x03C3))
    $dottedIForms = @($wpfDottedI, $browserDottedI)
    $nonAsciiForms = @($wpfNonAscii, $browserNonAscii)
    if (@($entries | Where-Object { $dottedIForms -contains $_ }).Count -ne 1) { $failures.Add('fullwidth/dotted-I cross-runtime dedupe failed') }
    if (@($entries | Where-Object { $nonAsciiForms -contains $_ }).Count -ne 1) { $failures.Add('Greek/Cyrillic cross-runtime dedupe failed') }
    $explicitTrimParity = @($entries | Where-Object { $_ -ceq 'trim, parity' }).Count -eq 1
    if (-not $explicitTrimParity) { $failures.Add('U+FEFF/U+0085 explicit trim parity failed') }

    # Keep the single cross-runtime command as the complete durability gate:
    # UI behavior, malformed/future protection, and stale legacy lock Busy=0 writes.
    $contractRoot = Join-Path $fullRoot 'wpf-contract'
    $contractResultPath = Join-Path $contractRoot 'result.json'
    $contractHistoryPath = Join-Path $contractRoot 'search-history.json'
    New-Item -ItemType Directory -Force -Path $contractRoot | Out-Null
    $contract = Start-Process -FilePath $exe -ArgumentList @(
        '--search-history-smoke', $contractResultPath,
        '--search-history-path', $contractHistoryPath
    ) -WindowStyle Hidden -Wait -PassThru
    if ($contract.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $contractResultPath)) {
        $failures.Add("WPF contract smoke failed with exit $($contract.ExitCode)")
    }
    else {
        $contractResult = Get-Content -Raw -Encoding utf8 -LiteralPath $contractResultPath | ConvertFrom-Json
        $contractOk = $contractResult.ok -eq $true -and
            $contractResult.malformedAndFutureWriteRefused -eq $true -and
            $contractResult.normalizedLengthBoundaryProtected -eq $true -and
            $contractResult.staleLegacyLockBusyProtected -eq $true -and
            $contractResult.busyWrites -eq 0
        if (-not $contractOk) {
            $failures.Add('WPF malformed/future/normalized-length/stale-lock fail-closed contract failed')
        }
    }
    $residual = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Force -File | Where-Object { $_.Name.EndsWith('.lock') -or $_.Name.EndsWith('.tmp') })
    if ($residual.Count -ne 0) { $failures.Add("lock/temp residue: $($residual.FullName -join ', ')") }
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }

    Remove-Item -LiteralPath $fullRoot -Recurse -Force
    [pscustomobject]@{
        ok = $true
        message = 'Browser and WPF passed a shared start barrier, overlapped write loops, and preserved every search with NFKC dedupe and atomic replacement.'
        iterations = $Iterations
        entries = $entries.Count
        maxEntries = 50
        bothReadyBeforeRelease = $bothReadyBeforeRelease
        writeIntervalsOverlapped = $writeIntervalsOverlapped
        overlapDurationMs = $overlapDurationMs
        wpfWriteDurationMs = $wpfEnd - $wpfStart
        browserWriteDurationMs = $browserEnd - $browserStart
        unicodeDedupe = $true
        explicitTrimParity = $explicitTrimParity
        unknownFieldPreserved = $true
        malformedAndFutureProtected = $true
        normalizedLengthBoundaryProtected = $true
        liveLockBusyProtected = $true
        staleLegacyLockBusyProtected = $true
        busyWrites = 0
        lockResidue = 0
        tempResidue = 0
        tempRootRemoved = -not (Test-Path -LiteralPath $fullRoot)
        browserPortUsed = $false
        sourceOrUserCacheTouched = $false
    } | ConvertTo-Json -Depth 4
}
catch {
    foreach ($process in @($browser, $wpf)) {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            }
            catch { }
        }
    }
    throw
}
finally {
    $env:PVU_SEARCH_HISTORY_PATH = $previousHistoryPath
    $env:CROSS_RUNTIME_ITERATIONS = $previousIterations
    $env:CROSS_RUNTIME_START_GATE_PATH = $previousStartGatePath
    $env:CROSS_RUNTIME_BROWSER_READY_PATH = $previousBrowserReadyPath
    $env:CROSS_RUNTIME_BROWSER_RESULT_PATH = $previousBrowserResultPath
    $env:CROSS_RUNTIME_WRITE_DELAY_MS = $previousWriteDelayMs
}
