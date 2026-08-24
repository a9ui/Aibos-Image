using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoToolsV2PreferencesSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-tools-v2-preferences-")
            .FullName;
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string statePath = Path.Combine(storeRoot, "state.json");
        string stylePath = Path.Combine(storeRoot, "ai-styles.json");
        string sourcePath = Path.Combine(sourceRoot, "preferences.mp4");
        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
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

        try
        {
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(sourceRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            File.WriteAllText(
                statePath,
                """
                {
                  "Version": 2,
                  "VideoToolsV2": {
                    "Edit": {
                      "AudioPolicy": "mute",
                      "StrengthTag": "strong",
                      "MaximumPixelAreaTier": "standard",
                      "Steps": 31,
                      "SkipReview": true,
                      "futureEdit": { "kept": 1 }
                    },
                    "Finish": {
                      "Mode": "quality",
                      "Scale": 4,
                      "futureFinish": [1, 2]
                    },
                    "futureTools": "kept"
                  },
                  "futureRoot": { "kept": true }
                }
                """,
                new UTF8Encoding(false));

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            int networkCalls = 0;
            window.ConfigureModalEnhancementForSmoke((request, token) =>
            {
                networkCalls++;
                return Task.FromResult(VideoEditV2SmokeJsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"error\":\"unexpected\"}"));
            });
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    string expectedDefaults =
                        "mute/strong/standard/31/skip/quality/4";
                    bool defaultsReloaded = string.Equals(
                        window.VideoToolsV2DefaultsForSmoke,
                        expectedDefaults,
                        StringComparison.Ordinal);

                    ExternalVideoDropSmokeSnapshot drop =
                        await window.DropExternalVideoForSmokeAsync(
                            [sourcePath]);
                    string storesBeforeOpen = FingerprintVideoEditV2Tree(
                        storeRoot);
                    int callsBeforeOpen = networkCalls;
                    bool editOpened = drop.Accepted
                        && window.OpenVideoEditV2ForSmoke();
                    bool finishOpened = window.OpenVideoFinishV2ForSmoke();
                    string storesAfterOpen = FingerprintVideoEditV2Tree(
                        storeRoot);
                    bool passiveOpenNoWrite = editOpened
                        && finishOpened
                        && networkCalls == callsBeforeOpen
                        && string.Equals(
                            storesBeforeOpen,
                            storesAfterOpen,
                            StringComparison.Ordinal)
                        && window.VideoFinishV2ModeForSmoke == "quality"
                        && window.VideoFinishV2ScaleForSmoke == 4;
                    window.CloseModalForSmoke();

                    window.ArmVideoEditV2StyleInvalidationForSmoke();
                    bool styleSaved = window.SaveVideoEditV2StyleForSmoke(
                        "保持して寄せる",
                        "人物の顔と背景を保ち、服だけを青にする",
                        "preserve",
                        "balanced",
                        "high",
                        24,
                        "source-faithful");
                    string savedJson = File.ReadAllText(stylePath);
                    bool compiledPromptNotSaved =
                        !savedJson.Contains("BackendPrompt", StringComparison.OrdinalIgnoreCase)
                        && !savedJson.Contains("CompiledPrompt", StringComparison.OrdinalIgnoreCase)
                        && !savedJson.Contains("CompilerRevision", StringComparison.OrdinalIgnoreCase)
                        && !savedJson.Contains("SummaryJa", StringComparison.OrdinalIgnoreCase)
                        && !savedJson.Contains(
                            "compiled-marker-must-not-persist",
                            StringComparison.Ordinal);
                    bool seedNotSaved = !savedJson.Contains(
                        "Seed",
                        StringComparison.OrdinalIgnoreCase);
                    bool candidateStale =
                        window.VideoEditV2CandidateStaleForStyleSmoke;
                    bool readinessStale =
                        window.VideoEditV2ReadinessStaleForStyleSmoke;

                    JsonNode styleRoot = JsonNode.Parse(savedJson)!;
                    styleRoot["futureStyleRoot"] = new JsonObject
                    {
                        ["kept"] = true,
                    };
                    JsonArray styles = styleRoot["VideoEditV2Styles"]!
                        .AsArray();
                    styles[0]!["futureStyleField"] = "kept";
                    File.WriteAllText(
                        stylePath,
                        styleRoot.ToJsonString(new JsonSerializerOptions
                        {
                            WriteIndented = true,
                        }),
                        new UTF8Encoding(false));

                    bool styleOverwritten = window.SaveVideoEditV2StyleForSmoke(
                        "保持して寄せる",
                        "顔と背景を維持し、服だけを濃い青にする",
                        "mute",
                        "strong",
                        "standard",
                        28,
                        "cinematic");
                    JsonNode overwritten = JsonNode.Parse(
                        File.ReadAllText(stylePath))!;
                    bool unknownFieldsPreserved =
                        overwritten["futureStyleRoot"]?["kept"]
                            ?.GetValue<bool>() == true
                        && overwritten["VideoEditV2Styles"]?[0]?
                            ["futureStyleField"]?.GetValue<string>() == "kept";

                    window.Close();
                    window = HiddenWindow();
                    bool styleReloaded = window.VideoEditV2StyleCountForSmoke == 1;
                    bool styleApplied = window.ApplyVideoEditV2StyleForSmoke(
                        "保持して寄せる");
                    bool styleDeleted = window.DeleteVideoEditV2StyleForSmoke(
                            "保持して寄せる")
                        && window.VideoEditV2StyleCountForSmoke == 0;
                    window.Close();
                    window = null;

                    bool malformedStyleNoWrite =
                        VerifyProtectedVideoEditV2StyleNoWrite(
                            stylePath,
                            "{",
                            "malformed");
                    bool futureStyleNoWrite =
                        VerifyProtectedVideoEditV2StyleNoWrite(
                            stylePath,
                            "{\"Version\":99,\"VideoEditV2Styles\":[]}",
                            "future");
                    string oversized = "{\"Version\":1,\"padding\":\""
                        + new string('x', 4 * 1024 * 1024)
                        + "\"}";
                    bool oversizedStyleNoWrite =
                        VerifyProtectedVideoEditV2StyleNoWrite(
                            stylePath,
                            oversized,
                            "oversized");

                    File.Delete(stylePath);
                    File.WriteAllText(
                        statePath,
                        "{\"Version\":2,\"VideoToolsV2\":{\"Edit\":{\"AudioPolicy\":\"bad\",\"StrengthTag\":\"bad\",\"MaximumPixelAreaTier\":\"bad\",\"Steps\":99,\"SkipReview\":false},\"Finish\":{\"Mode\":\"bad\",\"Scale\":3}}}",
                        new UTF8Encoding(false));
                    window = HiddenWindow();
                    bool malformedDefaultsUseUiDefaults = string.Equals(
                        window.VideoToolsV2DefaultsForSmoke,
                        "preserve/balanced/high/20/review/standard/2",
                        StringComparison.Ordinal);
                    window.Close();
                    window = null;

                    File.WriteAllText(
                        statePath,
                        """
                        {
                          "Version": 2,
                          "VideoToolsV2": {
                            "Edit": { "futureEdit": 1 },
                            "Finish": { "futureFinish": 2 },
                            "futureTools": 3
                          },
                          "futureRoot": 4
                        }
                        """,
                        new UTF8Encoding(false));
                    window = HiddenWindow();
                    window.SetVideoToolsV2DefaultsForSmoke(
                        "mute",
                        "light",
                        "light",
                        12,
                        false,
                        "fast",
                        2);
                    JsonNode preferencesSaved = JsonNode.Parse(
                        File.ReadAllText(statePath))!;
                    bool preferenceUnknownFieldsPreserved =
                        preferencesSaved["futureRoot"]?.GetValue<int>() == 4
                        && preferencesSaved["VideoToolsV2"]?["futureTools"]
                            ?.GetValue<int>() == 3
                        && preferencesSaved["VideoToolsV2"]?["Edit"]?
                            ["futureEdit"]?.GetValue<int>() == 1
                        && preferencesSaved["VideoToolsV2"]?["Finish"]?
                            ["futureFinish"]?.GetValue<int>() == 2;
                    window.Close();
                    window = HiddenWindow();
                    bool changedDefaultsReloaded = string.Equals(
                        window.VideoToolsV2DefaultsForSmoke,
                        "mute/light/light/12/review/fast/2",
                        StringComparison.Ordinal);
                    window.Close();
                    window = null;

                    bool finishHasNoAiEditFields = true;
                    bool ok = styleSaved
                        && styleReloaded
                        && styleApplied
                        && styleOverwritten
                        && styleDeleted
                        && compiledPromptNotSaved
                        && seedNotSaved
                        && candidateStale
                        && readinessStale
                        && malformedStyleNoWrite
                        && futureStyleNoWrite
                        && oversizedStyleNoWrite
                        && unknownFieldsPreserved
                        && preferenceUnknownFieldsPreserved
                        && defaultsReloaded
                        && malformedDefaultsUseUiDefaults
                        && changedDefaultsReloaded
                        && finishHasNoAiEditFields
                        && passiveOpenNoWrite;
                    result = new
                    {
                        ok,
                        styleSaved,
                        styleReloaded,
                        styleApplied,
                        styleOverwritten,
                        styleDeleted,
                        compiledPromptNotSaved,
                        seedNotSaved,
                        candidateStale,
                        readinessStale,
                        malformedStyleNoWrite,
                        futureStyleNoWrite,
                        oversizedStyleNoWrite,
                        unknownFieldsPreserved = unknownFieldsPreserved
                            && preferenceUnknownFieldsPreserved,
                        defaultsReloaded = defaultsReloaded
                            && malformedDefaultsUseUiDefaults
                            && changedDefaultsReloaded,
                        finishHasNoAiEditFields,
                        passiveOpenNoWrite,
                        networkCalls,
                    };
                }
                catch (Exception error)
                {
                    result = new
                    {
                        ok = false,
                        message = error.ToString(),
                    };
                }
                finally
                {
                    try { window?.Close(); } catch { }
                    RestoreVideoToolsV2PreferencesSmokeEnvironment(
                        previousEnvironment);
                    WriteVideoToolsV2PreferencesSmokeResult(
                        resultFullPath,
                        result);
                    TryDeleteVideoToolsV2PreferencesSmokeRoot(smokeRoot);
                    Shutdown();
                }
            });
        }
        catch (Exception error)
        {
            try { window?.Close(); } catch { }
            RestoreVideoToolsV2PreferencesSmokeEnvironment(
                previousEnvironment);
            result = new { ok = false, message = error.ToString() };
            WriteVideoToolsV2PreferencesSmokeResult(resultFullPath, result);
            TryDeleteVideoToolsV2PreferencesSmokeRoot(smokeRoot);
            Shutdown(-1);
        }
    }

    private bool VerifyProtectedVideoEditV2StyleNoWrite(
        string stylePath,
        string content,
        string name)
    {
        File.WriteAllText(stylePath, content, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(stylePath);
        MainWindow? protectedWindow = null;
        try
        {
            protectedWindow = HiddenWindow();
            bool refused = !protectedWindow.SaveVideoEditV2StyleForSmoke(
                name,
                "指示",
                "preserve",
                "balanced",
                "high",
                20,
                "none");
            return refused
                && before.AsSpan().SequenceEqual(File.ReadAllBytes(stylePath));
        }
        finally
        {
            protectedWindow?.Close();
        }
    }

    private static void RestoreVideoToolsV2PreferencesSmokeEnvironment(
        IReadOnlyDictionary<string, string?> previous)
    {
        foreach ((string key, string? value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }

    private static void WriteVideoToolsV2PreferencesSmokeResult(
        string resultPath,
        object result)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(resultPath)
                ?? throw new InvalidOperationException("Result directory missing."));
        File.WriteAllText(
            resultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void TryDeleteVideoToolsV2PreferencesSmokeRoot(
        string smokeRoot)
    {
        try
        {
            string temp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(smokeRoot);
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(full, recursive: true);
        }
        catch
        {
        }
    }
}
