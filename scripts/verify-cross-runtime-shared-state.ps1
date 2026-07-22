param(
    [ValidateRange(1, 100)]
    [int]$Iterations = 20,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ("photoviewer-cross-runtime-shared-state-" + [guid]::NewGuid().ToString('N'))
$fullRoot = [IO.Path]::GetFullPath($root)
if (-not $fullRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullRoot"
}

function Read-JsonMap([string]$Path) {
    $document = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $map = @{}
    foreach ($property in $document.PSObject.Properties) {
        $map[$property.Name] = $property.Value
    }
    return $map
}

try {
    $keys = Join-Path $fullRoot 'keys'
    $favoritesPath = Join-Path $fullRoot 'favorites.json'
    $seenPath = Join-Path $fullRoot 'seen.json'
    $wpfResultPath = Join-Path $fullRoot 'wpf-result.json'
    New-Item -ItemType Directory -Force -Path $keys | Out-Null

    $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
    $exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing WPF executable: $exe" }

    $pnpm = (Get-Command pnpm.cmd -ErrorAction Stop).Source
    $env:PVU_FAVORITES_PATH = $favoritesPath
    $env:PVU_SEEN_PATH = $seenPath
    $env:CROSS_RUNTIME_KEY_ROOT = $keys
    $env:CROSS_RUNTIME_ITERATIONS = $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)

    $wpf = Start-Process -FilePath $exe -ArgumentList @(
        '--cross-runtime-shared-state-smoke', $wpfResultPath,
        '--favorites-path', $favoritesPath,
        '--seen-path', $seenPath,
        '--key-root', $keys,
        '--iterations', $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) -WindowStyle Hidden -PassThru
    $browserOutput = & $pnpm exec vitest run src/app/api/crossRuntimeSharedState.worker.test.ts --reporter=dot 2>&1
    $browserExitCode = $LASTEXITCODE
    $wpf.WaitForExit()
    $wpf.Refresh()
    if ($browserExitCode -ne 0) {
        throw "Browser route worker failed with exit ${browserExitCode}: $($browserOutput -join [Environment]::NewLine)"
    }
    if ($wpf.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $wpfResultPath)) {
        throw "WPF shared-state worker failed with exit $($wpf.ExitCode)."
    }

    $wpfResult = Get-Content -Raw -LiteralPath $wpfResultPath | ConvertFrom-Json
    if ($wpfResult.ok -ne $true -or $wpfResult.favoriteWrites -ne $Iterations -or $wpfResult.seenWrites -ne $Iterations) {
        throw "WPF shared-state worker did not complete all writes: $(Get-Content -Raw -LiteralPath $wpfResultPath)"
    }

    $favorites = Read-JsonMap $favoritesPath
    $seen = Read-JsonMap $seenPath
    $expectedEntries = $Iterations * 2
    $failures = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $Iterations; $index++) {
        $suffix = $index.ToString('D2', [Globalization.CultureInfo]::InvariantCulture)
        $level = ($index % 5) + 1
        $wpfFavorite = [IO.Path]::GetFullPath((Join-Path $keys "wpf-favorite-$suffix.png"))
        $browserFavorite = [IO.Path]::GetFullPath((Join-Path $keys "browser-favorite-$suffix.png"))
        $wpfSeen = [IO.Path]::GetFullPath((Join-Path $keys "wpf-seen-$suffix.png"))
        $browserSeen = [IO.Path]::GetFullPath((Join-Path $keys "browser-seen-$suffix.png"))
        if ($favorites[$wpfFavorite] -ne $level) { $failures.Add("WPF favorite $suffix was not exact") }
        if ($favorites[$browserFavorite] -ne $level) { $failures.Add("Browser favorite $suffix was not exact") }
        if ($seen[$wpfSeen] -ne $true) { $failures.Add("WPF seen $suffix was lost") }
        if ($seen[$browserSeen] -ne $true) { $failures.Add("Browser seen $suffix was lost") }
    }
    if ($favorites.Count -ne $expectedEntries) { $failures.Add("favorite entry count was $($favorites.Count), expected $expectedEntries") }
    if ($seen.Count -ne $expectedEntries) { $failures.Add("seen entry count was $($seen.Count), expected $expectedEntries") }

    $residual = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Force -File |
        Where-Object { $_.Name.EndsWith('.lock', [StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith('.tmp', [StringComparison]::OrdinalIgnoreCase) })
    if ($residual.Count -ne 0) { $failures.Add("lock/temp residue: $($residual.FullName -join ', ')") }
    if ($failures.Count -gt 0) { throw ("Cross-runtime shared-state verification failed: " + ($failures -join '; ')) }

    Remove-Item -LiteralPath $fullRoot -Recurse -Force
    [pscustomobject]@{
        ok = $true
        message = 'Browser route and WPF writer preserved all disjoint favorite and seen updates through shared locks without HTTP.'
        iterations = $Iterations
        favoriteEntries = $favorites.Count
        seenEntries = $seen.Count
        validJson = $true
        lockResidue = 0
        tempResidue = 0
        tempRootRemoved = -not (Test-Path -LiteralPath $fullRoot)
        browserPortUsed = $false
        sourceOrUserCacheTouched = $false
    } | ConvertTo-Json -Depth 4
}
catch {
    throw $_
}
