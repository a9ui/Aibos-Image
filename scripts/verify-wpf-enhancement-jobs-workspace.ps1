param(
    [string]$Configuration = "Release",
    [string]$OutputPath = (Join-Path $env:TEMP "aibos-wpf-enhancement-jobs-workspace.json"),
    [string]$DotnetPath = "dotnet",
    [string]$TargetFrameworkOverride = "",
    [ValidateRange(1, 300)]
    [int]$OverallTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj"
$queueContractPath = Join-Path $repoRoot "contracts\enhancement-queue-order-v1.json"
$healthContractPath = Join-Path $repoRoot "contracts\enhancement-health-v1.json"
$videoContractPath = Join-Path $repoRoot "contracts\enhancement-video-v1.json"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-enhancement-jobs-verifier-' + [guid]::NewGuid().ToString('N'))))
$buildRoot = Join-Path $runRoot 'build'
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$process = $null

if (-not $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verifier root must stay under TEMP."
}

try {
    if (-not (Test-Path -LiteralPath $queueContractPath -PathType Leaf)) {
        throw "Enhancement queue ordering contract was not found: $queueContractPath"
    }
    $queueContract = Get-Content -LiteralPath $queueContractPath -Raw | ConvertFrom-Json
    $queueContractChecks = @(
        ($queueContract.schemaVersion -eq 1)
        ($queueContract.contractId -eq "PV-ENHANCE-QUEUE-001")
        ($queueContract.protocol -eq "aibos.enhancement-queue-order/v1")
        ($queueContract.queueOrder.field -eq "queueOrder")
        ($queueContract.queueOrder.type -eq "non-negative integer")
        ($queueContract.workerRules.singleWorker -eq $true)
        ($queueContract.workerRules.runningJobIsNeverPreemptedByReorder -eq $true)
        ($queueContract.workerRules.pumpAfterRunningCancel -eq $true)
        ($queueContract.workerRules.pumpAfterStartupRecovery -eq $true)
        (($queueContract.readerFixture.expectedVisibleOrder -join ",") -eq
            "running,ordered-first,ordered-second,legacy-earlier,invalid-negative,legacy-later")
        ($queueContract.readerFixture.expectedClaimedQueuedJob -eq "ordered-first")
    )
    if ($queueContractChecks -contains $false) {
        throw "Enhancement queue ordering contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $healthContractPath -PathType Leaf)) {
        throw "Enhancement health contract was not found: $healthContractPath"
    }
    $healthContract = Get-Content -LiteralPath $healthContractPath -Raw | ConvertFrom-Json
    $healthContractChecks = @(
        ($healthContract.schemaVersion -eq 1)
        ($healthContract.contractId -eq "PV-ENHANCE-HEALTH-001")
        ($healthContract.protocol -eq "aibos.enhancement-health/v1")
        ($healthContract.route.method -eq "GET")
        ($healthContract.route.path -eq "/api/enhance/health")
        ($healthContract.route.cacheControl -eq "no-store")
        ($healthContract.route.loopbackOnly -eq $true)
        ($healthContract.passiveRead.createsJobs -eq $false)
        ($healthContract.passiveRead.startsWorker -eq $false)
        ($healthContract.passiveRead.wakesQueue -eq $false)
        ($healthContract.passiveRead.claimsJobs -eq $false)
        ($healthContract.passiveRead.retriesJobs -eq $false)
        ($healthContract.passiveRead.pollsComfyUi -eq $false)
        (($healthContract.status -join ",") -eq "healthy,working,needs-attention")
        ($healthContract.workingFixture.expectedDisplay.state -eq "Working")
        ($healthContract.workingFixture.expectedDisplay.detail -eq "1 running / 4 queued")
        ($healthContract.workingFixture.expectedDisplay.sourceRevisionPrefix -eq "69684954")
    )
    if ($healthContractChecks -contains $false) {
        throw "Enhancement health contract fields are invalid."
    }
    if (-not (Test-Path -LiteralPath $videoContractPath -PathType Leaf)) {
        throw "Enhancement video contract was not found: $videoContractPath"
    }
    $videoContract = Get-Content -LiteralPath $videoContractPath -Raw | ConvertFrom-Json
    $videoContractChecks = @(
        ($videoContract.schemaVersion -eq 1)
        ($videoContract.contractId -eq "PV-ENHANCE-VIDEO-001")
        ($videoContract.protocol -eq "aibos.enhancement-video/v1")
        ($videoContract.jobStoreVersion -eq 1)
        (($videoContract.operationEnvelope.acceptedValues -join ",") -eq
            "upscale,photoreal,video")
        ($videoContract.operationEnvelope.legacyMissingOperation -eq "upscale")
        ($videoContract.operationEnvelope.storeVersionChange -eq $false)
        ($videoContract.normalV1.presetId -eq "wan22-ti2v-5b-normal-v1")
        ($videoContract.normalV1.backendId -eq "wan22-ti2v-5b-core-v1")
        ($videoContract.normalV1.nominalDurationSeconds -eq 6)
        ($videoContract.normalV1.playbackFps -eq 16)
        ($videoContract.normalV1.frameCount -eq 97)
        ($videoContract.normalV1.maximumPixelArea -eq 409600)
        ($videoContract.normalV1.alignment -eq 32)
        ($videoContract.normalV1.steps -eq 20)
        ($videoContract.normalV1.cfg -eq 5)
        ($videoContract.normalV1.sampler -eq "uni_pc")
        ($videoContract.normalV1.scheduler -eq "simple")
        ($videoContract.normalV1.shift -eq 8)
        ($videoContract.normalV1.seedRange.maximum -eq 2147483647)
        ($videoContract.normalV1.seedRange.fixedAtEnqueue -eq $true)
        ($videoContract.managedOutput.folder -eq "Videos")
        ($videoContract.managedOutput.flat -eq $true)
        ($videoContract.readerFirst.wpfWriterEnabled -eq $false)
        ($videoContract.readerFirst.h25WriterEnabled -eq $false)
        ($videoContract.readerFixture.expectedOperations.'legacy-missing-operation' -eq "upscale")
        ($videoContract.readerFixture.expectedOperations.'explicit-video' -eq "video")
        ($videoContract.readerFixture.expectedOperations.'future-operation' -eq "unsupported")
        ($videoContract.readerFixture.expectedOperations.'null-operation' -eq "unsupported")
        (($videoContract.readerFixture.expectedImageVersionIds -join ",") -eq
            "legacy-missing-operation,explicit-upscale,explicit-photoreal")
        (($videoContract.readerFixture.expectedReaderOnlyIds -join ",") -eq
            "explicit-video,future-operation,null-operation")
        ($videoContract.readerFixture.expectedMutationRequests -eq 0)
    )
    if ($videoContractChecks -contains $false) {
        throw "Enhancement video contract fields are invalid."
    }

    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $buildOutput = $buildRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ([string]::IsNullOrWhiteSpace($TargetFrameworkOverride)) {
        & $DotnetPath build $project -c $Configuration "-p:OutputPath=$buildOutput" --nologo -v:minimal
    }
    else {
        & $DotnetPath msbuild $project -restore "-property:TargetFramework=$TargetFrameworkOverride" "-property:OutputPath=$buildOutput" "-property:Configuration=$Configuration" -nologo -verbosity:minimal
    }
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $dll = Join-Path $buildRoot 'PhotoViewer.Wpf.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "WPF assembly was not found: $dll"
    }
    if (Test-Path -LiteralPath $fullOutputPath) {
        Remove-Item -LiteralPath $fullOutputPath -Force
    }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--enhancement-jobs-workspace-smoke', ('"{0}"' -f $fullOutputPath)) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Enhancement jobs workspace smoke timed out after $OverallTimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
        throw "Enhancement jobs workspace smoke produced no result. Process exit code: $($process.ExitCode)"
    }

    $processExitCode = $process.ExitCode
    $process.WaitForExit()
    $process.Dispose()
    $process = $null
    $result = Get-Content -Raw -LiteralPath $fullOutputPath | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 10
    $required = @(
        'passiveOpen',
        'healthVisible',
        'healthProvenance',
        'healthPassive',
        'legacyHealthFallback',
        'futureHealthFallback',
        'unknownIssueSafe',
        'healthRecovered',
        'routesOk',
        'outputOpened',
        'sourceOpenedInViewer',
        'queueInventoryOrdered',
        'operationLabelsVisible',
        'videoReaderSafe',
        'unknownOperationSafe',
        'legacyMissingOperation',
        'readerOnlyNoMutation',
        'imageVersionsExcludeVideo',
        'stableJobViews',
        'failedCancelIssued',
        'moveNextIssued',
        'canceledRetryIssued',
        'rerunSettingsContract',
        'clearQueuedIssued',
        'jobsRestoredAfterViewerClose',
        'sourceUnchanged',
        'storesUnchanged',
        'outputDeleted'
    )
    $missing = @($required | Where-Object { $result.$_ -ne $true })
    if ($processExitCode -ne 0 -or $result.ok -ne $true -or $missing.Count -gt 0) {
        throw "Enhancement jobs workspace contract failed (exit $processExitCode): $($missing -join ', ')"
    }
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
        for ($cleanupAttempt = 1; $cleanupAttempt -le 10; $cleanupAttempt++) {
            try {
                Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
                break
            }
            catch {
                if ($cleanupAttempt -eq 10) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
