using Microsoft.Data.Sqlite;
using System.IO;

namespace PhotoViewer.Wpf;

internal enum FavoriteActivityStoreReadState
{
    Missing,
    Loaded,
    Protected,
}

internal sealed record FavoriteActivityStoreReadResult(
    FavoriteActivityStoreReadState State,
    Dictionary<string, DateTimeOffset> Entries,
    string? Error = null);

internal sealed record FavoriteActivityStoreWriteResult(
    bool Saved,
    bool Protected,
    Dictionary<string, DateTimeOffset> EvictedEntries,
    string? Error = null);

/// <summary>
/// Small WPF-local activity index for the "Fav touched" sort. This store does
/// not own favorite levels; it only avoids rewriting the full viewer state for
/// each activity timestamp.
/// </summary>
internal static class FavoriteActivityStore
{
    private const int SchemaVersion = 1;
    private const int ApplicationId = 0x41424641; // "AFBA"
    private const int MaximumPathCharacters = 32 * 1024;
    private const long MaximumFamilyBytes = 128L * 1024 * 1024;
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    public static FavoriteActivityStoreReadResult Read(
        LocalPersistenceStorePath path,
        int maximumEntries)
    {
        string fullPath = path.FullPath;
        // This capability fixes the leaf to favorite-activity.sqlite3 beside
        // the Viewer state store or inside an explicitly TEMP-only fixture.
        // codeql[cs/path-injection]
        if (!File.Exists(fullPath))
        {
            string? orphan = SidecarSuffixes
                .Select(suffix => fullPath + suffix)
                .FirstOrDefault(File.Exists);
            return orphan is null
                ? new FavoriteActivityStoreReadResult(
                    FavoriteActivityStoreReadState.Missing,
                    new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase))
                : Protected($"orphan SQLite sidecar was preserved: {Path.GetFileName(orphan)}");
        }

        try
        {
            EnsureFamilyWithinBudget(path);
            using SqliteConnection connection = Open(fullPath, SqliteOpenMode.ReadOnly);
            Execute(connection, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;");
            ValidateSchema(connection);
            using (SqliteCommand check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1);";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    return Protected("SQLite quick_check failed");
            }

            var entries = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT path, changed_at_utc_ticks
                FROM favorite_activity
                ORDER BY changed_at_utc_ticks DESC, path_key ASC;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (entries.Count >= maximumEntries)
                    return Protected($"entry count exceeded {maximumEntries}");
                string itemPath = reader.GetString(0);
                long ticks = reader.GetInt64(1);
                if (string.IsNullOrWhiteSpace(itemPath)
                    || itemPath.Length > MaximumPathCharacters
                    || ticks < DateTimeOffset.MinValue.UtcTicks
                    || ticks > DateTimeOffset.MaxValue.UtcTicks)
                {
                    return Protected("an activity row was outside the supported bounds");
                }

                string normalized = Path.GetFullPath(itemPath);
                if (!entries.TryAdd(normalized, new DateTimeOffset(ticks, TimeSpan.Zero)))
                    return Protected("duplicate case-insensitive activity paths were found");
            }

