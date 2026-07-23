using System.Security.Cryptography;
using System.Text.Json;
using PhotoViewer.Wpf;

namespace Aibos.SharedRootSetup;

internal static class SharedRootSetupSmoke
{
    internal static int Run(string resultPath)
    {
        string fullResultPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-shared-root-setup-").FullName;
        object result;
        try
        {
            string dataRoot = Path.Combine(smokeRoot, "data");
            string otherRoot = Path.Combine(smokeRoot, "other");
            string locatorPath = Path.Combine(
                smokeRoot,
                "config",
                SharedDataRootLocator.DefaultFileName);
            string leases = Path.Combine(smokeRoot, "leases");
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(otherRoot);
            SharedDataRootLocatorLease.ConfigureLeaseDirectoryForSmoke(leases);
            WriteValidStores(dataRoot);

            IReadOnlyDictionary<string, string> before = SnapshotTree(dataRoot);
            SharedDataRootSetupResult inspected =
                SharedDataRootConfigurator.Inspect(dataRoot, locatorPath);
            bool inspectOnly = inspected.Status == SharedDataRootSetupStatus.Ready
                && !inspected.Changed
                && !File.Exists(locatorPath)
                && TreesEqual(before, SnapshotTree(dataRoot));

            SharedDataRootSetupResult applied =
                SharedDataRootConfigurator.Apply(dataRoot, locatorPath);
            SharedDataRootResolution resolution = SharedDataRootLocator.Resolve(
                locatorPath,
                otherRoot);
            bool created = applied.Status == SharedDataRootSetupStatus.Created
                && applied.Changed
                && resolution.Status == SharedDataRootResolutionStatus.Resolved
                && string.Equals(
                    resolution.SharedDataRoot,
                    Path.GetFullPath(dataRoot),
                    StringComparison.OrdinalIgnoreCase)
                && TreesEqual(before, SnapshotTree(dataRoot));

            byte[] locatorBytes = File.ReadAllBytes(locatorPath);
            SharedDataRootSetupResult repeated =
                SharedDataRootConfigurator.Apply(dataRoot, locatorPath);
            bool idempotent =
                repeated.Status == SharedDataRootSetupStatus.AlreadyConfigured
                && !repeated.Changed
                && locatorBytes.AsSpan().SequenceEqual(
                    File.ReadAllBytes(locatorPath));

            SharedDataRootSetupResult conflict =
                SharedDataRootConfigurator.Apply(otherRoot, locatorPath);
            bool conflictProtected =
                conflict.Status == SharedDataRootSetupStatus.Blocked
                && conflict.ErrorCode == "locator-conflict"
                && locatorBytes.AsSpan().SequenceEqual(
                    File.ReadAllBytes(locatorPath));

            string busyLocator = Path.Combine(
                smokeRoot,
                "busy",
                SharedDataRootLocator.DefaultFileName);
            bool readerAcquired = SharedDataRootLocatorLease.TryAcquireReader(
                busyLocator,
                out SharedDataRootLocatorLease? readerLease,
                out _,
                out _);
            SharedDataRootSetupResult busy;
            using (readerLease)
                busy = SharedDataRootConfigurator.Apply(dataRoot, busyLocator);
            bool leaseProtected = readerAcquired
                && busy.Status == SharedDataRootSetupStatus.Blocked
                && busy.ErrorCode == "locator-lease-busy"
                && !File.Exists(busyLocator);

            string malformedRoot = Path.Combine(smokeRoot, "malformed");
            Directory.CreateDirectory(malformedRoot);
            WriteValidStores(malformedRoot);
            File.WriteAllText(
                Path.Combine(malformedRoot, "favorites.json"),
                "{\"duplicate\":1,\"duplicate\":2}");
            string malformedLocator = Path.Combine(
                smokeRoot,
                "malformed-config",
                SharedDataRootLocator.DefaultFileName);
            SharedDataRootSetupResult malformed =
                SharedDataRootConfigurator.Apply(
                    malformedRoot,
                    malformedLocator);
            bool malformedProtected =
                malformed.Status == SharedDataRootSetupStatus.Blocked
                && malformed.ErrorCode == "shared-store-ambiguous"
                && !File.Exists(malformedLocator);

            string futureRoot = Path.Combine(smokeRoot, "future");
            Directory.CreateDirectory(futureRoot);
            WriteValidStores(futureRoot);
            File.WriteAllText(
                Path.Combine(futureRoot, "search-history.json"),
                "{\"version\":2,\"entries\":[]}");
            string futureLocator = Path.Combine(
                smokeRoot,
                "future-config",
                SharedDataRootLocator.DefaultFileName);
            SharedDataRootSetupResult future =
                SharedDataRootConfigurator.Apply(futureRoot, futureLocator);
            bool futureProtected =
                future.Status == SharedDataRootSetupStatus.Blocked
                && future.ErrorCode == "shared-store-unsupported"
                && !File.Exists(futureLocator);

            string invalidOutputsRoot = Path.Combine(
                smokeRoot,
                "invalid-outputs");
            Directory.CreateDirectory(invalidOutputsRoot);
            WriteValidStores(invalidOutputsRoot);
            string invalidOutputsPath = Path.Combine(
                invalidOutputsRoot,
                "enhance",
                "outputs");
            Directory.Delete(invalidOutputsPath, recursive: true);
            File.WriteAllText(invalidOutputsPath, "not-a-directory");
            string invalidOutputsLocator = Path.Combine(
                smokeRoot,
                "invalid-outputs-config",
                SharedDataRootLocator.DefaultFileName);
            SharedDataRootSetupResult invalidOutputs =
                SharedDataRootConfigurator.Apply(
                    invalidOutputsRoot,
                    invalidOutputsLocator);
            bool invalidOutputsProtected =
                invalidOutputs.Status == SharedDataRootSetupStatus.Blocked
                && invalidOutputs.ErrorCode
                    == "enhancement-outputs-identity-invalid"
                && !File.Exists(invalidOutputsLocator);

            bool ok = inspectOnly
                && created
                && idempotent
                && conflictProtected
                && leaseProtected
                && malformedProtected
                && futureProtected
                && invalidOutputsProtected;
            result = new
            {
                ok,
                inspectOnly,
                created,
                idempotent,
                conflictProtected,
                leaseProtected,
                malformedProtected,
                futureProtected,
                invalidOutputsProtected,
                status = new
                {
                    inspected = inspected.Status.ToString(),
                    applied = applied.Status.ToString(),
                    repeated = repeated.Status.ToString(),
                    conflict = conflict.Status.ToString(),
                    busy = busy.Status.ToString(),
                    malformed = malformed.Status.ToString(),
                    future = future.Status.ToString(),
                    invalidOutputs = invalidOutputs.Status.ToString(),
                },
            };
        }
        catch (Exception ex)
        {
            result = new { ok = false, error = ex.ToString() };
        }
        finally
        {
            try
            {
                Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
        File.WriteAllText(
            fullResultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(fullResultPath));
        return document.RootElement.TryGetProperty(
                "ok",
                out JsonElement okElement)
            && okElement.ValueKind == JsonValueKind.True
                ? 0
                : 1;
    }

    private static void WriteValidStores(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "enhance", "outputs"));
        File.WriteAllText(
            Path.Combine(root, "favorites.json"),
            "{\"C:\\\\fixture\\\\favorite.png\":3}");
        File.WriteAllText(
            Path.Combine(root, "seen.json"),
            "{\"C:\\\\fixture\\\\seen.png\":true}");
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            "{\"version\":1,\"confirmBeforeDelete\":true}");
        File.WriteAllText(
            Path.Combine(root, "albums.json"),
            "{\"albums\":[]}");
        File.WriteAllText(
            Path.Combine(root, "search-history.json"),
            "{\"version\":1,\"entries\":[]}");
        File.WriteAllText(
            Path.Combine(root, "recent-folders.json"),
            "{\"version\":1,\"lastFolderSet\":[],\"recentFolderSets\":[]}");
        File.WriteAllText(
            Path.Combine(root, "enhance", "jobs.json"),
            "{\"version\":1,\"jobs\":[]}");
        File.WriteAllBytes(
            Path.Combine(root, "enhance", "outputs", "fixture.webp"),
            [1, 2, 3, 4]);
    }

    private static IReadOnlyDictionary<string, string> SnapshotTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

    private static bool TreesEqual(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count
            && first.All(pair => second.TryGetValue(pair.Key, out string? value)
                && string.Equals(value, pair.Value, StringComparison.Ordinal));
}
