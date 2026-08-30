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
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot (
    'aibos-wpf-photoreal-safety-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith(
    $tempPrefix,
    [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'

$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$previousPromptPolicyPath = [Environment]::GetEnvironmentVariable(
    'AIBOS_WPF_PROMPT_POLICY_PATH',
    'Process')

try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    [Environment]::SetEnvironmentVariable(
        'AIBOS_WPF_PROMPT_POLICY_PATH',
        (Join-Path $runRoot 'missing-wpf-prompts.local.json'),
        'Process')
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration --nologo -v:minimal `
            "-p:OutputPath=$buildOutput"
    }
    else {
        & $DotnetPath msbuild $project -restore `
            "-property:TargetFramework=$TargetFrameworkOverride" `
            "-property:OutputPath=$buildOutput" `
            "-property:Configuration=$Configuration" `
            -nologo -verbosity:minimal
    }
    Assert-True ($LASTEXITCODE -eq 0) `
        "WPF build failed with exit code $LASTEXITCODE."

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    Assert-True (Test-Path -LiteralPath $dll -PathType Leaf) `
        "WPF build output was not found: $dll"
    & $DotnetPath $dll --photoreal-safety-smoke $resultPath
    $childExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) `
        'Photoreal safety smoke did not produce JSON.'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($childExitCode -ne 0 -or $result.ok -ne $true) {
        Write-Output ($result | ConvertTo-Json -Depth 8)
    }
    Assert-True ($childExitCode -eq 0) `
        "Photoreal safety smoke exited with $childExitCode."
    foreach ($propertyName in @(
        'ok',
        'initialSelectorsClosed',
        'failureMatrixBlocked',
        'validHealthPublished',
        'validHealthEnabledSelectors',
        'exactAllowlist',
        'legacyRetryPreserved',
        'unknownRowsReaderOnly',
        'modalClassificationExact',
        'unknownMutationRequestsZero',
        'authoritativeBatchHealthBlocked'
    )) {
        Assert-True ($result.$propertyName -eq $true) `
            "Photoreal safety invariant failed: $propertyName"
    }
    Assert-True ($result.readerOnlyMutationRequests -eq 0) `
        'A reader-only photoreal row emitted a mutation request.'
    Assert-True ($result.failureEvidence.Count -ge 7) `
        'Photoreal safety failure matrix is incomplete.'
    Assert-True (@($result.failureEvidence | Where-Object { -not $_.blocked }).Count -eq 0) `
        'A health failure mode published or mutated a Krea job.'
    $result | ConvertTo-Json -Depth 8
}
finally {
    [Environment]::SetEnvironmentVariable(
        'AIBOS_WPF_PROMPT_POLICY_PATH',
        $previousPromptPolicyPath,
        'Process')
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        Assert-True $resolvedRunRoot.StartsWith(
            $tempPrefix,
            [StringComparison]::OrdinalIgnoreCase) `
            'Refusing cleanup outside TEMP.'
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
