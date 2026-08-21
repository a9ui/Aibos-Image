param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = '',
    [switch]$SkipBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-jobs-sqlite-v1.json'
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
    $contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
    Assert-True ($contract.contractId -eq 'PV-ENHANCE-JOBS-SQLITE-001') 'SQLite reader contract id is invalid.'
    Assert-True ($contract.jobsWorkspaceSurface.enhancement_jobs.requiredColumns.reader_payload_json -like 'UTF-8 JSON object*') 'Jobs workspace projection column is not contracted.'
    Assert-True ($contract.jobsWorkspaceSurface.jobDetailSurface.requiredColumns.payload_json -like 'UTF-8 JSON object*') 'Lazy Jobs detail payload is not contracted.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.maximumRows -eq 100000) 'Jobs workspace row bound drifted from the reader.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.maximumProjectionBytesPerRow -eq 1048576) 'Jobs workspace per-row projection bound drifted from the reader.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.maximumTotalProjectionBytesPerSnapshot -eq 536870912) 'Jobs workspace snapshot projection bound drifted from the reader.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.maximumDetailPayloadBytesPerRow -eq 1048576) 'Jobs detail payload bound drifted from the reader.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.sqliteMaximumValueBytes -eq 1114112) 'Jobs SQLite engine value bound drifted from the reader.'
    Assert-True ($contract.jobsWorkspaceSurface.resourceBounds.sqliteMaximumSqlBytes -eq 65536) 'Jobs SQLite SQL bound drifted from the reader.'
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
        $buildArguments = @(
            'build',
            $project,
            '-c',
            $Configuration,
            '--nologo',
            '-v:minimal',
            "-p:OutputPath=$buildOutput"
        )
        if ($NoRestore) { $buildArguments += '--no-restore' }
        & $DotnetPath @buildArguments
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
        'enhancementStateUnchanged',
        'malformedSqliteCasesRejected',
        'jobsWorkspaceReadOk',
        'jobsDetailsLazyLoaded',
        'jobsFullPayloadPreserved',
        'largeJobsWorkspaceReadOk',
        'largeJobsDetailsLazyLoaded',
        'largeJobsOversizedDetailRejected',
        'largeJobsStateUnchanged'
    )) {
        Assert-True ($result.$propertyName -eq $true) "SQLite reader invariant failed: $propertyName"
    }
    Assert-True ($result.malformedSqliteCaseCount -eq 9) 'SQLite reader did not execute every malformed row/payload/metadata/size/projection case.'
    Assert-True ($result.jobsWorkspaceJobCount -eq 5) 'Jobs workspace did not read every valid projection row.'
    Assert-True ($result.largeJobsWorkspaceJobCount -eq 40) 'Jobs workspace did not read every projection when full payloads exceeded 32 MiB.'
    Assert-True ($result.largeJobsFullPayloadBytes -gt (32MB)) 'Large synthetic full payload did not exceed the former aggregate response limit.'
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
