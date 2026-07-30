using System.IO;

namespace PhotoViewer.Wpf;

internal enum SharedDataRootActivationStatus
{
    Activated,
    OverridesOnly,
    LegacyUninitialized,
    Unavailable,
}

internal sealed record SharedDataRootActivationResult(
    SharedDataRootActivationStatus Status,
    string? SharedDataRoot,
    IReadOnlyDictionary<string, string> Paths,
    string? ErrorCode,
    string? Error)
{
    internal string? LocatorPath { get; init; }
    internal SharedDataRootResolutionStatus? ResolutionStatus { get; init; }

    internal bool IsAvailable
        => Status is SharedDataRootActivationStatus.Activated
            or SharedDataRootActivationStatus.OverridesOnly
            or SharedDataRootActivationStatus.LegacyUninitialized;
}

/// <summary>
/// Fixes the seven shared durable-store paths once for the process. Existing
/// per-store overrides remain authoritative, while a valid locator supplies
/// every missing path from one root. This class changes process-local
/// environment variables and may acquire the empty TEMP coordination artifact;
/// it never creates a locator, shared root, durable-data directory, or store.
/// </summary>
internal static class SharedDataRootActivation
{
    internal const string FavoritesEnvironmentVariable = "PHOTOVIEWER_WPF_FAVORITES_PATH";
    internal const string SeenEnvironmentVariable = "PHOTOVIEWER_WPF_SEEN_PATH";
    internal const string SettingsEnvironmentVariable = "PHOTOVIEWER_WPF_SETTINGS_PATH";
    internal const string AlbumsEnvironmentVariable = "PHOTOVIEWER_WPF_ALBUMS_PATH";
    internal const string SearchHistoryEnvironmentVariable = "PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH";
    internal const string RecentFoldersEnvironmentVariable = "PHOTOVIEWER_WPF_RECENT_PATH";
    internal const string EnhancementJobsEnvironmentVariable = "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH";
    internal const string EnhancementOutputRootEnvironmentVariable = "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT";
    internal const string SharedEnhancementOutputRootEnvironmentVariable = "PVU_ENHANCE_OUTPUT_ROOT";
    internal const string EnhancementOutputRootConfigFileName = "output-root.txt";

    private static readonly (string EnvironmentVariable, string[] RelativeSegments)[] Stores =
    [
        (FavoritesEnvironmentVariable, ["favorites.json"]),
        (SeenEnvironmentVariable, ["seen.json"]),
        (SettingsEnvironmentVariable, ["settings.json"]),
        (AlbumsEnvironmentVariable, ["albums.json"]),
        (SearchHistoryEnvironmentVariable, ["search-history.json"]),
        (RecentFoldersEnvironmentVariable, ["recent-folders.json"]),
        (EnhancementJobsEnvironmentVariable, ["enhance", "jobs.json"]),
    ];

    private static readonly object ActivationGate = new();
    private static SharedDataRootActivationResult? _current;
    private static SharedDataRootLocatorLease? _readerLease;
    private static HashSet<string> _explicitStoreOverrides = new(StringComparer.Ordinal);

    internal static SharedDataRootActivationResult ActivateForCurrentProcess(string legacyDataRoot)
    {
        lock (ActivationGate)
        {
            if (_current is not null)
                return _current;

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var missing = new List<(string EnvironmentVariable, string[] RelativeSegments)>();
            var explicitOverrides = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string environmentVariable, string[] relativeSegments) in Stores)
            {
                string? existing = Environment.GetEnvironmentVariable(environmentVariable);
                if (string.IsNullOrWhiteSpace(existing))
                {
                    missing.Add((environmentVariable, relativeSegments));
                    continue;
                }

                try
                {
                    paths[environmentVariable] = Path.GetFullPath(existing);
                    explicitOverrides.Add(environmentVariable);
                }
                catch
                {
                    return _current = Unavailable(
                        paths,
                        "store-override-invalid",
                        $"The {environmentVariable} override is invalid.");
                }
            }

            if (missing.Count == 0)
            {
                if (!TryPublishFixedPaths(paths))
                {
                    return _current = Unavailable(
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        "store-routing-failed",
                        "The shared durable-store paths could not be fixed for this process.");
                }

                _explicitStoreOverrides = explicitOverrides;
                return _current = new SharedDataRootActivationResult(
                    SharedDataRootActivationStatus.OverridesOnly,
                    null,
                    paths,
                    null,
                    null);
            }

