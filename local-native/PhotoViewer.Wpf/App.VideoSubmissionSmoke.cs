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
        int rewrites = 0, enqueues = 0, unexpectedPosts = 0;
        bool failRewrite = false, invalidRewrite = false, staleDuringRewrite = false, staleDuringHealth = false;
        string publishedPrompt = "";
        TaskCompletionSource? rewriteGate = null;
        var rewriteEntered = new TaskCompletionSource();
        var draft = VideoPromptAnnotation.BuiltIn(CreateVideoH3Candidate("Let the camera follow gently. The subject waves."));
        draft.Options.Single().Value.ChoiceIndex = 4;
        window.ConfigureModalEnhancementForSmoke(async (request, token) =>
        {
            string route = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && route == "/api/enhance/health")
            {
                if (staleDuringHealth) { staleDuringHealth = false; window.SetVideoPromptProgramForSmoke(draft); }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(CreateVideoV2HealthJson(true, true, "ready", null)) };
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/video-prompts/h3/rewrite")
            {
                rewrites++;
                rewriteEntered.TrySetResult();
                if (rewriteGate is not null) await rewriteGate.Task.WaitAsync(token);
                if (staleDuringRewrite)
                {
                    staleDuringRewrite = false;
                    var changed = draft.Clone(); changed.Template += " ";
                    window.SetVideoPromptProgramForSmoke(changed);
                }
                if (failRewrite) return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new { error = "Compiler unavailable", code = "H3_PROMPT_REWRITE_UNAVAILABLE" });
                return JsonResponse(HttpStatusCode.OK, new { candidatePrompt = invalidRewrite ? "invalid candidate" : CreateVideoH3Candidate("QUEUE_RESULT"),
                    rewriteRevision = "aibos-h3-i2va-local-v1", sourceSha256 = sourceHash });
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/jobs")
            {
                enqueues++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
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
        window.SetVideoEnhanceBeforeEnqueueForSmoke(false);
        window.SyncVideoGenerationSettingsForSmoke();
        window.UpdateLayout();
        checks["submissionAlwaysMeansEnqueue"] = window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加" && window.VideoGenerationQueueEnabledForSmoke;
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
        string direct = window.VideoPromptForSmoke;
        checks["uncheckedSubmissionEnqueuesWithoutAi"] = await window.SubmitVideoGenerationForSmokeAsync()
            && rewrites == 0 && enqueues == 1 && publishedPrompt == direct && publishedPrompt.Contains("orbit the camera left");
        window.OpenVideoPromptPreparationForSmoke();
        checks["preparationLinkIsPassive"] = rewrites == 0 && enqueues == 1;
        window.SetVideoEnhanceBeforeEnqueueForSmoke(true);
        rewriteGate = new TaskCompletionSource();
        Task<bool> automatic = window.SubmitVideoGenerationForSmokeAsync();
        await rewriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        checks["automaticEnhancementBlocksRepeatedSubmission"] = !window.VideoGenerationQueueEnabledForSmoke
            && !await window.SubmitVideoGenerationForSmokeAsync() && rewrites == 1 && enqueues == 1;
        rewriteGate.SetResult();
        checks["checkedSubmissionEnhancesAppliesAndEnqueuesOnce"] = await automatic && rewrites == 1 && enqueues == 2
            && publishedPrompt == CreateVideoH3Candidate("QUEUE_RESULT");
        rewriteGate = null;
        failRewrite = true;
        checks["enhancementFailureDoesNotEnqueueFallback"] = !await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 2 && window.VideoGenerationQueueEnabledForSmoke && !string.IsNullOrWhiteSpace(window.VideoGenerationStatusForSmoke);
        failRewrite = false;
        invalidRewrite = true;
        checks["invalidEnhancementDoesNotEnqueue"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2;
        invalidRewrite = false;
        staleDuringRewrite = true;
        checks["changedInputDuringEnhancementDoesNotEnqueue"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2;
        window.SetVideoPromptProgramForSmoke(draft);
        window.SyncVideoGenerationSettingsForSmoke();
        staleDuringHealth = true;
        checks["automaticSubmissionRevalidatesBeforePublication"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2;
        checks["automaticEnhancementCanRetry"] = await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3;
        window.SetVideoEnhanceBeforeEnqueueForSmoke(false);
        int rewritesBeforeOff = rewrites;
        checks["turningEnhancementOffRestoresDirectSubmission"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 4 && rewrites == rewritesBeforeOff && !publishedPrompt.Contains("QUEUE_RESULT") && unexpectedPosts == 0;
    }
}
