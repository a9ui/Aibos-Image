using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class App
{
    private static async Task VerifyVideoSubmissionFlowAsync(MainWindow window, string sourceHash,
        Dictionary<string, bool> checks, Action<string, FrameworkElement> capture)
    {
        using (var stream = typeof(App).Assembly.GetManifestResourceStream("VideoPromptEnhancementFixtures.json")!)
        using (var fixtures = JsonDocument.Parse(stream))
            foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
                checks["promptEnhancementProtocol_" + item.GetProperty("name").GetString()] =
                    VideoPromptEnhancement.IsValid(item.GetProperty("value")) == item.GetProperty("accepted").GetBoolean();
        int rewrites = 0, enqueues = 0, unexpectedPosts = 0;
        bool staleDuringHealth = false, enhancementCapability = true;
        JsonElement lastRequested = default;
        TaskCompletionSource? healthGate = null;
        var healthEntered = new TaskCompletionSource();
        string publishedPrompt = "";
        var draft = VideoPromptAnnotation.BuiltIn(CreateVideoH3Candidate("Let the camera follow gently. The subject waves."));
        draft.Options.Single().Value.ChoiceIndex = 4;
        window.ConfigureModalEnhancementForSmoke(async (request, token) =>
        {
            string route = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && route == "/api/enhance/health")
            {
                healthEntered.TrySetResult();
                if (healthGate is not null) await healthGate.Task.WaitAsync(token);
                if (staleDuringHealth) { staleDuringHealth = false; window.SetVideoPromptProgramForSmoke(draft); }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(enhancementCapability ? CreateVideoV2HealthJson(true, true, "ready", null) : CreateVideoV2HealthJson(true, true, "ready", null).Replace("videoPromptEnhancementV1", "unsupportedCapability", StringComparison.Ordinal)) };
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/video-prompts/h3/rewrite")
            {
                rewrites++;
                return JsonResponse(HttpStatusCode.ServiceUnavailable, new { error = "AI must never run during enqueue" });
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/jobs")
            {
                enqueues++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                lastRequested = body.RootElement.GetProperty("video").GetProperty("requested").Clone();
                publishedPrompt = body.RootElement.GetProperty("video").GetProperty("requested").GetProperty("prompt").GetString()!;
                return BuildVideoToolsV2FlowAcceptedResponse(request, "synthetic-video-submission-" + enqueues);
            }
            if (request.Method == HttpMethod.Get && route == "/api/enhance/jobs")
                return JsonResponse(HttpStatusCode.OK, new { jobs = Array.Empty<object>() });
            if (request.Method != HttpMethod.Get) unexpectedPosts++;
            return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected route" });
        });
        window.SetUiLanguageForSmoke(UiLanguageResources.Japanese);
        window.SelectVideoPromptTemplateForSmoke("cinematic-camera");
        window.OpenVideoGenerationBoardForSmoke("original");
        window.SetVideoPromptProgramForSmoke(draft);
        window.SetVideoEnhanceAtExecutionForSmoke(false);
        window.SyncVideoGenerationSettingsForSmoke();
        window.UpdateLayout();
        checks["submissionAlwaysMeansEnqueue"] = window.VideoSubmissionActionForSmoke == "キューに追加" && window.VideoGenerationQueueEnabledForSmoke;
        checks["menuDetailsStartCollapsed"] = window.VideoMenuDetailsCollapsedForSmoke && !window.VideoPreparationExpandedForSmoke;
        checks["queueFooterStaysVisibleWhileScrolling"] = window.VideoSubmissionFooterFixedForSmoke();
        checks["annotatedReadingShowsCompleteH3Structure"] = window.VideoFullH3PromptPreservesSourceForSmoke;
        string speechPrompt = MiniMaxH3I2vaPromptConformance.Opening + MiniMaxH3I2vaPromptConformance.IntegratedPrefix
            + "The subject waves and says <d>[Japanese]こんにちは。</d>."
            + MiniMaxH3I2vaPromptConformance.SoundscapePrefix + "Quiet room ambience."
            + MiniMaxH3I2vaPromptConformance.MusicPrefix + "N/A";
        var literalEditor = new VideoPromptAuthoringControl();
        literalEditor.Load(new(), speechPrompt, "auto", "anime", null);
        checks["literalReadingPreservesCompleteH3AndJapaneseDialogue"] = literalEditor.FullH3PreservesSourceForSmoke()
            && literalEditor.ReadingTextForSmoke.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') == speechPrompt;
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-menu-ready", visual));
        window.VerifyVideoMenuScrollingForSmoke(checks, capture);
        checks["studioHasOneEditorAndVisibleDurationQuality"] = window.VideoStudioLayoutForSmoke;
        window.OpenVideoSubmissionPreviewForSmoke();
        string direct = window.VideoPromptForSmoke;
        checks["resolvedPreviewIsPassive"] = window.VideoResolvedPreviewForSmoke == direct && rewrites == 0 && enqueues == 0;
        string draftBefore = window.VideoPromptProgramSnapshotForSmoke.GetRawText();
        checks["uncheckedSubmissionEnqueuesWithoutAi"] = await window.SubmitVideoGenerationForSmokeAsync()
            && rewrites == 0 && enqueues == 1 && publishedPrompt == direct && publishedPrompt.Contains("orbit the camera left");
        window.OpenVideoSubmissionPreviewForSmoke();
        checks["preparationLinkIsPassive"] = rewrites == 0 && enqueues == 1;
        window.SetVideoEnhanceAtExecutionForSmoke(true);
        healthGate = new TaskCompletionSource();
        healthEntered = new TaskCompletionSource();
        Task<bool> queued = window.SubmitVideoGenerationForSmokeAsync();
        await healthEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        checks["registrationBlocksDuplicateClickWithoutCallingAi"] = !window.VideoGenerationQueueEnabledForSmoke
            && !await window.SubmitVideoGenerationForSmokeAsync() && rewrites == 0 && enqueues == 1;
        healthGate.SetResult();
        checks["checkedSubmissionImmediatelyEnqueuesDeferredEnhancement"] = await queued && rewrites == 0 && enqueues == 2
            && publishedPrompt == direct && VideoPromptEnhancement.IsValid(lastRequested.GetProperty("promptEnhancement"));
        checks["deferredInstructionIncludesManualSelection"] = lastRequested.GetProperty("promptEnhancement").GetProperty("instruction").GetString()!.Contains("orbit the camera left");
        checks["queuedEnhancementKeepsEditorAndProgramUnchanged"] = window.VideoPromptForSmoke == direct
            && window.VideoPromptProgramSnapshotForSmoke.GetRawText() == draftBefore;
        healthGate = null;
        enhancementCapability = false;
        checks["oldCompanionCannotSilentlyIgnoreEnhancement"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2 && rewrites == 0;
        enhancementCapability = true;
        staleDuringHealth = true;
        checks["deferredSubmissionRevalidatesBeforePublication"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2;
        checks["deferredSubmissionCanRetry"] = await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3 && rewrites == 0;
        window.SetVideoEnhanceAtExecutionForSmoke(false);
        checks["turningEnhancementOffRestoresDirectSubmission"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 4 && rewrites == 0 && !lastRequested.TryGetProperty("promptEnhancement", out _);
        // Literal styles also keep their source and variant base; automatic
        // enhancement must never use the legacy Apply-to-editor path.
        var literalProgram = new VideoPromptProgram { UseSourceVariants = true, BaseH3Template = speechPrompt };
        window.SetVideoPromptProgramForSmoke(literalProgram);
        window.SyncVideoGenerationSettingsForSmoke();
        string literalBefore = window.VideoPromptProgramSnapshotForSmoke.GetRawText();
        window.SetVideoEnhanceAtExecutionForSmoke(true);
        checks["literalDeferredEnhancementPreservesBase"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 5 && rewrites == 0 && lastRequested.GetProperty("promptEnhancement").GetProperty("instruction").GetString()!.Contains("<d>[Japanese]こんにちは。</d>") && window.VideoPromptForSmoke == speechPrompt
            && window.VideoPromptProgramSnapshotForSmoke.GetRawText() == literalBefore;
        window.SetVideoEnhanceAtExecutionForSmoke(false);
        checks["literalOffDoesNotReusePriorEnhancement"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 6 && publishedPrompt == speechPrompt;
        var imperfect = VideoPromptAnnotation.BuiltIn(speechPrompt.Replace(MiniMaxH3I2vaPromptConformance.Opening, "", StringComparison.Ordinal));
        window.SetVideoPromptProgramForSmoke(imperfect);
        window.SyncVideoGenerationSettingsForSmoke();
        checks["formatDiagnosticNeverRequiresAiToEnqueue"] = window.VideoGenerationQueueEnabledForSmoke
            && await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 7 && rewrites == 0
            && !lastRequested.TryGetProperty("promptEnhancement", out _) && publishedPrompt.Contains("<d>[Japanese]こんにちは。</d>");
        window.OpenVideoGenerationBoardForSmoke("original");
        window.Height = 1020;
        window.UpdateLayout();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-studio", visual));
        window.OpenVideoSubmissionPreviewForSmoke();
        checks["separateStyleManagementIsPassiveAndRestoresControls"] = window.VerifyVideoStyleManagementForSmoke(v => capture("video-style-management", v))
            && enqueues == 7 && unexpectedPosts == 0;
    }
}
