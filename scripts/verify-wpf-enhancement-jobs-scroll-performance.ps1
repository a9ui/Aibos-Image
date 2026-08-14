param(
    [string]$Configuration = "Release",
    [string]$DotnetPath = "",
    [ValidateRange(30, 180)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        throw "The local .NET 10 runtime was not found: $localDotnet10"
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-enhancement-jobs-scroll-performance-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    & $DotnetPath build $project `
        -c $Configuration `
        --no-restore `
        "-p:BaseOutputPath=$buildRoot\" `
        --nologo `
        -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Get-ChildItem -LiteralPath $buildRoot `
        -Filter 'PhotoViewer.Wpf.dll' `
        -File `
        -Recurse |
        Select-Object -First 1
    if ($null -eq $dll) {
        throw "WPF assembly was not found under the TEMP build root."
    }

    $process = Start-Process `
        -FilePath $DotnetPath `
        -ArgumentList @(
            ('"{0}"' -f $dll.FullName),
            '--enhancement-jobs-scroll-performance-smoke',
            ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Enhancement Jobs scroll performance smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Enhancement Jobs scroll performance smoke produced no result. Exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 3
    if ($processExitCode -ne 0 -or $result.ok -ne $true) {
        throw "Enhancement Jobs scroll performance contract failed (exit $processExitCode)."
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith(
                $tempPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
