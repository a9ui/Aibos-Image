using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
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
