param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\ContractBundles.ps1')
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$implementationPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoPromptRewrite.cs'
$jaResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-video-h3-prompt-' + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-video-h3-prompt-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$buildRoot = Join-Path $runRoot 'build'
$storesRoot = Join-Path $runRoot 'stores'
$contractFixturePath = Join-Path $runRoot 'enhancement-video-v2.json'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$previousEnvironment = @{}
$environmentPaths = [ordered]@{
    PHOTOVIEWER_WPF_STATE_PATH = (Join-Path $storesRoot 'state.json')
    PHOTOVIEWER_WPF_FAVORITES_PATH = (Join-Path $storesRoot 'favorites.json')
    PHOTOVIEWER_WPF_SEEN_PATH = (Join-Path $storesRoot 'seen.json')
    PHOTOVIEWER_WPF_RECENT_PATH = (Join-Path $storesRoot 'recent-folders.json')
    PHOTOVIEWER_WPF_ALBUMS_PATH = (Join-Path $storesRoot 'albums.json')
    PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH = (Join-Path $storesRoot 'search-history.json')
    PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH = (Join-Path $storesRoot 'enhance\jobs.json')
    AIBOS_SHARED_ROOT_LOCATOR_PATH = (Join-Path $storesRoot 'shared-root.v1.json')
    AIBOS_VIDEO_V2_CONTRACT_PATH = $contractFixturePath
}

