param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipBuild,
    [switch]$StaticOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$plannerPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MotionDirectorPlan.cs'
$surfacePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.MotionDirector.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.MotionDirectorSmoke.cs'
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-video-tools-v1.json'
$jaResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.ja.xaml'
$enResourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\Localization\StringResources.en.xaml'

foreach ($requiredPath in @($plannerPath, $surfacePath, $smokePath, $xamlPath, $appPath, $jaResourcePath, $enResourcePath, $contractPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Motion Director source is missing: $requiredPath"
    }
}

$planner = Get-Content -Raw -Encoding UTF8 -LiteralPath $plannerPath
if ($planner -notmatch 'subtle-gaze' `
    -or $planner -notmatch 'gentle-smile' `
    -or $planner -notmatch 'subject-turn' `
    -or $planner -notmatch 'natural-reach' `
    -or $planner -notmatch 'gentle-walk' `
    -or $planner -notmatch 'expressive-gesture' `
    -or $planner -notmatch 'slow-push' `
    -or $planner -notmatch 'slow-pull' `
    -or $planner -notmatch 'gentle-track' `
    -or $planner -notmatch 'Anticipation' `
    -or $planner -notmatch 'Settle' `
    -or $planner -notmatch 'Hold' `
    -or $planner -notmatch 'integrated_multimodal_description:' `
    -or $planner -notmatch 'overall_soundscape:' `
    -or $planner -notmatch 'non_diegetic_music:') {
    throw 'The bounded Motion Director catalog, timeline phases, or H3 compiler is incomplete.'
}
if ($planner -match 'HttpClient|SendEnhancement|Enqueue|SaveState|File\.|Directory\.|Process\.') {
    throw 'The deterministic Motion Director planner crossed an I/O boundary.'
}

$surface = Get-Content -Raw -Encoding UTF8 -LiteralPath $surfacePath
if ($surface -match 'SendEnhancement|Enqueue|SaveState|StartEnhancement|HttpClient|File\.|Directory\.|Process\.') {
    throw 'The Motion Director surface crossed its transient, no-transport boundary.'
}

$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
if ($xaml -notmatch 'x:Name="ModalVideoH3PromptRewritePanel"[\s\S]{0,2500}x:Name="ModalMotionDirectorBuildButton"' `
    -or $xaml -notmatch 'x:Name="ModalMotionDirectorActionsPanel"' `
    -or $xaml -notmatch 'x:Name="ModalMotionDirectorCameraComboBox"' `
    -or $xaml -notmatch 'x:Name="ModalMotionDirectorTimelineText"' `
    -or $xaml -notmatch 'x:Name="ModalMotionDirectorWarningText"' `
    -or $xaml -notmatch 'AutomationProperties\.Name="\{DynamicResource UiMotionDirectorBuildAutomation\}"' `
    -or $xaml -notmatch 'AutomationProperties\.HelpText="\{DynamicResource UiMotionDirectorBuildHelp\}"') {
    throw 'The H3 board is missing the compact Motion Director or its accessibility surface.'
}
foreach ($actionId in @('subtle-gaze', 'gentle-smile', 'subject-turn', 'natural-reach', 'gentle-walk', 'expressive-gesture')) {
    if ($xaml -notmatch ('Tag="' + [regex]::Escape($actionId) + '"[\s\S]{0,900}AutomationProperties\.HelpText="\{DynamicResource UiMotionDirectorActionHelp\}"')) {
        throw "The Motion Director action chip is missing its stable id or accessibility help: $actionId"
    }
}
foreach ($cameraId in @('fixed', 'slow-push', 'slow-pull', 'gentle-track')) {
    if ($xaml -notmatch ('<ComboBoxItem[^>]+Tag="' + [regex]::Escape($cameraId) + '"')) {
        throw "The Motion Director camera choice is missing its stable id: $cameraId"
    }
}

$requiredResourceKeys = @(
    'UiMotionDirectorTitle',
    'UiMotionDirectorPanelAutomation',
    'UiMotionDirectorHelp',
    'UiMotionDirectorActionsLabel',
    'UiMotionDirectorActionHelp',
    'UiMotionDirectorActionGaze',
    'UiMotionDirectorActionSmile',
    'UiMotionDirectorActionTurn',
    'UiMotionDirectorActionReach',
    'UiMotionDirectorActionWalk',
    'UiMotionDirectorActionGesture',
    'UiMotionDirectorCameraLabel',
    'UiMotionDirectorCameraAutomation',
    'UiMotionDirectorCameraHelp',
    'UiMotionDirectorCameraFixed',
    'UiMotionDirectorCameraPush',
    'UiMotionDirectorCameraPull',
    'UiMotionDirectorCameraTrack',
    'UiMotionDirectorTimelineAutomation',
    'UiMotionDirectorBuildButton',
    'UiMotionDirectorBuildAutomation',
    'UiMotionDirectorBuildHelp',
    'UiMotionDirectorAiProposalTitle'
)
foreach ($resourcePath in @($jaResourcePath, $enResourcePath)) {
    [xml]$resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $resourcePath
    $keys = @($resource.ResourceDictionary.String | ForEach-Object {
        $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    })
    foreach ($requiredKey in $requiredResourceKeys) {
        if ($keys -notcontains $requiredKey) {
            throw "Missing localized Motion Director resource $requiredKey in $resourcePath"
        }
    }
}

