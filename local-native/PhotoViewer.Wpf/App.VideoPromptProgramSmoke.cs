using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoPromptProgramSmoke(string resultPath)
    {
        string root = Directory.CreateTempSubdirectory("aibos-prompt-program-smoke-").FullName;
        var paths = new Dictionary<string, string>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = "state.json", ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = "favorites.json",
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = "seen.json", ["PHOTOVIEWER_WPF_RECENT_PATH"] = "recent.json",
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = "settings.json", ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = "albums.json",
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = "search.json", ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = "metadata",
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = "enhance/jobs.json",
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = "outputs", ["AIBOS_SHARED_ROOT_LOCATOR_PATH"] = "locator.json",
        };
        var previous = paths.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var pair in paths) Environment.SetEnvironmentVariable(pair.Key, Path.Combine(root, pair.Value));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        var checks = new Dictionary<string, bool>();
        string failure = "";
        try
        {
            string sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            string sourcePath = Path.Combine(sourceRoot, "synthetic.png");
            byte[] source = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            File.WriteAllBytes(sourcePath, source);
            string sourceHash = Convert.ToHexStringLower(SHA256.HashData(source));
            Directory.CreateDirectory(Path.Combine(root, "enhance"));
            File.WriteAllText(Path.Combine(root, "enhance/jobs.json"), "{\"jobs\":[]}");
            byte[] jobs = File.ReadAllBytes(Path.Combine(root, "enhance/jobs.json"));

            var program = new VideoPromptProgram
            {
                Enabled = true, Template = "[look toward the camera] smile {walk / run / jump}",
                PhotorealTemplate = "[look toward the camera] wave gently {walk / run / jump}",
                Description = "NOTE_MUST_NOT_REACH_MODEL", ActionSamples = "SAMPLE_MUST_NOT_REACH_MODEL",
                PreferredLoraId = "LORA_NOTE_MUST_NOT_REACH_MODEL", SourceRules = true,
            };
            program.Options["[look toward the camera]"] = new() { Mode = "off" };
            program.Options["{walk / run / jump}"] = new() { Mode = "on", ChoiceIndex = 1 };
            checks["manualOnlyChosenText"] = program.TryCompile("anime", "camera", 124, out string compiled, out _)
                && compiled.StartsWith(" smile run", StringComparison.Ordinal)
                && !compiled.Contains("look toward") && !compiled.Contains("jump")
                && !compiled.Contains("MUST_NOT_REACH_MODEL");
            program.ImageChoices = true;
            checks["manualChoiceOverridesAi"] = program.TryCompile("anime", "", 124, out compiled, out _)
                && !compiled.Contains("Choose exactly one");
            program.Options["{walk / run / jump}"].Mode = "auto";
            checks["aiHasBoundedAlternatives"] = program.TryCompile("anime", "", 243, out compiled, out _)
                && compiled.Contains("Choose exactly one") && compiled.Contains("jump");
            program.ActionPlot = true; program.PhysicalContinuity = true;
            checks["plotAndPhysicsIndependent"] = program.TryCompile("photoreal", "", 294, out compiled, out _)
                && compiled.Contains("12.250") && compiled.Contains("SAMPLE_MUST_NOT_REACH_MODEL")
                && compiled.Contains("Physical continuity") && compiled.StartsWith(" wave gently")
                && !compiled.Contains("NOTE_MUST_NOT_REACH_MODEL");
            var condition = new VideoPromptOption { Mode = "auto", Condition = "absent", Keyword = "rain", DefaultOn = false };
            checks["unknownIsNotAbsent"] = !program.IsOn(condition, null) && !program.IsOn(condition, "RAIN") && program.IsOn(condition, "sun");
            condition.Condition = "has-prompt";
            checks["promptPresence"] = program.IsOn(condition, "sun") && !program.IsOn(condition, "");
            condition.Condition = "no-prompt";
            checks["promptAbsence"] = program.IsOn(condition, "") && !program.IsOn(condition, null);
            program.SourceRules = false;
            checks["rulesOffUsesFallback"] = !program.IsOn(condition, "");
            checks["bracketGrammar"] = VideoPromptLanguage.TryParse("［look] smile ｛walk / run}", out var tokens, out _)
                && tokens.Count == 3 && tokens[0].Kind == '[' && tokens[2].Choices.Count == 2;
            checks["invalidGrammarBlocked"] = new[] { "[unclosed", "[a{b/c}]", "{a/}", "[Shot 1]", "x]" }
                .All(s => !VideoPromptLanguage.TryParse(s, out _, out _));
            checks["escapeLiteral"] = VideoPromptLanguage.TryParse(@"\[literal\]", out tokens, out _) && tokens.Single().Text == "[literal]";
            var camera = new VideoPromptProgram { Enabled = true, Template = "smile [fixed camera / orbit left / subtle first-person head motion]" };
            camera.Options["[fixed camera / orbit left / subtle first-person head motion]"] = new() { ChoiceIndex = 2 };
            checks["inlineManualCamera"] = camera.TryCompile("anime", "", 124, out string cameraPrompt, out _)
                && cameraPrompt.StartsWith("smile subtle first-person head motion") && !cameraPrompt.Contains("orbit left");
            camera.BaseH3Template = "BASE [Shot 1]"; camera.PhotorealBaseH3Template = "PHOTO [Shot 1]";
            checks["h3BaseNotParsedAsOptions"] = camera.TryCompile("photoreal", "", 124, out cameraPrompt, out _)
                && cameraPrompt.StartsWith("PHOTO [Shot 1]") && !cameraPrompt.Contains("BASE");
            checks["futureAndDuplicateBlocked"] = !VideoPromptProgram.TryRead(JsonSerializer.SerializeToElement(new { Version = 2 }), out _)
                && !VideoPromptProgram.TryRead(JsonDocument.Parse("{\"Version\":1,\"Version\":2}").RootElement, out _);
            program.ExtensionData = new() { ["FutureNote"] = JsonSerializer.SerializeToElement(new { Keep = true }) };
            checks["unknownFieldsPreserved"] = VideoPromptProgram.TryRead(program.Snapshot(), out var restored)
                && restored.Snapshot().GetProperty("FutureNote").GetProperty("Keep").GetBoolean();
            var tooLong = program.Clone(); tooLong.Template = new string('字', 7999);
            checks["expandedBounds"] = !tooLong.TryCompile("anime", "", 124, out _, out _);

            window = HiddenWindow(); window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                int rewriteCalls = 0, otherPosts = 0, starts = 0;
                bool changeDuringRewrite = false;
                string sentPrompt = "";
                try
                {
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        string path = request.RequestUri?.AbsolutePath ?? "";
                        if (request.Method == HttpMethod.Get && path == "/api/enhance/health")
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CreateVideoV2HealthJson(true, true, "ready", null)) };
                        if (request.Method == HttpMethod.Post && path == "/api/enhance/video-prompts/h3/rewrite")
                        {
                            rewriteCalls++;
                            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                            sentPrompt = body.RootElement.GetProperty("prompt").GetString()!;
                            if (changeDuringRewrite)
                            {
                                var changed = program.Clone(); changed.ActionPlot = !program.ActionPlot;
                                window.SetVideoPromptProgramForSmoke(changed, "photoreal");
                            }
                            return JsonResponse(HttpStatusCode.OK, new { candidatePrompt = CreateVideoH3Candidate("PROGRAM_RESULT"),
                                rewriteRevision = "aibos-h3-i2va-local-v1", sourceSha256 = sourceHash });
                        }
                        if (request.Method != HttpMethod.Get) otherPosts++;
                        return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected route" });
                    });
                    window.EnableEnhancementCompanionAutoStartProbeForSmoke(_ => { starts++; return (false, "Must not start a companion"); });
                    await window.LoadFolderAsync(sourceRoot);
                    checks["sourceSelected"] = window.SelectFileNameForSmoke("synthetic.png") && window.OpenVideoGenerationBoardForSmoke("original");
                    window.SelectVideoModelForSmoke("minimax-h3");
                    window.SetMiniMaxH3CapabilityForSmoke(true, true, null);
                    window.SetVideoPromptProgramForSmoke(program, "photoreal");
                    checks["temporaryOverride"] = window.VideoPromptProgramKindForSmoke == "photoreal";
                    checks["uncompiledEnqueueBlocked"] = window.VideoPromptProgramEnqueueErrorForSmoke is not null
                        && !await window.QueueVideoGenerationForSmokeAsync() && otherPosts == 0;
                    checks["programRewriteApplied"] = await window.RewriteVideoPromptProgramForSmokeAsync()
                        && window.VideoH3PromptCandidateApplyEnabledForSmoke
                        && window.ApplyVideoH3PromptCandidateForSmoke()
                        && window.VideoPromptProgramEnqueueErrorForSmoke is null;
                    checks["requestUsesVariantAndNoNotes"] = sentPrompt.StartsWith(" wave gently") && !sentPrompt.Contains("NOTE_MUST_NOT_REACH_MODEL");
                    checks["explicitMetadataDistinguishesNoPrompt"] = window.VideoPromptProgramSourcePromptForSmoke == "";
                    changeDuringRewrite = true;
                    checks["changedProgramRejectsPendingCandidate"] = !await window.RewriteVideoPromptProgramForSmokeAsync()
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke && window.VideoPromptProgramEnqueueErrorForSmoke is not null;
                    window.ResetVideoProgramOverrideForSmoke();
                    checks["overrideNotRemembered"] = window.VideoPromptProgramKindForSmoke == "anime" && window.VideoPromptProgramEnqueueErrorForSmoke is not null;
                    window.SetVideoPromptProgramForSmoke(program);
                    checks["styleSaved"] = window.SaveVideoStyleForSmoke("Synthetic authoring style");
                    for (int i = 0; i < 40; i++) window.SaveVideoStyleForSmoke($"Synthetic style {i:00}");
                    checks["styleCountHasNo32ItemCap"] = window.VideoStyleNamesForSmoke.Count == 41;
                    window.FlushStateForSmoke();
                    MainWindow reload = HiddenWindow();
                    checks["styleRoundTrip"] = reload.SelectVideoStyleForSmoke("Synthetic authoring style")
                        && reload.VideoStyleNamesForSmoke.Count == 41
                        && reload.VideoPromptProgramSnapshotForSmoke.GetProperty("Description").GetString() == program.Description
                        && reload.VideoPromptProgramSnapshotForSmoke.GetProperty("FutureNote").GetProperty("Keep").GetBoolean();
                    reload.Close();
                    var editorProgram = program.Clone(); editorProgram.Options.Clear();
                    var editor = new VideoPromptEditorWindow(editorProgram, "auto", "anime", "camera")
                    { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                    editor.Show(); editor.UpdateLayout();
                    void Capture(string name, FrameworkElement visual)
                    {
                        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        var drawing = new DrawingVisual();
                        using (DrawingContext dc = drawing.RenderOpen())
                            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                        bitmap.Render(drawing);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath))!, name + ".png"));
                        encoder.Save(stream);
                    }
                    checks["clickOpensOptionAndUpdatesText"] = editor.ExerciseManualOptionClickForSmoke(v => Capture("prompt-option", v));
                    editor.CaptureTabsForSmoke((i, v) => Capture(i == 0 ? "prompt-editor" : "prompt-tab-" + i, v));
                    editor.Close();
                    checks["unifiedStyleLibrary"] = window.VideoPromptTemplateSurfaceForSmoke
                        && window.SelectVideoPromptTemplateForSmoke("cinematic-camera")
                        && !window.VideoPromptProgramSnapshotForSmoke.GetProperty("Enabled").GetBoolean()
                        && window.SelectVideoStyleForSmoke("Synthetic authoring style");
                    var inlineProgram = new VideoPromptProgram { Enabled = true,
                        Template = "Camera: [slow push-in / orbit left / POV with gentle head movement].\n\nThe subject [smiles / looks curious] and {walks closer / turns / waves}.",
                        Description = "カメラの動きと表情を、本文の中で選べます。日本語訳は生成へ送りません。" };
                    window.SetVideoPromptProgramForSmoke(inlineProgram);
                    window.OpenModalForSmoke();
                    window.OpenVideoGenerationBoardForSmoke("original");
                    checks["nativeInlineEditingAndReversibleOff"] = window.ExerciseVideoAuthoringForSmoke(Capture);
                    const string originalWithNotes = "Original body [Shot 1].\r\n\r\n▼▼▼ 使用時はこの行から末尾まで全削除｜日本語訳 ▼▼▼\r\n説明はそのまま。\r\n";
                    window.SelectVideoPromptTemplateForSmoke("dynamic-general");
                    window.ConfigureVideoGenerationForSmoke(5, 24, 414720, originalWithNotes);
                    var separated = window.VideoPromptProgramSnapshotForSmoke;
                    checks["legacyNotesSeparatedLosslessly"] = window.VideoPromptForSmoke == "Original body [Shot 1].\r\n\r\n"
                        && separated.GetProperty("Description").GetString() == "説明はそのまま。\r\n"
                        && separated.GetProperty("OriginalStyleText").GetProperty("Prompt").GetString() == originalWithNotes;
                    window.SelectInlineVideoSourceForSmoke("photoreal");
                    var sourceProgram = window.VideoPromptProgramSnapshotForSmoke.Deserialize<VideoPromptProgram>()!;
                    checks["inlineSourceOverridePreservesLiteralBody"] = sourceProgram.Enabled
                        && window.VideoPromptProgramKindForSmoke == "photoreal"
                        && sourceProgram.TryCompile("photoreal", "", 124, out string sourceInstruction, out _)
                        && sourceInstruction.StartsWith("Original body [Shot 1].\r\n\r\n", StringComparison.Ordinal)
                        && !sourceInstruction.Contains("説明はそのまま", StringComparison.Ordinal);
                    string stylePath = Path.GetFullPath(window.AiStylePathForSmoke);
                    if (!stylePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Style fixture escaped its isolated root.");
                    var future = JsonNode.Parse(File.ReadAllText(stylePath))!.AsObject();
                    future["VideoStyles"]![0]!["InstructionProgram"]!["Version"] = 2;
                    File.WriteAllText(stylePath, future.ToJsonString());
                    byte[] protectedStyle = File.ReadAllBytes(stylePath);
                    _ = window.SaveVideoStyleForSmoke("Must not overwrite future data");
                    checks["futureProgramProtectsStyleFile"] = window.AiStyleWriteBlockedForSmoke
                        && protectedStyle.SequenceEqual(File.ReadAllBytes(stylePath));
                    checks["sourceAndJobsUnchanged"] = source.SequenceEqual(File.ReadAllBytes(sourcePath)) && jobs.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "enhance/jobs.json")));
                    checks["noWorkerOrJobMutation"] = starts == 0 && otherPosts == 0 && rewriteCalls == 2;
                }
                catch (Exception ex) { failure = ex.ToString(); }
                finally
                {
                    window.Close();
                    foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                    bool ok = failure.Length == 0 && checks.Values.All(v => v);
                    File.WriteAllText(resultPath, JsonSerializer.Serialize(new { ok, checks, failure, fixtureRoot = root }, new JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(ok ? 0 : 1);
                }
            });
        }
        catch (Exception ex)
        {
            window?.Close();
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { ok = false, checks, failure = ex.ToString() }));
            Shutdown(1);
        }
    }
}
