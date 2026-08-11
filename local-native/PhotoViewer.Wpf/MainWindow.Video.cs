using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string ManagedVideoFolderName = "Videos";
    private const string ManagedVideoExtension = ".mp4";
    private const int ManagedVideoAlignment = 32;
    private const long ManagedVideoMaximumPixelArea = 409_600;

    private static bool IsManagedVideoOutputLocation(
        string lexicalOutput,
        string canonicalOutput,
        string lexicalRoot,
        string canonicalRoot)
    {
        string? lexicalParent = Path.GetDirectoryName(lexicalOutput);
        string? canonicalParent = Path.GetDirectoryName(canonicalOutput);
        if (string.IsNullOrWhiteSpace(lexicalParent)
            || string.IsNullOrWhiteSpace(canonicalParent))
        {
            return false;
        }
        if (string.Equals(lexicalParent, lexicalRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(canonicalParent, canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string dateFolder = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(lexicalParent));
        if (!DateTime.TryParseExact(
                dateFolder,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _)
            || !string.Equals(
                dateFolder,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalParent)),
                StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(lexicalParent)),
                lexicalRoot,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(canonicalParent)),
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, List<ManagedVideoVersion>> _videoVersions =
        new(EnhancementSourceIdentityComparer);
    private readonly Dictionary<string, List<ManagedVideoVersion>> _catalogVideoVersionsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagedVideoVersion> _modalVideoVersions = [];
    private int _modalVideoVersionIndex;
    private int _videoCandidateCount;
    private bool _modalShowingVideo;
    private bool _modalVideoPlaying;
    private bool _modalVideoLoopEnabled = true;
    private bool _modalVideoAutoplayPending;
    private bool _suppressModalVideoVersionSelection;
    private bool _modalVideoTransportStubForSmoke;
    private bool _suppressModalVideoSeek;
    private bool _modalVideoSeekDragging;
    private double _modalVideoDurationSeconds;
    private long _modalVideoPlaybackGeneration;
    private DispatcherTimer? _modalVideoTimelineTimer;
    private TaskCompletionSource<bool>? _modalVideoMediaOpenCompletion;
    private string? _modalVideoMediaFailureForSmoke;

    private sealed record ManagedVideoOutput(
        string OutputPath,
        long SourceSize,
        double SourceMtimeMs);

    private sealed record ManagedVideoVersion(
        string JobId,
        string PresetId,
        string BackendId,
        string ModelName,
        bool IsMiniMaxH3,
        string? SourceProducerJobId,
        double DurationSeconds,
        int RequestedPlaybackFps,
        int PlaybackFps,
        int NativeFrameCount,
        int FrameCount,
        int MaximumPixelArea,
        int Width,
        int Height,
        string RequestedPrompt,
        string PositivePrompt,
        string NegativePrompt,
        int Steps,
        int Cfg,
        string Sampler,
        string Scheduler,
        int Shift,
        int Denoise,
        int Seed,
        string Codec,
        string Container,
        int BitDepth,
        ManagedVideoDeliverySnapshot? Delivery,
        DateTimeOffset? CompletedAtUtc,
        ManagedVideoOutput Output);

    private sealed record ManagedVideoDeliverySnapshot(
        string BackendId,
        string Model,
        int TargetFps,
        int FrameCount,
        double DurationSeconds,
        string PixelFormat,
        bool Audio,
        string? VideoCodec = null,
        string? AudioCodec = null);

    private sealed record ModalVideoVersionChoice(int Index, string Label);
    private sealed record ManagedPhotorealVideoSource(
        string ResolvedSource,
        string OutputPath,
        IReadOnlyList<string> CatalogAliases,
        long SourceSize,
        double SourceMtimeMs);

    private static bool TryReadMiniMaxH3SourceDimensions(
        string path,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using Stream stream = OpenBitmapReadStream(
                path,
                CancellationToken.None,
                onFirstRead: null);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            ushort orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (string query in new[]
                {
                    "/app1/ifd/{ushort=274}",
                    "/ifd/{ushort=274}",
                })
                {
                    try
                    {
                        object? value = metadata.GetQuery(query);
                        if (value is ushort unsignedValue)
                        {
                            orientation = unsignedValue;
                            break;
                        }
                        if (value is short signedValue && signedValue > 0)
                        {
                            orientation = (ushort)signedValue;
                            break;
                        }
                    }
                    catch
                    {
                        // The metadata query is format-specific.
                    }
                }
            }
            if (orientation is 5 or 6 or 7 or 8)
                (width, height) = (height, width);
            return width > 0 && height > 0;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private bool IsMiniMaxH3SourceCanvasCurrent(JsonElement job)
    {
        try
        {
            if (!TryGetStringProperty(job, "sourcePath", out string? sourcePath)
                || !job.TryGetProperty(
                    "sourceSignature",
                    out JsonElement signature)
                || !signature.TryGetProperty("size", out JsonElement sizeElement)
                || !sizeElement.TryGetInt64(out long expectedSize)
                || !signature.TryGetProperty(
                    "mtimeMs",
                    out JsonElement mtimeElement)
                || !mtimeElement.TryGetDouble(out double expectedMtimeMs)
                || !job.TryGetProperty("video", out JsonElement video)
                || !video.TryGetProperty(
                    "effective",
                    out JsonElement effective)
                || !effective.TryGetProperty("width", out JsonElement widthElement)
                || !widthElement.TryGetInt32(out int width)
                || !effective.TryGetProperty("height", out JsonElement heightElement)
                || !heightElement.TryGetInt32(out int height)
                || !TryResolveEnhancementSourceIdentity(
                    sourcePath,
                    out string resolvedSource)
                || !File.Exists(resolvedSource))
            {
                return false;
            }

            var info = new FileInfo(resolvedSource);
            double currentMtimeMs = new DateTimeOffset(
                info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info.Length != expectedSize
                || Math.Abs(currentMtimeMs - expectedMtimeMs) > 1
                || !TryReadMiniMaxH3SourceDimensions(
                    resolvedSource,
                    out int sourceWidth,
                    out int sourceHeight))
            {
                return false;
            }

            (int expectedWidth, int expectedHeight) =
                NormalizeMiniMaxH3VideoCanvas(sourceWidth, sourceHeight);
            return width == expectedWidth && height == expectedHeight;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBuildManagedVideoVersion(
        JsonElement job,
        IReadOnlyDictionary<string, ManagedPhotorealVideoSource>
            photorealSources,
        out string resolvedSource,
        out ManagedVideoVersion version,
        out IReadOnlyList<string> catalogAliases)
    {
        resolvedSource = "";
        version = null!;
        catalogAliases = [];
        if (IsMiniMaxH3VideoMutationSafe(job))
        {
            return TryBuildMiniMaxH3ManagedVideoVersion(
                job,
                photorealSources,
                out resolvedSource,
                out version,
                out catalogAliases);
        }

        if (!IsWanV1VideoMutationSafe(job)
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !TryGetStringProperty(job, "sourcePath", out string? sourcePath)
            || !TryGetStringProperty(job, "sourceId", out string? sourceId)
            || !TryGetStringProperty(job, "outputPath", out string? outputPath)
            || !TryGetExactStringProperty(job, "mediaKind", "video")
            || !job.TryGetProperty("sourceSignature", out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !signature.TryGetProperty("size", out JsonElement sizeElement)
            || !sizeElement.TryGetInt64(out long sourceSize)
            || !signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement)
            || !mtimeElement.TryGetDouble(out double sourceMtimeMs)
            || !job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(video, "presetId", out string? presetId)
            || !TryGetStringProperty(video, "backendId", out string? backendId)
            || !TryGetStringProperty(video, "modelName", out string? modelName)
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !requested.TryGetProperty("durationSeconds", out JsonElement durationElement)
            || !durationElement.TryGetDouble(out double durationSeconds)
            || !requested.TryGetProperty("playbackFps", out JsonElement fpsElement)
            || !fpsElement.TryGetInt32(out int playbackFps)
            || !requested.TryGetProperty("maximumPixelArea", out JsonElement maximumPixelAreaElement)
            || !maximumPixelAreaElement.TryGetInt32(out int maximumPixelArea)
            || !TryGetStringPropertyAllowEmpty(requested, "prompt", out string? requestedPrompt)
            || !video.TryGetProperty("effective", out JsonElement effective)
            || effective.ValueKind != JsonValueKind.Object
            || !effective.TryGetProperty("frameCount", out JsonElement frameCountElement)
            || !frameCountElement.TryGetInt32(out int frameCount)
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || !TryGetStringProperty(effective, "positivePrompt", out string? positivePrompt)
            || !TryGetStringPropertyAllowEmpty(effective, "negativePrompt", out string? negativePrompt)
            || !effective.TryGetProperty("steps", out JsonElement stepsElement)
            || !stepsElement.TryGetInt32(out int steps)
            || !effective.TryGetProperty("cfg", out JsonElement cfgElement)
            || !cfgElement.TryGetInt32(out int cfg)
            || !TryGetStringProperty(effective, "sampler", out string? sampler)
            || !TryGetStringProperty(effective, "scheduler", out string? scheduler)
            || !effective.TryGetProperty("shift", out JsonElement shiftElement)
            || !shiftElement.TryGetInt32(out int shift)
            || !effective.TryGetProperty("denoise", out JsonElement denoiseElement)
            || !denoiseElement.TryGetInt32(out int denoise)
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || !TryGetStringProperty(video, "codec", out string? codec)
            || !TryGetStringProperty(video, "container", out string? container)
            || !video.TryGetProperty("bitDepth", out JsonElement bitDepthElement)
            || !bitDepthElement.TryGetInt32(out int bitDepth))
        {
            return false;
        }

        if (!double.IsFinite(durationSeconds)
            || durationSeconds <= 0
            || durationSeconds > 60
            || playbackFps <= 0
            || playbackFps > 60
            || frameCount != checked((int)(4 * Math.Floor(durationSeconds * playbackFps / 4d) + 1))
            || width < ManagedVideoAlignment
            || height < ManagedVideoAlignment
            || width % ManagedVideoAlignment != 0
            || height % ManagedVideoAlignment != 0
            || checked((long)width * height) > ManagedVideoMaximumPixelArea
            || checked((long)width * height) > maximumPixelArea
            || seed < 0
            || !string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase)
            || bitDepth != 8)
        {
            return false;
        }

        if (!TryReadOptionalVideoSourceProducerJobId(
                job,
                out string? sourceProducerJobId))
        {
            return false;
        }

        int outputPlaybackFps = playbackFps;
        int outputFrameCount = frameCount;
        ManagedVideoDeliverySnapshot? deliverySnapshot = null;
        if (video.TryGetProperty("delivery", out JsonElement delivery))
        {
            if (video.EnumerateObject().Count(static property =>
                    property.NameEquals("delivery")) != 1
                || !durationElement.TryGetInt32(
                    out int deliveryDurationSeconds)
                || durationSeconds != deliveryDurationSeconds
                || deliveryDurationSeconds is not (4 or 6)
                || !IsVideoDeliveryMutationSafe(
                    video,
                    deliveryDurationSeconds))
            {
                return false;
            }

            outputPlaybackFps = 30;
            outputFrameCount = checked(deliveryDurationSeconds * 30);
            if (!TryGetStringProperty(delivery, "backendId", out string? deliveryBackendId)
                || !TryGetStringProperty(delivery, "model", out string? deliveryModel)
                || !delivery.TryGetProperty("targetFps", out JsonElement deliveryFpsElement)
                || !deliveryFpsElement.TryGetInt32(out int deliveryFps)
                || !delivery.TryGetProperty("frameCount", out JsonElement deliveryFramesElement)
                || !deliveryFramesElement.TryGetInt32(out int deliveryFrames)
                || !delivery.TryGetProperty("pixelFormat", out JsonElement deliveryPixelFormatElement)
                || deliveryPixelFormatElement.ValueKind != JsonValueKind.String
                || !delivery.TryGetProperty("audio", out JsonElement deliveryAudioElement)
                || deliveryAudioElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            deliverySnapshot = new ManagedVideoDeliverySnapshot(
                deliveryBackendId!,
                deliveryModel!,
                deliveryFps,
                deliveryFrames,
                deliveryDurationSeconds,
                deliveryPixelFormatElement.GetString()!,
                deliveryAudioElement.GetBoolean());
        }

        try
        {
            if (!TryResolveEnhancementSourceIdentity(sourcePath, out string resolvedSourcePath)
                || !TryResolveEnhancementSourceIdentity(
                    sourceId,
                    out string resolvedSourceId))
            {
                return false;
            }

            string resolvedInputPath = resolvedSourcePath;
            IReadOnlyList<string> producerAliases = [];
            if (sourceProducerJobId is null)
            {
                if (!EnhancementSourceIdentityComparer.Equals(
                        resolvedSourcePath,
                        resolvedSourceId))
                {
                    return false;
                }
            }
            else
            {
                if (!photorealSources.TryGetValue(
                        sourceProducerJobId,
                        out ManagedPhotorealVideoSource? producer)
                    || !EnhancementSourceIdentityComparer.Equals(
                        producer.ResolvedSource,
                        resolvedSourceId)
                    || !string.Equals(
                        producer.OutputPath,
                        resolvedSourcePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                resolvedSourceId = producer.ResolvedSource;
                resolvedInputPath = producer.OutputPath;
                producerAliases = producer.CatalogAliases;
            }
            if (!File.Exists(resolvedInputPath))
                return false;

            var sourceInfo = new FileInfo(resolvedInputPath);
            double currentMtimeMs =
                new DateTimeOffset(sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (sourceInfo.Length != sourceSize || Math.Abs(currentMtimeMs - sourceMtimeMs) > 1)
                return false;

            string lexicalOutput = Path.GetFullPath(outputPath!);
            string canonicalOutput = _resolveFinalPath(lexicalOutput);
            string lexicalRoot = Path.GetFullPath(
                Path.Combine(ResolvedManagedEnhancementOutputsRoot, ManagedVideoFolderName));
            string canonicalRoot = _resolveFinalPath(lexicalRoot);
            if (!IsManagedVideoOutputLocation(
                    lexicalOutput,
                    canonicalOutput,
                    lexicalRoot,
                    canonicalRoot)
                || !string.Equals(
                    Path.GetExtension(canonicalOutput),
                    ManagedVideoExtension,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonicalOutput)
                || new FileInfo(canonicalOutput).Length <= 0)
            {
                return false;
            }

            DateTimeOffset? completedAtUtc = ReadEnhancementActivityAtUtc(job);

            resolvedSource = resolvedSourceId;
            version = new ManagedVideoVersion(
                jobId!,
                presetId!,
                backendId!,
                modelName!,
                false,
                sourceProducerJobId,
                durationSeconds,
                playbackFps,
                outputPlaybackFps,
                frameCount,
                outputFrameCount,
                maximumPixelArea,
                width,
                height,
                requestedPrompt!,
                positivePrompt!,
                negativePrompt!,
                steps,
                cfg,
                sampler!,
                scheduler!,
                shift,
                denoise,
                seed,
                codec!.ToLowerInvariant(),
                container!.ToLowerInvariant(),
                bitDepth,
                deliverySnapshot,
                completedAtUtc,
                new ManagedVideoOutput(canonicalOutput, sourceSize, sourceMtimeMs));
            catalogAliases = producerAliases
                .Concat(new[] { sourceId, resolvedSourceId })
                .Select(NormalizeCatalogEnhancementPath)
                .Where(static path => path is not null)
                .Select(static path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBuildMiniMaxH3ManagedVideoVersion(
        JsonElement job,
        IReadOnlyDictionary<string, ManagedPhotorealVideoSource>
            photorealSources,
        out string resolvedSource,
        out ManagedVideoVersion version,
        out IReadOnlyList<string> catalogAliases)
    {
        resolvedSource = "";
        version = null!;
        catalogAliases = [];
        if (!IsMiniMaxH3VideoMutationSafe(job)
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !TryGetStringProperty(job, "sourcePath", out string? sourcePath)
            || !TryGetStringProperty(job, "sourceId", out string? sourceId)
            || !TryGetStringProperty(job, "outputPath", out string? outputPath)
            || !job.TryGetProperty("sourceSignature", out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !signature.TryGetProperty("size", out JsonElement sizeElement)
            || !sizeElement.TryGetInt64(out long sourceSize)
            || !signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement)
            || !mtimeElement.TryGetDouble(out double sourceMtimeMs)
            || !job.TryGetProperty("video", out JsonElement video)
            || !video.TryGetProperty("requested", out JsonElement requested)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? requestedPrompt)
            || !video.TryGetProperty("effective", out JsonElement effective)
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || !TryGetStringProperty(
                effective,
                "positivePrompt",
                out string? positivePrompt)
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || !video.TryGetProperty("delivery", out JsonElement delivery)
            || !delivery.TryGetProperty(
                "durationSeconds",
                out JsonElement durationElement)
            || !durationElement.TryGetDouble(out double durationSeconds)
            || !TryReadOptionalVideoSourceProducerJobId(
                job,
                out string? sourceProducerJobId))
        {
            return false;
        }

        try
        {
            if (!TryResolveEnhancementSourceIdentity(
                    sourcePath,
                    out string resolvedSourcePath)
                || !TryResolveEnhancementSourceIdentity(
                    sourceId,
                    out string resolvedSourceId))
            {
                return false;
            }

            string resolvedInputPath = resolvedSourcePath;
            IReadOnlyList<string> producerAliases = [];
            if (sourceProducerJobId is null)
            {
                if (!EnhancementSourceIdentityComparer.Equals(
                        resolvedSourcePath,
                        resolvedSourceId))
                {
                    return false;
                }
            }
            else
            {
                if (!photorealSources.TryGetValue(
                        sourceProducerJobId,
                        out ManagedPhotorealVideoSource? producer)
                    || !EnhancementSourceIdentityComparer.Equals(
                        producer.ResolvedSource,
                        resolvedSourceId)
                    || !string.Equals(
                        producer.OutputPath,
                        resolvedSourcePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                resolvedSourceId = producer.ResolvedSource;
                resolvedInputPath = producer.OutputPath;
                producerAliases = producer.CatalogAliases;
            }

            if (!File.Exists(resolvedInputPath))
                return false;
            var sourceInfo = new FileInfo(resolvedInputPath);
            double currentMtimeMs =
                new DateTimeOffset(sourceInfo.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds();
            if (sourceInfo.Length != sourceSize
                || Math.Abs(currentMtimeMs - sourceMtimeMs) > 1)
            {
                return false;
            }
            if (!TryReadMiniMaxH3SourceDimensions(
                    resolvedInputPath,
                    out int sourceWidth,
                    out int sourceHeight))
            {
                return false;
            }
            (int expectedWidth, int expectedHeight) =
                NormalizeMiniMaxH3VideoCanvas(sourceWidth, sourceHeight);
            if (width != expectedWidth || height != expectedHeight)
                return false;

            string lexicalOutput = Path.GetFullPath(outputPath!);
            string canonicalOutput = _resolveFinalPath(lexicalOutput);
            string lexicalRoot = Path.GetFullPath(
                Path.Combine(
                    ResolvedManagedEnhancementOutputsRoot,
                    ManagedVideoFolderName));
            string canonicalRoot = _resolveFinalPath(lexicalRoot);
            if (!IsManagedVideoOutputLocation(
                    lexicalOutput,
                    canonicalOutput,
                    lexicalRoot,
                    canonicalRoot)
                || !string.Equals(
                    Path.GetExtension(canonicalOutput),
                    ManagedVideoExtension,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonicalOutput)
                || new FileInfo(canonicalOutput).Length <= 0)
            {
                return false;
            }

            resolvedSource = resolvedSourceId;
            version = new ManagedVideoVersion(
                jobId!,
                MiniMaxH3VideoPresetId,
                MiniMaxH3VideoBackendId,
                "MiniMax-H3",
                true,
                sourceProducerJobId,
                durationSeconds,
                MiniMaxH3VideoPlaybackFps,
                MiniMaxH3VideoPlaybackFps,
                MiniMaxH3VideoFrameCount,
                MiniMaxH3VideoFrameCount,
                checked(width * height),
                width,
                height,
                requestedPrompt!,
                positivePrompt!,
                "",
                MiniMaxH3VideoSteps,
                0,
                "res_multistep",
                "simple",
                0,
                1,
                seed,
                "h264",
                "mp4",
                8,
                new ManagedVideoDeliverySnapshot(
                    MiniMaxH3VideoBackendId,
                    "MiniMax-H3",
                    MiniMaxH3VideoPlaybackFps,
                    MiniMaxH3VideoFrameCount,
                    durationSeconds,
                    "yuv420p",
                    true,
                    "h264",
                    "aac"),
                ReadEnhancementActivityAtUtc(job),
                new ManagedVideoOutput(
                    canonicalOutput,
                    sourceSize,
                    sourceMtimeMs));
            catalogAliases = producerAliases
                .Concat(new[] { sourceId, resolvedSourceId })
                .Select(NormalizeCatalogEnhancementPath)
                .Where(static path => path is not null)
                .Select(static path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetExactStringProperty(
        JsonElement element,
        string propertyName,
        string expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool TryGetStringPropertyAllowEmpty(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

    private IReadOnlyList<ManagedVideoVersion> GetManagedVideoVersionsForPath(string path)
    {
        string? alias = NormalizeCatalogEnhancementPath(path);
        if (alias is not null
            && _catalogVideoVersionsByPath.TryGetValue(
                alias,
                out List<ManagedVideoVersion>? catalogVersions))
        {
            return catalogVersions;
        }

        if (TryResolveEnhancementSourceIdentity(path, out string identity)
            && _videoVersions.TryGetValue(identity, out List<ManagedVideoVersion>? versions))
        {
            return versions;
        }

        return Array.Empty<ManagedVideoVersion>();
    }

    private IReadOnlyList<ManagedVideoVersion> GetCatalogManagedVideoVersionsForPath(string path)
    {
        string? alias = NormalizeCatalogEnhancementPath(path);
        return alias is not null
            && _catalogVideoVersionsByPath.TryGetValue(
                alias,
                out List<ManagedVideoVersion>? versions)
            ? versions
            : Array.Empty<ManagedVideoVersion>();
    }

    private void ApplyTileVideoAvailability(Tile tile)
    {
        ApplyTileVideoAvailability(
            tile,
            GetManagedVideoVersionsForPath(tile.Path));
    }

    private void ApplyTileVideoAvailability(
        Tile tile,
        IReadOnlyList<ManagedVideoVersion> versions)
    {
        ManagedVideoVersion[] validVersions = versions
            .Where(candidate => TryValidateManagedVideoVersion(
                tile,
                candidate,
                out _))
            .ToArray();
        tile.VideoVersionCount = validVersions.Length;
        tile.VideoGenerated = validVersions.Length > 0;
        tile.VideoFavoriteLevel = validVersions
            .Select(version => FavoriteLevelForPath(version.Output.OutputPath))
            .DefaultIfEmpty(0)
            .Max();
        tile.VideoOutputPath = validVersions.Length > 0
            ? validVersions[0].Output.OutputPath
            : null;
        tile.VideoCompletedAtUtc = LatestVideoActivityUtc(validVersions);
    }

    private void InitializeModalVideoVersions(Tile tile)
    {
        string? selectedJobId = _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count
                ? _modalVideoVersions[_modalVideoVersionIndex].JobId
                : null;
        _modalVideoVersions.Clear();
        _modalVideoVersions.AddRange(GetManagedVideoVersionsForPath(tile.Path));
        _modalVideoVersionIndex = selectedJobId is null
            ? 0
            : Math.Max(
                0,
                _modalVideoVersions.FindIndex(version =>
                    string.Equals(version.JobId, selectedJobId, StringComparison.Ordinal)));
        RefreshModalVideoVersionChoices();
    }

    private void ClearModalVideoVersions()
    {
        StopAndHideModalVideo(clearSource: true);
        _modalVideoVersions.Clear();
        _modalVideoVersionIndex = 0;
        RefreshModalVideoVersionChoices();
    }

    private void RefreshModalVideoVersionChoices()
    {
        if (ModalVideoVersionComboBox is null || ModalVideoPlaybackButton is null)
            return;

        _suppressModalVideoVersionSelection = true;
        try
        {
            ModalVideoVersionComboBox.ItemsSource = _modalVideoVersions
                .Select((version, index) => new ModalVideoVersionChoice(
                    index,
                    $"{(version.IsMiniMaxH3 ? "MiniMax H3 Preview" : $"V{index + 1}")} · {version.DurationSeconds:0.#######}s · "
                        + $"{version.PlaybackFps}fps · {version.FrameCount}f · "
                        + $"{version.Width}×{version.Height}"))
                .ToArray();
            ModalVideoVersionComboBox.SelectedIndex = _modalVideoVersions.Count == 0
                ? -1
                : Math.Clamp(_modalVideoVersionIndex, 0, _modalVideoVersions.Count - 1);
        }
        finally
        {
            _suppressModalVideoVersionSelection = false;
        }

        bool available = _modalVideoVersions.Count > 0;
        // The top display-version selector is the single user-facing media
        // inventory. Keep this legacy control populated for compatibility and
        // smoke inspection, but never expose a second video-only dropdown.
        ModalVideoVersionComboBox.Visibility = Visibility.Collapsed;
        ModalVideoPlaybackButton.Visibility = available
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoPlaybackButton.IsEnabled = available;
        if (ModalFooter is not null)
        {
            ModalFooter.Visibility = available && ModalChromeEffectivelyVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdateModalVideoPlaybackPresentation();
    }

    private bool ShowModalVideoVersion(int index, bool autoplay)
    {
        if (Modal.Visibility != Visibility.Visible
            || index < 0
            || index >= _modalVideoVersions.Count)
        {
            return false;
        }

        if (SelectedTile() is not Tile tile
            || !TryValidateManagedVideoVersion(
                tile,
                _modalVideoVersions[index],
                out ManagedVideoVersion version))
            return false;

        return ShowValidatedModalVideoVersion(version, index, autoplay);
    }

    private bool ShowValidatedModalVideoVersion(
        ManagedVideoVersion version,
        int index,
        bool autoplay)
    {
        if (Modal.Visibility != Visibility.Visible
            || index < 0
            || index >= _modalVideoVersions.Count)
        {
            return false;
        }

        _modalVideoVersionIndex = index;
        _modalShowingVideo = true;
        _modalShowingEnhanced = false;
        CancelModalMetadataRefresh(clearCurrent: true);
        _modalVideoPlaying = autoplay;
        _modalVideoAutoplayPending = autoplay;
        _modalVideoPlaybackGeneration++;
        _modalVideoMediaFailureForSmoke = null;
        _modalVideoMediaOpenCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        ModalVideo.Visibility = Visibility.Visible;
        ModalBitmap.Visibility = Visibility.Collapsed;
        ModalArtBase.Visibility = Visibility.Collapsed;
        ModalArtGlow.Visibility = Visibility.Collapsed;
        ResetModalVideoTimeline(version.DurationSeconds, show: true);
        EnsureModalVideoTimelineTimer();
        _modalVideoTimelineTimer?.Start();

        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Stop();
                ModalVideo.Source = null;
                ModalVideo.Source = new Uri(version.Output.OutputPath, UriKind.Absolute);
                // Manual MediaElement transport does not open a newly assigned
                // source until a transport state is requested. Pause kicks the
                // open without advancing frames; MediaOpened then rewinds and
                // dispatches the requested autoplay from exactly zero.
                ModalVideo.Pause();
                ModalVideo.Position = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                _modalVideoMediaFailureForSmoke = ex.Message;
                _modalVideoMediaOpenCompletion.TrySetResult(false);
                RestoreModalOriginalAfterVideoFailure();
                return false;
            }
        }
        else
        {
            _modalVideoPlaying = autoplay;
            _modalVideoMediaOpenCompletion.TrySetResult(true);
        }

        RefreshModalVideoVersionChoices();
        ModalVideoVersionComboBox.SelectedIndex = index;
        if (SelectedTile() is Tile selected)
        {
            RememberModalDisplayPreference(
                selected,
                ModalDisplayVersionKind.Video,
                version.JobId);
            UpdateModalDisplayedDimensionsInfo(
                selected,
                version.Width,
                version.Height);
            UpdateModalEnhancedControls(
                TryGetModalEnhancedOutput(selected, out _));
        }
        ModalSourceLabel.Text = $"Video V{index + 1}";
        ModalFileSizeText.Text = FormatFileSizeMb(new FileInfo(version.Output.OutputPath).Length);
        SyncModalMetadataSidebar();
        SetModalMetadataTab(ModalMetadataSettingsTab);
        SetModalMetadataSidebarVisible(true);
        return true;
    }

    private void StopAndHideModalVideo(bool clearSource)
    {
        _modalVideoPlaybackGeneration++;
        _modalVideoTimelineTimer?.Stop();
        if (ModalVideo is not null && !_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Stop();
                if (clearSource)
                    ModalVideo.Source = null;
            }
            catch
            {
            }
        }

        _modalShowingVideo = false;
        _modalVideoPlaying = false;
        _modalVideoAutoplayPending = false;
        if (ModalVideo is not null)
            ModalVideo.Visibility = Visibility.Collapsed;
        ResetModalVideoTimeline(0, show: false);
        RestoreModalImageVisibility();
        if (Modal?.Visibility == Visibility.Visible)
        {
            Tile? selected = SelectedTile();
            bool canShowEnhanced = selected is not null
                && TryGetModalEnhancedOutput(selected, out _);
            if (selected is not null)
            {
                UpdateModalDisplayedDimensionsInfo(
                    selected,
                    _modalDisplayedImagePixelWidth,
                    _modalDisplayedImagePixelHeight);
            }
            UpdateModalEnhancedControls(canShowEnhanced);
            if (!string.IsNullOrWhiteSpace(_modalDisplayPath)
                && File.Exists(_modalDisplayPath))
            {
                ModalFileSizeText.Text =
                    FormatFileSizeMb(new FileInfo(_modalDisplayPath).Length);
            }
        }
        UpdateModalVideoPlaybackPresentation();
    }

    private void RestoreModalOriginalAfterVideoFailure()
    {
        StopAndHideModalVideo(clearSource: true);
        if (Modal.Visibility != Visibility.Visible
            || SelectedTile() is not Tile tile)
        {
            return;
        }

        _modalEnhancementVersionIndex = 0;
        _modalShowingEnhanced = false;
        RememberModalDisplayPreference(
            tile,
            ModalDisplayVersionKind.Original,
            null);
        OpenModal();
    }

    private void RestoreModalImageVisibility()
    {
        if (ModalBitmap is null || ModalArtBase is null || ModalArtGlow is null)
            return;

        bool hasBitmap = ModalBitmap.Source is not null;
        ModalBitmap.Visibility = hasBitmap ? Visibility.Visible : Visibility.Collapsed;
        ModalArtBase.Visibility = hasBitmap ? Visibility.Collapsed : Visibility.Visible;
        ModalArtGlow.Visibility = hasBitmap ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool ToggleModalVideoPlayback()
    {
        if (Modal.Visibility != Visibility.Visible || _modalVideoVersions.Count == 0)
            return false;

        if (!_modalShowingVideo)
            return ShowModalVideoVersion(_modalVideoVersionIndex, autoplay: true);

        try
        {
            if (_modalVideoPlaying)
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Pause();
                _modalVideoPlaying = false;
                _modalVideoAutoplayPending = false;
            }
            else
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Play();
                _modalVideoPlaying = true;
                _modalVideoAutoplayPending = true;
            }
        }
        catch
        {
            RestoreModalOriginalAfterVideoFailure();
            return false;
        }

        UpdateModalVideoPlaybackPresentation();
        return true;
    }

    private void UpdateModalVideoPlaybackPresentation()
    {
        if (ModalVideoPlaybackButtonLabel is null || ModalVideoPlaybackButton is null)
            return;

        ModalVideoPlaybackButtonLabel.Text = _modalShowingVideo && _modalVideoPlaying
            ? "一時停止"
            : "動画再生";
        string shortcut = BindingText(ViewerKeyAction.ToggleVideoPlayback);
        ModalVideoPlaybackButton.ToolTip =
            $"動画を再生 / 一時停止 ({shortcut})。再生終了後は自動ループします。";
        AutomationProperties.SetName(
            ModalVideoPlaybackButton,
            _modalShowingVideo && _modalVideoPlaying
                ? "Pause generated video"
                : "Play generated video");
        UpdateModalDisplayedDeletePresentation();
    }

    private bool TryGetDisplayedModalVideoVersion(
        Tile tile,
        out ManagedVideoVersion version)
    {
        version = null!;
        if (!_modalShowingVideo
            || !string.Equals(
                _modalSourceTilePath,
                tile.Path,
                StringComparison.OrdinalIgnoreCase)
            || _modalVideoVersionIndex < 0
            || _modalVideoVersionIndex >= _modalVideoVersions.Count)
        {
            return false;
        }

        return TryValidateManagedVideoVersion(
            tile,
            _modalVideoVersions[_modalVideoVersionIndex],
            out version);
    }

    private bool TryValidateManagedVideoVersion(
        Tile tile,
        ManagedVideoVersion candidate,
        out ManagedVideoVersion version)
    {
        version = null!;
        if (!IsGloballyUniqueManagedJobId(candidate.JobId))
            return false;

        ManagedVideoVersion? uniqueVideo = null;
        foreach (ManagedVideoVersion current
                 in GetManagedVideoVersionsForPath(tile.Path))
        {
            if (!string.Equals(
                    current.JobId,
                    candidate.JobId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (uniqueVideo is not null)
                return false;
            uniqueVideo = current;
        }
        if (uniqueVideo is null || !Equals(uniqueVideo, candidate))
            return false;

        try
        {
            string currentInputPath;
            if (candidate.SourceProducerJobId is null)
            {
                if (!TryResolveEnhancementSourceIdentity(
                        tile.Path,
                        out currentInputPath))
                {
                    return false;
                }
            }
            else
            {
                if (!IsGloballyUniqueManagedJobId(candidate.SourceProducerJobId))
                    return false;

                ManagedEnhancementVersion? uniqueProducer = null;
                foreach (ManagedEnhancementVersion producer
                         in GetManagedEnhancementVersionsForPath(tile.Path))
                {
                    if (!string.Equals(
                            producer.JobId,
                            candidate.SourceProducerJobId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (uniqueProducer is not null
                        || !string.Equals(
                            producer.Operation,
                            "photoreal",
                            StringComparison.Ordinal)
                        || !TryCreateManagedEnhancedOutput(
                            tile,
                            producer.Output.OutputPath,
                            producer.Output.SourceSize,
                            producer.Output.SourceMtimeMs,
                            out ManagedEnhancedOutput currentProducerOutput))
                    {
                        return false;
                    }

                    uniqueProducer = producer with
                    {
                        Output = currentProducerOutput,
                    };
                }

                if (uniqueProducer is null)
                    return false;
                currentInputPath = uniqueProducer.Output.OutputPath;
            }

            var currentInput = new FileInfo(currentInputPath);
            double currentInputMtimeMs = new DateTimeOffset(
                currentInput.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (!currentInput.Exists
                || currentInput.Length != candidate.Output.SourceSize
                || Math.Abs(
                    currentInputMtimeMs
                        - candidate.Output.SourceMtimeMs) > 1)
            {
                return false;
            }

            string lexicalOutput = Path.GetFullPath(
                candidate.Output.OutputPath);
            string canonicalOutput = _resolveFinalPath(
                lexicalOutput);
            string lexicalRoot = Path.GetFullPath(Path.Combine(
                ResolvedManagedEnhancementOutputsRoot,
                ManagedVideoFolderName));
            string canonicalRoot = _resolveFinalPath(lexicalRoot);
            if (!IsManagedVideoOutputLocation(
                    lexicalOutput,
                    canonicalOutput,
                    lexicalRoot,
                    canonicalRoot)
                || !string.Equals(
                    Path.GetExtension(canonicalOutput),
                    ManagedVideoExtension,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonicalOutput)
                || new FileInfo(canonicalOutput).Length <= 0)
            {
                return false;
            }

            version = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DisplayedManagedVideoDeleteVerifiedForSmoke
        => SelectedTile() is Tile tile
            && TryGetDisplayedModalVideoVersion(tile, out _);

    public bool DisplayedManagedVideoDuplicateJobRejectedForSmoke()
    {
        if (SelectedTile() is not Tile tile
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion displayed)
            || GetManagedVideoVersionsForPath(tile.Path)
                is not List<ManagedVideoVersion> versions)
        {
            return false;
        }

        versions.Add(displayed);
        try
        {
            return !TryGetDisplayedModalVideoVersion(tile, out _);
        }
        finally
        {
            versions.RemoveAt(versions.Count - 1);
        }
    }

    public bool DisplayedManagedVideoGlobalJobIdRejectedForSmoke()
    {
        if (SelectedTile() is not Tile tile
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion displayed)
            || !_ambiguousEnhancementJobIds.Add(displayed.JobId))
        {
            return false;
        }

        try
        {
            return !TryGetDisplayedModalVideoVersion(tile, out _);
        }
        finally
        {
            _ambiguousEnhancementJobIds.Remove(displayed.JobId);
        }
    }

    private async Task<bool> DeleteDisplayedModalVideoOutputAsync()
    {
        if (_modalEnhancementRequestPending
            || SelectedTile() is not Tile tile
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion version))
        {
            SetStatusToast("Delete blocked: the displayed managed video could not be verified.");
            return false;
        }

        long requestGeneration = _modalEnhancementGeneration;
        string sourcePath = tile.Path;
        string requestJobId = version.JobId;
        int previousIndex = _modalVideoVersionIndex;
        bool confirmed = _confirmEnhancedOutputDeleteForSmoke?.Invoke() ?? MessageBox.Show(
                this,
                "Delete only the displayed video output? The original image and other AI versions will be kept.",
                "Delete video output version",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
            return false;

        if (!IsCurrentModalEnhancementContext(tile, sourcePath, requestGeneration)
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion revalidated)
            || !Equals(revalidated, version))
        {
            SetStatusToast("Delete blocked: the displayed video changed while confirmation was open.");
            return false;
        }

        _modalEnhancementRequestPending = true;
        StopAndHideModalVideo(clearSource: true);
        UpdateModalEnhancementActionControls();
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Delete,
                $"api/enhance/jobs/{Uri.EscapeDataString(requestJobId)}/output");
            if (!IsCurrentModalEnhancementContext(tile, sourcePath, requestGeneration))
                return false;
            if (!response.Ok)
            {
                SetStatusToast(response.Error);
                if (Modal.Visibility == Visibility.Visible)
                    ShowModalVideoVersion(previousIndex, autoplay: false);
                return false;
            }

            RemoveManagedVideoVersion(tile, requestJobId);
            OpenModal();
            BeginModalEnhancementRefresh(tile.Path);
            ShowModalInteractionFeedback(
                "Video output version deleted; original and other versions kept");
            return true;
        }
        finally
        {
            _modalEnhancementRequestPending = false;
            UpdateModalEnhancementActionControls();
        }
    }

    private void RemoveManagedVideoVersion(Tile tile, string jobId)
    {
        _modalVideoVersions.RemoveAll(version =>
            string.Equals(version.JobId, jobId, StringComparison.Ordinal));
        foreach (string key in _videoVersions.Keys.ToArray())
        {
            _videoVersions[key].RemoveAll(version =>
                string.Equals(version.JobId, jobId, StringComparison.Ordinal));
            if (_videoVersions[key].Count == 0)
                _videoVersions.Remove(key);
        }
        foreach (string key in _catalogVideoVersionsByPath.Keys.ToArray())
        {
            _catalogVideoVersionsByPath[key].RemoveAll(version =>
                string.Equals(version.JobId, jobId, StringComparison.Ordinal));
            if (_catalogVideoVersionsByPath[key].Count == 0)
                _catalogVideoVersionsByPath.Remove(key);
        }

        _modalVideoVersionIndex = 0;
        _modalShowingVideo = false;
        _modalEnhancementVersionIndex = 0;
        _modalShowingEnhanced = false;
        RememberModalDisplayPreference(
            tile,
            ModalDisplayVersionKind.Original,
            null);
        foreach (Tile liveTile in EnumerateLiveTiles())
            ApplyTileVideoAvailability(liveTile);
        RefreshModalVideoVersionChoices();
    }

    private void ModalVideoPlayback_Click(object sender, RoutedEventArgs e)
        => ToggleModalVideoPlayback();

    private void ModalVideoVersion_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressModalVideoVersionSelection
            || ModalVideoVersionComboBox.SelectedItem is not ModalVideoVersionChoice choice
            || choice.Index < 0
            || choice.Index >= _modalVideoVersions.Count)
        {
            return;
        }

        _modalVideoVersionIndex = choice.Index;
        if (_modalShowingVideo)
            ShowModalVideoVersion(choice.Index, autoplay: true);
    }

    private void ModalVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        long playbackGeneration = _modalVideoPlaybackGeneration;
        try
        {
            ModalVideo.Pause();
            ModalVideo.Position = TimeSpan.Zero;
        }
        catch
        {
            RestoreModalOriginalAfterVideoFailure();
            return;
        }
        if (ModalVideo.NaturalDuration.HasTimeSpan
            && ModalVideo.NaturalDuration.TimeSpan > TimeSpan.Zero)
        {
            _modalVideoDurationSeconds = ModalVideo.NaturalDuration.TimeSpan.TotalSeconds;
        }
        UpdateModalVideoTimeline(TimeSpan.Zero);
        _modalVideoMediaOpenCompletion?.TrySetResult(true);
        UpdateModalVideoPlaybackPresentation();
        if (!_modalVideoAutoplayPending)
            return;

        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!_modalShowingVideo
                    || !_modalVideoAutoplayPending
                    || playbackGeneration != _modalVideoPlaybackGeneration)
                {
                    return;
                }

                try
                {
                    ModalVideo.Position = TimeSpan.Zero;
                    ModalVideo.Play();
                    _modalVideoPlaying = true;
                    UpdateModalVideoTimeline(TimeSpan.Zero);
                    UpdateModalVideoPlaybackPresentation();
                }
                catch
                {
                    RestoreModalOriginalAfterVideoFailure();
                }
            }),
            DispatcherPriority.Render);
    }

    private void ModalVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        try
        {
            if (!_modalVideoTransportStubForSmoke)
                ModalVideo.Position = TimeSpan.Zero;
            UpdateModalVideoTimeline(TimeSpan.Zero);
            if (_modalVideoLoopEnabled)
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Play();
                _modalVideoPlaying = true;
                _modalVideoAutoplayPending = true;
            }
            else
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Pause();
                _modalVideoPlaying = false;
                _modalVideoAutoplayPending = false;
            }
        }
        catch
        {
            RestoreModalOriginalAfterVideoFailure();
            return;
        }
        UpdateModalVideoPlaybackPresentation();
    }

    private void EnsureModalVideoTimelineTimer()
    {
        if (_modalVideoTimelineTimer is not null)
            return;

        _modalVideoTimelineTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _modalVideoTimelineTimer.Tick += (_, _) =>
        {
            if (!_modalShowingVideo || _modalVideoTransportStubForSmoke)
                return;

            try
            {
                if (ModalVideo.NaturalDuration.HasTimeSpan
                    && ModalVideo.NaturalDuration.TimeSpan > TimeSpan.Zero)
                {
                    _modalVideoDurationSeconds = ModalVideo.NaturalDuration.TimeSpan.TotalSeconds;
                }
                if (!_modalVideoSeekDragging)
                    UpdateModalVideoTimeline(ModalVideo.Position);
            }
            catch
            {
            }
        };
    }

    private void ResetModalVideoTimeline(double durationSeconds, bool show)
    {
        _modalVideoDurationSeconds = double.IsFinite(durationSeconds)
            ? Math.Max(0, durationSeconds)
            : 0;
        if (ModalVideoSeekPanel is null
            || ModalVideoSeekSlider is null
            || ModalVideoSeekTimeText is null)
        {
            return;
        }

        ModalVideoSeekPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ModalFooter is not null)
        {
            bool hasFooterContent = show
                || ModalVideoPlaybackButton?.Visibility == Visibility.Visible;
            ModalFooter.Visibility = hasFooterContent && ModalChromeEffectivelyVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        _suppressModalVideoSeek = true;
        try
        {
            ModalVideoSeekSlider.Maximum = Math.Max(0.001, _modalVideoDurationSeconds);
            ModalVideoSeekSlider.Value = 0;
        }
        finally
        {
            _suppressModalVideoSeek = false;
        }
        ModalVideoSeekTimeText.Text = $"0:00 / {FormatModalVideoTime(_modalVideoDurationSeconds)}";
    }

    private void UpdateModalVideoTimeline(TimeSpan position)
    {
        if (ModalVideoSeekSlider is null || ModalVideoSeekTimeText is null)
            return;

        double totalSeconds = Math.Max(0, _modalVideoDurationSeconds);
        double currentSeconds = Math.Clamp(
            double.IsFinite(position.TotalSeconds) ? position.TotalSeconds : 0,
            0,
            totalSeconds);
        _suppressModalVideoSeek = true;
        try
        {
            ModalVideoSeekSlider.Maximum = Math.Max(0.001, totalSeconds);
            ModalVideoSeekSlider.Value = Math.Min(
                ModalVideoSeekSlider.Maximum,
                currentSeconds);
        }
        finally
        {
            _suppressModalVideoSeek = false;
        }
        ModalVideoSeekTimeText.Text =
            $"{FormatModalVideoTime(currentSeconds)} / {FormatModalVideoTime(totalSeconds)}";
    }

    private static string FormatModalVideoTime(double seconds)
    {
        int totalSeconds = (int)Math.Max(0, Math.Floor(seconds));
        return totalSeconds >= 3600
            ? TimeSpan.FromSeconds(totalSeconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : TimeSpan.FromSeconds(totalSeconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void ModalVideoSeekSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
        => _modalVideoSeekDragging = true;

    private void ModalVideoSeekSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _modalVideoSeekDragging = false;
        SeekModalVideoToSeconds(ModalVideoSeekSlider.Value);
    }

    private void ModalVideoSeekSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModalVideoSeek || !_modalShowingVideo)
            return;
        SeekModalVideoToSeconds(e.NewValue);
    }

    private bool SeekModalVideoToSeconds(double seconds)
    {
        if (!_modalShowingVideo || !double.IsFinite(seconds))
            return false;

        double clamped = Math.Clamp(seconds, 0, Math.Max(0, _modalVideoDurationSeconds));
        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Position = TimeSpan.FromSeconds(clamped);
            }
            catch
            {
                return false;
            }
        }
        UpdateModalVideoTimeline(TimeSpan.FromSeconds(clamped));
        return true;
    }

    private void ModalVideo_MediaFailed(
        object sender,
        System.Windows.ExceptionRoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        _modalVideoMediaFailureForSmoke =
            e.ErrorException?.Message ?? "Media Foundation rejected the video.";
        _modalVideoMediaOpenCompletion?.TrySetResult(false);
        RestoreModalOriginalAfterVideoFailure();
        SetStatusToast("動画を再生できません。元画像を表示します。");
    }

    public void SetVideoOnlyFilterForSmoke(bool enabled)
    {
        VideoOnlyFilter.IsChecked = enabled;
        ApplyFilters();
    }

    public bool VideoGeneratedForFileForSmoke(string fileName)
        => _allTiles.FirstOrDefault(tile =>
            string.Equals(tile.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.VideoGenerated == true;

    public string? VideoOutputPathForFileForSmoke(string fileName)
        => _allTiles.FirstOrDefault(tile =>
            string.Equals(tile.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.VideoOutputPath;

    public int VideoCandidateCountForSmoke => _videoCandidateCount;
    public int ManagedVideoVersionCountForSmoke =>
        _videoVersions.Values.Sum(static versions => versions.Count);
    public int VideoVersionCountForSmoke => _modalVideoVersions.Count;
    public int ModalVideoVersionIndexForSmoke => _modalVideoVersionIndex;
    public bool ModalShowingVideoForSmoke => _modalShowingVideo;
    public bool ModalVideoPlayingForSmoke => _modalVideoPlaying;
    public bool ModalVideoLoopEnabledForSmoke => _modalVideoLoopEnabled;
    public bool ModalVideoSeekVisibleForSmoke =>
        ModalVideoSeekPanel.Visibility == Visibility.Visible;
    public double ModalVideoSeekValueForSmoke => ModalVideoSeekSlider.Value;
    public double ModalVideoSeekMaximumForSmoke => ModalVideoSeekSlider.Maximum;
    public string ModalVideoSeekTimeForSmoke => ModalVideoSeekTimeText.Text;
    public string? ModalVideoPathForSmoke =>
        _modalVideoVersionIndex >= 0 && _modalVideoVersionIndex < _modalVideoVersions.Count
            ? _modalVideoVersions[_modalVideoVersionIndex].Output.OutputPath
            : null;
    public string? ModalVideoMediaFailureForSmoke =>
        _modalVideoMediaFailureForSmoke;
    public string[] ModalVideoVersionLabelsForSmoke =>
        ModalVideoVersionComboBox.Items
            .OfType<ModalVideoVersionChoice>()
            .Select(static choice => choice.Label)
            .ToArray();
    public (int PlaybackFps, int FrameCount)[]
        ModalVideoVersionPlaybackMetadataForSmoke =>
            _modalVideoVersions
                .Select(static version =>
                    (version.PlaybackFps, version.FrameCount))
                .ToArray();
    public bool ModalVideoHasNaturalDurationForSmoke =>
        _modalVideoTransportStubForSmoke
        || (ModalVideo.NaturalDuration.HasTimeSpan
            && ModalVideo.NaturalDuration.TimeSpan > TimeSpan.Zero);

    public void EnableModalVideoTransportStubForSmoke()
        => _modalVideoTransportStubForSmoke = true;

    public async Task<bool> WaitForModalVideoMediaOpenedForSmokeAsync(
        int timeoutMilliseconds = 10_000)
    {
        TaskCompletionSource<bool>? completion =
            _modalVideoMediaOpenCompletion;
        if (completion is null)
            return false;

        try
        {
            return await completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(
                    Math.Max(1, timeoutMilliseconds)));
        }
        catch (TimeoutException)
        {
            _modalVideoMediaFailureForSmoke =
                "Timed out waiting for MediaOpened.";
            return false;
        }
    }

    public async Task<bool> WaitForModalVideoPlaybackProgressForSmokeAsync(
        int timeoutMilliseconds = 10_000)
    {
        if (_modalVideoTransportStubForSmoke)
            return true;

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            Math.Max(1, timeoutMilliseconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!_modalShowingVideo || !_modalVideoPlaying)
                return false;
            if (ModalVideo.Position > TimeSpan.Zero)
                return true;
            await Task.Delay(50);
        }
        _modalVideoMediaFailureForSmoke =
            "Timed out waiting for video playback progress.";
        return false;
    }

    public async Task<bool> WaitForModalVideoPauseSettledForSmokeAsync()
    {
        if (_modalVideoTransportStubForSmoke)
            return !_modalVideoPlaying;
        if (!_modalShowingVideo || _modalVideoPlaying)
            return false;

        TimeSpan before = ModalVideo.Position;
        await Task.Delay(300);
        TimeSpan after = ModalVideo.Position;
        return _modalShowingVideo
            && !_modalVideoPlaying
            && Math.Abs((after - before).TotalMilliseconds) <= 100;
    }

    public bool ToggleModalVideoPlaybackForSmoke()
        => ToggleModalVideoPlayback();

    public bool SeekModalVideoForSmoke(double seconds)
        => SeekModalVideoToSeconds(seconds);

    public bool TriggerModalVideoEndedForSmoke()
    {
        if (!_modalShowingVideo)
            return false;
        ModalVideo_MediaEnded(ModalVideo, new RoutedEventArgs());
        return _modalShowingVideo
            && _modalVideoLoopEnabled
            && _modalVideoPlaying;
    }

    public bool SelectModalVideoVersionForSmoke(int index)
    {
        if (index < 0 || index >= _modalVideoVersions.Count)
            return false;
        _modalVideoVersionIndex = index;
        return Modal.Visibility == Visibility.Visible
            && ShowModalVideoVersion(index, autoplay: true);
    }

    public bool SelectModalVideoJobForSmoke(string jobId)
    {
        int index = _modalVideoVersions.FindIndex(version => string.Equals(
            version.JobId,
            jobId,
            StringComparison.Ordinal));
        return index >= 0 && SelectModalVideoVersionForSmoke(index);
    }

    public bool SelectCorruptModalVideoForSmoke(string path)
    {
        if (Modal.Visibility != Visibility.Visible
            || _modalVideoVersions.Count == 0
            || !File.Exists(path))
        {
            return false;
        }

        int index = Math.Clamp(
            _modalVideoVersionIndex,
            0,
            _modalVideoVersions.Count - 1);
        if (SelectedTile() is not Tile tile
            || !TryValidateManagedVideoVersion(
                tile,
                _modalVideoVersions[index],
                out ManagedVideoVersion source))
        {
            return false;
        }

        ManagedVideoVersion corrupt = source with
        {
            Output = source.Output with
            {
                OutputPath = Path.GetFullPath(path),
            },
        };
        return ShowValidatedModalVideoVersion(
            corrupt,
            index,
            autoplay: true);
    }

    public bool TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
        JsonElement job,
        out int width,
        out int height,
        out int playbackFps,
        out int frameCount,
        out double durationSeconds,
        out bool audio,
        out string settingsText)
    {
        width = 0;
        height = 0;
        playbackFps = 0;
        frameCount = 0;
        durationSeconds = 0;
        audio = false;
        settingsText = "";
        if (!TryBuildMiniMaxH3ManagedVideoVersion(
                job,
                new Dictionary<string, ManagedPhotorealVideoSource>(
                    StringComparer.Ordinal),
                out _,
                out ManagedVideoVersion version,
                out _)
            || !version.IsMiniMaxH3
            || version.Delivery is null)
        {
            return false;
        }

        width = version.Width;
        height = version.Height;
        playbackFps = version.PlaybackFps;
        frameCount = version.FrameCount;
        durationSeconds = version.DurationSeconds;
        audio = version.Delivery.Audio;
        settingsText = BuildManagedVideoSettingsText(version);
        return true;
    }
}
