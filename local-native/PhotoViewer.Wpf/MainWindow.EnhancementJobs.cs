using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementJobsThumbnailLimit = 48;
    private const int EnhancementJobsThumbnailCacheLimit = 96;
    private const string UnsupportedEnhancementOperation = "unsupported";
    private const string VideoPreservationPreamble =
        "Animate the supplied image as the exact first frame. "
        + "Preserve the same character identity, face, hairstyle, body proportions, outfit, colors, line art, rendering style, composition, background, lighting, and aspect ratio. "
        + "Keep temporal motion coherent and physically plausible with stable anatomy and clean frame-to-frame consistency.";
    private const string VideoBlankPromptMotion =
        "Use subtle natural idle motion only: gentle breathing, an occasional blink, and restrained secondary motion in hair and clothing. "
        + "Keep the camera locked and preserve the original framing.";
    private const string VideoNegativePrompt =
        "low quality, worst quality, blurry, flicker, jitter, frame interpolation artifacts, identity drift, face distortion, deformed hands, extra limbs, missing limbs, warped anatomy, melting, morphing, duplicate character, camera shake, text, logo, watermark";
    private static readonly JsonSerializerOptions VideoStableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly List<EnhancementWorkspaceJobView> _enhancementWorkspaceJobs = [];
    private readonly Dictionary<string, BitmapSource> _enhancementWorkspaceThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enhancementWorkspaceHighlightedJobIds = new(StringComparer.Ordinal);
    private DispatcherTimer _enhancementWorkspacePollTimer = null!;
    private CancellationTokenSource? _enhancementWorkspaceThumbnailCts;
    private bool _enhancementWorkspaceRefreshPending;
    private long _enhancementWorkspaceRefreshGeneration;
    private bool _enhancementWorkspaceMutationPending;
    private long _enhancementWorkspaceGeneration;
    private string _enhancementWorkspaceFilter = "all";
    private DateTimeOffset _enhancementWorkspaceHighlightExpiresAt;
    private IInputElement? _enhancementWorkspaceFocusBeforeDialog;
    private int _enhancementWorkspaceGetCount;
    private int _enhancementWorkspacePollCount;
    private int _enhancementWorkspaceHealthGetCount;
    private bool? _enhancementWorkspaceHealthEndpointSupported;
    private bool _returnToEnhancementJobsAfterModalClose;
    private Tile? _enhancementJobsTemporaryVisibleTile;
    private string? _enhancementJobsTrustedModalSourcePath;
    private readonly List<string> _enhancementJobsPreviousSelectionPaths = [];
    private string? _enhancementJobsPreviousPrimaryPath;
    private bool _enhancementJobsModalSelectionCaptured;
    private double _enhancementJobsReturnVerticalOffset;
    private string? _enhancementJobsReturnJobId;
    private double _enhancementJobsReturnAnchorViewportTop = double.NaN;

    private static string ReadEnhancementOperation(JsonElement job)
    {
        if (!job.TryGetProperty("operation", out JsonElement operation))
            return "upscale";
        if (operation.ValueKind != JsonValueKind.String)
            return UnsupportedEnhancementOperation;

        return operation.GetString() switch
        {
            "upscale" => "upscale",
            "photoreal" => "photoreal",
            "video" => "video",
            _ => UnsupportedEnhancementOperation,
        };
    }

    private static bool IsVideoMutationSafe(JsonElement job)
    {
        if (!TryReadOptionalVideoSourceProducerJobId(job, out _))
            return false;

        if ((job.TryGetProperty(
                    "cancelRequested",
                    out JsonElement cancelRequestedElement)
                && cancelRequestedElement.ValueKind is not (
                    JsonValueKind.True
                    or JsonValueKind.False
                    or JsonValueKind.Null))
            || !TryGetExactStringProperty(job, "mediaKind", "video")
            || !TryGetStringProperty(
                job,
                "presetId",
                out string? jobPresetId)
            || !TryGetVideoPresetSteps(
                jobPresetId,
                out int expectedSteps)
            || !TryGetExactStringProperty(
                job,
                "adapterId",
                "wan22-ti2v-5b-core-v1")
            || !TryGetStringProperty(job, "sourceSha256", out string? sourceSha256)
            || !IsLowerHex(sourceSha256, 64)
            || !TryGetStringProperty(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !HasExactVideoSnapshotProperties(video)
            || !TryGetStringProperty(
                video,
                "presetId",
                out string? videoPresetId)
            || !string.Equals(
                videoPresetId,
                jobPresetId,
                StringComparison.Ordinal)
            || !TryGetExactStringProperty(
                video,
                "backendId",
                "wan22-ti2v-5b-core-v1")
            || !TryGetExactStringProperty(
                video,
                "modelName",
                "wan2.2_ti2v_5B_fp16.safetensors")
            || !TryGetExactStringProperty(video, "codec", "h264")
            || !TryGetExactStringProperty(video, "container", "mp4")
            || !video.TryGetProperty("bitDepth", out JsonElement bitDepthElement)
            || !bitDepthElement.TryGetInt32(out int bitDepth)
            || bitDepth != 8
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || seed < 0
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                requested,
                "durationSeconds",
                "playbackFps",
                "maximumPixelArea",
                "prompt")
            || !video.TryGetProperty("effective", out JsonElement effective)
            || effective.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                effective,
                "frameCount",
                "width",
                "height",
                "positivePrompt",
                "negativePrompt",
                "steps",
                "cfg",
                "sampler",
                "scheduler",
                "shift",
                "denoise")
            || !requested.TryGetProperty(
                "durationSeconds",
                out JsonElement durationElement)
            || !durationElement.TryGetInt32(out int durationSeconds)
            || durationSeconds is not (4 or 6)
            || !requested.TryGetProperty(
                "playbackFps",
                out JsonElement playbackFpsElement)
            || !playbackFpsElement.TryGetInt32(out int playbackFps)
            || playbackFps is not (12 or 16)
            || !requested.TryGetProperty(
                "maximumPixelArea",
                out JsonElement maximumPixelAreaElement)
            || !maximumPixelAreaElement.TryGetInt32(out int maximumPixelArea)
            || maximumPixelArea is not (230400 or 307200 or 409600)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? prompt)
            || prompt!.Length > 2_000
            || !effective.TryGetProperty(
                "frameCount",
                out JsonElement frameCountElement)
            || !frameCountElement.TryGetInt32(out int frameCount)
            || frameCount != checked(
                4 * (durationSeconds * playbackFps / 4) + 1)
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || width < 32
            || height < 32
            || width % 32 != 0
            || height % 32 != 0
            || checked((long)width * height) > maximumPixelArea
            || !TryGetStringProperty(
                effective,
                "positivePrompt",
                out string? positivePrompt)
            || !TryGetStringPropertyAllowEmpty(
                effective,
                "negativePrompt",
                out string? negativePrompt)
            || !string.Equals(
                positivePrompt,
                BuildVideoPositivePrompt(prompt),
                StringComparison.Ordinal)
            || !string.Equals(
                negativePrompt,
                VideoNegativePrompt,
                StringComparison.Ordinal)
            || !HasExactInt32(effective, "steps", expectedSteps)
            || !HasExactInt32(effective, "cfg", 5)
            || !TryGetExactStringProperty(effective, "sampler", "uni_pc")
            || !TryGetExactStringProperty(effective, "scheduler", "simple")
            || !HasExactInt32(effective, "shift", 8)
            || !HasExactInt32(effective, "denoise", 1)
            || !IsVideoDeliveryMutationSafe(
                video,
                durationSeconds))
        {
            return false;
        }

        if (!string.Equals(
                presetHash,
                HashStableJson(video)[..12],
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!job.TryGetProperty(
                "outputPath",
                out JsonElement outputPathElement)
            || outputPathElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (outputPathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(outputPathElement.GetString())
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !TryGetStringProperty(
                job,
                "sourcePath",
                out string? sourcePath))
        {
            return false;
        }

        try
        {
            string expectedFileName = BuildVideoOutputFileName(
                jobId!,
                sourcePath!,
                sourceSha256!,
                jobPresetId!,
                presetHash!);
            return string.Equals(
                Path.GetFileName(outputPathElement.GetString()),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetVideoPresetSteps(
        string? presetId,
        out int steps)
    {
        switch (presetId)
        {
            case NormalVideoPresetId:
                steps = NormalVideoSteps;
                return true;
            case HighVideoPresetId:
                steps = HighVideoSteps;
                return true;
            default:
                steps = 0;
                return false;
        }
    }

    private static bool TryReadOptionalVideoSourceProducerJobId(
        JsonElement job,
        out string? sourceProducerJobId)
    {
        sourceProducerJobId = null;
        JsonElement sourceProducerElement = default;
        int propertyCount = 0;
        foreach (JsonProperty property in job.EnumerateObject())
        {
            if (!property.NameEquals("sourceProducerJobId"))
                continue;
            propertyCount++;
            if (propertyCount > 1)
                return false;
            sourceProducerElement = property.Value;
        }

        if (propertyCount == 0)
            return true;
        if (sourceProducerElement.ValueKind != JsonValueKind.String)
            return false;

        string? value = sourceProducerElement.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        sourceProducerJobId = value;
        return true;
    }

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expectedNames)
    {
        string[] actualNames = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return actualNames.Length == expectedNames.Length
            && actualNames
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames);
    }

    private static bool HasExactVideoSnapshotProperties(
        JsonElement video)
        => video.TryGetProperty("delivery", out _)
            ? HasExactProperties(
                video,
                "presetId",
                "backendId",
                "modelName",
                "requested",
                "effective",
                "delivery",
                "seed",
                "codec",
                "container",
                "bitDepth")
            : HasExactProperties(
                video,
                "presetId",
                "backendId",
                "modelName",
                "requested",
                "effective",
                "seed",
                "codec",
                "container",
                "bitDepth");

    private static bool IsVideoDeliveryMutationSafe(
        JsonElement video,
        int durationSeconds)
    {
        if (!video.TryGetProperty("delivery", out JsonElement delivery))
            return true;

        return delivery.ValueKind == JsonValueKind.Object
            && HasExactProperties(
                delivery,
                "backendId",
                "model",
                "targetFps",
                "frameCount",
                "durationSeconds",
                "pixelFormat",
                "audio")
            && TryGetExactStringProperty(
                delivery,
                "backendId",
                "vs-rife-5.7.0-rife-4.25-v1")
            && TryGetExactStringProperty(delivery, "model", "4.25")
            && HasExactInt32(delivery, "targetFps", 30)
            && HasExactInt32(
                delivery,
                "frameCount",
                checked(durationSeconds * 30))
            && HasExactInt32(
                delivery,
                "durationSeconds",
                durationSeconds)
            && TryGetExactStringProperty(
                delivery,
                "pixelFormat",
                "yuv420p")
            && delivery.TryGetProperty(
                "audio",
                out JsonElement audioElement)
            && audioElement.ValueKind == JsonValueKind.False;
    }

    private static string BuildVideoOutputFileName(
        string jobId,
        string sourcePath,
        string sourceSha256,
        string presetId,
        string presetHash)
    {
        string safeJobId = SanitizeVideoOutputNamePart(
            jobId,
            maximumLength: 48,
            fallback: "");
        if (safeJobId.Length == 0)
            throw new ArgumentException("Video job id is empty.", nameof(jobId));
        string safeSourceName = SanitizeVideoOutputNamePart(
            Path.GetFileNameWithoutExtension(sourcePath),
            maximumLength: 64,
            fallback: "image");
        return $"{safeJobId}__{safeSourceName}__{sourceSha256[..16]}__{presetId}__{presetHash}.mp4";
    }

    private static string SanitizeVideoOutputNamePart(
        string value,
        int maximumLength,
        string fallback)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            bool invalid = character < ' '
                || character is '<' or '>' or ':' or '"' or '/'
                    or '\\' or '|' or '?' or '*';
            builder.Append(invalid ? '_' : character);
        }
        string sanitized = builder.ToString();
        if (sanitized.Length > maximumLength)
            sanitized = sanitized[..maximumLength];
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static string BuildVideoPositivePrompt(string prompt)
    {
        string requestedPrompt = prompt.Trim();
        return requestedPrompt.Length == 0
            ? $"{VideoPreservationPreamble} {VideoBlankPromptMotion}"
            : $"{VideoPreservationPreamble} Follow this motion direction: {requestedPrompt}";
    }

    private static bool HasExactInt32(
        JsonElement element,
        string propertyName,
        int expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.TryGetInt32(out int value)
            && value == expected;

    private static bool IsLowerHex(string? value, int length)
        => value is not null
            && value.Length == length
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private static string HashStableJson(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendStableJson(builder, element);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendStableJson(
        StringBuilder builder,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool firstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(
                                 static property => property.Name,
                                 StringComparer.Ordinal))
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append(
                        JsonSerializer.Serialize(
                            property.Name,
                            VideoStableJsonOptions));
                    builder.Append(':');
                    AppendStableJson(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    AppendStableJson(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(
                    JsonSerializer.Serialize(
                        element.GetString(),
                        VideoStableJsonOptions));
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer))
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                else
                    builder.Append(
                        element.GetDouble().ToString(
                            "R",
                            CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported JSON value in video snapshot.");
        }
    }

    private static bool IsImageEnhancementOperation(string? operation)
        => operation is "upscale" or "photoreal";

    private bool CanCancelAllQueuedEnhancementJobs()
    {
        EnhancementWorkspaceJobView[] queued = _enhancementWorkspaceJobs
            .Where(static job => job.Status == "queued")
            .ToArray();
        return queued.Length > 0
            && queued.All(static job => job.IsSupportedMutationOperation);
    }

    private void InitializeEnhancementJobsWorkspace()
    {
        _enhancementWorkspacePollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _enhancementWorkspacePollTimer.Tick += EnhancementWorkspacePollTimer_Tick;
    }

    private async void OpenEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => await OpenEnhancementJobsWorkspaceAsync("all");

    private async Task OpenEnhancementJobsWorkspaceAsync(
        string initialFilter,
        IReadOnlyCollection<string>? highlightedJobIds = null,
        IInputElement? focusToRestore = null,
        bool restoreReturnViewport = false)
    {
        if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            return;

        StopGalleryAutoScroll();
        SearchHistoryPopup.IsOpen = false;
        _enhancementWorkspaceFocusBeforeDialog = focusToRestore ?? Keyboard.FocusedElement;
        _enhancementWorkspaceFilter = initialFilter is "queued" or "running" or "failed" or "canceled" or "completed" or "video"
            ? initialFilter
            : "all";
        EnhancementJobsAllFilter.IsChecked = _enhancementWorkspaceFilter == "all";
        EnhancementJobsQueuedFilter.IsChecked = _enhancementWorkspaceFilter == "queued";
        EnhancementJobsRunningFilter.IsChecked = _enhancementWorkspaceFilter == "running";
        EnhancementJobsFailedFilter.IsChecked = _enhancementWorkspaceFilter == "failed";
        EnhancementJobsCanceledFilter.IsChecked = _enhancementWorkspaceFilter == "canceled";
        EnhancementJobsCompletedFilter.IsChecked = _enhancementWorkspaceFilter == "completed";
        EnhancementJobsVideoFilter.IsChecked = _enhancementWorkspaceFilter == "video";
        _enhancementWorkspaceHighlightedJobIds.Clear();
        if (highlightedJobIds is not null)
        {
            _enhancementWorkspaceHighlightedJobIds.UnionWith(highlightedJobIds.Where(static id => !string.IsNullOrWhiteSpace(id)));
            _enhancementWorkspaceHighlightExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20);
        }
        else
        {
            _enhancementWorkspaceHighlightExpiresAt = default;
        }
        EnhancementJobsDialog.Visibility = Visibility.Visible;
        EnhancementJobsStatusText.Text = "Loading jobs from the local companion...";
        _enhancementWorkspaceHealthEndpointSupported = null;
        ApplyEnhancementQueueHealthUnavailable("Checking queue health...");
        EnhancementJobsEmptyText.Visibility = Visibility.Collapsed;
        if (!restoreReturnViewport)
            EnhancementJobsList.ItemsSource = null;
        long generation = ++_enhancementWorkspaceGeneration;
        _ = Dispatcher.BeginInvoke(EnhancementJobsRefreshButton.Focus, DispatcherPriority.Input);
        await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        if (restoreReturnViewport)
            await RestoreEnhancementJobsReturnViewportAsync();
    }

    private void CloseEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => CloseEnhancementJobsWorkspace(restoreFocus: true);

    private void CloseEnhancementJobsWorkspace(bool restoreFocus)
    {
        if (EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceGeneration++;
        _enhancementWorkspacePollTimer.Stop();
        Interlocked.Exchange(ref _enhancementWorkspaceThumbnailCts, null)?.Cancel();
        EnhancementJobsDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus)
            RestoreOverlayFocus(_enhancementWorkspaceFocusBeforeDialog);
        _enhancementWorkspaceFocusBeforeDialog = null;
    }

    private void EnhancementJobsBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EnhancementJobsDialog.Visibility == Visibility.Visible && ReferenceEquals(e.OriginalSource, EnhancementJobsDialog))
        {
            CloseEnhancementJobsWorkspace(restoreFocus: true);
            e.Handled = true;
        }
    }

    private async void RefreshEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => await RefreshEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration, isPoll: false);

    private async void EnhancementWorkspacePollTimer_Tick(object? sender, EventArgs e)
    {
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || (_enhancementWorkspaceRefreshPending && _enhancementWorkspaceRefreshGeneration == _enhancementWorkspaceGeneration))
            return;

        _enhancementWorkspacePollCount++;
        await RefreshEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration, isPoll: true);
    }

    private async Task RefreshEnhancementJobsWorkspaceAsync(long generation, bool isPoll)
    {
        if ((_enhancementWorkspaceRefreshPending && _enhancementWorkspaceRefreshGeneration == generation)
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceRefreshPending = true;
        _enhancementWorkspaceRefreshGeneration = generation;
        EnhancementJobsRefreshButton.IsEnabled = false;
        if (!isPoll)
            EnhancementJobsStatusText.Text = "Refreshing jobs...";
        try
        {
            _enhancementWorkspaceGetCount++;
            EnhancementApiResponse response = await SendEnhancementApiAsync(HttpMethod.Get, "api/enhance/jobs");
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;

            if (!response.Ok || response.Payload is not JsonElement payload)
            {
                EnhancementJobsStatusText.Text = response.Error;
                _enhancementWorkspacePollTimer.Stop();
                return;
            }

            if (!TryParseEnhancementWorkspaceJobs(payload, out List<EnhancementWorkspaceJobView> jobs, out string? error))
            {
                EnhancementJobsStatusText.Text = error ?? "The companion returned an invalid jobs response.";
                _enhancementWorkspacePollTimer.Stop();
                return;
            }

            ApplyEnhancementWorkspaceHighlights(jobs);
            ReconcileEnhancementWorkspaceJobs(jobs);
            bool highlightedBatchAlreadyTerminal = _enhancementWorkspaceFilter is "queued" or "running"
                && jobs.Any(static job => job.IsHighlighted)
                && !jobs.Any(static job => job.IsHighlighted && job.IsActive);
            if (highlightedBatchAlreadyTerminal)
            {
                _enhancementWorkspaceFilter = "all";
                EnhancementJobsAllFilter.IsChecked = true;
                EnhancementJobsQueuedFilter.IsChecked = false;
                EnhancementJobsRunningFilter.IsChecked = false;
            }
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            int activeCount = jobs.Count(static job => job.IsActive);
            int runningCount = jobs.Count(static job => job.Status == "running");
            int queuedCount = jobs.Count(static job => job.Status == "queued");
            int completedCount = jobs.Count(static job => job.Status is "succeeded" or "deleted");
            EnhancementJobsClearQueuedButton.IsEnabled =
                CanCancelAllQueuedEnhancementJobs()
                && !_enhancementWorkspaceMutationPending;
            EnhancementJobsHeaderSummary.Text = $"{jobs.Count:N0} total  ·  {activeCount:N0} active  ·  {completedCount:N0} completed";
            EnhancementJobsStatusText.Text = activeCount > 0
                ? $"共有GPUキューを実行順で表示中です。実行中 {runningCount:N0}、待ち {queuedCount:N0}。"
                : $"Updated {DateTime.Now:HH:mm:ss}. Polling is stopped because no jobs are active.";
            if (highlightedBatchAlreadyTerminal)
                EnhancementJobsStatusText.Text += " The new batch already finished, so all highlighted jobs are shown.";
            if (activeCount > 0)
                _enhancementWorkspacePollTimer.Start();
            else
                _enhancementWorkspacePollTimer.Stop();

            await RefreshEnhancementQueueHealthAsync(generation, isPoll);
        }
        finally
        {
            if (_enhancementWorkspaceRefreshGeneration == generation)
            {
                _enhancementWorkspaceRefreshPending = false;
                if (EnhancementJobsRefreshButton is not null)
                    EnhancementJobsRefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task RefreshEnhancementQueueHealthAsync(long generation, bool isPoll)
    {
        if (isPoll && _enhancementWorkspaceHealthEndpointSupported == false)
            return;

        _enhancementWorkspaceHealthGetCount++;
        EnhancementApiResponse response =
            await SendEnhancementApiAsync(HttpMethod.Get, "api/enhance/health");
        if (generation != _enhancementWorkspaceGeneration
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        if (response.StatusCode == 404)
        {
            _enhancementWorkspaceHealthEndpointSupported = false;
            ApplyEnhancementQueueHealthUnavailable(
                "Update the local companion to show queue health.");
            return;
        }

        _enhancementWorkspaceHealthEndpointSupported = true;
        if (!response.Ok || response.Payload is not JsonElement payload)
        {
            ApplyEnhancementQueueHealthUnavailable(
                "Queue health could not be read. Jobs remain available.");
            return;
        }

        if (!TryParseEnhancementQueueHealth(payload, out EnhancementQueueHealthView health))
        {
            ApplyEnhancementQueueHealthUnavailable(
                "The companion returned an unsupported health response.");
            return;
        }

        ApplyEnhancementQueueHealth(health);
    }

    private static bool TryParseEnhancementQueueHealth(
        JsonElement payload,
        out EnhancementQueueHealthView health)
    {
        health = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("version", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out int version)
            || version != 1
            || !payload.TryGetProperty("status", out JsonElement statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? status = statusElement.GetString();
        if (status is not ("healthy" or "working" or "needs-attention")
            || !payload.TryGetProperty("issues", out JsonElement issuesElement)
            || issuesElement.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("jobs", out JsonElement jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Object
            || !jobsElement.TryGetProperty("counts", out JsonElement countsElement)
            || countsElement.ValueKind != JsonValueKind.Object
            || !TryReadNonNegativeCount(countsElement, "queued", out int queued)
            || !TryReadNonNegativeCount(countsElement, "running", out int running)
            || !TryReadNonNegativeCount(countsElement, "succeeded", out _)
            || !TryReadNonNegativeCount(countsElement, "failed", out _)
            || !TryReadNonNegativeCount(countsElement, "canceled", out _)
            || !TryReadNonNegativeCount(countsElement, "deleted", out _)
            || !payload.TryGetProperty("runtime", out JsonElement runtimeElement)
            || runtimeElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? firstIssue = null;
        foreach (JsonElement issueElement in issuesElement.EnumerateArray())
        {
            if (issueElement.ValueKind != JsonValueKind.String)
                return false;
            firstIssue ??= DescribeEnhancementQueueHealthIssue(issueElement.GetString());
        }

        string revision = "H25 revision unavailable";
        if (runtimeElement.TryGetProperty("sourceRevision", out JsonElement revisionElement))
        {
            if (revisionElement.ValueKind == JsonValueKind.String)
            {
                string? sourceRevision = revisionElement.GetString();
                if (!string.IsNullOrWhiteSpace(sourceRevision))
                {
                    string prefix = sourceRevision.Length > 8
                        ? sourceRevision[..8]
                        : sourceRevision;
                    revision = $"H25 {prefix}";
                }
            }
            else if (revisionElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (runtimeElement.TryGetProperty("sourceDirty", out JsonElement dirtyElement))
        {
            if (dirtyElement.ValueKind == JsonValueKind.True)
                revision += " · modified";
            else if (dirtyElement.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                return false;
        }
        else
        {
            return false;
        }

        string stateLabel = status switch
        {
            "healthy" => "Healthy",
            "working" => "Working",
            _ => "Needs attention",
        };
        string detail = status == "needs-attention"
            ? firstIssue ?? "Queue attention is required."
            : running == 0 && queued == 0
                ? "Queue is idle"
                : $"{running:N0} running / {queued:N0} queued";
        string foregroundResource = status switch
        {
            "healthy" => "Success",
            "working" => "AccentLight",
            _ => "Warning",
        };
        health = new EnhancementQueueHealthView(
            stateLabel,
            detail,
            revision,
            foregroundResource);
        return true;
    }

    private static bool TryReadNonNegativeCount(
        JsonElement counts,
        string propertyName,
        out int value)
    {
        value = 0;
        return counts.TryGetProperty(propertyName, out JsonElement countElement)
            && countElement.TryGetInt32(out value)
            && value >= 0;
    }

    private static string DescribeEnhancementQueueHealthIssue(string? issue)
        => issue switch
        {
            "multiple-running-jobs" => "More than one job is marked running.",
            "running-without-worker-identity" => "The running job has no worker identity.",
            "running-without-local-pump" => "A job is running without this process's queue pump.",
            "queued-without-pump" => "Queued work is waiting without a queue pump.",
            "worker-loop-failing" => "The worker loop reported a failure.",
            "non-loopback-server" => "The local companion is not loopback-only.",
            "non-loopback-comfyui" => "ComfyUI is not loopback-only.",
            _ => "Queue attention is required.",
        };

    private void ApplyEnhancementQueueHealth(EnhancementQueueHealthView health)
    {
        EnhancementJobsHealthStateText.Text = health.State;
        EnhancementJobsHealthStateText.Foreground =
            (Brush)FindResource(health.ForegroundResource);
        EnhancementJobsHealthDetailText.Text = health.Detail;
        EnhancementJobsHealthRevisionText.Text = health.Revision;
    }

    private void ApplyEnhancementQueueHealthUnavailable(string detail)
    {
        EnhancementJobsHealthStateText.Text = "Health unavailable";
        EnhancementJobsHealthStateText.Foreground =
            (Brush)FindResource("TextTertiary");
        EnhancementJobsHealthDetailText.Text = detail;
        EnhancementJobsHealthRevisionText.Text = "";
    }

    private void ApplyEnhancementWorkspaceHighlights(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        if (_enhancementWorkspaceHighlightExpiresAt <= DateTimeOffset.UtcNow)
        {
            _enhancementWorkspaceHighlightedJobIds.Clear();
            _enhancementWorkspaceHighlightExpiresAt = default;
        }
        foreach (EnhancementWorkspaceJobView job in jobs)
            job.IsHighlighted = _enhancementWorkspaceHighlightedJobIds.Contains(job.Id);
    }

    private void ReconcileEnhancementWorkspaceJobs(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        Dictionary<string, EnhancementWorkspaceJobView> existingById =
            _enhancementWorkspaceJobs.ToDictionary(static job => job.Id, StringComparer.Ordinal);
        var reconciled = new List<EnhancementWorkspaceJobView>(jobs.Count);
        foreach (EnhancementWorkspaceJobView candidate in jobs)
        {
            if (existingById.TryGetValue(candidate.Id, out EnhancementWorkspaceJobView? existing)
                && existing.HasSameImmutableIdentity(candidate))
            {
                existing.RefreshFrom(candidate);
                reconciled.Add(existing);
            }
            else
            {
                reconciled.Add(candidate);
            }
        }

        _enhancementWorkspaceJobs.Clear();
        _enhancementWorkspaceJobs.AddRange(reconciled);
    }

    private static bool TryParseEnhancementWorkspaceJobs(
        JsonElement payload,
        out List<EnhancementWorkspaceJobView> jobs,
        out string? error)
    {
        jobs = [];
        error = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("jobs", out JsonElement jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Array)
        {
            error = "The companion response does not contain a jobs array.";
            return false;
        }

        int apiOrdinal = 0;
        foreach (JsonElement element in jobsElement.EnumerateArray())
        {
            EnhancementWorkspaceJobView? job = ParseEnhancementWorkspaceJob(element, apiOrdinal++);
            if (job is not null)
                jobs.Add(job);
        }

        AssignEnhancementWorkspaceQueuePositions(jobs);
        jobs.Sort(CompareEnhancementWorkspaceInventory);
        return true;
    }

    private static void AssignEnhancementWorkspaceQueuePositions(
        IReadOnlyCollection<EnhancementWorkspaceJobView> jobs)
    {
        int position = 1;
        EnhancementWorkspaceJobView[] queued = jobs
            .Where(static candidate => candidate.Status == "queued")
            .OrderBy(static candidate => candidate.QueueOrder ?? int.MaxValue)
            .ThenBy(static candidate => candidate.CreatedAt)
            .ThenBy(static candidate => candidate.ApiOrdinal)
            .ToArray();
        bool queueMutationScopeSafe =
            queued.All(static candidate => candidate.IsSupportedMutationOperation);
        foreach (EnhancementWorkspaceJobView job in queued)
        {
            job.QueuePosition = position++;
            job.QueueCount = queued.Length;
            job.QueueMutationScopeSafe = queueMutationScopeSafe;
        }
    }

    private static int CompareEnhancementWorkspaceInventory(
        EnhancementWorkspaceJobView left,
        EnhancementWorkspaceJobView right)
    {
        if (left.IsActive != right.IsActive)
            return left.IsActive ? -1 : 1;

        if (left.IsActive)
        {
            if (left.Status != right.Status)
                return left.Status == "running" ? -1 : 1;
            if (left.Status == "queued")
            {
                int position = (left.QueuePosition ?? int.MaxValue)
                    .CompareTo(right.QueuePosition ?? int.MaxValue);
                if (position != 0)
                    return position;
            }
            int created = left.CreatedAt.CompareTo(right.CreatedAt);
            return created != 0 ? created : left.ApiOrdinal.CompareTo(right.ApiOrdinal);
        }

        int updated = right.UpdatedAt.CompareTo(left.UpdatedAt);
        return updated != 0 ? updated : left.ApiOrdinal.CompareTo(right.ApiOrdinal);
    }

    private static EnhancementWorkspaceJobView? ParseEnhancementWorkspaceJob(
        JsonElement element,
        int apiOrdinal)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(element, "id", out string? id)
            || !TryGetStringProperty(element, "status", out string? rawStatus))
        {
            return null;
        }

        string status = rawStatus!.Trim().ToLowerInvariant();
        if (status is not ("queued" or "running" or "succeeded" or "failed" or "canceled" or "deleted"))
            return null;

        TryGetStringProperty(element, "sourceId", out string? sourceId);
        TryGetStringProperty(element, "sourcePath", out string? sourcePath);
        TryGetStringProperty(
            element,
            "sourceProducerJobId",
            out string? sourceProducerJobId);
        TryGetStringProperty(element, "presetId", out string? presetId);
        TryGetStringProperty(element, "adapterId", out string? adapterId);
        TryGetStringProperty(element, "outputPath", out string? outputPath);
        TryGetStringProperty(element, "errorMessage", out string? errorMessage);
        TryGetStringProperty(element, "createdAt", out string? createdAtText);
        TryGetStringProperty(element, "updatedAt", out string? updatedAtText);
        int progress = element.TryGetProperty("progress", out JsonElement progressElement)
            && progressElement.TryGetInt32(out int parsedProgress)
            ? Math.Clamp(parsedProgress, 0, 100)
            : 0;
        bool cancelRequested =
            element.TryGetProperty(
                "cancelRequested",
                out JsonElement cancelRequestedElement)
            && cancelRequestedElement.ValueKind == JsonValueKind.True;
        int? queueOrder = element.TryGetProperty("queueOrder", out JsonElement queueOrderElement)
            && queueOrderElement.ValueKind == JsonValueKind.Number
            && queueOrderElement.TryGetInt32(out int parsedQueueOrder)
            && parsedQueueOrder >= 0
            ? parsedQueueOrder
            : null;
        long? sourceSize = null;
        double? sourceMtimeMs = null;
        if (element.TryGetProperty("sourceSignature", out JsonElement signature)
            && signature.ValueKind == JsonValueKind.Object)
        {
            if (signature.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize))
                sourceSize = parsedSize;
            if (signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement) && mtimeElement.TryGetDouble(out double parsedMtime))
                sourceMtimeMs = parsedMtime;
        }

        DateTimeOffset.TryParse(createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset createdAt);
        DateTimeOffset.TryParse(updatedAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset updatedAt);
        if (updatedAt == default)
            updatedAt = createdAt == default ? DateTimeOffset.MinValue : createdAt;

        string operation = ReadEnhancementOperation(element);
        return new EnhancementWorkspaceJobView(
            id!,
            sourceId ?? "",
            sourcePath ?? "",
            sourceProducerJobId,
            presetId ?? "Default preset",
            adapterId ?? "local companion",
            operation,
            operation == "video" && IsVideoMutationSafe(element),
            status,
            cancelRequested,
            progress,
            outputPath,
            errorMessage,
            createdAt,
            updatedAt,
            sourceSize,
            sourceMtimeMs,
            queueOrder,
            apiOrdinal);
    }

    private void EnhancementJobsFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter })
        {
            _enhancementWorkspaceFilter = filter;
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        }
    }

    private void ApplyEnhancementWorkspaceFilter(bool loadThumbnails)
    {
        EnhancementWorkspaceJobView[] filtered = _enhancementWorkspaceJobs
            .Where(job => _enhancementWorkspaceFilter switch
            {
                "active" => job.IsActive,
                "queued" => job.Status == "queued",
                "running" => job.Status == "running",
                "failed" => job.Status == "failed",
                "canceled" => job.Status == "canceled",
                "completed" => job.Status is "succeeded" or "deleted",
                "video" => job.IsVideoOperation,
                _ => true,
            })
            .ToArray();
        EnhancementWorkspaceJobView[] current =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray()
            ?? [];
        bool sameItems = current.Length == filtered.Length
            && current.Zip(filtered, static (left, right) => ReferenceEquals(left, right)).All(static same => same);
        if (!sameItems)
            EnhancementJobsList.ItemsSource = filtered;
        EnhancementJobsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (loadThumbnails)
            BeginEnhancementWorkspaceThumbnailLoad(filtered);
    }

    private void BeginEnhancementWorkspaceThumbnailLoad(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        EnhancementWorkspaceJobView[] missing = jobs
            .Where(static job => job.Thumbnail is null)
            .Take(EnhancementJobsThumbnailLimit)
            .ToArray();
        if (EnhancementJobsDialog.Visibility != Visibility.Visible || missing.Length == 0)
            return;

        Interlocked.Exchange(ref _enhancementWorkspaceThumbnailCts, null)?.Cancel();
        var cts = new CancellationTokenSource();
        _enhancementWorkspaceThumbnailCts = cts;
        long generation = _enhancementWorkspaceGeneration;
        _ = LoadEnhancementWorkspaceThumbnailsAsync(missing, generation, cts);
    }

    private async Task LoadEnhancementWorkspaceThumbnailsAsync(
        IReadOnlyList<EnhancementWorkspaceJobView> jobs,
        long generation,
        CancellationTokenSource cts)
    {
        try
        {
            foreach (EnhancementWorkspaceJobView job in jobs)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!TryResolveEnhancementWorkspaceInput(
                        job,
                        out string canonicalSource))
                {
                    continue;
                }

                string cacheKey = $"{canonicalSource}|{job.SourceSize?.ToString(CultureInfo.InvariantCulture)}|{job.SourceMtimeMs?.ToString(CultureInfo.InvariantCulture)}";
                if (!_enhancementWorkspaceThumbnailCache.TryGetValue(cacheKey, out BitmapSource? thumbnail))
                {
                    thumbnail = await Task.Run(() => DecodeEnhancementWorkspaceThumbnail(canonicalSource), cts.Token);
                    if (thumbnail is null)
                        continue;
                    if (_enhancementWorkspaceThumbnailCache.Count >= EnhancementJobsThumbnailCacheLimit)
                        _enhancementWorkspaceThumbnailCache.Remove(_enhancementWorkspaceThumbnailCache.Keys.First());
                    _enhancementWorkspaceThumbnailCache[cacheKey] = thumbnail;
                }

                if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                    return;
                job.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_enhancementWorkspaceThumbnailCts, cts))
            {
                _enhancementWorkspaceThumbnailCts = null;
                cts.Dispose();
            }
        }
    }

    private static BitmapSource? DecodeEnhancementWorkspaceThumbnail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 96;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    private bool TryResolveEnhancementWorkspaceCatalogSource(
        EnhancementWorkspaceJobView job,
        out string canonicalSource)
    {
        canonicalSource = "";
        if (!TryResolveEnhancementSourceIdentity(
                job.SourceId,
                out string sourceIdIdentity)
            || !File.Exists(sourceIdIdentity)
            || !SupportedImageExtensions.Contains(
                Path.GetExtension(sourceIdIdentity)))
        {
            return false;
        }

        canonicalSource = sourceIdIdentity;
        return true;
    }

    private bool TryResolveEnhancementWorkspaceInput(
        EnhancementWorkspaceJobView job,
        out string canonicalInput)
    {
        canonicalInput = "";
        if (job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalCatalogSource)
            || !TryResolveEnhancementSourceIdentity(
                job.SourcePath,
                out string sourcePathIdentity)
            || !File.Exists(sourcePathIdentity)
            || !SupportedImageExtensions.Contains(
                Path.GetExtension(sourcePathIdentity)))
        {
            return false;
        }

        try
        {
            bool usesPhotorealInput = job.IsVideoOperation
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId);
            if (usesPhotorealInput)
            {
                string lexicalInput = Path.GetFullPath(job.SourcePath);
                string lexicalRoot =
                    Path.GetFullPath(ResolvedManagedEnhancementOutputsRoot);
                string canonicalRoot = Path.GetFullPath(
                    _resolveFinalPath(lexicalRoot));
                if (!IsPathInside(lexicalInput, lexicalRoot)
                    || !IsPathInside(sourcePathIdentity, canonicalRoot))
                {
                    return false;
                }
            }
            else if (!EnhancementSourceIdentityComparer.Equals(
                         sourcePathIdentity,
                         canonicalCatalogSource))
            {
                return false;
            }

            var info = new FileInfo(sourcePathIdentity);
            double currentMtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info.Length != job.SourceSize.Value || Math.Abs(currentMtimeMs - job.SourceMtimeMs.Value) > 1)
                return false;
            canonicalInput = sourcePathIdentity;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    private async void CancelEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanCancel)
            await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
    }

    private async void RetryEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanRetry)
            await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry", "Retry queued as a new job.");
    }

    private async void DismissEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanDismiss)
        {
            await RunEnhancementWorkspaceMutationAsync(
                job,
                HttpMethod.Delete,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}",
                "Job removed from history. Source and output files were not changed.");
        }
    }

    private async void MoveEnhancementJobInQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: EnhancementWorkspaceJobView job,
                CommandParameter: string move,
            }
            || move is not ("up" or "down" or "next")
            || !job.CanReorder)
        {
            return;
        }

        string message = move == "next"
            ? "このジョブを次の待機位置へ移動しました。"
            : "待機順を変更しました。";
        await RunEnhancementWorkspaceMutationAsync(
            job,
            HttpMethod.Post,
            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/queue",
            message,
            new { move });
    }

    private async void CancelAllQueuedEnhancementJobs_Click(object sender, RoutedEventArgs e)
    {
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || !CanCancelAllQueuedEnhancementJobs())
        {
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        EnhancementJobsClearQueuedButton.IsEnabled = false;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Delete,
                "api/enhance/jobs/queued");
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            EnhancementJobsStatusText.Text = "待機中のジョブをすべてキャンセルしました。実行中のジョブは変更していません。";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            _enhancementWorkspaceMutationPending = false;
            EnhancementJobsClearQueuedButton.IsEnabled =
                CanCancelAllQueuedEnhancementJobs();
        }
    }

    private async void RerunPhotorealJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || !job.CanRerunWithCurrentSettings
            || !TryResolveEnhancementWorkspaceInput(
                job,
                out string sourceIdentity))
        {
            if (sender is Button { Tag: EnhancementWorkspaceJobView })
                EnhancementJobsStatusText.Text = "元画像を検証できないため、現在設定で再実写化できません。";
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionReadyForExplicitActionAsync(sourceIdentity);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (!readiness.Ok)
            {
                EnhancementJobsStatusText.Text = readiness.Error;
                return;
            }

            ModalPhotorealRequestSettings settings =
                CurrentModalPhotorealRequestSettings();
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/jobs",
                new
                {
                    sourceId = sourceIdentity,
                    operation = "photoreal",
                    presetId = "photoreal-balanced",
                    adapterId = "comfyui-flux2-photoreal",
                    strength = settings.Strength,
                    structureStrength = settings.StructureStrength,
                    steps = settings.Steps,
                    cfgScale = settings.CfgScale,
                    maxDimension = settings.MaxDimension,
                    prompt = settings.Prompt,
                });
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            EnhancementJobsStatusText.Text =
                "現在のPrompt・強さ・構図・CFG・品質・解像度で再実写化を待機列へ追加しました。";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
        }
    }

    private async Task RunEnhancementWorkspaceMutationAsync(
        EnhancementWorkspaceJobView job,
        HttpMethod method,
        string route,
        string successMessage,
        object? body = null)
    {
        if (_enhancementWorkspaceMutationPending || job.IsBusy || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(method, route, body);
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            EnhancementJobsStatusText.Text = successMessage;
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
        }
    }

    private void OpenEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            TryOpenEnhancementWorkspaceOutput(job);
    }

    private bool TryOpenEnhancementWorkspaceOutput(EnhancementWorkspaceJobView job)
    {
        if (!job.CanUseOutput)
        {
            EnhancementJobsStatusText.Text =
                "Open output unavailable: this operation or state is not eligible. The source image was not changed.";
            return false;
        }
        if (job.IsVideoOperation)
        {
            if (!TryResolveManagedVideoWorkspaceOutput(
                    job,
                    out ManagedVideoVersion video,
                    out string videoReason))
            {
                EnhancementJobsStatusText.Text =
                    $"Open output unavailable: {videoReason}. The source image was not changed.";
                return false;
            }

            return TryRevealEnhancementVideoOutputInExplorer(video);
        }
        if (!TryResolveManagedEnhancementWorkspaceOutput(job, out ManagedEnhancedOutput output, out string reason))
        {
            EnhancementJobsStatusText.Text = $"Open output unavailable: {reason}. The source image was not changed.";
            return false;
        }

        return TryOpenEnhancementJobInViewer(job, output);
    }

    private void OpenEnhancementSourceInViewer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            TryOpenEnhancementSourceInViewer(job);
    }

    private bool TryOpenEnhancementSourceInViewer(EnhancementWorkspaceJobView job)
    {
        if (job.IsVideoOperation && job.CanUseOutput)
        {
            if (!TryResolveManagedVideoWorkspaceOutput(
                    job,
                    out ManagedVideoVersion video,
                    out string reason))
            {
                EnhancementJobsStatusText.Text =
                    $"動画を開けません: {reason}. 元画像は変更されていません。";
                return false;
            }
            return TryOpenEnhancementVideoJobInViewer(job, video);
        }

        return TryOpenEnhancementJobInViewer(job, preferredOutput: null);
    }

    private bool TryRevealEnhancementVideoOutputInExplorer(
        ManagedVideoVersion video)
    {
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add($"/select,{video.Output.OutputPath}");
            if (!_explorerLauncher(startInfo))
            {
                EnhancementJobsStatusText.Text =
                    "動画の保存先をExplorerで開けませんでした。もう一度試してください。";
                return false;
            }

            EnhancementJobsStatusText.Text =
                "Explorerで完成動画の保存先を開きました。";
            return true;
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            Trace.TraceWarning(
                $"Enhancement video reveal failed: {ex.GetType().Name}");
            EnhancementJobsStatusText.Text =
                "動画の保存先をExplorerで開けませんでした。もう一度試してください。";
            return false;
        }
    }

    private bool TryOpenEnhancementJobInViewer(
        EnhancementWorkspaceJobView job,
        ManagedEnhancedOutput? preferredOutput,
        ManagedVideoVersion? preferredVideo = null)
    {
        if (!TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource)
            || !File.Exists(canonicalSource))
        {
            EnhancementJobsStatusText.Text = "元画像が見つからないため、ビューワーで開けません。";
            return false;
        }

        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            candidate.IsRealFile
            && string.Equals(candidate.Path, canonicalSource, StringComparison.OrdinalIgnoreCase));
        if (tile is null)
        {
            var sourceInfo = new FileInfo(canonicalSource);
            tile = new Tile
            {
                Path = canonicalSource,
                FileName = Path.GetFileName(canonicalSource),
                IsRealFile = true,
                ModifiedUtc = sourceInfo.LastWriteTimeUtc,
            };
        }

        CaptureEnhancementJobsReturnViewport(job.Id);
        PrepareEnhancementJobsModalTile(tile, canonicalSource);
        if (preferredVideo is not null)
        {
            RememberModalDisplayPreference(
                tile,
                ModalDisplayVersionKind.Video,
                preferredVideo.JobId);
        }
        _returnToEnhancementJobsAfterModalClose = true;
        CloseEnhancementJobsWorkspace(restoreFocus: false);
        SelectTile(tile);
        OpenModal();
        if (Modal.Visibility != Visibility.Visible
            || !string.Equals(SelectedTile()?.Path, canonicalSource, StringComparison.OrdinalIgnoreCase))
        {
            _returnToEnhancementJobsAfterModalClose = false;
            RestoreEnhancementJobsModalSelection();
            return false;
        }

        if (preferredOutput is null)
            return true;

        int versionIndex = _modalEnhancementVersions.FindIndex(candidate =>
            string.Equals(candidate.JobId, job.Id, StringComparison.Ordinal)
            || string.Equals(
                candidate.Output.OutputPath,
                preferredOutput.OutputPath,
                StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0)
        {
            _modalEnhancementVersions.Add(
                new ManagedEnhancementVersion(job.Id, job.Operation, preferredOutput));
            versionIndex = _modalEnhancementVersions.Count - 1;
        }

        _modalEnhancementVersionIndex = versionIndex + 1;
        _modalShowingEnhanced = true;
        OpenModal();
        bool opened = _modalShowingEnhanced
            && string.Equals(
                _modalDisplayPath,
                preferredOutput.OutputPath,
                StringComparison.OrdinalIgnoreCase);
        if (!opened)
            SetStatusToast("The managed output could not be selected in the Aibos viewer.");
        return opened;
    }

    private bool TryOpenEnhancementVideoJobInViewer(
        EnhancementWorkspaceJobView job,
        ManagedVideoVersion preferredVideo)
    {
        if (!TryOpenEnhancementJobInViewer(
                job,
                preferredOutput: null,
                preferredVideo))
            return false;

        if (_modalShowingVideo
            && _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count
            && string.Equals(
                _modalVideoVersions[_modalVideoVersionIndex].JobId,
                preferredVideo.JobId,
                StringComparison.Ordinal))
        {
            return true;
        }

        int versionIndex = _modalVideoVersions.FindIndex(candidate =>
            string.Equals(candidate.JobId, job.Id, StringComparison.Ordinal)
            && string.Equals(
                candidate.Output.OutputPath,
                preferredVideo.Output.OutputPath,
                StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0)
        {
            _modalVideoVersions.Add(preferredVideo);
            versionIndex = _modalVideoVersions.Count - 1;
        }

        StopAndHideModalVideo(clearSource: true);
        bool opened = ShowModalVideoVersion(
            versionIndex,
            autoplay: true);
        if (!opened)
            SetStatusToast("The managed video could not be selected in the Aibos viewer.");
        return opened;
    }

    private void PrepareEnhancementJobsModalTile(Tile tile, string canonicalSource)
    {
        RestoreEnhancementJobsModalSelection();
        _enhancementJobsPreviousSelectionPaths.AddRange(_selectedPaths);
        _enhancementJobsPreviousPrimaryPath = _primarySelectedPath;
        _enhancementJobsModalSelectionCaptured = true;
        _enhancementJobsTrustedModalSourcePath = canonicalSource;
        if (!_tiles.Contains(tile))
        {
            _tiles.Add(tile);
            _enhancementJobsTemporaryVisibleTile = tile;
        }
    }

    private bool IsEnhancementJobsTrustedModalSource(Tile tile)
        => !string.IsNullOrWhiteSpace(_enhancementJobsTrustedModalSourcePath)
            && string.Equals(
                tile.Path,
                _enhancementJobsTrustedModalSourcePath,
                StringComparison.OrdinalIgnoreCase);

    private bool TryResolveEnhancementJobsTrustedModalSource(
        Tile tile,
        out string canonicalSource,
        out string reason)
    {
        canonicalSource = "";
        reason = "the Jobs source is unavailable";
        if (!IsEnhancementJobsTrustedModalSource(tile)
            || string.IsNullOrWhiteSpace(tile.Path)
            || !Path.IsPathFullyQualified(tile.Path)
            || !SupportedImageExtensions.Contains(Path.GetExtension(tile.Path)))
        {
            return false;
        }

        try
        {
            canonicalSource = _resolveFinalPath(Path.GetFullPath(tile.Path));
            if (!File.Exists(canonicalSource))
                return false;
            reason = "";
            return true;
        }
        catch
        {
            canonicalSource = "";
            return false;
        }
    }

    private void RestoreEnhancementJobsModalSelection()
    {
        if (!_enhancementJobsModalSelectionCaptured)
            return;

        _enhancementJobsModalSelectionCaptured = false;
        Tile? temporaryTile = _enhancementJobsTemporaryVisibleTile;
        _enhancementJobsTemporaryVisibleTile = null;
        _enhancementJobsTrustedModalSourcePath = null;
        if (temporaryTile is not null)
            _tiles.Remove(temporaryTile);

        var previousPaths = new HashSet<string>(
            _enhancementJobsPreviousSelectionPaths,
            StringComparer.OrdinalIgnoreCase);
        List<Tile> restored = _tiles
            .Where(tile => previousPaths.Contains(tile.Path))
            .ToList();
        Tile? primary = restored.FirstOrDefault(tile =>
            string.Equals(
                tile.Path,
                _enhancementJobsPreviousPrimaryPath,
                StringComparison.OrdinalIgnoreCase))
            ?? restored.LastOrDefault();
        _enhancementJobsPreviousSelectionPaths.Clear();
        _enhancementJobsPreviousPrimaryPath = null;
        SetSelection(restored, primary);
    }

    private void ReturnToEnhancementJobsAfterModalClose(bool modalWasVisible)
    {
        if (!_returnToEnhancementJobsAfterModalClose || !modalWasVisible)
            return;

        _returnToEnhancementJobsAfterModalClose = false;
        RestoreEnhancementJobsModalSelection();
        string filter = _enhancementWorkspaceFilter;
        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                if (IsLoaded && IsVisible && EnhancementJobsDialog.Visibility != Visibility.Visible)
                    await OpenEnhancementJobsWorkspaceAsync(
                        filter,
                        focusToRestore: OpenEnhancementJobsButton,
                        restoreReturnViewport: true);
            }),
            DispatcherPriority.Background);
    }

    private void CaptureEnhancementJobsReturnViewport(string jobId)
    {
        _enhancementJobsReturnJobId = jobId;
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        _enhancementJobsReturnVerticalOffset = viewer?.VerticalOffset ?? 0;
        _enhancementJobsReturnAnchorViewportTop = double.NaN;
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        if (viewer is not null
            && item is not null
            && TryGetEnhancementJobViewportTop(item, viewer, out double top))
        {
            _enhancementJobsReturnAnchorViewportTop = top;
        }
    }

    private async Task RestoreEnhancementJobsReturnViewportAsync()
    {
        double requestedOffset = Math.Max(0, _enhancementJobsReturnVerticalOffset);
        string? requestedJobId = _enhancementJobsReturnJobId;
        double requestedAnchorTop = _enhancementJobsReturnAnchorViewportTop;
        _enhancementJobsReturnVerticalOffset = 0;
        _enhancementJobsReturnJobId = null;
        _enhancementJobsReturnAnchorViewportTop = double.NaN;

        EnhancementWorkspaceJobView? restoredItem = null;

        void RestoreViewport()
        {
            EnhancementJobsList.UpdateLayout();
            ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
            if (viewer is null)
                return;

            if (restoredItem is not null && double.IsFinite(requestedAnchorTop))
            {
                EnhancementJobsList.ScrollIntoView(restoredItem);
                EnhancementJobsList.UpdateLayout();
                if (TryGetEnhancementJobViewportTop(
                        restoredItem,
                        viewer,
                        out double currentTop))
                {
                    viewer.ScrollToVerticalOffset(Math.Clamp(
                        viewer.VerticalOffset + currentTop - requestedAnchorTop,
                        0,
                        viewer.ScrollableHeight));
                    return;
                }
            }

            viewer.ScrollToVerticalOffset(Math.Min(requestedOffset, viewer.ScrollableHeight));
        }

        await Dispatcher.InvokeAsync(() =>
        {
            EnhancementJobsList.UpdateLayout();
            if (!string.IsNullOrWhiteSpace(requestedJobId))
            {
                restoredItem =
                    (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                        .FirstOrDefault(job => string.Equals(
                            job.Id,
                            requestedJobId,
                            StringComparison.Ordinal));
                EnhancementJobsList.SelectedItem = restoredItem;
            }
            RestoreViewport();
        }, DispatcherPriority.Loaded);

        await Dispatcher.InvokeAsync(RestoreViewport, DispatcherPriority.Render);
    }

    private bool TryGetEnhancementJobViewportTop(
        EnhancementWorkspaceJobView item,
        ScrollViewer viewer,
        out double top)
    {
        top = double.NaN;
        if (EnhancementJobsList.ItemContainerGenerator.ContainerFromItem(item)
                is not FrameworkElement container)
        {
            return false;
        }

        try
        {
            top = container.TranslatePoint(new Point(0, 0), viewer).Y;
            return double.IsFinite(top);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async void DeleteEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || job.IsBusy
            || !job.CanUseOutput)
        {
            return;
        }

        bool validOutput;
        string reason;
        if (job.IsVideoOperation)
        {
            validOutput = TryResolveManagedVideoWorkspaceOutput(job, out _, out reason);
        }
        else
        {
            validOutput = TryResolveManagedEnhancementWorkspaceOutput(job, out _, out reason);
        }
        if (!validOutput)
        {
            EnhancementJobsStatusText.Text = $"Delete output unavailable: {reason}. The source image was not changed.";
            return;
        }

        string mediaName = job.IsVideoOperation ? "video" : "enhanced";
        bool confirmed = _confirmEnhancedOutputDeleteForSmoke?.Invoke() ?? MessageBox.Show(
                this,
                $"Delete only this managed {mediaName} output? The original source image will be kept.",
                $"Delete {mediaName} output",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
            return;

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Delete,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/output");
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            ReloadEnhancedOutputsForVisibleCatalog();
            EnhancementJobsStatusText.Text =
                $"{(job.IsVideoOperation ? "Video" : "Enhanced")} output deleted. The original source image was kept.";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
        }
    }

    private bool TryResolveManagedEnhancementWorkspaceOutput(
        EnhancementWorkspaceJobView job,
        out ManagedEnhancedOutput managedOutput,
        out string reason)
    {
        managedOutput = null!;
        reason = "the output is missing, stale, or outside managed storage";
        if (!job.IsImageOperation
            || job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceInput(
                job,
                out string canonicalSource))
        {
            return false;
        }

        var tile = new Tile { Path = canonicalSource, IsRealFile = true };
        if (!TryCreateManagedEnhancedOutput(
                tile,
                job.OutputPath,
                job.SourceSize.Value,
                job.SourceMtimeMs.Value,
                out managedOutput))
            return false;

        reason = "";
        return true;
    }

    private bool TryResolveManagedVideoWorkspaceOutput(
        EnhancementWorkspaceJobView job,
        out ManagedVideoVersion managedVideo,
        out string reason)
    {
        managedVideo = null!;
        reason = "the video is missing, stale, malformed, or outside managed storage";
        if (!job.IsVideoOperation
            || job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource)
            || !ReloadEnhancedOutputsForVisibleCatalog())
        {
            return false;
        }

        try
        {
            string canonicalJobOutput =
                _resolveFinalPath(Path.GetFullPath(job.OutputPath));
            ManagedVideoVersion? candidate = GetManagedVideoVersionsForPath(canonicalSource)
                .FirstOrDefault(version =>
                    string.Equals(version.JobId, job.Id, StringComparison.Ordinal)
                    && string.Equals(
                        version.Output.OutputPath,
                        canonicalJobOutput,
                        StringComparison.OrdinalIgnoreCase));
            if (candidate is null
                || candidate.Output.SourceSize != job.SourceSize.Value
                || Math.Abs(candidate.Output.SourceMtimeMs - job.SourceMtimeMs.Value) > 1
                || !string.Equals(
                    candidate.PresetId,
                    job.PresetId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    candidate.BackendId,
                    job.AdapterId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            managedVideo = candidate;
            reason = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    private bool ReloadEnhancedOutputsForVisibleCatalog()
    {
        if (!LoadEnhancedState())
            return false;

        ApplyEnhancedOutputsToVisibleCatalog();
        return true;
    }

    private void ApplyEnhancedOutputsToVisibleCatalog()
    {
        foreach (Tile tile in _allTiles)
        {
            bool enhanced = TryGetCatalogManagedEnhancedOutputForPath(
                tile.Path,
                out ManagedEnhancedOutput output);
            string? outputPath = enhanced ? output.OutputPath : null;
            tile.EnhancedOutputPath = outputPath;
            ApplyTileEnhancementAvailability(
                tile,
                GetCatalogManagedEnhancementVersionsForPath(tile.Path));
            ApplyTileVideoAvailability(tile);
        }
    }

    public async Task OpenEnhancementJobsForSmokeAsync()
    {
        OpenEnhancementJobs_Click(this, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
    }

    public List<string> EnhancementWorkspaceCatalogPathsForSmoke
        => _allTiles.Where(static tile => tile.IsRealFile).Select(static tile => tile.Path).ToList();

    public void CloseEnhancementJobsForSmoke() => CloseEnhancementJobsWorkspace(restoreFocus: false);

    public double EnhancementJobsVerticalOffsetForSmoke
        => FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.VerticalOffset ?? 0;

    public double EnhancementJobViewportTopForSmoke(string jobId)
    {
        EnhancementJobsList.UpdateLayout();
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        return viewer is not null
            && item is not null
            && TryGetEnhancementJobViewportTop(item, viewer, out double top)
                ? top
                : double.NaN;
    }

    public double PositionEnhancementJobForSmoke(
        string jobId,
        double requestedViewportTop)
    {
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        if (item is null || viewer is null)
            return double.NaN;

        EnhancementJobsList.ScrollIntoView(item);
        EnhancementJobsList.UpdateLayout();
        if (!TryGetEnhancementJobViewportTop(item, viewer, out double currentTop))
            return double.NaN;

        viewer.ScrollToVerticalOffset(Math.Clamp(
            viewer.VerticalOffset + currentTop - requestedViewportTop,
            0,
            viewer.ScrollableHeight));
        EnhancementJobsList.UpdateLayout();
        return TryGetEnhancementJobViewportTop(item, viewer, out double positionedTop)
            ? positionedTop
            : double.NaN;
    }

    public string? SelectedEnhancementJobIdForSmoke
        => (EnhancementJobsList.SelectedItem as EnhancementWorkspaceJobView)?.Id;

    public double ScrollEnhancementJobsForSmoke(double offset)
    {
        EnhancementJobsList.UpdateLayout();
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        if (viewer is null)
            return 0;
        viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        EnhancementJobsList.UpdateLayout();
        return viewer.VerticalOffset;
    }

    public double EnhancementJobsVerticalThumbSlotHeightForSmoke
    {
        get
        {
            EnhancementJobsList.UpdateLayout();
            System.Windows.Controls.Primitives.ScrollBar? bar =
                FindVisualDescendants<System.Windows.Controls.Primitives.ScrollBar>(EnhancementJobsList)
                .FirstOrDefault(static candidate =>
                    candidate.Orientation == Orientation.Vertical
                    && candidate.IsVisible);
            System.Windows.Controls.Primitives.Track? track = bar is null
                ? null
                : FindVisualDescendant<System.Windows.Controls.Primitives.Track>(bar);
            return track?.Thumb is null
                ? 0
                : System.Windows.Controls.Primitives.LayoutInformation
                    .GetLayoutSlot(track.Thumb).Height;
        }
    }

    public void SelectEnhancementJobsFilterForSmoke(string filter)
    {
        _enhancementWorkspaceFilter = filter;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
    }

    public EnhancementJobsWorkspaceSmokeSnapshot EnhancementJobsWorkspaceForSmoke()
    {
        var visible = (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray() ?? [];
        return new EnhancementJobsWorkspaceSmokeSnapshot(
            EnhancementJobsDialog.Visibility == Visibility.Visible,
            _enhancementWorkspaceJobs.Count,
            visible.Length,
            _enhancementWorkspaceJobs.Count(static job => job.IsActive),
            visible.Count(static job => job.IsHighlighted),
            _enhancementWorkspacePollTimer.IsEnabled,
            _enhancementWorkspaceGetCount,
            _enhancementWorkspacePollCount,
            EnhancementJobsStatusText.Text,
            _enhancementWorkspaceHealthGetCount,
            EnhancementJobsHealthStateText.Text,
            EnhancementJobsHealthDetailText.Text,
            EnhancementJobsHealthRevisionText.Text,
            visible.Select(static job => job.Id).ToArray(),
            visible.Select(static job => job.StatusLabel).ToArray(),
            visible.Select(static job => job.OperationLabel).ToArray());
    }

    public async Task<bool> CancelEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanCancel)
            return false;
        await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> RetryEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanRetry)
            return false;
        await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry", "Retry queued as a new job.");
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> MoveEnhancementJobForSmokeAsync(string id, string move)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanReorder || move is not ("up" or "down" or "next"))
            return false;
        await RunEnhancementWorkspaceMutationAsync(
            job,
            HttpMethod.Post,
            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/queue",
            "Queue order changed.",
            new { move });
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> CancelAllQueuedEnhancementJobsForSmokeAsync()
    {
        if (!CanCancelAllQueuedEnhancementJobs())
            return false;
        CancelAllQueuedEnhancementJobs_Click(this, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> RerunPhotorealJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanRerunWithCurrentSettings)
            return false;
        RerunPhotorealJob_Click(new Button { Tag = job }, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> DeleteEnhancementJobOutputForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanUseOutput)
            return false;
        var button = new Button { Tag = job };
        DeleteEnhancementOutput_Click(button, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> DismissEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanDismiss)
            return false;
        DismissEnhancementJob_Click(new Button { Tag = job }, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public bool OpenEnhancementJobOutputForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementWorkspaceOutput(job);
    }

    public ExplorerRevealSmokeSnapshot RevealEnhancementJobOutputForSmoke(
        string id)
    {
        ProcessStartInfo? captured = null;
        Func<ProcessStartInfo, bool> previous = _explorerLauncher;
        _explorerLauncher = startInfo =>
        {
            captured = startInfo;
            return true;
        };
        try
        {
            EnhancementWorkspaceJobView? job =
                _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
            bool opened = job is not null
                && TryOpenEnhancementWorkspaceOutput(job);
            return new ExplorerRevealSmokeSnapshot(
                opened && captured is not null,
                captured?.FileName ?? "",
                captured?.ArgumentList.ToList() ?? [],
                captured?.Arguments ?? "",
                captured?.UseShellExecute ?? false,
                job is { IsVideoOperation: true, CanUseOutput: true },
                false,
                EnhancementJobsStatusText.Text,
                "jobs-output");
        }
        finally
        {
            _explorerLauncher = previous;
        }
    }

    public bool OpenEnhancementJobSourceInViewerForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementSourceInViewer(job);
    }

    public bool EnhancementJobsHeaderChromeContractForSmoke
        => WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsCloseButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsRefreshButton)
            && string.Equals(
                AutomationProperties.GetName(EnhancementJobsVideoFilter),
                "Show video generation jobs",
                StringComparison.Ordinal);

    public bool ActivateEnhancementJobsCloseForSmoke()
    {
        EnhancementJobsCloseButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        return EnhancementJobsDialog.Visibility != Visibility.Visible;
    }

    public object? EnhancementJobViewIdentityForSmoke(string id)
        => _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);

    public async Task RefreshEnhancementJobsForSmokeAsync()
    {
        await RefreshEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration, isPoll: false);
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
    }

    public async Task WaitForEnhancementJobsReturnForSmokeAsync()
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (EnhancementJobsDialog.Visibility == Visibility.Visible
                && !_enhancementWorkspaceRefreshPending)
            {
                return;
            }
            await Task.Delay(10);
        }
    }

    private async Task WaitForEnhancementWorkspaceIdleForSmokeAsync()
    {
        for (int attempt = 0; attempt < 400 && (_enhancementWorkspaceRefreshPending || _enhancementWorkspaceMutationPending); attempt++)
            await Task.Delay(10);
    }
}

