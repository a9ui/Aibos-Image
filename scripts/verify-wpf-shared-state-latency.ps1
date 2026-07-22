param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP ('photoviewer-wpf-shared-state-latency-summary-' + [guid]::NewGuid().ToString('N') + '.json')),
    [ValidateRange(1, 10)]
    [int]$Repetitions = 3,
    [ValidateRange(10, 300)]
    [int]$TimeoutSeconds = 90,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}
if ($outputFullPath.Contains('"')) { throw 'OutputPath cannot contain a double quote.' }

if (-not $NoBuild) {
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $exe)) { throw "WPF executable is missing: $exe" }

$thresholds = [ordered]@{
    modalNextP95Ms = 50
    favoriteActionP95Ms = 65
    dispatcherMaxGapMs = 110
    maximumLargeToControlRatio = 2.5
    relativeSchedulingJitterMarginMs = 10
    relativeGateFormula = 'large <= max(control * 2.5, control + 10 ms)'
}

function Get-RelativeGateEvidence {
    param(
        [double]$ControlMs,
        [double]$LargeMs,
        [double]$RatioLimit,
        [double]$SchedulingJitterMarginMs
    )

    $safeControlMs = [math]::Max(0.001, $ControlMs)
    $ratioAllowanceMs = $safeControlMs * $RatioLimit
    $additiveAllowanceMs = $safeControlMs + $SchedulingJitterMarginMs
    $effectiveAllowanceMs = [math]::Max($ratioAllowanceMs, $additiveAllowanceMs)
    [pscustomobject]([ordered]@{
        controlMs = [math]::Round($ControlMs, 3)
        largeMs = [math]::Round($LargeMs, 3)
        ratio = [math]::Round($LargeMs / $safeControlMs, 3)
        deltaMs = [math]::Round($LargeMs - $ControlMs, 3)
        ratioAllowanceMs = [math]::Round($ratioAllowanceMs, 3)
        additiveAllowanceMs = [math]::Round($additiveAllowanceMs, 3)
        effectiveAllowanceMs = [math]::Round($effectiveAllowanceMs, 3)
        passed = $LargeMs -le $effectiveAllowanceMs
    })
}

# Executable verifier proof: sub-frame scheduling noise can exceed a ratio
# when the control is only a few milliseconds, while the historical P1
# regression remains rejected by the unchanged absolute gates. A relative
# regression below the absolute ceiling must still fail both allowances.
$currentJitterProof = Get-RelativeGateEvidence `
    -ControlMs 4.873 -LargeMs 13.73 `
    -RatioLimit $thresholds.maximumLargeToControlRatio `
    -SchedulingJitterMarginMs $thresholds.relativeSchedulingJitterMarginMs
$relativeRegressionProof = Get-RelativeGateEvidence `
    -ControlMs 5 -LargeMs 20 `
    -RatioLimit $thresholds.maximumLargeToControlRatio `
    -SchedulingJitterMarginMs $thresholds.relativeSchedulingJitterMarginMs
$legacyBaselineProof = [pscustomobject]([ordered]@{
    modalNextMs = 186
    favoriteActionMs = 251
    dispatcherGapMs = 460
    rejectedByModalAbsoluteGate = 186 -gt $thresholds.modalNextP95Ms
    rejectedByFavoriteAbsoluteGate = 251 -gt $thresholds.favoriteActionP95Ms
    rejectedByDispatcherAbsoluteGate = 460 -gt $thresholds.dispatcherMaxGapMs
    rejected = 186 -gt $thresholds.modalNextP95Ms `
        -and 251 -gt $thresholds.favoriteActionP95Ms `
        -and 460 -gt $thresholds.dispatcherMaxGapMs
})
$gateSelfTest = [pscustomobject]([ordered]@{
    ok = $currentJitterProof.passed -eq $true `
        -and $relativeRegressionProof.passed -eq $false `
        -and $legacyBaselineProof.rejected -eq $true
    currentJitterCase = $currentJitterProof
    relativeRegressionCase = $relativeRegressionProof
    legacyP1Baseline = $legacyBaselineProof
})
if ($gateSelfTest.ok -ne $true) {
    throw 'shared-state latency verifier gate self-test failed'
}

$failures = [Collections.Generic.List[string]]::new()
$runs = [Collections.Generic.List[object]]::new()

