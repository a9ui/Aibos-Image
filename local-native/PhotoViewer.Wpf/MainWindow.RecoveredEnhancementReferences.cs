using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public readonly record struct RecoveredEnhancementReferenceSmokeSnapshot(
    bool ReadOk,
    int Total,
    int Upscaled,
    int Photorealized);

public readonly record struct RecoveredEnhancementReferenceCacheSmokeSnapshot(
    long FullScans,
    long CacheHits,
    long CatalogPathsVisited,
    long OutputFilesVisited,
    long SourceSignatureChecks,
    int CachedCatalogPaths,
    int CachedReferences,
    int CachedSourceProbes,
    double LastFullScanMilliseconds,
    long LastFullScanAllocatedBytes,
    double LastCacheHitMilliseconds,
    long LastCacheHitAllocatedBytes);

public partial class MainWindow
{
    private const string RecoveredUpscaleFolderName = "Upscaled";
    private const string RecoveredPhotorealFolderName = "Photorealized";
    private static readonly string[] RecoveredUpscaleAdapterIds =
        ["realesrgan-ncnn", "comfyui", "sharp-test"];
    private static readonly string[] RecoveredPhotorealAdapterIds =
        ["comfyui-flux2-photoreal", "a1111-photoreal"];

    private sealed record RecoveredEnhancementOutputCandidate(
        string JobId,
        string Operation,
        string SafeBase,
        string SourceHash,
        string PresetId,
        string PresetHash,
        string CanonicalOutputPath,
        DateTimeOffset CompletedAtUtc,
        IReadOnlyList<string> AdapterIds);

    private sealed record RecoveredEnhancementSourceCandidate(
        string SourcePathForHash,
        string ResolvedSourcePath,
        string SafeBase,
        long Size,
        double MtimeMs,
        IReadOnlyList<string> CatalogAliases);

    private sealed record RecoveredEnhancementSourceProbe(
        string SourcePath,
        long Size,
        double MtimeMs);

    private sealed record RecoveredEnhancementOutputFolderSnapshot(
        string Operation,
        string FolderName,
        bool Exists,
        bool CanonicallyOwned,
        string LexicalPath,
        string CanonicalPath,
        long LastWriteTimeUtcTicks,
        IReadOnlyList<string> AdapterIds);

    private sealed record RecoveredEnhancementOutputTreeSnapshot(
        string LexicalRoot,
        string CanonicalRoot,
        bool RootExists,
        long RootLastWriteTimeUtcTicks,
        RecoveredEnhancementOutputFolderSnapshot Photoreal,
        RecoveredEnhancementOutputFolderSnapshot Upscale);

    private sealed record CachedRecoveredEnhancementReference(
        string ResolvedSourcePath,
        IReadOnlyList<string> CatalogAliases,
        ManagedEnhancementVersion Version);

    private sealed record RecoveredEnhancementReferenceCache(
        string JobsPath,
        long CatalogRevision,
        string[] CatalogPaths,
        HashSet<string> KnownJobIds,
        RecoveredEnhancementOutputTreeSnapshot OutputTree,
        RecoveredEnhancementSourceProbe[] SourceProbes,
        CachedRecoveredEnhancementReference[] References);

    private readonly object _recoveredEnhancementReferenceCacheGate = new();
    private RecoveredEnhancementReferenceCache? _recoveredEnhancementReferenceCache;
    private long _recoveredEnhancementFullScanCount;
    private long _recoveredEnhancementCacheHitCount;
    private long _recoveredEnhancementCatalogPathsVisited;
    private long _recoveredEnhancementOutputFilesVisited;
    private long _recoveredEnhancementSourceSignatureChecks;
    private double _recoveredEnhancementLastFullScanMilliseconds;
    private long _recoveredEnhancementLastFullScanAllocatedBytes;
    private double _recoveredEnhancementLastCacheHitMilliseconds;
    private long _recoveredEnhancementLastCacheHitAllocatedBytes;

