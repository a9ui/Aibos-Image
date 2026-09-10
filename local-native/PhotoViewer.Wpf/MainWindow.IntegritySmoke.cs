using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    public async Task<bool> ApiOnlyUnavailableHealthForSmokeAsync()
    {
        bool listening = false;
        int starts = 0;
        int healthReads = 0;
        int mutations = 0;
        ConfigureEnhancementCompanionAutoStartForSmoke(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/enhance/identity")
            {
                if (!listening) throw new HttpRequestException("Synthetic API not started.");
                string challenge = request.Headers.GetValues(EnhancementCompanionChallengeHeader).Single();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(
                        EnhancementCompanionIdentityPayloadForSmoke(challenge))),
                };
            }
            var inner = await DecodeEnhancementCompanionSecureRequestForSmokeAsync(request, token);
            if (inner?.Method == "GET" && inner.PathAndQuery == "/api/enhance/health")
                healthReads++;
            else mutations++;
            return EnhancementCompanionSecureResponseForSmoke(request, 503,
                new { error = "Synthetic store unavailable.", code = "QUEUE_HEALTH_UNAVAILABLE" });
        }, _ => { starts++; listening = true; return (true, ""); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        EnhancementApiResponse response = await EnsureEnhancementCompanionApiReadyAsync(token: timeout.Token);
        return !timeout.IsCancellationRequested && !response.Ok && response.StatusCode == 503
            && starts == 1 && healthReads == 1 && mutations == 0;
    }

    public bool I2iV3RetryGateForSmoke(JsonElement ready, JsonElement unavailable)
    {
        Func<JsonElement, string?>? validator = CreateEnhancementRetryHealthValidator(
            "i2i", false, "synthetic-v3", "comfyui-flux2-i2i-v3", 3, null);
        return validator is not null && validator(ready) is null
            && validator(unavailable) is not null;
    }

    public bool EnhancementIntegrityParsersForSmoke(JsonElement validHealth)
    {
        if (!TryParseEnhancementQueueHealth(validHealth, out _)) return false;
        foreach (string member in new[] { "version", "runtime.processId", "jobs.counts.queued" })
        foreach (string invalid in new[] { "\"1\"", "null", "{}", "[]", "1e100" })
        {
            JsonNode root = JsonNode.Parse(validHealth.GetRawText())!;
            string[] segments = member.Split('.');
            JsonNode parent = root;
            foreach (string segment in segments[..^1]) parent = parent[segment]!;
            parent[segments[^1]] = JsonNode.Parse(invalid);
            if (TryParseEnhancementQueueHealth(JsonSerializer.SerializeToElement(root), out _))
                return false;
        }
        foreach (string invalid in new[] { "\"1\"", "null", "{}", "[]", "1e100" })
        {
            using JsonDocument health = JsonDocument.Parse(
                "{\"capabilities\":{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":"
                + invalid + ",\"backendGeneration\":\"json-v1\"}}}");
            if (EnhancementEnqueueProbePolicy.Classify(true, 200, health.RootElement)
                != EnhancementEnqueueBackendMode.Unknown) return false;
        }
        return true;
    }

    public async Task<bool> IdempotentMutationEpochForSmokeAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        ConfigureModalEnhancementForSmoke(async (_, _) =>
        {
            requests++;
            started.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}"),
            };
        });
        Task<EnhancementApiResponse> old = SendIdempotentEnhancementMutationAsync(
            HttpMethod.Delete, "api/enhance/jobs/synthetic-no-real-delete");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The delayed response now reaches the real reconnect decision.
            _usingDefaultModalEnhancementSender = true;
            InvalidateEnhancementCompanionOperations();
            release.TrySetResult();
            try { await old.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            return requests == 1;
        }
        finally
        {
            release.TrySetResult();
            _usingDefaultModalEnhancementSender = false;
        }
    }
}
