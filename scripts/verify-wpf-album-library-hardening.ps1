param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP 'photoviewer-wpf-album-library-hardening.json')
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

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verifier root must stay under TEMP.'
}

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    & dotnet build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $exe = Join-Path $buildRoot 'PhotoViewer.Wpf.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "WPF executable was not found: $exe"
    }

    $process = Start-Process -FilePath $exe -ArgumentList @(
        '--album-library-hardening-smoke', ('"{0}"' -f $resultPath),
        '--album-path', ('"{0}"' -f $albumPath)
    ) -WindowStyle Hidden -PassThru -Wait
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Album library hardening smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'mixedFormatFiltered',
        'addStatusCounts',
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
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Album library hardening contract failed (exit $($process.ExitCode)): $($missing -join ', ')"
    }

    $outputDirectory = Split-Path -Parent $fullOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
    $result | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
