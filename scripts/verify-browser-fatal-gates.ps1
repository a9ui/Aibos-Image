param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempRoot = Join-Path $tempBase ('aibos-browser-fatal-gate-' + [guid]::NewGuid().ToString('N'))
$fullTempRoot = [IO.Path]::GetFullPath($tempRoot)
if (-not $fullTempRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullTempRoot"
}

function Get-IsolatedPort {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $candidate = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
    } while ($candidate -eq 3000)
    return $candidate
}

function Get-FileTreeFingerprint([string]$RootPath) {
    if (-not (Test-Path -LiteralPath $RootPath)) { return '<missing>' }
    $entries = Get-ChildItem -LiteralPath $RootPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($RootPath.Length).TrimStart('\', '/')
            "$relative|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
        }
    return [string]::Join("`n", $entries)
}

function Send-Request(
    [Net.Http.HttpClient]$Client,
    [Net.Http.HttpMethod]$Method,
    [string]$Uri,
    [hashtable]$Headers = @{}
) {
    $request = [Net.Http.HttpRequestMessage]::new($Method, $Uri)
    foreach ($entry in $Headers.GetEnumerator()) {
        if ($entry.Key -eq 'Host') {
            $request.Headers.Host = [string]$entry.Value
        }
        else {
            [void]$request.Headers.TryAddWithoutValidation([string]$entry.Key, [string]$entry.Value)
        }
    }
    try {
        return $Client.SendAsync($request).GetAwaiter().GetResult()
    }
    finally {
        $request.Dispose()
    }
}

