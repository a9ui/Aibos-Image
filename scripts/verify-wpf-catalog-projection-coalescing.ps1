param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-projection-coalescing-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--catalog-projection-coalescing-smoke', ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited `
        -and -not (Test-Path -LiteralPath $resultPath -PathType Leaf) `
        -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        throw "Catalog projection smoke produced no result within $OverallTimeoutSeconds seconds."
    }

    if (-not $process.HasExited) {
        $process.WaitForExit(5000) | Out-Null
    }
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    [pscustomobject]@{
        ok = [bool]$result.ok
        message = [string]$result.message
        captures = [long]$result.captures
        appliedCount = [int]$result.appliedCount
        discardedCount = [int]$result.discardedCount
        startCoalesced = [long]$result.startCoalesced
        anchorCoalesced = [long]$result.anchorCoalesced
        exitCode = $exitCode
    } | ConvertTo-Json -Compress
    if (-not $result.ok -or $exitCode -ne 0) {
        throw 'Catalog projection coalescing smoke failed.'
    }
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
