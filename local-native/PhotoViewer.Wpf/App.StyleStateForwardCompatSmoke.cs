using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private const string StyleStateSmokeTempDirectoryPrefix =
        "photoviewer-wpf-automation-";

    private async void CaptureStyleStateForwardCompatSmoke(string resultPath)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        string resultFullPath = RequireStyleStateSmokeTempPath(resultPath, "result");
        string storageRoot = RequireManagedStyleStateSmokeRoot(
            _styleStateForwardCompatSmokeStorageRoot
                ?? throw new InvalidOperationException(
                    "The managed Style state smoke root was not configured."));
        string statePath = Path.Combine(storageRoot, "state.json");
        string stylePath = Path.Combine(storageRoot, "ai-styles.json");
        string activityPath = Path.Combine(storageRoot, "favorite-activity.sqlite3");
        string sourcePath = Path.Combine(storageRoot, "fixture-source.bin");
        string firstFavoritePath = Path.Combine(storageRoot, "first.png");
        string secondFavoritePath = Path.Combine(storageRoot, "second.png");
        string thirdFavoritePath = Path.Combine(storageRoot, "third.png");
        MainWindow? window = null;
        object result;
        bool ok = false;
        try
        {
            Directory.CreateDirectory(storageRoot);
            File.WriteAllBytes(sourcePath, [1, 3, 5, 7, 9, 11]);
            string sourceBefore = Fingerprint(sourcePath);
            DateTimeOffset firstTime = new(2026, 8, 10, 1, 2, 3, TimeSpan.Zero);
            DateTimeOffset secondTime = new(2026, 8, 11, 2, 3, 4, TimeSpan.Zero);
            DateTimeOffset thirdTime = new(2026, 8, 12, 3, 4, 5, TimeSpan.Zero);

            ViewerState legacy = CreateCompatibleStyleStateFixture();
            legacy.FavoriteChangedAtUtcByPath = new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase)
            {
                [firstFavoritePath] = firstTime,
                [secondFavoritePath] = secondTime,
            };
            legacy.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["FutureViewerObject"] = JsonSerializer.SerializeToElement(new { keep = true }),
            };
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));

            window = new MainWindow();
            AiStyleDocument migratedStyles = ReadAiStyleFixture(stylePath);
            FavoriteActivityStoreReadResult migratedActivity = FavoriteActivityStore.Read(
                activityPath,
                20_000);
            long migratedStyleDocumentBytes = new FileInfo(stylePath).Length;
            using JsonDocument compactedState = JsonDocument.Parse(File.ReadAllText(statePath));
            bool legacyFieldsRemoved =
                !compactedState.RootElement.TryGetProperty("FavoriteChangedAtUtcByPath", out _)
                && !compactedState.RootElement.TryGetProperty("PhotorealStyles", out _)
                && !compactedState.RootElement.TryGetProperty("SelectedPhotorealStyleName", out _)
                && !compactedState.RootElement.TryGetProperty("VideoStyles", out _)
                && !compactedState.RootElement.TryGetProperty("SelectedVideoStyleName", out _)
                && !compactedState.RootElement.TryGetProperty("I2iEditStyles", out _)
                && !compactedState.RootElement.TryGetProperty("SelectedI2iEditStyleName", out _);
            bool viewerUnknownPreserved =
                compactedState.RootElement.TryGetProperty("FutureViewerObject", out JsonElement viewerUnknown)
                && viewerUnknown.GetProperty("keep").GetBoolean();
            bool migrationPreserved = window.SplitLocalPersistenceReadyForSmoke
                && migratedActivity.State == FavoriteActivityStoreReadState.Loaded
                && migratedActivity.Entries.Count == 2
                && migratedActivity.Entries[firstFavoritePath] == firstTime
                && migratedActivity.Entries[secondFavoritePath] == secondTime
                && migratedStyles.VideoStyles?.SingleOrDefault()?.ExtensionData
                    ?.TryGetValue("FutureVideoObject", out JsonElement videoObject) == true
                && videoObject.GetProperty("mode").GetString() == "cinematic"
                && migratedStyles.I2iEditStyles?.SingleOrDefault()?.ExtensionData
                    ?.TryGetValue("FutureI2iScalar", out JsonElement i2iScalar) == true
                && i2iScalar.GetInt32() == 17
                && legacyFieldsRemoved
                && viewerUnknownPreserved;

            string stateBeforeActivity = Fingerprint(statePath);
            bool activityWriteCompleted = await window.PersistFavoriteActivityForSmokeAsync(
                thirdFavoritePath,
                thirdTime,
                TimeSpan.FromSeconds(10));
            string stateAfterActivity = Fingerprint(statePath);
            FavoriteActivityStoreReadResult afterActivity = FavoriteActivityStore.Read(
                activityPath,
                20_000);
            bool incrementalActivityWrite = activityWriteCompleted
                && string.Equals(stateBeforeActivity, stateAfterActivity, StringComparison.Ordinal)
                && afterActivity.State == FavoriteActivityStoreReadState.Loaded
                && afterActivity.Entries.Count == 3
                && afterActivity.Entries[thirdFavoritePath] == thirdTime;

            bool replayCompleted = await window.PersistFavoriteActivityForSmokeAsync(
                thirdFavoritePath,
                thirdTime,
                TimeSpan.FromSeconds(10));
            FavoriteActivityStoreReadResult afterReplay = FavoriteActivityStore.Read(
                activityPath,
                20_000);
            bool idempotentReplay = replayCompleted
                && afterReplay.State == FavoriteActivityStoreReadState.Loaded
                && afterReplay.Entries.Count == 3
                && afterReplay.Entries[thirdFavoritePath] == thirdTime;

            AiStyleDocument concurrent = ReadAiStyleFixture(stylePath);
            concurrent.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["ConcurrentRootObject"] = JsonSerializer.SerializeToElement(new { revision = 7 }),
            };
            concurrent.VideoStyles![0].ExtensionData ??=
                new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            concurrent.VideoStyles[0].ExtensionData!["ConcurrentVideoScalar"] =
                JsonSerializer.SerializeToElement(true);
            File.WriteAllText(
                stylePath,
                JsonSerializer.Serialize(concurrent, new JsonSerializerOptions { WriteIndented = true }));
            bool styleSaved = window.SaveVideoStyleForSmoke("Cinematic motion");
            AiStyleDocument afterConcurrentSave = ReadAiStyleFixture(stylePath);
            bool concurrentLatestUnknownFieldsPreserved = styleSaved
                && afterConcurrentSave.ExtensionData
                    ?.TryGetValue("ConcurrentRootObject", out JsonElement rootObject) == true
                && rootObject.GetProperty("revision").GetInt32() == 7
                && afterConcurrentSave.VideoStyles?[0].ExtensionData
                    ?.TryGetValue("ConcurrentVideoScalar", out JsonElement videoScalar) == true
                && videoScalar.GetBoolean();

            AiStyleDocument futureStyles = ReadAiStyleFixture(stylePath);
            futureStyles.Version = 2;
            File.WriteAllText(
                stylePath,
                JsonSerializer.Serialize(futureStyles, new JsonSerializerOptions { WriteIndented = true }));
            string futureStyleBefore = Fingerprint(stylePath);
            _ = window.SaveVideoStyleForSmoke("Future must stay protected");
            bool unsupportedFutureStyleProtected = string.Equals(
                futureStyleBefore,
                Fingerprint(stylePath),
                StringComparison.Ordinal);

            window.SuppressStatePersistence();
            window.Close();
            window = null;

            File.WriteAllText(stylePath, "{ malformed-style-document");
            string malformedStyleBefore = Fingerprint(stylePath);
            window = new MainWindow();
            _ = window.SaveVideoStyleForSmoke("Malformed must stay protected");
            bool malformedStyleProtected = string.Equals(
                malformedStyleBefore,
                Fingerprint(stylePath),
                StringComparison.Ordinal);
            window.SuppressStatePersistence();
            window.Close();
            window = null;

            SetSqliteUserVersion(activityPath, 2);
            string futureActivityBefore = Fingerprint(activityPath);
            window = new MainWindow();
            bool fallbackActivityCompleted = await window.PersistFavoriteActivityForSmokeAsync(
                Path.Combine(storageRoot, "future-fallback.png"),
                thirdTime.AddMinutes(1),
                TimeSpan.FromSeconds(10));
            bool unsupportedFutureActivityProtected = fallbackActivityCompleted
                && string.Equals(
                    futureActivityBefore,
                    Fingerprint(activityPath),
                    StringComparison.Ordinal);
            string sourceAfter = Fingerprint(sourcePath);
            bool sourceUnchanged = string.Equals(sourceBefore, sourceAfter, StringComparison.Ordinal);

            window.SuppressStatePersistence();
            window.Close();
            window = null;

            ok = migrationPreserved
                && incrementalActivityWrite
                && idempotentReplay
                && concurrentLatestUnknownFieldsPreserved
                && unsupportedFutureStyleProtected
                && malformedStyleProtected
                && unsupportedFutureActivityProtected
                && sourceUnchanged;
            result = new
            {
                ok,
                migrationPreserved,
                legacyFieldsRemoved,
                viewerUnknownPreserved,
                incrementalActivityWrite,
                idempotentReplay,
                concurrentLatestUnknownFieldsPreserved,
                unsupportedFutureStyleProtected,
                malformedStyleProtected,
                unsupportedFutureActivityProtected,
                sourceUnchanged,
                compactedStateBytes = new FileInfo(statePath).Length,
                styleDocumentBytes = migratedStyleDocumentBytes,
                migratedActivityCount = migratedActivity.Entries.Count,
            };
        }
        catch (Exception ex)
        {
            result = new { ok = false, message = ex.ToString() };
        }
        finally
        {
            if (window is not null)
            {
                window.SuppressStatePersistence();
                window.Close();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        TryDeleteStyleStateSmokeStorage(storageRoot);
        Shutdown(ok ? 0 : 1);
    }

    private static ViewerState CreateCompatibleStyleStateFixture()
        => new()
        {
            Version = 2,
            VideoDurationSeconds = 5,
            VideoPlaybackFps = 12,
            VideoMaximumPixelArea = 307200,
            VideoSteps = 20,
            VideoPrompt = "subtle natural motion",
            VideoModelId = "minimax-h3",
            VideoQualityId = "wan22-ti2v-5b-high-v1",
            VideoStyles =
            [
                new VideoStyleState
                {
                    Name = "Cinematic motion",
                    ModelId = "minimax-h3",
                    QualityId = "wan22-ti2v-5b-high-v1",
                    DurationSeconds = 5,
                    PlaybackFps = 12,
                    MaximumPixelArea = 307200,
                    Steps = 20,
                    Prompt = "subtle natural motion",
                    ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["FutureVideoObject"] = JsonSerializer.SerializeToElement(new { mode = "cinematic" }),
                    },
                },
            ],
            SelectedVideoStyleName = "Cinematic motion",
            I2iEditStyles =
            [
                new I2iEditStyleState
                {
                    Name = "Wardrobe edit",
                    Overall = "photographic editorial treatment",
                    Expression = "",
                    Outfit = "tailored navy jacket",
                    Background = "neutral studio",
                    Pose = "",
                    Steps = 12,
                    CfgScale = 1.4,
                    OutfitMaskMode = "auto",
                    OutfitMaskExpandPixels = 64,
                    SeedMode = "fixed",
                    Seed = 123456789,
                    ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["FutureI2iScalar"] = JsonSerializer.SerializeToElement(17),
                    },
                },
            ],
            SelectedI2iEditStyleName = "Wardrobe edit",
        };

    private static AiStyleDocument ReadAiStyleFixture(string path)
        => JsonSerializer.Deserialize<AiStyleDocument>(File.ReadAllText(path))
            ?? throw new InvalidDataException("AI Style fixture was unavailable.");

    private static void SetSqliteUserVersion(string path, int version)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version={version};";
        command.ExecuteNonQuery();
    }

    private static string Fingerprint(string path)
    {
        FileInfo info = new(path);
        return string.Join(
            ":",
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string RequireManagedStyleStateSmokeRoot(string candidate)
    {
        string storageRoot = Path.GetFullPath(candidate);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? storageParent = Path.GetDirectoryName(storageRoot);
        string storageLeaf = Path.GetFileName(storageRoot);
        if (!string.Equals(storageParent, tempRoot, StringComparison.OrdinalIgnoreCase)
            || !storageLeaf.StartsWith(StyleStateSmokeTempDirectoryPrefix, StringComparison.Ordinal)
            || storageLeaf.Length <= StyleStateSmokeTempDirectoryPrefix.Length)
        {
            throw new ArgumentException(
                "The managed Style state smoke root must be one app-created child directly under TEMP.");
        }
        return storageRoot;
    }

    private static void TryDeleteStyleStateSmokeStorage(string storageRoot)
    {
        try
        {
            string fullStorageRoot = RequireManagedStyleStateSmokeRoot(storageRoot);
            if (Directory.Exists(fullStorageRoot))
                Directory.Delete(fullStorageRoot, recursive: true);
        }
        catch
        {
            // The verifier owns its isolated TEMP fixture and can retry cleanup.
        }
    }

    private static string RequireStyleStateSmokeTempPath(string candidate, string description)
    {
        string fullPath = Path.GetFullPath(candidate);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tempPrefix = tempRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The Style state smoke {description} path must stay under TEMP.");
        }
        return fullPath;
    }
}
