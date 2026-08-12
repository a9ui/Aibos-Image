using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhotoViewer.Wpf;

internal sealed record PhotorealFavoriteHistoryRestoreOptions(
    string FavoritesPath,
    string JobsSqlitePath,
    string PhotorealRoot,
    string BackupDirectory,
    string ReceiptPath,
    bool DryRun = false);

internal sealed record PhotorealFavoriteHistoryRestoreResult(
    bool Success,
    bool DryRun,
    int EntriesBefore,
    int EntriesAfter,
    int LegacyCandidates,
    int CurrentOutputCandidates,
    int Added,
    int AlreadyPresent,
    int Conflicts,
    int Ambiguous,
    int Unmatched,
    string BeforeSha256,
    string AfterSha256,
    string? BackupPath,
    string ReceiptPath,
    string? Error = null);

internal static class PhotorealFavoriteHistoryRestorer
{
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static int Run(IReadOnlyList<string> args)
    {
        string? favoritesPath = Option(args, "--favorites");
        string? jobsSqlitePath = Option(args, "--jobs-sqlite");
        string? photorealRoot = Option(args, "--photoreal-root");
        string? backupDirectory = Option(args, "--backup-dir");
        string? receiptPath = Option(args, "--receipt");
        if (string.IsNullOrWhiteSpace(favoritesPath)
            || string.IsNullOrWhiteSpace(jobsSqlitePath)
            || string.IsNullOrWhiteSpace(photorealRoot)
            || string.IsNullOrWhiteSpace(backupDirectory)
            || string.IsNullOrWhiteSpace(receiptPath))
        {
            return 2;
        }

        PhotorealFavoriteHistoryRestoreResult result = Restore(new(
            favoritesPath,
            jobsSqlitePath,
            photorealRoot,
            backupDirectory,
            receiptPath,
            args.Contains("--dry-run", StringComparer.Ordinal)));
        return result.Success ? 0 : 1;
    }

