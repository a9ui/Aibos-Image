param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-selected-batch-enhancement.json"),
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 120,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-selected-batch-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$progressPath = $fullOutputPath + '.progress'
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    if ($SkipBuild) {
        $exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
    }
    else {
        New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
        $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }
        $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    }

    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "WPF executable was not found: $exe"
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }
    if (Test-Path -LiteralPath $progressPath) {
        Remove-Item -LiteralPath $progressPath -Force
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--selected-batch-enhancement-smoke', ('"{0}"' -f $fullOutputPath)) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $stage = if (Test-Path -LiteralPath $progressPath) { Get-Content -Raw -LiteralPath $progressPath } else { 'not started' }
        throw "Selected batch enhancement smoke timed out after $OverallTimeoutSeconds seconds at stage: $stage"
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "Selected batch enhancement smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 12
    $required = @(
        'ordinaryBrowsingPassive',
        'preflightPostZero',
        'reviewCancelPostZero',
        'escapeClosedReview',
        'keyboardSurface',
        'narrowLayoutFits',
        'responsiveOpen',
        'customAdapterPathDeferred',
        'boundedConcurrency',
        'doubleClickSuppressed',
        'doubleClickPromptProvenance',
        'failedOnlyRetry',
        'ambiguousPostSafe',
        'oneCreatedJobPerAcceptedSource',
        'stopUnsentOnly',
        'sourceUnchanged',
        'storesUnchanged',
        'managedOutputsEmpty'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Selected batch enhancement contract failed (exit $processExitCode): $($missing -join ', ')"
    }
    if (Test-Path -LiteralPath $progressPath) {
        Remove-Item -LiteralPath $progressPath -Force
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        for ($cleanupAttempt = 1; $cleanupAttempt -le 10; $cleanupAttempt++) {
            try {
                Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
                break
            }
            catch {
                if ($cleanupAttempt -eq 10) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
