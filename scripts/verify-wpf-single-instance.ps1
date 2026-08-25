param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-single-instance-' + [guid]::NewGuid().ToString('N'))))
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Single-instance verifier root must stay under TEMP.'
}

try {
    $buildRoot = Join-Path $runRoot 'build'
    $resultPath = Join-Path $runRoot 'result.json'
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $arguments = @(
        'build',
        $project,
        '-c',
        $Configuration,
        "-p:OutputPath=$buildOutput",
        '--nologo',
        '-v:minimal'
    )
    if ($NoRestore) { $arguments += '--no-restore' }
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit code $LASTEXITCODE."
    }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    & $DotnetPath $dll --single-instance-coordinator-smoke $resultPath
    $exitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Single-instance smoke did not produce a result.'
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    foreach ($property in @(
        'ok',
        'primaryExclusive',
        'signalAccepted',
        'exactlyOneActivation',
        'reacquired',
        'unownedExistingHandleRecovered'
    )) {
        if ($result.PSObject.Properties[$property].Value -ne $true) {
            throw "Single-instance invariant failed: $property"
        }
    }
    if ($exitCode -ne 0) {
        throw "Single-instance smoke exited with $exitCode."
    }
    [pscustomobject]@{
        allPassed = $true
        processIsolation = $true
        activationSignal = $true
        cleanReacquire = $true
        unownedExistingHandleRecovered = $true
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolved = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean verifier path outside TEMP: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
