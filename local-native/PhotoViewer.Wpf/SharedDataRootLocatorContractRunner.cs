using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class SharedDataRootLocatorContractRunner
{
    private sealed record CaseResult(
        string Id,
        bool Ok,
        string Status,
        string? ErrorCode,
        bool StatusMatches,
        bool ErrorMatches,
        bool RootMatches,
        bool TreeUnchanged,
        bool NoImplicitLocatorCreated,
        bool CrossLocationMatches);

    internal static int Run(
        string? contractPath,
        string? tempRoot,
        string? resultPath)
    {
        if (!TryNormalizeTempPath(tempRoot, out string fixtureRoot)
            || !TryNormalizeTempPath(resultPath, out string resultFullPath)
            || string.IsNullOrWhiteSpace(contractPath))
        {
            return 2;
        }

        var caseResults = new List<CaseResult>();
        try
        {
            string contractFullPath = Path.GetFullPath(contractPath);
            using JsonDocument contractDocument = JsonDocument.Parse(
                File.ReadAllBytes(contractFullPath));
            JsonElement contract = contractDocument.RootElement;
            if (contract.ValueKind != JsonValueKind.Object
                || contract.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(
                    contract.GetProperty("contractId").GetString(),
                    SharedDataRootLocator.ContractId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    contract.GetProperty("protocol").GetString(),
                    SharedDataRootLocator.Protocol,
                    StringComparison.Ordinal)
                || !string.Equals(
                    contract.GetProperty("environmentVariable").GetString(),
                    SharedDataRootLocator.LocatorPathEnvironmentVariable,
                    StringComparison.Ordinal)
                || contract.GetProperty("maxLocatorBytes").GetInt32()
                    != SharedDataRootLocator.MaxLocatorBytes
                || contract.GetProperty("activation").GetString() != "reader-only"
                || !ValidateContractShape(contract))
            {
                throw new InvalidDataException("locator contract metadata mismatch");
            }

            Directory.CreateDirectory(fixtureRoot);
            foreach (JsonElement testCase in contract.GetProperty("cases").EnumerateArray())
                caseResults.Add(RunCase(fixtureRoot, testCase));

            bool defaultPathShapeOk = ValidateDefaultPathShape();
            bool ok = defaultPathShapeOk && caseResults.All(static item => item.Ok);
            WriteResult(resultFullPath, new
            {
                ok,
                contractId = SharedDataRootLocator.ContractId,
                protocol = SharedDataRootLocator.Protocol,
                activation = "reader-only",
                defaultPathShapeOk,
                caseCount = caseResults.Count,
                cases = caseResults,
            });
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteResult(resultFullPath, new
            {
                ok = false,
                contractId = SharedDataRootLocator.ContractId,
                errorType = ex.GetType().Name,
            });
            return 1;
        }
    }

    private static CaseResult RunCase(string fixtureRoot, JsonElement testCase)
    {
        string id = testCase.GetProperty("id").GetString()
            ?? throw new InvalidDataException("case id missing");
        if (id.Length is < 1 or > 80
            || id.Any(static value =>
                !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_')))
        {
            throw new InvalidDataException("case id invalid");
        }

        string mode = testCase.GetProperty("mode").GetString()
            ?? throw new InvalidDataException("case mode missing");
        bool createLegacyRoot = !testCase.TryGetProperty(
            "legacyRoot",
            out JsonElement legacyRootElement)
            || legacyRootElement.GetString() != "missing";

        string caseRoot = Path.Combine(fixtureRoot, id);
        string legacyRoot = Path.Combine(caseRoot, "legacy");
        string dataRoot = Path.Combine(caseRoot, "data");
        string secondDataRoot = Path.Combine(caseRoot, "data-b");
        string missingDataRoot = Path.Combine(caseRoot, "missing-data");
        string secondLegacyRoot = Path.Combine(caseRoot, "legacy-b");
        string locatorPath = Path.Combine(caseRoot, SharedDataRootLocator.DefaultFileName);
        Directory.CreateDirectory(caseRoot);
        if (createLegacyRoot)
            Directory.CreateDirectory(legacyRoot);

        bool lockDuringRead = false;
        string effectiveLocatorPath = locatorPath;
        switch (mode)
        {
            case "missing":
                break;
            case "valid":
            case "environment-valid":
            case "locked-valid":
            case "normalized-existing-root":
            case "same-locator-different-legacy-roots":
                Directory.CreateDirectory(dataRoot);
                string configuredRoot = mode == "normalized-existing-root"
                    ? Path.Combine(dataRoot, ".") + Path.DirectorySeparatorChar
                    : dataRoot;
                WriteLocator(
                    locatorPath,
                    1,
                    configuredRoot,
                    includeUnknownField: true);
                lockDuringRead = mode == "locked-valid";
                if (mode == "same-locator-different-legacy-roots")
                    Directory.CreateDirectory(secondLegacyRoot);
                break;
            case "malformed":
                File.WriteAllText(
                    locatorPath,
                    "{\"schemaVersion\":1,\"sharedDataRoot\":");
                break;
            case "future":
                Directory.CreateDirectory(dataRoot);
                WriteLocator(locatorPath, 2, dataRoot, includeUnknownField: true);
                break;
            case "relative-data-root":
                WriteLocator(locatorPath, 1, "relative-data", includeUnknownField: false);
                break;
            case "invalid-data-root-character":
                string volumeRoot = Path.GetPathRoot(caseRoot)
                    ?? throw new InvalidDataException("volume root unavailable");
                WriteLocator(
                    locatorPath,
                    1,
                    volumeRoot + "invalid\0root",
                    includeUnknownField: false);
                break;
            case "missing-data-root":
                WriteLocator(
                    locatorPath,
                    1,
                    missingDataRoot,
                    includeUnknownField: false);
                break;
            case "file-data-root":
                File.WriteAllText(dataRoot, "synthetic file root");
                WriteLocator(locatorPath, 1, dataRoot, includeUnknownField: false);
                break;
            case "duplicate-required-field":
                Directory.CreateDirectory(dataRoot);
                string encodedDataRoot = JsonSerializer.Serialize(dataRoot);
                File.WriteAllText(
                    locatorPath,
                    $"{{\"schemaVersion\":1,\"schemaVersion\":1,\"sharedDataRoot\":{encodedDataRoot}}}");
                break;
            case "duplicate-shared-data-root":
                Directory.CreateDirectory(dataRoot);
                Directory.CreateDirectory(secondDataRoot);
                string encodedFirstDataRoot = JsonSerializer.Serialize(dataRoot);
                string encodedSecondDataRoot = JsonSerializer.Serialize(secondDataRoot);
                File.WriteAllText(
                    locatorPath,
                    $"{{\"schemaVersion\":1,\"sharedDataRoot\":{encodedFirstDataRoot},\"sharedDataRoot\":{encodedSecondDataRoot}}}");
                break;
            case "oversized":
                File.WriteAllText(
                    locatorPath,
                    new string(' ', SharedDataRootLocator.MaxLocatorBytes + 1));
                break;
            case "relative-locator-path":
            case "environment-relative-locator":
                effectiveLocatorPath = Path.Combine(
                    "relative",
                    SharedDataRootLocator.DefaultFileName);
                break;
            case "invalid-locator-character":
                effectiveLocatorPath =
                    (Path.GetPathRoot(caseRoot) ?? "C:\\") + "invalid\0locator";
                break;
            case "environment-whitespace-locator":
                effectiveLocatorPath = "   ";
                break;
            case "unavailable-volume-locator":
                effectiveLocatorPath = FindUnavailableVolumeLocatorPath();
                break;
            case "non-directory-locator-parent":
                string nonDirectoryParent = Path.Combine(caseRoot, "parent-file");
                File.WriteAllText(nonDirectoryParent, "synthetic parent file");
                effectiveLocatorPath = Path.Combine(
                    nonDirectoryParent,
                    SharedDataRootLocator.DefaultFileName);
                break;
            default:
                throw new InvalidDataException("unknown locator case mode");
        }

        IReadOnlyDictionary<string, string> before = SnapshotTree(caseRoot);
        SharedDataRootResolution resolution;
        using (FileStream? exclusiveHandle = lockDuringRead
            ? new FileStream(
                locatorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None)
            : null)
        {
            if (mode is "environment-valid"
                or "environment-relative-locator"
                or "environment-whitespace-locator")
            {
                string? previous = Environment.GetEnvironmentVariable(
                    SharedDataRootLocator.LocatorPathEnvironmentVariable);
                try
                {
                    Environment.SetEnvironmentVariable(
                        SharedDataRootLocator.LocatorPathEnvironmentVariable,
                        effectiveLocatorPath);
                    resolution = SharedDataRootLocator.ResolveForCurrentProcess(legacyRoot);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(
                        SharedDataRootLocator.LocatorPathEnvironmentVariable,
                        previous);
                }
            }
            else
            {
                resolution = SharedDataRootLocator.Resolve(
                    effectiveLocatorPath,
                    legacyRoot);
            }
        }

        bool crossLocationMatches = true;
        if (mode == "same-locator-different-legacy-roots")
        {
            SharedDataRootResolution secondResolution = SharedDataRootLocator.Resolve(
                effectiveLocatorPath,
                secondLegacyRoot);
            crossLocationMatches =
                secondResolution.Status == SharedDataRootResolutionStatus.Resolved
                && string.Equals(
                    resolution.SharedDataRoot,
                    secondResolution.SharedDataRoot,
                    StringComparison.Ordinal);
        }
        IReadOnlyDictionary<string, string> after = SnapshotTree(caseRoot);
        JsonElement expected = testCase.GetProperty("expected");
        string expectedStatus = expected.GetProperty("status").GetString() ?? "";
        string? expectedErrorCode = expected.TryGetProperty(
            "errorCode",
            out JsonElement errorCodeElement)
            && errorCodeElement.ValueKind == JsonValueKind.String
                ? errorCodeElement.GetString()
                : null;
        string expectedRoot = expected.GetProperty("root").GetString() ?? "none";
        string? expectedDataRoot = expectedRoot switch
        {
            "data" => dataRoot,
            "legacy" => legacyRoot,
            "none" => null,
            _ => throw new InvalidDataException("unknown expected root"),
        };

        bool statusMatches = string.Equals(
            resolution.Status.ToString(),
            expectedStatus,
            StringComparison.Ordinal);
        bool errorMatches = string.Equals(
            resolution.ErrorCode,
            expectedErrorCode,
            StringComparison.Ordinal);
        bool rootMatches = expectedDataRoot is null
            ? resolution.SharedDataRoot is null
            : string.Equals(
                Path.GetFullPath(resolution.SharedDataRoot ?? ""),
                Path.GetFullPath(expectedDataRoot),
                StringComparison.OrdinalIgnoreCase);
        bool treeUnchanged = SnapshotsEqual(before, after);
        bool noImplicitLocatorCreated = mode != "missing"
            || !File.Exists(locatorPath);
        bool ok = statusMatches
            && errorMatches
            && rootMatches
            && treeUnchanged
            && noImplicitLocatorCreated
            && crossLocationMatches;

        return new CaseResult(
            id,
            ok,
            resolution.Status.ToString(),
            resolution.ErrorCode,
            statusMatches,
            errorMatches,
            rootMatches,
            treeUnchanged,
            noImplicitLocatorCreated,
            crossLocationMatches);
    }

    private static void WriteLocator(
        string path,
        int schemaVersion,
        string sharedDataRoot,
        bool includeUnknownField)
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["sharedDataRoot"] = sharedDataRoot,
        };
        if (includeUnknownField)
        {
            document["ownerMarker"] = new Dictionary<string, object?>
            {
                ["preserve"] = true,
            };
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyDictionary<string, string> SnapshotTree(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var rootInfo = new DirectoryInfo(root);
        snapshot["D:."] =
            $"{(int)rootInfo.Attributes}:{rootInfo.LastWriteTimeUtc.Ticks}";
        foreach (string directory in Directory.EnumerateDirectories(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            snapshot["D:" + Path.GetRelativePath(root, directory)] =
                $"{(int)info.Attributes}:{info.LastWriteTimeUtc.Ticks}";
        }

        foreach (string file in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            string digest = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(file)));
            snapshot["F:" + Path.GetRelativePath(root, file)] =
                $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{digest}";
        }

        return snapshot;
    }

    private static string FindUnavailableVolumeLocatorPath()
    {
        var usedRoots = DriveInfo.GetDrives()
            .Select(static drive => drive.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            string root = $"{letter}:\\";
            if (!usedRoots.Contains(root))
            {
                return Path.Combine(
                    root,
                    SharedDataRootLocator.DefaultDirectoryName,
                    SharedDataRootLocator.DefaultFileName);
            }
        }

        throw new InvalidDataException("no unavailable test volume was available");
    }

    private static bool SnapshotsEqual(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
        => before.Count == after.Count
            && before.All(pair =>
                after.TryGetValue(pair.Key, out string? value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool ValidateDefaultPathShape()
    {
        try
        {
            string path = SharedDataRootLocator.GetDefaultLocatorPath();
            return Path.IsPathFullyQualified(path)
                && string.Equals(
                    Path.GetFileName(path),
                    SharedDataRootLocator.DefaultFileName,
                    StringComparison.Ordinal)
                && string.Equals(
                    Directory.GetParent(path)?.Name,
                    SharedDataRootLocator.DefaultDirectoryName,
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateContractShape(JsonElement contract)
    {
        JsonElement defaultLocator = contract.GetProperty("defaultLocator");
        string[] relativeSegments = defaultLocator
            .GetProperty("relativeSegments")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? "")
            .ToArray();
        JsonElement document = contract.GetProperty("document");
        JsonElement requiredFields = document.GetProperty("requiredFields");
        JsonElement locatorLease = contract.GetProperty("locatorLease");
        JsonElement leaseDirectory = locatorLease.GetProperty("directory");
        JsonElement leaseIdentity = locatorLease.GetProperty("identity");
        JsonElement readerLease = locatorLease.GetProperty("reader");
        JsonElement writerLease = locatorLease.GetProperty("writer");
        string[] leaseRelativeSegments = leaseDirectory
            .GetProperty("relativeSegments")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? "")
            .ToArray();
        string[] expectedLayout =
        [
            "favorites.json",
            "seen.json",
            "settings.json",
            "albums.json",
            "search-history.json",
            "recent-folders.json",
            "enhance/jobs.json",
            "enhance/outputs/**",
        ];
        string[] actualLayout = contract
            .GetProperty("dataLayout")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? "")
            .ToArray();

        return defaultLocator.GetProperty("specialFolder").GetString()
                == "LocalApplicationData"
            && relativeSegments.SequenceEqual(
                [
                    SharedDataRootLocator.DefaultDirectoryName,
                    SharedDataRootLocator.DefaultFileName,
                ],
                StringComparer.Ordinal)
            && document.GetProperty("encoding").GetString() == "UTF-8"
            && requiredFields.GetProperty("schemaVersion").GetInt32()
                == SharedDataRootLocator.SchemaVersion
            && requiredFields.GetProperty("sharedDataRoot").GetString()
                == "fully-qualified existing directory"
            && document.GetProperty("unknownFields").GetString() == "ignored"
            && document.GetProperty("duplicateRequiredFields").GetString()
                == "rejected"
            && leaseDirectory.GetProperty("specialFolder").GetString()
                == "Temporary"
            && leaseRelativeSegments.SequenceEqual(
                [SharedDataRootLocatorLease.DefaultLeaseDirectoryName],
                StringComparer.Ordinal)
            && leaseIdentity.GetProperty("scope").GetString()
                == "protocol-global within the temporary directory"
            && leaseIdentity.GetProperty("fileName").GetString()
                == SharedDataRootLocatorLease.LockFileName
            && leaseIdentity.GetProperty("locatorPathEncoding").GetString()
                == "none"
            && readerLease.GetProperty("fileMode").GetString() == "OpenOrCreate"
            && readerLease.GetProperty("fileAccess").GetString() == "Read"
            && readerLease.GetProperty("fileShare").GetString() == "Read"
            && readerLease.GetProperty("lifetime").GetString() == "process"
            && writerLease.GetProperty("fileMode").GetString() == "OpenOrCreate"
            && writerLease.GetProperty("fileAccess").GetString() == "ReadWrite"
            && writerLease.GetProperty("fileShare").GetString() == "None"
            && writerLease.GetProperty("lifetime").GetString()
                == "create-or-replace-operation"
            && locatorLease.GetProperty("contents").GetString() == "empty"
            && locatorLease.GetProperty("runtimeDeletion").GetString() == "never"
            && actualLayout.SequenceEqual(expectedLayout, StringComparer.Ordinal);
    }

    private static bool TryNormalizeTempPath(
        string? candidate,
        out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(candidate);
            string tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string tempPrefix = tempRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            normalized = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteResult(string resultPath, object result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