            if (!SharedDataRootLocator.TryGetSelectedLocatorPathForCurrentProcess(
                    out string locatorPath,
                    out string? locatorPathErrorCode,
                    out string? locatorPathError))
            {
                return _current = Unavailable(
                    paths,
                    locatorPathErrorCode ?? "locator-path-unavailable",
                    locatorPathError ?? "The shared data root locator path is unavailable.");
            }

            if (!SharedDataRootLocatorLease.TryAcquireReader(
                    locatorPath,
                    out SharedDataRootLocatorLease? readerLease,
                    out string? leaseErrorCode,
                    out string? leaseError))
            {
                return _current = Unavailable(
                    paths,
                    leaseErrorCode ?? "locator-reader-lease-unavailable",
                    leaseError ?? "The shared data root locator reader lease is unavailable.");
            }

            SharedDataRootResolution rootResolution =
                SharedDataRootLocator.Resolve(locatorPath, legacyDataRoot);
            SharedDataRootActivationStatus activationStatus =
                SharedDataRootActivationStatus.Activated;
            string sharedDataRoot;
            if (rootResolution.IsAvailable
                && !string.IsNullOrWhiteSpace(rootResolution.SharedDataRoot))
            {
                sharedDataRoot = Path.GetFullPath(rootResolution.SharedDataRoot);
            }
            else if (string.Equals(
                    rootResolution.ErrorCode,
                    "legacy-root-unavailable",
                    StringComparison.Ordinal)
                && SharedDataRootLocator.TryNormalizeAbsolutePath(
                    legacyDataRoot,
                    out string normalizedLegacyRoot))
            {
                // A fresh checkout may not have created its legacy .cache yet.
                // Fix every missing path to that lexical root without creating
                // it so later lazy writers cannot drift when CWD changes.
                activationStatus = SharedDataRootActivationStatus.LegacyUninitialized;
                sharedDataRoot = normalizedLegacyRoot;
            }
            else
            {
                readerLease?.Dispose();
                return _current = Unavailable(
                    paths,
                    rootResolution.ErrorCode ?? "shared-root-unavailable",
                    rootResolution.Error ?? "The shared data root is unavailable.");
            }

            try
            {
                foreach ((string environmentVariable, string[] relativeSegments) in missing)
                {
                    string path = Path.GetFullPath(
                        relativeSegments.Aggregate(
                            sharedDataRoot,
                            static (current, segment) => Path.Combine(current, segment)));
                    paths[environmentVariable] = path;
                }
            }
            catch
            {
                readerLease?.Dispose();
                return _current = Unavailable(
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "store-routing-failed",
                    "The shared durable-store paths could not be fixed for this process.");
            }

            if (!TryPublishFixedPaths(paths))
            {
                readerLease?.Dispose();
                return _current = Unavailable(
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "store-routing-failed",
                    "The shared durable-store paths could not be fixed for this process.");
            }

