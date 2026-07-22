using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PhotoViewer.Wpf;

internal sealed record SearchHistoryItemView(string Query);

internal sealed record SearchHistoryReadResult(
    bool Supported,
    IReadOnlyList<string> Entries,
    bool Malformed,
    bool FutureVersion,
    string? Error,
    IReadOnlyDictionary<string, JsonElement> Document);

internal enum SearchHistoryWriteStatus
{
    Succeeded,
    Busy,
    Protected,
    Failed,
}

internal sealed record SearchHistoryWriteResult(
    SearchHistoryWriteStatus Status,
    IReadOnlyList<string> Entries,
    bool Changed,
    string? Error = null);

internal static class SearchHistoryStore
{
    internal const int Version = 1;
    internal const int MaxEntries = 50;
    internal const int MaxQueryLength = 32_768;
    private const int DefaultLockTimeoutMilliseconds = 2_000;
    private const int LockRetryMilliseconds = 25;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    internal static string ResolvePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string root = MainWindow.ResolveSharedProjectRootForSmoke(start);
            if (File.Exists(Path.Combine(root, "project.toml")))
                return Path.Combine(root, ".cache", "search-history.json");
        }

        return Path.Combine(Environment.CurrentDirectory, ".cache", "search-history.json");
    }

    internal static string NormalizeQuery(string query)
        => string.Join(", ", query
            .Split(',', StringSplitOptions.None)
            .Select(TrimSearchHistoryToken)
            .Where(static token => token.Length > 0));

    private static string TrimSearchHistoryToken(string token)
    {
        int start = 0;
        int end = token.Length;
        while (start < end && IsSearchHistoryTrimCharacter(token[start]))
            start++;
        while (end > start && IsSearchHistoryTrimCharacter(token[end - 1]))
            end--;
        return token[start..end];
    }

    private static bool IsSearchHistoryTrimCharacter(char value)
        // Explicit Browser/.NET union of Unicode White_Space plus BOM. Keep in
        // sync with searchHistory.ts instead of relying on runtime trim tables.
        => value is >= '\u0009' and <= '\u000D'
            or '\u0020'
            or '\u0085'
            or '\u00A0'
            or '\u1680'
            or >= '\u2000' and <= '\u200A'
            or '\u2028'
            or '\u2029'
            or '\u202F'
            or '\u205F'
            or '\u3000'
            or '\uFEFF';

    internal static string ComparisonKey(string query)
    {
        // Browser and .NET runtime Unicode lowercase tables differ for expansion
        // cases such as U+0130. Use the same explicit fold as searchHistory.ts:
        // NFKC, per-code-point lowercase, and dotted capital I -> i + combining
        // dot. Per-code-point folding avoids contextual final-sigma behavior.
        string normalized = NormalizeQuery(query).Normalize(NormalizationForm.FormKC);
        var key = new StringBuilder(normalized.Length);
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (rune.Value == 0x130)
                key.Append("i\u0307");
            else
                key.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return key.ToString();
    }

    internal static SearchHistoryReadResult Read(string path)
    {
        if (!File.Exists(path))
            return Supported([], new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Malformed("search-history.json root must be an object");

            JsonElement root = document.RootElement;
            if (root.TryGetProperty("version", out JsonElement versionElement)
                && versionElement.ValueKind == JsonValueKind.Number
                && versionElement.TryGetInt32(out int futureVersion)
                && futureVersion > Version)
            {
                return new SearchHistoryReadResult(
                    false,
                    [],
                    false,
                    true,
                    $"search-history.json version {futureVersion} is newer than supported version {Version}",
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal));
            }

            if (!root.TryGetProperty("version", out versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out int version)
                || version != Version
                || !root.TryGetProperty("entries", out JsonElement entriesElement)
                || entriesElement.ValueKind != JsonValueKind.Array
                || (root.TryGetProperty("updatedAtUtc", out JsonElement updatedElement)
                    && updatedElement.ValueKind != JsonValueKind.String))
            {
                return Malformed("search-history.json does not match the supported version 1 schema");
            }

            var entries = new List<string>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement entryElement in entriesElement.EnumerateArray())
            {
                if (entryElement.ValueKind != JsonValueKind.String)
                    return Malformed("search-history.json entries must be strings");
                string raw = entryElement.GetString() ?? "";
                if (raw.Length > MaxQueryLength)
                    return Malformed("search-history.json contains an oversized entry");
                string entry = NormalizeQuery(raw);
                if (entry.Length > MaxQueryLength)
                    return Malformed("search-history.json contains an oversized normalized entry");
                if (entry.Length == 0)
                    continue;
                string key = ComparisonKey(entry);
                if (!keys.Add(key))
                    continue;
                entries.Add(entry);
                if (entries.Count >= MaxEntries)
                    break;
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
            return Supported(entries, properties);
        }
        catch (Exception ex)
        {
            return Malformed(ex.Message);
        }
    }

    internal static SearchHistoryWriteResult Commit(string path, string query, int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
        => Mutate(path, "commit", query, timeoutMilliseconds);

    internal static SearchHistoryWriteResult Delete(string path, string query, int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
        => Mutate(path, "delete", query, timeoutMilliseconds);

    internal static SearchHistoryWriteResult Clear(string path, int timeoutMilliseconds = DefaultLockTimeoutMilliseconds)
        => Mutate(path, "clear", null, timeoutMilliseconds);

    private static SearchHistoryWriteResult Mutate(
        string path,
        string action,
        string? rawQuery,
        int timeoutMilliseconds)
    {
        string fullPath = Path.GetFullPath(path);
        using PersistenceLease? lease = TryAcquireLock(fullPath, timeoutMilliseconds);
        if (lease is null)
            return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Busy, [], false, "shared search history is busy");

        SearchHistoryReadResult current = Read(fullPath);
        if (!current.Supported)
        {
            return new SearchHistoryWriteResult(
                SearchHistoryWriteStatus.Protected,
                [],
                false,
                "shared search history is malformed or from a newer version");
        }

        var entries = current.Entries.ToList();
        if (string.Equals(action, "clear", StringComparison.Ordinal))
        {
            entries.Clear();
        }
        else
        {
            string raw = rawQuery ?? "";
            if (raw.Length > MaxQueryLength)
                return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Failed, current.Entries, false, "query is empty or oversized");
            string query = NormalizeQuery(raw);
            if (query.Length == 0 || query.Length > MaxQueryLength)
                return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Failed, current.Entries, false, "query is empty or oversized");
            string key = ComparisonKey(query);
            entries.RemoveAll(entry => string.Equals(ComparisonKey(entry), key, StringComparison.Ordinal));
            if (string.Equals(action, "commit", StringComparison.Ordinal))
                entries.Insert(0, query);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        bool changed = entries.Count != current.Entries.Count
            || entries.Where((entry, index) => !string.Equals(entry, current.Entries[index], StringComparison.Ordinal)).Any();
        if (!changed && File.Exists(fullPath))
            return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Succeeded, entries, false);

        if (!TryWriteAtomic(fullPath, entries, current.Document))
            return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Failed, current.Entries, false, "atomic replace failed");
        return new SearchHistoryWriteResult(SearchHistoryWriteStatus.Succeeded, entries, changed);
    }

    private static bool TryWriteAtomic(
        string path,
        IReadOnlyList<string> entries,
        IReadOnlyDictionary<string, JsonElement> currentDocument)
    {
        string? tempPath = null;
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach ((string name, JsonElement value) in currentDocument)
                {
                    if (name is "version" or "entries" or "updatedAtUtc")
                        continue;
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
                writer.WriteNumber("version", Version);
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                foreach (string entry in entries)
                    writer.WriteStringValue(entry);
                writer.WriteEndArray();
                writer.WriteString("updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            File.WriteAllText(tempPath, Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(LockRetryMilliseconds);
                }
            }
            tempPath = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static PersistenceLease? TryAcquireLock(string targetPath, int timeoutMilliseconds)
    {
        string lockPath = targetPath + ".lock";
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    string payload = JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId,
                        createdAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    }, IndentedJson);
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                    CleanupAtomicResidue(targetPath);
                    return new PersistenceLease(lockPath, stream);
                }
                catch
                {
                    stream.Dispose();
                    try { File.Delete(lockPath); } catch { }
                    return null;
                }
            }
            catch (IOException)
            {
                // Never pathname-reap a legacy shared file lock. A stale
                // inspection cannot prove that lockPath still names the same
                // object, and Browser file handles permit a delayed WPF
                // deleter to remove a replacement live lock. Fail closed.
                if (wait.ElapsedMilliseconds >= timeoutMilliseconds)
                    return null;
                Thread.Sleep(LockRetryMilliseconds);
            }
            catch
            {
                return null;
            }
        }
    }

    private static void CleanupAtomicResidue(string targetPath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;
            string fileName = Path.GetFileName(targetPath);
            string browserPrefix = Path.GetFileNameWithoutExtension(targetPath) + "-";
            foreach (string candidate in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(candidate);
                if (!name.StartsWith($".{fileName}.", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(browserPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try { File.Delete(candidate); } catch { }
            }
        }
        catch
        {
            // Residue cleanup is best effort after exclusive lock ownership.
        }
    }

    private static SearchHistoryReadResult Supported(
        IReadOnlyList<string> entries,
        IReadOnlyDictionary<string, JsonElement> document)
        => new(true, entries, false, false, null, document);

    private static SearchHistoryReadResult Malformed(string error)
        => new(
            false,
            [],
            true,
            false,
            error,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    private sealed class PersistenceLease(string path, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try { File.Delete(path); } catch { }
        }
    }
}
