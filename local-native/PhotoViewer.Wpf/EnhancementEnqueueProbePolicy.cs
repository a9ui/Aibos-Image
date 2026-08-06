using System.Text.Json;

namespace PhotoViewer.Wpf;

internal enum EnhancementEnqueueBackendMode
{
    Durable,
    Unknown,
}

internal static class EnhancementEnqueueProbePolicy
{
    private const string DurableCapability = "durableEnqueueInboxV1";

    internal static EnhancementEnqueueBackendMode Classify(
        bool ok,
        int statusCode,
        JsonElement? payload)
    {
        if (!ok || statusCode is < 200 or >= 300)
            return EnhancementEnqueueBackendMode.Unknown;
        if (payload is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return EnhancementEnqueueBackendMode.Unknown;
        return TryParseDurableCapability(root, out _)
            ? EnhancementEnqueueBackendMode.Durable
            : EnhancementEnqueueBackendMode.Unknown;
    }

    internal static bool AllowsImmediateNudge(EnhancementEnqueueBackendMode mode)
        => mode == EnhancementEnqueueBackendMode.Durable;

    internal static bool AllowsFeatureValidation(EnhancementEnqueueBackendMode mode)
        => mode == EnhancementEnqueueBackendMode.Durable;

    internal static int NextEnvelopeItemCount(int remainingItems)
    {
        if (remainingItems < 0)
            throw new ArgumentOutOfRangeException(nameof(remainingItems));
        return Math.Min(
            remainingItems,
            EnhancementEnqueueInboxStore.MaximumItemsPerEnvelope);
    }

    internal static bool TryParseDurableCapability(
        JsonElement payload,
        out DurableEnqueueInboxCapabilityState capability)
    {
        capability = default!;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetExactlyOneProperty(
                payload,
                "capabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !TryGetExactlyOneProperty(
                capabilities,
                DurableCapability,
                out JsonElement inbox)
            || inbox.ValueKind != JsonValueKind.Object
            || !TryGetExactlyOneProperty(inbox, "ready", out JsonElement ready)
            || ready.ValueKind != JsonValueKind.True
            || !TryGetExactlyOneProperty(
                inbox,
                "protocolVersion",
                out JsonElement protocolVersion)
            || !protocolVersion.TryGetInt32(out int version)
            || version != EnhancementEnqueueInboxStore.ProtocolVersion
            || !TryGetExactlyOneProperty(
                inbox,
                "backendGeneration",
                out JsonElement generation)
            || generation.ValueKind != JsonValueKind.String
            || !string.Equals(
                generation.GetString(),
                EnhancementEnqueueInboxStore.BackendGeneration,
                StringComparison.Ordinal))
        {
            return false;
        }

        capability = new DurableEnqueueInboxCapabilityState(
            version,
            generation.GetString()!);
        return true;
    }

    internal static bool HasMatchingDurableReceipt(
        JsonElement? payload,
        string requestId)
    {
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !TryGetExactlyOneProperty(root, "receipt", out JsonElement receipt)
            || receipt.ValueKind != JsonValueKind.Object
            || !TryGetExactlyOneStringProperty(receipt, "jobId", out string? jobId)
            || string.IsNullOrWhiteSpace(jobId)
            || !TryGetUniqueProperty(
                receipt,
                "idempotencyKey",
                out JsonElement idempotencyKeyElement,
                out bool hasIdempotencyKey)
            || !TryGetUniqueProperty(
                receipt,
                "clientRequestId",
                out JsonElement clientRequestIdElement,
                out bool hasClientRequestId))
        {
            return false;
        }

        bool requestMatches =
            (hasIdempotencyKey || hasClientRequestId)
            && (!hasIdempotencyKey
                || (idempotencyKeyElement.ValueKind == JsonValueKind.String
                    && string.Equals(
                        idempotencyKeyElement.GetString(),
                        requestId,
                        StringComparison.Ordinal)))
            && (!hasClientRequestId
                || (clientRequestIdElement.ValueKind == JsonValueKind.String
                    && string.Equals(
                        clientRequestIdElement.GetString(),
                        requestId,
                        StringComparison.Ordinal)));
        if (!requestMatches
            || !TryGetUniqueProperty(
                root,
                "job",
                out JsonElement job,
                out bool hasJob))
        {
            return false;
        }
        if (!hasJob)
            return true;
        return job.ValueKind == JsonValueKind.Object
            && TryGetExactlyOneStringProperty(job, "id", out string? returnedJobId)
            && string.Equals(returnedJobId, jobId, StringComparison.Ordinal);
    }

    private static bool TryGetExactlyOneStringProperty(
        JsonElement owner,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!TryGetExactlyOneProperty(owner, propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return value is not null;
    }

    private static bool TryGetExactlyOneProperty(
        JsonElement owner,
        string propertyName,
        out JsonElement value)
        => TryGetUniqueProperty(
                owner,
                propertyName,
                out value,
                out bool found)
            && found;

    private static bool TryGetUniqueProperty(
        JsonElement owner,
        string propertyName,
        out JsonElement value,
        out bool found)
    {
        value = default;
        found = false;
        if (owner.ValueKind != JsonValueKind.Object)
            return false;
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
                continue;
            if (found)
                return false;
            value = property.Value;
            found = true;
        }
        return true;
    }
}

internal sealed record DurableEnqueueInboxCapabilityState(
    int ProtocolVersion,
    string BackendGeneration);