            _readerLease = readerLease;
            _explicitStoreOverrides = explicitOverrides;
            return _current = new SharedDataRootActivationResult(
                activationStatus,
                sharedDataRoot,
                paths,
                null,
                null)
            {
                LocatorPath = rootResolution.LocatorPath,
                ResolutionStatus = rootResolution.Status,
            };
        }
    }

    internal static IReadOnlyList<string> StoreEnvironmentVariables
        => Stores.Select(static store => store.EnvironmentVariable).ToArray();

    internal static SharedDataRootActivationResult? Current
    {
        get
        {
            lock (ActivationGate)
                return _current;
        }
    }

    internal static bool WasExplicitStoreOverride(string environmentVariable)
    {
        lock (ActivationGate)
            return _explicitStoreOverrides.Contains(environmentVariable);
    }

    internal static string ManagedOutputsRoot(SharedDataRootActivationResult activation)
        => ResolveManagedOutputsRoot(
            activation.Paths[EnhancementJobsEnvironmentVariable]);

    internal static string EnhancementOutputRootConfigPath(string enhancementJobsPath)
    {
        string enhanceStateRoot = Path.GetDirectoryName(
            Path.GetFullPath(enhancementJobsPath))!;
        return Path.Combine(
            enhanceStateRoot,
            EnhancementOutputRootConfigFileName);
    }

    internal static bool TryGetManagedOutputsRootEnvironmentOverride(
        out string? configuredRoot,
        out string? environmentVariable)
    {
        configuredRoot =
            Environment.GetEnvironmentVariable(EnhancementOutputRootEnvironmentVariable);
        environmentVariable = EnhancementOutputRootEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return true;

        configuredRoot =
            Environment.GetEnvironmentVariable(SharedEnhancementOutputRootEnvironmentVariable);
        environmentVariable = SharedEnhancementOutputRootEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return true;

        configuredRoot = null;
        environmentVariable = null;
        return false;
    }

    internal static string ResolveManagedOutputsRoot(string enhancementJobsPath)
    {
        TryGetManagedOutputsRootEnvironmentOverride(
            out string? configuredRoot,
            out _);

        string enhanceStateRoot = Path.GetDirectoryName(
            Path.GetFullPath(enhancementJobsPath))!;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            string configPath = EnhancementOutputRootConfigPath(enhancementJobsPath);
            if (File.Exists(configPath))
                configuredRoot = File.ReadAllText(configPath).Trim();
        }

        if (string.IsNullOrWhiteSpace(configuredRoot))
            return Path.Combine(enhanceStateRoot, "outputs");
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new InvalidDataException(
                $"{EnhancementOutputRootConfigFileName} must contain an absolute path.");
        }
        return Path.GetFullPath(configuredRoot);
    }

    internal static bool TryWriteManagedOutputsRoot(
        string enhancementJobsPath,
        string selectedRoot,
        out string normalizedRoot,
        out string? error)
    {
        normalizedRoot = "";
        error = null;
        string? temporaryPath = null;
        try
        {
            if (TryGetManagedOutputsRootEnvironmentOverride(
                    out _,
                    out string? environmentVariable))
            {
                error = $"{environmentVariable} is active, so the app setting cannot override it.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedRoot)
                || !Path.IsPathFullyQualified(selectedRoot))
            {
                error = "Select an absolute output folder.";
                return false;
            }

            normalizedRoot = Path.GetFullPath(selectedRoot);
            if (!Directory.Exists(normalizedRoot))
            {
                error = "The selected output folder does not exist.";
                return false;
            }

            string configPath = EnhancementOutputRootConfigPath(enhancementJobsPath);
            string configDirectory = Path.GetDirectoryName(configPath)!;
            if (!Directory.Exists(configDirectory))
            {
                error = "The shared Enhancement data folder is unavailable.";
                return false;
            }

            temporaryPath = Path.Combine(
                configDirectory,
                $".{EnhancementOutputRootConfigFileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(normalizedRoot);
                writer.WriteLine();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, configPath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            error = $"The output folder setting could not be saved ({ex.GetType().Name}).";
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    internal static void DisposeProcessLease()
    {
        lock (ActivationGate)
        {
            _readerLease?.Dispose();
            _readerLease = null;
        }
    }

    private static bool TryPublishFixedPaths(IReadOnlyDictionary<string, string> paths)
    {
        var previousValues = StoreEnvironmentVariables.ToDictionary(
            static variable => variable,
            static variable => Environment.GetEnvironmentVariable(variable),
            StringComparer.Ordinal);
        try
        {
            // Resolve every path before publishing any process-local override
            // so a validation failure cannot leave a split root. Publishing
            // explicit overrides too fixes relative-path meaning for the rest
            // of the process lifetime.
            foreach (string environmentVariable in StoreEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(
                    environmentVariable,
                    Path.GetFullPath(paths[environmentVariable]));
            }

            return true;
        }
        catch
        {
            foreach ((string environmentVariable, string? previousValue) in previousValues)
            {
                try
                {
                    Environment.SetEnvironmentVariable(environmentVariable, previousValue);
                }
                catch
                {
                    // Best-effort rollback of process-only values. No file or
                    // machine/user environment state was changed.
                }
            }

            return false;
        }
    }

    private static SharedDataRootActivationResult Unavailable(
        IReadOnlyDictionary<string, string> paths,
        string errorCode,
        string error)
        => new(
            SharedDataRootActivationStatus.Unavailable,
            null,
            paths.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal),
            errorCode,
            error);
}
