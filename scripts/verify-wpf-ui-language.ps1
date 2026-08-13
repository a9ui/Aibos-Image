param(
    [string]$Configuration = 'Release',
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
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    Assert-True ($LASTEXITCODE -eq 0) "WPF build failed with exit code $LASTEXITCODE."

    $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    Assert-True (Test-Path -LiteralPath $exe -PathType Leaf) "WPF executable was not found: $exe"

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--ui-language-smoke', ('"{0}"' -f $resultPath)) `
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
        'japaneseSaved',
        'japanesePersisted',
        'japaneseReloaded',
        'japaneseFavoriteFilterSurface',
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