$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath
if ($app -notmatch '--motion-director-smoke' -or $app -notmatch 'CaptureMotionDirectorSmoke') {
    throw 'The isolated Motion Director smoke dispatch is missing.'
}
if ($surface -notmatch '_videoH3PromptCandidate\s*=\s*normalizedCandidate' `
    -or $surface -notmatch 'TryCaptureVideoH3SourceStamp' `
    -or $surface -notmatch '_motionDirectorCandidateOrigin\s*=\s*true' `
    -or $surface -notmatch 'MotionDirectorCandidateContextMatches' `
    -or $surface -notmatch 'VideoH3SourceStampsEqual') {
    throw 'Motion Director does not preserve the transient candidate and source-staleness boundary.'
}
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
if ($smoke -notmatch 'noTransportOrDurableMutation' `
    -or $smoke -notmatch 'candidateSeparate' `
    -or $smoke -notmatch 'ApplyVideoH3PromptCandidateForSmoke' `
    -or $smoke -notmatch 'UndoAppliedVideoH3PromptForSmoke' `
    -or $smoke -notmatch 'contextChangesStaleAndRebuild') {
    throw 'The Motion Director smoke does not cover its transient and passive invariants.'
}

if ($StaticOnly) {
    $compilePrefix = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
'@
    $oracleSource = @'
public static class MotionDirectorStaticOracle
{
    public static bool Run(string expectedInteropPrompt)
    {
        bool coverage = true;
        bool grammar = true;
        foreach (int frames in MotionDirectorPlanner.SupportedFrameCounts)
        {
            bool built = MotionDirectorPlanner.TryBuild(
                frames,
                24,
                new[] { "subtle-gaze", "gentle-smile", "subject-turn" },
                "slow-push",
                out MotionDirectorPlan plan,
                out _);
            coverage &= built
                && plan.Segments.Count == plan.Actions.Count * 3 + 1
                && plan.Segments[0].StartFrame == 0
                && plan.Segments[^1].EndFrame == frames
                && plan.Segments[^1].Phase == MotionDirectorPhase.Hold
                && plan.Segments.All(segment => segment.EndFrame > segment.StartFrame);
            for (int index = 0; built && index + 1 < plan.Segments.Count; index++)
                coverage &= plan.Segments[index].EndFrame == plan.Segments[index + 1].StartFrame;
            foreach (MotionDirectorActionDefinition action in plan.Actions)
            {
                int allocated = plan.Segments
                    .Where(segment => segment.ActionId == action.Id)
                    .Sum(segment => segment.FrameCount);
                coverage &= allocated >= action.MinimumFrames
                    && allocated <= action.MaximumFrames;
            }
            string prompt = built ? plan.CandidatePrompt : "";
            int integrated = prompt.IndexOf(
                "integrated_multimodal_description:",
                StringComparison.Ordinal);
            int sound = prompt.IndexOf(
                "overall_soundscape:",
                StringComparison.Ordinal);
            int music = prompt.IndexOf(
                "non_diegetic_music:",
                StringComparison.Ordinal);
            grammar &= prompt.StartsWith(
                    "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.",
                    StringComparison.Ordinal)
                && integrated > 0
                && sound > integrated
                && music > sound
                && prompt.Split("integrated_multimodal_description:").Length == 2
                && prompt.Split("overall_soundscape:").Length == 2
                && prompt.Split("non_diegetic_music:").Length == 2
                && prompt.Contains("non_diegetic_music: None; do not add music.", StringComparison.Ordinal)
                && prompt.Length <= 8000;
        }

        MotionDirectorPlanner.TryBuild(
            124,
            24,
            new[] { "natural-reach", "gentle-walk", "expressive-gesture" },
            "fixed",
            out MotionDirectorPlan overflow,
            out _);
        bool dropped = overflow.Actions.Select(action => action.Id)
                .SequenceEqual(new[] { "natural-reach", "gentle-walk" })
            && overflow.DroppedActions.Select(action => action.Id)
                .SequenceEqual(new[] { "expressive-gesture" });
        MotionDirectorPlanner.TryBuild(
            243,
            24,
            new[] { "gentle-walk" },
            "gentle-track",
            out MotionDirectorPlan fallback,
            out _);
        bool safe = fallback.RequestedCamera.Id == "gentle-track"
            && fallback.EffectiveCamera.Id == "fixed"
            && fallback.WarningResourceKey == "UiMotionDirectorWarningFallback";
        MotionDirectorPlanner.TryBuild(
            124,
            24,
            new[] { "natural-reach", "gentle-walk", "expressive-gesture" },
            "gentle-track",
            out MotionDirectorPlan compound,
            out _);
        bool compoundWarning = compound.EffectiveCamera.Id == "fixed"
            && compound.WarningResourceKey == "UiMotionDirectorWarningFallback"
            && compound.DroppedActions.Select(action => action.Id)
                .SequenceEqual(new[] { "expressive-gesture" });
        MotionDirectorPlanner.TryBuild(
            243,
            24,
            new[] { "subtle-gaze", "gentle-smile", "subject-turn" },
            "slow-push",
            out MotionDirectorPlan first,
            out _);
        MotionDirectorPlanner.TryBuild(
            243,
            24,
            new[] { "subtle-gaze", "gentle-smile", "subject-turn" },
            "slow-push",
            out MotionDirectorPlan second,
            out _);
        MotionDirectorPlanner.TryBuild(
            124,
            24,
            new[] { "subtle-gaze", "gentle-smile" },
            "fixed",
            out MotionDirectorPlan interop,
            out _);
        return coverage
            && grammar
            && dropped
            && safe
            && compoundWarning
            && first.CandidatePrompt == second.CandidatePrompt
            && interop.CandidatePrompt == expectedInteropPrompt;
    }
}
'@
    Add-Type -TypeDefinition ($compilePrefix + [Environment]::NewLine + $planner + [Environment]::NewLine + $oracleSource) `
        -Language CSharp
    $contract = Get-Content -Raw -Encoding UTF8 -LiteralPath $contractPath |
        ConvertFrom-Json -Depth 100
    $expectedInteropPrompt = [string]$contract.promptInteropFixtures.motionDirectorV1.prompt
    if ([string]::IsNullOrWhiteSpace($expectedInteropPrompt)) {
        throw 'The shared Motion Director to Retake prompt fixture is missing.'
    }
    $plannerOracle = [PhotoViewer.Wpf.MotionDirectorStaticOracle]::Run(
        $expectedInteropPrompt)
    if (-not $plannerOracle) {
        throw 'The executable Motion Director planner oracle failed.'
    }
    [pscustomobject]@{
        ok = $true
        staticSurface = $true
        executablePlannerOracle = $plannerOracle
        runtimeSmoke = 'skipped: net10 SDK and WPF build output are required'
    } | ConvertTo-Json -Depth 4
    return
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('aibos-wpf-motion-director-' + [guid]::NewGuid().ToString('N'))))
$runParent = [IO.Path]::GetDirectoryName($runRoot)
$runLeaf = [IO.Path]::GetFileName($runRoot)
if (-not [string]::Equals($runParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $runLeaf -notmatch '^aibos-wpf-motion-director-[0-9a-f]{32}$') {
    throw "Run root escaped the exact TEMP boundary: $runRoot"
}

$buildRoot = Join-Path $runRoot 'build'
$storesRoot = Join-Path $runRoot 'stores'
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
}

try {
    New-Item -ItemType Directory -Path $runRoot, $storesRoot -Force | Out-Null
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
        & $dotNetExecutable build $project -c $Configuration --artifacts-path $buildRoot --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $exe = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'PhotoViewer.Wpf.exe' `
        -ErrorAction Stop | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The isolated WPF executable was not found.'
    }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--motion-director-smoke', ('"{0}"' -f $resultPath)) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { 'no stderr' }
        $captured = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath } else { 'no result' }
        throw "Motion Director smoke exited $($process.ExitCode): $stderr result=$captured"
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.ok `
        -or -not $result.everyProfileExactCoverage `
        -or -not $result.overflowDropsLowerPriority `
        -or -not $result.deterministic `
        -or -not $result.h3GrammarValid `
        -or -not $result.safeFallback `
        -or -not $result.simultaneousWarnings `
        -or -not $result.noTransportOrDurableMutation `
        -or -not $result.candidateSeparate `
        -or -not $result.applied `
        -or -not $result.undone `
        -or -not $result.stalesOnDurationChange `
        -or -not $result.contextChangesStaleAndRebuild `
        -or -not $result.surface `
        -or -not $result.boardWidthContract) {
        throw ('Motion Director smoke failed: ' + ($result | ConvertTo-Json -Compress -Depth 8))
    }

    [pscustomobject]@{
        ok = $true
        focusedSmoke = $result
    } | ConvertTo-Json -Depth 8
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if ([string]::Equals([IO.Path]::GetDirectoryName($resolvedRunRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
            -and [IO.Path]::GetFileName($resolvedRunRoot) -match '^aibos-wpf-motion-director-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
        }
    }
}
