param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-window-work-area-' + [guid]::NewGuid().ToString('N') + '.json')),
    [string]$DotnetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$dll = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll"
$resultPath = [IO.Path]::GetFullPath($OutputPath)

try {
    if (-not $SkipBuild) {
        & $DotnetPath build $project -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "WPF assembly was not found: $dll" }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--window-work-area-smoke', ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'window work-area smoke exceeded 30 seconds and was stopped'
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'window work-area smoke did not write its result' }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $failures = @()
    if ($process.ExitCode -ne 0) { $failures += "window work-area smoke exited with $($process.ExitCode)" }
    if ($result.ok -ne $true) { $failures += "result was not ok: $($result.message)" }
    if ($result.usedCurrentMonitor -ne $true) { $failures += 'maximize did not use the injected current monitor' }
    if ($result.wideSidebarLayout -ne $true) { $failures += 'wide Japanese sidebar, button text, or scrollbar did not fit' }
    if ($result.restoredExactly -ne $true) { $failures += 'restore bounds changed after maximize' }
    if ($result.disconnectedContained -ne $true) { $failures += 'disconnected-monitor restore remained off-screen' }
    if ($result.oversizedContained -ne $true) { $failures += 'resolution-change restore exceeded the current work area' }
    if ($result.dpiEquivalentContained -ne $true) { $failures += 'DPI-equivalent restore exceeded the new DIP work area' }
    if ($result.compactContained -ne $true) { $failures += 'work area below both design minimums clipped adaptive chrome' }
    if ($result.shortContained -ne $true) { $failures += 'work area below the design minimum height clipped adaptive chrome' }
    if ($result.safeFallback -ne $true) { $failures += 'monitor lookup failure did not use a safe fallback' }
    if ($result.fallbackRestored -ne $true) { $failures += 'fallback maximize did not restore exact bounds' }
    if ($result.fallbackOffscreenContained -ne $true) { $failures += 'fallback restore did not normalize off-screen bounds' }
    $result | ConvertTo-Json -Depth 6
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }
}
finally {
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
}