    private IReadOnlyList<string> SnapshotActiveEnhancementCatalogPaths()
        => _allTiles
            .Where(static tile => tile.IsRealFile)
            .Select(static tile => tile.Path)
            .ToArray();

    private bool NeedsRecoveredEnhancementCatalogSnapshot(
        string jobsPath,
        long catalogRevision)
    {
        string normalizedJobsPath = Path.GetFullPath(jobsPath);
        lock (_recoveredEnhancementReferenceCacheGate)
        {
            return _recoveredEnhancementReferenceCache is not
            {
                JobsPath: string cachedJobsPath,
                CatalogRevision: var cachedRevision,
            }
                || !string.Equals(
                    cachedJobsPath,
                    normalizedJobsPath,
                    StringComparison.OrdinalIgnoreCase)
                || cachedRevision != catalogRevision;
        }
    }

    private EnhancedStateSnapshot CreateRecoveredOnlyEnhancedStateSnapshot(
        string jobsPath,
        IReadOnlyList<string>? activeCatalogPaths,
        long catalogRevision)
    {
        var outputs = new Dictionary<string, ManagedEnhancedOutput>(
            EnhancementSourceIdentityComparer);
        var catalogOutputs = new Dictionary<string, ManagedEnhancedOutput>(
            StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, List<ManagedEnhancementVersion>>(
            EnhancementSourceIdentityComparer);
        var catalogVersions = new Dictionary<string, List<ManagedEnhancementVersion>>(
            StringComparer.OrdinalIgnoreCase);
        int candidateCount = 0;
        RecoverOrphanedEnhancementReferences(
            jobsPath,
            catalogRevision,
            activeCatalogPaths,
            new HashSet<string>(StringComparer.Ordinal),
            outputs,
            catalogOutputs,
            versions,
            catalogVersions,
            ref candidateCount);
        return new EnhancedStateSnapshot(
            outputs,
            catalogOutputs,
            versions,
            catalogVersions,
            new Dictionary<string, List<ManagedVideoVersion>>(
                EnhancementSourceIdentityComparer),
            new Dictionary<string, List<ManagedVideoVersion>>(
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ManagedEnhancementQueueActivity>(
                StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            0,
            candidateCount,
            0,
            default,
            -1);
    }

    private void RecoverOrphanedEnhancementReferences(
        string jobsPath,
        long catalogRevision,
        IReadOnlyList<string>? activeCatalogPaths,
        IReadOnlySet<string> knownJobIds,
        Dictionary<string, ManagedEnhancedOutput> outputs,
        Dictionary<string, ManagedEnhancedOutput> catalogOutputsByPath,
        Dictionary<string, List<ManagedEnhancementVersion>> versionsBySource,
        Dictionary<string, List<ManagedEnhancementVersion>> catalogVersionsByPath,
        ref int candidateCount)
    {
        var watch = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool cacheHit = false;
        bool fullScan = false;
        RecoveredEnhancementReferenceCache? cacheToApply = null;
        string normalizedJobsPath = Path.GetFullPath(jobsPath);

        lock (_recoveredEnhancementReferenceCacheGate)
        {
            RecoveredEnhancementReferenceCache? existing =
                _recoveredEnhancementReferenceCache;
            bool sameCatalog = existing is not null
                && string.Equals(
                    existing.JobsPath,
                    normalizedJobsPath,
                    StringComparison.OrdinalIgnoreCase)
                && existing.CatalogRevision == catalogRevision;
            if (sameCatalog
                && existing!.KnownJobIds.SetEquals(knownJobIds)
                && TryCaptureRecoveredEnhancementOutputTree(
                    normalizedJobsPath,
                    out RecoveredEnhancementOutputTreeSnapshot currentTree)
                && Equals(currentTree, existing.OutputTree)
                && AreRecoveredEnhancementSourceProbesCurrent(
                    existing.SourceProbes))
            {
                cacheHit = true;
                cacheToApply = existing;
                _recoveredEnhancementCacheHitCount++;
            }
            else
            {
                string[]? catalogPaths = activeCatalogPaths switch
                {
                    string[] array => array,
                    not null => activeCatalogPaths.ToArray(),
                    _ when sameCatalog => existing!.CatalogPaths,
                    _ => null,
                };
                if (catalogPaths is null)
                {
                    _recoveredEnhancementReferenceCache = null;
                }
                else if (TryCaptureRecoveredEnhancementOutputTree(
                             normalizedJobsPath,
                             out RecoveredEnhancementOutputTreeSnapshot beforeTree)
                    && TryScanRecoveredEnhancementReferences(
                        beforeTree,
                        catalogPaths,
                        knownJobIds,
                        out CachedRecoveredEnhancementReference[] references,
                        out RecoveredEnhancementSourceProbe[] sourceProbes)
                    && TryCaptureRecoveredEnhancementOutputTree(
                        normalizedJobsPath,
                        out RecoveredEnhancementOutputTreeSnapshot afterTree)
                    && Equals(beforeTree, afterTree)
                    && AreRecoveredEnhancementSourceProbesCurrent(sourceProbes))
                {
                    fullScan = true;
                    cacheToApply = new RecoveredEnhancementReferenceCache(
                        normalizedJobsPath,
                        catalogRevision,
                        catalogPaths,
                        knownJobIds.ToHashSet(StringComparer.Ordinal),
                        afterTree,
                        sourceProbes,
                        references);
                    _recoveredEnhancementReferenceCache = cacheToApply;
                    _recoveredEnhancementFullScanCount++;
                }
                else
                {
                    // A concurrent output/source change or an unreadable tree
                    // makes this read omit inferred references. Job-backed
                    // versions remain valid, and the next refresh retries.
                    _recoveredEnhancementReferenceCache = null;
                }
            }
        }

        if (cacheToApply is not null)
        {
            MergeCachedRecoveredEnhancementReferences(
                cacheToApply.References,
                outputs,
                catalogOutputsByPath,
                versionsBySource,
                catalogVersionsByPath,
                ref candidateCount);
        }

        watch.Stop();
        long allocated = Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        lock (_recoveredEnhancementReferenceCacheGate)
        {
            if (cacheHit)
            {
                _recoveredEnhancementLastCacheHitMilliseconds =
                    watch.Elapsed.TotalMilliseconds;
                _recoveredEnhancementLastCacheHitAllocatedBytes = allocated;
            }
            else if (fullScan)
            {
                _recoveredEnhancementLastFullScanMilliseconds =
                    watch.Elapsed.TotalMilliseconds;
                _recoveredEnhancementLastFullScanAllocatedBytes = allocated;
            }
        }
    }

    private bool TryScanRecoveredEnhancementReferences(
        RecoveredEnhancementOutputTreeSnapshot outputTree,
        IReadOnlyList<string> activeCatalogPaths,
        IReadOnlySet<string> knownJobIds,
        out CachedRecoveredEnhancementReference[] references,
        out RecoveredEnhancementSourceProbe[] sourceProbes)
    {
        references = [];
        sourceProbes = [];
        if (!TryDiscoverRecoveredEnhancementOutputCandidates(
                outputTree,
                knownJobIds,
                out IReadOnlyList<RecoveredEnhancementOutputCandidate> orphanOutputs))
        {
            return false;
        }
        if (orphanOutputs.Count == 0)
            return true;

        HashSet<string> requiredSafeBases = orphanOutputs
            .Select(static output => output.SafeBase)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<RecoveredEnhancementSourceCandidate>> sourcesBySafeBase =
            BuildRecoveredEnhancementSourceIndex(
                activeCatalogPaths,
                requiredSafeBases,
                out sourceProbes);
        var representedOutputs = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> duplicateOrphanJobIds = orphanOutputs
            .GroupBy(static output => output.JobId, StringComparer.Ordinal)
            .Where(static group => group.Count() != 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var recovered = new List<CachedRecoveredEnhancementReference>();

        foreach (RecoveredEnhancementOutputCandidate orphan in orphanOutputs
                     .OrderByDescending(static output => output.CompletedAtUtc)
                     .ThenBy(static output => output.CanonicalOutputPath,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (duplicateOrphanJobIds.Contains(orphan.JobId)
                || representedOutputs.Contains(orphan.CanonicalOutputPath)
                || !sourcesBySafeBase.TryGetValue(
                    orphan.SafeBase,
                    out List<RecoveredEnhancementSourceCandidate>? sourceCandidates))
            {
                continue;
            }

            RecoveredEnhancementSourceCandidate? matchedSource = null;
            int matchCount = 0;
            foreach (RecoveredEnhancementSourceCandidate source in sourceCandidates)
            {
                foreach (string adapterId in orphan.AdapterIds)
                {
                    string calculatedHash = ComputeRecoveredEnhancementSourceHash(
                        source.SourcePathForHash,
                        source.Size,
                        source.MtimeMs,
                        orphan.PresetHash,
                        adapterId);
                    if (!string.Equals(
                            calculatedHash,
                            orphan.SourceHash,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchedSource = source;
                    matchCount++;
                    if (matchCount > 1)
                        break;
                }
                if (matchCount > 1)
                    break;
            }
            if (matchCount != 1 || matchedSource is null)
                continue;

            // Recheck the source after hashing so a concurrently replaced file
            // cannot enter the recovered inventory with a stale signature.
            if (!TryReadCurrentRecoveredSourceSignature(
                    matchedSource.SourcePathForHash,
                    out long currentSize,
                    out double currentMtimeMs)
                || currentSize != matchedSource.Size
                || currentMtimeMs != matchedSource.MtimeMs)
            {
                continue;
            }

            var managedOutput = new ManagedEnhancedOutput(
                orphan.CanonicalOutputPath,
                matchedSource.Size,
                matchedSource.MtimeMs);
            var version = new ManagedEnhancementVersion(
                orphan.JobId,
                orphan.Operation,
                managedOutput,
                orphan.CompletedAtUtc,
                Recovered: true);
            recovered.Add(new CachedRecoveredEnhancementReference(
                matchedSource.ResolvedSourcePath,
                matchedSource.CatalogAliases,
                version));
            representedOutputs.Add(orphan.CanonicalOutputPath);
        }

        references = recovered.ToArray();
        return true;
    }

    private static void MergeCachedRecoveredEnhancementReferences(
        IReadOnlyList<CachedRecoveredEnhancementReference> recoveredReferences,
        Dictionary<string, ManagedEnhancedOutput> outputs,
        Dictionary<string, ManagedEnhancedOutput> catalogOutputsByPath,
        Dictionary<string, List<ManagedEnhancementVersion>> versionsBySource,
        Dictionary<string, List<ManagedEnhancementVersion>> catalogVersionsByPath,
        ref int candidateCount)
    {
        var representedOutputs = versionsBySource.Values
            .SelectMany(static versions => versions)
            .Select(static version => version.Output.OutputPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (CachedRecoveredEnhancementReference recovered in recoveredReferences)
        {
            ManagedEnhancementVersion version = recovered.Version;
            if (representedOutputs.Contains(version.Output.OutputPath))
                continue;
            if (!versionsBySource.TryGetValue(
                    recovered.ResolvedSourcePath,
                    out List<ManagedEnhancementVersion>? versions))
            {
                versions = [];
                versionsBySource[recovered.ResolvedSourcePath] = versions;
                outputs[recovered.ResolvedSourcePath] = version.Output;
                candidateCount++;
            }

            versions.Add(version);
            representedOutputs.Add(version.Output.OutputPath);
            outputs.TryAdd(recovered.ResolvedSourcePath, version.Output);
            foreach (string alias in recovered.CatalogAliases)
            {
                catalogOutputsByPath.TryAdd(
                    alias,
                    outputs[recovered.ResolvedSourcePath]);
                catalogVersionsByPath.TryAdd(alias, versions);
            }
        }
    }

    private bool TryDiscoverRecoveredEnhancementOutputCandidates(
        RecoveredEnhancementOutputTreeSnapshot outputTree,
        IReadOnlySet<string> knownJobIds,
        out IReadOnlyList<RecoveredEnhancementOutputCandidate> candidates)
    {
        var discovered = new List<RecoveredEnhancementOutputCandidate>();
        candidates = discovered;
        if (!outputTree.RootExists)
            return true;
        try
        {
            foreach (RecoveredEnhancementOutputFolderSnapshot folder in
                     new[] { outputTree.Photoreal, outputTree.Upscale })
            {
                if (!folder.Exists || !folder.CanonicallyOwned)
                    continue;
                string[] files = Directory.GetFiles(
                    folder.LexicalPath,
                    "*",
                    SearchOption.TopDirectoryOnly);
                _recoveredEnhancementOutputFilesVisited += files.Length;
                foreach (string file in files)
                {
                    if (!TryParseRecoveredEnhancementOutputName(
                            file,
                            out string jobId,
                            out string safeBase,
                            out string sourceHash,
                            out string presetId,
                            out string presetHash)
                        || knownJobIds.Contains(jobId)
                        || !TryResolveRecoveredEnhancementOutput(
                            file,
                            folder.LexicalPath,
                            folder.CanonicalPath,
                            out string canonicalOutput,
                            out DateTimeOffset completedAtUtc))
                    {
                        continue;
                    }

                    discovered.Add(new RecoveredEnhancementOutputCandidate(
                        jobId,
                        folder.Operation,
                        safeBase,
                        sourceHash,
                        presetId,
                        presetHash,
                        canonicalOutput,
                        completedAtUtc,
                        folder.AdapterIds));
                }
            }
            return true;
        }
        catch
        {
            candidates = [];
            return false;
        }
    }

    private Dictionary<string, List<RecoveredEnhancementSourceCandidate>>
        BuildRecoveredEnhancementSourceIndex(
            IReadOnlyList<string> activeCatalogPaths,
            IReadOnlySet<string> requiredSafeBases,
            out RecoveredEnhancementSourceProbe[] sourceProbes)
    {
        _recoveredEnhancementCatalogPathsVisited += activeCatalogPaths.Count;
        var probes = new List<RecoveredEnhancementSourceProbe>();
        var index = new Dictionary<string, List<RecoveredEnhancementSourceCandidate>>(
            StringComparer.Ordinal);
        foreach (string path in activeCatalogPaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !Path.IsPathFullyQualified(path)
                    || !SupportedImageExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                string lexicalSource = Path.GetFullPath(path);
                string safeBase = BuildRecoveredEnhancementSafeBase(lexicalSource);
                if (!requiredSafeBases.Contains(safeBase)
                    || !TryResolveEnhancementSourceIdentity(
                        lexicalSource,
                        out string resolvedSource)
                    || !TryReadCurrentRecoveredSourceSignature(
                        lexicalSource,
                        out long size,
                        out double mtimeMs))
                {
                    continue;
                }

                probes.Add(new RecoveredEnhancementSourceProbe(
                    lexicalSource,
                    size,
                    mtimeMs));
                IReadOnlyList<string> aliases = new[]
                    {
                        NormalizeCatalogEnhancementPath(path),
                        NormalizeCatalogEnhancementPath(lexicalSource),
                        NormalizeCatalogEnhancementPath(resolvedSource),
                    }
                    .Where(static alias => alias is not null)
                    .Select(static alias => alias!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!index.TryGetValue(
                        safeBase,
                        out List<RecoveredEnhancementSourceCandidate>? candidates))
                {
                    candidates = [];
                    index[safeBase] = candidates;
                }
                candidates.Add(new RecoveredEnhancementSourceCandidate(
                    lexicalSource,
                    resolvedSource,
                    safeBase,
                    size,
                    mtimeMs,
                    aliases));
            }
            catch
            {
                // One unavailable catalog entry cannot weaken the uniqueness
                // requirement for any other recovered output.
            }
        }
        sourceProbes = probes.ToArray();
        return index;
    }

    private bool AreRecoveredEnhancementSourceProbesCurrent(
        IReadOnlyList<RecoveredEnhancementSourceProbe> probes)
    {
        foreach (RecoveredEnhancementSourceProbe probe in probes)
        {
            _recoveredEnhancementSourceSignatureChecks++;
            if (!TryReadCurrentRecoveredSourceSignature(
                    probe.SourcePath,
                    out long size,
                    out double mtimeMs)
                || size != probe.Size
                || mtimeMs != probe.MtimeMs)
            {
                return false;
            }
        }
        return true;
    }

    private bool TryCaptureRecoveredEnhancementOutputTree(
        string jobsPath,
        out RecoveredEnhancementOutputTreeSnapshot snapshot)
    {
        snapshot = null!;
        try
        {
            string lexicalRoot = Path.GetFullPath(
                SharedDataRootActivation.ResolveManagedOutputsRoot(jobsPath));
            if (!Directory.Exists(lexicalRoot))
            {
                RecoveredEnhancementOutputFolderSnapshot Missing(
                    string operation,
                    string folderName,
                    IReadOnlyList<string> adapters)
                    => new(operation, folderName, false, false, "", "", 0, adapters);
                snapshot = new RecoveredEnhancementOutputTreeSnapshot(
                    lexicalRoot,
                    "",
                    false,
                    0,
                    Missing(
                        "photoreal",
                        RecoveredPhotorealFolderName,
                        RecoveredPhotorealAdapterIds),
                    Missing(
                        "upscale",
                        RecoveredUpscaleFolderName,
                        RecoveredUpscaleAdapterIds));
                return true;
            }

            string canonicalRoot = Path.GetFullPath(_resolveFinalPath(lexicalRoot));
            string[] directories = Directory.GetDirectories(
                lexicalRoot,
                "*",
                SearchOption.TopDirectoryOnly);
            RecoveredEnhancementOutputFolderSnapshot CaptureFolder(
                string operation,
                string folderName,
                IReadOnlyList<string> adapters)
            {
                string? lexicalFolder = directories.FirstOrDefault(path =>
                    string.Equals(
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                        folderName,
                        StringComparison.Ordinal));
                if (lexicalFolder is null)
                {
                    return new RecoveredEnhancementOutputFolderSnapshot(
                        operation,
                        folderName,
                        false,
                        false,
                        "",
                        "",
                        0,
                        adapters);
                }

                lexicalFolder = Path.GetFullPath(lexicalFolder);
                string canonicalFolder = Path.GetFullPath(
                    _resolveFinalPath(lexicalFolder));
                return new RecoveredEnhancementOutputFolderSnapshot(
                    operation,
                    folderName,
                    true,
                    IsDirectRecoveredChild(canonicalFolder, canonicalRoot),
                    lexicalFolder,
                    canonicalFolder,
                    Directory.GetLastWriteTimeUtc(lexicalFolder).Ticks,
                    adapters);
            }

            snapshot = new RecoveredEnhancementOutputTreeSnapshot(
                lexicalRoot,
                canonicalRoot,
                true,
                Directory.GetLastWriteTimeUtc(lexicalRoot).Ticks,
                CaptureFolder(
                    "photoreal",
                    RecoveredPhotorealFolderName,
                    RecoveredPhotorealAdapterIds),
                CaptureFolder(
                    "upscale",
                    RecoveredUpscaleFolderName,
                    RecoveredUpscaleAdapterIds));
            return true;
        }
        catch
        {
            snapshot = null!;
            return false;
        }
    }

    private bool TryResolveRecoveredEnhancementOutput(
        string path,
        string lexicalFolder,
        string canonicalFolder,
        out string canonicalOutput,
        out DateTimeOffset completedAtUtc)
    {
        canonicalOutput = "";
        completedAtUtc = default;
        try
        {
            string lexicalOutput = Path.GetFullPath(path);
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetDirectoryName(lexicalOutput) ?? ""),
                    Path.TrimEndingDirectorySeparator(lexicalFolder),
                    StringComparison.OrdinalIgnoreCase)
                || !SupportedImageExtensions.Contains(Path.GetExtension(lexicalOutput)))
            {
                return false;
            }

            canonicalOutput = Path.GetFullPath(_resolveFinalPath(lexicalOutput));
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetDirectoryName(canonicalOutput) ?? ""),
                    Path.TrimEndingDirectorySeparator(canonicalFolder),
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonicalOutput))
            {
                canonicalOutput = "";
                return false;
            }

            completedAtUtc = new DateTimeOffset(
                File.GetLastWriteTimeUtc(canonicalOutput)).ToUniversalTime();
            return true;
        }
        catch
        {
            canonicalOutput = "";
            completedAtUtc = default;
            return false;
        }
    }

    private static bool TryParseRecoveredEnhancementOutputName(
        string path,
        out string jobId,
        out string safeBase,
        out string sourceHash,
        out string presetId,
        out string presetHash)
    {
        jobId = "";
        safeBase = "";
        sourceHash = "";
        presetId = "";
        presetHash = "";
        string extension = Path.GetExtension(path);
        if (!SupportedImageExtensions.Contains(extension))
            return false;

        string stem = Path.GetFileNameWithoutExtension(path);
        int firstSeparator = stem.IndexOf("__", StringComparison.Ordinal);
        int presetHashSeparator = stem.LastIndexOf("__", StringComparison.Ordinal);
        if (firstSeparator <= 0 || presetHashSeparator <= firstSeparator)
            return false;
        int presetSeparator = stem.LastIndexOf(
            "__",
            presetHashSeparator - 1,
            StringComparison.Ordinal);
        if (presetSeparator <= firstSeparator)
            return false;
        int sourceHashSeparator = stem.LastIndexOf(
            "__",
            presetSeparator - 1,
            StringComparison.Ordinal);
        if (sourceHashSeparator <= firstSeparator)
            return false;

        jobId = stem[..firstSeparator];
        safeBase = stem[(firstSeparator + 2)..sourceHashSeparator];
        sourceHash = stem[(sourceHashSeparator + 2)..presetSeparator];
        presetId = stem[(presetSeparator + 2)..presetHashSeparator];
        presetHash = stem[(presetHashSeparator + 2)..];
        return Guid.TryParseExact(jobId, "D", out Guid parsedJobId)
            && string.Equals(jobId, parsedJobId.ToString("D"), StringComparison.Ordinal)
            && safeBase.Length is > 0 and <= 64
            && sourceHash.Length == 16
            && sourceHash.All(IsLowerHexCharacter)
            && IsRecoveredPresetId(presetId)
            && presetHash.Length == 12
            && presetHash.All(IsLowerHexCharacter);
    }

    private static bool IsRecoveredPresetId(string value)
        => value.Length is > 0 and <= 128
            && !value.Contains("__", StringComparison.Ordinal)
            && value.All(static character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_' or '.');

    private static bool IsLowerHexCharacter(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsDirectRecoveredChild(string candidate, string parent)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate)) ?? ""),
            Path.TrimEndingDirectorySeparator(parent),
            StringComparison.OrdinalIgnoreCase);

    private static string BuildRecoveredEnhancementSafeBase(string sourcePath)
    {
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        var characters = name.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            if (character < 0x20 || character is '<' or '>' or ':' or '"'
                    or '/' or '\\' or '|' or '?' or '*')
            {
                characters[index] = '_';
            }
        }
        string safeBase = new(characters);
        if (safeBase.Length > 64)
            safeBase = safeBase[..64];
        return safeBase.Length == 0 ? "image" : safeBase;
    }

    private static bool TryReadCurrentRecoveredSourceSignature(
        string sourcePath,
        out long size,
        out double mtimeMs)
    {
        size = 0;
        mtimeMs = 0;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                return false;
            size = info.Length;
            mtimeMs = (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks)
                / (double)TimeSpan.TicksPerMillisecond;
            return double.IsFinite(mtimeMs);
        }
        catch
        {
            size = 0;
            mtimeMs = 0;
            return false;
        }
    }

    private static string ComputeRecoveredEnhancementSourceHash(
        string sourcePath,
        long size,
        double mtimeMs,
        string presetHash,
        string adapterId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("sourcePath", sourcePath);
            writer.WritePropertyName("signature");
            writer.WriteStartObject();
            writer.WriteNumber("size", size);
            writer.WriteNumber("mtimeMs", mtimeMs);
            writer.WriteEndObject();
            writer.WriteString("presetHash", presetHash);
            writer.WriteString("adapterId", adapterId);
            writer.WriteEndObject();
            writer.Flush();
        }
        string hash = Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
        return hash[..16].ToLowerInvariant();
    }

    public static string ComputeRecoveredEnhancementSourceHashForSmoke(
        string sourcePath,
        long size,
        double mtimeMs,
        string presetHash,
        string adapterId)
        => ComputeRecoveredEnhancementSourceHash(
            sourcePath,
            size,
            mtimeMs,
            presetHash,
            adapterId);

    public RecoveredEnhancementReferenceSmokeSnapshot
        InspectRecoveredEnhancementReferencesForSmoke(
            string jobsPath,
            IReadOnlyList<string> activeCatalogPaths,
            long catalogRevision = 0)
    {
        EnhancedStateReadResult result = ReadEnhancedStateSnapshot(
            Path.GetFullPath(jobsPath),
            activeCatalogPaths,
            catalogRevision);
        if (result.Snapshot is not EnhancedStateSnapshot snapshot)
            return new RecoveredEnhancementReferenceSmokeSnapshot(false, 0, 0, 0);

        ManagedEnhancementVersion[] recovered = snapshot.Versions.Values
            .SelectMany(static versions => versions)
            .Where(static version => version.Recovered)
            .DistinctBy(static version => version.Output.OutputPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RecoveredEnhancementReferenceSmokeSnapshot(
            true,
            recovered.Length,
            recovered.Count(static version => version.Operation == "upscale"),
            recovered.Count(static version => version.Operation == "photoreal"));
    }

    public void ResetRecoveredEnhancementReferenceCacheForSmoke()
    {
        lock (_recoveredEnhancementReferenceCacheGate)
        {
            _recoveredEnhancementReferenceCache = null;
            _recoveredEnhancementFullScanCount = 0;
            _recoveredEnhancementCacheHitCount = 0;
            _recoveredEnhancementCatalogPathsVisited = 0;
            _recoveredEnhancementOutputFilesVisited = 0;
            _recoveredEnhancementSourceSignatureChecks = 0;
            _recoveredEnhancementLastFullScanMilliseconds = 0;
            _recoveredEnhancementLastFullScanAllocatedBytes = 0;
            _recoveredEnhancementLastCacheHitMilliseconds = 0;
            _recoveredEnhancementLastCacheHitAllocatedBytes = 0;
        }
    }

    public RecoveredEnhancementReferenceCacheSmokeSnapshot
        RecoveredEnhancementReferenceCacheForSmoke
    {
        get
        {
            lock (_recoveredEnhancementReferenceCacheGate)
            {
                return new RecoveredEnhancementReferenceCacheSmokeSnapshot(
                    _recoveredEnhancementFullScanCount,
                    _recoveredEnhancementCacheHitCount,
                    _recoveredEnhancementCatalogPathsVisited,
                    _recoveredEnhancementOutputFilesVisited,
                    _recoveredEnhancementSourceSignatureChecks,
                    _recoveredEnhancementReferenceCache?.CatalogPaths.Length ?? 0,
                    _recoveredEnhancementReferenceCache?.References.Length ?? 0,
                    _recoveredEnhancementReferenceCache?.SourceProbes.Length ?? 0,
                    _recoveredEnhancementLastFullScanMilliseconds,
                    _recoveredEnhancementLastFullScanAllocatedBytes,
                    _recoveredEnhancementLastCacheHitMilliseconds,
                    _recoveredEnhancementLastCacheHitAllocatedBytes);
            }
        }
    }
}
