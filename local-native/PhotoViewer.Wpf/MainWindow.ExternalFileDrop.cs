using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxExternalFileDropImages = 100;
    private const long MaxExternalFileDropBytes = 512L * 1024 * 1024;
    private const long MaxExternalFileDropSourcePixels = 100_000_000;
    private const int ExternalFileDropValidationDecodeWidth = 512;
    private const int ExternalFileDropValidationCopyBufferBytes = 256 * 1024;

    private readonly List<Tile> _externalFileDropCohort = [];
    private readonly List<Tile> _externalFileDropAddedProjectionTiles = [];
    private readonly List<Tile> _externalFileDropTransientTiles = [];
    private readonly List<ExternalFileDropProjectionReplacement>
        _externalFileDropProjectionReplacements = [];
    private readonly List<string> _externalFileDropPreviousSelectionPaths = [];
    private readonly Dictionary<Tile, ExternalFileDropSourceVersion>
        _externalFileDropSourceVersions = new(ReferenceEqualityComparer.Instance);
    private string? _externalFileDropPreviousPrimaryPath;
    private CancellationTokenSource? _externalFileDropThumbnailCts;
    private CancellationTokenSource? _externalFileDropIntakeCts;
    private ExternalFileDropValidationPause? _externalFileDropValidationPauseForSmoke;
    private long _externalFileDropGeneration;
    private long _externalFileDropSessionGeneration;
    private bool _externalFileDropSessionCaptured;
    private bool _externalFileDropPreviousSuppressStateSave;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeByHandleFileInformation information);

    private enum ViewerDropPayloadKind
    {
        Rejected,
        Folders,
        Images,
        Mixed,
    }

    private sealed record ViewerDropPayload(
        ViewerDropPayloadKind Kind,
        string[] Paths,
        DroppedFolderSet? Folders,
        string Reason);

    private sealed record ValidatedExternalImage(
        string CanonicalPath,
        ExternalFileDropSourceVersion Version,
        int Width,
        int Height,
        FileStream PinnedStream) : IDisposable
    {
        public long Length => Version.Length;
        public long LastWriteUtcTicks => Version.LastWriteUtcTicks;
        public long CreationUtcTicks => Version.CreationUtcTicks;

        public void Dispose() => PinnedStream.Dispose();
    }

    private readonly record struct ExternalFileDropSourceVersion(
        long SessionGeneration,
        uint VolumeSerialNumber,
        ulong FileIndex,
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks)
    {
        internal ExternalFileDropSourceVersion WithSession(long sessionGeneration)
            => this with { SessionGeneration = sessionGeneration };

        internal bool SameFileVersion(ExternalFileDropSourceVersion other)
            => VolumeSerialNumber == other.VolumeSerialNumber
                && FileIndex == other.FileIndex
                && Length == other.Length
                && LastWriteUtcTicks == other.LastWriteUtcTicks
                && CreationUtcTicks == other.CreationUtcTicks;
    }

    private sealed record ExternalFileDropValidationPause(
        ManualResetEventSlim Entered,
        ManualResetEventSlim Release);

    private sealed record ExternalFileDropProjectionReplacement(
        Tile Temporary,
        Tile Previous);

    private readonly record struct ExternalFileDropPrePublishGuard(
        long SessionGeneration,
        string CanonicalPath,
        ExternalFileDropSourceVersion SourceVersion);

    private sealed class ExternalImageValidation : IDisposable
    {
        private int _disposed;

        internal ExternalImageValidation(
            bool accepted,
            IReadOnlyList<ValidatedExternalImage> images,
            string reason)
        {
            Accepted = accepted;
            Images = images;
            Reason = reason;
        }

        internal bool Accepted { get; }
        internal IReadOnlyList<ValidatedExternalImage> Images { get; }
        internal string Reason { get; }

        public static ExternalImageValidation Reject(string reason)
            => new(false, [], reason);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            foreach (ValidatedExternalImage image in Images)
                image.Dispose();
        }
    }

    private bool ExternalFileDropSessionActive
        => _externalFileDropSessionCaptured
            && _externalFileDropCohort.Count > 0;

    private bool IsExternalFileDropSessionTile(Tile? tile)
        => TryResolveExternalFileDropSessionTile(
            tile,
            out _,
            out _);

    private bool TryResolveExternalFileDropSessionTile(
        Tile? presentedTile,
        out Tile authoritativeTile,
        out ExternalFileDropSourceVersion sourceVersion)
    {
        authoritativeTile = null!;
        sourceVersion = default;
        if (presentedTile is null
            || !presentedTile.IsRealFile
            || !TryGetExternalFileDropSessionTile(
                presentedTile.Path,
                out authoritativeTile)
            || !_externalFileDropSourceVersions.TryGetValue(
                authoritativeTile,
                out sourceVersion)
            || sourceVersion.SessionGeneration
                != _externalFileDropSessionGeneration)
        {
            return false;
        }

        // A catalog projection is rebuilt from _allTiles. If the dropped path
        // also belongs to the catalog, applying an Original/photoreal/video
        // Favorite filter can therefore replace the temporary Tile instance
        // while the modal session is still active. The session is authoritative
        // by its canonical path and generation, not by that presentation object.
        return ReferenceEquals(presentedTile, authoritativeTile)
            || _allTiles.Contains(presentedTile)
            || _tiles.Contains(presentedTile);
    }

    private bool TryGetExternalFileDropSessionTile(
        string? path,
        out Tile tile)
    {
        tile = null!;
        if (!ExternalFileDropSessionActive || string.IsNullOrWhiteSpace(path))
            return false;

        tile = _externalFileDropCohort.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                path,
                StringComparison.OrdinalIgnoreCase))!;
        return tile is not null;
    }

    private IReadOnlyList<Tile> ModalNavigationTiles()
        => ExternalFileDropSessionActive
            ? _externalFileDropCohort
            : _modalNavigationSnapshot.Length > 0
                ? _modalNavigationSnapshot
                : _tiles;

    private bool TryResolveModalNavigationTile(Tile candidate, out Tile tile)
    {
        if (TryGetExternalFileDropSessionTile(candidate.Path, out tile))
            return true;

        string normalizedPath = NormalizeFavoritePath(candidate.Path);
        if (_catalogTilesByFavoritePath.TryGetValue(
                normalizedPath,
                out List<Tile>? indexedTiles)
            && indexedTiles.Count > 0)
        {
            tile = indexedTiles[0];
            return true;
        }

        tile = _tiles.FirstOrDefault(item => string.Equals(
            item.Path,
            candidate.Path,
            StringComparison.OrdinalIgnoreCase))!;
        return tile is not null;
    }

    private static int IndexOfTile(
        IReadOnlyList<Tile> tiles,
        Tile tile)
    {
        for (int index = 0; index < tiles.Count; index++)
        {
            if (ReferenceEquals(tiles[index], tile)
                || string.Equals(
                    tiles[index].Path,
                    tile.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private IEnumerable<Tile> EnumerateLiveTiles()
    {
        var emitted = new HashSet<Tile>(ReferenceEqualityComparer.Instance);
        foreach (Tile tile in _allTiles)
        {
            if (emitted.Add(tile))
                yield return tile;
        }

        // Jobs and external FileDrop may temporarily publish a real source in
        // the current projection without making it part of the catalog.
        foreach (Tile tile in _tiles)
        {
            if (emitted.Add(tile))
                yield return tile;
        }
    }

    private void RebuildCatalogTileFavoritePathIndex()
    {
        _catalogTilesByFavoritePath.Clear();
        foreach (Tile tile in _allTiles)
            IndexCatalogTileForFavoritePath(tile);
    }

    private void IndexCatalogTileForFavoritePath(Tile tile)
    {
        string path = NormalizeFavoritePath(tile.Path);
        if (!_catalogTilesByFavoritePath.TryGetValue(path, out List<Tile>? tiles))
        {
            tiles = [];
            _catalogTilesByFavoritePath[path] = tiles;
        }
        if (!tiles.Contains(tile, ReferenceEqualityComparer.Instance))
            tiles.Add(tile);
    }

    private void UnindexCatalogTileForFavoritePath(Tile tile)
    {
        string path = NormalizeFavoritePath(tile.Path);
        if (!_catalogTilesByFavoritePath.TryGetValue(path, out List<Tile>? tiles))
            return;
        tiles.RemoveAll(candidate => ReferenceEquals(candidate, tile));
        if (tiles.Count == 0)
            _catalogTilesByFavoritePath.Remove(path);
    }

    private void CollectLiveTilesForFavoritePath(
        string path,
        Tile? preferredTile,
        ISet<Tile> destination)
    {
        string normalizedPath = NormalizeFavoritePath(path);
        if (_catalogTilesByFavoritePath.TryGetValue(
                normalizedPath,
                out List<Tile>? catalogTiles))
        {
            foreach (Tile tile in catalogTiles)
                destination.Add(tile);
        }

        if (preferredTile is not null
            && string.Equals(
                NormalizeFavoritePath(preferredTile.Path),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            destination.Add(preferredTile);
        }

        if (TryGetExternalFileDropSessionTile(normalizedPath, out Tile externalTile))
            destination.Add(externalTile);
    }

    private bool IsIndexedCatalogTile(Tile tile)
    {
        string path = NormalizeFavoritePath(tile.Path);
        return _catalogTilesByFavoritePath.TryGetValue(path, out List<Tile>? tiles)
            && tiles.Any(candidate => ReferenceEquals(candidate, tile));
    }

    private ViewerDropPayload ReadViewerDropPayload(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            return new ViewerDropPayload(
                ViewerDropPayloadKind.Rejected,
                [],
                null,
                "drop existing folders or supported image files from Explorer");

        try
        {
            return data.GetData(DataFormats.FileDrop) is string[] paths
                ? ReadViewerDropPayload(paths)
                : new ViewerDropPayload(
                    ViewerDropPayloadKind.Rejected,
                    [],
                    null,
                    "the Explorer payload was unavailable");
        }
        catch
        {
            return new ViewerDropPayload(
                ViewerDropPayloadKind.Rejected,
                [],
                null,
                "the Explorer payload could not be read");
        }
    }

    private ViewerDropPayload ReadViewerDropPayload(IEnumerable<string> paths)
    {
        string[] materialized = paths?.ToArray() ?? [];
        if (materialized.Length == 0)
        {
            return new ViewerDropPayload(
                ViewerDropPayloadKind.Rejected,
                [],
                null,
                "the drop did not contain a path");
        }

        bool hasExistingFolder = materialized.Any(Directory.Exists);
        bool hasExistingFile = materialized.Any(File.Exists);
        if (hasExistingFolder && hasExistingFile)
        {
            return new ViewerDropPayload(
                ViewerDropPayloadKind.Mixed,
                materialized,
                null,
                "folders and files cannot be opened in the same drop");
        }

        if (hasExistingFolder)
        {
            DroppedFolderSet folders = ReadDroppedFolders(materialized);
            return new ViewerDropPayload(
                ViewerDropPayloadKind.Folders,
                materialized,
                folders,
                folders.RejectionReason);
        }

        bool imageIntent = hasExistingFile
            || materialized.Any(path =>
                !string.IsNullOrWhiteSpace(path)
                && SupportedImageExtensions.Contains(Path.GetExtension(path)));
        return imageIntent
            ? new ViewerDropPayload(
                ViewerDropPayloadKind.Images,
                materialized,
                null,
                "")
            : new ViewerDropPayload(
                ViewerDropPayloadKind.Rejected,
                materialized,
                null,
                "drop existing folders or supported image files from Explorer");
    }

    private bool ViewerDropPayloadHasAcceptableAffordance(
        ViewerDropPayload payload)
    {
        if (payload.Kind == ViewerDropPayloadKind.Folders)
            return payload.Folders?.Folders.Count > 0;
        if (payload.Kind != ViewerDropPayloadKind.Images
            || payload.Paths.Length is < 1 or > MaxExternalFileDropImages)
        {
            return false;
        }

        long totalBytes = 0;
        foreach (string raw in payload.Paths)
        {
            if (string.IsNullOrWhiteSpace(raw)
                || !Path.IsPathFullyQualified(raw)
                || !File.Exists(raw)
                || !SupportedImageExtensions.Contains(Path.GetExtension(raw)))
            {
                return false;
            }

            try
            {
                long length = new FileInfo(raw).Length;
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaxExternalFileDropBytes)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private (long Generation, CancellationTokenSource Cts) BeginExternalFileDropIntake()
    {
        CancellationTokenSource? superseded = _externalFileDropIntakeCts;
        var cts = new CancellationTokenSource();
        long generation = ++_externalFileDropGeneration;
        _externalFileDropIntakeCts = cts;
        superseded?.Cancel();
        return (generation, cts);
    }

    private bool TryClaimExternalFileDropIntake(
        long generation,
        CancellationTokenSource cts)
    {
        if (generation != _externalFileDropGeneration
            || !ReferenceEquals(_externalFileDropIntakeCts, cts)
            || cts.IsCancellationRequested)
        {
            return false;
        }

        _externalFileDropIntakeCts = null;
        return true;
    }

    private void RetireExternalFileDropIntake(
        long generation,
        CancellationTokenSource cts)
    {
        if (generation == _externalFileDropGeneration
            && ReferenceEquals(_externalFileDropIntakeCts, cts))
        {
            _externalFileDropIntakeCts = null;
        }
        cts.Dispose();
    }

    private void CancelPendingExternalFileDropIntake()
    {
        CancellationTokenSource? cts = _externalFileDropIntakeCts;
        if (cts is null)
            return;

        _externalFileDropIntakeCts = null;
        _externalFileDropGeneration++;
        cts.Cancel();
    }

    private async Task<ExternalImageDropSmokeSnapshot> ApplyDroppedImagesAsync(
        IEnumerable<string> paths)
    {
        string[] materialized = paths?.ToArray() ?? [];
        (long generation, CancellationTokenSource cts) = BeginExternalFileDropIntake();
        try
        {
            using ExternalImageValidation validated = await Task.Run(
                () => ValidateExternalImageDrop(materialized, cts.Token),
                cts.Token);
            if (!TryClaimExternalFileDropIntake(generation, cts))
            {
                return SnapshotExternalImageDrop(
                    accepted: false,
                    "the image drop was superseded",
                    "");
            }

            if (!validated.Accepted)
            {
                string status = UiLanguageResources.Format(
                    "UiExternalImageDropRejectedFormat",
                    validated.Reason);
                SetStatusToast(status);
                return SnapshotExternalImageDrop(
                    accepted: false,
                    validated.Reason,
                    status);
            }

            CloseExternalFileDropSessionForReplacement();
            BeginExternalFileDropSession(validated.Images);
            string successStatus = UiLanguageResources.Format(
                "UiExternalImageDropStatusFormat",
                validated.Images.Count);
            SetTransientStatusToast(successStatus);
            return SnapshotExternalImageDrop(
                accepted: true,
                "",
                successStatus);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return SnapshotExternalImageDrop(
                accepted: false,
                "the image drop was superseded",
                "");
        }
        finally
        {
            RetireExternalFileDropIntake(generation, cts);
        }
    }

    private ExternalImageValidation ValidateExternalImageDrop(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return ExternalImageValidation.Reject("no image file was supplied");
        if (paths.Count > MaxExternalFileDropImages)
        {
            return ExternalImageValidation.Reject(
                $"at most {MaxExternalFileDropImages} images can be opened at once");
        }

        var validated = new List<ValidatedExternalImage>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        try
        {
            ExternalFileDropValidationPause? pause = Interlocked.Exchange(
                ref _externalFileDropValidationPauseForSmoke,
                null);
            if (pause is not null)
            {
                pause.Entered.Set();
                pause.Release.Wait(cancellationToken);
            }

            foreach (string raw in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)
                    || !Path.IsPathFullyQualified(raw))
                {
                    return RejectExternalImageValidation(
                        validated,
                        "image paths must be absolute");
                }

                string canonical;
                try
                {
                    string lexical = Path.GetFullPath(raw);
                    if (Directory.Exists(lexical))
                    {
                        return RejectExternalImageValidation(
                            validated,
                            "folders and files cannot be opened in the same drop");
                    }
                    canonical = Path.GetFullPath(_resolveFinalPath(lexical));
                }
                catch
                {
                    return RejectExternalImageValidation(
                        validated,
                        "an image path could not be canonicalized");
                }

                if (!SupportedImageExtensions.Contains(Path.GetExtension(canonical)))
                {
                    return RejectExternalImageValidation(
                        validated,
                        "an image format is unsupported");
                }

                FileStream? pinnedStream = null;
                try
                {
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
                        return RejectExternalImageValidation(
                            validated,
                            "an image identity could not be pinned");
                    }

                    if (!seen.Add(canonical))
                        continue;

                    try
                    {
                        totalBytes = checked(totalBytes + before.Length);
                    }
                    catch (OverflowException)
                    {
                        return RejectExternalImageValidation(
                            validated,
                            "the image drop exceeds the 512 MiB total limit");
                    }
                    if (totalBytes > MaxExternalFileDropBytes)
                    {
                        return RejectExternalImageValidation(
                            validated,
                            "the image drop exceeds the 512 MiB total limit");
                    }

                    if (!TryFullyDecodePinnedExternalImage(
                            pinnedStream,
                            canonical,
                            cancellationToken,
                            out int width,
                            out int height))
                    {
                        return RejectExternalImageValidation(
                            validated,
                            "an image file could not be decoded within the safe viewing bounds");
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
                        return RejectExternalImageValidation(
                            validated,
                            "an image changed while it was being validated");
                    }

                    validated.Add(new ValidatedExternalImage(
                        canonical,
                        before,
                        width,
                        height,
                        pinnedStream));
                    pinnedStream = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return RejectExternalImageValidation(
                        validated,
                        "an image file could not be opened safely");
                }
                finally
                {
                    pinnedStream?.Dispose();
                }
            }

            if (validated.Count is < 1 or > MaxExternalFileDropImages)
            {
                return RejectExternalImageValidation(
                    validated,
                    "the final image count exceeds the safe intake bounds");
            }

            long finalTotalBytes = 0;
            foreach (ValidatedExternalImage image in validated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadExternalFileDropSourceVersion(
                        image.PinnedStream.SafeFileHandle,
                        out ExternalFileDropSourceVersion finalVersion)
                    || !image.Version.SameFileVersion(finalVersion)
                    || !WindowsPathIdentity.TryGetFinalPath(
                        image.PinnedStream.SafeFileHandle,
                        out string finalCanonical)
                    || !string.Equals(
                        finalCanonical,
                        image.CanonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return RejectExternalImageValidation(
                        validated,
                        "an image changed before the drop could be accepted");
                }

                try
                {
                    finalTotalBytes = checked(finalTotalBytes + finalVersion.Length);
                }
                catch (OverflowException)
                {
                    return RejectExternalImageValidation(
                        validated,
                        "the image drop exceeds the 512 MiB total limit");
                }
            }
            if (finalTotalBytes > MaxExternalFileDropBytes)
            {
                return RejectExternalImageValidation(
                    validated,
                    "the image drop exceeds the 512 MiB total limit");
            }

            return new ExternalImageValidation(true, validated, "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (ValidatedExternalImage image in validated)
                image.Dispose();
            throw;
        }
        catch
        {
            return RejectExternalImageValidation(
                validated,
                "the image drop could not be validated safely");
        }
    }

    private static ExternalImageValidation RejectExternalImageValidation(
        List<ValidatedExternalImage> validated,
        string reason)
    {
        foreach (ValidatedExternalImage image in validated)
            image.Dispose();
        validated.Clear();
        return ExternalImageValidation.Reject(reason);
    }

    private static bool TryReadExternalFileDropSourceVersion(
        SafeFileHandle handle,
        out ExternalFileDropSourceVersion version)
    {
        version = default;
        try
        {
            if (handle.IsInvalid
                || handle.IsClosed
                || !GetFileInformationByHandle(handle, out NativeByHandleFileInformation info))
            {
                return false;
            }

            ulong unsignedLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (unsignedLength > long.MaxValue)
                return false;
            long creationFileTime = checked((long)(
                ((ulong)info.CreationTime.HighDateTime << 32)
                | info.CreationTime.LowDateTime));
            long lastWriteFileTime = checked((long)(
                ((ulong)info.LastWriteTime.HighDateTime << 32)
                | info.LastWriteTime.LowDateTime));
            version = new ExternalFileDropSourceVersion(
                SessionGeneration: 0,
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                checked((long)unsignedLength),
                DateTime.FromFileTimeUtc(lastWriteFileTime).Ticks,
                DateTime.FromFileTimeUtc(creationFileTime).Ticks);
            return true;
        }
        catch
        {
            version = default;
            return false;
        }
    }

    private static bool TryFullyDecodePinnedExternalImage(
        FileStream stream,
        string path,
        CancellationToken cancellationToken,
        out int sourceWidth,
        out int sourceHeight)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            sourceWidth = frame.PixelWidth;
            sourceHeight = frame.PixelHeight;
            long sourcePixels = checked((long)sourceWidth * sourceHeight);
            if (sourceWidth <= 0
                || sourceHeight <= 0
                || sourceWidth > MaxDecodedLongEdge
                || sourceHeight > MaxDecodedLongEdge
                || sourcePixels > MaxExternalFileDropSourcePixels)
            {
                return false;
            }

            BitmapDecodePlan? plan = BuildBitmapDecodePlanForDimensions(
                Path.GetExtension(path),
                ExternalFileDropValidationDecodeWidth,
                sourceWidth,
                sourceHeight,
                explicitManagedOutput: true);
            if (plan is null)
                return false;

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (plan.Value.PixelWidth > 0)
                image.DecodePixelWidth = plan.Value.PixelWidth;
            if (plan.Value.PixelHeight > 0)
                image.DecodePixelHeight = plan.Value.PixelHeight;
            image.StreamSource = stream;
            image.EndInit();
            cancellationToken.ThrowIfCancellationRequested();
            image.Freeze();

            BitmapSource decoded = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(
                    image,
                    PixelFormats.Bgra32,
                    destinationPalette: null,
                    alphaThreshold: 0);
            long decodedPixels = checked((long)decoded.PixelWidth * decoded.PixelHeight);
            if (decoded.PixelWidth <= 0
                || decoded.PixelHeight <= 0
                || Math.Max(decoded.PixelWidth, decoded.PixelHeight) > MaxDecodedLongEdge
                || decodedPixels > MaxDecodedPixelCount)
            {
                return false;
            }

            int stride = checked(decoded.PixelWidth * 4);
            int rowsPerCopy = Math.Max(
                1,
                Math.Min(
                    decoded.PixelHeight,
                    ExternalFileDropValidationCopyBufferBytes / Math.Max(1, stride)));
            byte[] copyBuffer = GC.AllocateUninitializedArray<byte>(
                checked(stride * rowsPerCopy));
            for (int y = 0; y < decoded.PixelHeight; y += rowsPerCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = Math.Min(rowsPerCopy, decoded.PixelHeight - y);
                decoded.CopyPixels(
                    new Int32Rect(0, y, decoded.PixelWidth, rows),
                    copyBuffer,
                    stride,
                    offset: 0);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "External image validation was canceled.",
                ex,
                cancellationToken);
        }
        catch
        {
            sourceWidth = 0;
            sourceHeight = 0;
            return false;
        }
    }

    private void BeginExternalFileDropSession(
        IReadOnlyList<ValidatedExternalImage> images)
    {
        _externalFileDropPreviousSelectionPaths.Clear();
        _externalFileDropPreviousSelectionPaths.AddRange(_selectedPaths);
        _externalFileDropPreviousPrimaryPath = _primarySelectedPath;
        _externalFileDropPreviousSuppressStateSave = _suppressStateSave;
        _externalFileDropSessionCaptured = true;
        _suppressStateSave = true;
        _externalFileDropSessionGeneration = ++_externalFileDropGeneration;

        _externalFileDropCohort.Clear();
        _externalFileDropAddedProjectionTiles.Clear();
        _externalFileDropTransientTiles.Clear();
        _externalFileDropProjectionReplacements.Clear();
        _externalFileDropSourceVersions.Clear();
        foreach (ValidatedExternalImage image in images)
        {
            Tile? previousProjection = _tiles.FirstOrDefault(candidate =>
                candidate.IsRealFile
                && string.Equals(
                    candidate.Path,
                    image.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase));
            Tile tile = CreateExternalFileDropTile(image);
            _externalFileDropTransientTiles.Add(tile);
            if (previousProjection?.Thumbnail is BitmapSource thumbnail)
                tile.Thumbnail = thumbnail;

            _externalFileDropCohort.Add(tile);
            _externalFileDropSourceVersions[tile] =
                image.Version.WithSession(_externalFileDropSessionGeneration);
            if (previousProjection is null)
            {
                _tiles.Add(tile);
                _externalFileDropAddedProjectionTiles.Add(tile);
            }
            else
            {
                int projectionIndex = _tiles.IndexOf(previousProjection);
                _tiles[projectionIndex] = tile;
                _externalFileDropProjectionReplacements.Add(new(
                    tile,
                    previousProjection));
            }
        }

        Tile first = _externalFileDropCohort[0];
        SetSelection([first], first, _tiles.IndexOf(first));
        OpenModal();

        _externalFileDropThumbnailCts?.Cancel();
        _externalFileDropThumbnailCts?.Dispose();
        var thumbnailCts = new CancellationTokenSource();
        _externalFileDropThumbnailCts = thumbnailCts;
        _ = LoadThumbnailCandidatesAsync(
            _externalFileDropCohort,
            _loadGeneration,
            thumbnailCts.Token,
            updateProgress: false);
    }

    private Tile CreateExternalFileDropTile(ValidatedExternalImage image)
    {
        var info = new FileInfo(image.CanonicalPath);
        int paletteIndex = image.CanonicalPath.GetHashCode(
            StringComparison.OrdinalIgnoreCase) & int.MaxValue;
        string normalizedPath = NormalizeFavoritePath(image.CanonicalPath);
        bool seen = _seenPaths.Contains(normalizedPath);
        var tile = new Tile
        {
            ArtBase = MakeBaseBrush(paletteIndex),
            ArtGlow = MakeGlowBrush(paletteIndex),
            FileName = Path.GetFileName(image.CanonicalPath),
            Fav = FavoriteLevelForPath(image.CanonicalPath),
            Unseen = !seen,
            ShowUnseenDot = _showUnseenDots && !seen,
            Group = FormatGroup(info.CreationTime),
            CardWidth = SizeSlider.Value,
            ModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = info.CreationTimeUtc,
            FavoriteChangedAtUtc =
                _favoriteChangedAtUtcByPath.TryGetValue(
                    normalizedPath,
                    out DateTimeOffset favoriteActivity)
                    ? favoriteActivity
                    : null,
            SourceLength = image.Length,
            SourceLastWriteUtcTicks = image.LastWriteUtcTicks,
            SourceCreationUtcTicks = image.CreationUtcTicks,
            Path = image.CanonicalPath,
            IsRealFile = true,
            FolderBucketKey = "",
            FolderBucketLabel = "",
            ImagePixelWidth = image.Width,
            ImagePixelHeight = image.Height,
            SizeText = FormatBytes(image.Length),
            ModifiedText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        };
        if (TryGetManagedEnhancedOutputForPath(
                image.CanonicalPath,
                out ManagedEnhancedOutput enhanced))
        {
            tile.EnhancedOutputPath = enhanced.OutputPath;
        }
        ApplyTileEnhancementAvailability(
            tile,
            GetManagedEnhancementVersionsForPath(image.CanonicalPath));
        ApplyTileEnhancementQueueActivity(tile);
        ApplyTileVideoAvailability(tile);
        ApplyCardLayout(tile);
        return tile;
    }

    private bool TryValidateExternalFileDropTile(
        Tile tile,
        out string canonical,
        out string reason)
    {
        canonical = "";
        reason = "the temporary image is unavailable";
        if (!TryResolveExternalFileDropSessionTile(
                tile,
                out Tile authoritativeTile,
                out ExternalFileDropSourceVersion expected)
            || string.IsNullOrWhiteSpace(authoritativeTile.Path)
            || !Path.IsPathFullyQualified(authoritativeTile.Path)
            || !SupportedImageExtensions.Contains(
                Path.GetExtension(authoritativeTile.Path)))
        {
            return false;
        }

        try
        {
            string resolved = Path.GetFullPath(
                _resolveFinalPath(Path.GetFullPath(authoritativeTile.Path)));
            if (!string.Equals(
                    resolved,
                    authoritativeTile.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "the temporary image identity changed";
                return false;
            }

            using var stream = new FileStream(
                resolved,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.RandomAccess);
            if (!WindowsPathIdentity.TryGetFinalPath(
                    stream.SafeFileHandle,
                    out string openedCanonical)
                || !string.Equals(
                    openedCanonical,
                    authoritativeTile.Path,
                    StringComparison.OrdinalIgnoreCase)
                || !TryReadExternalFileDropSourceVersion(
                    stream.SafeFileHandle,
                    out ExternalFileDropSourceVersion current)
                || !expected.SameFileVersion(current))
            {
                reason = "the temporary image changed after it was dropped";
                return false;
            }

            canonical = openedCanonical;
            reason = "";
            return true;
        }
        catch
        {
            canonical = "";
            return false;
        }
    }

    private bool TryValidateExternalFileDropTileForEnqueue(
        Tile capturedTile,
        out string error)
    {
        if (!TryCaptureExternalFileDropPrePublishGuard(
                capturedTile,
                out ExternalFileDropPrePublishGuard guard))
        {
            error = "the temporary image session ended";
            return false;
        }

        return TryValidateExternalFileDropPrePublishGuard(guard, out error);
    }

    private bool TryCaptureExternalFileDropPrePublishGuard(
        Tile capturedTile,
        out ExternalFileDropPrePublishGuard guard)
    {
        guard = default;
        if (!TryResolveExternalFileDropSessionTile(
                capturedTile,
                out Tile authoritativeTile,
                out ExternalFileDropSourceVersion sourceVersion))
        {
            return false;
        }

        guard = new ExternalFileDropPrePublishGuard(
            _externalFileDropSessionGeneration,
            authoritativeTile.Path,
            sourceVersion);
        return true;
    }

    private bool TryValidateExternalFileDropPrePublishGuard(
        ExternalFileDropPrePublishGuard guard,
        out string error)
    {
        error = "the temporary image session ended";
        if (!ExternalFileDropSessionActive
            || guard.SessionGeneration != _externalFileDropSessionGeneration
            || !TryGetExternalFileDropSessionTile(
                guard.CanonicalPath,
                out Tile authoritativeTile)
            || !_externalFileDropSourceVersions.TryGetValue(
                authoritativeTile,
                out ExternalFileDropSourceVersion current)
            || current.SessionGeneration != guard.SessionGeneration
            || !current.SameFileVersion(guard.SourceVersion))
        {
            return false;
        }

        return TryValidateExternalFileDropTile(
            authoritativeTile,
            out _,
            out error);
    }

    private bool IsExternalFileDropPrePublishGuardCurrent(
        ExternalFileDropPrePublishGuard guard)
        => ExternalFileDropSessionActive
            && guard.SessionGeneration == _externalFileDropSessionGeneration
            && TryGetExternalFileDropSessionTile(
                guard.CanonicalPath,
                out Tile authoritativeTile)
            && _externalFileDropSourceVersions.TryGetValue(
                authoritativeTile,
                out ExternalFileDropSourceVersion current)
            && current.SessionGeneration == guard.SessionGeneration
            && current.SameFileVersion(guard.SourceVersion);

    private Func<string?>? CaptureExternalFileDropPrePublishValidator(
        Tile capturedTile)
    {
        if (!TryCaptureExternalFileDropPrePublishGuard(
                capturedTile,
                out ExternalFileDropPrePublishGuard guard))
        {
            return null;
        }

        return () => TryValidateExternalFileDropPrePublishGuard(
                guard,
                out string error)
            ? null
            : error;
    }

    private void CloseExternalFileDropSessionForReplacement()
    {
        CancelPendingExternalFileDropIntake();
        if (!ExternalFileDropSessionActive)
            return;

        if (Modal.Visibility == Visibility.Visible)
            CloseModal(restoreFocus: false);
        else
            RestoreExternalFileDropSession();
    }

    private void RestoreExternalFileDropSession()
    {
        CancelPendingExternalFileDropIntake();
        if (!_externalFileDropSessionCaptured)
            return;

        _externalFileDropThumbnailCts?.Cancel();
        _externalFileDropThumbnailCts?.Dispose();
        _externalFileDropThumbnailCts = null;

        var replacedTemporaryTiles = new HashSet<Tile>(
            ReferenceEqualityComparer.Instance);
        foreach (ExternalFileDropProjectionReplacement replacement
            in _externalFileDropProjectionReplacements)
        {
            replacedTemporaryTiles.Add(replacement.Temporary);
            int projectionIndex = _tiles.IndexOf(replacement.Temporary);
            if (projectionIndex >= 0)
            {
                if (_tiles.Contains(replacement.Previous))
                    _tiles.RemoveAt(projectionIndex);
                else
                    _tiles[projectionIndex] = replacement.Previous;
            }
            RestoreExternalFileDropReplacementThumbnail(replacement);
        }
        foreach (Tile tile in _externalFileDropAddedProjectionTiles)
            _tiles.Remove(tile);
        foreach (Tile tile in _externalFileDropTransientTiles)
        {
            if (!replacedTemporaryTiles.Contains(tile))
                ReleaseExternalFileDropThumbnail(tile);
        }
        _modalFilmstripTiles.ReplaceAll(Array.Empty<Tile>());

        var previousPaths = new HashSet<string>(
            _externalFileDropPreviousSelectionPaths,
            StringComparer.OrdinalIgnoreCase);
        List<Tile> restored = _tiles
            .Where(tile => previousPaths.Contains(tile.Path))
            .ToList();
        Tile? primary = restored.FirstOrDefault(tile =>
            string.Equals(
                tile.Path,
                _externalFileDropPreviousPrimaryPath,
                StringComparison.OrdinalIgnoreCase))
            ?? restored.LastOrDefault();

        bool previousSuppress = _externalFileDropPreviousSuppressStateSave;
        _externalFileDropSessionCaptured = false;
        _externalFileDropSessionGeneration = 0;
        _externalFileDropCohort.Clear();
        _externalFileDropAddedProjectionTiles.Clear();
        _externalFileDropTransientTiles.Clear();
        _externalFileDropProjectionReplacements.Clear();
        _externalFileDropSourceVersions.Clear();
        _externalFileDropPreviousSelectionPaths.Clear();
        _externalFileDropPreviousPrimaryPath = null;
        try
        {
            SetSelection(restored, primary);
        }
        finally
        {
            _suppressStateSave = previousSuppress;
        }
    }

    private void RestoreExternalFileDropReplacementThumbnail(
        ExternalFileDropProjectionReplacement replacement)
    {
        if (replacement.Temporary.Thumbnail is BitmapSource thumbnail)
            replacement.Previous.Thumbnail = thumbnail;
        if (_residentThumbnailNodes.TryGetValue(
                replacement.Temporary.Path,
                out LinkedListNode<Tile>? node)
            && ReferenceEquals(node.Value, replacement.Temporary))
        {
            node.Value = replacement.Previous;
        }
        replacement.Temporary.Thumbnail = null;
    }

    private void ReleaseExternalFileDropThumbnail(Tile tile)
    {
        _protectedResidentThumbnailPaths.Remove(tile.Path);
        if (_residentThumbnailNodes.TryGetValue(
                tile.Path,
                out LinkedListNode<Tile>? node)
            && ReferenceEquals(node.Value, tile))
        {
            _residentThumbnailLru.Remove(node);
            _residentThumbnailNodes.Remove(tile.Path);
            if (_residentThumbnailByteSizes.Remove(tile.Path, out long bytes))
                _residentThumbnailBytes = Math.Max(0, _residentThumbnailBytes - bytes);
        }
        tile.Thumbnail = null;
    }

    private ExternalImageDropSmokeSnapshot SnapshotExternalImageDrop(
        bool accepted,
        string reason,
        string status)
        => new(
            accepted,
            _externalFileDropCohort.Select(static tile => tile.Path).ToArray(),
            _externalFileDropCohort.Count,
            _tiles.Count,
            _allTiles.Count,
            SelectedTile()?.Path,
            Modal.Visibility == Visibility.Visible,
            ModalDeleteButton.IsEnabled,
            reason,
            status);

    public Task<ExternalImageDropSmokeSnapshot> DropExternalImagesForSmokeAsync(
        IEnumerable<string> paths)
        => ApplyDroppedImagesAsync(paths);

    public ViewerDropClassificationSmokeSnapshot ClassifyViewerDropForSmoke(
        IEnumerable<string> paths)
    {
        ViewerDropPayload payload = ReadViewerDropPayload(paths);
        return new ViewerDropClassificationSmokeSnapshot(
            payload.Kind.ToString(),
            ViewerDropPayloadHasAcceptableAffordance(payload),
            payload.Reason);
    }

    public bool ExternalFileDropSessionActiveForSmoke
        => ExternalFileDropSessionActive;

    public string[] ExternalFileDropCohortPathsForSmoke
        => _externalFileDropCohort
            .Select(static tile => tile.Path)
            .ToArray();

    public string[] ModalFilmstripPathsForSmoke
        => _modalFilmstripTiles
            .Select(static tile => tile.Path)
            .ToArray();

    public bool ExternalFileDropSourceValidForSmoke
        => SelectedTile() is Tile tile
            && TryValidateExternalFileDropTile(tile, out _, out _);

    public Tile? CaptureExternalFileDropTileForSmoke()
        => SelectedTile() is Tile tile && IsExternalFileDropSessionTile(tile)
            ? tile
            : null;

    public bool ValidateExternalFileDropTileForEnqueueForSmoke(
        Tile capturedTile,
        out string error)
        => TryValidateExternalFileDropTileForEnqueue(capturedTile, out error);

    public async Task<ExternalFileDropPrePublishSmokeSnapshot>
        PublishExternalFileDropReservationForSmokeAsync(Tile capturedTile)
    {
        Func<string?>? prePublishValidator =
            CaptureExternalFileDropPrePublishValidator(capturedTile);
        if (prePublishValidator is null)
        {
            return new ExternalFileDropPrePublishSmokeSnapshot(
                false,
                409,
                "the temporary image session ended");
        }

        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
            new
            {
                operation = "upscale",
                presetId = "external-drop-smoke",
                sourceId = capturedTile.Path,
                sourcePath = capturedTile.Path,
                scale = 2,
                adapterId = "comfyui",
            },
            recoverySourceIdentity: capturedTile.Path,
            prePublishValidator: prePublishValidator);
        return new ExternalFileDropPrePublishSmokeSnapshot(
            response.SavedForDelivery,
            response.StatusCode,
            response.Error);
    }

    public void PauseNextExternalFileDropValidationForSmoke(
        ManualResetEventSlim entered,
        ManualResetEventSlim release)
        => _externalFileDropValidationPauseForSmoke = new(
            entered ?? throw new ArgumentNullException(nameof(entered)),
            release ?? throw new ArgumentNullException(nameof(release)));

    public bool ExternalFileDropExplicitAiSourcesAvailableForSmoke
    {
        get
        {
            if (SelectedTile() is not Tile { IsRealFile: true } tile
                || !TryResolveEnhancementSourceIdentity(
                    tile.Path,
                    out string identity)
                || !File.Exists(identity)
                || !TryResolveCurrentModalI2iEditSource(out _, out _)
                || !TryCaptureVideoSource(
                    tile,
                    "original",
                    out _,
                    out _))
            {
                return false;
            }

            return true;
        }
    }

    public bool ExternalFileDropSurfaceContractForSmoke
        => !string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetName(
                    ViewerFolderDropTarget))
            && !string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetHelpText(
                    ViewerFolderDropTarget));

    public bool PrepareCatalogTileForExternalFileDropProjectionSmoke(
        string path,
        int favoriteLevel)
    {
        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                canonical,
                StringComparison.OrdinalIgnoreCase));
        if (tile is null)
            return false;

        int normalizedLevel = Math.Clamp(favoriteLevel, 1, 5);
        tile.Fav = normalizedLevel;
        tile.Photorealized = true;
        tile.PhotorealFavoriteLevel = normalizedLevel;
        tile.VideoGenerated = true;
        tile.VideoFavoriteLevel = normalizedLevel;
        return true;
    }

    public bool RestoreCatalogTileAfterExternalFileDropProjectionSmoke(
        string path)
    {
        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                canonical,
                StringComparison.OrdinalIgnoreCase));
        if (tile is null)
            return false;

        tile.Fav = FavoriteLevelForPath(tile.Path);
        ApplyTileEnhancementAvailability(
            tile,
            GetManagedEnhancementVersionsForPath(tile.Path));
        ApplyTileVideoAvailability(tile);
        return true;
    }

    public bool ExternalFileDropDeleteEnabledForSmoke
        => ModalDeleteButton.IsEnabled;
}

public sealed record ExternalImageDropSmokeSnapshot(
    bool Accepted,
    IReadOnlyList<string> CohortPaths,
    int CohortCount,
    int ProjectionCount,
    int CatalogCount,
    string? SelectedPath,
    bool ModalVisible,
    bool DeleteEnabled,
    string Reason,
    string Status);

public sealed record ViewerDropClassificationSmokeSnapshot(
    string Kind,
    bool AffordanceAccepted,
    string Reason);

public sealed record ExternalFileDropPrePublishSmokeSnapshot(
    bool SavedForDelivery,
    int StatusCode,
    string Error);
