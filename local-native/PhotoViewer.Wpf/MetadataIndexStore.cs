using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhotoViewer.Wpf;

/// <summary>
/// WPF-owned derived cache for catalog dimensions and searchable prompt text.
/// It intentionally does not share ownership with Browser favorites, seen,
/// recent folders, viewer settings, or enhancement jobs.
/// </summary>
internal static class MetadataIndexStore
{
    private const int Magic = 0x494D5650; // "PVMI" in little-endian byte order.
    private const int Version = 1;
    private const int MaximumEntryCount = 1_000_000;
    private const int MaximumPathBytes = 128 * 1024;
    private const int MaximumPromptBytes = 4 * 1024 * 1024;
    private const long MaximumIndexBytes = 1024L * 1024 * 1024;
    private const int PayloadHashBytes = 32;
    private const int HeaderBytes = sizeof(int) * 3 + sizeof(long) + PayloadHashBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ResolvePath(IReadOnlyList<string> folderSet, string viewerStatePath)
    {
        ArgumentNullException.ThrowIfNull(folderSet);
        string? overrideDirectory = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY");
        string directory;
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            directory = Path.GetFullPath(overrideDirectory);
        }
        else
        {
            string stateFullPath = Path.GetFullPath(viewerStatePath);
            directory = Path.Combine(
                Path.GetDirectoryName(stateFullPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "metadata-index-v1");
        }

        string identity = string.Join(
            '\n',
            folderSet
                .Select(static folder => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .OrderBy(static folder => folder, StringComparer.OrdinalIgnoreCase)
                .Select(static folder => folder.ToUpperInvariant()));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(directory, $"{digest}.pvmi");
    }

    public static MetadataIndexLoadResult Load(string path, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return MetadataIndexLoadResult.Missing(fullPath);

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                // Our writer publishes through an atomic replace, so readers
                // only need to share read/delete. Denying in-place writes
                // keeps the bytes parsed below identical to the payload that
                // passed the checksum above.
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
            if (reader.ReadInt32() != Magic)
                return MetadataIndexLoadResult.Invalid(fullPath, "metadata index magic did not match");
            int version = reader.ReadInt32();
            if (version != Version)
                return MetadataIndexLoadResult.Unsupported(fullPath, $"metadata index version {version} is unsupported");
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumEntryCount)
                return MetadataIndexLoadResult.Invalid(fullPath, $"metadata index entry count {count} is outside the safe bound");
            long payloadLength = reader.ReadInt64();
            byte[] expectedPayloadHash = reader.ReadBytes(PayloadHashBytes);
            if (expectedPayloadHash.Length != PayloadHashBytes
                || payloadLength < 0
                || payloadLength > MaximumIndexBytes
                || stream.Length != HeaderBytes + payloadLength)
            {
                return MetadataIndexLoadResult.Invalid(fullPath, "metadata index payload length was invalid");
            }

            byte[] actualPayloadHash = ComputePayloadHash(stream, payloadLength, token);
            if (!CryptographicOperations.FixedTimeEquals(expectedPayloadHash, actualPayloadHash))
                return MetadataIndexLoadResult.Invalid(fullPath, "metadata index payload checksum did not match");
            stream.Position = HeaderBytes;

            var entries = new Dictionary<string, MetadataIndexEntry>(count, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < count; index++)
            {
                if ((index & 255) == 0)
                    token.ThrowIfCancellationRequested();
                string sourcePath = ReadBoundedString(reader, MaximumPathBytes);
                long sourceLength = reader.ReadInt64();
                long sourceLastWriteUtcTicks = reader.ReadInt64();
                long sourceCreationUtcTicks = reader.ReadInt64();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                string prompt = ReadBoundedString(reader, MaximumPromptBytes);
                if (!Path.IsPathFullyQualified(sourcePath)
                    || sourceLength < 0
                    || sourceLastWriteUtcTicks < DateTime.MinValue.Ticks
                    || sourceLastWriteUtcTicks > DateTime.MaxValue.Ticks
                    || sourceCreationUtcTicks < DateTime.MinValue.Ticks
                    || sourceCreationUtcTicks > DateTime.MaxValue.Ticks
                    || width <= 0
                    || height <= 0)
                {
                    return MetadataIndexLoadResult.Invalid(fullPath, $"metadata index entry {index} was invalid");
                }

                entries[sourcePath] = new MetadataIndexEntry(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtcTicks,
                    sourceCreationUtcTicks,
                    width,
                    height,
                    prompt);
            }

            if (stream.Position != stream.Length)
                return MetadataIndexLoadResult.Invalid(fullPath, "metadata index had unexpected trailing bytes");
            return MetadataIndexLoadResult.Loaded(fullPath, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or EndOfStreamException
            or DecoderFallbackException
            or ArgumentException
            or NotSupportedException)
        {
            return MetadataIndexLoadResult.Invalid(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static MetadataIndexSaveResult Save(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> entries,
        CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        if (entries.Count > MaximumEntryCount)
            return MetadataIndexSaveResult.Failed(fullPath, $"entry count {entries.Count} exceeds the safe bound");

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return MetadataIndexSaveResult.Failed(fullPath, "metadata index directory was unavailable");

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        string lockPath = fullPath + ".lock";
        FileStream? lockStream = null;
        try
        {
            Directory.CreateDirectory(directory);
            lockStream = AcquireWriterLock(lockPath, token);
            if (TryReadExistingVersion(fullPath, out int existingVersion) && existingVersion > Version)
            {
                return MetadataIndexSaveResult.Preserved(
                    fullPath,
                    entries.Count,
                    $"newer metadata index version {existingVersion} was preserved at commit time",
                    MetadataIndexSaveDisposition.Protected);
            }
            CleanupStaleTemporaryFiles(directory, Path.GetFileName(fullPath), token);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(entries.Count);
                writer.Write(0L); // Payload length is committed after the payload is complete.
                writer.Write(new byte[PayloadHashBytes]);
                int index = 0;
                foreach (MetadataIndexEntry entry in entries)
                {
                    if ((index++ & 255) == 0)
                        token.ThrowIfCancellationRequested();
                    WriteBoundedString(writer, entry.Path, MaximumPathBytes);
                    writer.Write(entry.SourceLength);
                    writer.Write(entry.SourceLastWriteUtcTicks);
                    writer.Write(entry.SourceCreationUtcTicks);
                    writer.Write(entry.Width);
                    writer.Write(entry.Height);
                    WriteBoundedString(writer, entry.Prompt ?? "", MaximumPromptBytes);
                    if (stream.Position - HeaderBytes > MaximumIndexBytes)
                        throw new InvalidDataException($"metadata index payload exceeds {MaximumIndexBytes:N0} bytes");
                }
                writer.Flush();

                long payloadLength = stream.Length - HeaderBytes;
                byte[] payloadHash = ComputePayloadHash(stream, payloadLength, token);
                stream.Position = sizeof(int) * 3;
                writer.Write(payloadLength);
                writer.Write(payloadHash);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return MetadataIndexSaveResult.Saved(fullPath, entries.Count);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            TryDeleteTemporary(temporaryPath);
            return MetadataIndexSaveResult.Failed(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lockStream?.Dispose();
            TryDeleteTemporary(lockPath);
        }
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > maximumBytes)
            throw new InvalidDataException($"string byte count {byteCount} is outside the safe bound");
        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
            throw new EndOfStreamException("metadata index string was truncated");
        return StrictUtf8.GetString(bytes);
    }

    private static void WriteBoundedString(BinaryWriter writer, string value, int maximumBytes)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException($"string byte count {bytes.Length} exceeds the safe bound");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ComputePayloadHash(FileStream stream, long payloadLength, CancellationToken token)
    {
        stream.Position = HeaderBytes;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long remaining = payloadLength;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, requested);
            if (read <= 0)
                throw new EndOfStreamException("metadata index payload was truncated while hashing");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hash.GetHashAndReset();
    }

    private static FileStream AcquireWriterLock(string lockPath, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (watch.ElapsedMilliseconds < 2_000)
            {
                if (token.WaitHandle.WaitOne(25))
                    token.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool TryReadExistingVersion(string path, out int version)
    {
        version = 0;
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: false);
            if (reader.ReadInt32() != Magic)
                return false;
            version = reader.ReadInt32();
            return true;
        }
        catch
        {
            // Malformed derived data is replaceable after a successful source
            // pass; only a recognizable newer schema is protected here.
            return false;
        }
    }

    private static void CleanupStaleTemporaryFiles(string directory, string targetFileName, CancellationToken token)
    {
        string pattern = $".{targetFileName}.*.tmp";
        DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetLastWriteTimeUtc(candidate) < cutoffUtc)
                        File.Delete(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A failed cleanup does not justify touching the last valid index.
        }
    }
}

internal sealed record MetadataIndexEntry(
    string Path,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    long SourceCreationUtcTicks,
    int Width,
    int Height,
    string Prompt)
{
    public bool Matches(Tile tile)
        => SourceLength == tile.SourceLength
            && SourceLastWriteUtcTicks == tile.SourceLastWriteUtcTicks
            && SourceCreationUtcTicks == tile.SourceCreationUtcTicks;
}

internal sealed record MetadataIndexLoadResult(
    string Path,
    MetadataIndexLoadState State,
    IReadOnlyDictionary<string, MetadataIndexEntry> Entries,
    string? Error)
{
    public static MetadataIndexLoadResult Missing(string path)
        => new(path, MetadataIndexLoadState.Missing, new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase), null);

