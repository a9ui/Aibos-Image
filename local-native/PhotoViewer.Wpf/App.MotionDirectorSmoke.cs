using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureMotionDirectorSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-motion-director-")
            .FullName;
        string sourceFolder = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourcePath = Path.Combine(
            sourceFolder,
            "synthetic-motion-director-source.png");
        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(
                storeRoot,
                "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(
                storeRoot,
                "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(
                storeRoot,
                "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(
                storeRoot,
                "recent-folders.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(
                storeRoot,
                "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(
                storeRoot,
                "search-history.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(
                storeRoot,
                "enhance",
                "jobs.json"),
        };
        Dictionary<string, string?> previousEnvironment = environment.Keys
            .ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        object result = new { ok = false };
        bool ok = false;
        MainWindow? window = null;

        try
        {
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(storeRoot);
            File.WriteAllBytes(sourcePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            bool everyProfileExactCoverage = true;
            bool h3GrammarValid = true;
            string[] profileCandidates = new string[
                MotionDirectorPlanner.SupportedFrameCounts.Count];
            for (int index = 0;
                index < MotionDirectorPlanner.SupportedFrameCounts.Count;
                index++)
            {
                int frameCount = MotionDirectorPlanner
                    .SupportedFrameCounts[index];
                bool built = MotionDirectorPlanner.TryBuild(
                    frameCount,
                    MotionDirectorPlanner.PlaybackFps,
                    ["subtle-gaze", "gentle-smile", "subject-turn"],
                    "slow-push",
                    out MotionDirectorPlan profilePlan,
                    out _);
                bool coverage = built
                    && profilePlan.Segments.Count
                        == profilePlan.Actions.Count * 3 + 1
                    && profilePlan.Segments[0].StartFrame == 0
                    && profilePlan.Segments[^1].EndFrame == frameCount
                    && profilePlan.Segments[^1].Phase
                        == MotionDirectorPhase.Hold
                    && profilePlan.Segments.All(static segment =>
                        segment.EndFrame > segment.StartFrame)
                    && profilePlan.Segments
                        .Zip(profilePlan.Segments.Skip(1))
                        .All(static pair =>
                            pair.First.EndFrame == pair.Second.StartFrame)
                    && profilePlan.Actions.All(action =>
                    {
                        int allocated = profilePlan.Segments
                            .Where(segment => string.Equals(
                                segment.ActionId,
                                action.Id,
                                StringComparison.Ordinal))
                            .Sum(static segment => segment.FrameCount);
                        return allocated >= action.MinimumFrames
                            && allocated <= action.MaximumFrames;
                    });
                everyProfileExactCoverage &= coverage;
                string normalized = "";
                h3GrammarValid &= built
                    && PhotoViewer.Wpf.MainWindow
                        .TryValidateVideoToolsRetakePromptForSmoke(
                            profilePlan.CandidatePrompt,
                            out normalized)
                    && string.Equals(
                        normalized,
                        profilePlan.CandidatePrompt,
                        StringComparison.Ordinal);
                profileCandidates[index] = built
                    ? profilePlan.CandidatePrompt
                    : "";
            }

            bool firstOverflowBuilt = MotionDirectorPlanner.TryBuild(
                124,
                MotionDirectorPlanner.PlaybackFps,
                ["natural-reach", "gentle-walk", "expressive-gesture"],
                "fixed",
                out MotionDirectorPlan overflowPlan,
                out _);
            bool overflowDropsLowerPriority = firstOverflowBuilt
                && overflowPlan.Actions.Select(static action => action.Id)
                    .SequenceEqual(["natural-reach", "gentle-walk"])
                && overflowPlan.DroppedActions
                    .Select(static action => action.Id)
                    .SequenceEqual(["expressive-gesture"])
                && overflowPlan.Segments[0].StartFrame == 0
                && overflowPlan.Segments[^1].EndFrame == 124;

            bool repeatBuilt = MotionDirectorPlanner.TryBuild(
                243,
                MotionDirectorPlanner.PlaybackFps,
                ["subtle-gaze", "gentle-smile", "subject-turn"],
                "slow-push",
                out MotionDirectorPlan repeatedPlan,
                out _);
            bool deterministic = repeatBuilt
                && string.Equals(
                    repeatedPlan.CandidatePrompt,
                    profileCandidates[1],
                    StringComparison.Ordinal)
                && repeatedPlan.Segments.Select(static segment =>
                        $"{segment.Phase}:{segment.ActionId}:{segment.StartFrame}:{segment.EndFrame}")
                    .SequenceEqual(MotionDirectorPlanner.TryBuild(
                        243,
                        MotionDirectorPlanner.PlaybackFps,
                        ["subtle-gaze", "gentle-smile", "subject-turn"],
                        "slow-push",
                        out MotionDirectorPlan secondRepeat,
                        out _)
                            ? secondRepeat.Segments.Select(static segment =>
                                $"{segment.Phase}:{segment.ActionId}:{segment.StartFrame}:{segment.EndFrame}")
                            : []);

            bool fallbackBuilt = MotionDirectorPlanner.TryBuild(
                243,
                MotionDirectorPlanner.PlaybackFps,
                ["gentle-walk"],
                "gentle-track",
                out MotionDirectorPlan fallbackPlan,
                out _);
            bool safeFallback = fallbackBuilt
                && fallbackPlan.RequestedCamera.Id == "gentle-track"
                && fallbackPlan.EffectiveCamera.Id == "fixed"
                && fallbackPlan.WarningResourceKey
                    == "UiMotionDirectorWarningFallback"
                && fallbackPlan.Risk == MotionDirectorRiskLevel.High;
            bool compoundBuilt = MotionDirectorPlanner.TryBuild(
                124,
                MotionDirectorPlanner.PlaybackFps,
                ["natural-reach", "gentle-walk", "expressive-gesture"],
                "gentle-track",
                out MotionDirectorPlan compoundPlan,
                out _);
            bool compoundPlanWarnings = compoundBuilt
                && compoundPlan.EffectiveCamera.Id == "fixed"
                && compoundPlan.WarningResourceKey
                    == "UiMotionDirectorWarningFallback"
                && compoundPlan.DroppedActions
                    .Select(static action => action.Id)
                    .SequenceEqual(["expressive-gesture"]);

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    int transportCalls = 0;
                    int companionStarterCalls = 0;
                    window.ConfigureModalEnhancementForSmoke((request, token) =>
                    {
                        transportCalls++;
                        return Task.FromResult(new HttpResponseMessage(
                            HttpStatusCode.NotFound));
                    });
                    window.EnableEnhancementCompanionAutoStartProbeForSmoke(_ =>
                    {
                        companionStarterCalls++;
                        return (false, "Motion Director must not launch the companion");
                    });
                    await window.LoadFolderAsync(sourceFolder);
                    bool selected = window.SelectFileNameForSmoke(
                        Path.GetFileName(sourcePath));
                    window.SetMiniMaxH3CapabilityForSmoke(
                        checkedHealth: true,
                        ready: true,
                        reasonCode: null);
                    window.SelectVideoModelForSmoke("minimax-h3");
                    const string basePrompt =
                        "A source-faithful portrait in one continuous shot.";
                    window.ConfigureVideoGenerationForSmoke(
                        5,
                        MotionDirectorPlanner.PlaybackFps,
                        414_720,
                        basePrompt);
                    window.FlushStateForSmoke();
                    await window.Dispatcher.InvokeAsync(
                        static () => { },
                        DispatcherPriority.ContextIdle);

                    string durableBefore = FingerprintMotionDirectorStores(
                        storeRoot);
                    int launchAttemptsBefore =
                        window.EnhancementCompanionLaunchAttemptCountForSmoke;
                    bool boardOpened = window.OpenVideoGenerationBoardForSmoke(
                        "original");
                    // Opening the existing H3 board performs one passive
                    // capability GET. Motion Director itself must add no
                    // transport beyond that established board behavior.
                    int transportCallsBeforeDirector = transportCalls;
                    window.SetMotionDirectorSelectionForSmoke(
                        ["natural-reach", "gentle-walk", "expressive-gesture"],
                        "gentle-track");
                    string compoundWarning =
                        window.MotionDirectorWarningForSmoke;
                    string fallbackWarning = window.FindResource(
                        "UiMotionDirectorWarningFallback") as string ?? "";
                    string droppedActionLabel = window.FindResource(
                        "UiMotionDirectorActionGesture") as string ?? "";
                    bool simultaneousWarnings = compoundPlanWarnings
                        && fallbackWarning.Length > 0
                        && droppedActionLabel.Length > 0
                        && compoundWarning.Contains(
                            fallbackWarning,
                            StringComparison.Ordinal)
                        && compoundWarning.Contains(
                            droppedActionLabel,
                            StringComparison.Ordinal);
                    window.SetMotionDirectorSelectionForSmoke(
                        ["subtle-gaze", "gentle-smile"],
                        "fixed");
                    string authoritativeBefore =
                        window.AuthoritativeVideoPromptForSmoke;
                    bool compiled = window.BuildMotionDirectorCandidateForSmoke();
                    string compiledCandidate =
                        window.VideoH3PromptCandidateForSmoke;
                    string durableAfterCompile = FingerprintMotionDirectorStores(
                        storeRoot);
                    bool noTransportOrDurableMutation = selected
                        && boardOpened
                        && compiled
                        && transportCalls == transportCallsBeforeDirector
                        && companionStarterCalls == 0
                        && window.EnhancementCompanionLaunchAttemptCountForSmoke
                            == launchAttemptsBefore
                        && string.Equals(
                            durableBefore,
                            durableAfterCompile,
                            StringComparison.Ordinal);
                    bool candidateSeparate = compiled
                        && compiledCandidate.Length > 0
                        && !string.Equals(
                            compiledCandidate,
                            authoritativeBefore,
                            StringComparison.Ordinal)
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            authoritativeBefore,
                            StringComparison.Ordinal)
                        && window.MotionDirectorCandidateFreshForSmoke;
                    bool applied = window.ApplyVideoH3PromptCandidateForSmoke()
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            compiledCandidate,
                            StringComparison.Ordinal);
                    bool undone = window.UndoAppliedVideoH3PromptForSmoke()
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            authoritativeBefore,
                            StringComparison.Ordinal);

                    window.BuildMotionDirectorCandidateForSmoke();
                    bool freshBeforeDurationChange =
                        window.MotionDirectorCandidateFreshForSmoke;
                    window.ConfigureVideoGenerationForSmoke(
                        10,
                        MotionDirectorPlanner.PlaybackFps,
                        414_720,
                        authoritativeBefore);
                    bool stalesOnDurationChange = freshBeforeDurationChange
                        && !window.MotionDirectorCandidateFreshForSmoke;
                    window.ConfigureVideoGenerationForSmoke(
                        5,
                        MotionDirectorPlanner.PlaybackFps,
                        414_720,
                        authoritativeBefore);
                    bool rebuiltAfterDuration =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;

                    window.SetAuthoritativeVideoPromptForSmoke(
                        authoritativeBefore + " Changed input.");
                    bool inputStale =
                        !window.MotionDirectorCandidateFreshForSmoke;
                    window.SetAuthoritativeVideoPromptForSmoke(
                        authoritativeBefore);
                    bool rebuiltAfterInput =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;

                    window.SelectVideoModelForSmoke("wan22");
                    bool modelStale =
                        !window.MotionDirectorCandidateFreshForSmoke;
                    window.SelectVideoModelForSmoke("minimax-h3");
                    bool rebuiltAfterModel =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;

                    window.SetMotionDirectorStyleContextForSmoke(
                        "Synthetic style");
                    bool styleStale =
                        !window.MotionDirectorCandidateFreshForSmoke;
                    bool rebuiltAfterStyle =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;
                    window.SetMotionDirectorStyleContextForSmoke(null);
                    bool resetStyleStale =
                        !window.MotionDirectorCandidateFreshForSmoke;
                    bool rebuiltAfterStyleReset =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;

                    using (var sourceMutation = new FileStream(
                        sourcePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        sourceMutation.WriteByte(0);
                    }
                    bool sourceStale =
                        !window.MotionDirectorCandidateFreshForSmoke;
                    bool rebuiltAfterSource =
                        window.BuildMotionDirectorCandidateForSmoke()
                        && window.MotionDirectorCandidateFreshForSmoke;
                    bool contextChangesStaleAndRebuild =
                        stalesOnDurationChange
                        && rebuiltAfterDuration
                        && inputStale
                        && rebuiltAfterInput
                        && modelStale
                        && rebuiltAfterModel
                        && styleStale
                        && rebuiltAfterStyle
                        && resetStyleStale
                        && rebuiltAfterStyleReset
                        && sourceStale
                        && rebuiltAfterSource;
                    string[] surfaceIssues = window
                        .MotionDirectorSurfaceIssuesForSmoke.ToArray();
                    bool surface = surfaceIssues.Length == 0
                        && window.VideoH3PromptRewritePanelVisibleForSmoke
                        && window.MotionDirectorTimelineForSmoke.Contains(
                            "124f @ 24fps",
                            StringComparison.Ordinal);
                    bool boardWidthContract =
                        window.MotionDirectorBoardWidthContractForSmoke;

                    ok = everyProfileExactCoverage
                        && overflowDropsLowerPriority
                        && deterministic
                        && h3GrammarValid
                        && safeFallback
                        && simultaneousWarnings
                        && noTransportOrDurableMutation
                        && candidateSeparate
                        && applied
                        && undone
                        && contextChangesStaleAndRebuild
                        && surface
                        && boardWidthContract;
                    result = new
                    {
                        ok,
                        everyProfileExactCoverage,
                        overflowDropsLowerPriority,
                        deterministic,
                        h3GrammarValid,
                        safeFallback,
                        simultaneousWarnings,
                        compoundWarning,
                        noTransportOrDurableMutation,
                        candidateSeparate,
                        applied,
                        undone,
                        stalesOnDurationChange,
                        contextChangesStaleAndRebuild,
                        rebuiltAfterDuration,
                        inputStale,
                        rebuiltAfterInput,
                        modelStale,
                        rebuiltAfterModel,
                        styleStale,
                        rebuiltAfterStyle,
                        resetStyleStale,
                        rebuiltAfterStyleReset,
                        sourceStale,
                        rebuiltAfterSource,
                        surface,
                        surfaceIssues,
                        boardWidthContract,
                        transportCalls,
                        transportCallsBeforeDirector,
                        companionStarterCalls,
                        launchAttemptsBefore,
                        launchAttemptsAfter = window
                            .EnhancementCompanionLaunchAttemptCountForSmoke,
                        timeline = window.MotionDirectorTimelineForSmoke,
                        warning = window.MotionDirectorWarningForSmoke,
                    };
                }
                catch (Exception ex)
                {
                    result = new
                    {
                        ok = false,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                        stackTrace = ex.ToString(),
                    };
                }
                finally
                {
                    FinishMotionDirectorSmoke(
                        resultFullPath,
                        result,
                        ok,
                        window,
                        previousEnvironment,
                        smokeRoot);
                }
            });
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                stackTrace = ex.ToString(),
            };
            FinishMotionDirectorSmoke(
                resultFullPath,
                result,
                false,
                window,
                previousEnvironment,
                smokeRoot);
        }
    }

    private void FinishMotionDirectorSmoke(
        string resultPath,
        object result,
        bool ok,
        MainWindow? window,
        IReadOnlyDictionary<string, string?> previousEnvironment,
        string smokeRoot)
    {
        try { window?.Close(); } catch { }
        foreach ((string key, string? value) in previousEnvironment)
            Environment.SetEnvironmentVariable(key, value);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(
            resultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (Directory.Exists(smokeRoot))
                Directory.Delete(smokeRoot, recursive: true);
        }
        catch
        {
        }
        Shutdown(ok ? 0 : 1);
    }

    private static string FingerprintMotionDirectorStores(
        string storeRoot)
    {
        var result = new StringBuilder();
        string root = Path.GetFullPath(storeRoot);
        foreach (string path in Directory
            .EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            result.Append(Path.GetRelativePath(root, path));
            result.Append('=');
            if (File.Exists(path))
            {
                result.Append(Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(path)))
                    .ToLowerInvariant());
            }
            else
            {
                result.Append("directory");
            }
            result.Append(';');
        }

        return result.ToString();
    }
}
