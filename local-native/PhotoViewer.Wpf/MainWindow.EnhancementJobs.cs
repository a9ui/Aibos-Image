using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
        _enhancementWorkspaceFilter = initialFilter is "active" or "failed" or "completed"
            ? initialFilter
            : "all";
        EnhancementJobsAllFilter.IsChecked = _enhancementWorkspaceFilter == "all";
        EnhancementJobsActiveFilter.IsChecked = _enhancementWorkspaceFilter == "active";
        EnhancementJobsFailedFilter.IsChecked = _enhancementWorkspaceFilter == "failed";
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
            _enhancementWorkspaceJobs.Clear();
            _enhancementWorkspaceJobs.AddRange(jobs);
            bool highlightedBatchAlreadyTerminal = _enhancementWorkspaceFilter == "active"
                && jobs.Any(static job => job.IsHighlighted)
                && !jobs.Any(static job => job.IsHighlighted && job.IsActive);
            if (highlightedBatchAlreadyTerminal)
            {
                _enhancementWorkspaceFilter = "all";
                EnhancementJobsAllFilter.IsChecked = true;
                EnhancementJobsActiveFilter.IsChecked = false;
            }
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            int activeCount = jobs.Count(static job => job.IsActive);
            int completedCount = jobs.Count(static job => job.Status is "succeeded" or "deleted");
            EnhancementJobsHeaderSummary.Text = $"{jobs.Count:N0} total  ·  {activeCount:N0} active  ·  {completedCount:N0} completed";
            EnhancementJobsStatusText.Text = activeCount > 0
                ? $"{activeCount:N0} active job{(activeCount == 1 ? "" : "s")}. Refreshing once per second while this workspace is visible."
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

        foreach (JsonElement element in jobsElement.EnumerateArray())
        {
            EnhancementWorkspaceJobView? job = ParseEnhancementWorkspaceJob(element);
            if (job is not null)
                jobs.Add(job);
        }

        jobs.Sort(static (left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        return true;
    }

    private static EnhancementWorkspaceJobView? ParseEnhancementWorkspaceJob(JsonElement element)
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
        TryGetStringProperty(element, "outputPath", out string? outputPath);
        TryGetStringProperty(element, "errorMessage", out string? errorMessage);
        TryGetStringProperty(element, "createdAt", out string? createdAtText);
        TryGetStringProperty(element, "updatedAt", out string? updatedAtText);
        int progress = element.TryGetProperty("progress", out JsonElement progressElement)
            && progressElement.TryGetInt32(out int parsedProgress)
            ? Math.Clamp(parsedProgress, 0, 100)
            : 0;
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
            status,
            progress,
            outputPath,
            errorMessage,
            createdAt,
            updatedAt,
            sourceSize,
            sourceMtimeMs);
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
                "failed" => job.Status is "failed" or "canceled",
                "completed" => job.Status is "succeeded" or "deleted",
                _ => true,
            })
            .ToArray();
        EnhancementJobsList.ItemsSource = filtered;
        EnhancementJobsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (loadThumbnails)
            BeginEnhancementWorkspaceThumbnailLoad(filtered);
    }

    private void BeginEnhancementWorkspaceThumbnailLoad(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        Interlocked.Exchange(ref _enhancementWorkspaceThumbnailCts, null)?.Cancel();
        if (EnhancementJobsDialog.Visibility != Visibility.Visible || jobs.Count == 0)
            return;

        var cts = new CancellationTokenSource();
        _enhancementWorkspaceThumbnailCts = cts;
        long generation = _enhancementWorkspaceGeneration;
        _ = LoadEnhancementWorkspaceThumbnailsAsync(jobs.Take(EnhancementJobsThumbnailLimit).ToArray(), generation, cts);
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

    private async Task RunEnhancementWorkspaceMutationAsync(
        EnhancementWorkspaceJobView job,
        HttpMethod method,
        string route,
        string successMessage)
    {
        if (_enhancementWorkspaceMutationPending || job.IsBusy || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(method, route);
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
        if (!TryResolveManagedEnhancementWorkspaceOutput(job, out string outputPath, out string reason))
        {
            EnhancementJobsStatusText.Text = $"Open output unavailable: {reason}. The source image was not changed.";
            return false;
        }

        try
        {
            if (!_externalFileLauncher(new ProcessStartInfo(outputPath) { UseShellExecute = true }))
                throw new InvalidOperationException("No default application accepted the output.");
            EnhancementJobsStatusText.Text = "Opened the managed enhanced output. The source image remains separate.";
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning($"Enhancement workspace output open failed: {ex.GetType().Name}");
            EnhancementJobsStatusText.Text = "The enhanced output could not be opened. Check its default application and try again.";
            return false;
        }
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
        out string outputPath,
        out string reason)
    {
        outputPath = "";
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
        if (!TryCreateManagedEnhancedOutput(tile, job.OutputPath, job.SourceSize.Value, job.SourceMtimeMs.Value, out ManagedEnhancedOutput managed))
            return false;

        outputPath = managed.OutputPath;
        reason = "";
        return true;
    }

    private bool ReloadEnhancedOutputsForVisibleCatalog()
    {
        if (!LoadEnhancedState())
            return false;

        foreach (Tile tile in _allTiles)
        {
            bool enhanced = TryGetCatalogManagedEnhancedOutputForPath(
                tile.Path,
                out ManagedEnhancedOutput output);
            string? outputPath = enhanced ? output.OutputPath : null;
            tile.EnhancedOutputPath = outputPath;
            tile.Enhanced = enhanced;
        }
        return true;
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
            visible.Select(static job => job.Id).ToArray());
    }

    public async Task<bool> CancelEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null)
            return false;
        await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> RetryEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null)
            return false;
        await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry", "Retry queued as a new job.");
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

    public void ConfigureEnhancementWorkspaceExternalOpenForSmoke(Func<ProcessStartInfo, bool> launcher)
        => _externalFileLauncher = launcher ?? throw new ArgumentNullException(nameof(launcher));

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
        string status,
        int progress,
        string? outputPath,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long? sourceSize,
        double? sourceMtimeMs)
    {
        Id = id;
        SourceId = sourceId;
        SourcePath = sourcePath;
        PresetId = presetId;
        AdapterId = adapterId;
        Status = status;
        Progress = progress;
        OutputPath = outputPath;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        SourceSize = sourceSize;
        SourceMtimeMs = sourceMtimeMs;
    }

    public string Id { get; }
    public string SourceId { get; }
    public string SourcePath { get; }
    public string PresetId { get; }
    public string AdapterId { get; }
    public string Status { get; }
    public int Progress { get; }
    public string? OutputPath { get; }
    public string? ErrorMessage { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public long? SourceSize { get; }
    public double? SourceMtimeMs { get; }
    public bool IsActive => Status is "queued" or "running";
    public bool CanCancel => !_isBusy && IsActive;
    public bool CanRetry => !_isBusy && Status is "failed" or "canceled";
    public bool CanUseOutput => !_isBusy && Status == "succeeded" && !string.IsNullOrWhiteSpace(OutputPath);
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath) ? "Unknown source" : Path.GetFileName(SourcePath);
    public string PresetSummary => $"{PresetId}  ·  {AdapterId}";
    public string StatusLabel => Status switch
    {
        "queued" => $"Queued  {Progress}%",
        "running" => $"Running  {Progress}%",
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
    public string AccessibleName => $"{SourceName}, {StatusLabel}, {PresetId}";

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
    string[] VisibleIds);
