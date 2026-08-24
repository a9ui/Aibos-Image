using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int ExternalVideoHeaderProbeBytes = 64 * 1024;

    private static readonly HashSet<string> SupportedExternalVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov",
        };

    private ExternalVideoDropSession? _externalVideoDropSession;
    private long _externalVideoDropGeneration;
    private string? _externalModalVideoPath;

    private sealed class ExternalVideoValidation : IDisposable
    {
        private FileStream? _pinnedStream;

        private ExternalVideoValidation(
            bool accepted,
            string canonicalPath,
            ExternalFileDropSourceVersion version,
            FileStream? pinnedStream,
            string reason)
        {
            Accepted = accepted;
            CanonicalPath = canonicalPath;
            Version = version;
            _pinnedStream = pinnedStream;
            Reason = reason;
        }

        internal bool Accepted { get; }
        internal string CanonicalPath { get; }
        internal ExternalFileDropSourceVersion Version { get; }
        internal string Reason { get; }

        internal static ExternalVideoValidation Reject(string reason)
            => new(false, "", default, null, reason);

        internal static ExternalVideoValidation Accept(
            string canonicalPath,
            ExternalFileDropSourceVersion version,
            FileStream pinnedStream)
            => new(true, canonicalPath, version, pinnedStream, "");

        internal FileStream TakePinnedStream()
            => Interlocked.Exchange(ref _pinnedStream, null)
                ?? throw new InvalidOperationException(
                    "The external video pin was already transferred.");

        public void Dispose()
            => Interlocked.Exchange(ref _pinnedStream, null)?.Dispose();
    }

    private sealed record ExternalVideoDropSession(
        long Generation,
        string CanonicalPath,
        ExternalFileDropSourceVersion Version,
        Tile Tile,
        FileStream PinnedStream) : IDisposable
    {
        public void Dispose() => PinnedStream.Dispose();
    }

    private bool ExternalVideoDropSessionActive
        => _externalVideoDropSession is not null;

    private bool TryGetExternalVideoDropSessionTile(out Tile tile)
    {
        tile = null!;
        ExternalVideoDropSession? session = _externalVideoDropSession;
        if (session is null
            || session.Generation != _externalVideoDropGeneration
            || session.PinnedStream.SafeFileHandle.IsClosed
            || session.PinnedStream.SafeFileHandle.IsInvalid
            || !TryReadExternalFileDropSourceVersion(
                session.PinnedStream.SafeFileHandle,
                out ExternalFileDropSourceVersion current)
            || !session.Version.SameFileVersion(current))
        {
            return false;
        }

        tile = session.Tile;
        return true;
    }

    private bool TryGetExternalVideoDropSessionTile(
        string? path,
        out Tile tile)
    {
        if (!TryGetExternalVideoDropSessionTile(out tile)
            || string.IsNullOrWhiteSpace(path)
            || !string.Equals(
                tile.Path,
                path,
                StringComparison.OrdinalIgnoreCase))
        {
            tile = null!;
            return false;
        }
        return true;
    }

    private bool IsExternalVideoDropSessionTile(Tile? tile)
        => tile is not null
            && TryGetExternalVideoDropSessionTile(out Tile authoritative)
            && ReferenceEquals(tile, authoritative);

    // Video Tools v2 can capture this immutable path/signature/generation
    // tuple, compute SHA-256 from the still-pinned stream only after an
    // explicit Start, then revalidate the tuple immediately before publish.
    private bool TryCaptureExternalVideoSourceSeam(
        out ExternalVideoSourceSeamSmokeSnapshot capture)
    {
        capture = null!;
        ExternalVideoDropSession? session = _externalVideoDropSession;
        if (session is null
            || !TryGetExternalVideoDropSessionTile(out _))
        {
            return false;
        }

        capture = new ExternalVideoSourceSeamSmokeSnapshot(
            session.Generation,
            session.CanonicalPath,
            session.Version.VolumeSerialNumber,
            session.Version.FileIndex,
            session.Version.Length,
            session.Version.LastWriteUtcTicks,
            session.Version.CreationUtcTicks);
        return true;
    }

    private bool TryRevalidateExternalVideoSourceSeam(
        ExternalVideoSourceSeamSmokeSnapshot capture)
    {
        ExternalVideoDropSession? session = _externalVideoDropSession;
        return capture is not null
            && session is not null
            && session.Generation == capture.Generation
            && string.Equals(
                session.CanonicalPath,
                capture.CanonicalPath,
                StringComparison.OrdinalIgnoreCase)
            && session.Version.VolumeSerialNumber == capture.VolumeSerialNumber
            && session.Version.FileIndex == capture.FileIndex
            && session.Version.Length == capture.Length
            && session.Version.LastWriteUtcTicks == capture.LastWriteUtcTicks
            && session.Version.CreationUtcTicks == capture.CreationUtcTicks
            && TryGetExternalVideoDropSessionTile(out _);
    }

    private async Task<ExternalVideoSourceIdentityCapture?>
        CaptureExternalVideoSourceIdentityForEditV2Async(
            ExternalVideoSourceSeamSmokeSnapshot capture,
            CancellationToken token)
    {
        ExternalVideoDropSession? session = _externalVideoDropSession;
        if (session is null
            || !ReferenceEquals(session, _externalVideoDropSession)
            || !string.Equals(
                Path.GetExtension(session.CanonicalPath),
                ".mp4",
                StringComparison.OrdinalIgnoreCase)
            || !TryRevalidateExternalVideoSourceSeam(capture)
            || !TryReadExternalFileDropSourceVersion(
                session.PinnedStream.SafeFileHandle,
                out ExternalFileDropSourceVersion before)
            || !session.Version.SameFileVersion(before)
            || !WindowsPathIdentity.TryGetFinalPath(
                session.PinnedStream.SafeFileHandle,
                out string openedCanonical)
            || !string.Equals(
                openedCanonical,
                capture.CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        try
        {
            while (offset < before.Length)
            {
                token.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min(
                    buffer.Length,
                    before.Length - offset));
                int read = await RandomAccess.ReadAsync(
                    session.PinnedStream.SafeFileHandle,
                    buffer.AsMemory(0, requested),
                    offset,
                    token);
                if (read <= 0)
                    return null;
                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }

            token.ThrowIfCancellationRequested();
            if (offset != before.Length
                || !ReferenceEquals(session, _externalVideoDropSession)
                || !TryReadExternalFileDropSourceVersion(
                    session.PinnedStream.SafeFileHandle,
                    out ExternalFileDropSourceVersion after)
                || !before.SameFileVersion(after)
                || !TryRevalidateExternalVideoSourceSeam(capture)
                || !WindowsPathIdentity.TryGetFinalPath(
                    session.PinnedStream.SafeFileHandle,
                    out string finalCanonical)
                || !string.Equals(
                    finalCanonical,
                    openedCanonical,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            long mtimeMs = new DateTimeOffset(
                    new DateTime(before.LastWriteUtcTicks, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();
            return new ExternalVideoSourceIdentityCapture(
                capture,
                openedCanonical,
                before.Length,
                mtimeMs,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<ExternalVideoDropSmokeSnapshot> ApplyDroppedVideoAsync(
        IEnumerable<string> paths)
    {
        string[] materialized = paths?.ToArray() ?? [];
        (long generation, CancellationTokenSource cts) = BeginExternalFileDropIntake();
        try
        {
            using ExternalVideoValidation validated = await Task.Run(
                () => ValidateExternalVideoDrop(materialized, cts.Token),
                cts.Token);
            if (!TryClaimExternalFileDropIntake(generation, cts))
            {
                return SnapshotExternalVideoDrop(
                    accepted: false,
                    "the video drop was superseded",
                    "");
            }

            if (!validated.Accepted)
            {
                string status = UiLanguageResources.Format(
                    "UiExternalVideoDropRejectedFormat",
                    validated.Reason);
                SetStatusToast(status);
                return SnapshotExternalVideoDrop(
                    accepted: false,
                    validated.Reason,
                    status);
            }

            CloseExternalFileDropSessionForReplacement();
            if (Modal.Visibility == Visibility.Visible)
                CloseModal(restoreFocus: false);
            CloseExternalVideoDropSession();

            FileStream pinnedStream = validated.TakePinnedStream();
            try
            {
                BeginExternalVideoDropSession(
                    validated.CanonicalPath,
                    validated.Version,
                    pinnedStream);
            }
            catch
            {
                pinnedStream.Dispose();
                throw;
            }

            string successStatus = UiLanguageResources.Text(
                "UiExternalVideoDropStatus");
            SetTransientStatusToast(successStatus);
            return SnapshotExternalVideoDrop(
                accepted: true,
                "",
                successStatus);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return SnapshotExternalVideoDrop(
                accepted: false,
                "the video drop was superseded",
                "");
        }
        finally
        {
            RetireExternalFileDropIntake(generation, cts);
        }
    }

    private ExternalVideoValidation ValidateExternalVideoDrop(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count != 1)
        {
            return ExternalVideoValidation.Reject(
                "open exactly one supported video at a time");
        }

        string raw = paths[0];
        if (string.IsNullOrWhiteSpace(raw)
            || !Path.IsPathFullyQualified(raw))
        {
            return ExternalVideoValidation.Reject(
                "the video path must be absolute");
        }

        string canonical;
        try
        {
            string lexical = Path.GetFullPath(raw);
            if (Directory.Exists(lexical))
            {
                return ExternalVideoValidation.Reject(
                    "a folder cannot be opened as a video");
            }
            canonical = Path.GetFullPath(_resolveFinalPath(lexical));
        }
        catch
        {
            return ExternalVideoValidation.Reject(
                "the video path could not be canonicalized");
        }

        if (!SupportedExternalVideoExtensions.Contains(
                Path.GetExtension(canonical)))
        {
            return ExternalVideoValidation.Reject(
                "the video format is unsupported");
        }

        FileStream? pinnedStream = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pinnedStream = new FileStream(
                canonical,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.RandomAccess);
            if (!WindowsPathIdentity.TryGetFinalPath(
                    pinnedStream.SafeFileHandle,
                    out string openedCanonical)
                || !string.Equals(
                    openedCanonical,
                    canonical,
                    StringComparison.OrdinalIgnoreCase)
                || !TryReadExternalFileDropSourceVersion(
                    pinnedStream.SafeFileHandle,
                    out ExternalFileDropSourceVersion before))
            {
                return ExternalVideoValidation.Reject(
                    "the video identity could not be pinned");
            }

            if (before.Length is <= 0 or > MaxExternalFileDropBytes)
            {
                return ExternalVideoValidation.Reject(
                    "the video must be between 1 byte and 512 MiB");
            }
            if (!HasBoundedIsoBaseMediaSignature(
                    pinnedStream,
                    before.Length,
                    cancellationToken))
            {
                return ExternalVideoValidation.Reject(
                    "the file is not a supported ISO base media video");
            }

            if (!TryReadExternalFileDropSourceVersion(
                    pinnedStream.SafeFileHandle,
                    out ExternalFileDropSourceVersion after)
                || !before.SameFileVersion(after)
                || pinnedStream.Length != before.Length
                || !WindowsPathIdentity.TryGetFinalPath(
                    pinnedStream.SafeFileHandle,
                    out string finalCanonical)
                || !string.Equals(
                    finalCanonical,
                    canonical,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ExternalVideoValidation.Reject(
                    "the video changed while it was being validated");
            }

            ExternalVideoValidation accepted = ExternalVideoValidation.Accept(
                canonical,
                before,
                pinnedStream);
            pinnedStream = null;
            return accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ExternalVideoValidation.Reject(
                "the video could not be opened safely");
        }
        finally
        {
            pinnedStream?.Dispose();
        }
    }

    private static bool HasBoundedIsoBaseMediaSignature(
        FileStream stream,
        long length,
        CancellationToken cancellationToken)
    {
        try
        {
            int probeLength = checked((int)Math.Min(
                length,
                ExternalVideoHeaderProbeBytes));
            if (probeLength < 12)
                return false;

            byte[] buffer = GC.AllocateUninitializedArray<byte>(probeLength);
            stream.Position = 0;
            int read = 0;
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int next = stream.Read(buffer, read, buffer.Length - read);
                if (next == 0)
                    break;
                read += next;
            }
            for (int typeOffset = 4; typeOffset + 4 <= read; typeOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer[typeOffset] != (byte)'f'
                    || buffer[typeOffset + 1] != (byte)'t'
                    || buffer[typeOffset + 2] != (byte)'y'
                    || buffer[typeOffset + 3] != (byte)'p')
                {
                    continue;
                }

                int boxOffset = typeOffset - 4;
                uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(
                    buffer.AsSpan(boxOffset, 4));
                return boxSize >= 8
                    && (ulong)boxOffset + boxSize <= (ulong)length;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { stream.Position = 0; } catch { }
        }
    }

    private void BeginExternalVideoDropSession(
        string canonicalPath,
        ExternalFileDropSourceVersion version,
        FileStream pinnedStream)
    {
        long generation = ++_externalVideoDropGeneration;
        ExternalFileDropSourceVersion sessionVersion =
            version.WithSession(generation);
        Tile tile = CreateExternalVideoDropTile(
            canonicalPath,
            sessionVersion);
        _externalVideoDropSession = new ExternalVideoDropSession(
            generation,
            canonicalPath,
            sessionVersion,
            tile,
            pinnedStream);

        OpenModal(tile);
        if (Modal.Visibility != Visibility.Visible
            || !_modalShowingVideo
            || !TryGetExternalVideoDropSessionTile(out _))
        {
            CloseExternalVideoDropSession();
            throw new InvalidOperationException(
                "The temporary video could not be opened in the modal viewer.");
        }
    }

    private Tile CreateExternalVideoDropTile(
        string canonicalPath,
        ExternalFileDropSourceVersion version)
    {
        var info = new FileInfo(canonicalPath);
        int paletteIndex = canonicalPath.GetHashCode(
            StringComparison.OrdinalIgnoreCase) & int.MaxValue;
        var tile = new Tile
        {
            ArtBase = MakeBaseBrush(paletteIndex),
            ArtGlow = MakeGlowBrush(paletteIndex),
            FileName = Path.GetFileName(canonicalPath),
            Fav = 0,
            Unseen = false,
            ShowUnseenDot = false,
            Group = FormatGroup(info.CreationTime),
            CardWidth = SizeSlider.Value,
            ModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = info.CreationTimeUtc,
            SourceLength = version.Length,
            SourceLastWriteUtcTicks = version.LastWriteUtcTicks,
            SourceCreationUtcTicks = version.CreationUtcTicks,
            Path = canonicalPath,
            IsRealFile = true,
            FolderBucketKey = "",
            FolderBucketLabel = "",
            ImagePixelWidth = 0,
            ImagePixelHeight = 0,
            SizeText = FormatBytes(version.Length),
            ModifiedText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        };
        ApplyCardLayout(tile);
        return tile;
    }

    private void CloseExternalVideoDropSession()
    {
        ExternalVideoDropSession? session = Interlocked.Exchange(
            ref _externalVideoDropSession,
            null);
        if (session is null)
            return;

        _externalVideoDropGeneration++;
        _externalModalVideoPath = null;
        session.Dispose();
        SetExternalVideoViewOnlyPresentation(enabled: false);
        RefreshModalVideoVersionChoices();
    }

    private void OpenExternalVideoModal(Tile tile)
    {
        if (!IsExternalVideoDropSessionTile(tile)
            || !TryGetExternalVideoDropSessionTile(
                tile.Path,
                out Tile authoritative))
        {
            CloseExternalVideoDropSession();
            SetStatusToast("動画の一時表示セッションが終了しました。");
            return;
        }

        bool opening = Modal.Visibility != Visibility.Visible;
        if (opening)
            _modalFocusBeforeOverlay = Keyboard.FocusedElement;
        StopGalleryAutoScroll();
        StopAndHideModalVideo(clearSource: true);
        _modalSourceTilePath = authoritative.Path;
        _modalNavigationSnapshot = [];
        ResetModalTransform(authoritative.Path, preserveZoom: false);
        _modalCts?.Cancel();
        _modalCts?.Dispose();
        _modalCts = null;
        _modalDecodeCompletion?.TrySetResult(false);
        CancelModalMetadataRefresh(clearCurrent: true);
        ClearModalEnhancementVersions();
        _modalVideoVersions.Clear();
        _modalVideoVersionIndex = 0;
        _modalShowingEnhanced = false;
        _modalDisplayPath = authoritative.Path;

        UpdateModalPositionText(authoritative);
        UpdateModalDisplayedImageInfo(authoritative, 0, 0);
        ModalFileSizeText.Text = FormatFileSizeMb(
            _externalVideoDropSession?.Version.Length);
        ModalBitmap.Source = null;
        ModalBitmap.Visibility = Visibility.Collapsed;
        ModalArtBase.Visibility = Visibility.Collapsed;
        ModalArtGlow.Visibility = Visibility.Collapsed;
        Modal.Visibility = Visibility.Visible;
        SetModalMetadataSidebarVisible(false);
        _ = SetModalFilmstripOpen(open: false, persist: false);
        SyncModalFilmstripSelection(authoritative);
        UpdateModalEnhancedControls(canShowEnhanced: false);
        UpdateModalEnhancementActionControls();
        UpdateVideoGenerationActionControls();
        SetExternalVideoViewOnlyPresentation(enabled: true);

        if (!ShowExternalModalVideo(authoritative.Path, autoplay: true))
        {
            CloseModal(restoreFocus: false);
            SetStatusToast("動画を再生できません。対応形式を確認してください。");
            return;
        }

        _modalHasPointerPosition = false;
        _modalPressedEdgeTarget = ModalEdgeTarget.None;
        SetModalChromeVisible(true, showFeedback: false);
        SetExternalVideoViewOnlyPresentation(enabled: true);
        ScheduleModalFitUpdate();
        if (opening)
            Dispatcher.BeginInvoke(Modal.Focus, DispatcherPriority.Input);
    }

    private bool ShowExternalModalVideo(string path, bool autoplay)
    {
        if (Modal.Visibility != Visibility.Visible
            || !TryGetExternalVideoDropSessionTile(path, out Tile tile))
        {
            return false;
        }

        string canonicalPath = tile.Path;
        _externalModalVideoPath = canonicalPath;
        _modalShowingVideo = true;
        _modalShowingEnhanced = false;
        _modalVideoPlaying = autoplay;
        _modalVideoAutoplayPending = autoplay;
        _modalVideoPlaybackGeneration++;
        _modalVideoMediaFailureForSmoke = null;
        _modalVideoMediaOpenCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_modalVideoTransportStubForSmoke)
            ReplaceModalVideoTransport();
        AutomationProperties.SetName(ModalVideo, "Dropped video playback");
        ModalVideo.Visibility = Visibility.Visible;
        ModalBitmap.Visibility = Visibility.Collapsed;
        ModalArtBase.Visibility = Visibility.Collapsed;
        ModalArtGlow.Visibility = Visibility.Collapsed;
        ResetModalVideoTimeline(durationSeconds: 0, show: true);
        EnsureModalVideoTimelineTimer();
        _modalVideoTimelineTimer?.Start();

        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Source = new Uri(canonicalPath, UriKind.Absolute);
                ModalVideo.Pause();
                ModalVideo.Position = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                _modalVideoMediaFailureForSmoke = ex.Message;
                _modalVideoMediaOpenCompletion.TrySetResult(false);
                return false;
            }
        }
        else
        {
            _modalVideoMediaOpenCompletion.TrySetResult(true);
        }

        RefreshModalVideoVersionChoices();
        ModalSourceLabel.Text = "Video";
        ModalFileSizeText.Text = FormatFileSizeMb(
            _externalVideoDropSession?.Version.Length);
        ModalTitle.Text = "動画を読み込み中";
        ModalTitle.ToolTip = tile.FileName;
        AutomationProperties.SetName(
            ModalTitle,
            $"{tile.FileName}, video dimensions pending");
        SetModalMetadataSidebarVisible(false);
        UpdateModalVideoPlaybackPresentation();
        return true;
    }

    private void SetExternalVideoViewOnlyPresentation(bool enabled)
    {
        if (ModalContextMenu is not null)
            ModalContextMenu.IsEnabled = !enabled;
        if (ModalEnhancementVersionComboBox is not null)
        {
            ModalEnhancementVersionComboBox.IsEnabled = !enabled;
            ModalEnhancementVersionComboBox.Visibility = enabled
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (!enabled)
        {
            ModalUpscaleSettingsButton.IsEnabled = true;
            ModalPhotorealSettingsButton.IsEnabled = true;
            ModalVideoSettingsButton.IsEnabled = true;
            return;
        }

        const string unavailable =
            "一時表示の動画は閲覧専用です。動画編集・動画高画質化の対応後に利用できます。";
        ModalEnhanceButton.IsEnabled = false;
        ModalUpscaleSettingsButton.IsEnabled = false;
        ModalPhotorealButton.IsEnabled = false;
        ModalPhotorealUpscaleButton.IsEnabled = false;
        ModalPhotorealSettingsButton.IsEnabled = false;
        ModalVideoGenerateButton.IsEnabled = false;
        ModalVideoSettingsButton.IsEnabled = false;
        ModalI2iEditButton.IsEnabled = false;
        ModalI2iEditSettingsButton.IsEnabled = false;
        ModalFavoriteDecreaseButton.IsEnabled = false;
        ModalFavoriteIncreaseButton.IsEnabled = false;
        ModalDeleteButton.IsEnabled = false;
        ModalEnhanceButton.ToolTip = unavailable;
        ModalPhotorealButton.ToolTip = unavailable;
        ModalVideoGenerateButton.ToolTip = unavailable;
        ModalI2iEditButton.ToolTip = unavailable;
        SyncModalVideoToolsEntryPresentation();
        UpdateModalDisplayedDeletePresentation();
    }

    private ExternalVideoDropSmokeSnapshot SnapshotExternalVideoDrop(
        bool accepted,
        string reason,
        string status)
        => new(
            accepted,
            _externalVideoDropSession?.CanonicalPath,
            Modal.Visibility == Visibility.Visible,
            _modalShowingVideo,
            TryGetExternalVideoDropSessionTile(out _),
            ExternalVideoAiActionsDisabledForSmoke,
            ExternalVideoMutationActionsDisabledForSmoke,
            reason,
            status);

    public Task<ExternalVideoDropSmokeSnapshot> DropExternalVideoForSmokeAsync(
        IEnumerable<string> paths)
        => ApplyDroppedVideoAsync(paths);

    public bool ExternalVideoDropSessionActiveForSmoke
        => TryGetExternalVideoDropSessionTile(out _);

    public bool ExternalVideoAiActionsDisabledForSmoke
        => ModalEnhanceButton?.IsEnabled == false
            && ModalUpscaleSettingsButton?.IsEnabled == false
            && ModalPhotorealButton?.IsEnabled == false
            && ModalPhotorealSettingsButton?.IsEnabled == false
            && ModalVideoGenerateButton?.IsEnabled == false
            && ModalVideoSettingsButton?.IsEnabled == false
            && ModalI2iEditButton?.IsEnabled == false
            && ModalI2iEditSettingsButton?.IsEnabled == false;

    public bool ExternalVideoMutationActionsDisabledForSmoke
        => ModalFavoriteDecreaseButton?.IsEnabled == false
            && ModalFavoriteIncreaseButton?.IsEnabled == false
            && ModalDeleteButton?.IsEnabled == false
            && ModalContextMenu?.IsEnabled == false;

    public ExternalVideoSourceSeamSmokeSnapshot?
        CaptureExternalVideoSourceSeamForSmoke()
        => TryCaptureExternalVideoSourceSeam(out var capture)
            ? capture
            : null;

    public bool RevalidateExternalVideoSourceSeamForSmoke(
        ExternalVideoSourceSeamSmokeSnapshot capture)
        => TryRevalidateExternalVideoSourceSeam(capture);
}

public sealed record ExternalVideoDropSmokeSnapshot(
    bool Accepted,
    string? CanonicalPath,
    bool ModalVisible,
    bool ShowingVideo,
    bool SourcePinned,
    bool AiActionsDisabled,
    bool MutationActionsDisabled,
    string Reason,
    string Status);

public sealed record ExternalVideoSourceSeamSmokeSnapshot(
    long Generation,
    string CanonicalPath,
    uint VolumeSerialNumber,
    ulong FileIndex,
    long Length,
    long LastWriteUtcTicks,
    long CreationUtcTicks);

internal sealed record ExternalVideoSourceIdentityCapture(
    ExternalVideoSourceSeamSmokeSnapshot Seam,
    string CanonicalPath,
    long Size,
    long MtimeMs,
    string Sha256);