try {
    New-Item -ItemType Directory -Path $runRoot, $storesRoot -Force | Out-Null
    $contract = Get-AibosVideoV2Bundle $repoRoot
    Write-AibosJsonFile $contractFixturePath $contract
    $rewriteContract = $contract.promptRewriteProtocol
    if ($rewriteContract.schemaVersion -ne 1 `
        -or $rewriteContract.contractId -ne 'PV-ENHANCE-VIDEO-H3-PROMPT-REWRITE-001' `
        -or $rewriteContract.protocol -ne 'aibos.enhancement-video-h3-prompt-rewrite/v1' `
        -or $rewriteContract.route.method -ne 'POST' `
        -or $rewriteContract.route.path -ne '/api/enhance/video-prompts/h3/rewrite' `
        -or $rewriteContract.rewriteRevision -ne 'aibos-h3-i2va-local-v1' `
        -or $rewriteContract.responseFixture.rewriteRevision -ne $rewriteContract.rewriteRevision `
        -or @($rewriteContract.revisionFixtures).Count -lt 2 `
        -or @($rewriteContract.errorFixtures).Count -lt 1) {
        throw 'The canonical H3 prompt-rewrite fixture is missing or inconsistent.'
    }

    $xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
    if ($xaml -notmatch 'x:Name="ModalVideoH3PromptRewritePanel"[\s\S]{0,300}Background="\{StaticResource SoftFill\}"' `
        -or $xaml -notmatch 'x:Name="ModalVideoH3RewriteModeComboBox"[\s\S]{0,900}Tag="polish"[\s\S]{0,300}Tag="direction"[\s\S]{0,300}Tag="auto"' `
        -or $xaml -notmatch 'x:Name="ModalVideoH3PromptCandidateTextBox"[\s\S]{0,500}Background="\{StaticResource BgPrimary\}"' `
        -or $xaml -notmatch 'x:Name="ModalVideoH3ApplyPromptButton"[\s\S]{0,180}Style="\{StaticResource PrimaryButton\}"' `
        -or $xaml -notmatch 'x:Name="ModalVideoH3UndoPromptButton"[\s\S]{0,180}Style="\{StaticResource GhostButton\}"' `
        -or $xaml -notmatch 'x:Name="ModalVideoH3PromptCandidateTextBox"[\s\S]{0,220}MaxLength="8001"') {
        throw 'The H3 prompt assistant does not preserve the existing bounded dark-resource pattern.'
    }

    $requiredResourceKeys = @(
        'UiVideoH3PromptAssistantTitle',
        'UiVideoH3PromptAssistantHelp',
        'UiVideoH3RewriteButton',
        'UiVideoH3RewriteAgainButton',
        'UiVideoH3RewriteWorkingButton',
        'UiVideoH3RewriteCancelButton',
        'UiVideoH3RewriteButtonAutomation',
        'UiVideoH3RewriteCancelButtonAutomation',
        'UiVideoH3RewriteCancelButtonHelp',
        'UiVideoH3RewriteModeLabel',
        'UiVideoH3RewriteModeAutomation',
        'UiVideoH3RewriteModePolish',
        'UiVideoH3RewriteModeDirection',
        'UiVideoH3RewriteModeAuto',
        'UiVideoH3ImageAnalysisOn',
        'UiVideoH3CandidateLabel',
        'UiVideoH3CandidateAutomation',
        'UiVideoH3ApplyButton',
        'UiVideoH3ApplyButtonAutomation',
        'UiVideoH3UndoButton',
        'UiVideoH3UndoButtonAutomation',
        'UiVideoH3StatusIdle',
        'UiVideoH3StatusReady',
        'UiVideoH3StatusStale',
        'UiVideoH3StatusInvalidCandidate',
        'UiVideoH3StatusInputTooLong'
        'UiVideoH3StatusBusy'
    )
    foreach ($resourcePath in @($jaResourcePath, $enResourcePath)) {
        [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
        $keys = @($resource.ResourceDictionary.String | ForEach-Object {
            $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        })
        foreach ($requiredKey in $requiredResourceKeys) {
            if ($keys -notcontains $requiredKey) {
                throw "Missing localized H3 prompt resource $requiredKey in $resourcePath"
            }
        }
    }

    $implementation = Get-Content -Raw -Encoding UTF8 -LiteralPath $implementationPath
    if ($implementation -notmatch 'api/enhance/video-prompts/h3/rewrite' `
        -or $implementation -notmatch 'aibos-h3-i2va-local-v1' `
        -or $implementation -notmatch 'BuildVideoH3RewriteRequestPrompt' `
        -or $implementation -notmatch 'VideoH3PromptRewriteMode\.Polish' `
        -or $implementation -notmatch 'VideoH3PromptRewriteMode\.Direction' `
        -or $implementation -notmatch 'VideoH3PromptRewriteMode\.Auto' `
        -or $implementation -notmatch 'CancelVideoH3PromptRewrite\(userInitiated:\s*true\)' `
        -or $implementation -notmatch 'UiVideoH3RewriteCancelButton' `
        -or $implementation -notmatch 'MaxVideoPromptLength' `
        -or $implementation -match 'EnsureEnhancementCompanionReadyForExplicitActionAsync' `
        -or $implementation -match 'SendEnhancementEnqueueAsync' `
        -or $implementation -match '\bSaveState\s*\(') {
        throw 'The H3 prompt assistant crossed its non-queue or non-persistent boundary.'
    }

    $dotNetExecutable = (Get-Command $DotNetPath -ErrorAction Stop).Source
    $dotNetRoot = Split-Path -Parent $dotNetExecutable
    foreach ($entry in $environmentPaths.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $previousEnvironment['DOTNET_ROOT'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    $previousEnvironment['DOTNET_ROOT_X64'] = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotNetRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $dotNetRoot, 'Process')

    if (-not $SkipBuild) {
        & $dotNetExecutable build $project `
            -c $Configuration `
            --artifacts-path $buildRoot `
            --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $exe = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.exe' `
        -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The isolated WPF executable was not found.'
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--video-h3-prompt-rewrite-smoke', ('"{0}"' -f $resultPath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -Raw -LiteralPath $stderrPath
        } else {
            'no stderr'
        }
        $captured = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        } else {
            'no result'
        }
        throw "H3 prompt rewrite smoke exited $($process.ExitCode): $stderr result=$captured"
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'H3 prompt rewrite smoke did not produce its result.'
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.ok `
        -or -not $result.contractIdentity `
        -or -not $result.sourceFixtureExact `
        -or -not $result.contractUnchanged `
        -or -not $result.surface `
        -or -not $result.requestExact `
        -or -not $result.responseFixtureAccepted `
        -or -not $result.revisionFixturesExact `
        -or -not $result.errorFixturesFailClosed `
        -or -not $result.candidateSeparate `
        -or -not $result.editorOversizeRejectedWhole `
        -or -not $result.candidateEditable `
        -or -not $result.rawUtf16LimitCheckedBeforeNormalization `
        -or -not $result.wholePromptMarkerUniqueness `
        -or -not $result.wholePromptMarkerOrder `
        -or -not $result.candidateNotPersisted `
        -or -not $result.queueReadsOnlyInput `
        -or -not $result.applied `
        -or -not $result.undone `
        -or -not $result.explicitCancelContract `
        -or -not $result.cancellationReachedTransport `
        -or -not $result.rewriteRequestTokenCanceled `
        -or $result.activeCancellationAwareRewriteRequests -ne 0 `
        -or -not $result.inputStale `
        -or -not $result.styleStale `
        -or -not $result.modelStale `
        -or -not $result.sourceStale `
        -or -not $result.oversizeRejected `
        -or -not $result.hashMismatchRejected `
        -or -not $result.modeOverflowRejectedExplicitly `
        -or -not $result.responseTransportBounded `
        -or -not $result.unavailableCompilerFailedClosed `
        -or -not $result.declaredOversizeRejected `
        -or $result.declaredOversizeBytesRead -ne 0 `
        -or -not $result.chunkedOversizeRejected `
        -or $result.chunkedOversizeBytesRead -le 0 `
        -or $result.chunkedOversizeBytesRead -gt ($result.responseByteLimit + 1) `
        -or -not $result.sourceChangedBeforePublishNoReservation `
        -or -not $result.candidateNotRestored `
        -or -not $result.noQueueMutation `
        -or $result.readinessGetCalls -ne 0 `
        -or $result.jobsPostCalls -ne 0 `
        -or $result.queueMutationCalls -ne 0 `
        -or $result.launchAttemptsAfterRewrite -ne 0 `
        -or $result.companionStarterCalls -ne 0) {
        throw ('H3 prompt rewrite smoke failed: ' + ($result | ConvertTo-Json -Compress -Depth 6))
    }

    [pscustomobject]@{
        ok = $true
        contractId = $rewriteContract.contractId
        protocol = $rewriteContract.protocol
        route = '{0} {1}' -f $rewriteContract.route.method, $rewriteContract.route.path
        rewriteRevision = $rewriteContract.rewriteRevision
        focusedSmoke = $result
    } | ConvertTo-Json -Depth 6
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ([string]::Equals([IO.Path]::GetDirectoryName($resolvedRunRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
            -and [IO.Path]::GetFileName($resolvedRunRoot) -match '^aibos-wpf-video-h3-prompt-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}
