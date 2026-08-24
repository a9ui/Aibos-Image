using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoEditV2Smoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-edit-v2-")
            .FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourcePath = Path.Combine(sourceRoot, "synthetic-edit.mp4");
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
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(
                storeRoot,
                "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(
                storeRoot,
                "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(
                storeRoot,
                "search-history.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(
                storeRoot,
                "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(
                storeRoot,
                "enhance",
                "jobs.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = Path.Combine(
                storeRoot,
                "outputs"),
        };
        Dictionary<string, string?> previousEnvironment = environment.Keys
            .ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        MainWindow? window = null;
        object result = new { ok = false, message = "Smoke did not complete." };
        bool ok = false;

        try
        {
            bool planner24 = VideoEditV2Planner.TryPlan(
                    7_200,
                    24,
                    1,
                    24,
                    144,
                    out VideoEditV2SelectionPlan plan24,
                    out _)
                && plan24.SelectedFrameCount == 120
                && plan24.StartPreviewFrame == 24
                && plan24.MiddlePreviewFrame == 83
                && plan24.EndPreviewFrame == 143
                && plan24.EndFrameExclusive == 144;
            bool planner30 = VideoEditV2Planner.TryPlan(
                    9_000,
                    30,
                    1,
                    30,
                    180,
                    out VideoEditV2SelectionPlan plan30,
                    out _)
                && plan30.SelectedFrameCount == 150
                && plan30.MaximumSelectionFrames == 150;
            bool planner60 = VideoEditV2Planner.TryPlan(
                    18_000,
                    60,
                    1,
                    60,
                    360,
                    out VideoEditV2SelectionPlan plan60,
                    out _)
                && plan60.SelectedFrameCount == 300
                && plan60.MaximumSelectionFrames == 300;
            bool invalidPlannerInputs =
                !VideoEditV2Planner.TryPlan(
                    7_200,
                    25,
                    1,
                    0,
                    100,
                    out _,
                    out VideoEditV2PlanError unsupportedFps)
                && unsupportedFps == VideoEditV2PlanError.UnsupportedFps
                && !VideoEditV2Planner.TryPlan(
                    7_200,
                    24,
                    1,
                    0,
                    121,
                    out _,
                    out VideoEditV2PlanError tooLong)
                && tooLong == VideoEditV2PlanError.SelectionTooLong
                && !VideoEditV2Planner.TryPlan(
                    7_200,
                    24,
                    1,
                    10,
                    10,
                    out _,
                    out VideoEditV2PlanError empty)
                && empty == VideoEditV2PlanError.InvalidRange;
            bool purePlanner = planner24
                && planner30
                && planner60
                && invalidPlannerInputs;

            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(storeRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            string sourceBefore = FingerprintVideoEditV2File(sourcePath);
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            int pathResolverCalls = 0;
            window.SetCanonicalPathResolverForSmoke(path =>
            {
                pathResolverCalls++;
                return Path.GetFullPath(path);
            });
            int companionCalls = 0;
            window.ConfigureModalEnhancementForSmoke((_, _) =>
            {
                companionCalls++;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable));
            });
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    bool hiddenForImages = !window.VideoEditV2EntryVisibleForSmoke;
                    ExternalVideoDropSmokeSnapshot drop =
                        await window.DropExternalVideoForSmokeAsync([sourcePath]);
                    int pathResolverCallsAfterDrop = pathResolverCalls;
                    string storesBefore = FingerprintVideoEditV2Tree(storeRoot);
                    bool videoEntry = drop.Accepted
                        && window.VideoEditV2EntryVisibleForSmoke
                        && window.VideoEditV2ExternalContextEntryForSmoke
                        && window.VideoEditV2DedicatedContextIsExternalOnlyForSmoke;
                    bool boardOpened = window.OpenVideoEditV2ForSmoke();
                    bool externalStartsUnverified = boardOpened
                        && window.VideoEditV2ProbeAffordanceVisibleForSmoke
                        && !window.VideoEditV2ExactFrameControlsEnabledForSmoke
                        && window.VideoEditV2CompilerDisabledForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2TrimDisabledForSmoke
                        && window.VideoEditV2PreviewFramesForSmoke
                            .All(static value => value == "f --");
                    string storesAfterOpen =
                        FingerprintVideoEditV2Tree(storeRoot);
                    bool passiveOpen = companionCalls == 0
                        && pathResolverCalls == pathResolverCallsAfterDrop
                        && string.Equals(
                            storesBefore,
                            storesAfterOpen,
                            StringComparison.Ordinal);

                    bool exactProbeAccepted =
                        window.SetVideoEditV2ExternalProbeForSmoke(24, 240)
                        && window.VideoEditV2ExactFrameControlsEnabledForSmoke
                        && window.VideoEditV2CompilerDisabledForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2TrimDisabledForSmoke;
                    bool selected = window.SetVideoEditV2SelectionForSmoke(
                        24,
                        144);
                    string[] previewFrames =
                        window.VideoEditV2PreviewFramesForSmoke;
                    bool halfOpenPreview = selected
                        && previewFrames.SequenceEqual(
                            ["f 24", "f 83", "f 143"])
                        && window.VideoEditV2RangeStatusForSmoke.Contains(
                            "[24, 144)",
                            StringComparison.Ordinal);
                    bool previewSeek = window.SeekVideoEditV2PreviewForSmoke(
                            "middle")
                        && Math.Abs(
                            window.VideoEditV2PlaybackPositionForSmoke
                                - 83d / 24d) < 0.001;

                    window.SetVideoEditV2InstructionForSmoke(
                        "人物と背景を保ち、服の色だけを青へ変える");
                    bool malformedCompilerResponseRejected =
                        !window.ApplyVideoEditV2CompiledCandidateForSmoke(
                            "Preserve the source except for the requested edit.",
                            "指定箇所以外を維持します。",
                            "synthetic-compiler-v1",
                            "not-a-sha256")
                        && !window.ApplyVideoEditV2CompiledCandidateForSmoke(
                            "Preserve the source.\0",
                            "指定箇所以外を維持します。",
                            "synthetic-compiler-v1",
                            new string('b', 64));
                    bool candidateApplied =
                        window.ApplyVideoEditV2CompiledCandidateForSmoke(
                            "Preserve the subject, background, timing, and camera. Change only the clothing color to blue.",
                            "人物・背景・動き・カメラを保ち、服の色だけを青へ変えます。");
                    bool reviewWithoutStart = candidateApplied
                        && window.VideoEditV2ReviewVisibleForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;
                    bool changed = window.SetVideoEditV2SelectionForSmoke(
                        48,
                        168);
                    bool candidateStales = changed
                        && window.VideoEditV2CandidateStaleForSmoke
                        && window.VideoEditV2StartDisabledForSmoke;

                    window.SetVideoEditV2SkipReviewForSmoke(true);
                    bool autoSuppressed =
                        window.ApplyVideoEditV2CompiledCandidateForSmoke(
                            "Preserve the source except for the requested semantic edit.",
                            "指定部分だけを変更し、他は維持します。")
                        && window.VideoEditV2ReviewVisibleForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;
                    string storesAfterCandidate =
                        FingerprintVideoEditV2Tree(storeRoot);
                    bool transientOnly = companionCalls == 0
                        && pathResolverCalls == pathResolverCallsAfterDrop
                        && string.Equals(
                            storesBefore,
                            storesAfterCandidate,
                            StringComparison.Ordinal);

                    bool escapeClosesBoard =
                        window.InvokePreviewKeyForSmoke(Key.Escape)
                        && !window.VideoEditV2BoardVisibleForSmoke
                        && window.VideoEditV2LastCloseWasStaleForSmoke;
                    bool reopenAfterEscape =
                        window.OpenVideoEditV2ForSmoke();
                    bool minimizeClosesBoard = reopenAfterEscape
                        && window.ActivateModalMinimizeForSmoke()
                        && !window.VideoEditV2BoardVisibleForSmoke
                        && window.VideoEditV2LastCloseWasStaleForSmoke;
                    bool reopenAfterMinimize =
                        window.OpenVideoEditV2ForSmoke();
                    bool sourceNavigationClosesBoard = reopenAfterMinimize
                        && window
                            .InvalidateVideoEditV2ForSourceNavigationForSmoke();
                    bool reopenAfterSourceNavigation =
                        window.OpenVideoEditV2ForSmoke();

                    window.CloseModalForSmoke();
                    bool sourceChangeClosesStale =
                        reopenAfterSourceNavigation
                        && window.VideoEditV2LastCloseWasStaleForSmoke
                        && !window.VideoEditV2EntryVisibleForSmoke;
                    bool sourceUntouched = string.Equals(
                        sourceBefore,
                        FingerprintVideoEditV2File(sourcePath),
                        StringComparison.Ordinal);

                    ok = purePlanner
                        && hiddenForImages
                        && videoEntry
                        && externalStartsUnverified
                        && passiveOpen
                        && exactProbeAccepted
                        && halfOpenPreview
                        && previewSeek
                        && malformedCompilerResponseRejected
                        && reviewWithoutStart
                        && candidateStales
                        && autoSuppressed
                        && transientOnly
                        && escapeClosesBoard
                        && minimizeClosesBoard
                        && sourceNavigationClosesBoard
                        && sourceChangeClosesStale
                        && sourceUntouched;
                    result = new
                    {
                        ok,
                        purePlanner,
                        planner24,
                        planner30,
                        planner60,
                        invalidPlannerInputs,
                        hiddenForImages,
                        videoEntry,
                        boardOpened,
                        externalStartsUnverified,
                        passiveOpen,
                        exactProbeAccepted,
                        halfOpenPreview,
                        previewFrames,
                        previewSeek,
                        malformedCompilerResponseRejected,
                        reviewWithoutStart,
                        candidateStales,
                        autoSuppressed,
                        transientOnly,
                        companionCalls,
                        pathResolverCalls,
                        pathResolverCallsAfterDrop,
                        escapeClosesBoard,
                        minimizeClosesBoard,
                        sourceNavigationClosesBoard,
                        sourceChangeClosesStale,
                        sourceUntouched,
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
                    try { window.Close(); } catch { }
                    foreach ((string key, string? value) in previousEnvironment)
                        Environment.SetEnvironmentVariable(key, value);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
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
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
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
            Shutdown(1);
        }
    }

    private static string FingerprintVideoEditV2Tree(string root)
    {
        if (!Directory.Exists(root))
            return "missing";
        return string.Join(
            "|",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                    $"{Path.GetRelativePath(root, path)}:{FingerprintVideoEditV2File(path)}"));
    }

    private static string FingerprintVideoEditV2File(string path)
    {
        if (!File.Exists(path))
            return "missing";
        byte[] bytes = File.ReadAllBytes(path);
        return $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }
}
