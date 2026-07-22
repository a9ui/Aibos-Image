using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

internal sealed record AlbumMemberEntry(string Id, string ImagePath, string AddedAtUtc);

internal sealed record AlbumEntry(
    string Id,
    string Name,
    bool Pinned,
    string? CoverMemberId,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    long Revision,
    IReadOnlyList<AlbumMemberEntry> Members);

internal sealed record AlbumDocumentSnapshot(
    int Version,
    long Revision,
    string UpdatedAtUtc,
    IReadOnlyList<AlbumEntry> Albums,
    IReadOnlyList<string> RecentAlbumIds);

internal sealed record AlbumReadResult(
    bool Supported,
    bool Exists,
    bool Malformed,
    bool FutureVersion,
    AlbumDocumentSnapshot? Document,
    string? Error);

internal enum AlbumMutationStatus
{
    Succeeded,
    Busy,
    Conflict,
    NotFound,
    Protected,
    Invalid,
    Failed,
}

internal sealed record AlbumMutationResult(
    AlbumMutationStatus Status,
    bool Changed,
    AlbumDocumentSnapshot? Document,
    AlbumEntry? Album,
    string? Error = null)
{
    public bool Ok => Status == AlbumMutationStatus.Succeeded;
}

internal static class AlbumStore
{
    internal const int Version = 1;
    internal const int MaxNameLength = 120;
    internal const int MaxMutationPaths = 10_000;
    internal const int MaxPathLength = 32_768;
    private const int DefaultLockTimeoutMilliseconds = 2_000;
    private const int LockRetryMilliseconds = 25;
    private const string DirectoryLockProtocol = "photoviewer.shared-directory-lock/v1";
    private const string DirectoryLockOwnerFileName = "owner.json";
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly Regex LockTokenPattern = new("^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LockClaimNamePattern = new(
        "^\\.claim\\.(?<owner>[0-9a-f]{32})\\.(?<pid>[1-9][0-9]*)\\.(?<ms>[0-9]+)\\.(?<claim>[0-9a-f]{32})\\.json$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static Action? ReleaseOwnerMissingHookForSmoke;

    internal static string ResolvePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ALBUMS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string root = MainWindow.ResolveSharedProjectRootForSmoke(start);
            if (File.Exists(Path.Combine(root, "project.toml")))
                return Path.Combine(root, ".cache", "albums.json");
        }
        return Path.Combine(Environment.CurrentDirectory, ".cache", "albums.json");
    }

    internal static bool HoldSharedLockForSmoke(
        string path,
        string readyPath,
        string releasePath,
        Action? releaseOwnerMissingHook = null,
        int timeoutMilliseconds = 2_000)
    {
        PersistenceLease? lease = TryAcquireLock(Path.GetFullPath(path), timeoutMilliseconds);
        if (lease is null)
            return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
            File.WriteAllText(readyPath, "ready");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(releasePath) && wait.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(10);
            ReleaseOwnerMissingHookForSmoke = releaseOwnerMissingHook;
            return File.Exists(releasePath);
        }
        finally
        {
            lease.Dispose();
            ReleaseOwnerMissingHookForSmoke = null;
        }
    }

    internal static AlbumReadResult Read(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new AlbumReadResult(true, false, false, false, EmptySnapshot(), null);
        try
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(fullPath));
            JsonNode? candidate = NormalizeLegacyEmptyDocument(parsed);
            ValidationResult validation = Validate(candidate);
            return validation.Supported
                ? new AlbumReadResult(true, true, false, false, validation.Snapshot, null)
                : new AlbumReadResult(false, true, !validation.FutureVersion, validation.FutureVersion, null, validation.Error);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new AlbumReadResult(false, true, true, false, null, ex.Message);
        }
    }

    internal static AlbumMutationResult Create(
        string path,
        string name,
        long? expectedRevision = null,
        string? albumId = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        string normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > MaxNameLength)
            return Invalid("name must contain 1-120 characters");
        string id = albumId ?? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        if (!IsBoundedId(id))
            return Invalid("Album id is invalid");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, snapshot, now) =>
        {
            if (snapshot.Albums.Any(album => string.Equals(album.Id, id, StringComparison.Ordinal)))
                return Mutation.Invalid("Album id already exists");
            var albumNode = new JsonObject
            {
                ["id"] = id,
                ["name"] = normalizedName,
                ["pinned"] = false,
                ["coverMemberId"] = null,
                ["createdAtUtc"] = now,
                ["updatedAtUtc"] = now,
                ["revision"] = 1,
                ["members"] = new JsonArray(),
            };
            root["albums"]!.AsArray().Add(albumNode);
            TouchRecent(root["recentAlbumIds"]!.AsArray(), id);
            return Mutation.WithChange(id);
        });
    }

    internal static AlbumMutationResult Update(
        string path,
        string albumId,
        long? expectedRevision = null,
        string? name = null,
        bool? pinned = null,
        string? coverMemberId = null,
        bool updateCover = false,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!IsBoundedId(albumId))
            return Invalid("Album id is invalid");
        string? normalizedName = name?.Trim();
        if (name is not null && (normalizedName!.Length is < 1 or > MaxNameLength))
            return Invalid("name must contain 1-120 characters");
        if (updateCover && coverMemberId is not null && !IsBoundedId(coverMemberId))
            return Invalid("cover member id is invalid");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, now) =>
        {
            JsonObject? album = FindAlbum(root, albumId);
            if (album is null)
                return Mutation.NotFound();
            if (updateCover && coverMemberId is not null
                && !album["members"]!.AsArray().OfType<JsonObject>().Any(member => StringValue(member, "id") == coverMemberId))
            {
                return Mutation.Invalid("cover must reference an Album member");
            }
            bool changed = false;
            if (normalizedName is not null && StringValue(album, "name") != normalizedName)
            {
                album["name"] = normalizedName;
                changed = true;
            }
            if (pinned.HasValue && BoolValue(album, "pinned") != pinned.Value)
            {
                album["pinned"] = pinned.Value;
                changed = true;
            }
            string? currentCover = NullableStringValue(album, "coverMemberId");
            if (updateCover && currentCover != coverMemberId)
            {
                album["coverMemberId"] = coverMemberId;
                changed = true;
            }
            if (changed)
                TouchAlbum(album, now);
            return changed ? Mutation.WithChange(albumId) : Mutation.Unchanged(albumId);
        });
    }

    internal static AlbumMutationResult AddMembers(
        string path,
        string albumId,
        IReadOnlyList<string> paths,
        long? expectedRevision = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!IsBoundedId(albumId) || !ValidPaths(paths))
            return Invalid("paths must be a non-empty bounded list of fully-qualified paths");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, now) =>
        {
            JsonObject? album = FindAlbum(root, albumId);
            if (album is null)
                return Mutation.NotFound();
            JsonArray members = album["members"]!.AsArray();
            var identities = members.OfType<JsonObject>()
                .Select(member => Path.GetFullPath(StringValue(member, "imagePath")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (string sourcePath in paths)
            {
                string fullPath = Path.GetFullPath(sourcePath);
                if (!identities.Add(fullPath))
                    continue;
                members.Add(new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    ["imagePath"] = fullPath,
                    ["addedAtUtc"] = now,
                });
                changed = true;
            }
            if (changed)
                TouchAlbum(album, now);
            return changed ? Mutation.WithChange(albumId) : Mutation.Unchanged(albumId);
        });
    }

    internal static AlbumMutationResult RemoveMembers(
        string path,
        string albumId,
        IReadOnlyList<string>? memberIds,
        IReadOnlyList<string>? paths,
        long? expectedRevision = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!IsBoundedId(albumId)
            || ((memberIds is null || memberIds.Count == 0) && (paths is null || paths.Count == 0))
            || (memberIds?.Any(id => !IsBoundedId(id)) ?? false)
            || (paths is not null && !ValidPaths(paths)))
        {
            return Invalid("remove requires bounded member ids or fully-qualified paths");
        }
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, now) =>
        {
            JsonObject? album = FindAlbum(root, albumId);
            if (album is null)
                return Mutation.NotFound();
            var ids = (memberIds ?? []).ToHashSet(StringComparer.Ordinal);
            var identities = (paths ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            JsonArray members = album["members"]!.AsArray();
            bool changed = false;
            for (int index = members.Count - 1; index >= 0; index--)
            {
                JsonObject member = members[index]!.AsObject();
                if (!ids.Contains(StringValue(member, "id"))
                    && !identities.Contains(Path.GetFullPath(StringValue(member, "imagePath"))))
                    continue;
                members.RemoveAt(index);
                changed = true;
            }
            if (changed)
            {
                string? cover = NullableStringValue(album, "coverMemberId");
                if (cover is not null && !members.OfType<JsonObject>().Any(member => StringValue(member, "id") == cover))
                    album["coverMemberId"] = null;
                TouchAlbum(album, now);
            }
            return changed ? Mutation.WithChange(albumId) : Mutation.Unchanged(albumId);
        });
    }

    internal static AlbumMutationResult Delete(
        string path,
        string albumId,
        long? expectedRevision = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!IsBoundedId(albumId))
            return Invalid("Album id is invalid");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, _) =>
        {
            JsonArray albums = root["albums"]!.AsArray();
            int index = albums.Select((node, itemIndex) => (node, itemIndex))
                .FirstOrDefault(pair => pair.node is JsonObject album && StringValue(album, "id") == albumId, (null, -1)).itemIndex;
            if (index < 0)
                return Mutation.NotFound();
            albums.RemoveAt(index);
            JsonArray recent = root["recentAlbumIds"]!.AsArray();
            for (int recentIndex = recent.Count - 1; recentIndex >= 0; recentIndex--)
            {
                if (recent[recentIndex]?.GetValue<string>() == albumId)
                    recent.RemoveAt(recentIndex);
            }
            return Mutation.WithChange(null);
        });
    }

    internal static AlbumMutationResult MarkRecent(
        string path,
        string albumId,
        long? expectedRevision = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!IsBoundedId(albumId))
            return Invalid("Album id is invalid");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, _) =>
        {
            if (FindAlbum(root, albumId) is null)
                return Mutation.NotFound();
            JsonArray recent = root["recentAlbumIds"]!.AsArray();
            bool changed = recent.Count == 0 || recent[0]?.GetValue<string>() != albumId;
            TouchRecent(recent, albumId);
            return changed ? Mutation.WithChange(albumId) : Mutation.Unchanged(albumId);
        });
    }

    internal static AlbumMutationResult CleanupPaths(
        string path,
        IReadOnlyList<string> paths,
        long? expectedRevision = null,
        int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
    {
        if (!ValidPaths(paths))
            return Invalid("paths must be a non-empty bounded list of fully-qualified paths");
        return Mutate(path, expectedRevision, timeoutMilliseconds, (root, _, now) =>
        {
            var identities = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (JsonObject album in root["albums"]!.AsArray().OfType<JsonObject>())
            {
                JsonArray members = album["members"]!.AsArray();
                bool albumChanged = false;
                for (int index = members.Count - 1; index >= 0; index--)
                {
                    JsonObject member = members[index]!.AsObject();
                    if (!identities.Contains(Path.GetFullPath(StringValue(member, "imagePath"))))
                        continue;
                    members.RemoveAt(index);
                    albumChanged = true;
                }
                if (!albumChanged)
                    continue;
                string? cover = NullableStringValue(album, "coverMemberId");
                if (cover is not null && !members.OfType<JsonObject>().Any(member => StringValue(member, "id") == cover))
                    album["coverMemberId"] = null;
                TouchAlbum(album, now);
                changed = true;
            }
            return changed ? Mutation.WithChange(null) : Mutation.Unchanged(null);
        });
    }

    private static AlbumMutationResult Mutate(
        string path,
        long? expectedRevision,
        int timeoutMilliseconds,
        Func<JsonObject, AlbumDocumentSnapshot, string, Mutation> mutation)
    {
        if (expectedRevision is < 0)
            return Invalid("expected revision must be non-negative");
        string fullPath = Path.GetFullPath(path);
        using PersistenceLease? lease = TryAcquireLock(fullPath, timeoutMilliseconds);
        if (lease is null)
            return new AlbumMutationResult(AlbumMutationStatus.Busy, false, null, null, "shared Album store is busy");
        try
        {
            JsonObject root;
            AlbumDocumentSnapshot snapshot;
            if (!File.Exists(fullPath))
            {
                root = EmptyNode();
                snapshot = EmptySnapshot();
            }
            else
            {
                JsonNode? parsed = JsonNode.Parse(File.ReadAllText(fullPath));
                JsonNode? candidate = NormalizeLegacyEmptyDocument(parsed);
                ValidationResult validation = Validate(candidate);
                if (!validation.Supported)
                    return new AlbumMutationResult(AlbumMutationStatus.Protected, false, null, null, validation.Error);
                root = candidate!.AsObject();
                snapshot = validation.Snapshot!;
            }
            if (expectedRevision.HasValue && expectedRevision.Value != snapshot.Revision)
                return new AlbumMutationResult(AlbumMutationStatus.Conflict, false, snapshot, null, $"expected revision {expectedRevision}, current {snapshot.Revision}");

            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Mutation applied = mutation(root, snapshot, now);
            if (applied.Status != AlbumMutationStatus.Succeeded)
                return new AlbumMutationResult(applied.Status, false, snapshot, null, applied.Error);
            if (!applied.Changed)
                return new AlbumMutationResult(AlbumMutationStatus.Succeeded, false, snapshot, applied.AlbumId is null ? null : snapshot.Albums.FirstOrDefault(album => album.Id == applied.AlbumId));

            root["version"] = Version;
            root["revision"] = snapshot.Revision + 1;
            root["updatedAtUtc"] = now;
            ValidationResult updated = Validate(root);
            if (!updated.Supported)
                return new AlbumMutationResult(AlbumMutationStatus.Failed, false, snapshot, null, updated.Error);
            Publish(fullPath, root);
            AlbumEntry? album = applied.AlbumId is null ? null : updated.Snapshot!.Albums.FirstOrDefault(candidate => candidate.Id == applied.AlbumId);
            return new AlbumMutationResult(AlbumMutationStatus.Succeeded, true, updated.Snapshot, album);
        }
        catch (JsonException ex)
        {
            return new AlbumMutationResult(AlbumMutationStatus.Protected, false, null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new AlbumMutationResult(AlbumMutationStatus.Failed, false, null, null, ex.Message);
        }
    }

    private static ValidationResult Validate(JsonNode? parsed)
    {
        if (parsed is not JsonObject root)
            return ValidationResult.Invalid("albums.json root must be an object");
        if (TryLong(root["version"], out long futureVersion) && futureVersion > Version)
            return ValidationResult.Future($"albums.json version {futureVersion} is newer than supported version {Version}");
        if (!TryLong(root["version"], out long version) || version != Version
            || !TryLong(root["revision"], out long revision) || revision < 0
            || !TryUtc(root["updatedAtUtc"], out string updatedAtUtc)
            || root["albums"] is not JsonArray albumNodes
            || root["recentAlbumIds"] is not JsonArray recentNodes)
        {
            return ValidationResult.Invalid("albums.json does not match the supported version 1 root schema");
        }

        var albumIds = new HashSet<string>(StringComparer.Ordinal);
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var albums = new List<AlbumEntry>(albumNodes.Count);
        foreach (JsonNode? albumNode in albumNodes)
        {
            if (albumNode is not JsonObject album
                || !TryId(album["id"], out string id) || !albumIds.Add(id)
                || !TryString(album["name"], out string name) || (name = name.Trim()).Length is < 1 or > MaxNameLength
                || !TryBool(album["pinned"], out bool pinned)
                || !album.ContainsKey("coverMemberId")
                || !TryNullableId(album["coverMemberId"], out string? coverMemberId)
                || !TryUtc(album["createdAtUtc"], out string createdAtUtc)
                || !TryUtc(album["updatedAtUtc"], out string albumUpdatedAtUtc)
                || !TryLong(album["revision"], out long albumRevision) || albumRevision < 0
                || album["members"] is not JsonArray memberNodes)
            {
                return ValidationResult.Invalid("albums.json contains an invalid or duplicate Album");
            }
            var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = new List<AlbumMemberEntry>(memberNodes.Count);
            foreach (JsonNode? memberNode in memberNodes)
            {
                if (memberNode is not JsonObject member
                    || !TryId(member["id"], out string memberId) || !memberIds.Add(memberId)
                    || !TryString(member["imagePath"], out string imagePath)
                    || imagePath.Length is < 1 or > MaxPathLength
                    || !Path.IsPathFullyQualified(imagePath)
                    || !IsSupportedImagePath(imagePath)
                    || !canonicalPaths.Add(Path.GetFullPath(imagePath))
                    || !TryUtc(member["addedAtUtc"], out string addedAtUtc))
                {
                    return ValidationResult.Invalid("albums.json contains an invalid or duplicate member");
                }
                members.Add(new AlbumMemberEntry(memberId, Path.GetFullPath(imagePath), addedAtUtc));
            }
            if (coverMemberId is not null && members.All(member => member.Id != coverMemberId))
                return ValidationResult.Invalid("albums.json contains a cover that is not an Album member");
            albums.Add(new AlbumEntry(id, name, pinned, coverMemberId, createdAtUtc, albumUpdatedAtUtc, albumRevision, members));
        }

        var recent = new List<string>(recentNodes.Count);
        var recentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? recentNode in recentNodes)
        {
            if (!TryId(recentNode, out string recentId) || !albumIds.Contains(recentId) || !recentIds.Add(recentId))
                return ValidationResult.Invalid("albums.json contains invalid recent Album ids");
            recent.Add(recentId);
        }
        return ValidationResult.Valid(new AlbumDocumentSnapshot(Version, revision, updatedAtUtc, albums, recent));
    }

    private static JsonNode? NormalizeLegacyEmptyDocument(JsonNode? parsed)
    {
        if (parsed is not JsonObject root || root.ContainsKey("version"))
            return parsed;
        bool hasVersionOneOnlyFields = root.ContainsKey("revision")
            || root.ContainsKey("updatedAtUtc")
            || root.ContainsKey("recentAlbumIds");
        if (hasVersionOneOnlyFields || root["albums"] is not JsonArray albums || albums.Count != 0)
            return parsed;

        root["version"] = Version;
        root["revision"] = 0;
        root["updatedAtUtc"] = DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture);
        root["recentAlbumIds"] = new JsonArray();
        return root;
    }

    private static void Publish(string targetPath, JsonObject document)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(document.ToJsonString(IndentedJson) + Environment.NewLine);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, targetPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(LockRetryMilliseconds);
                }
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static PersistenceLease? TryAcquireLock(string targetPath, int timeoutMilliseconds)
    {
        string lockPath = targetPath + ".lock";
        var wait = Stopwatch.StartNew();
        while (true)
        {
            string ownerToken = Guid.NewGuid().ToString("N");
            string candidatePath = lockPath + ".candidate." + ownerToken;
            bool acquired = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                Directory.CreateDirectory(candidatePath);
                string ownerPath = Path.Combine(candidatePath, DirectoryLockOwnerFileName);
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    protocol = DirectoryLockProtocol,
                    token = ownerToken,
                    pid = Environment.ProcessId,
                    createdAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                }) + Environment.NewLine);
                using (var stream = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                Directory.Move(candidatePath, lockPath);
                acquired = true;
            }
            catch (IOException)
            {
                TryDeleteDirectory(candidatePath);
                if (RecoverStaleDirectoryLock(lockPath))
                    continue;
                if (wait.ElapsedMilliseconds >= timeoutMilliseconds)
                    return null;
                Thread.Sleep(LockRetryMilliseconds);
            }
            catch
            {
                TryDeleteDirectory(candidatePath);
                return null;
            }

            if (!acquired)
                continue;
            CleanupAtomicResidue(targetPath);
            CleanupRetiredDirectoryLocks(lockPath);
            return new PersistenceLease(lockPath, ownerToken);
        }
    }

    private static bool RecoverStaleDirectoryLock(string lockPath)
    {
        try
        {
            // Legacy file locks and unexpected objects are never pathname-
            // deleted. They fail closed until an operator removes them.
            if (File.Exists(lockPath))
                return false;
            if (!Directory.Exists(lockPath))
                return true;
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) <= LockStaleAfter)
                return false;

            string ownerPath = Path.Combine(lockPath, DirectoryLockOwnerFileName);
            if (!File.Exists(ownerPath))
                return RecoverAbandonedClaimOrEmptyDirectory(lockPath);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(ownerPath) <= LockStaleAfter)
                return false;
            DirectoryLockOwner? owner = ReadDirectoryLockOwner(ownerPath);
            if (owner is null || HasLiveProcess(owner.Pid))
                return false;
            return ClaimOwnerAndRetire(lockPath, owner.Token);
        }
        catch { return false; }
    }

    private static bool RecoverAbandonedClaimOrEmptyDirectory(string lockPath)
    {
        try
        {
            string[] entries = Directory.GetFileSystemEntries(lockPath, "*", SearchOption.TopDirectoryOnly);
            if (entries.Length == 0)
            {
                Directory.Delete(lockPath, recursive: false);
                return true;
            }
            if (entries.Length != 1 || Directory.Exists(entries[0]))
                return false;
            Match match = LockClaimNamePattern.Match(Path.GetFileName(entries[0]));
            if (!match.Success
                || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int claimantPid)
                || !long.TryParse(match.Groups["ms"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long claimedAt)
                || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - claimedAt <= LockStaleAfter.TotalMilliseconds
                || HasLiveProcess(claimantPid))
            {
                return false;
            }

            DirectoryLockOwner? owner = ReadDirectoryLockOwner(entries[0]);
            // A crashed delayed reaper may have claimed a replacement live
            // owner's marker before detecting the token mismatch.
            if (owner is null || HasLiveProcess(owner.Pid))
                return false;
            string claimToken = Guid.NewGuid().ToString("N");
            string newClaimPath = Path.Combine(lockPath, DirectoryClaimName(owner.Token, claimToken));
            File.Move(entries[0], newClaimPath);
            return RetireClaimedDirectory(lockPath, newClaimPath, owner.Token, claimToken);
        }
        catch { return false; }
    }

    private static bool HasLiveProcess(int pid)
    {
        try
        {
            using Process owner = Process.GetProcessById(pid);
            return !owner.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Access-denied and platform-specific inspection failures fail
            // closed so recovery cannot create two owners.
            return true;
        }
    }

    private static DirectoryLockOwner? ReadDirectoryLockOwner(string ownerPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ownerPath));
            if (!document.RootElement.TryGetProperty("protocol", out JsonElement protocolElement)
                || !string.Equals(protocolElement.GetString(), DirectoryLockProtocol, StringComparison.Ordinal)
                || !document.RootElement.TryGetProperty("token", out JsonElement tokenElement)
                || tokenElement.GetString() is not string token
                || !LockTokenPattern.IsMatch(token)
                || !document.RootElement.TryGetProperty("pid", out JsonElement pidElement)
                || !pidElement.TryGetInt32(out int pid)
                || pid <= 0
                || !document.RootElement.TryGetProperty("createdAtUtc", out JsonElement createdElement)
                || createdElement.GetString() is not string createdAtUtc
                || !DateTimeOffset.TryParse(createdAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                return null;
            }
            return new DirectoryLockOwner(token, pid, createdAtUtc);
        }
        catch { return null; }
    }

    private static string DirectoryClaimName(string ownerToken, string claimToken)
        => $".claim.{ownerToken}.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{claimToken}.json";

    private static bool ClaimOwnerAndRetire(string lockPath, string expectedOwnerToken)
    {
        string ownerPath = Path.Combine(lockPath, DirectoryLockOwnerFileName);
        string claimToken = Guid.NewGuid().ToString("N");
        string claimPath = Path.Combine(lockPath, DirectoryClaimName(expectedOwnerToken, claimToken));
        try
        {
            // Moving the marker inside the non-empty directory atomically
            // claims this exact generation; a replacement cannot occupy the
            // generic lock pathname until the directory itself is retired.
            File.Move(ownerPath, claimPath);
        }
        catch
        {
            return !File.Exists(lockPath) && !Directory.Exists(lockPath);
        }

        DirectoryLockOwner? claimedOwner = ReadDirectoryLockOwner(claimPath);
        if (claimedOwner is null || !string.Equals(claimedOwner.Token, expectedOwnerToken, StringComparison.OrdinalIgnoreCase))
        {
            RestoreClaimToOwner(claimPath, ownerPath);
            return false;
        }
        return RetireClaimedDirectory(lockPath, claimPath, expectedOwnerToken, claimToken);
    }

    private static bool ReleaseOwnedDirectoryLock(string lockPath, string ownerToken)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (!File.Exists(lockPath) && !Directory.Exists(lockPath))
                return true;
            DirectoryLockOwner? owner = ReadDirectoryLockOwner(Path.Combine(lockPath, DirectoryLockOwnerFileName));
            if (owner is not null)
            {
                if (!string.Equals(owner.Token, ownerToken, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (ClaimOwnerAndRetire(lockPath, ownerToken))
                    return true;
            }
            else
            {
                // A delayed stale reaper can temporarily hold this live
                // generation's marker under its inspected token. Wait for the
                // exact marker to be restored instead of stranding the lock
                // after this owner's critical section has ended.
                ReleaseOwnerMissingHookForSmoke?.Invoke();
                string? matchingClaim = null;
                try
                {
                    matchingClaim = Directory.EnumerateFiles(lockPath, ".claim.*.json", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(candidate => ReadDirectoryLockOwner(candidate) is DirectoryLockOwner claimOwner
                            && string.Equals(claimOwner.Token, ownerToken, StringComparison.OrdinalIgnoreCase));
                }
                catch { }
                if (matchingClaim is not null)
                {
                    string claimToken = Guid.NewGuid().ToString("N");
                    string releaseClaimPath = Path.Combine(lockPath, DirectoryClaimName(ownerToken, claimToken));
                    try
                    {
                        // This process owns the payload and has finished its
                        // critical section, so it can atomically take its marker
                        // back from a delayed or crashed reaper.
                        File.Move(matchingClaim, releaseClaimPath);
                        if (RetireClaimedDirectory(lockPath, releaseClaimPath, ownerToken, claimToken))
                            return true;
                    }
                    catch
                    {
                        // The reaper may have restored owner.json. Retry normally.
                    }
                }
            }

            if (wait.ElapsedMilliseconds >= DefaultLockTimeoutMilliseconds)
                return false;
            Thread.Sleep(LockRetryMilliseconds);
        }
    }

    private static bool RetireClaimedDirectory(
        string lockPath,
        string claimPath,
        string ownerToken,
        string claimToken)
    {
        string retiredPath = $"{lockPath}.retired.{ownerToken}.{claimToken}";
        try
        {
            Directory.Move(lockPath, retiredPath);
        }
        catch
        {
            RestoreClaimToOwner(claimPath, Path.Combine(lockPath, DirectoryLockOwnerFileName));
            return false;
        }

        TryDeleteDirectory(retiredPath);
        return true;
    }

    private static void RestoreClaimToOwner(string claimPath, string ownerPath)
    {
        try { File.Move(claimPath, ownerPath); } catch { }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, recursive: true);
        }
        catch { }
    }

    private static void CleanupRetiredDirectoryLocks(string lockPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(lockPath)!;
            string prefix = Path.GetFileName(lockPath) + ".retired.";
            foreach (string candidate in Directory.EnumerateDirectories(directory, prefix + "*", SearchOption.TopDirectoryOnly))
                TryDeleteDirectory(candidate);
        }
        catch { }
    }

    private static void CleanupAtomicResidue(string targetPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(targetPath)!;
            string fileName = Path.GetFileName(targetPath);
            string browserPrefix = Path.GetFileNameWithoutExtension(targetPath) + "-";
            foreach (string candidate in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(candidate);
                if (name.StartsWith($".{fileName}.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(browserPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(candidate); } catch { }
                }
            }
        }
        catch { }
    }

    private static JsonObject EmptyNode() => new()
    {
        ["version"] = Version,
        ["revision"] = 0,
        ["updatedAtUtc"] = DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture),
        ["albums"] = new JsonArray(),
        ["recentAlbumIds"] = new JsonArray(),
    };

    private static AlbumDocumentSnapshot EmptySnapshot()
        => new(Version, 0, DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture), [], []);

    private static JsonObject? FindAlbum(JsonObject root, string albumId)
        => root["albums"]!.AsArray().OfType<JsonObject>().FirstOrDefault(album => StringValue(album, "id") == albumId);

    private static void TouchAlbum(JsonObject album, string now)
    {
        album["revision"] = LongValue(album, "revision") + 1;
        album["updatedAtUtc"] = now;
    }

    private static void TouchRecent(JsonArray recent, string albumId)
    {
        for (int index = recent.Count - 1; index >= 0; index--)
        {
            if (recent[index]?.GetValue<string>() == albumId)
                recent.RemoveAt(index);
        }
        recent.Insert(0, albumId);
        while (recent.Count > 30)
            recent.RemoveAt(recent.Count - 1);
    }

    private static bool ValidPaths(IReadOnlyList<string> paths)
        => paths.Count is > 0 and <= MaxMutationPaths
            && paths.All(path => !string.IsNullOrWhiteSpace(path)
                && path.Length <= MaxPathLength
                && Path.IsPathFullyQualified(path)
                && IsSupportedImagePath(path));

    private static bool IsSupportedImagePath(string imagePath)
        => Path.GetExtension(imagePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".gif";

    private static bool IsBoundedId(string value) => value.Length is > 0 and <= 256;
    private static bool TryId(JsonNode? node, out string value) => TryString(node, out value) && IsBoundedId(value);
    private static bool TryNullableId(JsonNode? node, out string? value)
    {
        if (node is null) { value = null; return true; }
        if (TryId(node, out string id)) { value = id; return true; }
        value = null;
        return false;
    }
    private static bool TryString(JsonNode? node, out string value)
    {
        try { value = node?.GetValue<string>() ?? ""; return node is not null; }
        catch { value = ""; return false; }
    }
    private static bool TryBool(JsonNode? node, out bool value)
    {
        try { value = node?.GetValue<bool>() ?? false; return node is not null; }
        catch { value = false; return false; }
    }
    private static bool TryLong(JsonNode? node, out long value)
    {
        if (node is null)
        {
            value = 0;
            return false;
        }
        try
        {
            value = node.GetValue<long>();
            return true;
        }
        catch
        {
            try
            {
                value = node.GetValue<int>();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
        }
    }
    private static bool TryUtc(JsonNode? node, out string value)
        => TryString(node, out value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    private static string StringValue(JsonObject node, string name) => node[name]!.GetValue<string>();
    private static string? NullableStringValue(JsonObject node, string name) => node[name]?.GetValue<string>();
    private static bool BoolValue(JsonObject node, string name) => node[name]!.GetValue<bool>();
    private static long LongValue(JsonObject node, string name) => node[name]!.GetValue<long>();
    private static AlbumMutationResult Invalid(string error) => new(AlbumMutationStatus.Invalid, false, null, null, error);

    private sealed record ValidationResult(bool Supported, bool FutureVersion, AlbumDocumentSnapshot? Snapshot, string? Error)
    {
        public static ValidationResult Valid(AlbumDocumentSnapshot snapshot) => new(true, false, snapshot, null);
        public static ValidationResult Invalid(string error) => new(false, false, null, error);
        public static ValidationResult Future(string error) => new(false, true, null, error);
    }

    private sealed record Mutation(AlbumMutationStatus Status, bool Changed, string? AlbumId, string? Error)
    {
        public static Mutation WithChange(string? albumId) => new(AlbumMutationStatus.Succeeded, true, albumId, null);
        public static Mutation Unchanged(string? albumId) => new(AlbumMutationStatus.Succeeded, false, albumId, null);
        public static Mutation NotFound() => new(AlbumMutationStatus.NotFound, false, null, "Album not found");
        public static Mutation Invalid(string error) => new(AlbumMutationStatus.Invalid, false, null, error);
    }

    private sealed record DirectoryLockOwner(string Token, int Pid, string CreatedAtUtc);

    private sealed class PersistenceLease(string path, string ownerToken) : IDisposable
    {
        public void Dispose()
        {
            ReleaseOwnedDirectoryLock(path, ownerToken);
        }
    }
}
