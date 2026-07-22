param(
    [ValidateRange(1, 100)]
    [int]$Iterations = 20,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $tempBase ("photoviewer-cross-runtime-recent-" + [guid]::NewGuid().ToString('N'))
$fullRoot = [IO.Path]::GetFullPath($root)
if (-not $fullRoot.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a non-temp verifier root: $fullRoot"
}

try {
    $keys = Join-Path $fullRoot 'markers'
    $recentPath = Join-Path $fullRoot 'recent-folders.json'
    $wpfResultPath = Join-Path $fullRoot 'wpf-result.json'
    New-Item -ItemType Directory -Force -Path $keys | Out-Null
    @{ version = 1; lastFolderSet = @(); recentFolderSets = @(); updatedAtUtc = '2026-07-18T00:00:00.000Z'; futureFlag = @{ keep = $true } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recentPath -Encoding utf8

    $project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
    $exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing WPF executable: $exe" }

    $pnpm = (Get-Command pnpm.cmd -ErrorAction Stop).Source
    $env:PVU_RECENT_FOLDERS_PATH = $recentPath
    $env:CROSS_RUNTIME_KEY_ROOT = $keys
    $env:CROSS_RUNTIME_ITERATIONS = $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)
    $wpf = Start-Process -FilePath $exe -ArgumentList @(
        '--cross-runtime-recent-smoke', $wpfResultPath,
        '--recent-path', $recentPath,
        '--key-root', $keys,
        '--iterations', $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) -WindowStyle Hidden -PassThru
    $browserOutput = & $pnpm exec vitest run src/app/api/crossRuntimeRecent.worker.test.ts --reporter=dot 2>&1
    $browserExitCode = $LASTEXITCODE
    $wpf.WaitForExit()
    $wpf.Refresh()
    if ($browserExitCode -ne 0) { throw "Browser/third recent workers failed with exit ${browserExitCode}: $($browserOutput -join [Environment]::NewLine)" }
    if ($wpf.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $wpfResultPath)) { throw "WPF recent worker failed with exit $($wpf.ExitCode)." }

    $wpfResult = Get-Content -Raw -LiteralPath $wpfResultPath | ConvertFrom-Json
    $stored = Get-Content -Raw -LiteralPath $recentPath | ConvertFrom-Json
    $markers = @('browser-latest', 'wpf-latest', 'third-latest')
    $sets = @($stored.recentFolderSets | ForEach-Object { @($_) -join "`n" })
    $failures = [Collections.Generic.List[string]]::new()
    if ($wpfResult.ok -ne $true -or $wpfResult.recentWrites -ne $Iterations) { $failures.Add('WPF did not complete its recent writes') }
    if ($stored.version -ne 1 -or $stored.recentFolderSets.Count -gt 12) { $failures.Add('recent schema or 12-set bound failed') }
    if ($stored.futureFlag.keep -ne $true) { $failures.Add('unknown field was not preserved') }
    foreach ($marker in $markers) {
        $expected = [IO.Path]::GetFullPath((Join-Path $keys $marker))
        if (-not ($sets | Where-Object { $_ -eq $expected })) { $failures.Add("lost latest owner marker: $marker") }
    }
    $last = @($stored.lastFolderSet)
    if ($last.Count -ne 1 -or $last[0] -notin ($markers | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $keys $_)) })) { $failures.Add('lastFolderSet was not a deterministic valid last writer marker') }
    $residual = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Force -File | Where-Object { $_.Name.EndsWith('.lock') -or $_.Name.EndsWith('.tmp') })
    if ($residual.Count -ne 0) { $failures.Add("lock/temp residue: $($residual.FullName -join ', ')") }
    if ($failures.Count -gt 0) { throw ("Cross-runtime recent verification failed: " + ($failures -join '; ')) }

    Remove-Item -LiteralPath $fullRoot -Recurse -Force
    [pscustomobject]@{
        ok = $true; message = 'Browser route, WPF real writer, and independent third writer merged shared recent folders with bounded additive history.'
        iterations = $Iterations; recentSetCount = $sets.Count; validJson = $true; unknownFieldPreserved = $true
        latestOwnerSetsPreserved = $true; lastFolderSetPolicy = 'last successful lock holder wins'; lockResidue = 0; tempResidue = 0
        tempRootRemoved = -not (Test-Path -LiteralPath $fullRoot); browserPortUsed = $false; sourceOrUserCacheTouched = $false
    } | ConvertTo-Json -Depth 5
}
catch { throw $_ }