    public static MetadataIndexLoadResult Loaded(string path, IReadOnlyDictionary<string, MetadataIndexEntry> entries)
        => new(path, MetadataIndexLoadState.Loaded, entries, null);

    public static MetadataIndexLoadResult Invalid(string path, string error)
        => new(path, MetadataIndexLoadState.Invalid, new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase), error);

    public static MetadataIndexLoadResult Unsupported(string path, string error)
        => new(path, MetadataIndexLoadState.Unsupported, new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase), error);
}

internal enum MetadataIndexLoadState
{
    Missing,
    Loaded,
    Invalid,
    Unsupported,
}

internal sealed record MetadataIndexSaveResult(
    string Path,
    bool Ok,
    bool Written,
    int EntryCount,
    string? Error,
    MetadataIndexSaveDisposition Disposition)
{
    public static MetadataIndexSaveResult Saved(string path, int entryCount)
        => new(path, true, true, entryCount, null, MetadataIndexSaveDisposition.Saved);

    public static MetadataIndexSaveResult Preserved(
        string path,
        int entryCount,
        string reason,
        MetadataIndexSaveDisposition disposition = MetadataIndexSaveDisposition.Reused)
        => new(path, true, false, entryCount, reason, disposition);

    public static MetadataIndexSaveResult Failed(string path, string error)
        => new(path, false, false, 0, error, MetadataIndexSaveDisposition.Failed);
}

internal enum MetadataIndexSaveDisposition
{
    Saved,
    Reused,
    Protected,
    Incomplete,
    CatalogChanged,
    Failed,
}
