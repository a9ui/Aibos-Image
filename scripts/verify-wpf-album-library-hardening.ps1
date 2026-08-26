param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP 'photoviewer-wpf-album-library-hardening.json'),
    [string]$ScreenshotPath = '',
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore,
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-album-library-hardening-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$smokeRoot = Join-Path $runRoot 'smoke'
$resultPath = Join-Path $smokeRoot 'result.json'
$albumPath = Join-Path $smokeRoot 'albums.json'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null
$capturePath = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $buildArgs = @(
        'build', $project,
        '-c', $Configuration,
        "-p:OutputPath=$buildOutput",
        '--nologo', '-v:minimal'
    )
    if ($NoRestore) { $buildArgs += '--no-restore' }
    & $DotnetPath @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }

    $process = Start-Process -FilePath $DotnetPath -ArgumentList @(
        ('"{0}"' -f $dll),
        '--album-library-hardening-smoke', ('"{0}"' -f $resultPath),
        '--album-path', ('"{0}"' -f $albumPath)
    ) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Album library hardening smoke timed out after $OverallTimeoutSeconds seconds."
    }
    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Album library hardening smoke produced no result. Process exit code: $processExitCode"
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'presentationContract',
        'sourceSafetyPlacementContract',
        'mixedFormatFiltered',
        'addStatusCounts',
        'statusLanguageContract',
        'readOffDispatcher',
        'mutationOffDispatcher',
        'availabilityOffDispatcher',
        'generationGuard',
        'availabilityCancellation',
        'closeCancelsAvailability',
        'openUsesLatestRevision',
        'committedCloseReconciled',
        'dispatcherHeartbeat',
        'doubleSubmissionBlocked',
        'busyRecovered',
        'liveLockPreserved',
        'actionToolTips',
        'actionAutomationHelpText',
        'actionFocusVisuals',
        'sourceFilesUntouched',
        'noResidue'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Album library hardening contract failed (exit $processExitCode): $($missing -join ', ')"
    }

    if (-not [string]::IsNullOrWhiteSpace($ScreenshotPath)) {
        $fullScreenshotPath = [IO.Path]::GetFullPath($ScreenshotPath)
        $capturePath = Join-Path $tempRoot ('aibos-album-library-' + [guid]::NewGuid().ToString('N') + '.png')
        $process = Start-Process -FilePath $DotnetPath -ArgumentList @(
            ('"{0}"' -f $dll),
            '--album-library-shot', ('"{0}"' -f $capturePath),
            '--shot-width', '1120',
            '--shot-height', '760'
        ) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "Album library screenshot timed out after $OverallTimeoutSeconds seconds."
        }
        $captureExitCode = $process.ExitCode
        $process.WaitForExit()
        $process.Dispose()
        $process = $null
        if ($captureExitCode -ne 0 -or -not (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
            throw "Album library screenshot failed with exit code $captureExitCode."
        }
        $signature = [IO.File]::ReadAllBytes($capturePath)
        if ($signature.Length -lt 8 -or
            $signature[0] -ne 0x89 -or
            $signature[1] -ne 0x50 -or
            $signature[2] -ne 0x4e -or
            $signature[3] -ne 0x47) {
            throw 'Album library screenshot is not a PNG.'
        }
        $screenshotDirectory = Split-Path -Parent $fullScreenshotPath
        if (-not [string]::IsNullOrWhiteSpace($screenshotDirectory)) {
            New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null
        }
        Copy-Item -LiteralPath $capturePath -Destination $fullScreenshotPath -Force
    }

    $outputDirectory = Split-Path -Parent $fullOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
    $result | ConvertTo-Json -Depth 8
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
    if ($null -ne $capturePath -and (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
        $resolvedCapturePath = [IO.Path]::GetFullPath($capturePath)
        $captureFileName = [IO.Path]::GetFileName($resolvedCapturePath)
        $captureDirectory = [IO.Path]::GetDirectoryName($resolvedCapturePath).TrimEnd('\', '/')
        if (-not $captureDirectory.Equals($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $captureFileName.StartsWith('aibos-album-library-', [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetExtension($resolvedCapturePath).Equals('.png', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an unexpected Album Library capture path: $resolvedCapturePath"
        }
        Remove-Item -LiteralPath $resolvedCapturePath -Force
    }
}