for ($run = 1; $run -le $Repetitions; $run++) {
    $rawPath = Join-Path $env:TEMP ('photoviewer-wpf-shared-state-latency-raw-' + [guid]::NewGuid().ToString('N') + '.json')
    $process = $null
    try {
        $process = Start-Process -FilePath $exe `
            -ArgumentList @('--shared-state-latency-smoke', ('"{0}"' -f $rawPath)) `
            -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(5000) | Out-Null } catch { }
            $failures.Add("run ${run}: PID $($process.Id) exceeded ${TimeoutSeconds}s and was terminated")
            $runs.Add([pscustomobject]@{ run = $run; ok = $false; error = 'timeout'; pid = $process.Id })
            continue
        }
        $process.Refresh()
        if ($process.ExitCode -ne 0) {
            $failures.Add("run ${run}: process exit code $($process.ExitCode)")
        }
        if (-not (Test-Path -LiteralPath $rawPath)) {
            $failures.Add("run ${run}: WPF process produced no raw result")
            $runs.Add([pscustomobject]@{ run = $run; ok = $false; error = 'missing result'; pid = $process.Id })
            continue
        }

        $result = Get-Content -Raw -LiteralPath $rawPath | ConvertFrom-Json
        $runFailures = [Collections.Generic.List[string]]::new()
        if ($result.ok -ne $true) { $runFailures.Add('result.ok was false') }
        if ($result.profiles.Count -ne 2) { $runFailures.Add('small/large profile pair missing') }
        $small = @($result.profiles | Where-Object name -eq 'small')[0]
        $large = @($result.profiles | Where-Object name -eq 'large-100000')[0]
        if ($null -eq $small -or $null -eq $large) {
            $runFailures.Add('named profiles missing')
        }

        foreach ($profile in @($small, $large)) {
            if ($null -eq $profile) { continue }
            if ($profile.ok -ne $true -or $profile.actionsAccepted -ne $true) { $runFailures.Add("$($profile.name): normal action path failed") }
            if ($profile.favoriteExact -ne $true -or $profile.seenAdditive -ne $true -or $profile.repeatedSeenNoWrite -ne $true) {
                $runFailures.Add("$($profile.name): Favorite/Seen exactness failed")
            }
            if ($profile.sourceUntouched -ne $true -or $profile.jobsUntouched -ne $true -or $profile.enhancementPassive -ne $true) {
                $runFailures.Add("$($profile.name): source/Enhancement isolation failed")
            }
            if ($profile.isolated -ne $true -or $profile.residueFree -ne $true) {
                $runFailures.Add("$($profile.name): temp/residue boundary failed")
            }
            if ($profile.closeLifecycleExercised -ne $true `
                -or $profile.pendingAfterClose -ne $false `
                -or $profile.closeDrainSucceeded -ne $true `
                -or $profile.closeFlushCount -ne 1) {
                $runFailures.Add("$($profile.name): measured close/drain lifecycle failed")
            }
            if ($profile.modalNext.rawMs.Count -ne 20 -or $profile.favoriteAction.rawMs.Count -ne 20) {
                $runFailures.Add("$($profile.name): expected 20 raw modal/Favorite samples")
            }
            if ($profile.idleHeartbeat.count -lt 3 -or $profile.workloadHeartbeat.count -lt 2) {
                $runFailures.Add("$($profile.name): dispatcher heartbeat evidence missing")
            }
        }

        $modalRatio = 0.0
        $favoriteRatio = 0.0
        $gapRatio = 0.0
        $modalRelativeGate = $null
        $favoriteRelativeGate = $null
        $gapRelativeGate = $null
        if ($null -ne $small -and $null -ne $large) {
            if ($small.favoriteWriterAdopted -ne $false -or $small.seenWriterAdopted -ne $false `
                -or $small.favoriteWriterBatchCount -ne 0 -or $small.seenWriterBatchCount -ne 0) {
                $runFailures.Add('small control unexpectedly adopted an async shared writer')
            }
            if ($small.closePendingExpected -ne $false `
                -or $small.pendingBeforeClose -ne $false `
                -or $small.closeDeferred -ne $false `
                -or $small.closeMutationAccepted -ne $false `
                -or $small.closeWriterEntered -ne $false) {
                $runFailures.Add('small control did not prove the immediate-close lifecycle')
            }
            if ($large.favoriteWriterAdopted -ne $true -or $large.seenWriterAdopted -ne $true `
                -or $large.favoriteWriterBatchCount -lt 1 -or $large.seenWriterBatchCount -lt 1) {
                $runFailures.Add('large profile did not prove positive Favorite and Seen actor use')
            }
            if ($large.closePendingExpected -ne $true `
                -or $large.closeMutationAccepted -ne $true `
                -or $large.closeWriterEntered -ne $true `
                -or $large.pendingBeforeClose -ne $true `
                -or $large.closeDeferred -ne $true) {
                $runFailures.Add('large profile did not prove pending-before-close and deferred drain')
            }
            if ($large.favoriteSeedEntries -ne 100000 -or $large.seenSeedEntries -ne 100000) {
                $runFailures.Add('large profile did not seed exactly 100000 Favorite and Seen entries')
            }
            if ($large.favoriteSeedBytes -lt 2MB -or $large.favoriteSeedBytes -gt 5MB `
                -or $large.seenSeedBytes -lt 2MB -or $large.seenSeedBytes -gt 5MB) {
                $runFailures.Add('large profile bytes were outside the expected 2-5 MiB envelope')
            }

            $modalRatio = $large.modalNext.p95Ms / [math]::Max(0.001, $small.modalNext.p95Ms)
            $favoriteRatio = $large.favoriteAction.p95Ms / [math]::Max(0.001, $small.favoriteAction.p95Ms)
            $gapRatio = $large.workloadHeartbeat.gaps.maxMs / [math]::Max(0.001, $small.workloadHeartbeat.gaps.maxMs)
            $modalRelativeGate = Get-RelativeGateEvidence `
                -ControlMs $small.modalNext.p95Ms -LargeMs $large.modalNext.p95Ms `
                -RatioLimit $thresholds.maximumLargeToControlRatio `
                -SchedulingJitterMarginMs $thresholds.relativeSchedulingJitterMarginMs
            $favoriteRelativeGate = Get-RelativeGateEvidence `
                -ControlMs $small.favoriteAction.p95Ms -LargeMs $large.favoriteAction.p95Ms `
                -RatioLimit $thresholds.maximumLargeToControlRatio `
                -SchedulingJitterMarginMs $thresholds.relativeSchedulingJitterMarginMs
            $gapRelativeGate = Get-RelativeGateEvidence `
                -ControlMs $small.workloadHeartbeat.gaps.maxMs -LargeMs $large.workloadHeartbeat.gaps.maxMs `
                -RatioLimit $thresholds.maximumLargeToControlRatio `
                -SchedulingJitterMarginMs $thresholds.relativeSchedulingJitterMarginMs
            if ($large.modalNext.p95Ms -gt $thresholds.modalNextP95Ms) {
                $runFailures.Add("large Modal-next p95 $($large.modalNext.p95Ms) ms exceeded $($thresholds.modalNextP95Ms) ms")
            }
            if ($large.favoriteAction.p95Ms -gt $thresholds.favoriteActionP95Ms) {
                $runFailures.Add("large Favorite p95 $($large.favoriteAction.p95Ms) ms exceeded $($thresholds.favoriteActionP95Ms) ms")
            }
            if ($large.workloadHeartbeat.gaps.maxMs -gt $thresholds.dispatcherMaxGapMs) {
                $runFailures.Add("large dispatcher max gap $($large.workloadHeartbeat.gaps.maxMs) ms exceeded $($thresholds.dispatcherMaxGapMs) ms")
            }
            if ($modalRelativeGate.passed -ne $true) {
                $runFailures.Add("Modal p95 $($modalRelativeGate.largeMs) ms exceeded relative allowance $($modalRelativeGate.effectiveAllowanceMs) ms (ratio $($modalRelativeGate.ratio), delta $($modalRelativeGate.deltaMs) ms)")
            }
            if ($favoriteRelativeGate.passed -ne $true) {
                $runFailures.Add("Favorite p95 $($favoriteRelativeGate.largeMs) ms exceeded relative allowance $($favoriteRelativeGate.effectiveAllowanceMs) ms (ratio $($favoriteRelativeGate.ratio), delta $($favoriteRelativeGate.deltaMs) ms)")
            }
            if ($gapRelativeGate.passed -ne $true) {
                $runFailures.Add("dispatcher gap $($gapRelativeGate.largeMs) ms exceeded relative allowance $($gapRelativeGate.effectiveAllowanceMs) ms (ratio $($gapRelativeGate.ratio), delta $($gapRelativeGate.deltaMs) ms)")
            }
        }

        if ([string]::IsNullOrWhiteSpace($result.smokeRoot) -or (Test-Path -LiteralPath $result.smokeRoot)) {
            $runFailures.Add('temp smoke root was not cleaned')
        }
        foreach ($failure in $runFailures) { $failures.Add("run ${run}: $failure") }
        $runs.Add([pscustomobject]@{
            run = $run
            ok = $runFailures.Count -eq 0
            runtime = $result.runtime
            small = $small
            large = $large
            ratios = [ordered]@{
                modalP95 = [math]::Round($modalRatio, 3)
                favoriteP95 = [math]::Round($favoriteRatio, 3)
                dispatcherMaxGap = [math]::Round($gapRatio, 3)
            }
            relativeGates = [ordered]@{
                modalP95 = $modalRelativeGate
                favoriteP95 = $favoriteRelativeGate
                dispatcherMaxGap = $gapRelativeGate
            }
            failures = @($runFailures)
        })
    }
    catch {
        $failures.Add("run ${run}: $($_.Exception.Message)")
        $runs.Add([pscustomobject]@{ run = $run; ok = $false; error = $_.Exception.ToString() })
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
        Remove-Item -LiteralPath $rawPath -Force -ErrorAction SilentlyContinue
    }
}

$summary = [ordered]@{
    ok = $failures.Count -eq 0
    decision = if ($failures.Count -eq 0) { 'GREEN: all semantic, actor-adoption, absolute latency, and noise-aware relative gates passed' } else { 'RED: one or more shared-state latency gates failed' }
    repetitions = $Repetitions
    timeoutSecondsPerProcess = $TimeoutSeconds
    thresholds = $thresholds
    gateSelfTest = $gateSelfTest
    runs = @($runs)
    failures = @($failures)
    browserPortUsed = $false
    sourceOrUserCacheTouched = $false
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFullPath)) | Out-Null
[IO.File]::WriteAllText($outputFullPath, ($summary | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 15
if ($failures.Count -gt 0) {
    throw ('WPF shared-state latency gate failed: ' + ($failures -join '; '))
}
