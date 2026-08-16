using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureStyleStateForwardCompatSmoke(string resultPath)
    {
        string resultFullPath = RequireStyleStateSmokeTempPath(
            resultPath,
            "result");
        string configuredStorageRoot = _styleStateForwardCompatSmokeStorageRoot
            ?? throw new InvalidOperationException(
                "The managed Style state smoke root was not configured.");
        string storageRoot = Path.GetFullPath(configuredStorageRoot);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string expectedParent = Path.Combine(
            tempRoot,
            "photoviewer-wpf-automation");
        string expectedPrefix = expectedParent + Path.DirectorySeparatorChar;
        if (!storageRoot.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The managed Style state smoke root must stay under its TEMP parent.");
        }

        string relativeStorageRoot = storageRoot[expectedPrefix.Length..];
        if (relativeStorageRoot.Contains(Path.DirectorySeparatorChar)
            || relativeStorageRoot.Contains(Path.AltDirectorySeparatorChar)
            || !Guid.TryParseExact(relativeStorageRoot, "N", out _))
        {
            throw new ArgumentException(
                "The managed Style state smoke root must be one GUID child under its TEMP parent.");
        }

        string statePath = Path.Combine(storageRoot, "state.json");
        string sourcePath = Path.Combine(storageRoot, "fixture-source.bin");
        MainWindow? window = null;
        object result;
        bool ok = false;
        try
        {
            Directory.CreateDirectory(storageRoot);
            File.WriteAllBytes(sourcePath, [1, 3, 5, 7, 9, 11]);
            string sourceBefore = StyleStateSmokeFingerprint(sourcePath);

            ViewerState compatible = CreateCompatibleStyleStateFixture();
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    compatible,
                    new JsonSerializerOptions { WriteIndented = true }));

            window = new MainWindow();
            window.FlushStateForSmoke();
            ViewerState? roundTripped = ReadStyleStateFixture(statePath);
            JsonElement videoObject = default;
            JsonElement videoArray = default;
            JsonElement i2iScalar = default;
            bool compatibleUnknownFieldsPreserved =
                roundTripped?.VideoStyles?.SingleOrDefault()?.ExtensionData
                    ?.TryGetValue("FutureVideoObject", out videoObject) == true
                && videoObject.ValueKind == JsonValueKind.Object
                && videoObject.GetProperty("mode").GetString() == "cinematic"
                && roundTripped.VideoStyles[0].ExtensionData
                    ?.TryGetValue("FutureVideoArray", out videoArray) == true
                && videoArray.ValueKind == JsonValueKind.Array
                && videoArray.GetArrayLength() == 3
                && roundTripped.I2iEditStyles?.SingleOrDefault()?.ExtensionData
                    ?.TryGetValue("FutureI2iScalar", out i2iScalar) == true
                && i2iScalar.ValueKind == JsonValueKind.Number
                && i2iScalar.GetInt32() == 17;

            ViewerState latest = roundTripped
                ?? throw new InvalidOperationException(
                    "The compatible Style state did not round-trip.");
            VideoStyleState latestVideoStyle = latest.VideoStyles
                ?.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "The compatible Video Style was unavailable.");
            latestVideoStyle.ExtensionData ??=
                new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            latestVideoStyle.ExtensionData["ConcurrentVideoScalar"] =
                JsonSerializer.SerializeToElement(true);
            I2iEditStyleState latestI2iStyle = latest.I2iEditStyles
                ?.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "The compatible I2I Style was unavailable.");
            latestI2iStyle.ExtensionData ??=
                new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            latestI2iStyle.ExtensionData["ConcurrentI2iObject"] =
                JsonSerializer.SerializeToElement(new
                {
                    mode = "semantic-v2",
                    weights = new[] { 0.25, 0.75 },
                });
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    latest,
                    new JsonSerializerOptions { WriteIndented = true }));
            window.FlushStateForSmoke();

            ViewerState? afterConcurrentSave = ReadStyleStateFixture(statePath);
            JsonElement concurrentVideoScalar = default;
            JsonElement concurrentI2iObject = default;
            bool concurrentLatestUnknownFieldsPreserved =
                afterConcurrentSave?.VideoStyles?[0].ExtensionData
                    ?.TryGetValue(
                        "ConcurrentVideoScalar",
                        out concurrentVideoScalar) == true
                && concurrentVideoScalar.ValueKind == JsonValueKind.True
                && afterConcurrentSave.I2iEditStyles?[0].ExtensionData
                    ?.TryGetValue(
                        "ConcurrentI2iObject",
                        out concurrentI2iObject) == true
                && concurrentI2iObject.ValueKind == JsonValueKind.Object
                && concurrentI2iObject.GetProperty("mode").GetString()
                    == "semantic-v2"
                && concurrentI2iObject.GetProperty("weights").GetArrayLength()
                    == 2;

            ViewerState futureVideo = CreateCompatibleStyleStateFixture();
            futureVideo.VideoStyles![0].ModelId = "future-video-v4";
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    futureVideo,
                    new JsonSerializerOptions { WriteIndented = true }));
            byte[] futureVideoBefore = File.ReadAllBytes(statePath);
            DateTime futureVideoWriteTimeBefore = File.GetLastWriteTimeUtc(
                statePath);
            window.FlushStateForSmoke();
            bool unsupportedFutureVideoProtected =
                File.ReadAllBytes(statePath).SequenceEqual(futureVideoBefore)
                && File.GetLastWriteTimeUtc(statePath)
                    == futureVideoWriteTimeBefore;
            window.SuppressStatePersistence();
            window.Close();
            window = null;

            ViewerState futureI2i = CreateCompatibleStyleStateFixture();
            futureI2i.I2iEditStyles![0].OutfitMaskMode = "semantic-v4";
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    futureI2i,
                    new JsonSerializerOptions { WriteIndented = true }));
            byte[] futureI2iBefore = File.ReadAllBytes(statePath);
            DateTime futureI2iWriteTimeBefore = File.GetLastWriteTimeUtc(
                statePath);
            window = new MainWindow();
            window.FlushStateForSmoke();
            bool unsupportedFutureI2iProtected =
                File.ReadAllBytes(statePath).SequenceEqual(futureI2iBefore)
                && File.GetLastWriteTimeUtc(statePath)
                    == futureI2iWriteTimeBefore;
            string sourceAfter = StyleStateSmokeFingerprint(sourcePath);
            bool sourceUnchanged = string.Equals(
                sourceBefore,
                sourceAfter,
                StringComparison.Ordinal);

            window.SuppressStatePersistence();
            window.Close();
            window = null;

            ok = compatibleUnknownFieldsPreserved
                && concurrentLatestUnknownFieldsPreserved
                && unsupportedFutureVideoProtected
                && unsupportedFutureI2iProtected
                && sourceUnchanged;
            result = new
            {
                ok,
                compatibleUnknownFieldsPreserved,
                concurrentLatestUnknownFieldsPreserved,
                unsupportedFutureVideoProtected,
                unsupportedFutureI2iProtected,
                sourceUnchanged,
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                message = ex.ToString(),
            };
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
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        TryDeleteStyleStateSmokeStorage(storageRoot);
        Shutdown(ok ? 0 : 1);
    }

    private static ViewerState CreateCompatibleStyleStateFixture()
        => new()
        {
            Version = 2,
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
                    ExtensionData = new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal)
                    {
                        ["FutureVideoObject"] =
                            JsonSerializer.SerializeToElement(new
                            {
                                mode = "cinematic",
                            }),
                        ["FutureVideoArray"] =
                            JsonSerializer.SerializeToElement(
                                new[] { "slow", "stable", "locked" }),
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
                    ExtensionData = new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal)
                    {
                        ["FutureI2iScalar"] =
                            JsonSerializer.SerializeToElement(17),
                    },
                },
            ],
            SelectedI2iEditStyleName = "Wardrobe edit",
        };

    private static ViewerState? ReadStyleStateFixture(string path)
        => JsonSerializer.Deserialize<ViewerState>(File.ReadAllText(path));

    private static string StyleStateSmokeFingerprint(string path)
    {
        FileInfo info = new(path);
        return string.Join(
            ":",
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static void TryDeleteStyleStateSmokeStorage(string storageRoot)
    {
        try
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            string expectedPrefix = Path.Combine(
                    tempRoot,
                    "photoviewer-wpf-automation")
                + Path.DirectorySeparatorChar;
            string fullStorageRoot = Path.GetFullPath(storageRoot);
            if (fullStorageRoot.StartsWith(
                    expectedPrefix,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    fullStorageRoot,
                    tempRoot,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullStorageRoot))
            {
                Directory.Delete(fullStorageRoot, recursive: true);
            }
        }
        catch
        {
            // The verifier owns its isolated TEMP fixture and can retry cleanup.
        }
    }

    private static string RequireStyleStateSmokeTempPath(
        string candidate,
        string description)
    {
        string fullPath = Path.GetFullPath(candidate);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string tempPrefix = tempRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                tempPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The Style state smoke {description} path must stay under TEMP.");
        }

        return fullPath;
    }
}
