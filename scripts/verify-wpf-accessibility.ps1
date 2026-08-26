param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore,
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 150,
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-accessibility-' + [guid]::NewGuid().ToString('N') + '.json')),
    [string]$FallbackOutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-accessibility-fallback-' + [guid]::NewGuid().ToString('N') + '.json')),
    [string]$ScreenshotPath = (Join-Path $env:TEMP ('photoviewer-wpf-high-contrast-' + [guid]::NewGuid().ToString('N') + '.png')),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-BoundedWpf {
    param(
        [string[]]$Arguments,
        [string]$Description
    )

    $script:process = Start-Process -FilePath $DotnetPath `
        -ArgumentList $Arguments `
        -WindowStyle Hidden `
        -PassThru

    if (-not $script:process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        $timedOutPid = $script:process.Id
        Stop-Process -Id $timedOutPid -Force -ErrorAction SilentlyContinue
        throw "$Description timed out after $OverallTimeoutSeconds seconds (PID $timedOutPid)."
    }

    $exitCode = $script:process.ExitCode
    $script:process.WaitForExit()
    $script:process.Dispose()
    $script:process = $null
    return $exitCode
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-accessibility-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$fallbackResultPath = [IO.Path]::GetFullPath($FallbackOutputPath)
$shotPath = [IO.Path]::GetFullPath($ScreenshotPath)
$shotErrorPath = $shotPath + '.error.txt'
$process = $null

Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

    if ($SkipBuild) {
        $dll = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll"
    }
    else {
        $buildArgs = @(
            'build', $project,
            '-c', $Configuration,
            "-p:OutputPath=$buildOutput",
            '--nologo', '-v:minimal'
        )
        if ($NoRestore) { $buildArgs += '--no-restore' }
        & $DotnetPath @buildArgs
        Assert-True ($LASTEXITCODE -eq 0) "WPF build failed with exit code $LASTEXITCODE."
        $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    }

    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF assembly was not found: $dll"

    foreach ($path in @($resultPath, $fallbackResultPath, $shotPath, $shotErrorPath)) {
        $directory = Split-Path -Parent $path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        Assert-True (-not (Test-Path -LiteralPath $path)) "Refusing to overwrite verifier output: $path"
    }

    $smokeExitCode = Invoke-BoundedWpf -Description 'Accessibility smoke' -Arguments @(
        ('"{0}"' -f $dll),
        '--high-contrast-smoke', ('"{0}"' -f $resultPath)
    )
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) "Accessibility smoke produced no result. Process exit code: $smokeExitCode"
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @('colorResources', 'liveBrushes', 'paletteFlag', 'actionAccessibility', 'dialogFocusContracts', 'restored', 'brushesRestored')
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    Assert-True ($smokeExitCode -eq 0 -and $result.ok -eq $true -and $missing.Count -eq 0) `
        "Accessibility contract failed ($($missing -join ', '))."

    $fallbackExitCode = Invoke-BoundedWpf -Description 'Accessibility fallback smoke' -Arguments @(
        ('"{0}"' -f $dll),
        '--accessibility-fallback-smoke', ('"{0}"' -f $fallbackResultPath)
    )
    Assert-True (Test-Path -LiteralPath $fallbackResultPath -PathType Leaf) "Accessibility fallback smoke produced no result. Process exit code: $fallbackExitCode"
    $fallback = Get-Content -Raw -LiteralPath $fallbackResultPath | ConvertFrom-Json
    $fallbackRequired = @(
        'followsSystem',
        'fullOverrideSaved',
        'fullOverrideEffective',
        'standardMotionActive',
        'reduceOverrideSaved',
        'activeMotionCanceled',
        'reducedMotionInstant',
        'reducedTransparencyOpaque',
        'localOverridesPersisted',
        'unknownLocalPreserved',
        'localOverridesReloaded',
        'nullFollowsInjectedSystem',
        'explicitFullOverridesSystem',
        'highContrastWins',
        'highContrastExitRestoresOverride',
        'sharedSettingsUntouched'
    )
    $fallbackMissing = @($fallbackRequired | Where-Object { $fallback.$_ -ne $true })
    Assert-True ($fallbackExitCode -eq 0 -and $fallback.ok -eq $true -and $fallbackMissing.Count -eq 0 -and @($fallback.nonOpaqueKeys).Count -eq 0) `
        "Accessibility fallback contract failed ($($fallbackMissing -join ', '))."

    $captureExitCode = Invoke-BoundedWpf -Description 'High-contrast screenshot' -Arguments @(
        ('"{0}"' -f $dll),
        '--shot', ('"{0}"' -f $shotPath),
        '--screen', 'landing',
        '--show-app-settings',
        '--force-high-contrast',
        '--shot-width', '1280',
        '--shot-height', '820'
    )
    if ($captureExitCode -ne 0 -or -not (Test-Path -LiteralPath $shotPath -PathType Leaf)) {
        $diagnostic = if (Test-Path -LiteralPath $shotErrorPath -PathType Leaf) {
            Get-Content -Raw -LiteralPath $shotErrorPath
        }
        else {
            'No managed screenshot diagnostic was produced.'
        }
        throw "High-contrast screenshot failed with exit code $captureExitCode. $diagnostic"
    }

    $bytes = [IO.File]::ReadAllBytes($shotPath)
    Assert-True ($bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4e -and $bytes[3] -eq 0x47) `
        'High-contrast screenshot is not a valid PNG.'

    [pscustomobject]@{
        ok = $true
        highContrast = $result
        fallback = $fallback
        screenshot = $shotPath
        screenshotBytes = $bytes.Length
    } | ConvertTo-Json -Depth 8
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $process) {
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
