using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class SharedDataRootActivationSmokeRunner
{
    private static readonly string[] NonSharedEnvironmentVariables =
    [
        "PHOTOVIEWER_WPF_STATE_PATH",
        "PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY",
    ];

    internal static int Run(string? caseId, string? tempRoot, string? resultPath)
    {
        object result;
        bool ok = false;
        string? safeOutput = null;
        string originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            if (string.IsNullOrWhiteSpace(caseId)
                || string.IsNullOrWhiteSpace(tempRoot)
                || string.IsNullOrWhiteSpace(resultPath))
            {
                throw new ArgumentException(
                    "Activation smoke requires --case, --temp-root, and a result path.");
            }

            string root = RequirePathBelowTemp(tempRoot, "Activation smoke root");
            safeOutput = RequirePathBelowTemp(resultPath, "Activation smoke result");
            Directory.CreateDirectory(root);

            string leaseDirectory = Path.Combine(
                Path.GetDirectoryName(root)
                    ?? throw new InvalidOperationException("Activation smoke parent was unavailable."),
                "locator-leases-" + Path.GetFileName(root));
            SharedDataRootLocatorLease.ConfigureLeaseDirectoryForSmoke(leaseDirectory);

            foreach (string variable in SharedDataRootActivation.StoreEnvironmentVariables)
                Environment.SetEnvironmentVariable(variable, null);
            foreach (string variable in NonSharedEnvironmentVariables)
                Environment.SetEnvironmentVariable(variable, null);

            string dataRoot = Path.Combine(root, "data");
            string legacyRoot = Path.Combine(root, "legacy");
            string overrideRoot = Path.Combine(root, "overrides");
            string alternateProject = Path.Combine(root, "alternate-project");
            string locatorPath = Path.Combine(root, "shared-root.v1.json");
            string productionLeasePath =
                SharedDataRootLocatorLease.GetProductionLockPathForSmoke(locatorPath);
            string expectedProductionLeaseDirectory = Path.Combine(
                Path.GetFullPath(Path.GetTempPath()),
                SharedDataRootLocatorLease.DefaultLeaseDirectoryName);
            bool productionLeasePathMatches = string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(productionLeasePath) ?? ""),
                    Path.GetFullPath(expectedProductionLeaseDirectory),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileName(productionLeasePath),
                    SharedDataRootLocatorLease.LockFileName,
                    StringComparison.Ordinal);
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(overrideRoot);
            Directory.CreateDirectory(alternateProject);

            if (!string.Equals(caseId, "legacy-uninitialized", StringComparison.Ordinal))
                Directory.CreateDirectory(legacyRoot);

            switch (caseId)
            {
                case "valid":
                case "store-override":
                    WriteLocator(locatorPath, dataRoot);
                    break;
                case "invalid":
                    File.WriteAllText(
                        locatorPath,
                        "{\"schemaVersion\":1,\"sharedDataRoot\":",
                        Encoding.UTF8);
                    break;
                case "legacy":
                case "legacy-uninitialized":
                    break;
                case "overrides-only":
                    foreach (string variable in SharedDataRootActivation.StoreEnvironmentVariables)
                    {
                        Environment.SetEnvironmentVariable(
                            variable,
                            Path.Combine(overrideRoot, variable + ".json"));
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown activation smoke case: {caseId}");
            }

            string? favoriteOverride = null;
            if (string.Equals(caseId, "store-override", StringComparison.Ordinal))
            {
                favoriteOverride = Path.Combine(overrideRoot, "favorites-explicit.json");
                Environment.SetEnvironmentVariable(
                    SharedDataRootActivation.FavoritesEnvironmentVariable,
                    favoriteOverride);
            }

            Environment.SetEnvironmentVariable(
                SharedDataRootLocator.LocatorPathEnvironmentVariable,
                string.Equals(caseId, "overrides-only", StringComparison.Ordinal)
                    ? "   "
                    : locatorPath);

            IReadOnlyDictionary<string, string> before = SnapshotTree(root);
            SharedDataRootActivationResult activation =
                SharedDataRootActivation.ActivateForCurrentProcess(legacyRoot);
            IReadOnlyDictionary<string, string> after = SnapshotTree(root);

            Dictionary<string, string?> environment =
                SharedDataRootActivation.StoreEnvironmentVariables.ToDictionary(
                    static variable => variable,
                    static variable => Environment.GetEnvironmentVariable(variable),
                    StringComparer.Ordinal);
            int expectedPathCount = activation.IsAvailable
                ? SharedDataRootActivation.StoreEnvironmentVariables.Count
                : 0;
            bool pathCountMatches = activation.Paths.Count == expectedPathCount;
            bool treeUnchanged = SnapshotsEqual(before, after);
            bool nonSharedUntouched = NonSharedEnvironmentVariables.All(
                static variable => Environment.GetEnvironmentVariable(variable) is null);
            bool pathsMatchEnvironment = activation.Paths.Count == environment.Count(
                    static pair => !string.IsNullOrWhiteSpace(pair.Value))
                && activation.Paths.All(pair =>
                    string.Equals(
                        pair.Value,
                        environment[pair.Key] is string value ? Path.GetFullPath(value) : null,
                        StringComparison.OrdinalIgnoreCase));

            string expectedRoot = caseId switch
            {
                "valid" or "store-override" => dataRoot,
                "legacy" or "legacy-uninitialized" => legacyRoot,
                _ => "",
            };
            bool expectedStatus = caseId switch
            {
                "valid" or "store-override" or "legacy" =>
                    activation.Status == SharedDataRootActivationStatus.Activated,
                "invalid" =>
                    activation.Status == SharedDataRootActivationStatus.Unavailable
                    && string.Equals(
                        activation.ErrorCode,
                        "locator-malformed",
                        StringComparison.Ordinal),
                "overrides-only" =>
                    activation.Status == SharedDataRootActivationStatus.OverridesOnly,
                "legacy-uninitialized" =>
                    activation.Status == SharedDataRootActivationStatus.LegacyUninitialized,
                _ => false,
            };
            bool rootMatches = string.IsNullOrEmpty(expectedRoot)
                ? activation.SharedDataRoot is null
                : string.Equals(
                    Path.GetFullPath(expectedRoot),
                    Path.GetFullPath(activation.SharedDataRoot ?? ""),
                    StringComparison.OrdinalIgnoreCase);
            bool allCanonicalPaths = caseId switch
            {
                "valid" or "legacy" or "legacy-uninitialized" => AllPathsUnder(
                    activation.Paths,
                    expectedRoot),
                "store-override" =>
                    activation.Paths.Count == SharedDataRootActivation.StoreEnvironmentVariables.Count
                    && string.Equals(
                        activation.Paths[SharedDataRootActivation.FavoritesEnvironmentVariable],
                        favoriteOverride,
                        StringComparison.OrdinalIgnoreCase)
                    && activation.Paths
                        .Where(pair => !string.Equals(
                            pair.Key,
                            SharedDataRootActivation.FavoritesEnvironmentVariable,
                            StringComparison.Ordinal))
                        .All(pair => IsInside(pair.Value, dataRoot)),
                "invalid" =>
                    activation.Paths.Count == 0
                    && SharedDataRootActivation.StoreEnvironmentVariables.All(
                        variable => Environment.GetEnvironmentVariable(variable) is null),
                "overrides-only" =>
                    activation.Paths.Count == SharedDataRootActivation.StoreEnvironmentVariables.Count
                    && activation.Paths.Values.All(path => IsInside(path, overrideRoot)),
                _ => false,
            };

            string? outputsRoot = activation.Paths.ContainsKey(
                SharedDataRootActivation.EnhancementJobsEnvironmentVariable)
                    ? SharedDataRootActivation.ManagedOutputsRoot(activation)
                    : null;
            bool outputsMatch = outputsRoot is null
                ? caseId is "invalid"
                : IsInside(
                    outputsRoot,
                    caseId == "overrides-only" ? overrideRoot : expectedRoot);

            bool readerLeaseHeld = true;
            if (activation.Status is SharedDataRootActivationStatus.Activated
                or SharedDataRootActivationStatus.LegacyUninitialized)
            {
                bool writerAcquired = SharedDataRootLocatorLease.TryAcquireWriter(
                    locatorPath,
                    out SharedDataRootLocatorLease? writerLease,
                    out string? writerErrorCode,
                    out _);
                writerLease?.Dispose();
                readerLeaseHeld = !writerAcquired
                    && string.Equals(
                        writerErrorCode,
                        "locator-lease-busy",
                        StringComparison.Ordinal);
            }

            IReadOnlyDictionary<string, string> resolverPathsBefore = activation.IsAvailable
                ? MainWindow.ResolveSharedDurableStorePathsForSmoke()
                : new Dictionary<string, string>(StringComparer.Ordinal);
            Environment.CurrentDirectory = alternateProject;
            Environment.SetEnvironmentVariable(
                SharedDataRootLocator.LocatorPathEnvironmentVariable,
                Path.Combine(root, "different-locator.json"));
            SharedDataRootActivationResult repeated =
                SharedDataRootActivation.ActivateForCurrentProcess(
                    Path.Combine(root, "different-legacy"));
            IReadOnlyDictionary<string, string> resolverPathsAfter = activation.IsAvailable
                ? MainWindow.ResolveSharedDurableStorePathsForSmoke()
                : new Dictionary<string, string>(StringComparer.Ordinal);
            bool productionResolversMatch = !activation.IsAvailable
                || (ResolverPathsMatchActivation(resolverPathsBefore, activation.Paths)
                    && ResolverPathsMatchActivation(resolverPathsAfter, activation.Paths));
            bool fixedForLifetime = ReferenceEquals(activation, repeated)
                && activation.Paths.SequenceEqual(repeated.Paths)
                && pathCountMatches
                && productionResolversMatch
                && pathsMatchEnvironment;
            bool sourceMetadataMatches = caseId switch
            {
                "valid" or "store-override" =>
                    activation.ResolutionStatus == SharedDataRootResolutionStatus.Resolved
                    && string.Equals(
                        activation.LocatorPath,
                        Path.GetFullPath(locatorPath),
                        StringComparison.OrdinalIgnoreCase),
                "legacy" =>
                    activation.ResolutionStatus == SharedDataRootResolutionStatus.LegacyFallback
                    && string.Equals(
                        activation.LocatorPath,
                        Path.GetFullPath(locatorPath),
                        StringComparison.OrdinalIgnoreCase),
                "legacy-uninitialized" =>
                    activation.ResolutionStatus == SharedDataRootResolutionStatus.Unavailable
                    && string.Equals(
                        activation.LocatorPath,
                        Path.GetFullPath(locatorPath),
                        StringComparison.OrdinalIgnoreCase),
                "invalid" or "overrides-only" =>
                    activation.ResolutionStatus is null
                    && activation.LocatorPath is null,
                _ => false,
            };

            ok = expectedStatus
                && rootMatches
                && allCanonicalPaths
                && outputsMatch
                && pathsMatchEnvironment
                && pathCountMatches
                && productionResolversMatch
                && readerLeaseHeld
                && productionLeasePathMatches
                && nonSharedUntouched
                && treeUnchanged
                && fixedForLifetime
                && sourceMetadataMatches;
            result = new
            {
                ok,
                caseId,
                status = activation.Status.ToString(),
                resolutionStatus = activation.ResolutionStatus?.ToString(),
                locatorPath = activation.LocatorPath,
                errorCode = activation.ErrorCode,
                pathCount = activation.Paths.Count,
                expectedPathCount,
                pathCountMatches,
                rootMatches,
                allCanonicalPaths,
                outputsMatch,
                pathsMatchEnvironment,
                productionResolversMatch,
                storePaths = resolverPathsBefore,
                managedOutputsRoot = outputsRoot,
                readerLeaseHeld,
                productionLeasePathMatches,
                nonSharedUntouched,
                treeUnchanged,
                fixedForLifetime,
                sourceMetadataMatches,
                message = ok
                    ? "shared durable-store routing is fixed, leased, isolated, and byte-preserving"
                    : "shared durable-store routing contract failed",
            };

            WriteResult(safeOutput, result);
        }
        catch (Exception ex)
        {
            result = new { ok = false, caseId, message = ex.Message };
            try
            {
                safeOutput ??= string.IsNullOrWhiteSpace(resultPath)
                    ? null
                    : RequirePathBelowTemp(resultPath, "Activation smoke result");
                if (safeOutput is not null)
                    WriteResult(safeOutput, result);
            }
            catch
            {
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }

        return ok ? 0 : 1;
    }

    private static string RequirePathBelowTemp(string candidate, string description)
    {
        string path = Path.GetFullPath(candidate);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(
                temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} must be below TEMP.");
        }

        return path;
    }

    private static void WriteLocator(string path, string dataRoot)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = SharedDataRootLocator.SchemaVersion,
            sharedDataRoot = Path.GetFullPath(dataRoot),
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void WriteResult(string path, object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static bool AllPathsUnder(
        IReadOnlyDictionary<string, string> paths,
        string root)
        => paths.Count == SharedDataRootActivation.StoreEnvironmentVariables.Count
            && paths.Values.All(path => IsInside(path, root));

    private static bool ResolverPathsMatchActivation(
        IReadOnlyDictionary<string, string> resolverPaths,
        IReadOnlyDictionary<string, string> activationPaths)
        => resolverPaths.Count == activationPaths.Count
            && resolverPaths.All(pair =>
                activationPaths.TryGetValue(pair.Key, out string? expected)
                && string.Equals(
                    Path.GetFullPath(pair.Value),
                    Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase));

    private static bool IsInside(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> SnapshotTree(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            snapshot["D:" + Path.GetRelativePath(root, directory)] = "";
        }

        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            snapshot["F:" + Path.GetRelativePath(root, file)] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    private static bool SnapshotsEqual(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count
            && first.All(pair => second.TryGetValue(pair.Key, out string? value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
}
