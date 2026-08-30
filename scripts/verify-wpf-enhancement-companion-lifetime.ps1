param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = '',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$mainWindowSourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml.cs'
$companionSourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementCompanion.cs'
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
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-companion-lifetime-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay below TEMP.'
$artifacts = Join-Path $runRoot 'artifacts'
$resultPath = Join-Path $runRoot 'result.json'
$automationResultPath = Join-Path $runRoot 'automation-isolation.json'
$lazyResumeResultPath = Join-Path $runRoot 'queue-lazy-resume.json'
$previousNugetScratch = $env:NUGET_SCRATCH
$env:NUGET_SCRATCH = Join-Path $runRoot 'nuget-scratch'

try {
    $mainWindowSource = Get-Content -LiteralPath $mainWindowSourcePath -Raw
    $companionSource = Get-Content -LiteralPath $companionSourcePath -Raw
    $ordinaryStartupLazy =
        $mainWindowSource -notmatch 'StartEnhancementCompanionApiForApplicationLaunchAsync' -and
        $companionSource -notmatch 'StartEnhancementCompanionApiForApplicationLaunchAsync'
    $passiveMethod = [regex]::Match(
        $companionSource,
        'private async Task<EnhancementApiResponse\?>\s+EnsureEnhancementCompanionOwnershipForPassiveReadAsync[\s\S]*?private static bool ShouldReverifyEnhancementCompanionAfterAuthenticatedRequest',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant).Value
    $passiveReadProbeOnly =
        $passiveMethod -match 'ProbeEnhancementCompanionOwnershipAsync' -and
        $passiveMethod -notmatch 'TryStartOwnedEnhancementCompanion'
    $explicitActionAutoStartPreserved =
        $companionSource -match 'EnsureEnhancementCompanionReadyForExplicitActionAsync[\s\S]*?EnsureEnhancementCompanionApiReadyAsync' -and
        $companionSource -match 'TryStartOwnedEnhancementCompanion\(out string startError\)'
    Assert-True $ordinaryStartupLazy 'Ordinary WPF startup still references Companion auto-start.'
    Assert-True $passiveReadProbeOnly 'A passive Companion read can start a process.'
    Assert-True $explicitActionAutoStartPreserved 'Explicit Enhancement action lost owned Companion auto-start.'

    & $DotnetPath build $project -c $Configuration --artifacts-path $artifacts --nologo
    Assert-True ($LASTEXITCODE -eq 0) 'Aibos WPF build failed.'
    $wpfDll = Join-Path $artifacts (
        'bin\PhotoViewer.Wpf\{0}\PhotoViewer.Wpf.dll' -f
            $Configuration.ToLowerInvariant())
    Assert-True (Test-Path -LiteralPath $wpfDll -PathType Leaf) 'PhotoViewer.Wpf.dll build output is missing.'

    & $DotnetPath $wpfDll --enhancement-companion-lifetime-smoke $resultPath
    $smokeExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Companion lifetime result is missing.'
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($smokeExitCode -eq 0) "Companion lifetime smoke exited with $smokeExitCode."
    Assert-True ($result.AllPassed -eq $true) 'Companion lifetime policy failed.'
    Assert-True ($result.PassiveOwnedStopped -eq $true) 'Passive owned Companion was not stopped.'
    Assert-True ($result.RepeatedCloseIdempotent -eq $true) 'Repeated close was not idempotent.'
    Assert-True ($result.DurableOwnedReleased -eq $true) 'Durable active Companion was not released.'
    Assert-True ($result.ExitedOwnedNotSignalled -eq $true) 'Exited owned process was signalled.'
    Assert-True ($result.UnownedPreserved -eq $true) 'Unowned process was touched.'
    Assert-True ($result.RequestClassificationExact -eq $true) 'Companion request lifetime classification drifted.'

    & $DotnetPath $wpfDll --automation-isolation-smoke $automationResultPath
    $automationExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $automationResultPath -PathType Leaf) 'Automation isolation result is missing.'
    $automationResult = Get-Content -LiteralPath $automationResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($automationExitCode -eq 0 -and $automationResult.ok -eq $true) 'Automation isolation smoke failed.'

    & $DotnetPath $wpfDll --enhancement-queue-lazy-resume-smoke $lazyResumeResultPath
    $lazyResumeExitCode = $LASTEXITCODE
    Assert-True (Test-Path -LiteralPath $lazyResumeResultPath -PathType Leaf) 'Queue lazy Resume result is missing.'
    $lazyResumeResult = Get-Content -LiteralPath $lazyResumeResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($lazyResumeExitCode -eq 0 -and $lazyResumeResult.ok -eq $true) 'Queue lazy Resume smoke failed.'
    Assert-True ($lazyResumeResult.passiveDidNotStart -eq $true) 'Passive queue UI started the Companion.'
    Assert-True ($lazyResumeResult.explicitResumeExact -eq $true) 'Explicit queue Resume was not exact.'
    Assert-True ($lazyResumeResult.duplicateGuarded -eq $true) 'Duplicate queue Resume was not guarded.'
    Assert-True ($lazyResumeResult.walFixtureValid -eq $true) 'Queue bootstrap smoke did not create a valid TEMP SQLite WAL fixture.'
    Assert-True ($lazyResumeResult.recoveryPreservedQueueState -eq $true) 'Queue recovery changed paused/count/order semantics before Resume.'
    Assert-True ($lazyResumeResult.recoveryBeforeHealth -eq $true) 'Explicit bootstrap did not recover the authenticated queue before its first health read.'
    Assert-True ($lazyResumeResult.healthBeforeRecoveryRequests -eq 0) 'Explicit bootstrap read health before WAL recovery.'

    [pscustomobject]@{
        allPassed = $true
        passiveOwnedStopped = [bool]$result.PassiveOwnedStopped
        repeatedCloseIdempotent = [bool]$result.RepeatedCloseIdempotent
        durableOwnedReleased = [bool]$result.DurableOwnedReleased
        exitedOwnedNotSignalled = [bool]$result.ExitedOwnedNotSignalled
        unownedPreserved = [bool]$result.UnownedPreserved
        requestClassificationExact = [bool]$result.RequestClassificationExact
        ordinaryStartupLazy = $ordinaryStartupLazy
        passiveReadProbeOnly = $passiveReadProbeOnly
        explicitActionAutoStartPreserved = $explicitActionAutoStartPreserved
        automationIsolationPreserved = $true
        lazyResumeExact = $true
        walBootstrapRecoveryExact = $true
        actualCompanionStarted = $false
    } | ConvertTo-Json -Compress
}
finally {
    $env:NUGET_SCRATCH = $previousNugetScratch
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot)) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