public sealed class EnhancementWorkspaceJobView : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isBusy;
    private bool _isHighlighted;

    public EnhancementWorkspaceJobView(
        string id,
        string sourceId,
        string sourcePath,
        string? sourceProducerJobId,
        string presetId,
        string adapterId,
        string operation,
        bool videoMutationSafe,
        string status,
        bool cancelRequested,
        int progress,
        string? outputPath,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long? sourceSize,
        double? sourceMtimeMs,
        int? queueOrder,
        int apiOrdinal)
    {
        Id = id;
        SourceId = sourceId;
        SourcePath = sourcePath;
        SourceProducerJobId = sourceProducerJobId;
        PresetId = presetId;
        AdapterId = adapterId;
        Operation = operation;
        VideoMutationSafe = videoMutationSafe;
        Status = status;
        CancelRequested = cancelRequested;
        Progress = progress;
        OutputPath = outputPath;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        SourceSize = sourceSize;
        SourceMtimeMs = sourceMtimeMs;
        QueueOrder = queueOrder;
        ApiOrdinal = apiOrdinal;
    }

    public string Id { get; }
    public string SourceId { get; }
    public string SourcePath { get; }
    public string? SourceProducerJobId { get; }
    public string PresetId { get; }
    public string AdapterId { get; }
    public string Operation { get; }
    public bool VideoMutationSafe { get; }
    public string Status { get; private set; }
    public bool CancelRequested { get; private set; }
    public int Progress { get; private set; }
    public string? OutputPath { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long? SourceSize { get; }
    public double? SourceMtimeMs { get; }
    public int? QueueOrder { get; private set; }
    public int ApiOrdinal { get; }
    public int? QueuePosition { get; set; }
    public int QueueCount { get; set; }
    public bool IsActive => Status is "queued" or "running";
    public bool IsImageOperation => Operation is "upscale" or "photoreal";
    public bool IsVideoOperation => Operation == "video";
    public bool IsSupportedMutationOperation =>
        IsImageOperation || (IsVideoOperation && VideoMutationSafe);
    public bool QueueMutationScopeSafe { get; set; } = true;
    public bool CanCancel =>
        !_isBusy
        && !CancelRequested
        && IsSupportedMutationOperation
        && Status is "queued" or "running" or "failed";
    public bool CanRetry =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status is "failed" or "canceled";
    public bool CanDismiss =>
        !_isBusy
        && Status is "failed" or "canceled" or "deleted";
    public bool CanReorder =>
        !_isBusy
        && IsSupportedMutationOperation
        && QueueMutationScopeSafe
        && Status == "queued";
    public bool CanMoveUp => CanReorder && QueuePosition is > 1;
    public bool CanMoveDown => CanReorder
        && QueuePosition is int position
        && position < QueueCount;
    public bool CanMoveNext => CanMoveUp;
    public bool CanRerunWithCurrentSettings =>
        !_isBusy && Status == "succeeded" && Operation == "photoreal";
    public bool CanUseOutput =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status == "succeeded"
        && !string.IsNullOrWhiteSpace(OutputPath);
    public string ThumbnailToolTip => IsVideoOperation && CanUseOutput
        ? "完成動画をAibosの拡大ビューで再生"
        : "元画像をAibosのビューワーで開く";
    public string OpenOutputToolTip => IsVideoOperation
        ? "Explorerで完成動画の保存先を開く"
        : "このAI処理版をAibosの拡大ビューで開く";
    public string CancelLabel => Status switch
    {
        "queued" => "待機を削除",
        "running" when Operation == "photoreal" => "実写化を中止",
        "running" when Operation == "video" => "動画化を中止",
        "running" when !IsImageOperation => "未対応操作",
        "running" => "高画質化を中止",
        _ => "キャンセル済みにする",
    };
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath) ? "Unknown source" : Path.GetFileName(SourcePath);
    public string SourceVersionLabel => IsVideoOperation
        && !string.IsNullOrWhiteSpace(SourceProducerJobId)
            ? "実写版"
            : "Original";
    public string PresetSummary => IsVideoOperation
        ? $"{(PresetId switch
        {
            "wan22-ti2v-5b-normal-v1" => "Wan2.2 TI2V 5B · 標準 · 20 step",
            "wan22-ti2v-5b-high-v1" => "Wan2.2 TI2V 5B · 高品質 · 40 step",
            _ => PresetId,
        })}  ·  {SourceVersionLabel}"
        : $"{PresetId}  ·  {AdapterId}";
    public string OperationLabel => Operation switch
    {
        "upscale" => "HQ  高画質化",
        "photoreal" => "REAL  実写化",
        "video" => "VIDEO  動画化",
        _ => "UNSUPPORTED  未対応",
    };
    public string StatusLabel => CancelRequested && Status == "running"
        ? $"中止処理中  ·  Running {Progress}%"
        : Status switch
        {
            "queued" => $"待ち順 {QueuePosition ?? 0}  ·  Queued {Progress}%",
            "running" => $"実行中  ·  Running {Progress}%",
            "succeeded" => "Completed",
            "failed" => "Failed",
            "canceled" => "Canceled",
            "deleted" => "Output deleted",
            _ => Status,
        };
    public string DetailText => !string.IsNullOrWhiteSpace(ErrorMessage)
        ? ErrorMessage
        : CancelRequested && Status == "running"
            ? "Cancel requested. Waiting for the exact GPU prompt to settle before the next job starts."
        : IsVideoOperation
            ? !VideoMutationSafe
                ? "This video row is incomplete or incompatible and remains protected from mutations."
                : Status == "succeeded"
                    ? $"Managed video output from {SourceVersionLabel} is separate from the original source image."
                    : $"Video generation from {SourceVersionLabel} uses the same durable queue and GPU worker as image Enhancement."
            : !IsImageOperation
                ? "This operation is unsupported and protected from image actions."
                : Status == "succeeded"
                    ? "Managed output is separate from the original source."
                    : Status == "deleted"
                        ? "Managed output removed; original source kept."
                        : "Original source remains unchanged.";
    public string TimestampText => UpdatedAt == DateTimeOffset.MinValue
        ? "Time unavailable"
        : $"Updated {UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    public string AccessibleName => $"{SourceName}, {OperationLabel}, {StatusLabel}, {PresetId}";

    public bool HasSameImmutableIdentity(EnhancementWorkspaceJobView candidate)
        => string.Equals(SourceId, candidate.SourceId, StringComparison.Ordinal)
            && string.Equals(SourcePath, candidate.SourcePath, StringComparison.Ordinal)
            && string.Equals(
                SourceProducerJobId,
                candidate.SourceProducerJobId,
                StringComparison.Ordinal)
            && string.Equals(PresetId, candidate.PresetId, StringComparison.Ordinal)
            && string.Equals(AdapterId, candidate.AdapterId, StringComparison.Ordinal)
            && string.Equals(Operation, candidate.Operation, StringComparison.Ordinal)
            && VideoMutationSafe == candidate.VideoMutationSafe
            && CreatedAt == candidate.CreatedAt
            && SourceSize == candidate.SourceSize
            && SourceMtimeMs == candidate.SourceMtimeMs;

    public void RefreshFrom(EnhancementWorkspaceJobView candidate)
    {
        bool statusChanged = !string.Equals(Status, candidate.Status, StringComparison.Ordinal);
        bool cancelRequestedChanged = CancelRequested != candidate.CancelRequested;
        bool progressChanged = Progress != candidate.Progress;
        bool outputChanged = !string.Equals(OutputPath, candidate.OutputPath, StringComparison.OrdinalIgnoreCase);
        bool errorChanged = !string.Equals(ErrorMessage, candidate.ErrorMessage, StringComparison.Ordinal);
        bool updatedChanged = UpdatedAt != candidate.UpdatedAt;
        bool queueChanged = QueuePosition != candidate.QueuePosition;
        bool queueCountChanged = QueueCount != candidate.QueueCount;
        bool queueOrderChanged = QueueOrder != candidate.QueueOrder;
        bool queueMutationScopeChanged =
            QueueMutationScopeSafe != candidate.QueueMutationScopeSafe;

        Status = candidate.Status;
        CancelRequested = candidate.CancelRequested;
        Progress = candidate.Progress;
        OutputPath = candidate.OutputPath;
        ErrorMessage = candidate.ErrorMessage;
        UpdatedAt = candidate.UpdatedAt;
        QueueOrder = candidate.QueueOrder;
        QueuePosition = candidate.QueuePosition;
        QueueCount = candidate.QueueCount;
        QueueMutationScopeSafe = candidate.QueueMutationScopeSafe;
        IsHighlighted = candidate.IsHighlighted;

        if (progressChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        if (statusChanged
            || cancelRequestedChanged
            || progressChanged
            || queueChanged
            || queueCountChanged
            || queueOrderChanged
            || queueMutationScopeChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReorder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CancelLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunWithCurrentSettings)));
        }
        if (statusChanged || outputChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailToolTip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenOutputToolTip)));
        }
        if (statusChanged
            || cancelRequestedChanged
            || outputChanged
            || errorChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
        if (updatedChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText)));
        if (statusChanged
            || cancelRequestedChanged
            || progressChanged
            || queueChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value)
                return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReorder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunWithCurrentSettings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseOutput)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal readonly record struct EnhancementQueueHealthView(
    string State,
    string Detail,
    string Revision,
    string ForegroundResource);

public sealed record EnhancementJobsWorkspaceSmokeSnapshot(
    bool Visible,
    int Total,
    int Filtered,
    int Active,
    int Highlighted,
    bool Polling,
    int GetRequests,
    int PollRequests,
    string Status,
    int HealthGetRequests,
    string HealthState,
    string HealthDetail,
    string HealthRevision,
    string[] VisibleIds,
    string[] VisibleStatusLabels,
    string[] VisibleOperationLabels);
