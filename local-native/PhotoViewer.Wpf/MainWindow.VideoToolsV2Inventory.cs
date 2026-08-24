using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxVideoToolsV2InventoryJobs = 4_096;
    private const int MaxVideoToolsV2AncestryDepth = 64;

    private sealed record VideoToolsV2InventoryProducer(
        string RootSource,
        ManagedVideoVersion Version,
        IReadOnlyList<string> CatalogAliases);

    private bool TryResolveVideoToolsV2ManagedOutput(
        string rawPath,
        out string canonicalOutput)
    {
        canonicalOutput = "";
        try
        {
            if (string.IsNullOrWhiteSpace(rawPath)
                || rawPath.Length > 32_768
                || !Path.IsPathFullyQualified(rawPath)
                || !string.Equals(
                    Path.GetExtension(rawPath),
                    ManagedVideoExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string lexicalOutput = Path.GetFullPath(rawPath);
            string resolvedOutput = _resolveFinalPath(lexicalOutput);
            if (!Path.IsPathFullyQualified(resolvedOutput)
                || !string.Equals(
                    lexicalOutput,
                    resolvedOutput,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string lexicalRoot = Path.GetFullPath(Path.Combine(
                ResolvedManagedEnhancementOutputsRoot,
                ManagedVideoFolderName));
            string canonicalRoot = _resolveFinalPath(lexicalRoot);
            FileAttributes attributes = File.GetAttributes(resolvedOutput);
            var info = new FileInfo(resolvedOutput);
            if (!IsManagedVideoOutputLocation(
                    lexicalOutput,
                    resolvedOutput,
                    lexicalRoot,
                    canonicalRoot)
                || (attributes & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint)) != 0
                || !info.Exists
                || info.Length <= 0)
            {
                return false;
            }

            canonicalOutput = resolvedOutput;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryBuildVideoToolsV2ManagedVideoVersion(
        JsonElement job,
        IReadOnlyDictionary<string, VideoToolsV2InventoryProducer> producers,
        IReadOnlySet<string> ambiguousJobIds,
        out VideoToolsV2InventoryProducer resolution)
    {
        resolution = null!;
        if (!TryReadVideoToolsV2WorkspaceSnapshot(
                job,
                out VideoToolsV2ReaderSnapshot snapshot)
            || !HasSingleProperty(job, "id")
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !IsSafeVideoToolsJobId(jobId!)
            || ambiguousJobIds.Contains(jobId!)
            || !HasSingleProperty(job, "status")
            || !TryGetExactStringProperty(job, "status", "succeeded")
            || !HasSingleProperty(job, "outputPath")
            || !TryGetStringProperty(job, "outputPath", out string? outputPath)
            || !TryResolveVideoToolsV2ManagedOutput(
                outputPath!,
                out string canonicalOutput)
            || snapshot.OutputFpsDenominator != 1
            || snapshot.OutputFpsNumerator is < 1 or > 60
            || snapshot.OutputFrameCount is < 1 or > VideoToolsV2MaximumSourceFrames
            || !double.IsFinite(snapshot.OutputDurationMs)
            || snapshot.OutputDurationMs is <= 0 or > VideoToolsV2MaximumSourceDurationMs)
        {
            return false;
        }

        string rootSource;
        string? sourceManagedOutputPath = null;
        IReadOnlyList<string> inheritedAliases = [];
        if (snapshot.SourceKind == "managed-video-job")
        {
            if (snapshot.SourceVideoJobId is not string sourceVideoJobId
                || ambiguousJobIds.Contains(sourceVideoJobId)
                || !producers.TryGetValue(
                    sourceVideoJobId,
                    out VideoToolsV2InventoryProducer? producer)
                || !string.Equals(
                    snapshot.SourceCanonicalPath,
                    producer.Version.Output.OutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var producerInfo = new FileInfo(
                producer.Version.Output.OutputPath);
            double producerMtimeMs = new DateTimeOffset(
                producerInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (!producerInfo.Exists
                || producerInfo.Length != snapshot.SourceSize
                || Math.Abs(producerMtimeMs - snapshot.SourceMtimeMs) > 1)
            {
                return false;
            }

            rootSource = producer.RootSource;
            sourceManagedOutputPath = producer.Version.Output.OutputPath;
            inheritedAliases = producer.CatalogAliases;
        }
        else if (snapshot.SourceKind == "staged-displayed-file")
        {
            if (snapshot.SourceVideoJobId is not null
                || !Path.IsPathFullyQualified(snapshot.SourceCanonicalPath))
            {
                return false;
            }
            rootSource = Path.GetFullPath(snapshot.SourceCanonicalPath);
            if (!string.Equals(
                    rootSource,
                    snapshot.SourceCanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        string label = snapshot.Kind == "edit" ? "AI編集" : "AI高画質化";
        var version = new ManagedVideoVersion(
            jobId!,
            snapshot.Kind,
            snapshot.PresetId,
            snapshot.BackendId,
            label,
            false,
            snapshot.SourceVideoJobId,
            sourceManagedOutputPath,
            snapshot.OutputDurationMs / 1_000d,
            snapshot.SourceFpsNumerator,
            snapshot.OutputFpsNumerator,
            snapshot.SourceFrameCount,
            snapshot.OutputFrameCount,
            checked(snapshot.OutputWidth * snapshot.OutputHeight),
            snapshot.OutputWidth,
            snapshot.OutputHeight,
            snapshot.InstructionJa ?? "",
            snapshot.CompiledSummaryJa ?? "",
            "",
            snapshot.Steps ?? 0,
            0,
            "",
            "",
            0,
            snapshot.Strength ?? 0,
            0,
            "h264",
            "mp4",
            8,
            new ManagedVideoDeliverySnapshot(
                snapshot.BackendId,
                label,
                snapshot.OutputFpsNumerator,
                snapshot.OutputFrameCount,
                snapshot.OutputDurationMs / 1_000d,
                "bounded-v2",
                snapshot.SourceAudioStreamCount == 1),
            ReadEnhancementActivityAtUtc(job),
            new ManagedVideoOutput(
                canonicalOutput,
                snapshot.SourceSize,
                snapshot.SourceMtimeMs));
        string[] aliases = inheritedAliases
            .Append(rootSource)
            .Append(snapshot.SourceCanonicalPath)
            .Select(NormalizeCatalogEnhancementPath)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        resolution = new VideoToolsV2InventoryProducer(
            rootSource,
            version,
            aliases);
        return true;
    }

    private void ResolveVideoToolsV2ManagedInventory(
        IReadOnlyList<JsonElement> pendingJobs,
        IReadOnlySet<string> ambiguousJobIds,
        Dictionary<string, List<ManagedVideoVersion>> versionsBySource,
        Dictionary<string, List<ManagedVideoVersion>> catalogVersionsByPath,
        ref int candidateCount)
    {
        if (pendingJobs.Count == 0
            || pendingJobs.Count > MaxVideoToolsV2InventoryJobs)
        {
            return;
        }

        var producers = new Dictionary<string, VideoToolsV2InventoryProducer>(
            StringComparer.OrdinalIgnoreCase);
        var duplicateProducerIds = new HashSet<string>(
            ambiguousJobIds,
            StringComparer.OrdinalIgnoreCase);
        foreach (string jobId in FindAmbiguousVideoToolsV2OutputClaimJobIds(
                     pendingJobs,
                     ambiguousJobIds))
        {
            duplicateProducerIds.Add(jobId);
        }
        foreach ((string source, List<ManagedVideoVersion> versions) in versionsBySource)
        {
            foreach (ManagedVideoVersion version in versions)
            {
                if (!IsSafeVideoToolsJobId(version.JobId)
                    || duplicateProducerIds.Contains(version.JobId))
                {
                    continue;
                }
                var producer = new VideoToolsV2InventoryProducer(
                    source,
                    version,
                    [source]);
                if (!producers.TryAdd(version.JobId, producer))
                {
                    producers.Remove(version.JobId);
                    duplicateProducerIds.Add(version.JobId);
                }
            }
        }

        List<JsonElement> unresolved = pendingJobs.ToList();
        for (int depth = 0;
             depth < MaxVideoToolsV2AncestryDepth && unresolved.Count > 0;
             depth++)
        {
            bool progressed = false;
            var next = new List<JsonElement>(unresolved.Count);
            foreach (JsonElement job in unresolved)
            {
                if (!TryBuildVideoToolsV2ManagedVideoVersion(
                        job,
                        producers,
                        duplicateProducerIds,
                        out VideoToolsV2InventoryProducer resolution))
                {
                    next.Add(job);
                    continue;
                }

                ManagedVideoVersion version = resolution.Version;
                if (!producers.TryAdd(version.JobId, resolution))
                {
                    producers.Remove(version.JobId);
                    duplicateProducerIds.Add(version.JobId);
                    continue;
                }
                if (!versionsBySource.TryGetValue(
                        resolution.RootSource,
                        out List<ManagedVideoVersion>? versions))
                {
                    versions = [];
                    versionsBySource[resolution.RootSource] = versions;
                    candidateCount++;
                }
                if (versions.Any(candidate => string.Equals(
                        candidate.Output.OutputPath,
                        version.Output.OutputPath,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    producers.Remove(version.JobId);
                    duplicateProducerIds.Add(version.JobId);
                    continue;
                }
                versions.Add(version);
                foreach (string alias in resolution.CatalogAliases)
                    catalogVersionsByPath.TryAdd(alias, versions);
                progressed = true;
            }
            if (!progressed)
                break;
            unresolved = next;
        }
        // Cycles, missing or ambiguous producers, and over-depth ancestry stay
        // unresolved and therefore never enter the managed Videos inventory.
    }

    private HashSet<string> FindAmbiguousVideoToolsV2OutputClaimJobIds(
        IReadOnlyList<JsonElement> pendingJobs,
        IReadOnlySet<string> ambiguousJobIds)
    {
        var claims = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var ambiguousClaims = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement job in pendingJobs)
        {
            if (!TryReadVideoToolsV2WorkspaceSnapshot(job, out _)
                || !HasSingleProperty(job, "id")
                || !TryGetStringProperty(job, "id", out string? jobId)
                || !IsSafeVideoToolsJobId(jobId!)
                || ambiguousJobIds.Contains(jobId!)
                || !HasSingleProperty(job, "status")
                || !TryGetExactStringProperty(job, "status", "succeeded")
                || !HasSingleProperty(job, "outputPath")
                || !TryGetStringProperty(
                    job,
                    "outputPath",
                    out string? outputPath)
                || !TryResolveVideoToolsV2ManagedOutput(
                    outputPath!,
                    out string canonicalOutput))
            {
                continue;
            }

            if (claims.TryGetValue(
                    canonicalOutput,
                    out string? existingJobId))
            {
                ambiguousClaims.Add(existingJobId);
                ambiguousClaims.Add(jobId!);
            }
            else
            {
                claims.Add(canonicalOutput, jobId!);
            }
        }
        return ambiguousClaims;
    }

    private bool TryValidateVideoToolsV2InventoryAncestry(
        Tile tile,
        ManagedVideoVersion candidate,
        out ManagedVideoVersion version)
    {
        version = null!;
        IReadOnlyList<ManagedVideoVersion> inventory =
            GetManagedVideoVersionsForPath(tile.Path);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ManagedVideoVersion current = candidate;
        for (int depth = 0; depth < MaxVideoToolsV2AncestryDepth; depth++)
        {
            if (!visited.Add(current.JobId)
                || !TryResolveVideoToolsV2ManagedOutput(
                    current.Output.OutputPath,
                    out string verifiedOutput)
                || !string.Equals(
                    verifiedOutput,
                    current.Output.OutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (current.VersionKind == "generation")
            {
                version = candidate;
                return true;
            }
            if (current.VersionKind is not ("edit" or "finish"))
                return false;
            if (current.SourceProducerJobId is null)
            {
                if (current.SourceManagedOutputPath is not null)
                    return false;
                version = candidate;
                return true;
            }
            if (current.SourceManagedOutputPath is null)
                return false;

            ManagedVideoVersion[] producers = inventory
                .Where(item => string.Equals(
                    item.JobId,
                    current.SourceProducerJobId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (producers.Length != 1
                || !string.Equals(
                    producers[0].Output.OutputPath,
                    current.SourceManagedOutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            current = producers[0];
        }
        return false;
    }

    public string[] ResolveVideoToolsV2ManagedInventoryForSmoke(
        JsonElement jobs,
        out string[] versionKinds,
        out string[] outputPaths,
        out string[] labels)
    {
        var versions = new Dictionary<string, List<ManagedVideoVersion>>(
            EnhancementSourceIdentityComparer);
        var aliases = new Dictionary<string, List<ManagedVideoVersion>>(
            StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<JsonElement>();
        foreach (JsonElement job in jobs.EnumerateArray())
        {
            if (TryGetStringProperty(job, "id", out string? id)
                && !seen.Add(id!))
            {
                ambiguous.Add(id!);
            }
            pending.Add(job.Clone());
        }
        int count = 0;
        ResolveVideoToolsV2ManagedInventory(
            pending,
            ambiguous,
            versions,
            aliases,
            ref count);
        VideoToolsV2InventoryProducer[] resolved = versions
            .SelectMany(pair => pair.Value.Select(version =>
                new VideoToolsV2InventoryProducer(pair.Key, version, [])))
            .OrderBy(item => item.Version.CompletedAtUtc)
            .ThenBy(item => item.Version.JobId, StringComparer.Ordinal)
            .ToArray();
        versionKinds = resolved.Select(item => item.Version.VersionKind).ToArray();
        outputPaths = resolved.Select(item => item.Version.Output.OutputPath).ToArray();
        _videoVersions.Clear();
        foreach ((string source, List<ManagedVideoVersion> sourceVersions) in versions)
            _videoVersions[source] = sourceVersions;
        _modalVideoVersions.Clear();
        _modalVideoVersions.AddRange(resolved.Select(item => item.Version));
        labels = Enumerable.Range(0, _modalVideoVersions.Count)
            .Select(ModalVideoVersionChoiceLabel)
            .ToArray();
        return resolved.Select(item => item.RootSource).ToArray();
    }

    public bool RevealResolvedVideoToolsV2OutputForSmoke(
        string jobId,
        out string fileName,
        out string[] arguments)
    {
        fileName = "";
        arguments = [];
        ManagedVideoVersion[] matches = _videoVersions.Values
            .SelectMany(static versions => versions)
            .Where(version => string.Equals(
                version.JobId,
                jobId,
                StringComparison.Ordinal))
            .Distinct()
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            return false;

        ProcessStartInfo? captured = null;
        Func<ProcessStartInfo, bool> previous = _explorerLauncher;
        _explorerLauncher = info =>
        {
            captured = info;
            return true;
        };
        try
        {
            bool opened = TryRevealEnhancementVideoOutputInExplorer(matches[0]);
            fileName = captured?.FileName ?? "";
            arguments = captured?.ArgumentList.ToArray() ?? [];
            return opened && captured is not null;
        }
        finally
        {
            _explorerLauncher = previous;
        }
    }
}
