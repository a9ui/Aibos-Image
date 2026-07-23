using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal enum SharedDataRootSetupStatus
{
    Ready,
    Created,
    AlreadyConfigured,
    Blocked,
}

internal sealed record SharedDataRootStoreProbe(
    string RelativePath,
    bool Exists,
    long Length,
    string? Sha256);

internal sealed record SharedDataRootOutputsProbe(
    bool Exists,
    int FileCount,
    long TotalBytes,
    string ManifestSha256,
    string? ContentSha256);

internal sealed record SharedDataRootSetupResult(
    SharedDataRootSetupStatus Status,
    string LocatorPath,
    string? SharedDataRoot,
    bool Changed,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<SharedDataRootStoreProbe> Stores,
    SharedDataRootOutputsProbe? Outputs)
{
    internal bool Ok => Status is not SharedDataRootSetupStatus.Blocked;
}

/// <summary>
/// Performs the separately reviewed, one-time creation of the default
/// PV-ROOT-001 locator. Inspection is the default operation. Apply is
/// create-only and never initializes, copies, merges, rewrites, or deletes a
/// shared store. Existing locators are either left byte-identical or rejected.
/// </summary>
internal static class SharedDataRootConfigurator
{
    private const long MaxStoreBytes = 64L * 1024 * 1024;
    private const int MaxOutputFiles = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions LocatorJson = new()
    {
        WriteIndented = true,
    };
    private static readonly string[] StoreRelativePaths =
    [
        "favorites.json",
        "seen.json",
        "settings.json",
        "albums.json",
        "search-history.json",
        "recent-folders.json",
        Path.Combine("enhance", "jobs.json"),
    ];

    internal static SharedDataRootSetupResult InspectDefault(string existingRoot)
        => Run(existingRoot, SharedDataRootLocator.GetDefaultLocatorPath(), apply: false);

    internal static SharedDataRootSetupResult ApplyDefault(string existingRoot)
        => Run(existingRoot, SharedDataRootLocator.GetDefaultLocatorPath(), apply: true);

    internal static SharedDataRootSetupResult Inspect(
        string existingRoot,
        string locatorPath)
        => Run(existingRoot, locatorPath, apply: false);

    internal static SharedDataRootSetupResult Apply(
        string existingRoot,
        string locatorPath)
        => Run(existingRoot, locatorPath, apply: true);

    private static SharedDataRootSetupResult Run(
        string existingRoot,
        string locatorPath,
        bool apply)
    {
        if (!WindowsPathIdentity.TryResolveExistingDirectory(
                existingRoot,
                out string canonicalRoot))
        {
            return Blocked(
                locatorPath,
                "data-root-unavailable",
                "The requested shared data root must be an existing, readable directory.");
        }

        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(
                locatorPath,
                out string normalizedLocatorPath))
        {
            return Blocked(
                locatorPath,
                "locator-path-invalid",
                "The shared-root locator path must be fully qualified.");
        }

        if (!TryPreflightStores(
                canonicalRoot,
                hashOutputContents: false,
                out IReadOnlyList<SharedDataRootStoreProbe> initialStores,
                out SharedDataRootOutputsProbe? initialOutputs,
                out string? preflightCode,
                out string? preflightError))
        {
            return Blocked(
                normalizedLocatorPath,
                preflightCode ?? "shared-store-invalid",
                preflightError ?? "A shared durable store could not be validated.",
                canonicalRoot,
                initialStores,
                initialOutputs);
        }

        SharedDataRootSetupResult inspected = InspectLocator(
            normalizedLocatorPath,
            canonicalRoot,
            initialStores,
            initialOutputs);
        if (!apply || inspected.Status is not SharedDataRootSetupStatus.Ready)
            return inspected;

        if (!SharedDataRootLocatorLease.TryAcquireWriter(
                normalizedLocatorPath,
                out SharedDataRootLocatorLease? writerLease,
                out string? leaseCode,
                out string? leaseError))
        {
            return Blocked(
                normalizedLocatorPath,
                leaseCode ?? "locator-writer-lease-unavailable",
                leaseError ?? "The shared-root locator writer lease is unavailable.",
                canonicalRoot,
                initialStores,
                initialOutputs);
        }

