using System.IO;
using System.Net;
using System.Net.Http;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureModalPhotorealSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory("aibos-wpf-photoreal-").FullName;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            var previousEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);
            bool ok = false;
            string failure = "";
            bool selected = false;
            bool opened = false;
            bool passive = false;
            bool started = false;
            bool toolbarContract = false;
            bool loadTimingContract = false;
            bool requestContract = false;
            bool resetPromptContract = false;
            bool appSettingsPromptContract = false;
            bool appSettingsControlsContract = false;
            bool photorealEngineContract = false;
            bool fallbackPromptContract = false;
            bool negativePromptContract = false;
            bool loraToggleContract = false;
            bool structureRemovedContract = false;
            bool explicitBlankPersistenceContract = false;
            bool legacyPromptMigrationContract = false;
            bool styleContract = false;
            bool stylePersistenceContract = false;
            bool styleReloadContract = false;
            bool defaultPromptContract = false;
            bool promptMappingContract = false;
            bool promptMappingDirectToggleContract = false;
            bool promptMappingEditRefreshContract = false;
            bool promptMappingDefaultsMigrationContract = false;
            bool promptMappingButtonContrastContract = false;
            bool displayedVersionMetadataContract = false;
            object? displayedVersionMetadataDiagnostic = null;
            bool photoUpscaleProfileContract = false;
            bool veryHighQualityContract = false;
            bool sharedQueueRoute = false;
            bool versionCycleContract = false;
            bool versionSpecificFavoriteContract = false;
            bool photorealFavoriteBadgeFilterContract = false;
            bool photorealFavoriteLayoutContract = false;
            bool photorealFavoriteRetryContract = false;
            bool staleFavoriteSourceFallbackRejected = false;
            bool versionWheelCycleContract = false;
            bool thumbnailVersionPreferenceContract = false;
            bool thumbnailVariantCountContract = false;
            bool upscaleSettingsContract = false;
            bool persistedNcnnUpscaleChoiceContract = false;
            bool ncnnHighScaleSelectionContract = false;
            bool legacyComfyDefaultMigrationContract = false;
            bool photorealShortcutContract = false;
            bool queueAddedToastContract = false;
            bool sourceUntouched = false;
            bool independentCompanionContract = false;
            bool galleryContextNoModal = false;
            bool galleryContextDirectContract = false;
            bool galleryEnqueueNextContract = false;
            bool modalEnqueueNextDisplayedPhotorealContract = false;
            bool legacyPhotorealCapabilitySafe = false;
            bool modalPhotorealOperation = false;
            bool recoveredReferenceExact = false;
            bool recoveredReferenceValidEmptyJobs = false;
            bool recoveredReferenceMalformedJobsRejected = false;
            bool recoveredReferenceHashMismatchRejected = false;
            bool recoveredReferenceHashVector = false;
            bool recoveredReferenceAmbiguousRejected = false;
            bool recoveredReferenceKnownJobRejected = false;
            bool recoveredReferenceMutationBlocked = false;
            bool recoveredReferencePollingPreserved = false;
            bool recoveredReferenceReadOnly = false;
            bool recoveredReferenceCacheReuse = false;
            bool recoveredReferenceCacheInvalidation = false;
            bool hqPromptProvenanceContract = false;
            bool recoveredHqButtonContract = false;
            bool recoveredHqCapabilityGateContract = false;
            bool photorealSeedContract = false;
            bool photorealPreservationScanContract = false;
            bool randomSeedOmitted = false;
            bool seedDefaultAndSurface = false;
            bool fixedSeedExact = false;
            bool invalidSeedBlocked = false;
            bool missingSeedCapabilityBlocked = false;
            bool gallerySingleFlightContract = false;
            object? recoveredReferencePerformance = null;
            string recoveredReferenceDiagnostic = "";
            var requests = new List<string>();
            var createBodies = new List<string>();
            string createBody = "";
            bool photorealSeedCapabilityAvailable = true;
            TaskCompletionSource<bool>? galleryReadinessEntered = null;
            TaskCompletionSource<bool>? galleryReadinessRelease = null;
            try
            {
                string imageRoot = Path.Combine(smokeRoot, "images");
                string storesRoot = Path.Combine(smokeRoot, "stores");
                string aiStylePath = Path.Combine(storesRoot, "ai-styles.json");
                string sourcePath = Path.Combine(imageRoot, "source.png");
                string recoveredSourcePath = Path.Combine(
                    imageRoot,
                    "復旧 unique.png");
                string hashMismatchSourcePath = Path.Combine(
                    imageRoot,
                    "hash-mismatch.png");
                string knownJobSourcePath = Path.Combine(
                    imageRoot,
                    "known-job.png");
                string invalidationSourcePath = Path.Combine(
                    imageRoot,
                    "cache-invalidation.png");
                string ambiguousSourceRoot = Path.Combine(
                    smokeRoot,
                    "ambiguous-images");
                string ambiguousSourcePath = Path.Combine(
                    ambiguousSourceRoot,
                    "ambiguous.png");
                Directory.CreateDirectory(imageRoot);
                Directory.CreateDirectory(ambiguousSourceRoot);
                Directory.CreateDirectory(storesRoot);
                WritePhotorealSmokePng(sourcePath);
                WritePhotorealSmokePng(recoveredSourcePath);
                WritePhotorealSmokePng(hashMismatchSourcePath);
                WritePhotorealSmokePng(knownJobSourcePath);
                WritePhotorealSmokePng(invalidationSourcePath);
                WritePhotorealSmokePng(ambiguousSourcePath);
                const string originalEmbeddedPrompt =
                    "((troubled eyebrows:1.3)), (open mouth, blush:1.2), x-cross \\(bdsm\\)";
                InsertPngTextFixture(
                    sourcePath,
                    "parameters",
                    $"{originalEmbeddedPrompt}\nNegative prompt: source negative\nSteps: 20, CFG scale: 5, Seed: 42");
                string sourceHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                var sourceInfo = new FileInfo(sourcePath);
                double sourceMtimeMs = new DateTimeOffset(sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                string enhancementRoot = Path.Combine(storesRoot, "enhance");
                string outputsRoot = Path.Combine(enhancementRoot, "outputs");
                string upscaleOutputPath = Path.Combine(outputsRoot, "upscale", "upscale.png");
                string photorealOutputPath = Path.Combine(outputsRoot, "photoreal", "photoreal.png");
                string recoveredPhotorealRoot = Path.Combine(
                    outputsRoot,
                    "Photorealized");
                Directory.CreateDirectory(Path.GetDirectoryName(upscaleOutputPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(photorealOutputPath)!);
                Directory.CreateDirectory(recoveredPhotorealRoot);
                File.Copy(sourcePath, upscaleOutputPath);
                File.Copy(sourcePath, photorealOutputPath);
                WritePhotorealSmokePng(photorealOutputPath);
                InsertPngTextFixture(
                    photorealOutputPath,
                    "parameters",
                    "photoreal effective prompt\nNegative prompt: photoreal effective negative\nSteps: 8, CFG scale: 1.25, Seed: 123, Aibos operation: photoreal, LoRA: off, Max dimension: 1280");
                var upscaleJob = new
                {
                    id = "upscale-version",
                    operation = "upscale",
                    sourceId = sourcePath,
                    sourcePath,
                    sourceSignature = new { size = sourceInfo.Length, mtimeMs = sourceMtimeMs },
                    adapterId = "realesrgan-ncnn",
                    status = "succeeded",
                    progress = 100,
                    outputPath = upscaleOutputPath,
                };
                var photorealJob = new
                {
                    id = "photoreal-version",
                    operation = "photoreal",
                    sourceId = sourcePath,
                    sourcePath,
                    sourceSignature = new { size = sourceInfo.Length, mtimeMs = sourceMtimeMs },
                    adapterId = "comfyui-flux2-photoreal",
                    status = "succeeded",
                    progress = 100,
                    outputPath = photorealOutputPath,
                };
                const string recoveredJobId =
                    "11111111-1111-4111-8111-111111111111";
                const string hashMismatchJobId =
                    "22222222-2222-4222-8222-222222222222";
                const string knownJobId =
                    "33333333-3333-4333-8333-333333333333";
                const string ambiguousJobId =
                    "44444444-4444-4444-8444-444444444444";
                const string recoveredPresetId = "photoreal-balanced";
                const string recoveredPresetHash = "0123456789ab";
                const string recoveredAdapterId = "comfyui-flux2-photoreal";
                string recoveredPhotorealDateRoot = Path.Combine(
                    recoveredPhotorealRoot,
                    "2026-08-11");
                Directory.CreateDirectory(recoveredPhotorealDateRoot);
                string recoveredOutputPath = Path.Combine(
                    recoveredPhotorealDateRoot,
                    BuildRecoveredSmokeOutputFileName(
                        recoveredSourcePath,
                        recoveredJobId,
                        recoveredPresetId,
                        recoveredPresetHash,
                        recoveredAdapterId));
                string hashMismatchOutputPath = Path.Combine(
                    recoveredPhotorealRoot,
                    BuildRecoveredSmokeOutputFileName(
                        hashMismatchSourcePath,
                        hashMismatchJobId,
                        recoveredPresetId,
                        recoveredPresetHash,
                        recoveredAdapterId,
                        sourceHashOverride: "0000000000000000"));
                string knownJobOutputPath = Path.Combine(
                    recoveredPhotorealRoot,
                    BuildRecoveredSmokeOutputFileName(
                        knownJobSourcePath,
                        knownJobId,
                        recoveredPresetId,
                        recoveredPresetHash,
                        recoveredAdapterId));
                string ambiguousOutputPath = Path.Combine(
                    recoveredPhotorealRoot,
                    BuildRecoveredSmokeOutputFileName(
                        ambiguousSourcePath,
                        ambiguousJobId,
                        recoveredPresetId,
                        recoveredPresetHash,
                        recoveredAdapterId));
                File.Copy(recoveredSourcePath, recoveredOutputPath);
                File.Copy(hashMismatchSourcePath, hashMismatchOutputPath);
                File.Copy(knownJobSourcePath, knownJobOutputPath);
                File.Copy(ambiguousSourcePath, ambiguousOutputPath);
                var knownJobSourceInfo = new FileInfo(knownJobSourcePath);
                double knownJobSourceMtimeMs =
                    RecoveredSmokeMtimeMs(knownJobSourceInfo);
                var knownIdJob = new
                {
                    id = knownJobId,
                    operation = "photoreal",
                    sourceId = knownJobSourcePath,
                    sourcePath = knownJobSourcePath,
                    sourceSignature = new
                    {
                        size = knownJobSourceInfo.Length,
                        mtimeMs = knownJobSourceMtimeMs,
                    },
                    adapterId = recoveredAdapterId,
                    status = "failed",
                    progress = 0,
                    outputPath = knownJobOutputPath,
                };
                string jobsPath = Path.Combine(enhancementRoot, "jobs.json");
                File.WriteAllText(
                    jobsPath,
                    JsonSerializer.Serialize(new
                    {
                        version = 1,
                        jobs = new[] { upscaleJob, photorealJob, knownIdJob },
                    }));
                string recoveryInspectionJobsPath = Path.Combine(
                    enhancementRoot,
                    "recovery-inspection-jobs.json");
                File.WriteAllText(
                    recoveryInspectionJobsPath,
                    "{\"version\":1,\"jobs\":[]}");
                string malformedRecoveryJobsPath = Path.Combine(
                    enhancementRoot,
                    "recovery-malformed-jobs.json");
                File.WriteAllBytes(
                    malformedRecoveryJobsPath,
                    [0, 0, 0, 0]);
                string[] recoveredFixtureFiles =
                [
                    jobsPath,
                    recoveryInspectionJobsPath,
                    malformedRecoveryJobsPath,
                    recoveredSourcePath,
                    hashMismatchSourcePath,
                    knownJobSourcePath,
                    invalidationSourcePath,
                    ambiguousSourcePath,
                    recoveredOutputPath,
                    hashMismatchOutputPath,
                    knownJobOutputPath,
                    ambiguousOutputPath,
                ];
                Dictionary<string, string> recoveredFixtureHashesBefore =
                    FingerprintRecoveredSmokeFiles(recoveredFixtureFiles);

                var environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storesRoot, "state.json"),
                    ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storesRoot, "favorites.json"),
                    ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storesRoot, "seen.json"),
                    ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storesRoot, "recent-folders.json"),
                    ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storesRoot, "settings.json"),
                    ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storesRoot, "albums.json"),
                    ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storesRoot, "search-history.json"),
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputsRoot,
                    ["PVU_ENHANCE_OUTPUT_ROOT"] = outputsRoot,
                    ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storesRoot, "metadata-index"),
                };
                foreach ((string name, string value) in environment)
                {
                    previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, value);
                }

                static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
                    => new(status)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    };
                static JsonElement ParseRequestBody(string json)
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    return document.RootElement.Clone();
                }

                window = new MainWindow();
                recoveredReferenceHashVector = string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ComputeRecoveredEnhancementSourceHashForSmoke(
                            @"C:\画像\復旧.png",
                            123456,
                            1785751234567.125,
                            "0123456789ab",
                            "comfyui-flux2-photoreal"),
                    "993288ad42ff47db",
                    StringComparison.Ordinal);
                RecoveredEnhancementReferenceSmokeSnapshot uniqueRecovery =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        [recoveredSourcePath],
                        catalogRevision: 1);
                RecoveredEnhancementReferenceSmokeSnapshot hashMismatchRecovery =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        [hashMismatchSourcePath],
                        catalogRevision: 2);
                RecoveredEnhancementReferenceSmokeSnapshot ambiguousRecovery =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        [ambiguousSourcePath, ambiguousSourcePath],
                        catalogRevision: 3);
                RecoveredEnhancementReferenceSmokeSnapshot knownJobRecovery =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        jobsPath,
                        [knownJobSourcePath],
                        catalogRevision: 4);
                RecoveredEnhancementReferenceSmokeSnapshot malformedRecovery =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        malformedRecoveryJobsPath,
                        [recoveredSourcePath],
                        catalogRevision: 5);
                recoveredReferenceExact = uniqueRecovery is
                {
                    ReadOk: true,
                    Total: 1,
                    Upscaled: 0,
                    Photorealized: 1,
                };
                recoveredReferenceValidEmptyJobs = recoveredReferenceExact;
                recoveredReferenceMalformedJobsRejected =
                    malformedRecovery is { ReadOk: false, Total: 0 };
                recoveredReferenceHashMismatchRejected =
                    hashMismatchRecovery is { ReadOk: true, Total: 0 };
                recoveredReferenceAmbiguousRejected =
                    ambiguousRecovery is { ReadOk: true, Total: 0 };
                recoveredReferenceKnownJobRejected =
                    knownJobRecovery is { ReadOk: true, Total: 0 };
                recoveredReferenceDiagnostic =
                    $"scan={uniqueRecovery.Total}/{hashMismatchRecovery.Total}/"
                    + $"{ambiguousRecovery.Total}/{knownJobRecovery.Total}";

                window.ResetRecoveredEnhancementReferenceCacheForSmoke();
                const long invalidationCatalogRevision = 100;
                string[] invalidationCatalog =
                    [recoveredSourcePath, invalidationSourcePath];
                RecoveredEnhancementReferenceSmokeSnapshot invalidationBase =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationBaseMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;
                RecoveredEnhancementReferenceSmokeSnapshot invalidationReuse =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationReuseMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;
                RecoveredEnhancementReferenceSmokeSnapshot invalidationCatalogChanged =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationCatalogMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;

                File.WriteAllText(
                    recoveryInspectionJobsPath,
                    JsonSerializer.Serialize(new
                    {
                        version = 1,
                        jobs = new[] { new { id = recoveredJobId } },
                    }));
                RecoveredEnhancementReferenceSmokeSnapshot invalidationJobsChanged =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationJobsMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;
                File.WriteAllText(
                    recoveryInspectionJobsPath,
                    "{\"version\":1,\"jobs\":[]}");
                RecoveredEnhancementReferenceSmokeSnapshot invalidationJobsRestored =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationJobsRestoreMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;

                DateTime recoveredSourceWriteTimeUtc =
                    File.GetLastWriteTimeUtc(recoveredSourcePath);
                File.SetLastWriteTimeUtc(
                    recoveredSourcePath,
                    recoveredSourceWriteTimeUtc.AddSeconds(2));
                RecoveredEnhancementReferenceSmokeSnapshot invalidationSourceChanged =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationSourceMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;
                File.SetLastWriteTimeUtc(
                    recoveredSourcePath,
                    recoveredSourceWriteTimeUtc);
                RecoveredEnhancementReferenceSmokeSnapshot invalidationSourceRestored =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationSourceRestoreMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;

                const string invalidationJobId =
                    "55555555-5555-4555-8555-555555555555";
                string invalidationOutputPath = Path.Combine(
                    recoveredPhotorealRoot,
                    BuildRecoveredSmokeOutputFileName(
                        invalidationSourcePath,
                        invalidationJobId,
                        recoveredPresetId,
                        recoveredPresetHash,
                        recoveredAdapterId));
                DateTime outputFolderWriteTimeUtc =
                    Directory.GetLastWriteTimeUtc(recoveredPhotorealRoot);
                File.Copy(invalidationSourcePath, invalidationOutputPath);
                Directory.SetLastWriteTimeUtc(
                    recoveredPhotorealRoot,
                    outputFolderWriteTimeUtc.AddSeconds(2));
                RecoveredEnhancementReferenceSmokeSnapshot invalidationOutputAdded =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationOutputAddMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;
                File.Delete(invalidationOutputPath);
                Directory.SetLastWriteTimeUtc(
                    recoveredPhotorealRoot,
                    outputFolderWriteTimeUtc.AddSeconds(4));
                RecoveredEnhancementReferenceSmokeSnapshot invalidationOutputDeleted =
                    window.InspectRecoveredEnhancementReferencesForSmoke(
                        recoveryInspectionJobsPath,
                        invalidationCatalog,
                        invalidationCatalogRevision + 1);
                RecoveredEnhancementReferenceCacheSmokeSnapshot invalidationOutputDeleteMetrics =
                    window.RecoveredEnhancementReferenceCacheForSmoke;

                recoveredReferenceCacheInvalidation =
                    invalidationBase is { ReadOk: true, Total: 1 }
                    && invalidationReuse is { ReadOk: true, Total: 1 }
                    && invalidationCatalogChanged is { ReadOk: true, Total: 1 }
                    && invalidationJobsChanged is { ReadOk: true, Total: 0 }
                    && invalidationJobsRestored is { ReadOk: true, Total: 1 }
                    && invalidationSourceChanged is { ReadOk: true, Total: 0 }
                    && invalidationSourceRestored is { ReadOk: true, Total: 1 }
                    && invalidationOutputAdded is { ReadOk: true, Total: 2 }
                    && invalidationOutputDeleted is { ReadOk: true, Total: 1 }
                    && invalidationBaseMetrics.FullScans == 1
                    && invalidationReuseMetrics.FullScans == 1
                    && invalidationReuseMetrics.CacheHits == 1
                    && invalidationCatalogMetrics.FullScans == 2
                    && invalidationJobsMetrics.FullScans == 3
                    && invalidationJobsRestoreMetrics.FullScans == 4
                    && invalidationSourceMetrics.FullScans == 5
                    && invalidationSourceRestoreMetrics.FullScans == 6
                    && invalidationOutputAddMetrics.FullScans == 7
                    && invalidationOutputDeleteMetrics.FullScans == 8;

                string performanceRoot = Path.Combine(smokeRoot, "recovery-performance");
                string performanceImageRoot = Path.Combine(performanceRoot, "images");
                string performanceJobsPath = Path.Combine(
                    performanceRoot,
                    "enhance",
                    "jobs.json");
                string performanceOutputsRoot = Path.Combine(
                    performanceRoot,
                    "enhance",
                    "outputs");
                string performancePhotorealRoot = Path.Combine(
                    performanceOutputsRoot,
                    "Photorealized");
                Directory.CreateDirectory(performanceImageRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(performanceJobsPath)!);
                Directory.CreateDirectory(performancePhotorealRoot);
                const string performanceProgressJobId =
                    "66666666-6666-4666-8666-666666666666";
                File.WriteAllText(
                    performanceJobsPath,
                    JsonSerializer.Serialize(new
                    {
                        version = 1,
                        jobs = new[]
                        {
                            new
                            {
                                id = performanceProgressJobId,
                                status = "running",
                                progress = 1,
                            },
                        },
                    }));
                const int performanceSourceCount = 885;
                const int performanceCatalogCount = 140_085;
                var performanceCatalog = new string[performanceCatalogCount];
                for (int index = 0; index < performanceSourceCount; index++)
                {
                    string performanceSource = Path.Combine(
                        performanceImageRoot,
                        $"perf-{index:D4}.png");
                    File.Copy(sourcePath, performanceSource);
                    performanceCatalog[index] = performanceSource;
                    string performanceOutput = Path.Combine(
                        performancePhotorealRoot,
                        BuildRecoveredSmokeOutputFileName(
                            performanceSource,
                            Guid.NewGuid().ToString("D"),
                            recoveredPresetId,
                            recoveredPresetHash,
                            recoveredAdapterId));
                    File.Copy(performanceSource, performanceOutput);
                }
                for (int index = performanceSourceCount;
                     index < performanceCatalog.Length;
                     index++)
                {
                    performanceCatalog[index] = Path.Combine(
                        performanceImageRoot,
                        $"virtual-{index:D6}.png");
                }

                Environment.SetEnvironmentVariable(
                    "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                    performanceOutputsRoot);
                RecoveredEnhancementReferenceCacheSmokeSnapshot firstScanMetrics;
                RecoveredEnhancementReferenceCacheSmokeSnapshot reuseMetrics;
                RecoveredEnhancementReferenceSmokeSnapshot firstPerformanceScan;
                RecoveredEnhancementReferenceSmokeSnapshot reusedPerformanceScan;
                try
                {
                    window.ResetRecoveredEnhancementReferenceCacheForSmoke();
                    firstPerformanceScan =
                        window.InspectRecoveredEnhancementReferencesForSmoke(
                            performanceJobsPath,
                            performanceCatalog,
                            catalogRevision: 9_001);
                    firstScanMetrics =
                        window.RecoveredEnhancementReferenceCacheForSmoke;
                    // A progress-only jobs.json update keeps the job-ID set
                    // stable and must reuse the catalog/output match cache.
                    File.WriteAllText(
                        performanceJobsPath,
                        JsonSerializer.Serialize(new
                        {
                            version = 1,
                            jobs = new[]
                            {
                                new
                                {
                                    id = performanceProgressJobId,
                                    status = "running",
                                    progress = 73,
                                },
                            },
                        }));
                    reusedPerformanceScan =
                        window.InspectRecoveredEnhancementReferencesForSmoke(
                            performanceJobsPath,
                            performanceCatalog,
                            catalogRevision: 9_001);
                    reuseMetrics =
                        window.RecoveredEnhancementReferenceCacheForSmoke;
                }
                finally
                {
                    Environment.SetEnvironmentVariable(
                        "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                        outputsRoot);
                }
                recoveredReferenceCacheReuse =
                    firstPerformanceScan is
                    {
                        ReadOk: true,
                        Total: performanceSourceCount,
                        Photorealized: performanceSourceCount,
                    }
                    && reusedPerformanceScan is
                    {
                        ReadOk: true,
                        Total: performanceSourceCount,
                        Photorealized: performanceSourceCount,
                    }
                    && firstScanMetrics.FullScans == 1
                    && firstScanMetrics.CacheHits == 0
                    && firstScanMetrics.CatalogPathsVisited == performanceCatalogCount
                    && firstScanMetrics.OutputFilesVisited == performanceSourceCount
                    && firstScanMetrics.CachedCatalogPaths == performanceCatalogCount
                    && firstScanMetrics.CachedReferences == performanceSourceCount
                    && firstScanMetrics.CachedSourceProbes == performanceSourceCount
                    && reuseMetrics.FullScans == 1
                    && reuseMetrics.CacheHits == 1
                    && reuseMetrics.CatalogPathsVisited == performanceCatalogCount
                    && reuseMetrics.OutputFilesVisited == performanceSourceCount
                    && reuseMetrics.LastCacheHitAllocatedBytes
                        < firstScanMetrics.LastFullScanAllocatedBytes;
                recoveredReferencePerformance = new
                {
                    catalogPaths = performanceCatalogCount,
                    outputs = performanceSourceCount,
                    first = new
                    {
                        milliseconds = firstScanMetrics.LastFullScanMilliseconds,
                        allocatedBytes = firstScanMetrics.LastFullScanAllocatedBytes,
                        fullScans = firstScanMetrics.FullScans,
                        cacheHits = firstScanMetrics.CacheHits,
                        catalogPathsVisited = firstScanMetrics.CatalogPathsVisited,
                        outputFilesVisited = firstScanMetrics.OutputFilesVisited,
                        sourceSignatureChecks = firstScanMetrics.SourceSignatureChecks,
                    },
                    reuse = new
                    {
                        milliseconds = reuseMetrics.LastCacheHitMilliseconds,
                        allocatedBytes = reuseMetrics.LastCacheHitAllocatedBytes,
                        fullScans = reuseMetrics.FullScans,
                        cacheHits = reuseMetrics.CacheHits,
                        catalogPathsVisited = reuseMetrics.CatalogPathsVisited,
                        outputFilesVisited = reuseMetrics.OutputFilesVisited,
                        sourceSignatureChecks = reuseMetrics.SourceSignatureChecks,
                    },
                };
                recoveredReferenceDiagnostic +=
                    $";cache={firstScanMetrics.LastFullScanMilliseconds:F1}ms/"
                    + $"{reuseMetrics.LastCacheHitMilliseconds:F1}ms;"
                    + $"alloc={firstScanMetrics.LastFullScanAllocatedBytes}/"
                    + $"{reuseMetrics.LastCacheHitAllocatedBytes};"
                    + $"scans={reuseMetrics.FullScans};hits={reuseMetrics.CacheHits}";
                window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    requests.Add($"{request.Method.Method} {route}");
                    if (request.Method == HttpMethod.Get)
                    {
                        if (route.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal)
                            && galleryReadinessRelease is not null)
                        {
                            galleryReadinessEntered?.TrySetResult(true);
                            await galleryReadinessRelease.Task.WaitAsync(token);
                        }
                        if (route.EndsWith("/api/enhance/health", StringComparison.Ordinal))
                        {
                            return JsonResponse(HttpStatusCode.OK, new
                            {
                                capabilities = new
                                {
                                    durableEnqueueInboxV1 = new
                                    {
                                        ready = true,
                                        protocolVersion = 1,
                                        backendGeneration = "json-v1",
                                    },
                                    photorealPromptControlsV2 = true,
                                    atomicImageEnqueueNext = true,
                                    photorealSourceUpscale = true,
                                    recoveredPhotorealSourceUpscaleV1 = true,
                                    photorealSeedControlV1 =
                                        photorealSeedCapabilityAvailable,
                                },
                            });
                        }
                        return JsonResponse(
                            HttpStatusCode.OK,
                            new { jobs = new[] { photorealJob, upscaleJob } });
                    }
                    if (request.Method == HttpMethod.Post
                        && route.EndsWith("/api/enhance/jobs", StringComparison.Ordinal))
                    {
                        string bodyText = request.Content is null
                            ? ""
                            : await request.Content.ReadAsStringAsync(token);
                        createBodies.Add(bodyText);
                        using JsonDocument requestDocument = JsonDocument.Parse(bodyText);
                        JsonElement requestBody = requestDocument.RootElement;
                        string requestOperation = requestBody.TryGetProperty(
                                "operation",
                                out JsonElement operationElement)
                            ? operationElement.GetString() ?? "upscale"
                            : "upscale";
                        string requestSource = requestBody.GetProperty("sourceId")
                            .GetString() ?? sourcePath;
                        if (string.Equals(
                                requestOperation,
                                "photoreal",
                                StringComparison.Ordinal))
                        {
                            createBody = bodyText;
                        }
                        var requestSourceInfo = new FileInfo(requestSource);
                        string jobId = $"{requestOperation}-smoke-job-{createBodies.Count}";
                        string requestId = request.Headers.TryGetValues(
                                "Idempotency-Key",
                                out IEnumerable<string>? requestIds)
                            ? requestIds.Single()
                            : "missing-request-id";
                        return JsonResponse(HttpStatusCode.Accepted, new
                        {
                            job = new
                            {
                                id = jobId,
                                operation = requestOperation,
                                sourceId = requestSource,
                                sourcePath = requestSource,
                                sourceSignature = new
                                {
                                    size = requestSourceInfo.Length,
                                    mtimeMs = new DateTimeOffset(
                                        requestSourceInfo.LastWriteTimeUtc)
                                        .ToUnixTimeMilliseconds(),
                                },
                                adapterId = requestBody.GetProperty("adapterId")
                                    .GetString(),
                                status = "queued",
                                progress = 0,
                            },
                            receipt = new
                            {
                                idempotencyKey = requestId,
                                jobId,
                            },
                        });
                    }
                    return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected smoke route" });
                });
                const string customPrompt = "custom adult photoreal portrait";
                const string customEmptyPrompt = "fallback adult photoreal portrait";
                const string customNegativePrompt = "anime, illustration, forced smile";
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.55,
                    12,
                    1280,
                    customPrompt,
                    emptyPrompt: customEmptyPrompt,
                    negativePrompt: customNegativePrompt);
                veryHighQualityContract = window.ModalPhotorealSettingsForSmoke.Steps == 12;
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.55,
                    8,
                    1280,
                    customPrompt,
                    emptyPrompt: customEmptyPrompt,
                    negativePrompt: customNegativePrompt);
                bool customPromptApplied = window.ModalPhotorealSettingsForSmoke.Prompt == customPrompt;
                window.SetPhotorealPreservationScanForSmoke(true);
                var beforeBuiltInStyle = window.ModalPhotorealSettingsForSmoke;
                var beforeBuiltInSeed = window.PhotorealSeedForSmoke;
                bool builtInStyleCatalogContract =
                    window.BuiltInPhotorealStyleIdsForSmoke.SequenceEqual(
                        new[]
                        {
                            "soft-beauty-glamour",
                            "beauty-natural",
                            "lifestyle-beauty",
                            "clean-beauty",
                            "cinematic-glamour",
                            "wet-underwater-beauty",
                        },
                        StringComparer.Ordinal);
                bool builtInStyleSelected =
                    window.SelectBuiltInPhotorealStyleForSmoke(
                        "soft-beauty-glamour");
                var appliedBuiltInStyle = window.ModalPhotorealSettingsForSmoke;
                bool builtInStylePromptOnlyContract = builtInStyleSelected
                    && appliedBuiltInStyle.Prompt.Contains(
                        "soft beauty glamour photography",
                        StringComparison.Ordinal)
                    && appliedBuiltInStyle.EmptyPrompt == appliedBuiltInStyle.Prompt
                    && appliedBuiltInStyle.LoraEnabled == beforeBuiltInStyle.LoraEnabled
                    && Math.Abs(
                        appliedBuiltInStyle.Strength
                            - beforeBuiltInStyle.Strength) < 0.001
                    && Math.Abs(
                        appliedBuiltInStyle.CfgScale
                            - beforeBuiltInStyle.CfgScale) < 0.001
                    && appliedBuiltInStyle.Steps == beforeBuiltInStyle.Steps
                    && appliedBuiltInStyle.MaxDimension
                        == beforeBuiltInStyle.MaxDimension
                    && appliedBuiltInStyle.NegativePrompt
                        == beforeBuiltInStyle.NegativePrompt
                    && appliedBuiltInStyle.NegativePromptEnabled
                        == beforeBuiltInStyle.NegativePromptEnabled
                    && window.PhotorealPreservationScanForSmoke
                        is (true, true)
                    && window.PhotorealSeedForSmoke == beforeBuiltInSeed
                    && window.BuiltInPhotorealStyleDeleteDisabledForSmoke;
                window.FlushStateForSmoke();
                bool builtInStyleReloadContract;
                var builtInReloadWindow = new MainWindow();
                try
                {
                    builtInReloadWindow.SuppressStatePersistence();
                    var reloadedBuiltInStyle =
                        builtInReloadWindow.ModalPhotorealSettingsForSmoke;
                    builtInStyleReloadContract = string.Equals(
                            builtInReloadWindow.SelectedBuiltInPhotorealStyleIdForSmoke,
                            "soft-beauty-glamour",
                            StringComparison.Ordinal)
                        && reloadedBuiltInStyle.Prompt
                            == appliedBuiltInStyle.Prompt
                        && reloadedBuiltInStyle.EmptyPrompt
                            == appliedBuiltInStyle.EmptyPrompt
                        && reloadedBuiltInStyle.NegativePrompt
                            == appliedBuiltInStyle.NegativePrompt
                        && builtInReloadWindow.PhotorealSeedForSmoke
                            == beforeBuiltInSeed;
                }
                finally
                {
                    builtInReloadWindow.Close();
                }
                const string styleName = "Soft Japanese portrait";
                const string stylePrompt = "adult Japanese portrait preserving the source expression";
                const string styleEmptyPrompt = "same scene as a photograph";
                const string styleNegativePrompt = "anime, illustration, smile";
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.4,
                    6,
                    1024,
                    stylePrompt,
                    1.35,
                    styleEmptyPrompt,
                    styleNegativePrompt,
                    loraEnabled: false);
                bool styleSaved = window.SavePhotorealStyleForSmoke(styleName);
                window.FlushStateForSmoke();
                AiStyleDocument? persistedStyleState =
                    JsonSerializer.Deserialize<AiStyleDocument>(
                        // aiStylePath is a fixed child of this smoke's
                        // app-created TEMP store root.
                        // codeql[cs/path-injection]
                        File.ReadAllText(aiStylePath));
                PhotorealStyleState? persistedStyle =
                    persistedStyleState?.PhotorealStyles?.SingleOrDefault();
                stylePersistenceContract = string.Equals(
                        window.AiStylePathForSmoke,
                        aiStylePath,
                        StringComparison.OrdinalIgnoreCase)
                    && persistedStyle is not null
                    && persistedStyle.Name == styleName
                    && persistedStyle.LoraEnabled == false
                    && Math.Abs(persistedStyle.Strength - 0.4) < 0.001
                    && persistedStyle.StructureStrength is null
                    && Math.Abs((persistedStyle.CfgScale ?? 0) - 1.35) < 0.001
                    && persistedStyle.Steps == 6
                    && persistedStyle.MaxDimension == 1024
                    && persistedStyle.Prompt == stylePrompt
                    && persistedStyle.EmptyPrompt == styleEmptyPrompt
                    && persistedStyle.NegativePrompt == styleNegativePrompt
                    && persistedStyleState?.SelectedPhotorealStyleName == styleName;
                const string futureStyleField = "FuturePromptMode";
                if (persistedStyle is not null && persistedStyleState is not null)
                {
                    persistedStyle.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        [futureStyleField] = JsonSerializer.SerializeToElement("future-mode"),
                    };
                    // The fixture path is the fixed AI Style child already
                    // proven above to be the window's resolved store.
                    // codeql[cs/path-injection]
                    File.WriteAllText(
                        aiStylePath,
                        JsonSerializer.Serialize(persistedStyleState));
                }
                var reloadedStyleWindow = new MainWindow();
                try
                {
                    var reloadedStyleSettings = reloadedStyleWindow.ModalPhotorealSettingsForSmoke;
                    bool styleResaved =
                        reloadedStyleWindow.SavePhotorealStyleForSmoke(styleName);
                    AiStyleDocument? roundTrippedStyleState =
                        JsonSerializer.Deserialize<AiStyleDocument>(
                            // The reloaded window must resolve this same fixed
                            // app-created TEMP child before the assertion passes.
                            // codeql[cs/path-injection]
                            File.ReadAllText(aiStylePath));
                    JsonElement futureStyleValue = default;
                    bool futureStyleFieldPreserved = roundTrippedStyleState?.PhotorealStyles?
                        .SingleOrDefault()?.ExtensionData?
                        .TryGetValue(futureStyleField, out futureStyleValue) == true
                        && futureStyleValue.GetString() == "future-mode";
                    reloadedStyleWindow.SuppressStatePersistence();
                    styleReloadContract = string.Equals(
                            reloadedStyleWindow.AiStylePathForSmoke,
                            aiStylePath,
                            StringComparison.OrdinalIgnoreCase)
                        && reloadedStyleWindow.PhotorealStyleNamesForSmoke.Contains(
                            styleName,
                            StringComparer.OrdinalIgnoreCase)
                        && Math.Abs(reloadedStyleSettings.Strength - 0.4) < 0.001
                        && !reloadedStyleSettings.LoraEnabled
                        && Math.Abs(reloadedStyleSettings.CfgScale - 1.35) < 0.001
                        && reloadedStyleSettings.Steps == 6
                        && reloadedStyleSettings.MaxDimension == 1024
                        && reloadedStyleSettings.Prompt == stylePrompt
                        && reloadedStyleSettings.EmptyPrompt == styleEmptyPrompt
                        && reloadedStyleSettings.NegativePrompt == styleNegativePrompt
                        && styleResaved
                        && futureStyleFieldPreserved;
                }
                finally
                {
                    reloadedStyleWindow.Close();
                }
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.7,
                    12,
                    768,
                    "temporary custom settings");
                bool styleSelected = window.SelectPhotorealStyleForSmoke(styleName);
                var appliedStyle = window.ModalPhotorealSettingsForSmoke;
                bool styleApplied = Math.Abs(appliedStyle.Strength - 0.4) < 0.001
                    && !appliedStyle.LoraEnabled
                    && Math.Abs(appliedStyle.CfgScale - 1.35) < 0.001
                    && appliedStyle.Steps == 6
                    && appliedStyle.MaxDimension == 1024
                    && appliedStyle.Prompt == stylePrompt
                    && appliedStyle.EmptyPrompt == styleEmptyPrompt
                    && appliedStyle.NegativePrompt == styleNegativePrompt;
                bool styleDeleted = window.DeleteSelectedPhotorealStyleForSmoke();
                styleContract = window.PhotorealStyleSurfaceForSmoke
                    && builtInStyleCatalogContract
                    && builtInStylePromptOnlyContract
                    && builtInStyleReloadContract
                    && styleSaved
                    && styleSelected
                    && styleApplied
                    && styleDeleted
                    && !window.PhotorealStyleNamesForSmoke.Contains(styleName, StringComparer.OrdinalIgnoreCase);
                const string kreaEngineId =
                    "comfyui-krea2-anything2real-v3-photoreal";
                window.SelectPhotorealEngineForSmoke(kreaEngineId);
                bool kreaSelected = window.PhotorealEngineForSmoke is
                    (kreaEngineId, true, true);
                bool builtInStyleSelectedWithKrea =
                    window.SelectBuiltInPhotorealStyleForSmoke(
                        "soft-beauty-glamour");
                bool styleDidNotChangeEngine =
                    window.PhotorealEngineForSmoke.EngineId == kreaEngineId;
                window.FlushStateForSmoke();
                var engineReloadWindow = new MainWindow();
                try
                {
                    engineReloadWindow.SuppressStatePersistence();
                    photorealEngineContract = kreaSelected
                        && builtInStyleSelectedWithKrea
                        && styleDidNotChangeEngine
                        && engineReloadWindow.PhotorealEngineForSmoke.EngineId
                            == kreaEngineId;
                }
                finally
                {
                    engineReloadWindow.Close();
                }
                window.SelectPhotorealEngineForSmoke(
                    "comfyui-flux2-photoreal");
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.55,
                    8,
                    1280,
                    "",
                    1.25,
                    "",
                    "");
                window.FlushStateForSmoke();
                var blankReloadWindow = new MainWindow();
                try
                {
                    blankReloadWindow.SuppressStatePersistence();
                    var blankReloadSettings = blankReloadWindow.ModalPhotorealSettingsForSmoke;
                    explicitBlankPersistenceContract = blankReloadSettings.Prompt.Length == 0
                        && blankReloadSettings.EmptyPrompt.Length == 0
                        && blankReloadSettings.NegativePrompt.Length == 0
                        && blankReloadSettings.EffectivePrompt.Length == 0;
                }
                finally
                {
                    blankReloadWindow.Close();
                }
                File.WriteAllText(
                    environment["PHOTOVIEWER_WPF_STATE_PATH"],
                    JsonSerializer.Serialize(new
                    {
                        Version = 2,
                        PhotorealStrength = 0.55,
                        PhotorealCfgScale = 1.25,
                        PhotorealSteps = 8,
                        PhotorealMaxDimension = 1280,
                        PhotorealPrompt = "legacy positive prompt",
                    }));
                var legacyStateWindow = new MainWindow();
                try
                {
                    legacyStateWindow.SuppressStatePersistence();
                    var legacySettings = legacyStateWindow.ModalPhotorealSettingsForSmoke;
                    legacyPromptMigrationContract = !legacySettings.LoraEnabled
                        && legacySettings.Prompt == "legacy positive prompt"
                        && legacySettings.EmptyPrompt == legacyStateWindow.DefaultModalPhotorealEmptyPromptForSmoke
                        && legacySettings.NegativePrompt == legacyStateWindow.DefaultModalPhotorealNegativePromptForSmoke;
                }
                finally
                {
                    legacyStateWindow.Close();
                }
                const string appSettingsPrompt = "adult Japanese portrait with unchanged expression";
                const string appSettingsEmptyPrompt = "fallback from app settings";
                const string appSettingsNegativePrompt = "anime, illustration, smile";
                window.SetAppPhotorealPromptForSmoke(appSettingsPrompt);
                window.SetAppPhotorealEmptyPromptForSmoke(appSettingsEmptyPrompt);
                window.SetAppPhotorealNegativePromptForSmoke(appSettingsNegativePrompt);
                bool appToModalSynchronized =
                    window.ModalPhotorealSettingsForSmoke.Prompt == appSettingsPrompt
                    && window.ModalPhotorealSettingsForSmoke.EmptyPrompt == appSettingsEmptyPrompt
                    && window.ModalPhotorealSettingsForSmoke.NegativePrompt == appSettingsNegativePrompt;
                window.ResetAppPhotorealPromptForSmoke();
                window.ResetAppPhotorealEmptyPromptForSmoke();
                window.ResetAppPhotorealNegativePromptForSmoke();
                appSettingsPromptContract = window.AppPhotorealPromptSurfaceForSmoke
                    && appToModalSynchronized
                    && window.ModalPhotorealSettingsForSmoke.Prompt == window.DefaultModalPhotorealPromptForSmoke
                    && window.ModalPhotorealSettingsForSmoke.EmptyPrompt == window.DefaultModalPhotorealEmptyPromptForSmoke
                    && window.ModalPhotorealSettingsForSmoke.NegativePrompt == window.DefaultModalPhotorealNegativePromptForSmoke;
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.3,
                    12,
                    768,
                    "temporary reset contract",
                    1.75,
                    "temporary empty prompt",
                    "temporary negative prompt");
                window.ResetAppPhotorealSettingsForSmoke();
                var resetSettings = window.ModalPhotorealSettingsForSmoke;
                appSettingsControlsContract = window.AppPhotorealSettingsSurfaceForSmoke
                    && !resetSettings.LoraEnabled
                    && Math.Abs(resetSettings.Strength - 0.4) < 0.001
                    && Math.Abs(resetSettings.CfgScale - 1.0) < 0.001
                    && resetSettings.Steps == 8
                    && resetSettings.MaxDimension == 1280
                    && resetSettings.Prompt == window.DefaultModalPhotorealPromptForSmoke
                    && resetSettings.EmptyPrompt == window.DefaultModalPhotorealEmptyPromptForSmoke
                    && resetSettings.NegativePrompt == window.DefaultModalPhotorealNegativePromptForSmoke;
                const string publicFallbackPrompt =
                    "Convert the supplied image into a faithful realistic photograph while preserving its visible subject and composition.";
                defaultPromptContract = string.Equals(
                        window.DefaultModalPhotorealPromptForSmoke,
                        publicFallbackPrompt,
                        StringComparison.Ordinal)
                    && string.Equals(
                        window.DefaultModalPhotorealEmptyPromptForSmoke,
                        publicFallbackPrompt,
                        StringComparison.Ordinal)
                    && string.IsNullOrEmpty(
                        window.DefaultModalPhotorealNegativePromptForSmoke);
                const string nippleTexturePrompt =
                    "realistic nipple and areola texture with subtle Montgomery glands, fine natural creases, and shallow indentations";
                var mappingRows = new[]
                {
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "表情",
                        SourceTag = "troubled eyebrows",
                        OutputPrompt = "brows angled upward toward the center",
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "口",
                        SourceTag = "open mouth",
                        OutputPrompt = "lips separated",
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "赤面",
                        SourceTag = "blush",
                        OutputPrompt = "natural cheek flush",
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "赤面",
                        SourceTag = "full-face blush",
                        OutputPrompt = "natural cheek flush",
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "拘束具",
                        SourceTag = "x-cross (bdsm)",
                        OutputPrompt = "X-shaped restraint frame",
                        ExtensionData = new Dictionary<string, JsonElement>
                        {
                            ["future"] = JsonDocument.Parse("{\"kept\":true}")
                                .RootElement.Clone(),
                        },
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "表情",
                        SourceTag = "embarrassed",
                        OutputPrompt = "subtle embarrassed tension",
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "body detail",
                        SourceTag = "nipple",
                        OutputPrompt = nippleTexturePrompt,
                    },
                    new PhotorealPromptMappingState
                    {
                        Enabled = true,
                        Category = "body detail",
                        SourceTag = "puffy nipples",
                        OutputPrompt = "puffy nipples",
                    },
                };
                string mappedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting, lips separated",
                        "((troubled eyebrows:1.3)), (open mouth, blush:1.2), full-face blush, x-cross \\(bdsm\\)",
                        mappingRows);
                string breakSeparatedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "unused tag\nBREAK\nembarrassed",
                        mappingRows);
                string underscoreMappedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "open_mouth",
                        mappingRows);
                string compoundNippleMappedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "covered nipples, super_detailed_areola, nipple_pinch",
                        mappingRows);
                string exactAndCompoundNippleMappedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "nipple, puffy_nipples, areola slip",
                        mappingRows);
                string unrelatedNippleWordPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "nippleless costume",
                        mappingRows);
                string absentNipplePrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "anti-nipple accessory, no areola",
                        mappingRows);
                string existingNippleTexturePrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        nippleTexturePrompt,
                        "super_detailed_areola",
                        mappingRows);
                string pluralFallbackPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "super_detailed_nipples",
                        [
                            new PhotorealPromptMappingState
                            {
                                Enabled = true,
                                Category = "body detail",
                                SourceTag = "nipples",
                                OutputPrompt = nippleTexturePrompt,
                            },
                        ]);
                string disabledNippleTexturePrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        "neutral lighting",
                        "puffy_nipples",
                        [
                            new PhotorealPromptMappingState
                            {
                                Enabled = true,
                                Category = "body detail",
                                SourceTag = "puffy nipples",
                                OutputPrompt = "puffy nipples",
                            },
                        ]);
                string lengthCappedPrompt = PhotoViewer.Wpf.MainWindow
                    .ComposeMappedPhotorealPromptForSmoke(
                        new string('x', 1_980),
                        "open mouth, embarrassed",
                        mappingRows);
                bool underscoreDuplicatesRejected =
                    !PhotoViewer.Wpf.MainWindow
                        .TryValidatePhotorealPromptMappingsForEditor(
                            [
                                new PhotorealPromptMappingState
                                {
                                    Enabled = true,
                                    Category = "表記揺れ",
                                    SourceTag = "wet_skin",
                                    OutputPrompt = "wet skin",
                                },
                                new PhotorealPromptMappingState
                                {
                                    Enabled = true,
                                    Category = "表記揺れ",
                                    SourceTag = "wet skin",
                                    OutputPrompt = "wet skin",
                                },
                            ],
                            out _);
                window.RestorePhotorealPromptMappingsForSmoke(
                    [],
                    defaultsRevision: 0);
                bool emptyMappingPersistsAsEmpty =
                    window.PhotorealPromptMappingCountForSmoke == 0;
                window.RestorePhotorealPromptMappingsForSmoke(mappingRows);
                IReadOnlyList<PhotorealPromptMappingState> mappingSnapshot =
                    window.SnapshotPhotorealPromptMappingsForSmoke();
                bool extensionPreserved = mappingSnapshot.FirstOrDefault(
                    static row => row.SourceTag == "x-cross (bdsm)")?
                    .ExtensionData?.ContainsKey("future") == true;
                window.RestorePhotorealPromptMappingsForSmoke(null);
                bool defaultsLoaded =
                    window.PhotorealPromptMappingCountForSmoke > 0;
                window.RestorePhotorealPromptMappingsForSmoke(
                    [
                        new PhotorealPromptMappingState
                        {
                            Enabled = true,
                            Category = "表情・顔",
                            SourceTag = "troubled eyebrows",
                            OutputPrompt = "slightly raised inner eyebrow ends",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = false,
                            Category = "カスタム",
                            SourceTag = "custom preserved tag",
                            OutputPrompt = "custom preserved output",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = true,
                            Category = "表情・顔",
                            SourceTag = "light smile",
                            OutputPrompt = "light smile",
                        },
                    ],
                    defaultsRevision: 0);
                IReadOnlyList<PhotorealPromptMappingState> migratedMappings =
                    window.SnapshotPhotorealPromptMappingsForSmoke();
                promptMappingDefaultsMigrationContract =
                    migratedMappings.Count > mappingRows.Length
                    && migratedMappings.Count <= 256
                    && migratedMappings.Any(static row =>
                        !row.Enabled
                        && row.SourceTag == "troubled eyebrows"
                        && row.OutputPrompt ==
                            "barely raised inner brow ends, relaxed forehead")
                    && migratedMappings.Any(static row =>
                        row.SourceTag == "profile"
                        && row.OutputPrompt == "face shown in side profile")
                    && migratedMappings.Any(static row =>
                        row.SourceTag == "wet hair"
                        && row.OutputPrompt ==
                            "wet hair clumped into damp strands")
                    && migratedMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "nipple rub"
                        && row.OutputPrompt ==
                            "fingertips visibly rubbing the nipple")
                    && migratedMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "nipple flick"
                        && row.OutputPrompt ==
                            "a fingertip visibly touching the nipple")
                    && migratedMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "nipple tweak"
                        && row.OutputPrompt ==
                            "nipples visibly pinched between fingertips")
                    && migratedMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "nipple"
                        && row.OutputPrompt ==
                            "realistic nipple and areola texture with subtle Montgomery glands, fine natural creases, and shallow indentations")
                    && migratedMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "nipples"
                        && row.OutputPrompt ==
                            "realistic nipple and areola texture with subtle Montgomery glands, fine natural creases, and shallow indentations")
                    && migratedMappings.Any(static row =>
                        !row.Enabled
                        && row.SourceTag == "custom preserved tag"
                        && row.OutputPrompt == "custom preserved output")
                    && migratedMappings.Any(static row =>
                        !row.Enabled
                        && row.SourceTag == "light smile"
                        && row.OutputPrompt == "light smile");
                window.RestorePhotorealPromptMappingsForSmoke(
                    [
                        new PhotorealPromptMappingState
                        {
                            Enabled = true,
                            Category = "表情・顔",
                            SourceTag = "light smile",
                            OutputPrompt = "light smile",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = true,
                            Category = "拘束具",
                            SourceTag = "ball gag",
                            OutputPrompt = "ball gag",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = false,
                            Category = "表情・顔",
                            SourceTag = "looking at viewer",
                            OutputPrompt = "looking at viewer",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = false,
                            SourceTag = "shiny skin",
                            OutputPrompt = "moist skin with realistic highlights",
                        },
                        new PhotorealPromptMappingState
                        {
                            Enabled = true,
                            SourceTag = "open mouth",
                            OutputPrompt = "open mouth",
                        },
                    ],
                    defaultsRevision: 1);
                IReadOnlyList<PhotorealPromptMappingState> revisionTwoMappings =
                    window.SnapshotPhotorealPromptMappingsForSmoke();
                promptMappingDefaultsMigrationContract =
                    promptMappingDefaultsMigrationContract
                    && revisionTwoMappings.Count <= 256
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "light smile"
                        && row.OutputPrompt == "light smile")
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "ball gag"
                        && row.OutputPrompt.StartsWith(
                            "a clearly visible spherical ball",
                            StringComparison.Ordinal))
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "looking at viewer"
                        && row.OutputPrompt.Contains(
                            "direct eye contact",
                            StringComparison.Ordinal))
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "shiny skin"
                        && row.OutputPrompt.StartsWith(
                            "subtle natural skin sheen",
                            StringComparison.Ordinal))
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "open mouth"
                        && row.OutputPrompt ==
                            "lips slightly parted with a small natural opening")
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "ballgag")
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "blindfold")
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "breasts on glass")
                    && revisionTwoMappings.Any(static row =>
                        row.Enabled
                        && row.SourceTag == "looking away");
                window.RestorePhotorealPromptMappingsForSmoke(mappingRows);
                var mappingEditor = new PhotorealPromptMappingEditorWindow(
                    mappingRows,
                    mappingRows);
                promptMappingDirectToggleContract =
                    mappingEditor.DirectEnabledToggleContractForSmoke();
                promptMappingEditRefreshContract =
                    await mappingEditor.FilterRefreshDuringEditContractForSmokeAsync();
                promptMappingButtonContrastContract =
                    PhotorealPromptMappingEditorWindow
                        .ActionButtonContrastContractForSmoke();
                mappingEditor.Close();
                promptMappingContract = mappedPrompt ==
                        "neutral lighting, lips separated, brows angled upward toward the center, natural cheek flush, X-shaped restraint frame"
                    && breakSeparatedPrompt ==
                        "neutral lighting, subtle embarrassed tension"
                    && underscoreMappedPrompt ==
                        "neutral lighting, lips separated"
                    && compoundNippleMappedPrompt ==
                        $"neutral lighting, {nippleTexturePrompt}"
                    && exactAndCompoundNippleMappedPrompt ==
                        $"neutral lighting, {nippleTexturePrompt}, puffy nipples"
                    && unrelatedNippleWordPrompt == "neutral lighting"
                    && absentNipplePrompt == "neutral lighting"
                    && existingNippleTexturePrompt == nippleTexturePrompt
                    && pluralFallbackPrompt ==
                        $"neutral lighting, {nippleTexturePrompt}"
                    && disabledNippleTexturePrompt ==
                        "neutral lighting, puffy nipples"
                    && lengthCappedPrompt.Length <= 2_000
                    && lengthCappedPrompt.EndsWith(
                        ", lips separated",
                        StringComparison.Ordinal)
                    && PhotoViewer.Wpf.MainWindow
                        .NormalizeA1111PromptTagForSmoke("wet_skin") ==
                            "wet skin"
                    && underscoreDuplicatesRejected
                    && PhotoViewer.Wpf.MainWindow.NormalizeA1111PromptTagForSmoke(
                        "(((open mouth:1.30)))") == "open mouth"
                    && PhotoViewer.Wpf.MainWindow.NormalizeA1111PromptTagForSmoke(
                        "x-cross \\(bdsm\\)") == "x-cross (bdsm)"
                    && emptyMappingPersistsAsEmpty
                    && extensionPreserved
                    && defaultsLoaded
                    && promptMappingDirectToggleContract
                    && promptMappingEditRefreshContract
                    && promptMappingDefaultsMigrationContract
                    && promptMappingButtonContrastContract;
                EnhancementCompanionLaunchContractSmokeSnapshot companionLaunch =
                    PhotoViewer.Wpf.MainWindow.EnhancementCompanionLaunchContractForSmoke();
                independentCompanionContract = !companionLaunch.UseShellExecute
                    && companionLaunch.CreateNoWindow
                    && !companionLaunch.RedirectStandardOutput
                    && !companionLaunch.RedirectStandardError
                    && !companionLaunch.HasExplicitWorkingDirectory
                    && !companionLaunch.HasExternalOwnerPid
                    && companionLaunch.HasInheritedAuthenticationToken
                    && companionLaunch.HasInheritedInstanceId
                    && companionLaunch.NoOpen == "1"
                    && companionLaunch.ComfyAutostart == "0"
                    && companionLaunch.H3PowerShellPath == Path.Combine(
                        Environment.SystemDirectory,
                        "WindowsPowerShell",
                        "v1.0",
                        "powershell.exe")
                    && companionLaunch.DefersQueueRecovery;
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.55,
                    8,
                    1280,
                    customPrompt,
                    1.25,
                    customEmptyPrompt,
                    customNegativePrompt);
                window.ResetModalPhotorealPromptForSmoke();
                resetPromptContract = customPromptApplied
                    && window.ModalPhotorealSettingsForSmoke.Prompt == window.DefaultModalPhotorealPromptForSmoke
                    && window.AppPhotorealPromptSurfaceForSmoke;
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.55,
                    8,
                    1280,
                    "",
                    1.25,
                    customEmptyPrompt,
                    customNegativePrompt,
                    loraEnabled: false);
                fallbackPromptContract =
                    window.ModalPhotorealSettingsForSmoke.EffectivePrompt == customEmptyPrompt;
                var loraControls = window.PhotorealLoraControlsForSmoke;
                loraToggleContract = !window.ModalPhotorealSettingsForSmoke.LoraEnabled
                    && !loraControls.AppChecked
                    && !loraControls.ModalChecked
                    && !loraControls.AppStrengthEnabled
                    && !loraControls.ModalStrengthEnabled;
                window.SetPhotorealPreservationScanForSmoke(true);
                window.Show();
                await window.LoadFolderSetAsync([imageRoot], commitRecent: false);
                bool loadTimingDefaultOff = !window.ShowLoadTimingForSmoke
                    && !window.LoadTimingVisibleForSmoke
                    && string.IsNullOrEmpty(window.LoadTimingStatusForSmoke);
                window.SetShowLoadTimingForSmoke(true);
                bool loadTimingEnabled = window.ShowLoadTimingForSmoke
                    && window.LoadTimingVisibleForSmoke
                    && !string.IsNullOrWhiteSpace(window.LoadTimingStatusForSmoke);
                window.SetShowLoadTimingForSmoke(false);
                loadTimingContract = loadTimingDefaultOff
                    && loadTimingEnabled
                    && !window.ShowLoadTimingForSmoke
                    && !window.LoadTimingVisibleForSmoke
                    && string.IsNullOrEmpty(window.LoadTimingStatusForSmoke);
                selected = window.SelectFileNameForSmoke(Path.GetFileName(sourcePath));
                opened = window.OpenModalForSmoke();
                ModalMetadataSmokeSnapshot displayedPhotorealMetadata =
                    await window.WaitForModalDisplayedMetadataForSmokeAsync(
                        photorealOutputPath);
                string[] displayedVersionLabels =
                    window.ModalDisplayVersionLabelsForSmoke;
                displayedVersionMetadataDiagnostic = new
                {
                    displayedPhotorealMetadata.MetadataCurrent,
                    displayedPhotorealMetadata.Prompt,
                    displayedPhotorealMetadata.NegativePrompt,
                    displayedPhotorealMetadata.Settings,
                    Labels = displayedVersionLabels,
                };
                displayedVersionMetadataContract =
                    displayedPhotorealMetadata.MetadataCurrent
                    && displayedPhotorealMetadata.Prompt ==
                        "photoreal effective prompt"
                    && displayedPhotorealMetadata.NegativePrompt ==
                        "photoreal effective negative"
                    && displayedPhotorealMetadata.Settings.Contains(
                        "Aibos operation: photoreal",
                        StringComparison.Ordinal)
                    && displayedVersionLabels.SequenceEqual(
                        ["Original", "実写化 1/1", "高画質化 1/1"],
                        StringComparer.Ordinal);
                toolbarContract = window.ModalPhotorealToolbarContractForSmoke
                    && window.ModalPrimaryWorkflowToolbarContractForSmoke
                    && window.HeaderProductVersionContractForSmoke;
                var initialUpscaleSettings = window.UpscaleSettingsForSmoke;
                window.RestoreUpscaleSettingsForSmoke(new ViewerState
                {
                    UpscalePresetId = "photo-detail-x4",
                    UpscaleAdapterId = "comfyui",
                    UpscaleScale = 4d,
                    UpscaleOutputFormat = "webp",
                });
                legacyComfyDefaultMigrationContract =
                    window.UpscaleSettingsForSmoke.AdapterId ==
                        "realesrgan-ncnn";
                window.RestoreUpscaleSettingsForSmoke(new ViewerState
                {
                    UpscalePresetId = "anime-sharp-x2",
                    UpscaleAdapterId = "realesrgan-ncnn",
                    UpscaleScale = 2d,
                    UpscaleOutputFormat = "webp",
                });
                persistedNcnnUpscaleChoiceContract =
                    window.UpscaleSettingsForSmoke.AdapterId ==
                        "realesrgan-ncnn";
                bool selectedNcnn6 = window.SelectModalUpscaleScaleForSmoke(6d);
                var ncnn6Settings = window.UpscaleSettingsForSmoke;
                bool selectedNcnn8 = window.SelectModalUpscaleScaleForSmoke(8d);
                var ncnn8Settings = window.UpscaleSettingsForSmoke;
                ncnnHighScaleSelectionContract = selectedNcnn6
                    && ncnn6Settings.AdapterId == "realesrgan-ncnn"
                    && ncnn6Settings.Scale == 6d
                    && selectedNcnn8
                    && ncnn8Settings.AdapterId == "realesrgan-ncnn"
                    && ncnn8Settings.Scale == 8d;
                window.ConfigureUpscaleSettingsForSmoke(
                    "general-balanced-x4",
                    "comfyui",
                    3d,
                    "png");
                var configuredUpscaleSettings = window.UpscaleSettingsForSmoke;
                bool upscaleSettingsPopupOpened =
                    window.OpenModalUpscaleSettingsForSmoke();
                bool upscaleSettingsPopupClosed =
                    window.CloseTopmostOverlayForSmoke()
                    && !window.ModalUpscaleSettingsVisibleForSmoke;
                ViewerState? persistedUpscaleSettings = JsonSerializer.Deserialize<ViewerState>(
                    File.ReadAllText(environment["PHOTOVIEWER_WPF_STATE_PATH"]));
                bool upscaleSettingsSurfaceAndPersistence =
                    window.UpscaleSettingsSurfaceContractForSmoke
                    && upscaleSettingsPopupOpened
                    && upscaleSettingsPopupClosed
                    && initialUpscaleSettings.AdapterId == "realesrgan-ncnn"
                    && legacyComfyDefaultMigrationContract
                    && persistedNcnnUpscaleChoiceContract
                    && ncnnHighScaleSelectionContract
                    && configuredUpscaleSettings.PresetId == "general-balanced-x4"
                    && configuredUpscaleSettings.AdapterId == "realesrgan-ncnn"
                    && configuredUpscaleSettings.Scale == 3d
                    && configuredUpscaleSettings.OutputFormat == "png"
                    && persistedUpscaleSettings?.UpscalePresetId == "general-balanced-x4"
                    && persistedUpscaleSettings.UpscaleAdapterId == "realesrgan-ncnn"
                    && persistedUpscaleSettings.UpscaleScale == 3d
                    && persistedUpscaleSettings.UpscaleOutputFormat == "png";
                bool initialPhotoreal = string.Equals(
                    window.ModalDisplayPathForSmoke,
                    photorealOutputPath,
                    StringComparison.OrdinalIgnoreCase);
                var photoUpscaleProfile = window.ModalUpscaleProfileForSmoke;
                photoUpscaleProfileContract = photoUpscaleProfile.Ok
                    && photoUpscaleProfile.SourceProducerJobId == "photoreal-version"
                    && photoUpscaleProfile.SourceRecoveredOutputPath is null
                    && photoUpscaleProfile.PresetId == "photo-natural-x2"
                    && photoUpscaleProfile.AdapterId == "realesrgan-ncnn"
                    && photoUpscaleProfile.Scale == 2
                    && string.IsNullOrEmpty(photoUpscaleProfile.Error);
                bool downToUpscale = window.InvokePreviewKeyForSmoke(Key.Down, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        upscaleOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                bool downToOriginal = window.InvokePreviewKeyForSmoke(Key.Down, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase);
                bool upWrapsToUpscale = window.InvokePreviewKeyForSmoke(Key.Up, ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        upscaleOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                versionCycleContract = initialPhotoreal
                    && downToUpscale
                    && downToOriginal
                    && upWrapsToUpscale;
                bool wheelDownToOriginal = window.InvokeModalVersionMouseWheelForSmoke(-120)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase);
                bool wheelUpToUpscale = window.InvokeModalVersionMouseWheelForSmoke(120)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        upscaleOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                versionWheelCycleContract = wheelDownToOriginal && wheelUpToUpscale;
                passive = requests.All(static request => request.StartsWith("GET ", StringComparison.Ordinal));

                bool producerVersionSelected =
                    window.SelectModalEnhancementJobVersionForSmoke(
                        "photoreal-version");
                string sourceFileName = Path.GetFileName(sourcePath);
                bool lastDisplayedThumbnailSelected =
                    producerVersionSelected
                    && string.Equals(
                        window.GalleryThumbnailAssetPathForSmoke(
                            sourceFileName),
                        photorealOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                bool originalThumbnailModeSelected =
                    window.SetUseLastDisplayedImageVersionForThumbnailsForSmoke(
                        false)
                    && string.Equals(
                        window.GalleryThumbnailAssetPathForSmoke(
                            sourceFileName),
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase);
                bool lastDisplayedThumbnailModeRestored =
                    window.SetUseLastDisplayedImageVersionForThumbnailsForSmoke(
                        true)
                    && string.Equals(
                        window.GalleryThumbnailAssetPathForSmoke(
                            sourceFileName),
                        photorealOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                thumbnailVersionPreferenceContract =
                    lastDisplayedThumbnailSelected
                    && originalThumbnailModeSelected
                    && lastDisplayedThumbnailModeRestored;
                var galleryVariantCounts =
                    window.GalleryVariantCountsForSmoke(sourceFileName);
                thumbnailVariantCountContract =
                    galleryVariantCounts.Upscale == 1
                    && galleryVariantCounts.Photoreal == 1
                    && galleryVariantCounts.Video == 0
                    && PhotoViewer.Wpf.MainWindow
                        .ThumbnailVariantCountVisibilityContractForSmoke();
                int modalNextHqBodyIndex = createBodies.Count;
                bool modalNextHqStarted =
                    await window.StartModalContextEnhancementNextForSmokeAsync(
                        "upscale");
                JsonElement modalNextHqBody = ParseRequestBody(
                    createBodies[modalNextHqBodyIndex]);
                modalEnqueueNextDisplayedPhotorealContract =
                    producerVersionSelected
                    && modalNextHqStarted
                    && createBodies.Count == modalNextHqBodyIndex + 1
                    && modalNextHqBody.GetProperty("operation").GetString()
                        == "upscale"
                    && modalNextHqBody.GetProperty("queuePlacement").GetString()
                        == "next"
                    && modalNextHqBody.GetProperty("sourceProducerJobId")
                        .GetString() == "photoreal-version"
                    && modalNextHqBody.GetProperty("presetId").GetString()
                        == "photo-natural-x2"
                    && modalNextHqBody.GetProperty("adapterId").GetString()
                        == "realesrgan-ncnn"
                    && modalNextHqBody.GetProperty("scale").GetDouble() == 2d
                    && string.Equals(
                        modalNextHqBody.GetProperty("sourceId").GetString(),
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase)
                    && !modalNextHqBody.TryGetProperty("prompt", out _)
                    && !modalNextHqBody.TryGetProperty("outputFormat", out _)
                    && !modalNextHqBody.TryGetProperty(
                        "sourceRecoveredOutputPath",
                        out _)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        photorealOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                int producerHqBodyIndex = createBodies.Count;
                bool producerHqStarted =
                    await window.StartModalEnhancementForSmokeAsync();
                JsonElement producerHqBody = ParseRequestBody(
                    createBodies[producerHqBodyIndex]);
                bool producerHqProvenance = producerVersionSelected
                    && producerHqStarted
                    && producerHqBody.GetProperty("operation").GetString()
                        == "upscale"
                    && producerHqBody.GetProperty("presetId").GetString()
                        == "photo-natural-x2"
                    && producerHqBody.GetProperty("adapterId").GetString()
                        == "realesrgan-ncnn"
                    && producerHqBody.GetProperty("scale").GetDouble() == 2d
                    && !producerHqBody.TryGetProperty("outputFormat", out _)
                    && producerHqBody.GetProperty("sourceProducerJobId")
                        .GetString() == "photoreal-version"
                    && !producerHqBody.TryGetProperty(
                        "sourceRecoveredOutputPath",
                        out _)
                    && !producerHqBody.TryGetProperty("prompt", out _);

                window.CloseModalForSmoke();
                bool originalModalReopened = window.OpenModalForSmoke();
                bool originalVersionSelected =
                    window.SelectModalOriginalVersionForSmoke();
                int originalHqBodyIndex = createBodies.Count;
                bool originalHqStarted =
                    await window.StartModalEnhancementForSmokeAsync();
                JsonElement originalHqBody = ParseRequestBody(
                    createBodies[originalHqBodyIndex]);
                bool originalHqProvenance = originalModalReopened
                    && originalVersionSelected
                    && originalHqStarted
                    && originalHqBody.GetProperty("operation").GetString()
                        == "upscale"
                    && originalHqBody.GetProperty("presetId").GetString()
                        == "general-balanced-x4"
                    && originalHqBody.GetProperty("adapterId").GetString()
                        == "realesrgan-ncnn"
                    && originalHqBody.GetProperty("scale").GetDouble() == 3d
                    && originalHqBody.GetProperty("outputFormat").GetString()
                        == "png"
                    && originalHqBody.GetProperty("prompt").GetString()
                        == originalEmbeddedPrompt
                    && !originalHqBody.TryGetProperty(
                        "sourceProducerJobId",
                        out _)
                    && !originalHqBody.TryGetProperty(
                        "sourceRecoveredOutputPath",
                        out _);
                hqPromptProvenanceContract = producerHqProvenance
                    && originalHqProvenance;
                upscaleSettingsContract = upscaleSettingsSurfaceAndPersistence
                    && producerHqProvenance
                    && originalHqProvenance;

                window.CloseModalForSmoke();
                _ = window.OpenModalForSmoke();
                photorealShortcutContract = await window.StartModalPhotorealWithShortcutForSmokeAsync();
                started = photorealShortcutContract;
                queueAddedToastContract = photorealShortcutContract
                    && window.DeleteStatusVisibleForSmoke
                    && window.DeleteStatusForSmoke.Contains(
                        "Jobsの待機列へ追加しました",
                        StringComparison.Ordinal);
                modalPhotorealOperation =
                    window.ModalEnhancementOperationForSmoke == "photoreal";
                int createRequestsBeforeGalleryContext = requests.Count(static request =>
                    request == "POST /api/enhance/jobs");
                window.CloseModalForSmoke();
                galleryContextNoModal = await window.StartGalleryContextEnhancementForSmokeAsync("photoreal");
                galleryContextDirectContract = galleryContextNoModal
                    && requests.Count(static request => request == "POST /api/enhance/jobs")
                        == createRequestsBeforeGalleryContext + 1;
                int createRequestsBeforeEnqueueNext = requests.Count(static request =>
                    request == "POST /api/enhance/jobs");
                bool galleryEnqueueNextNoModal =
                    await window.StartGalleryContextEnhancementForSmokeAsync(
                        "photoreal",
                        enqueueNext: true);
                galleryEnqueueNextContract = galleryEnqueueNextNoModal
                    && requests.Count(static request => request == "POST /api/enhance/jobs")
                        == createRequestsBeforeEnqueueNext + 1;
                string randomPhotorealBody = createBody;
                using JsonDocument randomSeedDocument = JsonDocument.Parse(
                    randomPhotorealBody);
                randomSeedOmitted = !randomSeedDocument.RootElement
                    .TryGetProperty("seed", out _);
                seedDefaultAndSurface = window.PhotorealSeedForSmoke
                        is (false, "0", true)
                    && window.PhotorealSeedSurfaceForSmoke;

                const int fixedPhotorealSeed = int.MaxValue;
                window.ConfigurePhotorealSeedForSmoke(
                    fixedMode: true,
                    value: fixedPhotorealSeed.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                window.FlushStateForSmoke();
                ViewerState? persistedPhotorealSeedState =
                    JsonSerializer.Deserialize<ViewerState>(File.ReadAllText(
                        environment["PHOTOVIEWER_WPF_STATE_PATH"]));
                int fixedSeedBodyIndex = createBodies.Count;
                _ = await window.StartGalleryContextEnhancementForSmokeAsync(
                    "photoreal");
                JsonElement fixedSeedBody = ParseRequestBody(
                    createBodies[fixedSeedBodyIndex]);
                fixedSeedExact = fixedSeedBody.GetProperty("seed")
                        .GetInt32() == fixedPhotorealSeed
                    && persistedPhotorealSeedState?.PhotorealSeedMode == "fixed"
                    && persistedPhotorealSeedState.PhotorealSeedValue
                        == fixedPhotorealSeed;

                window.ConfigurePhotorealSeedForSmoke(
                    fixedMode: true,
                    value: "2147483648");
                int postsBeforeInvalidSeed = createBodies.Count;
                _ = await window.StartGalleryContextEnhancementForSmokeAsync(
                    "photoreal");
                invalidSeedBlocked = createBodies.Count
                        == postsBeforeInvalidSeed
                    && !window.PhotorealSeedForSmoke.Valid
                    && window.PhotorealSeedStatusForSmoke.Contains(
                        "0〜2147483647",
                        StringComparison.Ordinal);

                window.ConfigurePhotorealSeedForSmoke(
                    fixedMode: true,
                    value: "246810");
                photorealSeedCapabilityAvailable = false;
                int postsBeforeMissingSeedCapability = createBodies.Count;
                _ = await window.StartGalleryContextEnhancementForSmokeAsync(
                    "photoreal");
                missingSeedCapabilityBlocked = createBodies.Count
                        == postsBeforeMissingSeedCapability
                    && window.PhotorealSeedStatusForSmoke.Contains(
                        "fixed photoreal seeds",
                        StringComparison.Ordinal);
                photorealSeedCapabilityAvailable = true;
                window.ConfigurePhotorealSeedForSmoke(
                    fixedMode: false,
                    value: "246810");
                photorealSeedContract = randomSeedOmitted
                    && seedDefaultAndSurface
                    && fixedSeedExact
                    && invalidSeedBlocked
                    && missingSeedCapabilityBlocked;
                int ordinaryDoublePostsBefore = createBodies.Count;
                galleryReadinessEntered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                galleryReadinessRelease = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> ordinaryDoubleActivation = window
                    .StartGalleryContextEnhancementDoubleActivationForSmokeAsync(
                        "photoreal");
                await galleryReadinessEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(3));
                galleryReadinessRelease.TrySetResult(true);
                bool ordinaryDoubleCompleted = await ordinaryDoubleActivation;
                bool ordinaryGallerySingleFlight = ordinaryDoubleCompleted
                    && createBodies.Count == ordinaryDoublePostsBefore + 1;
                galleryReadinessEntered = null;
                galleryReadinessRelease = null;

                window.CloseModalForSmoke();
                bool recoveredSelected = window.SelectFileNameForSmoke(
                    Path.GetFileName(recoveredSourcePath));
                bool recoveredOpened = window.OpenModalForSmoke();
                bool recoveredDefault = string.Equals(
                    window.ModalDisplayPathForSmoke,
                    recoveredOutputPath,
                    StringComparison.OrdinalIgnoreCase);
                IReadOnlyList<string> recoveredLabelsBeforePoll =
                    window.ModalDisplayVersionLabelsForSmoke;
                bool recoveredCycledToOriginal =
                    window.InvokePreviewKeyForSmoke(
                        Key.Down,
                        ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        recoveredSourcePath,
                        StringComparison.OrdinalIgnoreCase);
                bool recoveredCycledBack =
                    window.InvokePreviewKeyForSmoke(
                        Key.Up,
                        ModifierKeys.Control)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        recoveredOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                recoveredReferenceExact = recoveredReferenceExact
                    && recoveredSelected
                    && recoveredOpened
                    && recoveredDefault
                    && recoveredCycledToOriginal
                    && recoveredCycledBack
                    && window.PhotorealizedForFileForSmoke(
                        Path.GetFileName(recoveredSourcePath))
                    && recoveredLabelsBeforePoll.Count == 2
                    && recoveredLabelsBeforePoll[0] == "Original"
                    && recoveredLabelsBeforePoll[1].EndsWith(
                        "1/1",
                        StringComparison.Ordinal);
                bool recoveredCapabilityRefreshed =
                    await window.RefreshModalEnhancementForSmokeAsync();
                var recoveredUpscaleProfile = window.ModalUpscaleProfileForSmoke;
                recoveredReferenceMutationBlocked =
                    !window.DisplayedManagedImageDeleteVerifiedForSmoke
                    && recoveredUpscaleProfile.Ok
                    && recoveredUpscaleProfile.SourceProducerJobId
                        == recoveredJobId
                    && string.Equals(
                        recoveredUpscaleProfile.SourceRecoveredOutputPath,
                        recoveredOutputPath,
                        StringComparison.OrdinalIgnoreCase)
                    && window.GalleryVideoSourceRequestsForSmoke.SequenceEqual(
                        ["original"],
                        StringComparer.Ordinal);
                recoveredHqButtonContract = recoveredCapabilityRefreshed
                    && window.ModalHqButtonEnabledForSmoke
                    && window.ModalPhotorealUpscaleButtonEnabledForSmoke;

                int recoveredGenericBodyIndex = createBodies.Count;
                bool recoveredGenericStarted =
                    await window.StartModalEnhancementForSmokeAsync();
                JsonElement recoveredGenericBody = ParseRequestBody(
                    createBodies[recoveredGenericBodyIndex]);
                window.CloseModalForSmoke();
                bool recoveredExplicitReopened = window.OpenModalForSmoke();
                bool recoveredExplicitRefreshed =
                    await window.RefreshModalEnhancementForSmokeAsync();
                int recoveredExplicitBodyIndex = createBodies.Count;
                bool recoveredExplicitStarted =
                    await window.StartModalPhotorealUpscaleForSmokeAsync();
                JsonElement recoveredExplicitBody = ParseRequestBody(
                    createBodies[recoveredExplicitBodyIndex]);
                window.CloseModalForSmoke();
                int recoveredGalleryBodyIndex = createBodies.Count;
                galleryReadinessEntered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                galleryReadinessRelease = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> recoveredGalleryActivation = window
                    .StartGalleryContextEnhancementDoubleActivationForSmokeAsync(
                        "upscale",
                        requirePhotorealSource: true);
                await galleryReadinessEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(3));
                galleryReadinessRelease.TrySetResult(true);
                bool recoveredGalleryStarted = await recoveredGalleryActivation;
                bool recoveredGallerySingleFlight = createBodies.Count
                    == recoveredGalleryBodyIndex + 1;
                galleryReadinessEntered = null;
                galleryReadinessRelease = null;
                JsonElement recoveredGalleryBody = ParseRequestBody(
                    createBodies[recoveredGalleryBodyIndex]);
                gallerySingleFlightContract = ordinaryGallerySingleFlight
                    && recoveredGallerySingleFlight;
                static bool IsRecoveredHqBody(
                    JsonElement requestBody,
                    string expectedJobId,
                    string expectedOutputPath)
                    => requestBody.GetProperty("operation").GetString()
                            == "upscale"
                        && requestBody.GetProperty("sourceProducerJobId")
                            .GetString() == expectedJobId
                        && string.Equals(
                            requestBody.GetProperty(
                                    "sourceRecoveredOutputPath")
                                .GetString(),
                            expectedOutputPath,
                            StringComparison.OrdinalIgnoreCase)
                        && !requestBody.TryGetProperty("prompt", out _);
                bool recoveredPayloads = recoveredGenericStarted
                    && recoveredExplicitReopened
                    && recoveredExplicitRefreshed
                    && recoveredExplicitStarted
                    && recoveredGalleryStarted
                    && gallerySingleFlightContract
                    && IsRecoveredHqBody(
                        recoveredGenericBody,
                        recoveredJobId,
                        recoveredOutputPath)
                    && IsRecoveredHqBody(
                        recoveredExplicitBody,
                        recoveredJobId,
                        recoveredOutputPath)
                    && IsRecoveredHqBody(
                        recoveredGalleryBody,
                        recoveredJobId,
                        recoveredOutputPath)
                    && recoveredGalleryBody.GetProperty("queuePlacement")
                        .GetString() == "last";
                hqPromptProvenanceContract = hqPromptProvenanceContract
                    && recoveredPayloads
                    && createBodies
                        .Select(ParseRequestBody)
                        .Where(body => body.GetProperty("operation").GetString()
                            == "upscale")
                        .All(body => !body.TryGetProperty("prompt", out _)
                            || body.GetProperty("prompt").GetString()
                                == originalEmbeddedPrompt);

                bool recoveredPollModalReopened = window.OpenModalForSmoke();
                bool recoveredPollCompleted =
                    await window.RefreshModalEnhancementForSmokeAsync();
                IReadOnlyList<string> recoveredLabelsAfterPoll =
                    window.ModalDisplayVersionLabelsForSmoke;
                recoveredReferencePollingPreserved = recoveredPollModalReopened
                    && recoveredPollCompleted
                    && recoveredLabelsAfterPoll.Count == 2
                    && recoveredLabelsAfterPoll[0] == "Original"
                    && recoveredLabelsAfterPoll[1].EndsWith(
                        "1/1",
                        StringComparison.Ordinal)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        recoveredOutputPath,
                        StringComparison.OrdinalIgnoreCase);
                recoveredReferenceDiagnostic +=
                    $";ui={recoveredSelected}/{recoveredOpened}/{recoveredDefault}/"
                    + $"{recoveredLabelsBeforePoll.Count}/"
                    + $"{recoveredLabelsAfterPoll.Count};"
                    + $"path={Path.GetFileName(window.ModalDisplayPathForSmoke)}";
                window.CloseModalForSmoke();
                window.SetPhotorealOnlyFilterForSmoke(true);
                List<string> recoveredFilteredNames =
                    window.FilteredFileNamesForSmoke(100);
                recoveredReferenceExact = recoveredReferenceExact
                    && recoveredFilteredNames.Contains(
                        Path.GetFileName(recoveredSourcePath),
                        StringComparer.OrdinalIgnoreCase)
                    && !recoveredFilteredNames.Contains(
                        Path.GetFileName(hashMismatchSourcePath),
                        StringComparer.OrdinalIgnoreCase)
                    && !recoveredFilteredNames.Contains(
                        Path.GetFileName(knownJobSourcePath),
                        StringComparer.OrdinalIgnoreCase);
                window.SetPhotorealOnlyFilterForSmoke(false);

                int legacyRecoveredPostCount = 0;
                string recoveredCapabilityHealthMode = "missing-capability";
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
                    recoveredCapabilitySender = (request, _) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method == HttpMethod.Get
                        && route.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(recoveredCapabilityHealthMode switch
                        {
                            "404" => JsonResponse(
                                HttpStatusCode.NotFound,
                                new { error = "health route unavailable" }),
                            "unavailable" => JsonResponse(
                                HttpStatusCode.ServiceUnavailable,
                                new { error = "health unavailable" }),
                            "malformed" => JsonResponse(
                                HttpStatusCode.OK,
                                new { capabilities = "invalid" }),
                            _ => JsonResponse(
                                HttpStatusCode.OK,
                                new
                                {
                                    capabilities = new
                                    {
                                        photorealSourceUpscale = true,
                                    },
                                }),
                        });
                    }
                    if (request.Method == HttpMethod.Get)
                    {
                        return Task.FromResult(JsonResponse(
                            HttpStatusCode.OK,
                            new { jobs = Array.Empty<object>() }));
                    }
                    legacyRecoveredPostCount++;
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.Accepted,
                        new { job = new { } }));
                };
                bool recoveredCapabilityModesFailClosed = true;
                foreach (string healthMode in new[]
                    {
                        "missing-capability",
                        "404",
                        "unavailable",
                        "malformed",
                    })
                {
                    recoveredCapabilityHealthMode = healthMode;
                    window.ConfigureModalEnhancementForSmoke(
                        recoveredCapabilitySender);
                    bool selectedForMode = window.SelectFileNameForSmoke(
                        Path.GetFileName(recoveredSourcePath));
                    bool openedForMode = window.OpenModalForSmoke();
                    bool refreshedForMode =
                        await window.RefreshModalEnhancementForSmokeAsync();
                    bool genericStartForMode =
                        await window.StartModalEnhancementForSmokeAsync();
                    bool explicitStartForMode =
                        await window.StartModalPhotorealUpscaleForSmokeAsync();
                    recoveredCapabilityModesFailClosed =
                        recoveredCapabilityModesFailClosed
                        && selectedForMode
                        && openedForMode
                        && refreshedForMode
                        && !window.ModalHqButtonEnabledForSmoke
                        && !window.ModalPhotorealUpscaleButtonEnabledForSmoke
                        && !genericStartForMode
                        && !explicitStartForMode;
                    window.CloseModalForSmoke();
                }
                recoveredHqCapabilityGateContract =
                    recoveredCapabilityModesFailClosed
                    && legacyRecoveredPostCount == 0;

                using JsonDocument legacyHealth = JsonDocument.Parse(
                    "{\"capabilities\":{\"queuedPhotorealPromptUpdate\":true}}");
                legacyPhotorealCapabilitySafe =
                    !PhotoViewer.Wpf.MainWindow.HasPhotorealPromptControlsCapabilityForSmoke(
                        legacyHealth.RootElement);

                using JsonDocument document = JsonDocument.Parse(
                    randomPhotorealBody);
                JsonElement body = document.RootElement;
                requestContract = body.GetProperty("operation").GetString() == "photoreal"
                    && body.GetProperty("presetId").GetString() == "photoreal-balanced"
                    && body.GetProperty("adapterId").GetString() == "comfyui-flux2-photoreal"
                    && !body.GetProperty("loraEnabled").GetBoolean()
                    && Math.Abs(body.GetProperty("strength").GetDouble() - 0.55) < 0.001
                    && Math.Abs(body.GetProperty("cfgScale").GetDouble() - 1.25) < 0.001
                    && body.GetProperty("steps").GetInt32() == 8
                    && body.GetProperty("maxDimension").GetInt32() == 1280
                    && body.GetProperty("prompt").GetString() ==
                        $"{customEmptyPrompt}, brows angled upward toward the center, lips separated, natural cheek flush, X-shaped restraint frame"
                    && body.GetProperty("negativePrompt").GetString() == string.Empty
                    && body.GetProperty("preservationScanEnabled").GetBoolean()
                    && body.GetProperty("queuePlacement").GetString() == "next";
                var negativeOff = window.ModalPhotorealSettingsForSmoke;
                window.SetModalPhotorealNegativePromptEnabledForSmoke(true);
                var negativeOn = window.ModalPhotorealSettingsForSmoke;
                window.SetModalPhotorealNegativePromptEnabledForSmoke(false);
                window.FlushStateForSmoke();
                ViewerState? persistedNegativeToggleState =
                    JsonSerializer.Deserialize<ViewerState>(File.ReadAllText(
                        environment["PHOTOVIEWER_WPF_STATE_PATH"]));
                negativePromptContract =
                    body.GetProperty("negativePrompt").GetString() == string.Empty
                    && negativeOff.NegativePrompt == customNegativePrompt
                    && !negativeOff.NegativePromptEnabled
                    && negativeOff.EffectiveNegativePrompt.Length == 0
                    && negativeOn.NegativePrompt == customNegativePrompt
                    && negativeOn.NegativePromptEnabled
                    && negativeOn.EffectiveNegativePrompt == customNegativePrompt
                    && persistedNegativeToggleState?.PhotorealNegativePromptEnabled == false;
                photorealPreservationScanContract =
                    body.GetProperty("preservationScanEnabled").GetBoolean()
                    && window.PhotorealPreservationScanForSmoke is (true, true)
                    && persistedNegativeToggleState?.PhotorealPreservationScanEnabled
                        == true;
                loraToggleContract = loraToggleContract
                    && !body.GetProperty("loraEnabled").GetBoolean();
                structureRemovedContract =
                    !body.TryGetProperty("structureStrength", out _);
                sharedQueueRoute = requests.Any(static request => request == "POST /api/enhance/jobs")
                    && requests.All(static request => !request.Contains("/photoreal/", StringComparison.Ordinal));
                window.CloseModalForSmoke();
                bool favoriteSourceSelected = window.SelectFileNameForSmoke(
                    Path.GetFileName(sourcePath));
                bool favoriteModalOpened = window.OpenModalForSmoke();
                bool favoritePhotorealSelected =
                    window.SelectModalEnhancementJobVersionForSmoke(
                        "photoreal-version");
                bool photorealFavoriteRaised = favoriteSourceSelected
                    && favoriteModalOpened
                    && favoritePhotorealSelected
                    && window.ModalFavoriteLevelForSmoke == 0
                    && window.SelectedFavoriteLevelForSmoke == 0
                    && window.AdjustModalFavoriteForSmoke(1)
                    && window.AdjustModalFavoriteForSmoke(1)
                    && window.ModalFavoriteLevelForSmoke == 2
                    && window.SelectedFavoriteLevelForSmoke == 0;
                bool originalFavoriteAbsentAfterPhotoreal = false;
                string favoritesPath =
                    environment["PHOTOVIEWER_WPF_FAVORITES_PATH"];
                if (File.Exists(favoritesPath))
                {
                    using JsonDocument favoritesAfterPhotoreal =
                        JsonDocument.Parse(File.ReadAllText(favoritesPath));
                    JsonElement favoriteRoot =
                        favoritesAfterPhotoreal.RootElement;
                    originalFavoriteAbsentAfterPhotoreal =
                        favoriteRoot.TryGetProperty(
                            photorealOutputPath,
                            out JsonElement photorealFavorite)
                        && photorealFavorite.GetInt32() == 2
                        && !favoriteRoot.TryGetProperty(sourcePath, out _);
                }
                bool originalFavoriteRaised =
                    window.SelectModalOriginalVersionForSmoke()
                    && window.ModalFavoriteLevelForSmoke == 0
                    && window.AdjustModalFavoriteForSmoke(1)
                    && window.ModalFavoriteLevelForSmoke == 1
                    && window.SelectedFavoriteLevelForSmoke == 1;
                bool photorealFavoriteRestored =
                    window.SelectModalEnhancementJobVersionForSmoke(
                        "photoreal-version")
                    && window.ModalFavoriteLevelForSmoke == 2
                    && window.SelectedFavoriteLevelForSmoke == 1;
                staleFavoriteSourceFallbackRejected =
                    photorealFavoriteRestored
                    && window.RejectStaleModalFavoriteSourceFallbackForSmoke();
                bool favoriteKeysPersisted = false;
                if (File.Exists(favoritesPath))
                {
                    using JsonDocument persistedFavorites =
                        JsonDocument.Parse(File.ReadAllText(favoritesPath));
                    JsonElement favoriteRoot = persistedFavorites.RootElement;
                    favoriteKeysPersisted = favoriteRoot.TryGetProperty(
                            sourcePath,
                            out JsonElement originalFavorite)
                        && originalFavorite.GetInt32() == 1
                        && favoriteRoot.TryGetProperty(
                            photorealOutputPath,
                            out JsonElement photorealFavorite)
                        && photorealFavorite.GetInt32() == 2;
                }
                versionSpecificFavoriteContract = photorealFavoriteRaised
                    && originalFavoriteAbsentAfterPhotoreal
                    && originalFavoriteRaised
                    && photorealFavoriteRestored
                    && window.ModalPhotorealFavoriteActiveSurfaceForSmoke
                    && staleFavoriteSourceFallbackRejected
                    && favoriteKeysPersisted;
                string sourceFileNameForFavorite = Path.GetFileName(sourcePath);
                bool badgeShowsMaximum =
                    window.PhotorealFavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite) == 2
                    && window.PhotorealFavoriteBadgeForFileForSmoke(
                        sourceFileNameForFavorite);
                window.CloseModalForSmoke();
                Tile? favoriteTile = window.TileForFileForSmoke(
                    sourceFileNameForFavorite);
                // Card-layout refreshes intentionally notify only realized
                // containers. Realize this card before asserting the visual
                // notification contract so the smoke does not require an
                // O(catalog) notification fan-out for an off-screen Tile.
                _ = window.FavoriteBadgeVisualsForFileForSmoke(
                    sourceFileNameForFavorite);
                int photorealBadgeLayoutNotifications = 0;
                PropertyChangedEventHandler photorealBadgeLayoutHandler =
                    (_, args) =>
                    {
                        if (args.PropertyName ==
                            nameof(Tile.ShowPhotorealFavoriteBadge))
                        {
                            photorealBadgeLayoutNotifications++;
                        }
                    };
                if (favoriteTile is not null)
                    favoriteTile.PropertyChanged += photorealBadgeLayoutHandler;
                try
                {
                    bool zoomedBelowBadgeThreshold =
                        window.SetGridZoomForSmoke(20d);
                    bool badgeHiddenAtSmallZoom =
                        favoriteTile?.ShowPhotorealFavoriteBadge == false;
                    bool zoomedAboveBadgeThreshold =
                        window.SetGridZoomForSmoke(200d);
                    bool badgeRestoredAtLargeZoom =
                        favoriteTile?.ShowPhotorealFavoriteBadge == true;
                    photorealFavoriteLayoutContract =
                        zoomedBelowBadgeThreshold
                        && badgeHiddenAtSmallZoom
                        && zoomedAboveBadgeThreshold
                        && badgeRestoredAtLargeZoom
                        && photorealBadgeLayoutNotifications >= 2;
                }
                finally
                {
                    if (favoriteTile is not null)
                    {
                        favoriteTile.PropertyChanged -=
                            photorealBadgeLayoutHandler;
                    }
                }
                bool selectedPhotorealLevelTwo =
                    window.SetPhotorealFavoriteFilterLevelsForSmoke(2);
                window.SetFavoriteOnlyFilterForSmoke(true);
                bool levelTwoIncludes =
                    selectedPhotorealLevelTwo
                    && window.FilteredFileNamesForSmoke().Contains(
                        sourceFileNameForFavorite,
                        StringComparer.OrdinalIgnoreCase);
                bool levelOneExcludes =
                    window.SetPhotorealFavoriteFilterLevelsForSmoke(1)
                    && !window.FilteredFileNamesForSmoke().Contains(
                        sourceFileNameForFavorite,
                        StringComparer.OrdinalIgnoreCase);
                window.SetFavoriteOnlyFilterForSmoke(false);
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                bool sourceReselectedForZero = window.SelectFileNameForSmoke(
                    sourceFileNameForFavorite);
                bool zeroFavoriteApplied = sourceReselectedForZero
                    && window.OpenModalForSmoke()
                    && window.SelectModalEnhancementJobVersionForSmoke(
                        "photoreal-version")
                    && window.AdjustModalFavoriteForSmoke(-1)
                    && window.AdjustModalFavoriteForSmoke(-1)
                    && window.ModalFavoriteLevelForSmoke == 0;
                window.CloseModalForSmoke();
                window.ForceSharedStoreWritersForSmoke();
                window.FailNextFavoriteWriterForSmoke();
                bool failedFavoriteAccepted = zeroFavoriteApplied
                    && window.SetIndependentFavoriteLevelForSmoke(
                        photorealOutputPath,
                        1);
                SharedWriteStatus[] failedFavoriteStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                bool failedFavoriteRolledBack = failedFavoriteAccepted
                    && failedFavoriteStatuses.Contains(
                        SharedWriteStatus.Failed)
                    && window.FailedFavoriteRetryPendingForSmoke
                    && window.PhotorealFavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite) == 0
                    && !window.PhotorealFavoriteBadgeForFileForSmoke(
                        sourceFileNameForFavorite);
                window.RetryFailedFavoriteForSmoke();
                int retryPresentationLevel =
                    window.PhotorealFavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite);
                bool retryPresentationBadge =
                    window.PhotorealFavoriteBadgeForFileForSmoke(
                        sourceFileNameForFavorite);
                bool retryPresentationAppliedImmediately =
                    retryPresentationLevel == 1
                    && retryPresentationBadge;
                SharedWriteStatus[] retriedFavoriteStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                bool retriedFavoritePersisted =
                    retriedFavoriteStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && !window.FailedFavoriteRetryPendingForSmoke
                    && ReadFavoriteLevel(
                        favoritesPath,
                        photorealOutputPath) == 1;
                bool retryFavoriteResetAccepted =
                    window.SetIndependentFavoriteLevelForSmoke(
                        photorealOutputPath,
                        0);
                SharedWriteStatus[] retryFavoriteResetStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                bool retryFavoriteReset = retryFavoriteResetAccepted
                    && retryFavoriteResetStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && window.PhotorealFavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite) == 0
                    && !window.PhotorealFavoriteBadgeForFileForSmoke(
                        sourceFileNameForFavorite)
                    && ReadFavoriteLevel(
                        favoritesPath,
                        photorealOutputPath) == 0;
                bool photorealLevelZeroAccepted =
                    window.SetPhotorealFavoriteFilterLevelsForSmoke(0);
                window.SetFavoriteOnlyFilterForSmoke(true);
                _ = await window.WaitForFavoritePresentationStateForSmokeAsync(
                    TimeSpan.FromSeconds(10));
                bool photorealLevelZeroIncludes =
                    photorealLevelZeroAccepted
                    && window.FilteredFileNamesForSmoke().Contains(
                        sourceFileNameForFavorite,
                        StringComparer.OrdinalIgnoreCase);
                window.SetFavoriteOnlyFilterForSmoke(false);
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                photorealFavoriteRetryContract =
                    failedFavoriteRolledBack
                    && retryPresentationAppliedImmediately
                    && retriedFavoritePersisted
                    && retryFavoriteReset
                    && photorealLevelZeroIncludes;
                if (!photorealFavoriteRetryContract)
                {
                    failure =
                        $"Photoreal Favorite retry: accepted={failedFavoriteAccepted}; "
                        + $"rolledBack={failedFavoriteRolledBack}; "
                        + $"immediate={retryPresentationAppliedImmediately}; "
                        + $"retryLevel={retryPresentationLevel}; "
                        + $"retryBadge={retryPresentationBadge}; "
                        + $"persisted={retriedFavoritePersisted}; "
                        + $"reset={retryFavoriteReset}; "
                        + $"resetAccepted={retryFavoriteResetAccepted}; "
                        + $"resetLevel={window.PhotorealFavoriteLevelForFileForSmoke(sourceFileNameForFavorite)}; "
                        + $"resetBadge={window.PhotorealFavoriteBadgeForFileForSmoke(sourceFileNameForFavorite)}; "
                        + $"resetDurable={ReadFavoriteLevel(favoritesPath, photorealOutputPath)}; "
                        + $"levelZeroAccepted={photorealLevelZeroAccepted}; "
                        + $"levelZeroIncludes={photorealLevelZeroIncludes}; "
                        + $"failedStatuses={string.Join(',', failedFavoriteStatuses)}; "
                        + $"retryStatuses={string.Join(',', retriedFavoriteStatuses)}; "
                        + $"resetStatuses={string.Join(',', retryFavoriteResetStatuses)}";
                }
                bool originalClearedForGlobalUnrated =
                    window.SetFileFavoriteLevelForSmoke(
                        sourceFileNameForFavorite,
                        0);
                bool photorealRestoredForGlobalUnrated =
                    window.SetIndependentFavoriteLevelForSmoke(
                        photorealOutputPath,
                        2);
                SharedWriteStatus[] globalUnratedFixtureStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                bool globalUnratedFixture = zeroFavoriteApplied
                    && originalClearedForGlobalUnrated
                    && photorealRestoredForGlobalUnrated
                    && globalUnratedFixtureStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && ReadFavoriteLevel(favoritesPath, sourcePath) == 0
                    && ReadFavoriteLevel(favoritesPath, photorealOutputPath) == 2
                    && window.FavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite) == 0
                    && window.PhotorealFavoriteLevelForFileForSmoke(
                        sourceFileNameForFavorite) == 2
                    && window.PhotorealFavoriteBadgeForFileForSmoke(
                        sourceFileNameForFavorite);
                window.SetUnfavoriteOnlyFilterForSmoke(true);
                _ = await window.WaitForFavoritePresentationStateForSmokeAsync(
                    TimeSpan.FromSeconds(10));
                bool originalUnratedIncludes = globalUnratedFixture
                    && window.ShowUnfavoriteOnlyForSmoke
                    && window.FilteredFileNamesForSmoke().Contains(
                        sourceFileNameForFavorite,
                        StringComparer.OrdinalIgnoreCase);
                ViewerState? persistedPhotorealFavoriteFilter =
                    JsonSerializer.Deserialize<ViewerState>(File.ReadAllText(
                        environment["PHOTOVIEWER_WPF_STATE_PATH"]));
                bool filterPersisted = persistedPhotorealFavoriteFilter
                    ?.ShowUnfavoriteOnly == true
                    && !persistedPhotorealFavoriteFilter.ShowFavoritesOnly
                    && (persistedPhotorealFavoriteFilter
                            .PhotorealFavoriteFilterLevels is null
                        || !persistedPhotorealFavoriteFilter
                            .PhotorealFavoriteFilterLevels.Contains(0));
                photorealFavoriteBadgeFilterContract = badgeShowsMaximum
                    && photorealFavoriteLayoutContract
                    && photorealFavoriteRetryContract
                    && levelTwoIncludes
                    && levelOneExcludes
                    && originalUnratedIncludes
                    && filterPersisted;
                if (!photorealFavoriteBadgeFilterContract)
                {
                    failure =
                        $"Photoreal Favorite filter: badgeMax={badgeShowsMaximum}; "
                        + $"layout={photorealFavoriteLayoutContract}; "
                        + $"retry={photorealFavoriteRetryContract}; "
                        + $"levelTwo={levelTwoIncludes}; "
                        + $"levelOneExcluded={levelOneExcludes}; "
                        + $"globalFixture={globalUnratedFixture}; "
                        + $"showUnrated={window.ShowUnfavoriteOnlyForSmoke}; "
                        + $"originalUnratedIncludes={originalUnratedIncludes}; "
                        + $"persisted={filterPersisted}; "
                        + $"fixtureStatuses={string.Join(',', globalUnratedFixtureStatuses)}";
                }
                window.SetUnfavoriteOnlyFilterForSmoke(false);
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                sourceUntouched = sourceHashBefore == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                Dictionary<string, string> recoveredFixtureHashesAfter =
                    FingerprintRecoveredSmokeFiles(recoveredFixtureFiles);
                recoveredReferenceReadOnly = recoveredFixtureHashesBefore.Count
                    == recoveredFixtureHashesAfter.Count
                    && recoveredFixtureHashesBefore.All(pair =>
                        recoveredFixtureHashesAfter.TryGetValue(
                            pair.Key,
                            out string? after)
                        && string.Equals(
                            pair.Value,
                            after,
                            StringComparison.Ordinal));
                ok = selected
                    && opened
                    && passive
                    && started
                    && toolbarContract
                    && loadTimingContract
                    && versionCycleContract
                    && versionSpecificFavoriteContract
                    && photorealFavoriteBadgeFilterContract
                    && photorealFavoriteLayoutContract
                    && photorealFavoriteRetryContract
                    && versionWheelCycleContract
                    && thumbnailVersionPreferenceContract
                    && thumbnailVariantCountContract
                    && upscaleSettingsContract
                    && persistedNcnnUpscaleChoiceContract
                    && ncnnHighScaleSelectionContract
                    && legacyComfyDefaultMigrationContract
                    && photorealShortcutContract
                    && queueAddedToastContract
                    && requestContract
                    && fallbackPromptContract
                    && negativePromptContract
                    && loraToggleContract
                    && structureRemovedContract
                    && explicitBlankPersistenceContract
                    && legacyPromptMigrationContract
                    && resetPromptContract
                    && appSettingsPromptContract
                    && appSettingsControlsContract
                    && photorealEngineContract
                    && styleContract
                    && stylePersistenceContract
                    && styleReloadContract
                    && defaultPromptContract
                    && promptMappingContract
                    && promptMappingEditRefreshContract
                    && displayedVersionMetadataContract
                    && photoUpscaleProfileContract
                    && veryHighQualityContract
                    && independentCompanionContract
                    && galleryContextDirectContract
                    && galleryEnqueueNextContract
                    && modalEnqueueNextDisplayedPhotorealContract
                    && legacyPhotorealCapabilitySafe
                    && sharedQueueRoute
                    && sourceUntouched
                    && modalPhotorealOperation
                    && recoveredReferenceExact
                    && recoveredReferenceValidEmptyJobs
                    && recoveredReferenceMalformedJobsRejected
                    && recoveredReferenceHashVector
                    && recoveredReferenceHashMismatchRejected
                    && recoveredReferenceAmbiguousRejected
                    && recoveredReferenceKnownJobRejected
                    && recoveredReferenceMutationBlocked
                    && recoveredReferencePollingPreserved
                    && recoveredReferenceReadOnly
                    && recoveredReferenceCacheReuse
                    && recoveredReferenceCacheInvalidation
                    && hqPromptProvenanceContract
                    && recoveredHqButtonContract
                    && recoveredHqCapabilityGateContract
                    && photorealSeedContract
                    && photorealPreservationScanContract
                    && gallerySingleFlightContract;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                if (window is not null)
                {
                    try { window.Close(); } catch { }
                }
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(new
                {
                    ok,
                    message = ok ? "AI photoreal button, settings, and shared GPU queue request passed." : failure,
                    selected,
                    opened,
                    passive,
                    started,
                    toolbarContract,
                    loadTimingContract,
                    versionCycleContract,
                    versionSpecificFavoriteContract,
                    photorealFavoriteBadgeFilterContract,
                    photorealFavoriteLayoutContract,
                    photorealFavoriteRetryContract,
                    staleFavoriteSourceFallbackRejected,
                    versionWheelCycleContract,
                    thumbnailVersionPreferenceContract,
                    thumbnailVariantCountContract,
                    upscaleSettingsContract,
                    persistedNcnnUpscaleChoiceContract,
                    ncnnHighScaleSelectionContract,
                    legacyComfyDefaultMigrationContract,
                    photorealShortcutContract,
                    queueAddedToastContract,
                    requestContract,
                    fallbackPromptContract,
                    negativePromptContract,
                    loraToggleContract,
                    structureRemovedContract,
                    explicitBlankPersistenceContract,
                    legacyPromptMigrationContract,
                    resetPromptContract,
                    appSettingsPromptContract,
                    appSettingsControlsContract,
                    photorealEngineContract,
                    styleContract,
                    stylePersistenceContract,
                    styleReloadContract,
                    defaultPromptContract,
                    promptMappingContract,
                    promptMappingDirectToggleContract,
                    promptMappingEditRefreshContract,
                    promptMappingDefaultsMigrationContract,
                    promptMappingButtonContrastContract,
                    displayedVersionMetadataContract,
                    displayedVersionMetadataDiagnostic,
                    photoUpscaleProfileContract,
                    veryHighQualityContract,
                    independentCompanionContract,
                    galleryContextNoModal,
                    galleryContextDirectContract,
                    galleryEnqueueNextContract,
                    modalEnqueueNextDisplayedPhotorealContract,
                    legacyPhotorealCapabilitySafe,
                    modalPhotorealOperation,
                    sharedQueueRoute,
                    sourceUntouched,
                    recoveredReferenceExact,
                    recoveredReferenceValidEmptyJobs,
                    recoveredReferenceMalformedJobsRejected,
                    recoveredReferenceHashVector,
                    recoveredReferenceHashMismatchRejected,
                    recoveredReferenceAmbiguousRejected,
                    recoveredReferenceKnownJobRejected,
                    recoveredReferenceMutationBlocked,
                    recoveredReferencePollingPreserved,
                    recoveredReferenceReadOnly,
                    recoveredReferenceCacheReuse,
                    recoveredReferenceCacheInvalidation,
                    hqPromptProvenanceContract,
                    recoveredHqButtonContract,
                    recoveredHqCapabilityGateContract,
                    photorealSeedContract,
                    photorealPreservationScanContract,
                    photorealSeedDetails = new
                    {
                        randomSeedOmitted,
                        seedDefaultAndSurface,
                        fixedSeedExact,
                        invalidSeedBlocked,
                        missingSeedCapabilityBlocked,
                    },
                    gallerySingleFlightContract,
                    recoveredReferencePerformance,
                    recoveredReferenceDiagnostic,
                    requests,
                }, new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }

    private static string BuildRecoveredSmokeOutputFileName(
        string sourcePath,
        string jobId,
        string presetId,
        string presetHash,
        string adapterId,
        string? sourceHashOverride = null)
    {
        string normalizedSourcePath = Path.GetFullPath(sourcePath);
        var sourceInfo = new FileInfo(normalizedSourcePath);
        double mtimeMs = RecoveredSmokeMtimeMs(sourceInfo);
        string payload = JsonSerializer.Serialize(
            new
            {
                sourcePath = normalizedSourcePath,
                signature = new
                {
                    size = sourceInfo.Length,
                    mtimeMs,
                },
                presetHash,
                adapterId,
            },
            new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        string sourceHash = sourceHashOverride
            ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant()[..16];
        string safeBase = Path.GetFileNameWithoutExtension(normalizedSourcePath);
        var characters = safeBase.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            if (character < 0x20 || character is '<' or '>' or ':' or '"'
                    or '/' or '\\' or '|' or '?' or '*')
            {
                characters[index] = '_';
            }
        }
        safeBase = new string(characters);
        if (safeBase.Length > 64)
            safeBase = safeBase[..64];
        if (safeBase.Length == 0)
            safeBase = "image";
        return $"{jobId}__{safeBase}__{sourceHash}__{presetId}__{presetHash}.png";
    }

    private static double RecoveredSmokeMtimeMs(FileInfo info)
        => (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks)
            / (double)TimeSpan.TicksPerMillisecond;

    private static Dictionary<string, string> FingerprintRecoveredSmokeFiles(
        IEnumerable<string> paths)
        => paths.ToDictionary(
            static path => Path.GetFullPath(path),
            static path => Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);

    private static void WritePhotorealSmokePng(string path)
    {
        const int width = 16;
        const int height = 24;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x70;
            pixels[index + 1] = 0x90;
            pixels[index + 2] = 0xB0;
            pixels[index + 3] = 0xFF;
        }
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
