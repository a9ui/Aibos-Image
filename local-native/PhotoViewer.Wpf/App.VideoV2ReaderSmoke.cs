using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private const int VideoV2ReaderFixtureBytes = 8_634;
    private const string VideoV2ReaderFixtureSha256 =
        "96c2e817c0dc7e0ee2b2d4865351d7d86894c7cb4ff6d084e8c440e3bd928002";

    private void CaptureVideoV2ReaderSmoke(
        string resultPath,
        IReadOnlyList<string> args)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string? contractArgument = ArgValue(args.ToArray(), "--contract");
        string? mediaFixtureArgument =
            ArgValue(args.ToArray(), "--media-fixture-base64");
        string smokeRoot = Directory.CreateTempSubdirectory(
            "photoviewer-wpf-video-v2-reader-").FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string exifSourceRoot = Path.Combine(smokeRoot, "exif-source");
        string outputRoot = Path.Combine(smokeRoot, "managed-output");
        string jobsPath = Path.Combine(smokeRoot, "shared", "enhance", "jobs.json");
        string sourcePath = Path.Combine(sourceRoot, "source.png");
        string statePath = Path.Combine(smokeRoot, "state.json");
        string favoritesPath = Path.Combine(smokeRoot, "favorites.json");
        string seenPath = Path.Combine(smokeRoot, "seen.json");
        string recentPath = Path.Combine(smokeRoot, "recent.json");
        string searchHistoryPath = Path.Combine(
            smokeRoot,
            "search-history.json");
        string settingsPath = Path.Combine(smokeRoot, "settings.json");
        string albumsPath = Path.Combine(smokeRoot, "albums.json");
        string metadataIndexDirectory = Path.Combine(
            smokeRoot,
            "metadata-index");
        string? previousStatePath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_STATE_PATH");
        string? previousFavoritesPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_FAVORITES_PATH");
        string? previousSeenPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SEEN_PATH");
        string? previousRecentPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_RECENT_PATH");
        string? previousSearchHistoryPath =
            Environment.GetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH");
        string? previousSettingsPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SETTINGS_PATH");
        string? previousAlbumsPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ALBUMS_PATH");
        string? previousMetadataIndexDirectory =
            Environment.GetEnvironmentVariable(
                "PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY");
        string? previousJobsPath =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH");
        string? previousOutputRoot =
            Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT");
        string? previousSharedOutputRoot =
            Environment.GetEnvironmentVariable("PVU_ENHANCE_OUTPUT_ROOT");
        MainWindow? window = null;
        int mutationRequests = 0;
        object result;
        bool ok = false;

        try
        {
            if (string.IsNullOrWhiteSpace(contractArgument))
                throw new InvalidDataException("--contract is required.");
            if (string.IsNullOrWhiteSpace(mediaFixtureArgument))
                throw new InvalidDataException(
                    "--media-fixture-base64 is required.");
            string contractPath = Path.GetFullPath(contractArgument);
            string mediaFixturePath = Path.GetFullPath(mediaFixtureArgument);
            byte[] contractBefore = File.ReadAllBytes(contractPath);
            byte[] mediaFixtureBefore = File.ReadAllBytes(mediaFixturePath);
            string mediaFixtureText = Encoding.ASCII.GetString(
                mediaFixtureBefore);
            byte[] mediaFixtureBytes = Convert.FromBase64String(
                string.Concat(mediaFixtureText.Where(
                    static character => !char.IsWhiteSpace(character))));
            string mediaFixtureSha256 = Convert.ToHexString(
                    SHA256.HashData(mediaFixtureBytes))
                .ToLowerInvariant();
            bool mediaFixtureExact = mediaFixtureBytes.Length
                    == VideoV2ReaderFixtureBytes
                && string.Equals(
                    mediaFixtureSha256,
                    VideoV2ReaderFixtureSha256,
                    StringComparison.Ordinal)
                && ContainsAscii(mediaFixtureBytes, "avc1")
                && ContainsAscii(mediaFixtureBytes, "mp4a");
            if (!mediaFixtureExact)
                throw new InvalidDataException(
                    "The synthetic H.264/AAC media fixture is not the pinned fixture.");
            string contractText = Encoding.UTF8.GetString(contractBefore);
            using JsonDocument canonicalDocument = JsonDocument.Parse(contractBefore);
            JsonElement canonical = canonicalDocument.RootElement;
            JsonElement fixture = canonical.GetProperty("readerFixture");
            byte[] sourceBytes = Convert.FromBase64String(
                fixture
                    .GetProperty("syntheticSourcePngBase64")
                    .GetString()!);
            int expectedSourceByteLength = fixture
                .GetProperty("syntheticSourceByteLength")
                .GetInt32();
            int expectedSourceWidth = fixture
                .GetProperty("syntheticSourceWidth")
                .GetInt32();
            int expectedSourceHeight = fixture
                .GetProperty("syntheticSourceHeight")
                .GetInt32();
            string expectedSourceSha = fixture
                .GetProperty("syntheticSourceSha256")
                .GetString()!;
            string sourceSha = Convert.ToHexString(SHA256.HashData(sourceBytes))
                .ToLowerInvariant();
            JsonElement canonicalValidJob = fixture
                .GetProperty("jobs")
                .EnumerateArray()
                .Single(job => job.GetProperty("id").GetString() == "valid-h3-video");
            long sourceMtimeMs = canonicalValidJob
                .GetProperty("sourceSignature")
                .GetProperty("mtimeMs")
                .GetInt64();
            bool contractIdentity = canonical.GetProperty("schemaVersion").GetInt32() == 2
                && canonical.GetProperty("contractId").GetString() == "PV-ENHANCE-VIDEO-002"
                && canonical.GetProperty("protocol").GetString()
                    == "aibos.enhancement-video/v2";
            bool sourceContract = sourceBytes.Length == expectedSourceByteLength
                && sourceBytes.Length == canonicalValidJob
                    .GetProperty("sourceSignature")
                    .GetProperty("size")
                    .GetInt64()
                && string.Equals(sourceSha, expectedSourceSha, StringComparison.Ordinal)
                && string.Equals(
                    sourceSha,
                    canonicalValidJob.GetProperty("sourceSha256").GetString(),
                    StringComparison.Ordinal);

            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
            string videosRoot = Path.Combine(outputRoot, "Videos");
            Directory.CreateDirectory(videosRoot);
            string corruptVideoPath = Path.Combine(
                videosRoot,
                "synthetic-corrupt-video.mp4");
            File.WriteAllBytes(
                corruptVideoPath,
                [0, 0, 0, 12, 98, 97, 100, 33]);
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.SetLastWriteTimeUtc(
                sourcePath,
                DateTimeOffset.FromUnixTimeMilliseconds(sourceMtimeMs).UtcDateTime);
            ImageDimensions? sourceDimensions = PhotoViewer.Wpf.MainWindow
                .ReadBitmapDimensionsForSmoke(sourcePath);
            bool sourceDimensionsExact = sourceDimensions is ImageDimensions dimensions
                && dimensions.Width == expectedSourceWidth
                && dimensions.Height == expectedSourceHeight;
            File.WriteAllText(statePath, "{}", new UTF8Encoding(false));
            File.WriteAllText(favoritesPath, "{}", new UTF8Encoding(false));
            File.WriteAllText(
                seenPath,
                JsonSerializer.Serialize(
                    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Path.GetFullPath(sourcePath)] = true,
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(recentPath, "{}", new UTF8Encoding(false));
            File.WriteAllText(searchHistoryPath, "{}", new UTF8Encoding(false));
            File.WriteAllText(settingsPath, "{}", new UTF8Encoding(false));
            File.WriteAllText(albumsPath, "{}", new UTF8Encoding(false));
            Directory.CreateDirectory(metadataIndexDirectory);
            File.WriteAllText(
                Path.Combine(
                    Path.GetDirectoryName(jobsPath)!,
                    SharedDataRootActivation.EnhancementOutputRootConfigFileName),
                outputRoot,
                new UTF8Encoding(false));

            string materializedContractText = contractText
                .Replace(
                    "${OUTPUT_ROOT}",
                    Path.GetFullPath(outputRoot).Replace('\\', '/'),
                    StringComparison.Ordinal)
                .Replace(
                    "${ROOT}",
                    Path.GetFullPath(sourceRoot).Replace('\\', '/'),
                    StringComparison.Ordinal);
            using JsonDocument materializedContract =
                JsonDocument.Parse(materializedContractText);
            JsonElement readerFixture = materializedContract.RootElement
                .GetProperty("readerFixture");
            JsonElement canvasMutationFixture = readerFixture
                .GetProperty("canvasPolicyMutationFixture");
            string canvasMutationId = canvasMutationFixture
                .GetProperty("id")
                .GetString()!;
            JsonArray jobNodes = JsonNode.Parse(
                    readerFixture.GetProperty("jobs").GetRawText())!
                .AsArray();
            JsonObject outOfPolicyJob = JsonNode.Parse(
                    jobNodes
                        .Single(node => node!["id"]!.GetValue<string>()
                            == "valid-h3-video")!
                        .ToJsonString())!
                .AsObject();
            outOfPolicyJob["id"] = canvasMutationId;
            outOfPolicyJob["status"] = "queued";
            outOfPolicyJob["progress"] = 0;
            outOfPolicyJob["outputPath"] = null;
            JsonObject outOfPolicyVideo = outOfPolicyJob["video"]!.AsObject();
            JsonObject outOfPolicyEffective = outOfPolicyVideo["effective"]!
                .AsObject();
            outOfPolicyEffective["width"] = canvasMutationFixture
                .GetProperty("effectiveWidth")
                .GetInt32();
            outOfPolicyEffective["height"] = canvasMutationFixture
                .GetProperty("effectiveHeight")
                .GetInt32();
            using (JsonDocument outOfPolicyVideoDocument = JsonDocument.Parse(
                outOfPolicyVideo.ToJsonString()))
            {
                string outOfPolicyHash = PhotoViewer.Wpf.MainWindow
                    .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                        outOfPolicyVideoDocument.RootElement);
                string expectedOutOfPolicyHash = canvasMutationFixture
                    .GetProperty("expectedPresetHash")
                    .GetString()!;
                if (!string.Equals(
                        outOfPolicyHash[..12],
                        expectedOutOfPolicyHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The out-of-policy canvas fixture hash drifted.");
                }
                outOfPolicyJob["presetHash"] = expectedOutOfPolicyHash;
            }
            jobNodes.Add(outOfPolicyJob);
            string jobsRaw = jobNodes.ToJsonString();
            string jobStoreText =
                $"{{\"version\":1,\"jobs\":{jobsRaw},\"futureRootField\":{{\"keep\":true}}}}";
            File.WriteAllText(jobsPath, jobStoreText, new UTF8Encoding(false));
            byte[] jobsBefore = File.ReadAllBytes(jobsPath);
            byte[] sourceBefore = File.ReadAllBytes(sourcePath);
            byte[] stateBefore = File.ReadAllBytes(statePath);
            byte[] favoritesBefore = File.ReadAllBytes(favoritesPath);
            byte[] seenBefore = File.ReadAllBytes(seenPath);
            byte[] recentBefore = File.ReadAllBytes(recentPath);
            byte[] searchHistoryBefore = File.ReadAllBytes(searchHistoryPath);
            byte[] settingsBefore = File.ReadAllBytes(settingsPath);
            byte[] albumsBefore = File.ReadAllBytes(albumsPath);

            using JsonDocument jobsDocument = JsonDocument.Parse(jobsBefore);
            JsonElement[] jobs = jobsDocument.RootElement
                .GetProperty("jobs")
                .EnumerateArray()
                .Select(static job => job.Clone())
                .ToArray();
            JsonElement validJob = jobs.Single(job =>
                job.GetProperty("id").GetString() == "valid-h3-video");
            JsonElement validVideo = validJob.GetProperty("video");
            JsonElement exactIdentityJob = validJob.Clone();
            JsonElement envelopeIdentityMutationJob =
                BuildMiniMaxH3IdentityMutationForSmoke(
                    validJob,
                    seed: checked(validVideo.GetProperty("seed").GetInt32() + 1),
                    prompt: null);
            const string refreshedIdentityPrompt =
                "A deliberate turn under refreshed afternoon light.";
            JsonElement snapshotIdentityMutationJob =
                BuildMiniMaxH3IdentityMutationForSmoke(
                    validJob,
                    seed: null,
                    prompt: refreshedIdentityPrompt);
            string expectedIdentityPrompt = validVideo
                .GetProperty("requested")
                .GetProperty("prompt")
                .GetString()!;
            bool exactIdentityCompared = PhotoViewer.Wpf.MainWindow
                .TryCompareVideoWorkspaceImmutableIdentityForSmoke(
                    validJob,
                    exactIdentityJob,
                    out bool exactSameIdentity,
                    out string exactLeftFingerprint,
                    out string exactRightFingerprint,
                    out string? exactLeftPrompt,
                    out string? exactRightPrompt);
            bool compactVideoMutationProbeExtracted = exactIdentityCompared
                && IsLowerHexSha256(exactLeftFingerprint)
                && IsLowerHexSha256(exactRightFingerprint);
            bool exactVideoProbeIdentityStable = exactIdentityCompared
                && exactSameIdentity
                && string.Equals(
                    exactLeftFingerprint,
                    exactRightFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    exactLeftPrompt,
                    expectedIdentityPrompt,
                    StringComparison.Ordinal)
                && string.Equals(
                    exactRightPrompt,
                    expectedIdentityPrompt,
                    StringComparison.Ordinal);
            bool envelopeIdentityCompared = PhotoViewer.Wpf.MainWindow
                .TryCompareVideoWorkspaceImmutableIdentityForSmoke(
                    validJob,
                    envelopeIdentityMutationJob,
                    out bool envelopeSameIdentity,
                    out string envelopeLeftFingerprint,
                    out string envelopeRightFingerprint,
                    out string? envelopeLeftPrompt,
                    out string? envelopeRightPrompt);
            bool fullVideoEnvelopeFingerprintGuardsIdentity =
                envelopeIdentityCompared
                && !envelopeSameIdentity
                && IsLowerHexSha256(envelopeLeftFingerprint)
                && IsLowerHexSha256(envelopeRightFingerprint)
                && !string.Equals(
                    envelopeLeftFingerprint,
                    envelopeRightFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    envelopeLeftPrompt,
                    expectedIdentityPrompt,
                    StringComparison.Ordinal)
                && string.Equals(
                    envelopeRightPrompt,
                    expectedIdentityPrompt,
                    StringComparison.Ordinal);
            bool snapshotIdentityCompared = PhotoViewer.Wpf.MainWindow
                .TryCompareVideoWorkspaceImmutableIdentityForSmoke(
                    validJob,
                    snapshotIdentityMutationJob,
                    out bool snapshotSameIdentity,
                    out string snapshotLeftFingerprint,
                    out string snapshotRightFingerprint,
                    out string? snapshotLeftPrompt,
                    out string? snapshotRightPrompt);
            bool h3SnapshotDifferenceGuardsIdentity = snapshotIdentityCompared
                && !snapshotSameIdentity
                && IsLowerHexSha256(snapshotLeftFingerprint)
                && IsLowerHexSha256(snapshotRightFingerprint)
                && !string.Equals(
                    snapshotLeftFingerprint,
                    snapshotRightFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    snapshotLeftPrompt,
                    expectedIdentityPrompt,
                    StringComparison.Ordinal)
                && string.Equals(
                    snapshotRightPrompt,
                    refreshedIdentityPrompt,
                    StringComparison.Ordinal);
            JsonElement[] stableHashVectors = readerFixture
                .GetProperty("stableSnapshotHashVectors")
                .EnumerateArray()
                .Select(static vector => vector.Clone())
                .ToArray();
            JsonElement asciiHashVector = stableHashVectors.Single(vector =>
                vector.GetProperty("id").GetString() == "ascii-prompt");
            JsonElement japaneseHashVector = stableHashVectors.Single(vector =>
                vector.GetProperty("id").GetString()
                    == "utf8-japanese-prompt");
            JsonElement portraitHashVector = stableHashVectors.Single(vector =>
                vector.GetProperty("id").GetString()
                    == "portrait-source-aspect");
            bool canvasPolicyVectorsExact = readerFixture
                .GetProperty("canvasPolicyVectors")
                .EnumerateArray()
                .All(vector =>
                {
                    (int width, int height) = PhotoViewer.Wpf.MainWindow
                        .NormalizeMiniMaxH3VideoCanvasForSmoke(
                            vector.GetProperty("sourceWidth").GetInt32(),
                            vector.GetProperty("sourceHeight").GetInt32());
                    return width
                            == vector.GetProperty("effectiveWidth").GetInt32()
                        && height
                            == vector.GetProperty("effectiveHeight").GetInt32();
                });
            string asciiExpectedPresetHash = asciiHashVector
                .GetProperty("expectedPresetHash")
                .GetString()!;
            string japaneseExpectedPresetHash = japaneseHashVector
                .GetProperty("expectedPresetHash")
                .GetString()!;
            string portraitExpectedPresetHash = portraitHashVector
                .GetProperty("expectedPresetHash")
                .GetString()!;
            string validHash = PhotoViewer.Wpf.MainWindow
                .ComputeMiniMaxH3VideoSnapshotHashForSmoke(validVideo);
            string validPresetHash = validJob.GetProperty("presetHash").GetString()!;
            string expectedFileName = PhotoViewer.Wpf.MainWindow.BuildVideoOutputFileNameForSmoke(
                validJob.GetProperty("id").GetString()!,
                validJob.GetProperty("sourcePath").GetString()!,
                validJob.GetProperty("sourceSha256").GetString()!,
                validJob.GetProperty("presetId").GetString()!,
                validPresetHash);
            string validOutputPath = validJob.GetProperty("outputPath").GetString()!;
            string expectedVideoRoot = Path.GetFullPath(
                Path.Combine(outputRoot, "Videos"));
            string resolvedValidOutputPath = Path.GetFullPath(validOutputPath);
            string? resolvedValidOutputFolder =
                Path.GetDirectoryName(resolvedValidOutputPath);
            bool stableHashExact = validPresetHash == validHash[..12]
                && validPresetHash == asciiExpectedPresetHash
                && validVideo.GetProperty("requested").GetProperty("prompt").GetString()
                    == asciiHashVector.GetProperty("prompt").GetString()
                && validVideo.GetProperty("seed").GetInt32()
                    == asciiHashVector.GetProperty("seed").GetInt32();
            JsonObject longVideoNode = JsonNode.Parse(validVideo.GetRawText())!
                .AsObject();
            longVideoNode["requested"]!.AsObject()["profileId"] =
                "minimax-h3-hq-12s-v1";
            longVideoNode["effective"]!.AsObject()["frameCount"] = 294;
            JsonObject longDelivery = longVideoNode["delivery"]!.AsObject();
            longDelivery["frameCount"] = 294;
            longDelivery["durationSeconds"] = 12.25;
            using JsonDocument longVideo = JsonDocument.Parse(
                longVideoNode.ToJsonString());
            bool longSnapshotExact = PhotoViewer.Wpf.MainWindow
                .IsExactMiniMaxH3VideoSnapshotForSmoke(
                    longVideo.RootElement);
            bool ownedOutputName = resolvedValidOutputFolder is not null
                && string.Equals(
                    Path.GetDirectoryName(resolvedValidOutputFolder),
                    expectedVideoRoot,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileName(resolvedValidOutputFolder),
                    "2026-08-11",
                    StringComparison.Ordinal)
                && string.Equals(
                    Path.GetFileName(resolvedValidOutputPath),
                    expectedFileName,
                    StringComparison.OrdinalIgnoreCase);
            if (!ownedOutputName)
                throw new InvalidDataException(
                    "The canonical valid output escaped the TEMP managed Videos folder.");
            Directory.CreateDirectory(resolvedValidOutputFolder!);
            File.WriteAllBytes(resolvedValidOutputPath, mediaFixtureBytes);

            const int selectedVideoSteps = 7;
            JsonObject selectedStepsJobNode = JsonNode.Parse(
                    validJob.GetRawText())!
                .AsObject();
            const string selectedStepsJobId = "valid-h3-selected-steps";
            selectedStepsJobNode["id"] = selectedStepsJobId;
            JsonObject selectedStepsVideoNode = selectedStepsJobNode["video"]!
                .AsObject();
            JsonObject selectedStepsRequested = selectedStepsVideoNode["requested"]!
                .AsObject();
            selectedStepsRequested["profileId"] = "minimax-h3-hq-5s-v1";
            selectedStepsRequested["steps"] = selectedVideoSteps;
            selectedStepsVideoNode["effective"]!["steps"] = selectedVideoSteps;
            using (JsonDocument selectedStepsVideoDocument = JsonDocument.Parse(
                selectedStepsVideoNode.ToJsonString()))
            {
                selectedStepsJobNode["presetHash"] = PhotoViewer.Wpf.MainWindow
                    .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                        selectedStepsVideoDocument.RootElement)[..12];
            }
            string selectedStepsOutputFileName = PhotoViewer.Wpf.MainWindow
                .BuildVideoOutputFileNameForSmoke(
                    selectedStepsJobId,
                    selectedStepsJobNode["sourcePath"]!.GetValue<string>(),
                    selectedStepsJobNode["sourceSha256"]!.GetValue<string>(),
                    selectedStepsJobNode["presetId"]!.GetValue<string>(),
                    selectedStepsJobNode["presetHash"]!.GetValue<string>());
            const string selectedStepsExpectedOutputFileName =
                "valid-h3-selected-steps__source__9be09b982e5cb105__"
                + "minimax-h3-i2v-preview-v1__e832f2b2d204.mp4";
            if (!string.Equals(
                    selectedStepsOutputFileName,
                    selectedStepsExpectedOutputFileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The selected-steps output identity fixture drifted.");
            }
            string selectedStepsOutputPath = Path.Combine(
                outputRoot,
                "Videos",
                selectedStepsExpectedOutputFileName);
            selectedStepsJobNode["outputPath"] = selectedStepsOutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(
                selectedStepsOutputPath)!);
            File.WriteAllBytes(selectedStepsOutputPath, mediaFixtureBytes);
            using JsonDocument selectedStepsJobDocument = JsonDocument.Parse(
                selectedStepsJobNode.ToJsonString());
            JsonElement selectedStepsJob =
                selectedStepsJobDocument.RootElement.Clone();

            string legacyManagedSourcePath = Path.Combine(
                outputRoot,
                "Edited",
                "legacy-managed-source.png");
            Directory.CreateDirectory(Path.GetDirectoryName(
                legacyManagedSourcePath)!);
            File.WriteAllBytes(legacyManagedSourcePath, sourceBytes);
            File.SetLastWriteTimeUtc(
                legacyManagedSourcePath,
                DateTimeOffset.FromUnixTimeMilliseconds(sourceMtimeMs + 2_000L)
                    .UtcDateTime);
            var legacyManagedSourceInfo = new FileInfo(
                legacyManagedSourcePath);
            long legacyManagedSourceMtimeMs = new DateTimeOffset(
                    legacyManagedSourceInfo.LastWriteTimeUtc)
                .ToUnixTimeMilliseconds();
            const string legacyManagedJobId = "valid-h3-legacy-managed-source";
            JsonObject legacyManagedJobNode = JsonNode.Parse(
                    validJob.GetRawText())!
                .AsObject();
            legacyManagedJobNode["id"] = legacyManagedJobId;
            legacyManagedJobNode["sourceId"] = Path.Combine(
                sourceRoot,
                "missing-original.png");
            legacyManagedJobNode["sourcePath"] = legacyManagedSourcePath;
            legacyManagedJobNode["sourceSignature"] = new JsonObject
            {
                ["size"] = legacyManagedSourceInfo.Length,
                ["mtimeMs"] = legacyManagedSourceMtimeMs,
            };
            string legacyManagedOutputFileName = PhotoViewer.Wpf.MainWindow
                .BuildVideoOutputFileNameForSmoke(
                    legacyManagedJobId,
                    legacyManagedSourcePath,
                    sourceSha,
                    validJob.GetProperty("presetId").GetString()!,
                    validPresetHash);
            string legacyManagedOutputPath = Path.Combine(
                outputRoot,
                "Videos",
                "2026-08-20",
                legacyManagedOutputFileName);
            legacyManagedJobNode["outputPath"] = legacyManagedOutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(
                legacyManagedOutputPath)!);
            File.WriteAllBytes(legacyManagedOutputPath, mediaFixtureBytes);
            using JsonDocument legacyManagedJobDocument = JsonDocument.Parse(
                legacyManagedJobNode.ToJsonString());
            JsonElement legacyManagedJob =
                legacyManagedJobDocument.RootElement.Clone();
            string displayedManagedSourcePath = Path.Combine(
                outputRoot,
                "Photorealized",
                "displayed-managed-source.png");
            Directory.CreateDirectory(Path.GetDirectoryName(
                displayedManagedSourcePath)!);
            File.WriteAllBytes(displayedManagedSourcePath, sourceBytes);
            File.SetLastWriteTimeUtc(
                displayedManagedSourcePath,
                DateTimeOffset.FromUnixTimeMilliseconds(sourceMtimeMs + 4_000L)
                    .UtcDateTime);
            var displayedManagedSourceInfo = new FileInfo(
                displayedManagedSourcePath);
            long displayedManagedSourceMtimeMs = new DateTimeOffset(
                    displayedManagedSourceInfo.LastWriteTimeUtc)
                .ToUnixTimeMilliseconds();
            const string displayedManagedJobId =
                "valid-h3-displayed-managed-source";
            JsonObject displayedManagedJobNode = JsonNode.Parse(
                    validJob.GetRawText())!
                .AsObject();
            displayedManagedJobNode["id"] = displayedManagedJobId;
            displayedManagedJobNode["sourceId"] = sourcePath;
            displayedManagedJobNode["sourcePath"] = displayedManagedSourcePath;
            displayedManagedJobNode.Remove("sourceProducerJobId");
            displayedManagedJobNode["sourceSignature"] = new JsonObject
            {
                ["size"] = displayedManagedSourceInfo.Length,
                ["mtimeMs"] = displayedManagedSourceMtimeMs,
            };
            string displayedManagedOutputFileName = PhotoViewer.Wpf.MainWindow
                .BuildVideoOutputFileNameForSmoke(
                    displayedManagedJobId,
                    displayedManagedSourcePath,
                    sourceSha,
                    validJob.GetProperty("presetId").GetString()!,
                    validPresetHash);
            string displayedManagedOutputPath = Path.Combine(
                outputRoot,
                "Videos",
                "2026-08-23",
                displayedManagedOutputFileName);
            displayedManagedJobNode["outputPath"] = displayedManagedOutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(
                displayedManagedOutputPath)!);
            File.WriteAllBytes(displayedManagedOutputPath, mediaFixtureBytes);
            using JsonDocument displayedManagedJobDocument = JsonDocument.Parse(
                displayedManagedJobNode.ToJsonString());
            JsonElement displayedManagedJob =
                displayedManagedJobDocument.RootElement.Clone();
            JsonObject staleProducerManagedJobNode = JsonNode.Parse(
                    legacyManagedJob.GetRawText())!
                .AsObject();
            staleProducerManagedJobNode["sourceProducerJobId"] =
                "missing-producer-job";
            using JsonDocument staleProducerManagedJobDocument =
                JsonDocument.Parse(staleProducerManagedJobNode.ToJsonString());
            JsonElement staleProducerManagedJob =
                staleProducerManagedJobDocument.RootElement.Clone();
            byte[] legacyManagedSourceBefore = File.ReadAllBytes(
                legacyManagedSourcePath);
            byte[] legacyManagedOutputBefore = File.ReadAllBytes(
                legacyManagedOutputPath);
            byte[] displayedManagedSourceBefore = File.ReadAllBytes(
                displayedManagedSourcePath);
            byte[] displayedManagedOutputBefore = File.ReadAllBytes(
                displayedManagedOutputPath);

            const int exifStoredWidth = 96;
            const int exifStoredHeight = 64;
            (int exifWriterWidth, int exifWriterHeight) =
                PhotoViewer.Wpf.MainWindow.NormalizeMiniMaxH3VideoCanvasForSmoke(
                    exifStoredHeight,
                    exifStoredWidth);
            (int exifUnswappedWidth, int exifUnswappedHeight) =
                PhotoViewer.Wpf.MainWindow.NormalizeMiniMaxH3VideoCanvasForSmoke(
                    exifStoredWidth,
                    exifStoredHeight);
            bool exifWriterCanvasExact = exifWriterWidth == 512
                && exifWriterHeight == 768
                && exifUnswappedWidth == 768
                && exifUnswappedHeight == 512;
            bool exifOrientationMetadataExact = true;
            var exifValidJobs = new List<JsonElement>();
            var exifUnswappedJobs = new List<JsonElement>();
            var exifSourceBytesBefore =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var exifOutputBytesBefore =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(exifSourceRoot);
            foreach (ushort orientation in new ushort[] { 5, 6, 7, 8 })
            {
                string exifSourcePath = Path.Combine(
                    exifSourceRoot,
                    $"synthetic-exif-orientation-{orientation}.jpg");
                byte[] exifSourceBytes = CreateSyntheticOrientationJpeg(
                    exifStoredWidth,
                    exifStoredHeight,
                    orientation);
                File.WriteAllBytes(exifSourcePath, exifSourceBytes);
                File.SetLastWriteTimeUtc(
                    exifSourcePath,
                    DateTimeOffset.FromUnixTimeMilliseconds(
                            sourceMtimeMs + orientation * 1_000L)
                        .UtcDateTime);
                var exifSourceInfo = new FileInfo(exifSourcePath);
                long exifMtimeMs = new DateTimeOffset(
                        exifSourceInfo.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds();
                string exifSourceSha = Convert.ToHexString(
                        SHA256.HashData(exifSourceBytes))
                    .ToLowerInvariant();
                exifSourceBytesBefore.Add(exifSourcePath, exifSourceBytes);
                exifOrientationMetadataExact &= TryReadSyntheticJpegOrientation(
                        exifSourcePath,
                        out int decodedWidth,
                        out int decodedHeight,
                        out ushort decodedOrientation)
                    && decodedWidth == exifStoredWidth
                    && decodedHeight == exifStoredHeight
                    && decodedOrientation == orientation;

                JsonElement exifValidJob = BuildSyntheticExifVideoJob(
                    validJob,
                    $"valid-h3-exif-{orientation}",
                    exifSourcePath,
                    exifSourceInfo.Length,
                    exifMtimeMs,
                    exifSourceSha,
                    exifWriterWidth,
                    exifWriterHeight,
                    outputRoot,
                    mediaFixtureBytes);
                exifValidJobs.Add(exifValidJob);
                string exifValidOutput = exifValidJob
                    .GetProperty("outputPath")
                    .GetString()!;
                exifOutputBytesBefore.Add(
                    exifValidOutput,
                    File.ReadAllBytes(exifValidOutput));

                JsonElement exifUnswappedJob = BuildSyntheticExifVideoJob(
                    validJob,
                    $"invalid-h3-exif-unswapped-{orientation}",
                    exifSourcePath,
                    exifSourceInfo.Length,
                    exifMtimeMs,
                    exifSourceSha,
                    exifUnswappedWidth,
                    exifUnswappedHeight,
                    outputRoot,
                    mediaFixtureBytes);
                exifUnswappedJobs.Add(exifUnswappedJob);
                string exifUnswappedOutput = exifUnswappedJob
                    .GetProperty("outputPath")
                    .GetString()!;
                exifOutputBytesBefore.Add(
                    exifUnswappedOutput,
                    File.ReadAllBytes(exifUnswappedOutput));
            }

            string japanesePrompt = japaneseHashVector
                .GetProperty("prompt")
                .GetString()!;
            string originalPromptJson = JsonSerializer.Serialize(
                "A gentle head turn in dawn light.");
            string japanesePromptJson = JsonSerializer.Serialize(japanesePrompt);
            string japaneseVideoJson = validVideo.GetRawText()
                .Replace(
                    originalPromptJson,
                    japanesePromptJson,
                    StringComparison.Ordinal);
            using JsonDocument japaneseVideoDocument =
                JsonDocument.Parse(japaneseVideoJson);
            string japaneseHash = PhotoViewer.Wpf.MainWindow
                .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                    japaneseVideoDocument.RootElement);
            bool japaneseHashInterop = japaneseHash[..12]
                    == japaneseExpectedPresetHash
                && japaneseVideoDocument.RootElement
                    .GetProperty("requested")
                    .GetProperty("prompt")
                    .GetString() == japanesePrompt
                && japaneseVideoDocument.RootElement.GetProperty("seed").GetInt32()
                    == japaneseHashVector.GetProperty("seed").GetInt32();

            string portraitPrompt = portraitHashVector
                .GetProperty("prompt")
                .GetString()!;
            JsonObject portraitVideoNode = JsonNode.Parse(validVideo.GetRawText())!
                .AsObject();
            portraitVideoNode["requested"]!["prompt"] = portraitPrompt;
            JsonObject portraitEffective = portraitVideoNode["effective"]!
                .AsObject();
            portraitEffective["width"] = portraitHashVector
                .GetProperty("effectiveWidth")
                .GetInt32();
            portraitEffective["height"] = portraitHashVector
                .GetProperty("effectiveHeight")
                .GetInt32();
            portraitEffective["positivePrompt"] = portraitPrompt;
            portraitVideoNode["seed"] = portraitHashVector
                .GetProperty("seed")
                .GetInt32();
            string portraitVideoJson = portraitVideoNode.ToJsonString();
            using JsonDocument portraitVideoDocument =
                JsonDocument.Parse(portraitVideoJson);
            string portraitHash = PhotoViewer.Wpf.MainWindow
                .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                    portraitVideoDocument.RootElement);
            bool portraitHashInterop = portraitHash[..12]
                    == portraitExpectedPresetHash
                && PhotoViewer.Wpf.MainWindow.IsExactMiniMaxH3VideoSnapshotForSmoke(
                    portraitVideoDocument.RootElement)
                && portraitVideoDocument.RootElement
                    .GetProperty("effective")
                    .GetProperty("width")
                    .GetInt32() == 512
                && portraitVideoDocument.RootElement
                    .GetProperty("effective")
                    .GetProperty("height")
                    .GetInt32() == 768;
            (int portraitWidth, int portraitHeight) = PhotoViewer.Wpf.MainWindow
                .NormalizeMiniMaxH3VideoCanvasForSmoke(
                    portraitHashVector.GetProperty("sourceWidth").GetInt32(),
                    portraitHashVector.GetProperty("sourceHeight").GetInt32());
            portraitHashInterop = portraitHashInterop
                && portraitWidth
                    == portraitHashVector.GetProperty("effectiveWidth").GetInt32()
                && portraitHeight
                    == portraitHashVector.GetProperty("effectiveHeight").GetInt32();

            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_STATE_PATH",
                statePath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_FAVORITES_PATH",
                favoritesPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SEEN_PATH",
                seenPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_RECENT_PATH",
                recentPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH",
                searchHistoryPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SETTINGS_PATH",
                settingsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ALBUMS_PATH",
                albumsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY",
                metadataIndexDirectory);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                jobsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                null);
            Environment.SetEnvironmentVariable("PVU_ENHANCE_OUTPUT_ROOT", null);
            window = HiddenWindow();
            window.SuppressStatePersistence();
            window.MarkSharedRecentFolderSetCommittedForSmoke(sourceRoot);
            window.ConfigureModalEnhancementForSmoke((request, _) =>
            {
                if (request.Method != HttpMethod.Get)
                    mutationRequests++;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound));
            });

            bool rerunPresentationExact = window
                .TryBuildMiniMaxH3VideoRerunForSmoke(
                    validJob,
                    out string[] rerunActionKinds,
                    out string rerunProfileId,
                    out int rerunNominalDurationSeconds,
                    out int rerunMaximumPixelArea,
                    out int rerunSteps,
                    out string rerunPrompt,
                    out string rerunSourceIdentity,
                    out string rerunDisplayPath,
                    out bool rerunUsesDisplayedFileDirectly,
                    out string rerunRequestJson)
                && rerunActionKinds.SequenceEqual(
                    new[]
                    {
                        "video-rerun-saved",
                        "video-edit-prompt",
                        "open-output",
                        "delete-output",
                    },
                    StringComparer.Ordinal)
                && rerunProfileId == "minimax-h3-hq-5s-v1"
                && rerunNominalDurationSeconds == 5
                && rerunMaximumPixelArea == 414720
                && rerunSteps == 20
                && rerunPrompt == "A gentle head turn in dawn light."
                && string.Equals(
                    rerunSourceIdentity,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    rerunDisplayPath,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && !rerunUsesDisplayedFileDirectly;
            bool rerunRequestExact = false;
            if (rerunPresentationExact)
            {
                using JsonDocument rerunRequest = JsonDocument.Parse(
                    rerunRequestJson);
                JsonElement root = rerunRequest.RootElement;
                JsonElement requested = root
                    .GetProperty("video")
                    .GetProperty("requested");
                rerunRequestExact = HasExactJsonProperties(
                        root,
                        "sourceId",
                        "operation",
                        "mediaKind",
                        "presetId",
                        "adapterId",
                        "video")
                    && HasExactJsonString(
                        root,
                        "sourceId",
                        sourcePath)
                    && HasExactJsonString(root, "operation", "video")
                    && HasExactJsonString(root, "mediaKind", "video")
                    && HasExactJsonString(
                        root,
                        "presetId",
                        "minimax-h3-i2v-preview-v1")
                    && HasExactJsonString(
                        root,
                        "adapterId",
                        "minimax-h3-local-v1")
                    && HasExactJsonProperties(
                        root.GetProperty("video"),
                        "requested")
                    && HasExactJsonProperties(
                        requested,
                        "profileId",
                        "prompt",
                        "steps",
                        "maximumPixelArea")
                    && HasExactJsonString(
                        requested,
                        "profileId",
                        rerunProfileId)
                    && HasExactJsonString(
                        requested,
                        "prompt",
                        rerunPrompt)
                    && HasExactJsonInt32(requested, "steps", rerunSteps)
                    && HasExactJsonInt32(
                        requested,
                        "maximumPixelArea",
                        rerunMaximumPixelArea);
            }

            bool legacyManagedRerunExact = window
                .TryBuildMiniMaxH3VideoRerunForSmoke(
                    legacyManagedJob,
                    out string[] legacyManagedActionKinds,
                    out string legacyManagedProfileId,
                    out int legacyManagedNominalDurationSeconds,
                    out int legacyManagedMaximumPixelArea,
                    out int legacyManagedSteps,
                    out string legacyManagedPrompt,
                    out string legacyManagedSourceIdentity,
                    out string legacyManagedDisplayPath,
                    out bool legacyManagedUsesDisplayedFileDirectly,
                    out string legacyManagedRequestJson)
                && legacyManagedActionKinds.SequenceEqual(
                    rerunActionKinds,
                    StringComparer.Ordinal)
                && legacyManagedProfileId == rerunProfileId
                && legacyManagedNominalDurationSeconds
                    == rerunNominalDurationSeconds
                && legacyManagedMaximumPixelArea == rerunMaximumPixelArea
                && legacyManagedSteps == rerunSteps
                && legacyManagedPrompt == rerunPrompt
                && legacyManagedUsesDisplayedFileDirectly
                && string.Equals(
                    legacyManagedSourceIdentity,
                    legacyManagedSourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    legacyManagedDisplayPath,
                    legacyManagedSourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && !File.Exists(legacyManagedJob
                    .GetProperty("sourceId")
                    .GetString()!);
            bool legacyManagedRerunRequestExact = false;
            if (legacyManagedRerunExact)
            {
                using JsonDocument legacyManagedRequest = JsonDocument.Parse(
                    legacyManagedRequestJson);
                JsonElement root = legacyManagedRequest.RootElement;
                JsonElement requested = root
                    .GetProperty("video")
                    .GetProperty("requested");
                legacyManagedRerunRequestExact = HasExactJsonProperties(
                        root,
                        "sourceId",
                        "operation",
                        "mediaKind",
                        "presetId",
                        "adapterId",
                        "video",
                        "sourceManagedOutputPath")
                    && HasExactJsonString(
                        root,
                        "sourceId",
                        legacyManagedSourcePath)
                    && HasExactJsonString(
                        root,
                        "sourceManagedOutputPath",
                        legacyManagedSourcePath)
                    && HasExactJsonProperties(
                        requested,
                        "profileId",
                        "prompt",
                        "steps",
                        "maximumPixelArea")
                    && HasExactJsonString(
                        requested,
                        "profileId",
                        rerunProfileId)
                    && HasExactJsonString(
                        requested,
                        "prompt",
                        rerunPrompt)
                    && HasExactJsonInt32(requested, "steps", rerunSteps)
                    && HasExactJsonInt32(
                        requested,
                        "maximumPixelArea",
                        rerunMaximumPixelArea);
            }
            bool staleManagedProducerIgnoredExact = window
                .TryBuildMiniMaxH3VideoRerunForSmoke(
                    staleProducerManagedJob,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out string staleManagedSourceIdentity,
                    out string staleManagedDisplayPath,
                    out bool staleManagedUsesDisplayedFileDirectly,
                    out string staleManagedRequestJson)
                && staleManagedUsesDisplayedFileDirectly
                && string.Equals(
                    staleManagedSourceIdentity,
                    legacyManagedSourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    staleManagedDisplayPath,
                    legacyManagedSourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    staleManagedRequestJson,
                    legacyManagedRequestJson,
                    StringComparison.Ordinal);
            bool displayedManagedWorkspaceSourceExact =
                !displayedManagedJob.TryGetProperty(
                    "sourceProducerJobId",
                    out _)
                && window.TryReadMiniMaxH3WorkspaceSourceForSmoke(
                    displayedManagedJob,
                    out string displayedManagedSourceVersion,
                    out string displayedManagedSourceName,
                    out string displayedManagedCanonicalInput)
                && displayedManagedSourceVersion == "実写版"
                && displayedManagedSourceName
                    == Path.GetFileName(displayedManagedSourcePath)
                && string.Equals(
                    displayedManagedCanonicalInput,
                    Path.GetFullPath(displayedManagedSourcePath),
                    StringComparison.OrdinalIgnoreCase);
            bool displayedManagedPlaybackExact = PhotoViewer.Wpf.MainWindow
                    .IsMiniMaxH3VideoMutationSafeForSmoke(displayedManagedJob)
                && window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                    displayedManagedJob,
                    out int displayedManagedWidth,
                    out int displayedManagedHeight,
                    out int displayedManagedPlaybackFps,
                    out int displayedManagedFrameCount,
                    out double displayedManagedDuration,
                    out bool displayedManagedAudio,
                    out _)
                && displayedManagedWidth == 864
                && displayedManagedHeight == 480
                && displayedManagedPlaybackFps == 24
                && displayedManagedFrameCount == 124
                && Math.Abs(
                    displayedManagedDuration - 124d / 24d) <= 1e-12
                && displayedManagedAudio;
            bool displayedManagedCatalogExact = window
                    .TryBuildMiniMaxH3ManagedVideoCatalogForSmoke(
                        displayedManagedJob,
                        out string displayedManagedResolvedSource,
                        out string[] displayedManagedCatalogAliases)
                && string.Equals(
                    displayedManagedResolvedSource,
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase)
                && displayedManagedCatalogAliases.Contains(
                    Path.GetFullPath(sourcePath),
                    StringComparer.OrdinalIgnoreCase)
                && displayedManagedCatalogAliases.Contains(
                    Path.GetFullPath(displayedManagedSourcePath),
                    StringComparer.OrdinalIgnoreCase);

            bool exifWorkspaceReaderExact = exifValidJobs.All(job =>
                PhotoViewer.Wpf.MainWindow
                    .IsMiniMaxH3VideoMutationSafeForSmoke(job)
                && window.TryReadMiniMaxH3WorkspacePresentationForSmoke(
                    job,
                    out string operation,
                    out string presetSummary,
                    out bool mutationSafe,
                    out bool canUseOutput)
                && operation == "video"
                && presetSummary.Contains(
                    "MiniMax H3 Preview",
                    StringComparison.Ordinal)
                && mutationSafe
                && canUseOutput);
            bool exifPlaybackReaderExact = exifValidJobs.All(job =>
                window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                    job,
                    out int width,
                    out int height,
                    out int fps,
                    out int frames,
                    out double duration,
                    out bool hasAudio,
                    out _)
                && width == exifWriterWidth
                && height == exifWriterHeight
                && fps == 24
                && frames == 124
                && Math.Abs(duration - 124d / 24d) <= 1e-12
                && hasAudio);
            bool exifUnswappedCanvasProtected = exifUnswappedJobs.All(job =>
                PhotoViewer.Wpf.MainWindow
                    .IsMiniMaxH3VideoMutationSafeForSmoke(job)
                && window.TryReadMiniMaxH3WorkspacePresentationForSmoke(
                    job,
                    out string operation,
                    out _,
                    out bool mutationSafe,
                    out bool canUseOutput)
                && operation == "video"
                && !mutationSafe
                && !canUseOutput
                && !window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                    job,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _));

            string[] expectedMutationSafe = readerFixture
                .GetProperty("expectedMutationSafeIds")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            string[] expectedProtected = readerFixture
                .GetProperty("expectedProtectedIds")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .Append(canvasMutationId)
                .ToArray();
            var observedMutationSafe = new List<string>();
            var observedProtected = new List<string>();
            bool operationsExact = true;
            bool labelsExact = true;
            bool invalidPlaybackProtected = true;
            bool snapshotHashesExact = true;

            foreach (JsonElement job in jobs)
            {
                string id = job.GetProperty("id").GetString()!;
                string presetHash = job.GetProperty("presetHash").GetString()!;
                string snapshotHash = PhotoViewer.Wpf.MainWindow
                    .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                        job.GetProperty("video"));
                snapshotHashesExact &= string.Equals(
                    presetHash,
                    snapshotHash[..12],
                    StringComparison.Ordinal);
                bool parsed = window
                    .TryReadMiniMaxH3WorkspacePresentationForSmoke(
                        job,
                        out string operation,
                        out string presetSummary,
                        out bool mutationSafe,
                        out bool canUseOutput);
                string expectedOperation = string.Equals(
                        id,
                        canvasMutationId,
                        StringComparison.Ordinal)
                    ? canvasMutationFixture
                        .GetProperty("expectedOperation")
                        .GetString()!
                    : readerFixture
                        .GetProperty("expectedOperations")
                        .GetProperty(id)
                        .GetString()!;
                operationsExact &= parsed && operation == expectedOperation;
                labelsExact &= presetSummary.Contains(
                    "MiniMax H3 Preview",
                    StringComparison.Ordinal);
                if (mutationSafe)
                    observedMutationSafe.Add(id);
                else
                    observedProtected.Add(id);
                if (!string.Equals(id, "valid-h3-video", StringComparison.Ordinal))
                {
                    invalidPlaybackProtected &= !canUseOutput
                        && !window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                            job,
                            out _,
                            out _,
                            out _,
                            out _,
                            out _,
                            out _,
                            out _);
                }
            }

            bool validWorkspace = window
                    .TryReadMiniMaxH3WorkspacePresentationForSmoke(
                        validJob,
                        out string validOperation,
                        out string validSummary,
                        out bool validMutationSafe,
                        out bool validCanUseOutput)
                && validOperation == "video"
                && validMutationSafe
                && validCanUseOutput
                && validSummary.Contains("MiniMax H3 Preview", StringComparison.Ordinal);
            bool protectedRerunActionsHidden = jobs
                .Where(job => !string.Equals(
                    job.GetProperty("id").GetString(),
                    "valid-h3-video",
                    StringComparison.Ordinal))
                .All(job =>
                {
                    string[] actionKinds = window
                        .ReadMiniMaxH3VideoActionKindsForSmoke(job);
                    return !actionKinds.Contains(
                            "video-rerun-saved",
                            StringComparer.Ordinal)
                        && !actionKinds.Contains(
                            "video-edit-prompt",
                            StringComparer.Ordinal);
                });
            bool sourceAspectPolicyProtected = observedProtected.Contains(
                    canvasMutationId,
                    StringComparer.Ordinal)
                && !canvasMutationFixture
                    .GetProperty("expectedMutationSafe")
                    .GetBoolean();
            bool validPlayback = window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                    validJob,
                    out int videoWidth,
                    out int videoHeight,
                    out int playbackFps,
                    out int frameCount,
                    out double durationSeconds,
                    out bool audio,
                    out string settingsText)
                && videoWidth == 864
                && videoHeight == 480
                && playbackFps == 24
                && frameCount == 124
                && Math.Abs(durationSeconds - 5.166666666666667) <= 1e-12
                && audio
                && settingsText.Contains("MiniMax H3 Preview", StringComparison.Ordinal)
                && settingsText.Contains("Duration: 5.1666667 sec", StringComparison.Ordinal)
                && settingsText.Contains("Playback FPS: 24", StringComparison.Ordinal)
                && settingsText.Contains("Frames: 124", StringComparison.Ordinal)
                && settingsText.Contains("aac", StringComparison.OrdinalIgnoreCase)
                && settingsText.Contains("audio True", StringComparison.Ordinal);
            bool selectedStepsPlayback = PhotoViewer.Wpf.MainWindow
                    .IsMiniMaxH3VideoMutationSafeForSmoke(selectedStepsJob)
                && window.TryBuildMiniMaxH3ManagedVideoVersionForSmoke(
                    selectedStepsJob,
                    out int selectedStepsWidth,
                    out int selectedStepsHeight,
                    out int selectedStepsPlaybackFps,
                    out int selectedStepsFrameCount,
                    out double selectedStepsDuration,
                    out bool selectedStepsAudio,
                    out string selectedStepsSettingsText)
                && selectedStepsWidth == 864
                && selectedStepsHeight == 480
                && selectedStepsPlaybackFps == 24
                && selectedStepsFrameCount == 124
                && Math.Abs(selectedStepsDuration - 124d / 24d) <= 1e-12
                && selectedStepsAudio
                && selectedStepsSettingsText.Contains(
                    $"Steps: {selectedVideoSteps}",
                    StringComparison.Ordinal);
            bool exactSets = observedMutationSafe.SequenceEqual(
                    expectedMutationSafe,
                    StringComparer.Ordinal)
                && observedProtected.SequenceEqual(
                    expectedProtected,
                    StringComparer.Ordinal);
            bool unknownFieldsPresent = jobsDocument.RootElement
                    .GetProperty("futureRootField")
                    .GetProperty("keep")
                    .GetBoolean()
                && jobs.Single(job =>
                        job.GetProperty("id").GetString() == "future-field-h3")
                    .GetProperty("futureJobField")
                    .GetProperty("keep")
                    .GetBoolean();

            bool modalSourceSelected = false;
            bool modalOpened = false;
            bool modalMediaOpened = false;
            bool modalPlaybackProgress = false;
            bool modalPauseResume = false;
            bool modalMediaFailureFallback = false;
            bool validMediaPreserved = false;
            string? modalPlaybackException = null;
            window.Show();
            var playbackFrame = new DispatcherFrame();
            _ = window.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    try
                    {
                        await window.LoadFolderAsync(sourceRoot);
                        modalSourceSelected = window.SelectFileNameForSmoke(
                            Path.GetFileName(sourcePath));
                        modalOpened = modalSourceSelected
                            && window.OpenModalForSmoke();
                        bool videoVersionSelected = modalOpened
                            && window.SelectModalVideoVersionForSmoke(0);
                        modalMediaOpened = videoVersionSelected
                            && await window
                                .WaitForModalVideoMediaOpenedForSmokeAsync();
                        modalPlaybackProgress = modalMediaOpened
                            && window.ModalVideoHasNaturalDurationForSmoke
                            && await window
                                .WaitForModalVideoPlaybackProgressForSmokeAsync();
                        bool paused = modalPlaybackProgress
                            && window.ToggleModalVideoPlaybackForSmoke()
                            && await window
                                .WaitForModalVideoPauseSettledForSmokeAsync();
                        bool resumed = paused
                            && window.ToggleModalVideoPlaybackForSmoke()
                            && await window
                                .WaitForModalVideoPlaybackProgressForSmokeAsync();
                        modalPauseResume = paused && resumed;
                        window.CloseModalForSmoke();

                        bool failureModalOpened = window.OpenModalForSmoke();
                        bool failureVersionSelected = failureModalOpened
                            && window.SelectCorruptModalVideoForSmoke(
                                corruptVideoPath);
                        bool corruptMediaOpened = failureVersionSelected
                            && await window
                                .WaitForModalVideoMediaOpenedForSmokeAsync(
                                    timeoutMilliseconds: 5_000);
                        modalMediaFailureFallback = failureModalOpened
                            && !corruptMediaOpened
                            && !window.ModalShowingVideoForSmoke
                            && !string.IsNullOrWhiteSpace(
                                window.ModalVideoMediaFailureForSmoke);
                        window.CloseModalForSmoke();
                        validMediaPreserved = File.ReadAllBytes(
                                resolvedValidOutputPath)
                            .SequenceEqual(mediaFixtureBytes);
                    }
                    catch (Exception ex)
                    {
                        modalPlaybackException =
                            $"{ex.GetType().Name}: {ex.Message}";
                    }
                    finally
                    {
                        playbackFrame.Continue = false;
                    }
                }),
                DispatcherPriority.Background);
            Dispatcher.PushFrame(playbackFrame);

            bool exifFixturesReadOnly = exifSourceBytesBefore.All(pair =>
                    File.Exists(pair.Key)
                    && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && exifOutputBytesBefore.All(pair =>
                    File.Exists(pair.Key)
                    && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value));
            bool exifFixturesIsolated = exifSourceBytesBefore.Keys
                .Concat(exifOutputBytesBefore.Keys)
                .All(path => Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(smokeRoot)
                        + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            bool readOnly = File.ReadAllBytes(jobsPath).SequenceEqual(jobsBefore)
                && File.ReadAllBytes(sourcePath).SequenceEqual(sourceBefore)
                && File.ReadAllBytes(statePath).SequenceEqual(stateBefore)
                && File.ReadAllBytes(favoritesPath).SequenceEqual(favoritesBefore)
                && File.ReadAllBytes(seenPath).SequenceEqual(seenBefore)
                && File.ReadAllBytes(recentPath).SequenceEqual(recentBefore)
                && File.ReadAllBytes(searchHistoryPath)
                    .SequenceEqual(searchHistoryBefore)
                && File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore)
                && File.ReadAllBytes(albumsPath).SequenceEqual(albumsBefore)
                && File.ReadAllBytes(contractPath).SequenceEqual(contractBefore)
                && File.ReadAllBytes(mediaFixturePath)
                    .SequenceEqual(mediaFixtureBefore)
                && File.Exists(resolvedValidOutputPath)
                && File.ReadAllBytes(resolvedValidOutputPath)
                    .SequenceEqual(mediaFixtureBytes)
                && File.Exists(selectedStepsOutputPath)
                && File.ReadAllBytes(selectedStepsOutputPath)
                    .SequenceEqual(mediaFixtureBytes)
                && File.ReadAllBytes(legacyManagedSourcePath)
                    .SequenceEqual(legacyManagedSourceBefore)
                && File.ReadAllBytes(legacyManagedOutputPath)
                    .SequenceEqual(legacyManagedOutputBefore)
                && File.ReadAllBytes(displayedManagedSourcePath)
                    .SequenceEqual(displayedManagedSourceBefore)
                && File.ReadAllBytes(displayedManagedOutputPath)
                    .SequenceEqual(displayedManagedOutputBefore)
                && exifFixturesReadOnly
                && mutationRequests == readerFixture
                    .GetProperty("expectedMutationRequestsDuringRead")
                    .GetInt32();
            bool isolated = new[]
                {
                    sourcePath,
                    resolvedValidOutputPath,
                    selectedStepsOutputPath,
                    legacyManagedSourcePath,
                    legacyManagedOutputPath,
                    displayedManagedSourcePath,
                    displayedManagedOutputPath,
                    corruptVideoPath,
                    jobsPath,
                    statePath,
                    favoritesPath,
                    seenPath,
                    recentPath,
                    searchHistoryPath,
                    settingsPath,
                    albumsPath,
                    metadataIndexDirectory,
                }
                .All(path => Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(smokeRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                && exifFixturesIsolated;

            ok = contractIdentity
                && sourceContract
                && sourceDimensionsExact
                && compactVideoMutationProbeExtracted
                && exactVideoProbeIdentityStable
                && fullVideoEnvelopeFingerprintGuardsIdentity
                && h3SnapshotDifferenceGuardsIdentity
                && stableHashExact
                && longSnapshotExact
                && japaneseHashInterop
                && portraitHashInterop
                && canvasPolicyVectorsExact
                && exifOrientationMetadataExact
                && exifWriterCanvasExact
                && exifWorkspaceReaderExact
                && exifPlaybackReaderExact
                && exifUnswappedCanvasProtected
                && exifFixturesReadOnly
                && exifFixturesIsolated
                && mediaFixtureExact
                && ownedOutputName
                && snapshotHashesExact
                && operationsExact
                && labelsExact
                && validWorkspace
                && rerunPresentationExact
                && rerunRequestExact
                && legacyManagedRerunExact
                && legacyManagedRerunRequestExact
                && staleManagedProducerIgnoredExact
                && displayedManagedWorkspaceSourceExact
                && displayedManagedPlaybackExact
                && displayedManagedCatalogExact
                && protectedRerunActionsHidden
                && validPlayback
                && selectedStepsPlayback
                && invalidPlaybackProtected
                && sourceAspectPolicyProtected
                && exactSets
                && unknownFieldsPresent
                && modalSourceSelected
                && modalOpened
                && modalMediaOpened
                && modalPlaybackProgress
                && modalPauseResume
                && modalMediaFailureFallback
                && validMediaPreserved
                && modalPlaybackException is null
                && readOnly
                && isolated;
            result = new
            {
                ok,
                contractIdentity,
                sourceContract,
                sourceDimensionsExact,
                compactVideoMutationProbeExtracted,
                exactVideoProbeIdentityStable,
                fullVideoEnvelopeFingerprintGuardsIdentity,
                h3SnapshotDifferenceGuardsIdentity,
                stableHashExact,
                longSnapshotExact,
                japaneseHashInterop,
                portraitHashInterop,
                canvasPolicyVectorsExact,
                exifOrientationMetadataExact,
                exifWriterCanvasExact,
                exifWorkspaceReaderExact,
                exifPlaybackReaderExact,
                exifUnswappedCanvasProtected,
                exifFixturesReadOnly,
                exifFixturesIsolated,
                exifOrientationsCovered = new[] { 5, 6, 7, 8 },
                exifStoredWidth,
                exifStoredHeight,
                exifWriterWidth,
                exifWriterHeight,
                exifUnswappedWidth,
                exifUnswappedHeight,
                mediaFixtureExact,
                mediaFixtureSha256,
                ownedOutputName,
                snapshotHashesExact,
                operationsExact,
                labelsExact,
                validWorkspace,
                rerunPresentationExact,
                rerunRequestExact,
                legacyManagedRerunExact,
                legacyManagedRerunRequestExact,
                staleManagedProducerIgnoredExact,
                displayedManagedWorkspaceSourceExact,
                displayedManagedPlaybackExact,
                displayedManagedCatalogExact,
                protectedRerunActionsHidden,
                validPlayback,
                selectedStepsPlayback,
                invalidPlaybackProtected,
                sourceAspectPolicyProtected,
                exactSets,
                unknownFieldsPresent,
                modalSourceSelected,
                modalOpened,
                modalMediaOpened,
                modalPlaybackProgress,
                modalPauseResume,
                modalMediaFailureFallback,
                validMediaPreserved,
                modalPlaybackException,
                readOnly,
                isolated,
                mutationRequests,
                playbackFps,
                frameCount,
                durationSeconds,
                audio,
                exactLeftFingerprint,
                exactRightFingerprint,
                envelopeLeftFingerprint,
                envelopeRightFingerprint,
                snapshotLeftFingerprint,
                snapshotRightFingerprint,
                exactLeftPrompt,
                exactRightPrompt,
                envelopeLeftPrompt,
                envelopeRightPrompt,
                snapshotLeftPrompt,
                snapshotRightPrompt,
                validHash,
                japaneseHash,
                portraitHash,
                asciiExpectedPresetHash,
                japaneseExpectedPresetHash,
                portraitExpectedPresetHash,
                videoWidth,
                videoHeight,
                observedMutationSafe,
                observedProtected,
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                stackTrace = ex.StackTrace,
            };
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_STATE_PATH",
                previousStatePath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_FAVORITES_PATH",
                previousFavoritesPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SEEN_PATH",
                previousSeenPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_RECENT_PATH",
                previousRecentPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH",
                previousSearchHistoryPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_SETTINGS_PATH",
                previousSettingsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ALBUMS_PATH",
                previousAlbumsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY",
                previousMetadataIndexDirectory);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                previousJobsPath);
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                previousOutputRoot);
            Environment.SetEnvironmentVariable(
                "PVU_ENHANCE_OUTPUT_ROOT",
                previousSharedOutputRoot);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        try
        {
            if (Directory.Exists(smokeRoot))
                Directory.Delete(smokeRoot, recursive: true);
        }
        catch
        {
        }
        Shutdown(ok ? 0 : 1);
    }

    private static JsonElement BuildSyntheticExifVideoJob(
        JsonElement template,
        string jobId,
        string sourcePath,
        long sourceSize,
        long sourceMtimeMs,
        string sourceSha256,
        int effectiveWidth,
        int effectiveHeight,
        string outputRoot,
        byte[] mediaFixtureBytes)
    {
        JsonObject job = JsonNode.Parse(template.GetRawText())!.AsObject();
        job["id"] = jobId;
        job["sourceId"] = sourcePath;
        job["sourcePath"] = sourcePath;
        job["sourceSignature"]!["size"] = sourceSize;
        job["sourceSignature"]!["mtimeMs"] = sourceMtimeMs;
        job["sourceSha256"] = sourceSha256;
        JsonObject video = job["video"]!.AsObject();
        video["effective"]!["width"] = effectiveWidth;
        video["effective"]!["height"] = effectiveHeight;
        using (JsonDocument videoDocument = JsonDocument.Parse(
            video.ToJsonString()))
        {
            job["presetHash"] = PhotoViewer.Wpf.MainWindow
                .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                    videoDocument.RootElement)[..12];
        }
        string presetHash = job["presetHash"]!.GetValue<string>();
        string presetId = job["presetId"]!.GetValue<string>();
        string fileName = PhotoViewer.Wpf.MainWindow
            .BuildVideoOutputFileNameForSmoke(
                jobId,
                sourcePath,
                sourceSha256,
                presetId,
                presetHash);
        string outputPath = Path.Combine(outputRoot, "Videos", fileName);
        job["outputPath"] = outputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, mediaFixtureBytes);
        using JsonDocument jobDocument = JsonDocument.Parse(
            job.ToJsonString());
        return jobDocument.RootElement.Clone();
    }

    private static JsonElement BuildMiniMaxH3IdentityMutationForSmoke(
        JsonElement template,
        int? seed,
        string? prompt)
    {
        JsonObject job = JsonNode.Parse(template.GetRawText())!.AsObject();
        JsonObject video = job["video"]!.AsObject();
        if (seed.HasValue)
            video["seed"] = seed.Value;
        if (prompt is not null)
        {
            video["requested"]!["prompt"] = prompt;
            video["effective"]!["positivePrompt"] = prompt;
        }

        using (JsonDocument videoDocument = JsonDocument.Parse(
            video.ToJsonString()))
        {
            job["presetHash"] = PhotoViewer.Wpf.MainWindow
                .ComputeMiniMaxH3VideoSnapshotHashForSmoke(
                    videoDocument.RootElement)[..12];
        }

        if (job["outputPath"]?.GetValueKind() == JsonValueKind.String)
        {
            string currentOutputPath = job["outputPath"]!.GetValue<string>();
            string outputFileName = PhotoViewer.Wpf.MainWindow
                .BuildVideoOutputFileNameForSmoke(
                    job["id"]!.GetValue<string>(),
                    job["sourcePath"]!.GetValue<string>(),
                    job["sourceSha256"]!.GetValue<string>(),
                    job["presetId"]!.GetValue<string>(),
                    job["presetHash"]!.GetValue<string>());
            job["outputPath"] = Path.Combine(
                Path.GetDirectoryName(currentOutputPath)!,
                outputFileName);
        }

        using JsonDocument jobDocument = JsonDocument.Parse(job.ToJsonString());
        return jobDocument.RootElement.Clone();
    }

    private static bool IsLowerHexSha256(string value)
        => value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private static byte[] CreateSyntheticOrientationJpeg(
        int width,
        int height,
        ushort orientation)
    {
        int stride = checked(width * 3);
        byte[] pixels = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = checked(y * stride + x * 3);
                pixels[offset] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[offset + 2] = (byte)((x + y) % 256);
            }
        }
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            palette: null,
            pixels: pixels,
            stride: stride);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery(
            "/app1/ifd/{ushort=274}",
            orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(
            bitmap,
            thumbnail: null,
            metadata: metadata,
            colorContexts: null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool TryReadSyntheticJpegOrientation(
        string path,
        out int width,
        out int height,
        out ushort orientation)
    {
        width = 0;
        height = 0;
        orientation = 0;
        try
        {
            using Stream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            if (frame.Metadata is not BitmapMetadata metadata)
                return false;
            foreach (string query in new[]
            {
                "/app1/ifd/{ushort=274}",
                "/ifd/{ushort=274}",
            })
            {
                try
                {
                    object? value = metadata.GetQuery(query);
                    if (value is ushort unsignedValue)
                    {
                        orientation = unsignedValue;
                        return true;
                    }
                    if (value is short signedValue && signedValue > 0)
                    {
                        orientation = (ushort)signedValue;
                        return true;
                    }
                }
                catch
                {
                    // The exact query varies by decoder container.
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool ContainsAscii(byte[] bytes, string value)
    {
        ReadOnlySpan<byte> pattern = Encoding.ASCII.GetBytes(value);
        return bytes.AsSpan().IndexOf(pattern) >= 0;
    }

    private static bool HasExactJsonProperties(
        JsonElement element,
        params string[] expectedNames)
        => element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .SequenceEqual(
                    expectedNames.OrderBy(
                        static name => name,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal);

    private static bool HasExactJsonString(
        JsonElement element,
        string propertyName,
        string expectedValue)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(
                value.GetString(),
                expectedValue,
                StringComparison.Ordinal);

    private static bool HasExactJsonInt32(
        JsonElement element,
        string propertyName,
        int expectedValue)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int actualValue)
            && actualValue == expectedValue;

}