        using (writerLease)
        {
            if (!TryPreflightStores(
                    canonicalRoot,
                    hashOutputContents: true,
                    out IReadOnlyList<SharedDataRootStoreProbe> stores,
                    out SharedDataRootOutputsProbe? outputs,
                    out preflightCode,
                    out preflightError))
            {
                return Blocked(
                    normalizedLocatorPath,
                    preflightCode ?? "shared-store-invalid",
                    preflightError ?? "A shared durable store could not be validated.",
                    canonicalRoot,
                    stores,
                    outputs);
            }

            SharedDataRootSetupResult rechecked = InspectLocator(
                normalizedLocatorPath,
                canonicalRoot,
                stores,
                outputs);
            if (rechecked.Status is not SharedDataRootSetupStatus.Ready)
                return rechecked;

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = SharedDataRootLocator.SchemaVersion,
                    sharedDataRoot = canonicalRoot,
                },
                LocatorJson);

            if (!TryCreateLocatorAtomically(
                    normalizedLocatorPath,
                    payload,
                    out string? createCode,
                    out string? createError))
            {
                return Blocked(
                    normalizedLocatorPath,
                    createCode ?? "locator-create-failed",
                    createError ?? "The shared-root locator could not be created.",
                    canonicalRoot,
                    stores,
                    outputs);
            }

            SharedDataRootResolution resolution = SharedDataRootLocator.Resolve(
                normalizedLocatorPath,
                canonicalRoot);
            bool locatorVerified = resolution.Status == SharedDataRootResolutionStatus.Resolved
                && string.Equals(
                    resolution.SharedDataRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase);
            if (!locatorVerified)
            {
                bool rolledBack = TryRollbackCreatedLocator(
                    normalizedLocatorPath,
                    payload);
                return Blocked(
                    normalizedLocatorPath,
                    rolledBack
                        ? "locator-postcondition-failed"
                        : "locator-postcondition-rollback-failed",
                    rolledBack
                        ? "The locator failed post-creation validation and was removed."
                        : "The locator failed post-creation validation and could not be safely removed.",
                    canonicalRoot,
                    stores,
                    outputs);
            }

            if (!TryPreflightStores(
                    canonicalRoot,
                    hashOutputContents: true,
                    out IReadOnlyList<SharedDataRootStoreProbe> afterStores,
                    out SharedDataRootOutputsProbe? afterOutputs,
                    out preflightCode,
                    out preflightError)
                || !StoreSnapshotsEqual(stores, afterStores)
                || !Equals(outputs, afterOutputs))
            {
                bool rolledBack = TryRollbackCreatedLocator(
                    normalizedLocatorPath,
                    payload);
                return Blocked(
                    normalizedLocatorPath,
                    rolledBack
                        ? "shared-state-changed-during-setup"
                        : "shared-state-changed-rollback-failed",
                    rolledBack
                        ? "Shared state changed during setup; the new locator was removed."
                        : "Shared state changed during setup and the new locator could not be safely removed.",
                    canonicalRoot,
                    afterStores,
                    afterOutputs);
            }

            return new SharedDataRootSetupResult(
                SharedDataRootSetupStatus.Created,
                normalizedLocatorPath,
                canonicalRoot,
                true,
                null,
                null,
                afterStores,
                afterOutputs);
        }
    }

    private static SharedDataRootSetupResult InspectLocator(
        string locatorPath,
        string canonicalRoot,
        IReadOnlyList<SharedDataRootStoreProbe> stores,
        SharedDataRootOutputsProbe? outputs)
    {
        SharedDataRootResolution resolution = SharedDataRootLocator.Resolve(
            locatorPath,
            canonicalRoot);
        if (resolution.Status == SharedDataRootResolutionStatus.LegacyFallback)
        {
            return new SharedDataRootSetupResult(
                SharedDataRootSetupStatus.Ready,
                locatorPath,
                canonicalRoot,
                false,
                null,
                null,
                stores,
                outputs);
        }

        if (resolution.Status == SharedDataRootResolutionStatus.Resolved
            && string.Equals(
                resolution.SharedDataRoot,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return new SharedDataRootSetupResult(
                SharedDataRootSetupStatus.AlreadyConfigured,
                locatorPath,
                canonicalRoot,
                false,
                null,
                null,
                stores,
                outputs);
        }

        string errorCode = resolution.Status == SharedDataRootResolutionStatus.Resolved
            ? "locator-conflict"
            : resolution.ErrorCode ?? "locator-unavailable";
        string error = resolution.Status == SharedDataRootResolutionStatus.Resolved
            ? "The existing locator points to a different shared data root and was not changed."
            : resolution.Error ?? "The existing locator is unavailable and was not changed.";
        return Blocked(
            locatorPath,
            errorCode,
            error,
            canonicalRoot,
            stores,
            outputs);
    }

    private static bool TryPreflightStores(
        string canonicalRoot,
        bool hashOutputContents,
        out IReadOnlyList<SharedDataRootStoreProbe> stores,
        out SharedDataRootOutputsProbe? outputs,
        out string? errorCode,
        out string? error)
    {
        var probes = new List<SharedDataRootStoreProbe>(StoreRelativePaths.Length);
        stores = probes;
        outputs = null;
        errorCode = null;
        error = null;

        foreach (string relativePath in StoreRelativePaths)
        {
            string fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
            if (!IsWithinRoot(canonicalRoot, fullPath))
            {
                errorCode = "shared-store-path-invalid";
                error = $"{relativePath} resolves outside the shared data root.";
                return false;
            }

            if (!TryReadStableJson(
                    fullPath,
                    relativePath,
                    out byte[]? bytes,
                    out SharedDataRootStoreProbe probe,
                    out errorCode,
                    out error))
            {
                probes.Add(probe);
                return false;
            }
            probes.Add(probe);
            if (bytes is null)
                continue;

            if (!TryValidateStore(
                    relativePath,
                    fullPath,
                    bytes,
                    out errorCode,
                    out error))
            {
                return false;
            }
        }

        string outputsRoot = Path.Combine(canonicalRoot, "enhance", "outputs");
        if (!TrySnapshotOutputs(
                canonicalRoot,
                outputsRoot,
                hashOutputContents,
                out outputs,
                out errorCode,
                out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadStableJson(
        string fullPath,
        string relativePath,
        out byte[]? bytes,
        out SharedDataRootStoreProbe probe,
        out string? errorCode,
        out string? error)
    {
        bytes = null;
        probe = new SharedDataRootStoreProbe(relativePath, false, 0, null);
        errorCode = null;
        error = null;

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (!WindowsPathIdentity.TryGetFinalPath(
                    stream.SafeFileHandle,
                    out string finalPath)
                || !string.Equals(
                    finalPath,
                    Path.GetFullPath(fullPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "shared-store-identity-invalid";
                error = $"{relativePath} is redirected or has an invalid file identity.";
                return false;
            }

            if (stream.Length > MaxStoreBytes)
            {
                errorCode = "shared-store-too-large";
                error = $"{relativePath} exceeds the setup preflight size limit.";
                probe = new SharedDataRootStoreProbe(relativePath, true, stream.Length, null);
                return false;
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                errorCode = "shared-store-changed-during-read";
                error = $"{relativePath} changed while it was being read.";
                return false;
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            probe = new SharedDataRootStoreProbe(
                relativePath,
                true,
                bytes.LongLength,
                sha256);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            errorCode = "shared-store-unreadable";
            error = $"{relativePath} could not be read: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateStore(
        string relativePath,
        string fullPath,
        byte[] bytes,
        out string? errorCode,
        out string? error)
    {
        errorCode = null;
        error = null;
        string json;
        JsonDocument document;
        try
        {
            if (HasUtf16OrUtf32Bom(bytes))
                throw new JsonException("UTF-16 and UTF-32 are unsupported.");
            int offset = HasUtf8Bom(bytes) ? 3 : 0;
            json = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
        }
        catch (Exception ex) when (
            ex is JsonException
                or DecoderFallbackException
                or ArgumentException)
        {
            errorCode = "shared-store-malformed";
            error = $"{relativePath} is malformed or unsupported: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "shared-store-malformed";
                error = $"{relativePath} must contain a JSON object.";
                return false;
            }

            if (!TryRejectDuplicateProperties(
                    document.RootElement,
                    "$",
                    out string? duplicatePath))
            {
                errorCode = "shared-store-ambiguous";
                error = $"{relativePath} contains a duplicate property at {duplicatePath}.";
                return false;
            }

            bool valid = relativePath.Replace('\\', '/') switch
            {
                "favorites.json" => ValidateFavorites(document.RootElement),
                "seen.json" => ValidateSeen(document.RootElement),
                "settings.json" => !ThumbnailStatusBorderSettingsStore.Parse(json).IsProtected,
                "albums.json" => AlbumStore.Read(fullPath).Supported,
                "search-history.json" => SearchHistoryStore.Read(fullPath).Supported,
                "recent-folders.json" => MainWindow.ReadSharedRecentFoldersForSmoke(fullPath).Ok,
                "enhance/jobs.json" => ValidateEnhancementJobs(document.RootElement),
                _ => false,
            };
            if (valid)
                return true;

            errorCode = "shared-store-unsupported";
            error = $"{relativePath} does not match the supported schema.";
            return false;
        }
    }

    private static bool ValidateFavorites(JsonElement root)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || !identities.Add(property.Name)
                || !TryReadFavoriteValue(property.Value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadFavoriteValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out double number)
                && double.IsFinite(number),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };

    private static bool ValidateSeen(JsonElement root)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || !identities.Add(property.Name))
            {
                return false;
            }

            bool valid = property.Value.ValueKind is JsonValueKind.True
                or JsonValueKind.False
                || (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out _))
                || (property.Value.ValueKind == JsonValueKind.String
                    && bool.TryParse(property.Value.GetString(), out _));
            if (!valid)
                return false;
        }
        return true;
    }

    private static bool ValidateEnhancementJobs(JsonElement root)
    {
        if (root.TryGetProperty("version", out JsonElement version)
            && (version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int value)
                || value != 1))
        {
            return false;
        }

        if (!root.TryGetProperty("jobs", out JsonElement jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return jobs.EnumerateArray().All(
            static job => job.ValueKind == JsonValueKind.Object);
    }

    private static bool TryRejectDuplicateProperties(
        JsonElement element,
        string path,
        out string? duplicatePath)
    {
        duplicatePath = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string childPath = $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                {
                    duplicatePath = childPath;
                    return false;
                }
                if (!TryRejectDuplicateProperties(
                        property.Value,
                        childPath,
                        out duplicatePath))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!TryRejectDuplicateProperties(
                        item,
                        $"{path}[{index}]",
                        out duplicatePath))
                {
                    return false;
                }
                index++;
            }
        }
        return true;
    }

    private static bool TrySnapshotOutputs(
        string canonicalRoot,
        string outputsRoot,
        bool hashContents,
        out SharedDataRootOutputsProbe? probe,
        out string? errorCode,
        out string? error)
    {
        probe = null;
        errorCode = null;
        error = null;
        try
        {
            FileAttributes outputsAttributes;
            try
            {
                outputsAttributes = File.GetAttributes(outputsRoot);
            }
            catch (FileNotFoundException)
            {
                probe = new SharedDataRootOutputsProbe(
                    false,
                    0,
                    0,
                    Convert.ToHexString(SHA256.HashData([])),
                    hashContents
                        ? Convert.ToHexString(SHA256.HashData([]))
                        : null);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                probe = new SharedDataRootOutputsProbe(
                    false,
                    0,
                    0,
                    Convert.ToHexString(SHA256.HashData([])),
                    hashContents
                        ? Convert.ToHexString(SHA256.HashData([]))
                        : null);
                return true;
            }

            if (!outputsAttributes.HasFlag(FileAttributes.Directory)
                || outputsAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                errorCode = "enhancement-outputs-identity-invalid";
                error = "The managed Enhancement output path is not a direct directory.";
                return false;
            }

            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    outputsRoot,
                    out string canonicalOutputs)
                || !string.Equals(
                    canonicalOutputs,
                    Path.GetFullPath(outputsRoot),
                    StringComparison.OrdinalIgnoreCase)
                || !IsWithinRoot(canonicalRoot, canonicalOutputs))
            {
                errorCode = "enhancement-outputs-identity-invalid";
                error = "The managed Enhancement output directory is redirected or invalid.";
                return false;
            }

            var entries = new List<(
                string FullPath,
                string Relative,
                long Length,
                long LastWriteUtcTicks)>();
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(canonicalOutputs);
            while (pendingDirectories.Count > 0)
            {
                string directory = pendingDirectories.Pop();
                foreach (string entryPath in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(entryPath);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        errorCode = "enhancement-outputs-identity-invalid";
                        error = "The managed Enhancement output tree contains a redirected entry.";
                        return false;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pendingDirectories.Push(entryPath);
                        continue;
                    }
                    if (entries.Count >= MaxOutputFiles)
                    {
                        errorCode = "enhancement-outputs-too-many";
                        error = "The managed Enhancement output tree exceeds the setup preflight file-count limit.";
                        return false;
                    }

                    var info = new FileInfo(entryPath);
                    entries.Add((
                        info.FullName,
                        Path.GetRelativePath(canonicalOutputs, entryPath)
                            .Replace(Path.DirectorySeparatorChar, '/'),
                        info.Length,
                        info.LastWriteTimeUtc.Ticks));
                }
            }

            long totalBytes = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            (string FullPath, string Relative, long Length, long LastWriteUtcTicks)[] ordered =
                entries
                    .OrderBy(
                        static item => item.Relative,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            foreach ((string _, string relative, long length, long ticks) in ordered)
            {
                totalBytes = checked(totalBytes + length);
                byte[] line = Encoding.UTF8.GetBytes(
                    $"{relative}\0{length}\0{ticks}\n");
                hash.AppendData(line);
            }
            string? contentSha256 = null;
            if (hashContents)
            {
                using IncrementalHash contentHash = IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
                foreach ((string fullPath, string relative, long length, _) in ordered)
                {
                    using var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 1024 * 1024,
                        FileOptions.SequentialScan);
                    if (!WindowsPathIdentity.TryGetFinalPath(
                            stream.SafeFileHandle,
                            out string finalPath)
                        || !string.Equals(
                            finalPath,
                            Path.GetFullPath(fullPath),
                            StringComparison.OrdinalIgnoreCase)
                        || stream.Length != length)
                    {
                        errorCode = "enhancement-output-identity-invalid";
                        error = "A managed Enhancement output changed or was redirected during setup.";
                        return false;
                    }

                    byte[] fileHash = SHA256.HashData(stream);
                    contentHash.AppendData(Encoding.UTF8.GetBytes(
                        $"{relative}\0{length}\0"));
                    contentHash.AppendData(fileHash);
                }
                contentSha256 = Convert.ToHexString(
                    contentHash.GetHashAndReset());
            }

            probe = new SharedDataRootOutputsProbe(
                true,
                entries.Count,
                totalBytes,
                Convert.ToHexString(hash.GetHashAndReset()),
                contentSha256);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or OverflowException)
        {
            errorCode = "enhancement-outputs-unreadable";
            error = $"The managed Enhancement output tree could not be inspected: {ex.Message}";
            return false;
        }
    }

    private static bool TryCreateLocatorAtomically(
        string locatorPath,
        byte[] payload,
        out string? errorCode,
        out string? error)
    {
        errorCode = null;
        error = null;
        string? parent = Path.GetDirectoryName(locatorPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            errorCode = "locator-parent-invalid";
            error = "The locator parent directory is invalid.";
            return false;
        }

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(parent);
            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    parent,
                    out string canonicalParent)
                || !string.Equals(
                    canonicalParent,
                    Path.GetFullPath(parent),
                    StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "locator-parent-identity-invalid";
                error = "The locator parent directory is redirected or invalid.";
                return false;
            }

            tempPath = Path.Combine(
                canonicalParent,
                $".{Path.GetFileName(locatorPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                if (!WindowsPathIdentity.TryGetFinalPath(
                        stream.SafeFileHandle,
                        out string finalTempPath)
                    || !string.Equals(
                        finalTempPath,
                        Path.GetFullPath(tempPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "locator-temp-identity-invalid";
                    error = "The locator temporary file identity is invalid.";
                    return false;
                }
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, locatorPath, overwrite: false);
            tempPath = null;
            return true;
        }
        catch (IOException)
        {
            errorCode = File.Exists(locatorPath)
                ? "locator-already-exists"
                : "locator-create-failed";
            error = File.Exists(locatorPath)
                ? "A locator appeared during setup and was not changed."
                : "The locator could not be created atomically.";
            return false;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            errorCode = "locator-create-failed";
            error = $"The locator could not be created: {ex.Message}";
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // The unreferenced sibling temporary file contains only
                    // the locator payload. It is never shared durable state.
                }
            }
        }
    }

    private static bool TryRollbackCreatedLocator(
        string locatorPath,
        byte[] expectedPayload)
    {
        try
        {
            byte[] current = File.ReadAllBytes(locatorPath);
            if (!current.AsSpan().SequenceEqual(expectedPayload))
                return false;
            File.Delete(locatorPath);
            return !File.Exists(locatorPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool StoreSnapshotsEqual(
        IReadOnlyList<SharedDataRootStoreProbe> first,
        IReadOnlyList<SharedDataRootStoreProbe> second)
        => first.Count == second.Count
            && first.Zip(second).All(static pair => Equals(pair.First, pair.Second));

    private static bool IsWithinRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Length > 0
            && relative != "."
            && !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF;

    private static bool HasUtf16OrUtf32Bom(byte[] bytes)
        => bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                || (bytes[0] == 0xFE && bytes[1] == 0xFF))
            || bytes.Length >= 4
            && ((bytes[0] == 0x00
                    && bytes[1] == 0x00
                    && bytes[2] == 0xFE
                    && bytes[3] == 0xFF)
                || (bytes[0] == 0xFF
                    && bytes[1] == 0xFE
                    && bytes[2] == 0x00
                    && bytes[3] == 0x00));

    private static SharedDataRootSetupResult Blocked(
        string locatorPath,
        string errorCode,
        string error,
        string? sharedDataRoot = null,
        IReadOnlyList<SharedDataRootStoreProbe>? stores = null,
        SharedDataRootOutputsProbe? outputs = null)
        => new(
            SharedDataRootSetupStatus.Blocked,
            locatorPath,
            sharedDataRoot,
            false,
            errorCode,
            error,
            stores ?? [],
            outputs);
}
