using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class EnhancementEnqueueInboxStore
{
    internal const int ProtocolVersion = 1;
    internal const string BackendGeneration = "json-v1";
    internal const int MaximumItemsPerEnvelope = 1_000;
    internal const int MaximumBodyJsonBytes = 8 * 1024 * 1024;
    internal const int MaximumEnvelopeBytes = 16 * 1024 * 1024;
    internal const uint MoveFileWriteThroughFlagForSmoke = 0x00000008;
    private const string BatchTimestampFormat = "yyyyMMddHHmmssfffffff";
    private static long _lastBatchTimestampTicks;

    internal static EnhancementEnqueueInboxItem CreateItem(
        object? body,
        string queuePlacement,
        int batchIndex,
        string kind = "create",
        string? retryJobId = null,
        string? requestId = null,
        bool includeQueuePlacementInBody = true)
    {
        if (queuePlacement is not ("last" or "next"))
            throw new ArgumentOutOfRangeException(nameof(queuePlacement));
        if (batchIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(batchIndex));
        if (kind is not ("create" or "retry"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == "retry" && string.IsNullOrWhiteSpace(retryJobId))
            throw new ArgumentException("A retry job id is required.", nameof(retryJobId));
        if (kind == "retry" && queuePlacement != "last")
            throw new ArgumentException("Retry delivery version 1 supports tail placement only.", nameof(queuePlacement));
        if (kind == "create" && retryJobId is not null)
            throw new ArgumentException("A create request cannot name a retry job.", nameof(retryJobId));
        if (!includeQueuePlacementInBody && queuePlacement != "last")
            throw new ArgumentException("A body without queue placement supports tail placement only.", nameof(queuePlacement));

        string resolvedRequestId = NormalizeGuid(requestId ?? Guid.NewGuid().ToString("D"), nameof(requestId));
        string bodyJson = SerializeBodyJson(
            body,
            queuePlacement,
            includeQueuePlacementInBody);
        if (Encoding.UTF8.GetByteCount(bodyJson) > MaximumBodyJsonBytes)
        {
            throw new EnhancementEnqueuePayloadTooLargeException(
                "An enqueue request body exceeds the 8 MiB delivery limit.");
        }
        return new EnhancementEnqueueInboxItem(
            resolvedRequestId,
            ComputeRequestHash(kind, retryJobId, bodyJson),
            batchIndex,
            kind,
            retryJobId,
            bodyJson,
            queuePlacement);
    }

    internal static EnhancementEnqueueInboxPublishResult Publish(
        string jobsPath,
        IReadOnlyList<EnhancementEnqueueInboxItem> items,
        string? batchId = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsPath);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("At least one enqueue item is required.", nameof(items));

        string resolvedBatchId = batchId is null
            ? CreateSortableBatchId()
            : NormalizeBatchId(batchId, nameof(batchId));
        ValidateItems(items);
        byte[] payload = SerializeEnvelope(
            resolvedBatchId,
            createdAt ?? DateTimeOffset.UtcNow,
            items);
        if (payload.Length > MaximumEnvelopeBytes)
        {
            throw new EnhancementEnqueuePayloadTooLargeException(
                "An enqueue envelope exceeds the 16 MiB delivery limit.");
        }

        string pendingDirectory = GetPendingDirectory(jobsPath);
        Directory.CreateDirectory(pendingDirectory);
        string destinationPath = Path.Combine(pendingDirectory, resolvedBatchId + ".json");
        string temporaryPath = Path.Combine(
            pendingDirectory,
            resolvedBatchId + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           Options = FileOptions.WriteThrough,
                       }))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            if (!MoveFileEx(
                    temporaryPath,
                    destinationPath,
                    MoveFileWriteThroughFlagForSmoke))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The durable enqueue envelope could not be published atomically.");
            }
            return new EnhancementEnqueueInboxPublishResult(
                resolvedBatchId,
                destinationPath,
                items.ToArray());
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static string GetPendingDirectory(string jobsPath)
    {
        string fullJobsPath = Path.GetFullPath(jobsPath);
        string? enhanceDirectory = Path.GetDirectoryName(fullJobsPath);
        if (string.IsNullOrWhiteSpace(enhanceDirectory))
            throw new ArgumentException("The jobs path must have a parent directory.", nameof(jobsPath));
        return Path.Combine(enhanceDirectory, "enqueue-inbox", "v1", "pending");
    }

    internal static string ComputeRequestHash(
        string kind,
        string? retryJobId,
        string bodyJson)
    {
        string material = string.Concat(
            ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            "\n",
            BackendGeneration,
            "\n",
            kind,
            "\n",
            retryJobId ?? "",
            "\n",
            bodyJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string SerializeBodyJson(
        object? body,
        string queuePlacement,
        bool includeQueuePlacementInBody)
    {
        JsonElement root;
        if (body is null)
        {
            using JsonDocument emptyDocument = JsonDocument.Parse("{}");
            root = emptyDocument.RootElement.Clone();
        }
        else
        {
            root = JsonSerializer.SerializeToElement(body);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("An enqueue body must be a JSON object.", nameof(body));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("queuePlacement"))
                    continue;
                property.WriteTo(writer);
            }
            if (includeQueuePlacementInBody)
                writer.WriteString("queuePlacement", queuePlacement);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static byte[] SerializeEnvelope(
        string batchId,
        DateTimeOffset createdAt,
        IReadOnlyList<EnhancementEnqueueInboxItem> items)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", ProtocolVersion);
            writer.WriteString("backendGeneration", BackendGeneration);
            writer.WriteString("batchId", batchId);
            writer.WriteString("createdAt", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (EnhancementEnqueueInboxItem item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("requestId", item.RequestId);
                writer.WriteString("requestHash", item.RequestHash);
                writer.WriteNumber("batchIndex", item.BatchIndex);
                writer.WriteString("kind", item.Kind);
                if (item.Kind == "retry")
                    writer.WriteString("retryJobId", item.RetryJobId);
                writer.WriteString("bodyJson", item.BodyJson);
                writer.WriteString("queuePlacement", item.QueuePlacement);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void ValidateItems(IReadOnlyList<EnhancementEnqueueInboxItem> items)
    {
        if (items.Count > MaximumItemsPerEnvelope)
        {
            throw new ArgumentException(
                $"An enqueue envelope supports at most {MaximumItemsPerEnvelope} items.",
                nameof(items));
        }

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < items.Count; index++)
        {
            EnhancementEnqueueInboxItem item = items[index];
            NormalizeGuid(item.RequestId, nameof(item.RequestId));
            if (Encoding.UTF8.GetByteCount(item.BodyJson) > MaximumBodyJsonBytes)
            {
                throw new EnhancementEnqueuePayloadTooLargeException(
                    "An enqueue request body exceeds the 8 MiB delivery limit.");
            }
            if (!requestIds.Add(item.RequestId))
                throw new ArgumentException("Request ids must be unique.", nameof(items));
            if (item.BatchIndex != index)
                throw new ArgumentException("Batch indexes must be contiguous and ordered.", nameof(items));
            if (item.Kind is not ("create" or "retry")
                || item.QueuePlacement is not ("last" or "next")
                || (item.Kind == "retry") != !string.IsNullOrWhiteSpace(item.RetryJobId)
                || !string.Equals(
                    item.RequestHash,
                    ComputeRequestHash(item.Kind, item.RetryJobId, item.BodyJson),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("An enqueue item is inconsistent.", nameof(items));
            }
        }
    }

    private static string NormalizeGuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed))
            throw new ArgumentException("A lowercase UUID is required.", parameterName);
        return parsed.ToString("D");
    }

    private static string CreateSortableBatchId()
    {
        long observed = Volatile.Read(ref _lastBatchTimestampTicks);
        while (true)
        {
            long now = DateTime.UtcNow.Ticks;
            long candidate = Math.Max(now, observed + 1);
            long exchanged = Interlocked.CompareExchange(
                ref _lastBatchTimestampTicks,
                candidate,
                observed);
            if (exchanged == observed)
            {
                string timestamp = new DateTime(candidate, DateTimeKind.Utc)
                    .ToString(BatchTimestampFormat, CultureInfo.InvariantCulture);
                return timestamp + "-" + Guid.NewGuid().ToString("N");
            }
            observed = exchanged;
        }
    }

    private static string NormalizeBatchId(string value, string parameterName)
    {
        const int timestampLength = 21;
        if (value.Length != timestampLength + 1 + 32
            || value[timestampLength] != '-'
            || !DateTime.TryParseExact(
                value[..timestampLength],
                BatchTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _)
            || !Guid.TryParseExact(value[(timestampLength + 1)..], "N", out Guid suffix))
        {
            throw new ArgumentException(
                "A sortable UTC timestamp and lowercase UUID batch id is required.",
                parameterName);
        }
        return value[..timestampLength]
            + "-"
            + suffix.ToString("N");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}

internal sealed record EnhancementEnqueueInboxItem(
    string RequestId,
    string RequestHash,
    int BatchIndex,
    string Kind,
    string? RetryJobId,
    string BodyJson,
    string QueuePlacement);

internal sealed record EnhancementEnqueueInboxPublishResult(
    string BatchId,
    string Path,
    EnhancementEnqueueInboxItem[] Items);

internal sealed class EnhancementEnqueuePayloadTooLargeException(string message)
    : ArgumentException(message);
