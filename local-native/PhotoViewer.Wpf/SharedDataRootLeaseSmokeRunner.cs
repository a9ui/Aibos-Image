using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class SharedDataRootLeaseSmokeRunner
{
    internal static int HoldReader(
        string? resultPath,
        string? tempRoot,
        string? locatorPath,
        string? legacyRoot,
        string? readyPath,
        string? releasePath,
        string? leaseDirectory)
    {
        object result;
        bool ok = false;
        string? output = null;
        try
        {
            string root = RequireTempRoot(tempRoot);
            output = RequireInsideRoot(resultPath, root, "result");
            string locator = RequireInsideRoot(locatorPath, root, "locator");
            string legacy = RequireInsideRoot(legacyRoot, root, "legacy root");
            string ready = RequireInsideRoot(readyPath, root, "ready signal");
            string release = RequireInsideRoot(releasePath, root, "release signal");
            string leases = RequireInsideRoot(leaseDirectory, root, "lease directory");

            SharedDataRootLocatorLease.ConfigureLeaseDirectoryForSmoke(leases);
            foreach (string variable in SharedDataRootActivation.StoreEnvironmentVariables)
                Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(
                SharedDataRootLocator.LocatorPathEnvironmentVariable,
                locator);

            SharedDataRootActivationResult activation =
                SharedDataRootActivation.ActivateForCurrentProcess(legacy);
            bool activated = activation.IsAvailable
                && activation.Paths.Count == SharedDataRootActivation.StoreEnvironmentVariables.Count;
            if (!activated)
            {
                result = new
                {
                    ok = false,
                    status = activation.Status.ToString(),
                    errorCode = activation.ErrorCode,
                    pathCount = activation.Paths.Count,
                    message = "reader holder activation failed",
                };
                WriteResult(output, result);
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
            File.WriteAllText(ready, "ready", new UTF8Encoding(false));
            var wait = Stopwatch.StartNew();
            while (!File.Exists(release) && wait.Elapsed < TimeSpan.FromSeconds(20))
                Thread.Sleep(25);

            bool released = File.Exists(release);
            ok = activated && released;
            result = new
            {
                ok,
                status = activation.Status.ToString(),
                pathCount = activation.Paths.Count,
                sharedDataRoot = activation.SharedDataRoot,
                released,
                message = ok
                    ? "reader lease was held until the release signal"
                    : "reader lease holder timed out",
            };
            WriteResult(output, result);
        }
        catch (Exception ex)
        {
            result = new { ok = false, message = ex.Message };
            try
            {
                if (output is not null)
                    WriteResult(output, result);
            }
            catch
            {
            }
        }

        return ok ? 0 : 1;
    }

    internal static int RunWriter(
        string? mode,
        string? resultPath,
        string? tempRoot,
        string? locatorPath,
        string? sharedDataRoot,
        string? leaseDirectory)
    {
        object result;
        bool ok = false;
        string? output = null;
        try
        {
            string root = RequireTempRoot(tempRoot);
            output = RequireInsideRoot(resultPath, root, "result");
            string locator = RequireInsideRoot(locatorPath, root, "locator");
            string dataRoot = RequireInsideRoot(sharedDataRoot, root, "shared data root");
            string leases = RequireInsideRoot(leaseDirectory, root, "lease directory");
            if (!Directory.Exists(dataRoot))
                throw new InvalidOperationException("The synthetic shared data root must exist.");
            if (mode is not ("create" or "replace"))
                throw new ArgumentException("Writer smoke mode must be create or replace.");

            SharedDataRootLocatorLease.ConfigureLeaseDirectoryForSmoke(leases);
            bool existedBefore = File.Exists(locator);
            string? hashBefore = existedBefore ? HashFile(locator) : null;
            bool acquired = SharedDataRootLocatorLease.TryAcquireWriter(
                locator,
                out SharedDataRootLocatorLease? writerLease,
                out string? errorCode,
                out _);
            try
            {
                if (acquired)
                    WriteLocatorAtomically(mode, locator, dataRoot);
            }
            finally
            {
                writerLease?.Dispose();
            }

            bool existsAfter = File.Exists(locator);
            string? hashAfter = existsAfter ? HashFile(locator) : null;
            bool locatorChanged = existedBefore != existsAfter
                || !string.Equals(hashBefore, hashAfter, StringComparison.Ordinal);
            ok = true;
            result = new
            {
                ok,
                mode,
                acquired,
                errorCode,
                existedBefore,
                existsAfter,
                locatorChanged,
                resolvedRoot = existsAfter ? ReadLocatorRoot(locator) : null,
                message = acquired
                    ? "exclusive writer lease acquired and synthetic locator mutation completed"
                    : "exclusive writer lease was blocked without changing the locator",
            };
            WriteResult(output, result);
        }
        catch (Exception ex)
        {
            result = new { ok = false, mode, message = ex.Message };
            try
            {
                if (output is not null)
                    WriteResult(output, result);
            }
            catch
            {
            }
        }

        return ok ? 0 : 1;
    }

    private static void WriteLocatorAtomically(
        string mode,
        string locatorPath,
        string sharedDataRoot)
    {
        bool exists = File.Exists(locatorPath);
        if (mode == "create" && exists)
            throw new IOException("Create mode refuses to replace an existing locator.");
        if (mode == "replace" && !exists)
            throw new IOException("Replace mode requires an existing locator.");

        Directory.CreateDirectory(Path.GetDirectoryName(locatorPath)!);
        string temporary = locatorPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = SharedDataRootLocator.SchemaVersion,
                sharedDataRoot = Path.GetFullPath(sharedDataRoot),
            });
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (mode == "create")
                File.Move(temporary, locatorPath);
            else
                File.Replace(temporary, locatorPath, null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    private static string? ReadLocatorRoot(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("sharedDataRoot").GetString();
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string RequireTempRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException("Lease smoke requires --temp-root.");
        string root = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!root.StartsWith(
                temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Lease smoke root must be below TEMP.");
        }

        return root;
    }

    private static string RequireInsideRoot(
        string? candidate,
        string root,
        string description)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException($"Lease smoke requires a {description} path.");
        string path = Path.GetFullPath(candidate);
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Lease smoke {description} path must be below its TEMP root.");
        }

        return path;
    }

    private static void WriteResult(string path, object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }
}
