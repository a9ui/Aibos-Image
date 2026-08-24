param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-enhancement-notifications.json"),
    [string]$DotnetPath = "dotnet",
    [switch]$NoRestore,
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$fixturePath = Join-Path $repoRoot "contracts\fixtures\enhancement-video-tools-v2-reader-v1.json"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-notification-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}
if (-not $fullOutputPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Synthetic verifier output must stay under TEMP."
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
        throw "WPF notification verifier build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "Built WPF DLL was not found: $dll"
    }
    if (Test-Path -LiteralPath $fullOutputPath -PathType Leaf) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }

    $process = Start-Process `
        -FilePath $DotnetPath `
        -ArgumentList @(
            ('"{0}"' -f $dll),
            '--enhancement-notification-smoke',
            ('"{0}"' -f $fullOutputPath),
            '--fixture',
            ('"{0}"' -f $fixturePath)) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "WPF notification verifier exceeded $OverallTimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $fullOutputPath -PathType Leaf) {
            Get-Content -LiteralPath $fullOutputPath -Raw
        } else {
            'No result JSON was produced.'
        }
        throw "WPF notification smoke failed with exit code $($process.ExitCode). $detail"
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "WPF notification smoke did not produce a result JSON."
    }
    $result = Get-Content -LiteralPath $fullOutputPath -Raw | ConvertFrom-Json
    if ($result.success -ne $true) {
        throw "WPF notification smoke reported failure: $($result | ConvertTo-Json -Depth 8 -Compress)"
    }
    $result
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = (Resolve-Path -LiteralPath $runRoot).Path
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier root outside TEMP."
        }
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 9) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
