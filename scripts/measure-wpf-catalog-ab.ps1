param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineExecutablePath,
    [Parameter(Mandatory = $true)]
    [string]$CandidateExecutablePath,
    [int]$Count = 10000,
    [ValidateRange(2, 50)]
    [int]$RunsPerVariant = 10,
    [string]$BaselineSha = "",
    [string]$BaselineTree = "",
    [string]$CandidateLabel = "working-tree",
    [string]$OutputRoot = (Join-Path $env:TEMP ("aibos-wpf-catalog-ab-" + [guid]::NewGuid().ToString('N'))),
    [double]$MinimumP95ImprovementPercent = 15,
    [double]$MaximumLatencyRegressionPercent = 5,
    [double]$MaximumResourceRegressionPercent = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Count -lt 512) {
    throw 'Count must be at least 512 so the candidate exercises parallel catalog preparation.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$verifier = Join-Path $PSScriptRoot 'verify-wpf-catalog-stress.ps1'
$baselineExe = [IO.Path]::GetFullPath($BaselineExecutablePath)
$candidateExe = [IO.Path]::GetFullPath($CandidateExecutablePath)
if (-not (Test-Path -LiteralPath $baselineExe -PathType Leaf)) {
    throw "Baseline executable was not found: $baselineExe"
}
if (-not (Test-Path -LiteralPath $candidateExe -PathType Leaf)) {
    throw "Candidate executable was not found: $candidateExe"
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempRootPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
$outputLeaf = [IO.Path]::GetFileName($outputRootFull)
if (-not $outputRootFull.StartsWith($tempRootPrefix, [StringComparison]::OrdinalIgnoreCase) `
    -or $outputLeaf -notmatch '^aibos-wpf-catalog-ab-[0-9a-z-]+$') {
    throw "OutputRoot must be a dedicated aibos-wpf-catalog-ab-* directory under TEMP: $outputRootFull"
}
if (Test-Path -LiteralPath $outputRootFull) {
    throw "OutputRoot already exists; choose a fresh exact directory: $outputRootFull"
}
[IO.Directory]::CreateDirectory($outputRootFull) | Out-Null

function Get-PercentileInc {
    param(
        [double[]]$Values,
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    if ($Values.Count -lt 1) {
        throw 'Cannot calculate a percentile from an empty sample.'
    }
    [Array]::Sort($Values)
    if ($Values.Count -eq 1) {
        return $Values[0]
    }

    $rank = ($Values.Count - 1) * $Percentile
    $lower = [int][Math]::Floor($rank)
    $upper = [int][Math]::Ceiling($rank)
    if ($lower -eq $upper) {
        return $Values[$lower]
    }
    return $Values[$lower] + (($rank - $lower) * ($Values[$upper] - $Values[$lower]))
}

function Get-ChangePercent {
    param(
        [double]$Baseline,
        [double]$Candidate
    )

    if ($Baseline -eq 0) {
        return $(if ($Candidate -eq 0) { 0.0 } else { [double]::PositiveInfinity })
    }
    return (($Candidate - $Baseline) / $Baseline) * 100
}

function Add-SummaryMetric {
    param(
        [System.Collections.IDictionary]$Target,
        [Collections.Generic.List[object]]$Rows,
        [string]$Name
    )

    [double[]]$baselineValues = @(
        $Rows |
            Where-Object Variant -eq 'baseline' |
            ForEach-Object { [double]$_.$Name }
    )
    [double[]]$candidateValues = @(
        $Rows |
            Where-Object Variant -eq 'candidate' |
            ForEach-Object { [double]$_.$Name }
    )
    $baselineMedian = Get-PercentileInc $baselineValues 0.5
    $candidateMedian = Get-PercentileInc $candidateValues 0.5
    $baselineP95 = Get-PercentileInc $baselineValues 0.95
    $candidateP95 = Get-PercentileInc $candidateValues 0.95
    $Target[$Name] = [ordered]@{
        baselineMedian = [Math]::Round($baselineMedian, 2)
        candidateMedian = [Math]::Round($candidateMedian, 2)
        medianChangePercent = [Math]::Round((Get-ChangePercent $baselineMedian $candidateMedian), 2)
        baselineP95 = [Math]::Round($baselineP95, 2)
        candidateP95 = [Math]::Round($candidateP95, 2)
        p95ChangePercent = [Math]::Round((Get-ChangePercent $baselineP95 $candidateP95), 2)
    }
}

$rows = [Collections.Generic.List[object]]::new()
$runFailures = [Collections.Generic.List[string]]::new()
try {
    for ($round = 1; $round -le $RunsPerVariant; $round++) {
        $roundOrder = if (($round % 2) -eq 1) {
            @('baseline', 'candidate')
        }
        else {
            @('candidate', 'baseline')
        }
        foreach ($variant in $roundOrder) {
            $exe = if ($variant -eq 'baseline') { $baselineExe } else { $candidateExe }
            $outputPath = Join-Path $outputRootFull ("{0:D2}-{1}.json" -f $round, $variant)
            $arguments = @{
                ExecutablePath = $exe
                SkipBuild = $true
                Count = $Count
                FolderCount = 1
                ViewportChurnSeconds = 0
                OutputPath = $outputPath
                OverallTimeoutSeconds = 120
                RequireProcessResourceCounters = $true
            }
            if ($variant -eq 'candidate') {
                $arguments.RequireParallelCatalogPreparation = $true
            }

            $wallWatch = [Diagnostics.Stopwatch]::StartNew()
            & $verifier @arguments | Out-Null
            $wallWatch.Stop()
            $data = Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json
            if ($data.ok -ne $true) {
                throw "$variant round $round returned ok=false"
            }

            $rows.Add([pscustomobject]@{
                round = $round
                variant = $variant
                catalogReadyMs = [double]$data.catalogReadyElapsedMs
                firstUsableMs = [double]$data.firstUsableViewportElapsedMs
                thumbnailMs = [double]$data.thumbnailElapsedMs
                totalMs = [double]$data.loadMetricsTotalElapsedMs
                scanMs = [double]$data.scanElapsedMs
                materializeMs = [double]$data.materializeElapsedMs
                metadataMs = [double]$data.metadataElapsedMs
                dispatcherGapMs = [double]$data.dispatcherHeartbeatMaxGapMs
                workingSetAfterBytes = [double]$data.workingSetAfterBytes
                workingSetDeltaBytes = [double]$data.workingSetAfterBytes - [double]$data.workingSetBeforeBytes
                peakWorkingSetBytes = [double]$data.processPeakWorkingSetBytes
                readBytes = [double]$data.processReadBytes
                writeBytes = [double]$data.processWriteBytes
                readOperationCount = [double]$data.processReadOperationCount
                writeOperationCount = [double]$data.processWriteOperationCount
                sourceCountAfter = [int]$data.sourceCountAfter
                enhancementJobsRead = [int]$data.enhancementJobsRead
                parallelized = $(if ($variant -eq 'candidate') { [bool]$data.catalogPreparationParallelized } else { $false })
                workers = $(if ($variant -eq 'candidate') { [int]$data.catalogPreparationWorkers } else { 0 })
                wallMs = $wallWatch.ElapsedMilliseconds
            })
            Write-Output (
                "round={0} variant={1} catalogReady={2} firstUsable={3} thumbnail={4} total={5} read={6} write={7} peakWs={8}" -f
                    $round,
                    $variant,
                    $data.catalogReadyElapsedMs,
                    $data.firstUsableViewportElapsedMs,
                    $data.thumbnailElapsedMs,
                    $data.loadMetricsTotalElapsedMs,
                    $data.processReadBytes,
                    $data.processWriteBytes,
                    $data.processPeakWorkingSetBytes)
        }
    }
}
catch {
    $runFailures.Add($_.Exception.GetType().Name + ': ' + $_.Exception.Message)
}

$metricNames = @(
    'catalogReadyMs',
    'firstUsableMs',
    'thumbnailMs',
    'totalMs',
    'scanMs',
    'materializeMs',
    'metadataMs',
    'dispatcherGapMs',
    'workingSetAfterBytes',
    'workingSetDeltaBytes',
    'peakWorkingSetBytes',
    'readBytes',
    'writeBytes',
    'readOperationCount',
    'writeOperationCount'
)
$summaryMetrics = [ordered]@{}
if ($rows.Count -eq ($RunsPerVariant * 2)) {
    foreach ($metricName in $metricNames) {
        Add-SummaryMetric $summaryMetrics $rows $metricName
    }
}

$gateFailures = [Collections.Generic.List[string]]::new()
foreach ($runFailure in $runFailures) {
    $gateFailures.Add($runFailure)
}
if ($rows.Count -ne ($RunsPerVariant * 2)) {
    $gateFailures.Add("completed $($rows.Count) of $($RunsPerVariant * 2) required runs")
}
else {
    foreach ($metricName in @('catalogReadyMs', 'firstUsableMs')) {
        $change = [double]$summaryMetrics[$metricName].p95ChangePercent
        if ($change -gt -$MinimumP95ImprovementPercent) {
            $gateFailures.Add("$metricName p95 changed $change%; required at most -$MinimumP95ImprovementPercent%")
        }
    }
    # firstUsableMs is the end-to-end completion timestamp for both the
    # prefetched and visible thumbnail loads. thumbnailMs is their internal
    # component duration and remains diagnostic; gating it again would double
    # count the same work while ignoring the much earlier catalog start.
    foreach ($metricName in @('totalMs', 'scanMs', 'materializeMs', 'metadataMs', 'dispatcherGapMs')) {
        $change = [double]$summaryMetrics[$metricName].p95ChangePercent
        if ($change -gt $MaximumLatencyRegressionPercent) {
            $gateFailures.Add("$metricName p95 regressed $change%; limit $MaximumLatencyRegressionPercent%")
        }
    }
    foreach ($metricName in @('workingSetAfterBytes', 'workingSetDeltaBytes', 'peakWorkingSetBytes', 'readBytes', 'writeBytes')) {
        $change = [double]$summaryMetrics[$metricName].p95ChangePercent
        if ($change -gt $MaximumResourceRegressionPercent) {
            $gateFailures.Add("$metricName p95 regressed $change%; limit $MaximumResourceRegressionPercent%")
        }
    }
}

$summary = [ordered]@{
    ok = $gateFailures.Count -eq 0
    baselineSha = $BaselineSha
    baselineTree = $BaselineTree
    baselineExecutable = $baselineExe
    baselineExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $baselineExe).Hash.ToLowerInvariant()
    candidateLabel = $CandidateLabel
    candidateExecutable = $candidateExe
    candidateExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateExe).Hash.ToLowerInvariant()
    count = $Count
    runsPerVariant = $RunsPerVariant
    order = 'odd rounds baseline,candidate; even rounds candidate,baseline'
    percentileMethod = 'PERCENTILE.INC'
    minimumP95ImprovementPercent = $MinimumP95ImprovementPercent
    maximumLatencyRegressionPercent = $MaximumLatencyRegressionPercent
    maximumResourceRegressionPercent = $MaximumResourceRegressionPercent
    metrics = $summaryMetrics
    failures = @($gateFailures)
    rows = @($rows)
}
$summaryPath = Join-Path $outputRootFull 'summary.json'
[IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

foreach ($metricName in $summaryMetrics.Keys) {
    $metric = $summaryMetrics[$metricName]
    Write-Output (
        "summary metric={0} median={1}/{2} ({3}%) p95={4}/{5} ({6}%)" -f
            $metricName,
            $metric.baselineMedian,
            $metric.candidateMedian,
            $metric.medianChangePercent,
            $metric.baselineP95,
            $metric.candidateP95,
            $metric.p95ChangePercent)
}
Write-Output "summaryPath=$summaryPath"
if ($gateFailures.Count -gt 0) {
    throw ("WPF catalog A/B gate failed: " + ($gateFailures -join '; '))
}
