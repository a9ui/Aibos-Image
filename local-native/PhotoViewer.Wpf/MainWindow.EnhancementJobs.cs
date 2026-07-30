using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementJobsThumbnailLimit = 48;
    private const int EnhancementJobsThumbnailCacheLimit = 96;
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
    private bool _returnToEnhancementJobsAfterModalClose;
    private Tile? _enhancementJobsTemporaryVisibleTile;
    private string? _enhancementJobsTrustedModalSourcePath;
    private readonly List<string> _enhancementJobsPreviousSelectionPaths = [];
    private string? _enhancementJobsPreviousPrimaryPath;
    private bool _enhancementJobsModalSelectionCaptured;

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
        IInputElement? focusToRestore = null)
    {
        if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            return;

        StopGalleryAutoScroll();
        SearchHistoryPopup.IsOpen = false;
        _enhancementWorkspaceFocusBeforeDialog = focusToRestore ?? Keyboard.FocusedElement;
        _enhancementWorkspaceFilter = initialFilter is "queued" or "running" or "failed" or "canceled" or "completed"
            ? initialFilter
            : "all";
        EnhancementJobsAllFilter.IsChecked = _enhancementWorkspaceFilter == "all";
        EnhancementJobsQueuedFilter.IsChecked = _enhancementWorkspaceFilter == "queued";
        EnhancementJobsRunningFilter.IsChecked = _enhancementWorkspaceFilter == "running";
        EnhancementJobsFailedFilter.IsChecked = _enhancementWorkspaceFilter == "failed";
        EnhancementJobsCanceledFilter.IsChecked = _enhancementWorkspaceFilter == "canceled";
        EnhancementJobsCompletedFilter.IsChecked = _enhancementWorkspaceFilter == "completed";
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
        EnhancementJobsEmptyText.Visibility = Visibility.Collapsed;
        EnhancementJobsList.ItemsSource = null;
        long generation = ++_enhancementWorkspaceGeneration;
        _ = Dispatcher.BeginInvoke(EnhancementJobsRefreshButton.Focus, DispatcherPriority.Input);
        await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
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
                queuedCount > 0 && !_enhancementWorkspaceMutationPending;
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
            if (existingById.TryGetValue(candidate.Id, out EnhancementWorkspaceJobView? existing))
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
        foreach (EnhancementWorkspaceJobView job in queued)
        {
            job.QueuePosition = position++;
            job.QueueCount = queued.Length;
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
        TryGetStringProperty(element, "presetId", out string? presetId);
        TryGetStringProperty(element, "adapterId", out string? adapterId);
        TryGetStringProperty(element, "operation", out string? rawOperation);
        TryGetStringProperty(element, "outputPath", out string? outputPath);
        TryGetStringProperty(element, "errorMessage", out string? errorMessage);
        TryGetStringProperty(element, "createdAt", out string? createdAtText);
        TryGetStringProperty(element, "updatedAt", out string? updatedAtText);
        int progress = element.TryGetProperty("progress", out JsonElement progressElement)
            && progressElement.TryGetInt32(out int parsedProgress)
            ? Math.Clamp(parsedProgress, 0, 100)
            : 0;
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

        return new EnhancementWorkspaceJobView(
            id!,
            sourceId ?? "",
            sourcePath ?? "",
            presetId ?? "Default preset",
            adapterId ?? "local companion",
            string.Equals(rawOperation?.Trim(), "photoreal", StringComparison.OrdinalIgnoreCase)
                ? "photoreal"
                : "upscale",
            status,
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
                if (!TryResolveEnhancementWorkspaceSource(job, out string canonicalSource))
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

    private bool TryResolveEnhancementWorkspaceSource(EnhancementWorkspaceJobView job, out string canonicalSource)
    {
        canonicalSource = "";
        if (job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementSourceIdentity(job.SourcePath, out string sourcePathIdentity)
            || !TryResolveEnhancementSourceIdentity(job.SourceId, out string sourceIdIdentity)
            || !EnhancementSourceIdentityComparer.Equals(sourcePathIdentity, sourceIdIdentity)
            || !File.Exists(sourcePathIdentity)
            || !SupportedImageExtensions.Contains(Path.GetExtension(sourcePathIdentity)))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(sourcePathIdentity);
            double currentMtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info.Length != job.SourceSize.Value || Math.Abs(currentMtimeMs - job.SourceMtimeMs.Value) > 1)
                return false;
            canonicalSource = sourcePathIdentity;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private async void CancelEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
    }

    private async void RetryEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry", "Retry queued as a new job.");
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
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
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
                _enhancementWorkspaceJobs.Any(static job => job.Status == "queued");
        }
    }

    private async void RerunPhotorealJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || !job.CanRerunWithCurrentSettings
            || !TryResolveEnhancementWorkspaceSource(job, out string sourceIdentity))
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
        => TryOpenEnhancementJobInViewer(job, preferredOutput: null);

    private bool TryOpenEnhancementJobInViewer(
        EnhancementWorkspaceJobView job,
        ManagedEnhancedOutput? preferredOutput)
    {
        if (!TryResolveEnhancementWorkspaceSource(job, out string canonicalSource)
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

        PrepareEnhancementJobsModalTile(tile, canonicalSource);
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
                    await OpenEnhancementJobsWorkspaceAsync(filter, focusToRestore: OpenEnhancementJobsButton);
            }),
            DispatcherPriority.Background);
    }

    private async void DeleteEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || job.IsBusy)
        {
            return;
        }

        if (!TryResolveManagedEnhancementWorkspaceOutput(job, out _, out string reason))
        {
            EnhancementJobsStatusText.Text = $"Delete output unavailable: {reason}. The source image was not changed.";
            return;
        }

        bool confirmed = _confirmEnhancedOutputDeleteForSmoke?.Invoke() ?? MessageBox.Show(
                this,
                "Delete only this enhanced output? The original source image will be kept.",
                "Delete enhanced output",
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
            EnhancementJobsStatusText.Text = "Enhanced output deleted. The original source image was kept.";
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
        if (job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceSource(job, out string canonicalSource))
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
        if (!_enhancementWorkspaceJobs.Any(static job => job.Status == "queued"))
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
        if (job is null)
            return false;
        var button = new Button { Tag = job };
        DeleteEnhancementOutput_Click(button, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public bool OpenEnhancementJobOutputForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementWorkspaceOutput(job);
    }

    public bool OpenEnhancementJobSourceInViewerForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementSourceInViewer(job);
    }

    public bool EnhancementJobsHeaderChromeContractForSmoke
        => WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsCloseButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsRefreshButton);

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
        string presetId,
        string adapterId,
        string operation,
        string status,
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
        PresetId = presetId;
        AdapterId = adapterId;
        Operation = operation;
        Status = status;
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
    public string PresetId { get; }
    public string AdapterId { get; }
    public string Operation { get; }
    public string Status { get; private set; }
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
    public bool CanCancel => !_isBusy && Status is "queued" or "running" or "failed";
    public bool CanRetry => !_isBusy && Status is "failed" or "canceled";
    public bool CanReorder => !_isBusy && Status == "queued";
    public bool CanMoveUp => CanReorder && QueuePosition is > 1;
    public bool CanMoveDown => CanReorder
        && QueuePosition is int position
        && position < QueueCount;
    public bool CanMoveNext => CanMoveUp;
    public bool CanRerunWithCurrentSettings =>
        !_isBusy && Status == "succeeded" && Operation == "photoreal";
    public bool CanUseOutput => !_isBusy && Status == "succeeded" && !string.IsNullOrWhiteSpace(OutputPath);
    public string CancelLabel => Status switch
    {
        "queued" => "待機を削除",
        "running" when Operation == "photoreal" => "実写化を中止",
        "running" => "高画質化を中止",
        _ => "キャンセル済みにする",
    };
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath) ? "Unknown source" : Path.GetFileName(SourcePath);
    public string PresetSummary => $"{PresetId}  ·  {AdapterId}";
    public string OperationLabel => Operation == "photoreal" ? "REAL  実写化" : "HQ  高画質化";
    public string StatusLabel => Status switch
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
        : Status == "succeeded"
            ? "Managed output is separate from the original source."
            : Status == "deleted"
                ? "Managed output removed; original source kept."
                : "Original source remains unchanged.";
    public string TimestampText => UpdatedAt == DateTimeOffset.MinValue
        ? "Time unavailable"
        : $"Updated {UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    public string AccessibleName => $"{SourceName}, {OperationLabel}, {StatusLabel}, {PresetId}";

    public void RefreshFrom(EnhancementWorkspaceJobView candidate)
    {
        bool statusChanged = !string.Equals(Status, candidate.Status, StringComparison.Ordinal);
        bool progressChanged = Progress != candidate.Progress;
        bool outputChanged = !string.Equals(OutputPath, candidate.OutputPath, StringComparison.OrdinalIgnoreCase);
        bool errorChanged = !string.Equals(ErrorMessage, candidate.ErrorMessage, StringComparison.Ordinal);
        bool updatedChanged = UpdatedAt != candidate.UpdatedAt;
        bool queueChanged = QueuePosition != candidate.QueuePosition;
        bool queueCountChanged = QueueCount != candidate.QueueCount;
        bool queueOrderChanged = QueueOrder != candidate.QueueOrder;

        Status = candidate.Status;
        Progress = candidate.Progress;
        OutputPath = candidate.OutputPath;
        ErrorMessage = candidate.ErrorMessage;
        UpdatedAt = candidate.UpdatedAt;
        QueueOrder = candidate.QueueOrder;
        QueuePosition = candidate.QueuePosition;
        QueueCount = candidate.QueueCount;
        IsHighlighted = candidate.IsHighlighted;

        if (progressChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        if (statusChanged || progressChanged || queueChanged || queueCountChanged || queueOrderChanged)
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseOutput)));
        if (statusChanged || outputChanged || errorChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
        if (updatedChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText)));
        if (statusChanged || progressChanged || queueChanged)
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
    string[] VisibleIds,
    string[] VisibleStatusLabels,
    string[] VisibleOperationLabels);