$server = $null
$client = $null
$runtimeBuild = $null
$stderrPath = $null
try {
    New-Item -ItemType Directory -Path $fullTempRoot | Out-Null
    $fixtureDir = Join-Path $fullTempRoot 'images'
    New-Item -ItemType Directory -Path $fixtureDir | Out-Null
    $imagePath = Join-Path $fixtureDir 'Unicode 100% complete & safe!.png'
    [IO.File]::WriteAllBytes(
        $imagePath,
        [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZpXUAAAAASUVORK5CYII='))

    if (-not $SkipBuild) {
        & corepack pnpm build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $buildId = Join-Path $repoRoot '.next\BUILD_ID'
    if (-not (Test-Path -LiteralPath $buildId)) {
        throw 'Production build is missing. Run without -SkipBuild or build first.'
    }

    # Run the compiled app from a TEMP staging root so its process.cwd()-owned
    # index and thumbnail cache cannot touch the candidate's real .cache tree.
    $runtimeRoot = Join-Path $fullTempRoot 'runtime'
    New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
    $runtimeBuild = Join-Path $runtimeRoot '.next'
    New-Item -ItemType Junction -Path $runtimeBuild `
        -Target (Join-Path $repoRoot '.next') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'package.json') `
        -Destination (Join-Path $runtimeRoot 'package.json')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'next.config.js') `
        -Destination (Join-Path $runtimeRoot 'next.config.js')

    $candidateCache = Join-Path $repoRoot '.cache'
    $candidateCacheBefore = Get-FileTreeFingerprint $candidateCache
    $fixtureHashBefore = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash

    $port = Get-IsolatedPort
    $stdoutPath = Join-Path $fullTempRoot 'server.stdout.log'
    $stderrPath = Join-Path $fullTempRoot 'server.stderr.log'
    $node = (Get-Command node.exe -ErrorAction Stop).Source
    $nextBin = Join-Path $repoRoot 'node_modules\next\dist\bin\next'
    $server = Start-Process -FilePath $node -ArgumentList @(
        $nextBin,
        'start',
        '--hostname',
        '127.0.0.1',
        '--port',
        $port.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) -WorkingDirectory $runtimeRoot -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru

    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $baseUri = "http://127.0.0.1:$port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($server.HasExited) {
            throw "Isolated Browser server exited with code $($server.ExitCode)."
        }
        try {
            $readyResponse = Send-Request $client ([Net.Http.HttpMethod]::Get) $baseUri
            try {
                if ([int]$readyResponse.StatusCode -eq 200) {
                    $ready = $true
                    break
                }
            }
            finally {
                $readyResponse.Dispose()
            }
        }
        catch {
            # Server startup is still in progress.
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        $startupError = if (Test-Path -LiteralPath $stderrPath) {
            (Get-Content -LiteralPath $stderrPath -ErrorAction SilentlyContinue |
                Select-Object -Last 40) -join "`n"
        }
        else { '' }
        throw "Isolated Browser server did not become ready. $startupError"
    }

    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop)
    $listenerAddresses = @($listeners | Select-Object -ExpandProperty LocalAddress -Unique)
    $nonLoopbackListeners = @($listenerAddresses | Where-Object { $_ -notin @('127.0.0.1', '::1') })
    if ($listenerAddresses.Count -eq 0 -or $nonLoopbackListeners.Count -ne 0) {
        throw "Browser listener escaped loopback: $($listenerAddresses -join ', ')"
    }

    $encodedDir = [Uri]::EscapeDataString($fixtureDir)
    $scanResponse = Send-Request $client ([Net.Http.HttpMethod]::Get) "$baseUri/api/scan?dir=$encodedDir" @{
        Accept = 'text/event-stream'
    }
    try {
        $scanBody = $scanResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$scanResponse.StatusCode -ne 200) {
            $scanError = $scanBody
            if ($scanError.Length -gt 1000) { $scanError = $scanError.Substring(0, 1000) }
            throw "Scan returned HTTP $([int]$scanResponse.StatusCode): $scanError"
        }
    }
    finally {
        $scanResponse.Dispose()
    }
    $tokenMatch = [regex]::Match($scanBody, '"indexToken":"([^"]+)"')
    if (-not $tokenMatch.Success) { throw 'Scan did not return an index token.' }

    $indexToken = $tokenMatch.Groups[1].Value
    $encodedImage = [Uri]::EscapeDataString($imagePath)
    $encodedToken = [Uri]::EscapeDataString($indexToken)
    $thumbnailResponse = Send-Request $client ([Net.Http.HttpMethod]::Get) `
        "$baseUri/api/image?path=$encodedImage&thumb=true&indexToken=$encodedToken" @{
            'Sec-Fetch-Site' = 'same-origin'
            'Sec-Fetch-Mode' = 'no-cors'
            'Sec-Fetch-Dest' = 'image'
        }
    try {
        $thumbnailStatus = [int]$thumbnailResponse.StatusCode
        $thumbnailContentType = $thumbnailResponse.Content.Headers.ContentType.MediaType
        [void]$thumbnailResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    }
    finally {
        $thumbnailResponse.Dispose()
    }

    $foreignOriginResponse = Send-Request $client ([Net.Http.HttpMethod]::Post) `
        "$baseUri/api/open?path=$encodedImage&indexToken=$encodedToken" @{
            Origin = 'https://evil.example'
            'Sec-Fetch-Site' = 'cross-site'
        }
    try { $foreignOriginStatus = [int]$foreignOriginResponse.StatusCode }
    finally { $foreignOriginResponse.Dispose() }

    $foreignHostResponse = Send-Request $client ([Net.Http.HttpMethod]::Get) `
        "$baseUri/api/runtime" @{ Host = "photo.attacker.example:$port" }
    try { $foreignHostStatus = [int]$foreignHostResponse.StatusCode }
    finally { $foreignHostResponse.Dispose() }

    $outsidePath = [Uri]::EscapeDataString((Join-Path $fullTempRoot 'outside.png'))
    $outsideOpenResponse = Send-Request $client ([Net.Http.HttpMethod]::Post) `
        "$baseUri/api/open?path=$outsidePath&indexToken=$encodedToken"
    try { $outsideOpenStatus = [int]$outsideOpenResponse.StatusCode }
    finally { $outsideOpenResponse.Dispose() }

    $outsideDeleteResponse = Send-Request $client ([Net.Http.HttpMethod]::new('DELETE')) `
        "$baseUri/api/delete?path=$outsidePath&indexToken=$encodedToken"
    try { $outsideDeleteStatus = [int]$outsideDeleteResponse.StatusCode }
    finally { $outsideDeleteResponse.Dispose() }

    $candidateCacheAfter = Get-FileTreeFingerprint $candidateCache
    $fixtureHashAfter = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
    $candidateCacheTouched = $candidateCacheBefore -ne $candidateCacheAfter
    $fixtureSourceTouched = $fixtureHashBefore -ne $fixtureHashAfter

    $ok = $thumbnailStatus -eq 200 `
        -and $foreignOriginStatus -eq 403 `
        -and $foreignHostStatus -eq 403 `
        -and $outsideOpenStatus -in @(403, 404) `
        -and $outsideDeleteStatus -in @(403, 404) `
        -and -not $candidateCacheTouched `
        -and -not $fixtureSourceTouched
    if (-not $ok) { throw 'One or more Browser fatal-only integration gates failed.' }

    [pscustomobject]@{
        ok = $true
        port = $port
        listenerAddresses = $listenerAddresses
        thumbnailStatus = $thumbnailStatus
        thumbnailContentType = $thumbnailContentType
        foreignOriginStatus = $foreignOriginStatus
        foreignHostStatus = $foreignHostStatus
        outsideOpenStatus = $outsideOpenStatus
        outsideDeleteStatus = $outsideDeleteStatus
        userPort3000Touched = $false
        candidateCacheTouched = $candidateCacheTouched
        fixtureSourceTouched = $fixtureSourceTouched
        runtimeCacheRootIsTemp = $true
    } | ConvertTo-Json -Depth 4
}
catch {
    if ($stderrPath -and (Test-Path -LiteralPath $stderrPath)) {
        $serverErrorTail = (Get-Content -LiteralPath $stderrPath -ErrorAction SilentlyContinue |
            Select-Object -Last 40) -join "`n"
        if ($serverErrorTail) { Write-Error $serverErrorTail -ErrorAction Continue }
    }
    throw
}
finally {
    if ($client) { $client.Dispose() }
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        [void]$server.WaitForExit(5000)
    }
    if ($runtimeBuild -and (Test-Path -LiteralPath $runtimeBuild)) {
        $junction = Get-Item -LiteralPath $runtimeBuild -Force
        if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw "Refusing to recursively clean a non-junction runtime build: $runtimeBuild"
        }
        [IO.Directory]::Delete($runtimeBuild, $false)
    }
    if (Test-Path -LiteralPath $fullTempRoot) {
        [IO.Directory]::Delete("\\?\$fullTempRoot", $true)
    }
}
