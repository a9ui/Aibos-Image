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
        foreach (string fixtureName in new[] { "VideoPromptEnhancementFixtures.json", "VideoPromptEnrichmentFixtures.json" })
        using (var stream = typeof(App).Assembly.GetManifestResourceStream(fixtureName)!)
        using (var fixtures = JsonDocument.Parse(stream))
            foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
                checks["promptEnhancementProtocol_" + item.GetProperty("name").GetString()] =
                    VideoPromptEnhancement.IsValid(item.GetProperty("value")) == item.GetProperty("accepted").GetBoolean();
        int rewrites = 0, enqueues = 0, unexpectedPosts = 0;
        bool staleDuringHealth = false, enhancementCapability = true;
        JsonElement lastRequested = default;
        TaskCompletionSource? publicationGate = null;
        var publicationEntered = new TaskCompletionSource();
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
                    { Content = new StringContent(enhancementCapability ? CreateVideoV2HealthJson(true, true, "ready", null) : CreateVideoV2HealthJson(true, true, "ready", null).Replace("videoPromptEnhancementV2", "unsupportedCapability", StringComparison.Ordinal)) };
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
                publicationEntered.TrySetResult();
                if (publicationGate is not null) await publicationGate.Task.WaitAsync(token);
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
        checks["preservingEnhancementHasPinnedAudioAndNoMetadataByDefault"] = lastRequested.GetProperty("promptEnhancement").GetProperty("schemaVersion").GetInt32() == 2
            && lastRequested.GetProperty("promptEnhancement").GetProperty("instruction").GetString() == direct
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("referencePrompt").GetString() == ""
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("dialogue").GetString() == "preserve";
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
        window.SetVideoPromptProgramForSmoke(new VideoPromptProgram { UseSourceVariants = true, BaseH3Template = speechPrompt, PhotorealBaseH3Template = speechPrompt });
        window.SyncVideoGenerationSettingsForSmoke();
        window.SelectActingForSmoke("approach", "annoyed", "lighthearted", "behind-back");
        string actingPrompt = window.VideoPromptForSmoke;
        checks["actingSelectorsWorkForLegacyStylesWithoutAi"] = actingPrompt.Contains("first one to two seconds")
            && actingPrompt.Contains("behind her lower back") && actingPrompt.Contains("hand movements required by the main action take priority")
            && actingPrompt.Contains("mildly annoyed") && actingPrompt.Contains("lighthearted")
            && actingPrompt.Contains("<d>[Japanese]こんにちは。</d>") && rewrites == 0;
        checks["actingSelectionEnqueuesWithoutAi"] = await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 8 && publishedPrompt == actingPrompt;
        window.SelectActingForSmoke("step-back", "bright", "relaxed", "lower");
        checks["actingChangeRebuildsFromDraftWithoutDuplicateDirections"] =
            window.VideoPromptForSmoke.Split("Opening movement, immediately", StringSplitOptions.None).Length == 2
            && !window.VideoPromptForSmoke.Contains("behind her lower back");
        window.SelectActingForSmoke("original", "original", "original", "original");
        checks["actingResetInRealSubmissionPathRestoresExactSource"] = window.VideoPromptForSmoke == speechPrompt;
        window.OpenVideoGenerationBoardForSmoke("original");
        window.Height = 1020;
        window.UpdateLayout();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-studio", visual));
        window.OpenVideoSubmissionPreviewForSmoke();
        checks["separateStyleManagementIsPassiveAndRestoresControls"] = window.VerifyVideoStyleManagementForSmoke(v => capture("video-style-management", v))
            && enqueues == 8 && unexpectedPosts == 0;
        // Closing or canceling during health validation must still invalidate
        // the draft; request-pending is earlier than durable publication.
        foreach (bool enhance in new[] { false, true })
        foreach (bool close in new[] { false, true })
        {
            window.OpenVideoGenerationBoardForSmoke("original");
            window.SetVideoEnhanceAtExecutionForSmoke(enhance);
            healthGate = new TaskCompletionSource();
            healthEntered = new TaskCompletionSource();
            Task<bool> pending = window.SubmitVideoGenerationForSmokeAsync();
            await healthEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool cancelAvailable = window.VideoSubmissionCancelVisibleForSmoke;
            if (close) window.CloseVideoSubmissionForSmoke();
            else window.CancelVideoSubmissionForSmoke();
            healthGate.SetResult();
            checks[$"{(enhance ? "enhanced" : "direct")}{(close ? "Close" : "Cancel")}BeforePublicationAddsNothing"] =
                cancelAvailable && !await pending && enqueues == 8 && rewrites == 0;
            healthGate = null;
        }
        window.OpenVideoGenerationBoardForSmoke("original");
        checks["canceledRegistrationCanBeExplicitlyRetried"] =
            await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 9;
        // Once durable publication begins, an editor close or change belongs to
        // the next draft. It cannot invalidate the accepted request or receipt.
        foreach (bool enhance in new[] { false, true })
        {
            window.OpenVideoGenerationBoardForSmoke("original");
            window.SetVideoEnhanceAtExecutionForSmoke(enhance);
            publicationGate = new TaskCompletionSource();
            publicationEntered = new TaskCompletionSource();
            Task<bool> publishing = window.SubmitVideoGenerationForSmokeAsync();
            await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string acceptedSnapshot = lastRequested.GetRawText();
            int acceptedCount = enqueues;
            window.SetVideoPromptProgramForSmoke(draft);
            window.CloseVideoSubmissionForSmoke();
            window.CancelVideoSubmissionForSmoke();
            publicationGate.SetResult();
            bool accepted = await publishing;
            window.OpenVideoGenerationBoardForSmoke("original");
            checks[$"{(enhance ? "enhanced" : "direct")}PublishedRequestSurvivesEditCloseAndReopen"] =
                accepted && enqueues == acceptedCount && lastRequested.GetRawText() == acceptedSnapshot && unexpectedPosts == 0;
            publicationGate = null;
        }
        int beforeOptions = enqueues;
        window.SetVideoEnhanceAtExecutionForSmoke(true);
        window.ConfigureVideoEnrichmentForSmoke(true, true, true, 2, false);
        window.UpdateLayout();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-enrichment-settings", visual));
        checks["enrichmentControlsArePassive"] = enqueues == beforeOptions && rewrites == 0;
        checks["enrichmentControlsCapturedWithoutInference"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == beforeOptions + 1 && rewrites == 0
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("dialogue").GetString() == "auto"
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("music").GetString() == "off"
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("speechAmount").GetInt32() == 2;
        window.ConfigureVideoEnrichmentForSmoke(false, false, true, 1, true);
        var timed = draft.Clone();
        timed.CaptureModeId = "pov";
        timed.DirectionPhases = [new() { EndMillionths = 333333, ExpressionId = "surprised", CameraId = "front", PositionId = "approach-stop", GazeId = "viewer" },
            new() { EndMillionths = 666667, ExpressionId = "suspicious", CameraId = "upper-body", PositionId = "stay", GazeId = "downcast" },
            new() { ExpressionId = "calm", CameraId = "face", PositionId = "stay", GazeId = "down-then-viewer" }];
        window.SetVideoPromptProgramForSmoke(timed);
        checks["sharedTimelineEnqueuesWithoutWaitingForAiAndKeepsActualDuration"] = await window.SubmitVideoGenerationForSmokeAsync()
            && rewrites == 0 && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("timeline").GetArrayLength() == 3
            && lastRequested.GetProperty("promptEnhancement").GetProperty("options").GetProperty("timeline")[2].GetProperty("endMs").GetInt32() == 5166
            && publishedPrompt.Contains("From 3.444 to 5.166 seconds:") && publishedPrompt.Contains("same observer's first-person viewpoint")
            && publishedPrompt.Contains("subject moves a short distance toward the viewer") && publishedPrompt.Contains("subject lowers the gaze");
        window.RevealVideoTimelineForSmoke(); window.UpdateLayout();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-timeline-settings", visual));
        window.SetVideoPromptProgramForSmoke(draft);
    }
}
