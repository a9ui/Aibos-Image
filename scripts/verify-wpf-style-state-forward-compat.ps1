param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-style-state-forward-compat.json"),
    [string]$DotnetPath = "dotnet",
    [switch]$NoRestore,
    [ValidateRange(1, 180)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-style-state-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $buildArguments = @(
        'build',
        $project,
        '-c',
        $Configuration,
        "-p:OutputPath=$buildOutput",
        '--nologo',
        '-v:minimal'
    )
    if ($NoRestore) { $buildArguments += '--no-restore' }
    & $DotnetPath @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--style-state-forward-compat-smoke', ('"{0}"' -f $fullOutputPath)) `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Style state forward-compat smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "Style state forward-compat smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 4
    $required = @(
        'localPersistencePathCapabilities',
        'migrationPreserved',
        'legacyFieldsRemoved',
        'viewerUnknownPreserved',
        'incrementalActivityWrite',
        'idempotentReplay',
        'concurrentLatestUnknownFieldsPreserved',
        'externalKnownEditProtected',
        'unsupportedFutureStyleProtected',
        'malformedStyleProtected',
        'oversizedStyleProtected',
        'unsupportedFutureActivityProtected',
        'sourceUnchanged'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Style state forward-compat contract failed (exit $processExitCode): $($missing -join ', ')"
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
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