    internal static PhotorealFavoriteHistoryRestoreResult Restore(
        PhotorealFavoriteHistoryRestoreOptions options)
    {
        string favoritesPath = Path.GetFullPath(options.FavoritesPath);
        string jobsSqlitePath = Path.GetFullPath(options.JobsSqlitePath);
        string photorealRoot = Path.GetFullPath(options.PhotorealRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string backupDirectory = Path.GetFullPath(options.BackupDirectory);
        string receiptPath = Path.GetFullPath(options.ReceiptPath);
        string lockPath = favoritesPath + ".lock";
        string? temporaryPath = null;
        FileStream? lockStream = null;
        byte[] beforeBytes = [];
        string beforeSha256 = "";
        string afterSha256 = "";
        string? backupPath = null;
        int entriesBefore = 0;
        int entriesAfter = 0;
        int legacyCandidates = 0;
        int currentOutputCandidates = 0;
        int added = 0;
        int alreadyPresent = 0;
        int conflicts = 0;
        int ambiguous = 0;
        int unmatched = 0;

        PhotorealFavoriteHistoryRestoreResult Complete(
            bool success,
            string? error = null)
        {
            var result = new PhotorealFavoriteHistoryRestoreResult(
                success,
                options.DryRun,
                entriesBefore,
                entriesAfter,
                legacyCandidates,
                currentOutputCandidates,
                added,
                alreadyPresent,
                conflicts,
                ambiguous,
                unmatched,
                beforeSha256,
                afterSha256,
                backupPath,
                receiptPath,
                error);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                File.WriteAllText(
                    receiptPath,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        operation = "restore-photoreal-favorite-paths",
                        completedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        result.Success,
                        result.DryRun,
                        result.EntriesBefore,
                        result.EntriesAfter,
                        result.LegacyCandidates,
                        result.CurrentOutputCandidates,
                        result.Added,
                        result.AlreadyPresent,
                        result.Conflicts,
                        result.Ambiguous,
                        result.Unmatched,
                        result.BeforeSha256,
                        result.AfterSha256,
                        result.BackupPath,
                        result.Error,
                    }, ReceiptJsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch when (!success)
            {
                // The original failure remains authoritative when even the
                // best-effort failure receipt cannot be written.
            }
            return result;
        }

        try
        {
            if (!File.Exists(favoritesPath))
                return Complete(false, "favorites file does not exist");
            if (!File.Exists(jobsSqlitePath))
                return Complete(false, "jobs SQLite file does not exist");
            if (!Directory.Exists(photorealRoot))
                return Complete(false, "Photorealized root does not exist");
            if (IsPathWithin(photorealRoot, favoritesPath)
                || string.Equals(
                    Path.GetDirectoryName(favoritesPath),
                    photorealRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Complete(false, "favorites file must stay outside the Photorealized root");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(favoritesPath)!);
            try
            {
                lockStream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                byte[] lockPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    pid = Environment.ProcessId,
                    createdAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                    operation = "restore-photoreal-favorite-paths",
                }));
                lockStream.Write(lockPayload, 0, lockPayload.Length);
                lockStream.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                return Complete(false, "favorites file is busy in another Aibos writer");
            }

            beforeBytes = File.ReadAllBytes(favoritesPath);
            beforeSha256 = Convert.ToHexString(SHA256.HashData(beforeBytes))
                .ToLowerInvariant();
            var favorites = ReadFavoriteDocument(beforeBytes);
            entriesBefore = favorites.Count;

            Dictionary<string, List<string>> currentPathsByFileName =
                ReadCurrentPhotorealOutputPaths(jobsSqlitePath, photorealRoot);
            currentOutputCandidates = currentPathsByFileName.Values
                .SelectMany(static paths => paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var additions = new List<(string Path, int Level)>();
            foreach ((string legacyPath, JsonElement value) in favorites)
            {
                if (!TryReadFavoriteLevel(value, out int level)
                    || level <= 0
                    || !Path.IsPathFullyQualified(legacyPath))
                {
                    continue;
                }

                string normalizedLegacyPath;
                try
                {
                    normalizedLegacyPath = Path.GetFullPath(legacyPath);
                }
                catch
                {
                    continue;
                }
                if (!string.Equals(
                        Path.GetDirectoryName(normalizedLegacyPath)?.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        photorealRoot,
                        StringComparison.OrdinalIgnoreCase)
                    || File.Exists(normalizedLegacyPath))
                {
                    continue;
                }

                legacyCandidates++;
                string fileName = Path.GetFileName(normalizedLegacyPath);
                if (!currentPathsByFileName.TryGetValue(
                        fileName,
                        out List<string>? currentPaths)
                    || currentPaths.Count == 0)
                {
                    unmatched++;
                    continue;
                }
                if (currentPaths.Count != 1)
                {
                    ambiguous++;
                    continue;
                }

                string targetPath = currentPaths[0];
                if (favorites.TryGetValue(targetPath, out JsonElement currentValue))
                {
                    if (TryReadFavoriteLevel(currentValue, out int currentLevel)
                        && currentLevel == level)
                    {
                        alreadyPresent++;
                    }
                    else
                    {
                        conflicts++;
                    }
                    continue;
                }
                additions.Add((targetPath, level));
            }

            added = additions.Count;
            entriesAfter = checked(entriesBefore + added);
            if (options.DryRun)
            {
                afterSha256 = beforeSha256;
                return Complete(true);
            }

            Directory.CreateDirectory(backupDirectory);
            string backupName =
                $"favorites.before-photoreal-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{beforeSha256[..12]}.json";
            backupPath = Path.Combine(backupDirectory, backupName);
            if (File.Exists(backupPath))
            {
                backupPath = Path.Combine(
                    backupDirectory,
                    $"favorites.before-photoreal-restore-{Guid.NewGuid():N}-{beforeSha256[..12]}.json");
            }
            File.WriteAllBytes(backupPath, beforeBytes);

            foreach ((string path, int level) in additions)
                favorites[path] = JsonSerializer.SerializeToElement(level);
            string json = JsonSerializer.Serialize(
                favorites
                    .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        static item => item.Key,
                        static item => item.Value,
                        StringComparer.OrdinalIgnoreCase),
                new JsonSerializerOptions { WriteIndented = true });
            byte[] afterBytes = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            temporaryPath = Path.Combine(
                Path.GetDirectoryName(favoritesPath)!,
                $".{Path.GetFileName(favoritesPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporaryPath, afterBytes);
            File.Move(temporaryPath, favoritesPath, overwrite: true);
            temporaryPath = null;
            afterSha256 = Convert.ToHexString(SHA256.HashData(afterBytes))
                .ToLowerInvariant();
            return Complete(true);
        }
        catch (Exception error)
        {
            return Complete(false, error.Message);
        }
        finally
        {
            lockStream?.Dispose();
            if (lockStream is not null)
            {
                try { File.Delete(lockPath); } catch { }
            }
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static Dictionary<string, JsonElement> ReadFavoriteDocument(
        byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("favorites document is not an object");
        var result = new Dictionary<string, JsonElement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || !result.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new InvalidDataException(
                    "favorites document contains an empty or duplicate key");
            }
        }
        return result;
    }

    private static Dictionary<string, List<string>>
        ReadCurrentPhotorealOutputPaths(
            string jobsSqlitePath,
            string photorealRoot)
    {
        var result = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = jobsSqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM enhancement_jobs
            WHERE status = 'succeeded'
              AND operation = 'photoreal'
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            string payload = reader.GetString(0);
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(
                        "outputPath",
                        out JsonElement outputPathElement)
                    || outputPathElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(outputPathElement.GetString()))
                {
                    continue;
                }
                string outputPath = Path.GetFullPath(outputPathElement.GetString()!);
                if (!IsPathWithin(photorealRoot, outputPath)
                    || string.Equals(
                        Path.GetDirectoryName(outputPath)?.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        photorealRoot,
                        StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(outputPath))
                {
                    continue;
                }
                string fileName = Path.GetFileName(outputPath);
                if (!result.TryGetValue(fileName, out List<string>? paths))
                {
                    paths = [];
                    result[fileName] = paths;
                }
                if (!paths.Contains(outputPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(outputPath);
            }
            catch (JsonException)
            {
                // One malformed future row is never a reason to guess a path.
            }
        }
        return result;
    }

    private static bool TryReadFavoriteLevel(JsonElement value, out int level)
    {
        level = 0;
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double numeric)
            && double.IsFinite(numeric))
        {
            level = (int)Math.Clamp(Math.Truncate(numeric), 0, 5);
            return true;
        }
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            level = Math.Clamp(parsed, 0, 5);
            return true;
        }
        if (value.ValueKind == JsonValueKind.True)
        {
            level = 1;
            return true;
        }
        return value.ValueKind is JsonValueKind.False or JsonValueKind.Null;
    }

    private static bool IsPathWithin(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }
        return null;
    }
}
