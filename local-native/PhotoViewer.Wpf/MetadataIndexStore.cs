using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhotoViewer.Wpf;

/// <summary>
/// WPF-owned derived cache for catalog dimensions and searchable prompt text.
/// It intentionally does not share ownership with historical cross-app state,
/// viewer settings, or enhancement jobs.
/// </summary>
internal static class MetadataIndexStore
{
    private const int SchemaVersion = 1;
    private const int LegacyMagic = 0x494D5650; // "PVMI" in little-endian byte order.
    private const int LegacyVersion = 1;
    private const int MaximumEntryCount = 1_000_000;
    private const int MaximumPathBytes = 128 * 1024;
    private const int MaximumPromptBytes = 4 * 1024 * 1024;
    private const long MaximumLegacyIndexBytes = 1024L * 1024 * 1024;
    private const long MaximumSqliteFamilyBytes = MaximumLegacyIndexBytes;
    private const long MaximumSqliteAggregateBytes = MaximumLegacyIndexBytes;
    private const int LegacyPayloadHashBytes = 32;
    private const int LegacyHeaderBytes = sizeof(int) * 3 + sizeof(long) + LegacyPayloadHashBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SqliteSidecarSuffixes = ["-wal", "-shm", "-journal"];

    private const string CreateSchemaSql = """
        CREATE TABLE store_meta (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            schema_version INTEGER NOT NULL,
            entry_revision INTEGER NOT NULL CHECK (entry_revision >= 0)
        ) WITHOUT ROWID;
        INSERT INTO store_meta(singleton, schema_version, entry_revision)
        VALUES (1, 1, 0);
        CREATE TABLE metadata (
            path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            source_length INTEGER NOT NULL CHECK (source_length >= 0),
            source_mtime_ticks INTEGER NOT NULL,
            source_ctime_ticks INTEGER NOT NULL,
            width INTEGER NOT NULL CHECK (width > 0),
            height INTEGER NOT NULL CHECK (height > 0),
            prompt_utf8 BLOB NOT NULL
        ) WITHOUT ROWID;
        """;

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
        return Path.Combine(directory, $"{digest}.sqlite3");
    }

    public static MetadataIndexLoadResult Load(string path, CancellationToken token)
        => LoadCore(path, token, MaximumSqliteFamilyBytes, MaximumSqliteAggregateBytes);

    internal static MetadataIndexLoadResult LoadWithBudgetForSmoke(
        string path,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
        => LoadCore(path, token, maximumFamilyBytes, maximumAggregateBytes);

    private static MetadataIndexLoadResult LoadCore(
        string path,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
    {
        string fullPath = Path.GetFullPath(path);
        ValidateBudgets(maximumFamilyBytes, maximumAggregateBytes);
        if (!File.Exists(fullPath))
        {
            string? orphanSidecar = FindExistingSqliteSidecar(fullPath);
            if (orphanSidecar is not null)
            {
                return MetadataIndexLoadResult.Invalid(
                    fullPath,
                    $"metadata cache main file was absent while orphan sidecar {Path.GetFileName(orphanSidecar)} was preserved");
            }

            string legacyPath = Path.ChangeExtension(fullPath, ".pvmi");
            if (!File.Exists(legacyPath))
                return MetadataIndexLoadResult.Missing(fullPath);

            MetadataIndexLoadResult legacy = LoadLegacyBinary(legacyPath, token);
            return legacy.State switch
            {
                MetadataIndexLoadState.Loaded => MetadataIndexLoadResult.Loaded(
                    fullPath,
                    legacy.Entries,
                    requiresMigration: true,
                    sourcePath: legacyPath),
                MetadataIndexLoadState.Unsupported => MetadataIndexLoadResult.Unsupported(
                    fullPath,
                    legacy.Error ?? "legacy metadata index version was unsupported",
                    sourcePath: legacyPath),
                _ => MetadataIndexLoadResult.Invalid(
                    fullPath,
                    legacy.Error ?? "legacy metadata index was invalid",
                    sourcePath: legacyPath),
            };
        }

        try
        {
            token.ThrowIfCancellationRequested();
            EnsureSqliteFamilyWithinBudget(fullPath, maximumFamilyBytes);
            using SqliteConnection connection = OpenConnection(fullPath, SqliteOpenMode.ReadOnly);
            ExecuteNonQuery(connection, "PRAGMA query_only = ON;");
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            int schemaVersion = ReadSchemaVersion(connection, transaction);
            if (schemaVersion > SchemaVersion)
            {
                return MetadataIndexLoadResult.Unsupported(
                    fullPath,
                    $"metadata cache schema version {schemaVersion} is unsupported");
            }
            if (schemaVersion != SchemaVersion)
            {
                return MetadataIndexLoadResult.Invalid(
                    fullPath,
                    $"metadata cache schema version {schemaVersion} was invalid");
            }

            int count = ValidateStoredPayloadBudget(connection, transaction, maximumAggregateBytes);

            var entries = new Dictionary<string, MetadataIndexEntry>(count, StringComparer.OrdinalIgnoreCase);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT path,
                       source_length,
                       source_mtime_ticks,
                       source_ctime_ticks,
                       width,
                       height,
                       prompt_utf8
                FROM metadata
                ORDER BY path COLLATE NOCASE;
                """;
            int index = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if ((index & 255) == 0)
                        token.ThrowIfCancellationRequested();
                    string sourcePath = reader.GetString(0);
                    long sourceLength = reader.GetInt64(1);
                    long sourceLastWriteUtcTicks = reader.GetInt64(2);
                    long sourceCreationUtcTicks = reader.GetInt64(3);
                    int width = reader.GetInt32(4);
                    int height = reader.GetInt32(5);
                    byte[] promptUtf8 = reader.GetFieldValue<byte[]>(6);
                    ValidateEntry(
                        sourcePath,
                        sourceLength,
                        sourceLastWriteUtcTicks,
                        sourceCreationUtcTicks,
                        width,
                        height,
                        promptUtf8,
                        index);
                    if (!entries.TryAdd(
                        sourcePath,
                        new MetadataIndexEntry(
                            sourcePath,
                            sourceLength,
                            sourceLastWriteUtcTicks,
                            sourceCreationUtcTicks,
                            width,
                            height,
                            promptUtf8)))
                    {
                        return MetadataIndexLoadResult.Invalid(
                            fullPath,
                            $"metadata cache entry {index} duplicated path {sourcePath}");
                    }
                    index++;
                }
            }

            if (index != count)
                return MetadataIndexLoadResult.Invalid(fullPath, "metadata cache row count changed during the read");
            transaction.Commit();
            return MetadataIndexLoadResult.Loaded(fullPath, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableStoreException(ex))
        {
            return MetadataIndexLoadResult.Invalid(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static MetadataIndexSaveResult Save(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> entries,
        CancellationToken token)
        => SaveCore(path, entries, token, MaximumSqliteFamilyBytes, MaximumSqliteAggregateBytes);

    internal static MetadataIndexSaveResult SaveWithBudgetForSmoke(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> entries,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
        => SaveCore(path, entries, token, maximumFamilyBytes, maximumAggregateBytes);

    private static MetadataIndexSaveResult SaveCore(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> entries,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
    {
        string fullPath = Path.GetFullPath(path);
        ValidateBudgets(maximumFamilyBytes, maximumAggregateBytes);
        if (entries.Count > MaximumEntryCount)
            return MetadataIndexSaveResult.Failed(fullPath, $"entry count {entries.Count} exceeds the safe bound");

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return MetadataIndexSaveResult.Failed(fullPath, "metadata cache directory was unavailable");

        string temporaryToken = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))
            .ToLowerInvariant()[..12];
        string temporaryPath = Path.Combine(
            directory,
            $".mi-{temporaryToken}-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        string lockPath = Path.Combine(directory, $".mi-{temporaryToken}.lock");
        FileStream? lockStream = null;
        try
        {
            Directory.CreateDirectory(directory);
            lockStream = AcquireWriterLock(lockPath, token);
            if (File.Exists(fullPath))
            {
                EnsureSqliteFamilyWithinBudget(fullPath, maximumFamilyBytes);
                int existingVersion;
                try
                {
                    existingVersion = ReadExistingSchemaVersion(fullPath);
                }
                catch (Exception ex) when (IsRecoverableStoreException(ex))
                {
                    return MetadataIndexSaveResult.Failed(
                        fullPath,
                        $"existing metadata cache was unreadable and preserved: {ex.GetType().Name}: {ex.Message}");
                }
                if (existingVersion > SchemaVersion)
                {
                    return MetadataIndexSaveResult.Preserved(
                        fullPath,
                        entries.Count,
                        $"newer metadata cache schema version {existingVersion} was preserved at commit time",
                        MetadataIndexSaveDisposition.Protected);
                }
                if (existingVersion != SchemaVersion)
                {
                    return MetadataIndexSaveResult.Failed(
                        fullPath,
                        $"existing metadata cache schema version {existingVersion} was preserved because it cannot be rebuilt safely");
                }

                // A WAL database is one live family. Rebuild a readable v1
                // target inside SQLite so concurrent readers keep their old
                // snapshot and committed WAL frames are never detached by
                // file-level replacement.
                using SqliteConnection connection = OpenConnection(fullPath, SqliteOpenMode.ReadWrite);
                ConfigureWriter(connection);
                using SqliteTransaction transaction = connection.BeginTransaction();
                using (SqliteCommand clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM metadata;";
                    _ = clear.ExecuteNonQuery();
                }
                using (SqliteCommand insert = CreateInsertCommand(connection, transaction, updateExisting: false))
                {
                    int index = 0;
                    long aggregateBytes = 0;
                    foreach (MetadataIndexEntry entry in entries)
                    {
                        if ((index & 255) == 0)
                            token.ThrowIfCancellationRequested();
                        aggregateBytes = AccumulateEntryBudget(
                            entry,
                            index,
                            aggregateBytes,
                            maximumAggregateBytes);
                        BindEntry(insert, entry);
                        if (insert.ExecuteNonQuery() != 1)
                            throw new InvalidDataException($"metadata cache entry {index} was not inserted exactly once");
                        index++;
                    }
                }
                using (SqliteCommand revision = connection.CreateCommand())
                {
                    revision.Transaction = transaction;
                    revision.CommandText = "UPDATE store_meta SET entry_revision = entry_revision + 1 WHERE singleton = 1;";
                    if (revision.ExecuteNonQuery() != 1)
                        throw new InvalidDataException("metadata cache revision row was unavailable");
                }
                CheckExpectedEntryCount(connection, entries.Count, transaction);
                _ = ValidateStoredPayloadBudget(connection, transaction, maximumAggregateBytes);
                transaction.Commit();
                Checkpoint(connection, required: false);
                return MetadataIndexSaveResult.Saved(fullPath, entries.Count);
            }

            string? orphanSidecar = FindExistingSqliteSidecar(fullPath);
            if (orphanSidecar is not null)
            {
                return MetadataIndexSaveResult.Failed(
                    fullPath,
                    $"metadata cache main file was absent while orphan sidecar {Path.GetFileName(orphanSidecar)} was preserved");
            }

            CleanupStaleTemporaryFiles(directory, temporaryToken, token);
            TryDeleteSqliteFamily(temporaryPath);
            using (SqliteConnection connection = OpenConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                ConfigureWriter(connection);
                ExecuteNonQuery(connection, CreateSchemaSql);
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand insert = CreateInsertCommand(connection, transaction, updateExisting: false);
                int index = 0;
                long aggregateBytes = 0;
                foreach (MetadataIndexEntry entry in entries)
                {
                    if ((index & 255) == 0)
                        token.ThrowIfCancellationRequested();
                    aggregateBytes = AccumulateEntryBudget(
                        entry,
                        index,
                        aggregateBytes,
                        maximumAggregateBytes);
                    BindEntry(insert, entry);
                    if (insert.ExecuteNonQuery() != 1)
                        throw new InvalidDataException($"metadata cache entry {index} was not inserted exactly once");
                    index++;
                }
                CheckExpectedEntryCount(connection, entries.Count, transaction);
                _ = ValidateStoredPayloadBudget(connection, transaction, maximumAggregateBytes);
                transaction.Commit();
                Checkpoint(connection, required: true);
            }

            token.ThrowIfCancellationRequested();
            FlushFile(temporaryPath);
            EnsureSqliteFamilyWithinBudget(temporaryPath, maximumFamilyBytes);
            // The target was absent while holding the writer lock. Do not
            // publish into a namespace where either a main file or SQLite
            // sidecar appeared unexpectedly after that check.
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new IOException("metadata cache target appeared before publication and was preserved");
            orphanSidecar = FindExistingSqliteSidecar(fullPath);
            if (orphanSidecar is not null)
            {
                throw new IOException(
                    $"metadata cache orphan sidecar {Path.GetFileName(orphanSidecar)} appeared before publication and was preserved");
            }
            File.Move(temporaryPath, fullPath);
            TryDeleteSqliteSidecars(temporaryPath);
            return MetadataIndexSaveResult.Saved(fullPath, entries.Count);
        }
        catch (OperationCanceledException)
        {
            TryDeleteSqliteFamily(temporaryPath);
            throw;
        }
        catch (Exception ex) when (IsRecoverableStoreException(ex))
        {
            TryDeleteSqliteFamily(temporaryPath);
            return MetadataIndexSaveResult.Failed(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lockStream?.Dispose();
            TryDeleteTemporary(lockPath);
        }
    }

    public static MetadataIndexSaveResult ApplyChanges(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> upserts,
        IReadOnlyCollection<string> removedPaths,
        int expectedEntryCount,
        CancellationToken token)
        => ApplyChangesCore(
            path,
            upserts,
            removedPaths,
            expectedEntryCount,
            token,
            MaximumSqliteFamilyBytes,
            MaximumSqliteAggregateBytes);

    internal static MetadataIndexSaveResult ApplyChangesWithBudgetForSmoke(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> upserts,
        IReadOnlyCollection<string> removedPaths,
        int expectedEntryCount,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
        => ApplyChangesCore(
            path,
            upserts,
            removedPaths,
            expectedEntryCount,
            token,
            maximumFamilyBytes,
            maximumAggregateBytes);

    private static MetadataIndexSaveResult ApplyChangesCore(
        string path,
        IReadOnlyCollection<MetadataIndexEntry> upserts,
        IReadOnlyCollection<string> removedPaths,
        int expectedEntryCount,
        CancellationToken token,
        long maximumFamilyBytes,
        long maximumAggregateBytes)
    {
        string fullPath = Path.GetFullPath(path);
        ValidateBudgets(maximumFamilyBytes, maximumAggregateBytes);
        if (expectedEntryCount < 0 || expectedEntryCount > MaximumEntryCount)
            return MetadataIndexSaveResult.Failed(fullPath, $"entry count {expectedEntryCount} exceeds the safe bound");
        if (upserts.Count == 0 && removedPaths.Count == 0)
            return MetadataIndexSaveResult.Preserved(fullPath, expectedEntryCount, "metadata cache required no row changes");

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return MetadataIndexSaveResult.Failed(fullPath, "metadata cache directory was unavailable");
        string lockToken = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))
            .ToLowerInvariant()[..12];
        string lockPath = Path.Combine(directory, $".mi-{lockToken}.lock");
        FileStream? lockStream = null;
        try
        {
            token.ThrowIfCancellationRequested();
            lockStream = AcquireWriterLock(lockPath, token);
            EnsureSqliteFamilyWithinBudget(fullPath, maximumFamilyBytes);
            int existingVersion = ReadExistingSchemaVersion(fullPath);
            if (existingVersion > SchemaVersion)
            {
                return MetadataIndexSaveResult.Preserved(
                    fullPath,
                    expectedEntryCount,
                    $"newer metadata cache schema version {existingVersion} was preserved at commit time",
                    MetadataIndexSaveDisposition.Protected);
            }
            if (existingVersion != SchemaVersion)
                return MetadataIndexSaveResult.Failed(fullPath, $"metadata cache schema version {existingVersion} was invalid");

            using SqliteConnection connection = OpenConnection(fullPath, SqliteOpenMode.ReadWrite);
            ConfigureWriter(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand upsert = CreateInsertCommand(connection, transaction, updateExisting: true);
            int index = 0;
            long aggregateBytes = 0;
            foreach (MetadataIndexEntry entry in upserts)
            {
                if ((index & 255) == 0)
                    token.ThrowIfCancellationRequested();
                aggregateBytes = AccumulateEntryBudget(
                    entry,
                    index,
                    aggregateBytes,
                    maximumAggregateBytes);
                BindEntry(upsert, entry);
                _ = upsert.ExecuteNonQuery();
                index++;
            }

            using SqliteCommand remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM metadata WHERE path = $path COLLATE NOCASE;";
            SqliteParameter removePath = remove.Parameters.Add("$path", SqliteType.Text);
            foreach (string pathToRemove in removedPaths)
            {
                token.ThrowIfCancellationRequested();
                removePath.Value = pathToRemove;
                _ = remove.ExecuteNonQuery();
            }

            using (SqliteCommand revision = connection.CreateCommand())
            {
                revision.Transaction = transaction;
                revision.CommandText = "UPDATE store_meta SET entry_revision = entry_revision + 1 WHERE singleton = 1;";
                if (revision.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("metadata cache revision row was unavailable");
            }
            CheckExpectedEntryCount(connection, expectedEntryCount, transaction);
            _ = ValidateStoredPayloadBudget(connection, transaction, maximumAggregateBytes);
            transaction.Commit();
            Checkpoint(connection, required: false);
            return MetadataIndexSaveResult.Saved(fullPath, expectedEntryCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableStoreException(ex))
        {
            return MetadataIndexSaveResult.Failed(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lockStream?.Dispose();
            TryDeleteTemporary(lockPath);
        }
    }

    public static byte[] EncodePrompt(string? prompt)
        => string.IsNullOrEmpty(prompt)
            ? []
            : StrictUtf8.GetBytes(prompt);

    public static string DecodePrompt(byte[] promptUtf8)
    {
        ArgumentNullException.ThrowIfNull(promptUtf8);
        return promptUtf8.Length == 0
            ? ""
            : StrictUtf8.GetString(promptUtf8);
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ConfigureWriter(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 2000;");
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
        ExecuteNonQuery(connection, "PRAGMA temp_store = MEMORY;");
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM store_meta WHERE singleton = 1;";
        object? value = command.ExecuteScalar();
        if (value is not long raw || raw < int.MinValue || raw > int.MaxValue)
            throw new InvalidDataException("metadata cache schema row was unavailable");
        return (int)raw;
    }

    private static int ValidateStoredPayloadBudget(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long maximumAggregateBytes)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(MAX(CASE
                       WHEN typeof(path) = 'text' AND length(path) <= $maximumPathBytes
                       THEN length(CAST(path AS BLOB)) ELSE $rejectedPathBytes END), 0),
                   COALESCE(MAX(CASE
                       WHEN typeof(prompt_utf8) = 'blob'
                       THEN length(prompt_utf8) ELSE $rejectedPromptBytes END), 0),
                   COALESCE(SUM(CASE
                       WHEN typeof(path) = 'text' AND length(path) <= $maximumPathBytes
                       THEN length(CAST(path AS BLOB)) ELSE $rejectedPathBytes END), 0),
                   COALESCE(SUM(CASE
                       WHEN typeof(prompt_utf8) = 'blob'
                       THEN length(prompt_utf8) ELSE $rejectedPromptBytes END), 0),
                   COALESCE(SUM(CASE
                       WHEN typeof(path) = 'text'
                        AND typeof(source_length) = 'integer'
                        AND typeof(source_mtime_ticks) = 'integer'
                        AND typeof(source_ctime_ticks) = 'integer'
                        AND typeof(width) = 'integer'
                        AND typeof(height) = 'integer'
                        AND typeof(prompt_utf8) = 'blob'
                       THEN 0 ELSE 1 END), 0)
            FROM metadata;
            """;
        command.Parameters.AddWithValue("$maximumPathBytes", MaximumPathBytes);
        command.Parameters.AddWithValue("$rejectedPathBytes", (long)MaximumPathBytes + 1);
        command.Parameters.AddWithValue("$rejectedPromptBytes", (long)MaximumPromptBytes + 1);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("metadata cache bounds preflight returned no result");

        long countRaw = ReadNonnegativeInt64(reader, 0, "entry count");
        long maximumPathBytes = ReadNonnegativeInt64(reader, 1, "maximum path length");
        long maximumPromptBytes = ReadNonnegativeInt64(reader, 2, "maximum prompt length");
        long totalPathBytes = ReadNonnegativeInt64(reader, 3, "aggregate path length");
        long totalPromptBytes = ReadNonnegativeInt64(reader, 4, "aggregate prompt length");
        long invalidTypeCount = ReadNonnegativeInt64(reader, 5, "invalid type count");

        if (countRaw > MaximumEntryCount)
            throw new InvalidDataException($"metadata cache entry count {countRaw} is outside the safe bound");
        if (maximumPathBytes > MaximumPathBytes)
            throw new InvalidDataException($"metadata cache path length {maximumPathBytes} is outside the safe bound");
        if (maximumPromptBytes > MaximumPromptBytes)
            throw new InvalidDataException($"metadata cache prompt length {maximumPromptBytes} is outside the safe bound");
        if (invalidTypeCount != 0)
            throw new InvalidDataException($"metadata cache contained {invalidTypeCount} row(s) with invalid SQLite value types");
        if (totalPathBytes > maximumAggregateBytes
            || totalPromptBytes > maximumAggregateBytes - totalPathBytes)
        {
            throw new InvalidDataException(
                $"metadata cache aggregate path/prompt payload is outside the {maximumAggregateBytes:N0}-byte safe bound");
        }

        return (int)countRaw;
    }

    private static long ReadNonnegativeInt64(SqliteDataReader reader, int ordinal, string name)
    {
        if (reader.GetValue(ordinal) is not long value || value < 0)
            throw new InvalidDataException($"metadata cache {name} was invalid");
        return value;
    }

    private static int ReadEntryCount(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM metadata;";
        object? value = command.ExecuteScalar();
        if (value is not long raw || raw < 0 || raw > int.MaxValue)
            throw new InvalidDataException("metadata cache entry count was invalid");
        return (int)raw;
    }

    private static void CheckExpectedEntryCount(
        SqliteConnection connection,
        int expectedEntryCount,
        SqliteTransaction? transaction = null)
    {
        int actual = ReadEntryCount(connection, transaction);
        if (actual != expectedEntryCount)
            throw new InvalidDataException($"metadata cache contained {actual:N0} rows; expected {expectedEntryCount:N0}");
    }

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool updateExisting)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = updateExisting
            ? """
                INSERT INTO metadata(
                    path, source_length, source_mtime_ticks, source_ctime_ticks,
                    width, height, prompt_utf8)
                VALUES ($path, $length, $mtime, $ctime, $width, $height, $prompt)
                ON CONFLICT(path) DO UPDATE SET
                    source_length = excluded.source_length,
                    source_mtime_ticks = excluded.source_mtime_ticks,
                    source_ctime_ticks = excluded.source_ctime_ticks,
                    width = excluded.width,
                    height = excluded.height,
                    prompt_utf8 = excluded.prompt_utf8;
                """
            : """
                INSERT INTO metadata(
                    path, source_length, source_mtime_ticks, source_ctime_ticks,
                    width, height, prompt_utf8)
                VALUES ($path, $length, $mtime, $ctime, $width, $height, $prompt);
                """;
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$length", SqliteType.Integer);
        command.Parameters.Add("$mtime", SqliteType.Integer);
        command.Parameters.Add("$ctime", SqliteType.Integer);
        command.Parameters.Add("$width", SqliteType.Integer);
        command.Parameters.Add("$height", SqliteType.Integer);
        command.Parameters.Add("$prompt", SqliteType.Blob);
        return command;
    }

    private static void BindEntry(SqliteCommand command, MetadataIndexEntry entry)
    {
        command.Parameters["$path"].Value = entry.Path;
        command.Parameters["$length"].Value = entry.SourceLength;
        command.Parameters["$mtime"].Value = entry.SourceLastWriteUtcTicks;
        command.Parameters["$ctime"].Value = entry.SourceCreationUtcTicks;
        command.Parameters["$width"].Value = entry.Width;
        command.Parameters["$height"].Value = entry.Height;
        command.Parameters["$prompt"].Value = entry.PromptUtf8;
    }

    private static long AccumulateEntryBudget(
        MetadataIndexEntry entry,
        int index,
        long aggregateBytes,
        long maximumAggregateBytes)
    {
        ValidateEntry(entry, index);
        long rowBytes = StrictUtf8.GetByteCount(entry.Path) + (long)entry.PromptUtf8.Length;
        if (rowBytes > maximumAggregateBytes
            || aggregateBytes > maximumAggregateBytes - rowBytes)
        {
            throw new InvalidDataException(
                $"metadata cache aggregate path/prompt payload is outside the {maximumAggregateBytes:N0}-byte safe bound");
        }
        return aggregateBytes + rowBytes;
    }

    private static void ValidateEntry(MetadataIndexEntry entry, int index)
        => ValidateEntry(
            entry.Path,
            entry.SourceLength,
            entry.SourceLastWriteUtcTicks,
            entry.SourceCreationUtcTicks,
            entry.Width,
            entry.Height,
            entry.PromptUtf8,
            index);

    private static void ValidateEntry(
        string sourcePath,
        long sourceLength,
        long sourceLastWriteUtcTicks,
        long sourceCreationUtcTicks,
        int width,
        int height,
        byte[] promptUtf8,
        int index)
    {
        if (!Path.IsPathFullyQualified(sourcePath)
            || StrictUtf8.GetByteCount(sourcePath) > MaximumPathBytes
            || sourceLength < 0
            || sourceLastWriteUtcTicks < DateTime.MinValue.Ticks
            || sourceLastWriteUtcTicks > DateTime.MaxValue.Ticks
            || sourceCreationUtcTicks < DateTime.MinValue.Ticks
            || sourceCreationUtcTicks > DateTime.MaxValue.Ticks
            || width <= 0
            || height <= 0
            || promptUtf8.Length > MaximumPromptBytes)
        {
            throw new InvalidDataException($"metadata cache entry {index} was invalid or outside the safe bound");
        }
        _ = StrictUtf8.GetCharCount(promptUtf8);
    }

    private static int ReadExistingSchemaVersion(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("metadata cache disappeared before the incremental commit");
        using SqliteConnection connection = OpenConnection(path, SqliteOpenMode.ReadOnly);
        return ReadSchemaVersion(connection);
    }

    private static void Checkpoint(SqliteConnection connection, bool required)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                if (required)
                    throw new InvalidDataException("metadata cache checkpoint returned no result");
                return;
            }
            int busy = reader.GetInt32(0);
            int logFrames = reader.GetInt32(1);
            int checkpointedFrames = reader.GetInt32(2);
            if (required && (busy != 0 || logFrames != checkpointedFrames))
            {
                throw new InvalidDataException(
                    $"metadata cache checkpoint was incomplete ({busy}, {logFrames}, {checkpointedFrames})");
            }
        }
        catch (SqliteException) when (!required)
        {
            // A reader can temporarily keep WAL pages live. They remain part
            // of the same SQLite cache authority and are recovered normally.
        }
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateBudgets(long maximumFamilyBytes, long maximumAggregateBytes)
    {
        if (maximumFamilyBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFamilyBytes));
        if (maximumAggregateBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAggregateBytes));
    }

    private static void EnsureSqliteFamilyWithinBudget(string path, long maximumFamilyBytes)
    {
        long totalBytes = 0;
        foreach (string member in EnumerateSqliteFamily(path))
        {
            if (Directory.Exists(member))
                throw new InvalidDataException($"metadata cache family member {Path.GetFileName(member)} was not a file");
            if (!File.Exists(member))
                continue;

            long length = new FileInfo(member).Length;
            if (length < 0
                || length > maximumFamilyBytes
                || totalBytes > maximumFamilyBytes - length)
            {
                throw new InvalidDataException(
                    $"metadata cache SQLite family is outside the {maximumFamilyBytes:N0}-byte safe bound");
            }
            totalBytes += length;
        }
    }

    private static IEnumerable<string> EnumerateSqliteFamily(string path)
    {
        yield return path;
        foreach (string suffix in SqliteSidecarSuffixes)
            yield return path + suffix;
    }

    private static string? FindExistingSqliteSidecar(string path)
    {
        foreach (string suffix in SqliteSidecarSuffixes)
        {
            string sidecar = path + suffix;
            if (File.Exists(sidecar) || Directory.Exists(sidecar))
                return sidecar;
        }
        return null;
    }

    private static bool IsRecoverableStoreException(Exception ex)
        => ex is SqliteException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or EndOfStreamException
            or DecoderFallbackException
            or ArgumentException
            or InvalidCastException
            or OverflowException
            or NotSupportedException;

    private static MetadataIndexLoadResult LoadLegacyBinary(string path, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
            if (reader.ReadInt32() != LegacyMagic)
                return MetadataIndexLoadResult.Invalid(fullPath, "legacy metadata index magic did not match");
            int version = reader.ReadInt32();
            if (version != LegacyVersion)
                return MetadataIndexLoadResult.Unsupported(fullPath, $"legacy metadata index version {version} is unsupported");
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumEntryCount)
                return MetadataIndexLoadResult.Invalid(fullPath, $"legacy metadata index entry count {count} is outside the safe bound");
            long payloadLength = reader.ReadInt64();
            byte[] expectedPayloadHash = reader.ReadBytes(LegacyPayloadHashBytes);
            if (expectedPayloadHash.Length != LegacyPayloadHashBytes
                || payloadLength < 0
                || payloadLength > MaximumLegacyIndexBytes
                || stream.Length != LegacyHeaderBytes + payloadLength)
            {
                return MetadataIndexLoadResult.Invalid(fullPath, "legacy metadata index payload length was invalid");
            }

            byte[] actualPayloadHash = ComputeLegacyPayloadHash(stream, payloadLength, token);
            if (!CryptographicOperations.FixedTimeEquals(expectedPayloadHash, actualPayloadHash))
                return MetadataIndexLoadResult.Invalid(fullPath, "legacy metadata index payload checksum did not match");
            stream.Position = LegacyHeaderBytes;

            var entries = new Dictionary<string, MetadataIndexEntry>(count, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < count; index++)
            {
                if ((index & 255) == 0)
                    token.ThrowIfCancellationRequested();
                string sourcePath = ReadLegacyBoundedString(reader, MaximumPathBytes);
                long sourceLength = reader.ReadInt64();
                long sourceLastWriteUtcTicks = reader.ReadInt64();
                long sourceCreationUtcTicks = reader.ReadInt64();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                byte[] promptUtf8 = ReadLegacyBoundedUtf8Bytes(reader, MaximumPromptBytes);
                ValidateEntry(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtcTicks,
                    sourceCreationUtcTicks,
                    width,
                    height,
                    promptUtf8,
                    index);
                entries[sourcePath] = new MetadataIndexEntry(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtcTicks,
                    sourceCreationUtcTicks,
                    width,
                    height,
                    promptUtf8);
            }

            if (stream.Position != stream.Length)
                return MetadataIndexLoadResult.Invalid(fullPath, "legacy metadata index had unexpected trailing bytes");
            return MetadataIndexLoadResult.Loaded(fullPath, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableStoreException(ex))
        {
            return MetadataIndexLoadResult.Invalid(fullPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ReadLegacyBoundedString(BinaryReader reader, int maximumBytes)
        => StrictUtf8.GetString(ReadLegacyBoundedUtf8Bytes(reader, maximumBytes));

    private static byte[] ReadLegacyBoundedUtf8Bytes(BinaryReader reader, int maximumBytes)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > maximumBytes)
            throw new InvalidDataException($"string byte count {byteCount} is outside the safe bound");
        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
            throw new EndOfStreamException("legacy metadata index string was truncated");
        _ = StrictUtf8.GetCharCount(bytes);
        return bytes;
    }

    private static byte[] ComputeLegacyPayloadHash(FileStream stream, long payloadLength, CancellationToken token)
    {
        stream.Position = LegacyHeaderBytes;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long remaining = payloadLength;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, requested);
            if (read <= 0)
                throw new EndOfStreamException("legacy metadata index payload was truncated while hashing");
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

    private static void CleanupStaleTemporaryFiles(
        string directory,
        string temporaryToken,
        CancellationToken token)
    {
        string pattern = $".mi-{temporaryToken}-*.tmp";
        DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetLastWriteTimeUtc(candidate) < cutoffUtc)
                        TryDeleteSqliteFamily(candidate);
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

    private static void TryDeleteSqliteFamily(string path)
    {
        TryDeleteTemporary(path);
        TryDeleteSqliteSidecars(path);
    }

    private static void TryDeleteSqliteSidecars(string path)
    {
        foreach (string suffix in SqliteSidecarSuffixes)
            TryDeleteTemporary(path + suffix);
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
            // A failed cleanup does not justify touching the last valid cache.
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
    byte[] PromptUtf8)
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
    string? Error,
    bool RequiresMigration,
    string? SourcePath)
{
    public static MetadataIndexLoadResult Missing(string path)
        => new(path, MetadataIndexLoadState.Missing, EmptyEntries(), null, false, null);

    public static MetadataIndexLoadResult Loaded(
        string path,
        IReadOnlyDictionary<string, MetadataIndexEntry> entries,
        bool requiresMigration = false,
        string? sourcePath = null)
        => new(path, MetadataIndexLoadState.Loaded, entries, null, requiresMigration, sourcePath);

    public static MetadataIndexLoadResult Invalid(string path, string error, string? sourcePath = null)
        => new(path, MetadataIndexLoadState.Invalid, EmptyEntries(), error, false, sourcePath);

    public static MetadataIndexLoadResult Unsupported(string path, string error, string? sourcePath = null)
        => new(path, MetadataIndexLoadState.Unsupported, EmptyEntries(), error, false, sourcePath);

    private static IReadOnlyDictionary<string, MetadataIndexEntry> EmptyEntries()
        => new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase);
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
