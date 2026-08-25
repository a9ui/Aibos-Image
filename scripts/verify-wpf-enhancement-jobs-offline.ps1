param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = '',
    [switch]$NoRestore,
    [switch]$KeepArtifacts,
    [ValidateRange(5, 120)]
    [int]$SmokeTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-jobs-sqlite-v1.json'
$localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        'dotnet.exe'
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-jobs-offline-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith(
    $tempPrefix,
    [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay below TEMP.'
$artifacts = Join-Path $runRoot 'artifacts'
$resultPath = Join-Path $runRoot 'result.json'
$previousNugetScratch = $env:NUGET_SCRATCH
$env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'
$process = $null

try {
    $contract = Get-Content -LiteralPath $contractPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    Assert-True ($contract.schemaVersion -eq 1) 'Jobs SQLite schema version drifted.'
    Assert-True (
        $contract.jobsWorkspaceSurface.offlinePassive.Contains(
            'do not arm the one-second poll')) (
        'Jobs SQLite offline passive rule is missing.')
    Assert-True (
        $contract.wpfRules.startsWorker -eq $false) (
        'The WPF direct reader may start a worker.')
    Assert-True (
        $contract.wpfRules.mutatesQueue -eq $false) (
        'The WPF direct reader may mutate the queue.')

    $buildArguments = @(
        'build',
        $project,
        '-c',
        $Configuration,
        '--artifacts-path',
        $artifacts,
        '--nologo'
    )
    if ($NoRestore) { $buildArguments += '--no-restore' }
    & $DotnetPath @buildArguments
    Assert-True ($LASTEXITCODE -eq 0) 'Aibos WPF build failed.'
    $wpfDll = Join-Path $artifacts (
        'bin\PhotoViewer.Wpf\{0}\PhotoViewer.Wpf.dll' -f
            $Configuration.ToLowerInvariant())
    Assert-True (
        (Test-Path -LiteralPath $wpfDll -PathType Leaf)) (
        'PhotoViewer.Wpf.dll build output is missing.')

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(
            ('"{0}"' -f $wpfDll),
            '--enhancement-jobs-offline-smoke',
            ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden `
        -PassThru
    if (-not $process.WaitForExit($SmokeTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Offline Jobs smoke timed out after $SmokeTimeoutSeconds seconds."
    }
    $exitCode = $process.ExitCode
    $process.Dispose()
    $process = $null
    Assert-True (
        (Test-Path -LiteralPath $resultPath -PathType Leaf)) (
        'Offline Jobs result is missing.')
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $resultSummary = $result | ConvertTo-Json -Compress -Depth 6
    Assert-True ($exitCode -eq 0 -and $result.ok -eq $true) (
        "Offline Jobs smoke failed with exit code $exitCode`: $resultSummary")
    foreach ($field in @(
        'passiveOfflineExact',
        'idleStable',
        'manualRefreshExact',
        'futureRejected',
        'malformedRejected',
        'noMutationOrStart')) {
        Assert-True ($result.$field -eq $true) "Offline Jobs gate failed: $field"
    }
    Assert-True ($result.starterCalls -eq 0) 'Passive Jobs attempted to start the Companion.'
    Assert-True ($result.jobApiTransportCalls -eq 0) 'Passive offline Jobs sent an API or mutation request.'
    Assert-True ($result.actualCompanionStarted -eq $false) 'The smoke started a real Companion.'
    Assert-True ($result.actualUserStoreRead -eq $false) 'The smoke read an actual user store.'

    [pscustomobject]@{
        allPassed = $true
        passiveOfflineExact = $true
        idleStable = $true
        manualRefreshExact = $true
        futureRejected = $true
        malformedRejected = $true
        noMutationOrStart = $true
        identityProbes = [int]$result.identityProbes
        actualCompanionStarted = $false
        actualUserStoreRead = $false
    } | ConvertTo-Json -Compress
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $process) {
        $process.Dispose()
    }
    $env:NUGET_SCRATCH = $previousNugetScratch
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot)) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith(
                $tempPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
