[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild,
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$mainCode = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.ExternalFileDrop.cs'
$externalVideoCode = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.ExternalVideoDrop.cs'
$mediaFixture = Join-Path $repoRoot (
    'local-native\PhotoViewer.Wpf\SmokeFixtures\' +
    'valid-h3-864x480-124f-h264-aac.mp4.b64')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-video-drag-in-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$process = $null
$previousDotNetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$previousDotNetRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $dropSource = Get-Content -LiteralPath $mainCode -Raw -Encoding UTF8
    $externalVideoSource = Get-Content -LiteralPath $externalVideoCode -Raw -Encoding UTF8
    if ($dropSource -notmatch 'FileShare\.Read' `
        -or $dropSource -notmatch 'TryReadExternalFileDropSourceVersion' `
        -or $dropSource -notmatch 'MaxExternalFileDropBytes' `
        -or $externalVideoSource -notmatch 'ExternalFileDropSourceVersion' `
        -or $externalVideoSource -notmatch '_externalVideoDropGeneration' `
        -or $externalVideoSource -notmatch 'FileShare\.Read') {
        throw 'External video intake must retain the existing identity pin and bounded file rules.'
    }

    if (-not (Test-Path -LiteralPath $mediaFixture -PathType Leaf)) {
        throw "Synthetic H.264/AAC media fixture was not found: $mediaFixture"
    }

    if ($DotNetPath -eq 'dotnet') {
        $localDotNet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
        if (Test-Path -LiteralPath $localDotNet10 -PathType Leaf) {
            $DotNetPath = $localDotNet10
        }
    }
    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    if (-not $SkipBuild) {
        & $dotNetExecutable build $project `
            -c $Configuration `
            --artifacts-path $buildRoot `
            --nologo `
            -v:minimal
        if ($LASTEXITCODE -ne 0) {
            throw "WPF video drag-in verifier build failed with exit code $LASTEXITCODE."
        }
    }

    $dll = if ($SkipBuild) {
        Join-Path $repoRoot (
            'local-native\PhotoViewer.Wpf\bin\' + $Configuration +
            '\net10.0-windows\PhotoViewer.Wpf.dll')
    }
    else {
        Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.dll' `
            -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($dll)) {
        throw 'Built WPF DLL was not found.'
    }

    $process = Start-Process `
        -FilePath $dotNetExecutable `
        -ArgumentList @(
            ('"{0}"' -f $dll),
            '--video-drag-in-smoke',
            ('"{0}"' -f $resultPath),
            '--media-fixture-base64',
            ('"{0}"' -f $mediaFixture)) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "WPF video drag-in verifier exceeded $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'WPF video drag-in smoke did not produce a result JSON.'
    }

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $required = @(
        'classification',
        'singleVideoPass',
        'invalidRejected',
        'mixedRejected',
        'multipleRejected',
        'directoryRejected',
        'unsupportedRejected',
        'surfaceSafe',
        'realTransportReady',
        'explicitAiStillBlocked',
        'writeBlockedWhilePinned',
        'modalPinnedAcrossBackgroundSelection',
        'sourceSeamReusable',
        'staleSourceSeamRejected',
        'pinReleasedAfterClose',
        'passive',
        'sourceUntouched')
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or $failed.Count -gt 0) {
        throw ('Video drag-in smoke failed: ' + ($result | ConvertTo-Json -Depth 8 -Compress) `
            + '; failed=' + ($failed -join ','))
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = (Resolve-Path -LiteralPath $runRoot).Path
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a verifier root outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $previousDotNetRootX64, 'Process')
}
