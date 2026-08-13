param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$contract = Join-Path $repoRoot 'contracts\shared-root-locator-v1.json'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$runRoot = Join-Path $tempBase ('aibos-shared-root-locator-' + [guid]::NewGuid().ToString('N'))
$fixtureRoot = Join-Path $runRoot 'fixture'
$resultPath = Join-Path $runRoot 'result.json'
$activationRoot = Join-Path $runRoot 'activation'
$leaseProofRoot = Join-Path $runRoot 'lease-proof'
$buildOutput = Join-Path $runRoot 'build'
$holderProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $localDotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet10 -PathType Leaf) {
        $localDotnet10
    }
    else {
        'dotnet'
    }
}

function Start-DotNetSmokeProcess {
    param([string[]]$Arguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotnetPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $quotedArguments = foreach ($argument in $Arguments) {
        if ($argument.Contains('"')) {
            throw 'Smoke-process arguments must not contain quote characters.'
        }
        '"' + $argument + '"'
    }
    $startInfo.Arguments = $quotedArguments -join ' '
    return [Diagnostics.Process]::Start($startInfo)
}

function Wait-SmokeSignal {
    param(
        [Diagnostics.Process]$Process,
        [string]$Path,
        [string]$Description
    )

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf) -and
        -not $Process.HasExited -and
        $watch.Elapsed -lt [TimeSpan]::FromSeconds(10)) {
        Start-Sleep -Milliseconds 25
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $exitDetail = if ($Process.HasExited) { " exit=$($Process.ExitCode)" } else { '' }
        throw "$Description did not become ready.$exitDetail"
    }
}

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "WPF build failed with exit $LASTEXITCODE."
    }

    $dll = Join-Path $buildOutput 'PhotoViewer.Wpf.dll'
    & $DotnetPath $dll --shared-root-locator-smoke $resultPath --contract $contract --temp-root $fixtureRoot
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Shared root locator smoke failed with exit $exitCode."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Shared root locator smoke produced no result.'
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $failedCases = @($result.cases | Where-Object { $_.ok -ne $true })
    if ($result.ok -ne $true -or
        $result.activation -ne 'reader-only' -or
        $result.defaultPathShapeOk -ne $true -or
        $result.caseCount -ne 23 -or
        $failedCases.Count -ne 0) {
        $failedIds = @($failedCases | ForEach-Object { $_.id }) -join ', '
        throw "Shared root locator contract failed. Failed cases: $failedIds"
    }

    $changedTrees = @($result.cases | Where-Object { $_.treeUnchanged -ne $true })
    $implicitLocators = @($result.cases | Where-Object { $_.noImplicitLocatorCreated -ne $true })
    $splitRoots = @($result.cases | Where-Object { $_.crossLocationMatches -ne $true })
    if ($changedTrees.Count -ne 0 -or
        $implicitLocators.Count -ne 0 -or
        $splitRoots.Count -ne 0) {
        throw 'The reader changed a fixture or created a missing locator.'
    }

    $activationCases = @(
        'valid',
        'sqlite',
        'store-override',
        'invalid',
        'legacy',
        'overrides-only',
        'legacy-uninitialized'
    )
    $activationResults = foreach ($caseId in $activationCases) {
        $caseRoot = Join-Path $activationRoot $caseId
        $caseResult = Join-Path $runRoot ("activation-$caseId.json")
        & $DotnetPath $dll --shared-root-activation-smoke $caseResult --case $caseId --temp-root $caseRoot
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "Shared root activation case '$caseId' failed with exit $exitCode."
        }
        if (-not (Test-Path -LiteralPath $caseResult -PathType Leaf)) {
            throw "Shared root activation case '$caseId' produced no result."
        }

        Get-Content -Raw -LiteralPath $caseResult | ConvertFrom-Json
    }

    $failedActivationCases = @($activationResults | Where-Object {
        $_.ok -ne $true -or
        $_.rootMatches -ne $true -or
        $_.allCanonicalPaths -ne $true -or
        $_.outputsMatch -ne $true -or
        $_.enhancementStoreMatches -ne $true -or
        $_.pathsMatchEnvironment -ne $true -or
        $_.pathCountMatches -ne $true -or
        $_.productionResolversMatch -ne $true -or
        $_.readerLeaseHeld -ne $true -or
        $_.productionLeasePathMatches -ne $true -or
        $_.nonSharedUntouched -ne $true -or
        $_.treeUnchanged -ne $true -or
        $_.fixedForLifetime -ne $true
    })
    if ($failedActivationCases.Count -ne 0) {
        $failedIds = @($failedActivationCases | ForEach-Object { $_.caseId }) -join ', '
        throw "WPF shared root activation failed. Failed cases: $failedIds"
    }

    $legacyRoot = Join-Path $leaseProofRoot 'legacy'
    $dataRootA = Join-Path $leaseProofRoot 'data-a'
    $dataRootB = Join-Path $leaseProofRoot 'data-b'
    $leaseDirectory = Join-Path $leaseProofRoot 'leases'
    $locatorPath = Join-Path $leaseProofRoot 'shared-root.v1.json'
    New-Item -ItemType Directory -Path $legacyRoot, $dataRootA, $dataRootB -Force | Out-Null

    $invokeWriter = {
        param(
            [string]$Mode,
            [string]$Result,
            [string]$DataRoot,
            [string]$SelectedLeaseDirectory = $leaseDirectory,
            [string]$SelectedLocatorPath = $locatorPath
        )
        & $DotnetPath $dll --shared-root-lease-writer-smoke $Result `
            --mode $Mode `
            --temp-root $leaseProofRoot `
            --locator-path $SelectedLocatorPath `
            --shared-data-root $DataRoot `
            --lease-directory $SelectedLeaseDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Locator writer smoke '$Mode' failed with exit $LASTEXITCODE."
        }
        if (-not (Test-Path -LiteralPath $Result -PathType Leaf)) {
            throw "Locator writer smoke '$Mode' produced no result."
        }
        return Get-Content -Raw -LiteralPath $Result | ConvertFrom-Json
    }

    $redirectedLeaseTarget = Join-Path $leaseProofRoot 'redirected-lease-target'
    $redirectedLeaseDirectory = Join-Path $leaseProofRoot 'redirected-leases'
    New-Item -ItemType Directory -Path $redirectedLeaseTarget -Force | Out-Null
    New-Item -ItemType Junction -Path $redirectedLeaseDirectory -Target $redirectedLeaseTarget | Out-Null
    $redirectedLocatorPath = Join-Path $leaseProofRoot 'redirected-shared-root.v1.json'
    $redirectedResultPath = Join-Path $leaseProofRoot 'redirected-lease-result.json'
    $redirected = & $invokeWriter 'create' $redirectedResultPath $dataRootA $redirectedLeaseDirectory $redirectedLocatorPath
    if ($redirected.ok -ne $true -or
        $redirected.acquired -ne $false -or
        $redirected.errorCode -ne 'locator-writer-lease-unavailable' -or
        $redirected.locatorChanged -ne $false -or
        (Test-Path -LiteralPath $redirectedLocatorPath)) {
        throw 'A redirected locator lease directory did not fail closed.'
    }

    $startHolder = {
        param([string]$Prefix)
        $holderResult = Join-Path $leaseProofRoot ($Prefix + '-holder.json')
        $ready = Join-Path $leaseProofRoot ($Prefix + '.ready')
        $release = Join-Path $leaseProofRoot ($Prefix + '.release')
        $arguments = @(
            $dll,
            '--shared-root-lease-holder-smoke', $holderResult,
            '--temp-root', $leaseProofRoot,
            '--locator-path', $locatorPath,
            '--legacy-root', $legacyRoot,
            '--ready-path', $ready,
            '--release-path', $release,
            '--lease-directory', $leaseDirectory
        )
        $process = Start-DotNetSmokeProcess -Arguments $arguments
        $null = $holderProcesses.Add($process)
        Wait-SmokeSignal -Process $process -Path $ready -Description "Locator reader '$Prefix'"
        return [pscustomobject]@{
            Process = $process
            Result = $holderResult
            Release = $release
        }
    }

    $releaseHolder = {
        param($Holder, [string]$ExpectedRoot)
        [IO.File]::WriteAllText($Holder.Release, 'release')
        if (-not $Holder.Process.WaitForExit(10000)) {
            throw 'Locator reader holder did not exit after release.'
        }
        if ($Holder.Process.ExitCode -ne 0) {
            throw "Locator reader holder failed with exit $($Holder.Process.ExitCode)."
        }
        if (-not (Test-Path -LiteralPath $Holder.Result -PathType Leaf)) {
            throw 'Locator reader holder produced no result.'
        }
        $holderResult = Get-Content -Raw -LiteralPath $Holder.Result | ConvertFrom-Json
        if ($holderResult.ok -ne $true -or
            $holderResult.pathCount -ne 7 -or
            [IO.Path]::GetFullPath($holderResult.sharedDataRoot) -ne [IO.Path]::GetFullPath($ExpectedRoot)) {
            throw 'Locator reader holder resolved the wrong fixed root.'
        }
        return $holderResult
    }

    $missingHolder = & $startHolder 'missing'
    $blockedCreateResultPath = Join-Path $leaseProofRoot 'blocked-create.json'
    $blockedCreate = & $invokeWriter 'create' $blockedCreateResultPath $dataRootA
    if ($blockedCreate.ok -ne $true -or
        $blockedCreate.acquired -ne $false -or
        $blockedCreate.errorCode -ne 'locator-lease-busy' -or
        $blockedCreate.locatorChanged -ne $false -or
        (Test-Path -LiteralPath $locatorPath)) {
        throw 'An exclusive locator create was not blocked by the live reader.'
    }
    $null = & $releaseHolder $missingHolder $legacyRoot

    $createResultPath = Join-Path $leaseProofRoot 'create.json'
    $created = & $invokeWriter 'create' $createResultPath $dataRootA
    if ($created.ok -ne $true -or
        $created.acquired -ne $true -or
        $created.locatorChanged -ne $true -or
        [IO.Path]::GetFullPath($created.resolvedRoot) -ne [IO.Path]::GetFullPath($dataRootA)) {
        throw 'Exclusive locator create did not publish the expected synthetic root.'
    }

    $resolvedHolder = & $startHolder 'resolved-a'
    $blockedReplaceResultPath = Join-Path $leaseProofRoot 'blocked-replace.json'
    $blockedReplace = & $invokeWriter 'replace' $blockedReplaceResultPath $dataRootB
    if ($blockedReplace.ok -ne $true -or
        $blockedReplace.acquired -ne $false -or
        $blockedReplace.errorCode -ne 'locator-lease-busy' -or
        $blockedReplace.locatorChanged -ne $false -or
        [IO.Path]::GetFullPath($blockedReplace.resolvedRoot) -ne [IO.Path]::GetFullPath($dataRootA)) {
        throw 'An exclusive locator replacement was not blocked by the live reader.'
    }
    $null = & $releaseHolder $resolvedHolder $dataRootA

    $replaceResultPath = Join-Path $leaseProofRoot 'replace.json'
    $replaced = & $invokeWriter 'replace' $replaceResultPath $dataRootB
    if ($replaced.ok -ne $true -or
        $replaced.acquired -ne $true -or
        $replaced.locatorChanged -ne $true -or
        [IO.Path]::GetFullPath($replaced.resolvedRoot) -ne [IO.Path]::GetFullPath($dataRootB)) {
        throw 'Exclusive locator replacement did not publish the expected synthetic root.'
    }

    $finalHolder = & $startHolder 'resolved-b'
    $null = & $releaseHolder $finalHolder $dataRootB

    Write-Host "Shared root locator PASS: $($result.caseCount)/$($result.caseCount) reader cases; $($activationResults.Count)/$($activationResults.Count) WPF activation cases; redirected lease rejection; two-process missing/create/replace lease proof; fixture bytes unchanged."
}
finally {
    foreach ($holder in $holderProcesses) {
        try {
            if (-not $holder.HasExited) {
                $holder.Kill($true)
                $holder.WaitForExit(5000) | Out-Null
            }
            $holder.Dispose()
        }
        catch {
        }
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside TEMP.'
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
