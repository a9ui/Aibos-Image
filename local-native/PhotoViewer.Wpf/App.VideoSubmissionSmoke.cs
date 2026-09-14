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
        bool failRewrite = false, staleDuringHealth = false;
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
                if (staleDuringHealth)
                {
                    staleDuringHealth = false;
                    window.SetVideoPromptProgramForSmoke(draft);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(CreateVideoV2HealthJson(true, true, "ready", null)) };
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/video-prompts/h3/rewrite")
            {
                rewrites++;
                rewriteEntered.TrySetResult();
                if (rewriteGate is not null) await rewriteGate.Task.WaitAsync(token);
                if (failRewrite) return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    new { error = "Compiler unavailable", code = "H3_PROMPT_REWRITE_UNAVAILABLE" });
                return JsonResponse(HttpStatusCode.OK, new { candidatePrompt = CreateVideoH3Candidate("QUEUE_RESULT"),
                    rewriteRevision = "aibos-h3-i2va-local-v1", sourceSha256 = sourceHash });
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/jobs")
            {
                enqueues++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                publishedPrompt = body.RootElement.GetProperty("video").GetProperty("requested").GetProperty("prompt").GetString()!;
                return BuildVideoToolsV2FlowAcceptedResponse(request, "synthetic-video-submission");
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
        window.SyncVideoGenerationSettingsForSmoke();
        window.UpdateLayout();
        checks["submissionAlwaysMeansEnqueue"] = window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加"
            && !window.VideoGenerationQueueEnabledForSmoke
            && !await window.SubmitVideoGenerationForSmokeAsync() && rewrites == 0 && enqueues == 0;
        checks["menuDetailsStartCollapsed"] = window.VideoMenuDetailsCollapsedForSmoke && !window.VideoPreparationExpandedForSmoke;
        checks["queueFooterStaysVisibleWhileScrolling"] = window.VideoSubmissionFooterFixedForSmoke();
        checks["readingModeSimplifiesH3WithoutChangingSource"] = window.VideoReadablePromptPreservesSourceForSmoke;
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-menu-overview", visual));
        capture("video-submit-prepare", window.VideoSubmissionGuidePanelForSmoke);
        window.OpenVideoPromptPreparationForSmoke();
        checks["preparationLinkIsPassive"] = window.VideoPreparationExpandedForSmoke && rewrites == 0 && enqueues == 0;
        string before = window.VideoPromptForSmoke;
        rewriteGate = new TaskCompletionSource();
        Task<bool> preparing = window.PrepareVideoPromptForSubmissionForSmokeAsync();
        await rewriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        checks["submissionPendingBlocksRepeatedClicks"] = !window.VideoGenerationQueueEnabledForSmoke
            && window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加"
            && !await window.PrepareVideoPromptForSubmissionForSmokeAsync()
            && !await window.SubmitVideoGenerationForSmokeAsync()
            && !await window.QueueVideoGenerationForSmokeAsync() && rewrites == 1 && enqueues == 0;
        rewriteGate.SetResult();
        checks["submissionPreparesWithoutApplyingOrEnqueueing"] = await preparing
            && window.VideoPromptForSmoke == before && enqueues == 0
            && window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加" && !window.VideoGenerationQueueEnabledForSmoke;
        rewriteGate = null;
        window.OpenVideoPromptPreparationForSmoke();
        checks["submissionReviewsExistingCandidateWithoutRewrite"] = !window.VideoGenerationQueueEnabledForSmoke
            && rewrites == 1 && enqueues == 0 && window.VideoH3PromptCandidateApplyEnabledForSmoke;
        window.UpdateLayout();
        capture("video-submit-review", window.VideoReviewPanelForSmoke);
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-submit-review-board", visual));
        window.SetVideoH3PromptCandidateForSmoke("invalid candidate");
        checks["submissionInvalidCandidateRequiresCorrection"] = !window.ApplyVideoCandidateAndShowSubmissionForSmoke()
            && !await window.SubmitVideoGenerationForSmokeAsync() && rewrites == 1 && enqueues == 0;
        window.SetVideoH3PromptCandidateForSmoke(CreateVideoH3Candidate("QUEUE_RESULT"));
        checks["submissionApplyEnablesQueueWithoutSubmitting"] = window.ApplyVideoCandidateAndShowSubmissionForSmoke()
            && window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加"
            && !window.VideoPreparationExpandedForSmoke && window.VideoGenerationQueueEnabledForSmoke && enqueues == 0;
        checks["readyQueueFooterStaysVisibleWhileScrolling"] = window.VideoSubmissionFooterFixedForSmoke();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-menu-ready", visual));
        staleDuringHealth = true;
        checks["submissionRejectsChangesBeforePublication"] = !await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 0 && window.VideoSubmissionActionForSmoke == "H3動画化をキューへ追加" && !window.VideoGenerationQueueEnabledForSmoke;
        failRewrite = true;
        before = window.VideoPromptForSmoke;
        checks["submissionPreparationFailureIsRetryable"] = !await window.PrepareVideoPromptForSubmissionForSmokeAsync()
            && window.VideoPromptForSmoke == before && enqueues == 0
            && !window.VideoGenerationQueueEnabledForSmoke && window.VideoH3PromptRewriteButtonEnabledForSmoke
            && !string.IsNullOrWhiteSpace(window.VideoGenerationStatusForSmoke);
        failRewrite = false;
        checks["submissionRetryReturnsToReview"] = await window.PrepareVideoPromptForSubmissionForSmokeAsync()
            && window.ApplyVideoCandidateAndShowSubmissionForSmoke() && enqueues == 0;
        checks["submissionExplicitQueuePublishesResolvedPromptOnce"] = await window.SubmitVideoGenerationForSmokeAsync()
            && enqueues == 1 && publishedPrompt == CreateVideoH3Candidate("QUEUE_RESULT")
            && !publishedPrompt.Contains("orbit the camera") && unexpectedPosts == 0;
    }
}
