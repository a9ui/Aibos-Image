using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal enum SharedDataRootResolutionStatus
{
    Resolved,
    LegacyFallback,
    Unavailable,
}

internal sealed record SharedDataRootResolution(
    SharedDataRootResolutionStatus Status,
    string LocatorPath,
    string? SharedDataRoot,
    string? ErrorCode,
    string? Error)
{
    internal bool IsAvailable
        => Status is SharedDataRootResolutionStatus.Resolved
            or SharedDataRootResolutionStatus.LegacyFallback;
}

internal static class SharedDataRootLocator
{
    internal const int SchemaVersion = 1;
    internal const int MaxLocatorBytes = 64 * 1024;
    internal const string ContractId = "PV-ROOT-001";
    internal const string Protocol = "aibos.shared-root-locator/v1";
    internal const string LocatorPathEnvironmentVariable = "AIBOS_SHARED_ROOT_LOCATOR_PATH";
    internal const string DefaultDirectoryName = "Aibos Image";
    internal const string DefaultFileName = "shared-root.v1.json";

    internal static string GetDefaultLocatorPath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData)
            || !Path.IsPathFullyQualified(localApplicationData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, DefaultDirectoryName, DefaultFileName);
    }

    internal static SharedDataRootResolution ResolveForCurrentProcess(string legacyDataRoot)
    {
        if (!TryGetSelectedLocatorPathForCurrentProcess(
                out string locatorPath,
                out string? errorCode,
                out string? error))
        {
            return Unavailable(
                locatorPath,
                errorCode ?? "locator-path-unavailable",
                error ?? "The shared data root locator path is unavailable.");
        }
        return Resolve(locatorPath, legacyDataRoot);
    }

    internal static bool TryGetSelectedLocatorPathForCurrentProcess(
        out string locatorPath,
        out string? errorCode,
        out string? error)
    {
        locatorPath = "";
        errorCode = null;
        error = null;

        string? selected = Environment.GetEnvironmentVariable(
            LocatorPathEnvironmentVariable);
        if (selected is null)
        {
            try
            {
                selected = GetDefaultLocatorPath();
            }
            catch
            {
                errorCode = "locator-path-unavailable";
                error = "The shared data root locator path is unavailable.";
                return false;
            }
        }

        if (!TryNormalizeAbsolutePath(selected, out locatorPath))
        {
            errorCode = "locator-path-invalid";
            error = "The shared data root locator path must be fully qualified.";
            return false;
        }

        return true;
    }

    internal static SharedDataRootResolution Resolve(
        string? locatorPath,
        string legacyDataRoot)
    {
        if (!TryNormalizeAbsolutePath(locatorPath, out string normalizedLocatorPath))
        {
            return Unavailable(
                locatorPath ?? "",
                "locator-path-invalid",
                "The shared data root locator path must be fully qualified.");
        }

        FileProbe locatorProbe = ProbeFile(normalizedLocatorPath);
        if (locatorProbe == FileProbe.Missing)
        {
            if (!TryProbeExistingDirectory(legacyDataRoot, out string normalizedLegacyRoot))
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "legacy-root-unavailable",
                    "The locator is missing and the legacy data root is unavailable.");
            }

            return new SharedDataRootResolution(
                SharedDataRootResolutionStatus.LegacyFallback,
                normalizedLocatorPath,
                normalizedLegacyRoot,
                null,
                null);
        }

        if (locatorProbe == FileProbe.Unavailable)
        {
            return Unavailable(
                normalizedLocatorPath,
                "locator-unreadable",
                "The shared data root locator exists but cannot be read.");
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                normalizedLocatorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxLocatorBytes)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-too-large",
                    "The shared data root locator exceeds its size limit.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-changed-during-read",
                    "The shared data root locator changed while it was being read.");
            }
        }
        catch
        {
            return Unavailable(
                normalizedLocatorPath,
                "locator-unreadable",
                "The shared data root locator exists but cannot be read.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-malformed",
                    "The shared data root locator must be a JSON object.");
            }

            int schemaVersionCount = 0;
            int sharedDataRootCount = 0;
            JsonElement schemaVersionElement = default;
            JsonElement sharedDataRootElement = default;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("schemaVersion"))
                {
                    schemaVersionCount++;
                    schemaVersionElement = property.Value;
                }
                else if (property.NameEquals("sharedDataRoot"))
                {
                    sharedDataRootCount++;
                    sharedDataRootElement = property.Value;
                }
            }

            if (schemaVersionCount > 1 || sharedDataRootCount > 1)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-ambiguous",
                    "The shared data root locator contains duplicate required fields.");
            }

            if (schemaVersionCount != 1
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out int schemaVersion))
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-malformed",
                    "The shared data root locator has no valid schemaVersion.");
            }

            if (schemaVersion != SchemaVersion)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "schema-unsupported",
                    "The shared data root locator schema version is unsupported.");
            }

            if (sharedDataRootCount != 1
                || sharedDataRootElement.ValueKind != JsonValueKind.String)
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "locator-malformed",
                    "The shared data root locator has no valid sharedDataRoot.");
            }

            string? sharedDataRoot = sharedDataRootElement.GetString();
            if (!TryNormalizeAbsolutePath(
                sharedDataRoot,
                out string normalizedSharedDataRoot))
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "data-root-invalid",
                    "The shared data root must be fully qualified.");
            }

            if (!TryProbeExistingDirectory(
                normalizedSharedDataRoot,
                out normalizedSharedDataRoot))
            {
                return Unavailable(
                    normalizedLocatorPath,
                    "data-root-unavailable",
                    "The configured shared data root is unavailable.");
            }

            return new SharedDataRootResolution(
                SharedDataRootResolutionStatus.Resolved,
                normalizedLocatorPath,
                normalizedSharedDataRoot,
                null,
                null);
        }
        catch (JsonException)
        {
            return Unavailable(
                normalizedLocatorPath,
                "locator-malformed",
                "The shared data root locator contains malformed JSON.");
        }
        catch
        {
            return Unavailable(
                normalizedLocatorPath,
                "locator-unreadable",
                "The shared data root locator could not be validated.");
        }
    }

    private static SharedDataRootResolution Unavailable(
        string locatorPath,
        string errorCode,
        string error)
        => new(
            SharedDataRootResolutionStatus.Unavailable,
            locatorPath,
            null,
            errorCode,
            error);

    internal static bool TryNormalizeAbsolutePath(
        string? candidate,
        out string normalized)
    {
        normalized = "";
        try
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || !Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(candidate);
            string? pathRoot = Path.GetPathRoot(fullPath);
            normalized = !string.IsNullOrEmpty(pathRoot)
                && fullPath.Length > pathRoot.Length
                    ? fullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    : fullPath;
            return true;
        }
        catch
        {
            normalized = "";
            return false;
        }
    }

    private static bool TryProbeExistingDirectory(
        string? candidate,
        out string normalized)
    {
        if (!TryNormalizeAbsolutePath(candidate, out normalized))
            return false;

        try
        {
            FileAttributes attributes = File.GetAttributes(normalized);
            return attributes.HasFlag(FileAttributes.Directory);
        }
        catch
        {
            normalized = "";
            return false;
        }
    }

    private static FileProbe ProbeFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? FileProbe.Unavailable
                : FileProbe.File;
        }
        catch (FileNotFoundException)
        {
            return ProbeMissingFile(path);
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeMissingFile(path);
        }
        catch
        {
            return FileProbe.Unavailable;
        }
    }

    private static FileProbe ProbeMissingFile(string path)
    {
        string? current = Path.GetDirectoryName(path);
        string? pathRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(current)
            || string.IsNullOrWhiteSpace(pathRoot))
        {
            return FileProbe.Unavailable;
        }

        while (true)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                return attributes.HasFlag(FileAttributes.Directory)
                    ? FileProbe.Missing
                    : FileProbe.Unavailable;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch
            {
                return FileProbe.Unavailable;
            }

            if (string.Equals(
                current.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return FileProbe.Unavailable;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return FileProbe.Unavailable;
            }

            current = parent;
        }
    }

    private enum FileProbe
    {
        Missing,
        File,
        Unavailable,
    }
}
