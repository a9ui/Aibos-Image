param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [string]$TargetFrameworkOverride = ''
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
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-photoreal-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration --nologo -v:minimal "-p:OutputPath=$buildOutput"
    }
    else {
        & $DotnetPath msbuild $project -restore "-property:TargetFramework=$TargetFrameworkOverride" "-property:OutputPath=$buildOutput" "-property:Configuration=$Configuration" -nologo -verbosity:minimal
    }
    Assert-True ($LASTEXITCODE -eq 0) "WPF build failed with exit code $LASTEXITCODE."

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF build output was not found: $dll"
    & $DotnetPath $dll --modal-photoreal-smoke $resultPath
    $childExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Photoreal smoke did not produce JSON.'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-True ($childExitCode -eq 0) "Photoreal smoke exited with $childExitCode."
    foreach ($propertyName in @(
        'ok',
        'selected',
        'opened',
        'passive',
        'started',
        'toolbarContract',
        'versionCycleContract',
        'requestContract',
        'resetPromptContract',
        'appSettingsPromptContract',
        'defaultPromptContract',
        'independentCompanionContract',
        'galleryContextNoModal',
        'galleryContextDirectContract',
        'modalPhotorealOperation',
        'sharedQueueRoute',
        'sourceUntouched'
    )) {
        Assert-True ($result.$propertyName -eq $true) "Photoreal invariant failed: $propertyName"
    }
    $result | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        Assert-True $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Refusing cleanup outside TEMP.'
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
