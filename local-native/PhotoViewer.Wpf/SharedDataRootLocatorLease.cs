using System.IO;

namespace PhotoViewer.Wpf;

/// <summary>
/// Coordinates locator readers and the separately reviewed locator writer.
/// Multiple processes may hold reader leases. A writer must acquire the same
/// fixed-name TEMP lock file with exclusive sharing before creating or replacing
/// the locator. The lease file contains no locator path or user data.
/// </summary>
internal sealed class SharedDataRootLocatorLease : IDisposable
{
    internal const string DefaultLeaseDirectoryName = "aibos-shared-root-locator-leases-v1";
    internal const string LockFileName = "locator.lock";
    private static readonly object SmokeConfigurationGate = new();
    private static string? _leaseDirectoryForSmoke;

    private FileStream? _stream;

    private SharedDataRootLocatorLease(FileStream stream, string lockPath)
    {
        _stream = stream;
        LockPath = lockPath;
    }

    internal string LockPath { get; }

    internal static bool TryAcquireReader(
        string locatorPath,
        out SharedDataRootLocatorLease? lease,
        out string? errorCode,
        out string? error)
        => TryAcquire(
            locatorPath,
            FileAccess.Read,
            FileShare.Read,
            "locator-reader-lease-unavailable",
            out lease,
            out errorCode,
            out error);

    internal static bool TryAcquireWriter(
        string locatorPath,
        out SharedDataRootLocatorLease? lease,
        out string? errorCode,
        out string? error)
        => TryAcquire(
            locatorPath,
            FileAccess.ReadWrite,
            FileShare.None,
            "locator-writer-lease-unavailable",
            out lease,
            out errorCode,
            out error);

    internal static void ConfigureLeaseDirectoryForSmoke(string directory)
    {
        string normalized = RequireDirectoryBelowTemp(directory);
        lock (SmokeConfigurationGate)
            _leaseDirectoryForSmoke = normalized;
    }

    internal static string GetProductionLockPathForSmoke(string locatorPath)
    {
        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(locatorPath, out _))
        {
            throw new InvalidOperationException("Locator path is invalid.");
        }

        string directory = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            DefaultLeaseDirectoryName);
        return BuildLockPath(directory);
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }

    private static bool TryAcquire(
        string locatorPath,
        FileAccess access,
        FileShare share,
        string unavailableErrorCode,
        out SharedDataRootLocatorLease? lease,
        out string? errorCode,
        out string? error)
    {
        lease = null;
        errorCode = null;
        error = null;

        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(locatorPath, out _))
        {
            errorCode = "locator-path-invalid";
            error = "The shared data root locator path must be fully qualified.";
            return false;
        }

        string lockPath;
        try
        {
            string leaseDirectory = ResolveLeaseDirectory();
            if (!TryPrepareCanonicalLeaseDirectory(
                    leaseDirectory,
                    out string canonicalLeaseDirectory))
            {
                errorCode = unavailableErrorCode;
                error = "The shared data root locator lease directory identity is invalid.";
                return false;
            }
            lockPath = BuildLockPath(canonicalLeaseDirectory);
        }
        catch
        {
            errorCode = unavailableErrorCode;
            error = "The shared data root locator lease directory is unavailable.";
            return false;
        }

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                access,
                share,
                bufferSize: 1,
                FileOptions.None);
            if (!WindowsPathIdentity.TryGetFinalPath(
                    stream.SafeFileHandle,
                    out string finalLockPath)
                || !string.Equals(
                    finalLockPath,
                    Path.GetFullPath(lockPath),
                    StringComparison.OrdinalIgnoreCase)
                || stream.Length != 0)
            {
                stream.Dispose();
                errorCode = unavailableErrorCode;
                error = "The shared data root locator lease identity is invalid.";
                return false;
            }
            lease = new SharedDataRootLocatorLease(stream, lockPath);
            return true;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            errorCode = "locator-lease-busy";
            error = "The shared data root locator is being changed by another process.";
            return false;
        }
        catch (IOException)
        {
            errorCode = unavailableErrorCode;
            error = "The shared data root locator lease is unavailable.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = unavailableErrorCode;
            error = "The shared data root locator lease is unavailable.";
            return false;
        }
        catch
        {
            errorCode = unavailableErrorCode;
            error = "The shared data root locator lease could not be acquired.";
            return false;
        }
    }

    private static string ResolveLeaseDirectory()
    {
        lock (SmokeConfigurationGate)
        {
            if (_leaseDirectoryForSmoke is not null)
                return _leaseDirectoryForSmoke;
        }

        string temp = Path.GetFullPath(Path.GetTempPath());
        return Path.Combine(temp, DefaultLeaseDirectoryName);
    }

    private static string BuildLockPath(string leaseDirectory)
        => Path.Combine(leaseDirectory, LockFileName);

    private static bool TryPrepareCanonicalLeaseDirectory(
        string leaseDirectory,
        out string canonicalLeaseDirectory)
    {
        canonicalLeaseDirectory = "";
        try
        {
            string lexicalTemp = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            string lexicalLease = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(leaseDirectory));
            string relative = Path.GetRelativePath(lexicalTemp, lexicalLease);
            if (relative.Length == 0
                || relative == "."
                || Path.IsPathFullyQualified(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    lexicalTemp,
                    out string canonicalTemp))
            {
                return false;
            }

            string existingAncestor = lexicalLease;
            while (!Directory.Exists(existingAncestor))
            {
                string? parent = Directory.GetParent(existingAncestor)?.FullName;
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(
                        parent,
                        existingAncestor,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                existingAncestor = parent;
            }

            string ancestorRelative = Path.GetRelativePath(
                lexicalTemp,
                existingAncestor);
            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    existingAncestor,
                    out string canonicalAncestor)
                || !string.Equals(
                    canonicalAncestor,
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(
                            Path.Combine(canonicalTemp, ancestorRelative))),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Directory.CreateDirectory(lexicalLease);
            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    lexicalLease,
                    out canonicalLeaseDirectory))
            {
                return false;
            }

            string expectedCanonical = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(canonicalTemp, relative)));
            return string.Equals(
                canonicalLeaseDirectory,
                expectedCanonical,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            canonicalLeaseDirectory = "";
            return false;
        }
    }

    private static bool IsSharingViolation(IOException exception)
        => (exception.HResult & 0xFFFF) is 32 or 33;

    private static string RequireDirectoryBelowTemp(string candidate)
    {
        string directory = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!directory.StartsWith(
                temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The locator lease smoke directory must be below TEMP.");
        }

        return directory;
    }
}