            return new FavoriteActivityStoreReadResult(
                FavoriteActivityStoreReadState.Loaded,
                entries);
        }
        catch (Exception error)
        {
            return Protected(error.Message);
        }
    }

    public static FavoriteActivityStoreWriteResult Upsert(
        LocalPersistenceStorePath path,
        IReadOnlyDictionary<string, DateTimeOffset> activity,
        int maximumEntries)
    {
        string fullPath = path.FullPath;
        FavoriteActivityStoreReadResult existing = Read(path, maximumEntries);
        if (existing.State == FavoriteActivityStoreReadState.Protected)
        {
            return new FavoriteActivityStoreWriteResult(
                Saved: false,
                Protected: true,
                new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
                existing.Error);
        }

        try
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using SqliteConnection connection = Open(fullPath, SqliteOpenMode.ReadWriteCreate);
            ConfigureWriter(connection);
            if (existing.State == FavoriteActivityStoreReadState.Missing)
                CreateSchema(connection);
            else
                ValidateSchema(connection);

            var evicted = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO favorite_activity(path_key, path, changed_at_utc_ticks)
                    VALUES ($key, $path, $ticks)
                    ON CONFLICT(path_key) DO UPDATE SET
                        path = excluded.path,
                        changed_at_utc_ticks = excluded.changed_at_utc_ticks
                    WHERE excluded.changed_at_utc_ticks > favorite_activity.changed_at_utc_ticks;
                    """;
                SqliteParameter keyParameter = upsert.Parameters.Add("$key", SqliteType.Text);
                SqliteParameter pathParameter = upsert.Parameters.Add("$path", SqliteType.Text);
                SqliteParameter ticksParameter = upsert.Parameters.Add("$ticks", SqliteType.Integer);
                foreach ((string itemPath, DateTimeOffset changedAtUtc) in activity)
                {
                    if (string.IsNullOrWhiteSpace(itemPath) || changedAtUtc == default)
                        continue;
                    string normalized = Path.GetFullPath(itemPath);
                    if (normalized.Length > MaximumPathCharacters)
                        throw new InvalidDataException("favorite activity path exceeded the supported bound");
                    keyParameter.Value = normalized.ToUpperInvariant();
                    pathParameter.Value = normalized;
                    ticksParameter.Value = changedAtUtc.ToUniversalTime().UtcTicks;
                    upsert.ExecuteNonQuery();
                }
            }

            using (SqliteCommand overflow = connection.CreateCommand())
            {
                overflow.Transaction = transaction;
                overflow.CommandText = """
                    SELECT path_key, path, changed_at_utc_ticks
                    FROM favorite_activity
                    ORDER BY changed_at_utc_ticks DESC, path_key ASC
                    LIMIT -1 OFFSET $maximum;
                    """;
                overflow.Parameters.AddWithValue("$maximum", maximumEntries);
                var keys = new List<string>();
                using SqliteDataReader reader = overflow.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    string itemPath = reader.GetString(1);
                    long ticks = reader.GetInt64(2);
                    keys.Add(key);
                    evicted[itemPath] = new DateTimeOffset(ticks, TimeSpan.Zero);
                }
                reader.Close();

                using SqliteCommand delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM favorite_activity WHERE path_key=$key;";
                SqliteParameter deleteKey = delete.Parameters.Add("$key", SqliteType.Text);
                foreach (string key in keys)
                {
                    deleteKey.Value = key;
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            EnsureFamilyWithinBudget(path);
            return new FavoriteActivityStoreWriteResult(
                Saved: true,
                Protected: false,
                evicted);
        }
        catch (Exception error)
        {
            return new FavoriteActivityStoreWriteResult(
                Saved: false,
                Protected: false,
                new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
                error.Message);
        }
    }

    private static FavoriteActivityStoreReadResult Protected(string error)
        => new(
            FavoriteActivityStoreReadState.Protected,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            error);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
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
        => Execute(
            connection,
            "PRAGMA busy_timeout=2000; PRAGMA trusted_schema=OFF; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY;");

    private static void CreateSchema(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version={SchemaVersion};
            CREATE TABLE store_meta (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),
                schema_version INTEGER NOT NULL CHECK(schema_version>=1)
            ) STRICT, WITHOUT ROWID;
            INSERT INTO store_meta(singleton, schema_version) VALUES(1, {SchemaVersion});
            CREATE TABLE favorite_activity (
                path_key TEXT NOT NULL PRIMARY KEY,
                path TEXT NOT NULL,
                changed_at_utc_ticks INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        long applicationId = ExecuteScalarInt64(connection, "PRAGMA application_id;");
        long userVersion = ExecuteScalarInt64(connection, "PRAGMA user_version;");
        if (applicationId != ApplicationId)
            throw new InvalidDataException("favorite activity SQLite application id was unsupported");
        if (userVersion > SchemaVersion)
            throw new InvalidDataException($"favorite activity schema version {userVersion} is newer than supported");
        if (userVersion != SchemaVersion)
            throw new InvalidDataException($"favorite activity schema version {userVersion} was invalid");
        long metaVersion = ExecuteScalarInt64(
            connection,
            "SELECT schema_version FROM store_meta WHERE singleton=1;");
        if (metaVersion != SchemaVersion)
            throw new InvalidDataException("favorite activity schema metadata did not match");

        using SqliteCommand objects = connection.CreateCommand();
        objects.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var names = new List<string>();
        using (SqliteDataReader reader = objects.ExecuteReader())
        {
            while (reader.Read())
                names.Add(reader.GetString(0));
        }
        if (!names.SequenceEqual(["favorite_activity", "store_meta"], StringComparer.Ordinal))
            throw new InvalidDataException("favorite activity schema contained unsupported objects");
    }

    private static void EnsureFamilyWithinBudget(LocalPersistenceStorePath path)
    {
        string fullPath = path.FullPath;
        long total = 0;
        foreach (string candidate in new[] { fullPath }.Concat(
                     SidecarSuffixes.Select(suffix => fullPath + suffix)))
        {
            // Candidate names are the fixed SQLite store capability plus
            // SQLite's three fixed sidecar suffixes; no row or UI value can
            // select a filesystem target.
            // codeql[cs/path-injection]
            if (File.Exists(candidate))
                total = checked(total + new FileInfo(candidate).Length);
        }
        if (total > MaximumFamilyBytes)
            throw new InvalidDataException("favorite activity SQLite family exceeded the supported size");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarInt64(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
