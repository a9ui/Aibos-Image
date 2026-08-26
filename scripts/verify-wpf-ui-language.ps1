param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore,
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-ui-language-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$process = $null

Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
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
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF assembly was not found: $dll"

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--ui-language-smoke', ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "UI language smoke timed out after $OverallTimeoutSeconds seconds."
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null

    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) "UI language smoke produced no result. Process exit code: $processExitCode"
    $details = Get-Content -Raw -LiteralPath $resultPath
    $result = $details | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 6

    $required = @(
        'englishLoaded',
        'englishFavoriteFilterSurface',
        'englishGalleryShellSurface',
        'japaneseSaved',
        'japaneseHotApplied',
        'japanesePersisted',
        'japaneseReloaded',
        'japaneseFavoriteFilterSurface',
        'japaneseGalleryShellSurface',
        'englishSaved',
        'englishReloaded',
        'unknownLocalPreserved',
        'sharedSettingsUntouched'
    )
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    Assert-True ($processExitCode -eq 0 -and $result.ok -eq $true -and $failed.Count -eq 0) `
        "UI language invariants failed ($($failed -join ', ')): $details"
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
