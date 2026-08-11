param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        'dotnet'
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-enhancement-sqlite-reader-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'
$fixtureRoot = Join-Path $runRoot 'images'
$resultPath = Join-Path $runRoot 'result.json'
$buildRoot = Join-Path $runRoot 'build'
$automationRoot = $null

try {
    New-Item -ItemType Directory -Path $fixtureRoot, $buildRoot -Force | Out-Null
    $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7l3agAAAAFElEQVR4nGPkEpFjQAJMDKiAVD4AANMABQ+5f2QAAAAASUVORK5CYII=')
    1..3 | ForEach-Object {
        [IO.File]::WriteAllBytes((Join-Path $fixtureRoot ("source-{0}.png" -f $_)), $pngBytes)
    }

    if ($SkipBuild) {
        $dll = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll"
    }
    else {
        $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        & $DotnetPath build $project -c $Configuration --nologo -v:minimal "-p:OutputPath=$buildOutput"
        Assert-True ($LASTEXITCODE -eq 0) "WPF build failed with exit code $LASTEXITCODE."
        $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    }
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) "WPF build output was not found: $dll"

    & $DotnetPath $dll `
        --modal-enhanced-smoke $resultPath `
        --folder $fixtureRoot `
        --sqlite-jobs
    $childExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'SQLite reader smoke did not produce JSON.'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $automationRoot = [IO.Path]::GetFullPath([string]$result.ProjectRoot)
    Assert-True $automationRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Automation root must stay under TEMP.'
    Assert-True ([IO.Path]::GetFileName($automationRoot).StartsWith('photoviewer-wpf-automation-', [StringComparison]::OrdinalIgnoreCase)) 'Automation root name is unexpected.'
    if ($childExitCode -ne 0) {
        $result | ConvertTo-Json -Depth 5 | Write-Host
        throw "SQLite reader smoke exited with $childExitCode."
    }
    foreach ($propertyName in @(
        'ok',
        'readOk',
        'validToggleAvailable',
        'toggledEnhanced',
        'toggledOriginal',
        'navigationResetToOriginal',
        'enhancementStateUnchanged'
    )) {
        Assert-True ($result.$propertyName -eq $true) "SQLite reader invariant failed: $propertyName"
    }
    Assert-True ([string]::Equals([IO.Path]::GetFileName($result.jobsPath), 'jobs.sqlite3', [StringComparison]::OrdinalIgnoreCase)) 'WPF did not read the SQLite fixture.'
    $result | ConvertTo-Json -Depth 5
}
finally {
    if ($automationRoot -and (Test-Path -LiteralPath $automationRoot)) {
        $resolvedAutomationRoot = [IO.Path]::GetFullPath($automationRoot)
        Assert-True $resolvedAutomationRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Refusing automation cleanup outside TEMP.'
        Remove-Item -LiteralPath $resolvedAutomationRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        Assert-True $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Refusing cleanup outside TEMP.'
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
