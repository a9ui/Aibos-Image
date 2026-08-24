using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    public bool DiagnoseVideoTrimV1InventoryForSmoke(
        JsonElement job,
        out bool readerExact,
        out bool outputExact,
        out string? versionKind)
    {
        readerExact = TryReadVideoTrimV1WorkspaceSnapshot(job, out _);
        outputExact = job.TryGetProperty("outputPath", out JsonElement output)
            && output.ValueKind == JsonValueKind.String
            && TryResolveVideoToolsV2ManagedOutput(
                output.GetString() ?? "",
                out _);
        bool built = TryBuildVideoTrimV1ManagedVideoVersion(
            job,
            new Dictionary<string, VideoToolsV2InventoryProducer>(
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            out VideoToolsV2InventoryProducer resolution);
        versionKind = built ? resolution.Version.VersionKind : null;
        return built;
    }

    private bool TryBuildVideoTrimV1ManagedVideoVersion(
        JsonElement job,
        IReadOnlyDictionary<string, VideoToolsV2InventoryProducer> producers,
        IReadOnlySet<string> ambiguousJobIds,
        out VideoToolsV2InventoryProducer resolution)
    {
        resolution = null!;
        if (!TryReadVideoTrimV1WorkspaceSnapshot(
                job,
                out VideoTrimV1ReaderSnapshot snapshot)
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !IsSafeVideoToolsJobId(jobId!)
            || ambiguousJobIds.Contains(jobId!)
            || !TryGetExactStringProperty(job, "status", "succeeded")
            || !TryGetStringProperty(
                job,
                "outputPath",
                out string? outputPath)
            || !TryResolveVideoToolsV2ManagedOutput(
                outputPath!,
                out string canonicalOutput))
        {
            return false;
        }

        string rootSource;
        string? sourceManagedOutputPath = null;
        IReadOnlyList<string> inheritedAliases = [];
        if (snapshot.SourceKind == "managed-video-job")
        {
            if (snapshot.SourceVideoJobId is not string producerJobId
                || ambiguousJobIds.Contains(producerJobId)
                || !producers.TryGetValue(
                    producerJobId,
                    out VideoToolsV2InventoryProducer? producer)
                || !string.Equals(
                    snapshot.SourceCanonicalPath,
                    producer.Version.Output.OutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var producerInfo = new FileInfo(
                producer.Version.Output.OutputPath);
            double producerMtimeMs = new DateTimeOffset(
                producerInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (!producerInfo.Exists
                || producerInfo.Length != snapshot.SourceSize
                || Math.Abs(producerMtimeMs - snapshot.SourceMtimeMs) > 1)
            {
                return false;
            }
            rootSource = producer.RootSource;
            sourceManagedOutputPath = producer.Version.Output.OutputPath;
            inheritedAliases = producer.CatalogAliases;
        }
        else if (snapshot.SourceKind == "staged-displayed-file")
        {
            if (snapshot.SourceVideoJobId is not null
                || !Path.IsPathFullyQualified(snapshot.SourceCanonicalPath))
            {
                return false;
            }
            rootSource = Path.GetFullPath(snapshot.SourceCanonicalPath);
            if (!string.Equals(
                    rootSource,
                    snapshot.SourceCanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var version = new ManagedVideoVersion(
            jobId!,
            "trim",
            snapshot.PresetId,
            snapshot.AdapterId,
            "動画トリム",
            false,
            snapshot.SourceVideoJobId,
            sourceManagedOutputPath,
            snapshot.OutputDurationMs / 1_000d,
            snapshot.SourceFpsNumerator,
            snapshot.OutputFpsNumerator,
            snapshot.SourceFrameCount,
            snapshot.OutputFrameCount,
            checked(snapshot.OutputWidth * snapshot.OutputHeight),
            snapshot.OutputWidth,
            snapshot.OutputHeight,
            "",
            "",
            "",
            0,
            0,
            "",
            "",
            0,
            0,
            0,
            "h264",
            "mp4",
            8,
            new ManagedVideoDeliverySnapshot(
                snapshot.AdapterId,
                "動画トリム",
                snapshot.OutputFpsNumerator,
                snapshot.OutputFrameCount,
                snapshot.OutputDurationMs / 1_000d,
                "exact-half-open-v1",
                snapshot.DeliveryAudioKind == "aac-selected-interval"),
            ReadEnhancementActivityAtUtc(job),
            new ManagedVideoOutput(
                canonicalOutput,
                snapshot.SourceSize,
                snapshot.SourceMtimeMs));
        string[] aliases = inheritedAliases
            .Append(rootSource)
            .Append(snapshot.SourceCanonicalPath)
            .Select(NormalizeCatalogEnhancementPath)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        resolution = new(rootSource, version, aliases);
        return true;
    }
}
