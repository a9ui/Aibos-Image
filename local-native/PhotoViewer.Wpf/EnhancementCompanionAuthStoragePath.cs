using System.IO;
using Microsoft.Win32.SafeHandles;

namespace PhotoViewer.Wpf;

/// <summary>
/// A capability for the current-user companion authentication store. Product
/// code can select only the Windows LocalApplicationData root; every child and
/// the DPAPI file leaf are fixed by the public companion-auth contract.
/// Synthetic callers are restricted to an existing root below OS TEMP.
/// </summary>
internal sealed class EnhancementCompanionAuthStoragePath
{
    private const string ApplicationDirectoryName = "PhotoViewer.Wpf";
    private const string AuthDirectoryName = "companion-auth-v1";
    private const string AuthFileName = "companion-auth-v1.key";

    private EnhancementCompanionAuthStoragePath(
        string rootPath,
        string applicationDirectoryPath,
        string directoryPath,
        string filePath)
    {
        RootPath = rootPath;
        ApplicationDirectoryPath = applicationDirectoryPath;
        DirectoryPath = directoryPath;
        FilePath = filePath;
    }

    internal string RootPath { get; }
    internal string ApplicationDirectoryPath { get; }
    internal string DirectoryPath { get; }
    internal string FilePath { get; }

    internal static bool TryForCurrentUser(
        out EnhancementCompanionAuthStoragePath? storage)
    {
        storage = null;
        try
        {
            return TryCreateFixedTree(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                out storage);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            storage = null;
            return false;
        }
    }

    internal static bool RejectsUnavailableProductRootsForSmoke()
        => !TryCreateFixedTree("", out _)
            && !TryCreateFixedTree("relative-auth-root", out _);

    internal static EnhancementCompanionAuthStoragePath ForManagedTempFixtureRoot(
        string fixtureRoot)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(fixtureRoot));
        if (!Path.IsPathFullyQualified(fixtureRoot)
            || !IsStrictExistingTempDescendant(fullRoot))
        {
            throw new InvalidDataException(
                "Synthetic companion authentication roots must exist below TEMP.");
        }

        return CreateFixedTree(fullRoot);
    }

    internal static bool AcceptsManagedTempFixtureRootForSmoke(string fixtureRoot)
    {
        try
        {
            _ = ForManagedTempFixtureRoot(fixtureRoot);
            return true;
        }
        catch (Exception ex) when (ex is
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static EnhancementCompanionAuthStoragePath CreateFixedTree(
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)
            || !Path.IsPathFullyQualified(rootPath))
        {
            throw new InvalidDataException(
                "The companion authentication root was not an absolute OS path.");
        }
        string fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath));

        string applicationDirectory = FixedChild(
            fullRoot,
            ApplicationDirectoryName);
        string authDirectory = FixedChild(
            applicationDirectory,
            AuthDirectoryName);
        string authFile = FixedChild(authDirectory, AuthFileName);
        return new EnhancementCompanionAuthStoragePath(
            fullRoot,
            applicationDirectory,
            authDirectory,
            authFile);
    }

    private static bool TryCreateFixedTree(
        string rootPath,
        out EnhancementCompanionAuthStoragePath? storage)
    {
        storage = null;
        try
        {
            storage = CreateFixedTree(rootPath);
            return true;
        }
        catch (Exception ex) when (ex is
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            storage = null;
            return false;
        }
    }

    private static string FixedChild(string directory, string leafName)
    {
        string fullDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        string candidate = Path.GetFullPath(Path.Combine(fullDirectory, leafName));
        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                fullDirectory,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(candidate),
                leafName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The companion authentication path escaped its fixed directory.");
        }
        return candidate;
    }

    private static bool IsStrictExistingTempDescendant(string candidate)
    {
        string tempRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        string relative = Path.GetRelativePath(tempRoot, candidate);
        bool lexicalInside = !Path.IsPathFullyQualified(relative)
            && relative is not ("." or "..")
            && !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal);
        if (!lexicalInside)
            return false;

        // This is the synthetic capability boundary. Both roots must already
        // exist so final handle identities, rather than lexical strings, bind
        // the fixture below TEMP.
        // codeql[cs/path-injection]
        if (!Directory.Exists(candidate)
            || !WindowsPathIdentity.TryResolveExistingDirectory(
                tempRoot,
                out string canonicalTempRoot)
            || !WindowsPathIdentity.TryResolveExistingDirectory(
                candidate,
                out string canonicalCandidate))
        {
            return false;
        }

        string canonicalPrefix = Path.TrimEndingDirectorySeparator(
                canonicalTempRoot)
            + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(
            canonicalPrefix,
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class EnhancementCompanionAuthDirectoryLease : IDisposable
{
    internal EnhancementCompanionAuthDirectoryLease(
        EnhancementCompanionAuthStoragePath storage,
        SafeFileHandle root,
        SafeFileHandle applicationDirectory,
        SafeFileHandle authDirectory)
    {
        Storage = storage;
        Root = root;
        ApplicationDirectory = applicationDirectory;
        AuthDirectory = authDirectory;
    }

    internal EnhancementCompanionAuthStoragePath Storage { get; }
    private SafeFileHandle Root { get; }
    private SafeFileHandle ApplicationDirectory { get; }
    private SafeFileHandle AuthDirectory { get; }

    internal bool IsStillBound()
        => WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                Root,
                Storage.RootPath)
            && WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                ApplicationDirectory,
                Storage.ApplicationDirectoryPath)
            && WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                AuthDirectory,
                Storage.DirectoryPath);

    public void Dispose()
    {
        AuthDirectory.Dispose();
        ApplicationDirectory.Dispose();
        Root.Dispose();
    }
}
