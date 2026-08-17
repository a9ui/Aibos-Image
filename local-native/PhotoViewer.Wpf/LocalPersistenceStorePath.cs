using System.IO;
using System.Text;

namespace PhotoViewer.Wpf;

internal enum LocalPersistenceStoreKind
{
    AiStyles,
    FavoriteActivity,
    FolderSetFavorites,
}

/// <summary>
/// A capability for one application-owned local persistence file. Callers can
/// select the state directory, but never the leaf name of a split store.
/// Synthetic fixtures additionally have to stay below the OS TEMP directory.
/// </summary>
internal sealed class LocalPersistenceStorePath
{
    private LocalPersistenceStorePath(string fullPath, LocalPersistenceStoreKind kind)
    {
        FullPath = fullPath;
        Kind = kind;
    }

    public string FullPath { get; }
    public LocalPersistenceStoreKind Kind { get; }

    public static LocalPersistenceStorePath ForStateSibling(
        string statePath,
        LocalPersistenceStoreKind kind)
    {
        string fullStatePath = Path.GetFullPath(statePath);
        string defaultStatePath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoViewer.Wpf",
            "state.json"));
        if (!string.Equals(
                fullStatePath,
                defaultStatePath,
                StringComparison.OrdinalIgnoreCase)
            && !IsStrictTempDescendant(fullStatePath))
        {
            throw new InvalidDataException(
                "A Viewer state override may isolate split stores only below TEMP.");
        }
        string directory = Path.GetDirectoryName(fullStatePath)
            ?? throw new InvalidDataException(
                "The Viewer state path did not have a persistence directory.");
        return CreateFixedChild(directory, kind);
    }

    public static LocalPersistenceStorePath ForManagedTempFixture(
        string path,
        LocalPersistenceStoreKind kind)
    {
        string fullPath = Path.GetFullPath(path);
        if (!IsStrictTempDescendant(fullPath))
        {
            throw new InvalidDataException(
                "Synthetic local persistence fixtures must stay below TEMP.");
        }

        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                "The synthetic persistence path did not have a directory.");
        LocalPersistenceStorePath expected = CreateFixedChild(directory, kind);
        if (!string.Equals(
                expected.FullPath,
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The synthetic persistence file name was not application-owned.");
        }
        return expected;
    }

    private static bool IsStrictTempDescendant(string path)
    {
        string tempRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(tempRoot, fullPath);
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

        string? directory = Path.GetDirectoryName(fullPath);
        // This is the validation boundary itself: both directories must exist
        // before their final handle identities can be compared.
        // codeql[cs/path-injection]
        if (string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory)
            || !WindowsPathIdentity.TryResolveExistingDirectory(
                tempRoot,
                out string canonicalTempRoot)
            || !WindowsPathIdentity.TryResolveExistingDirectory(
                directory,
                out string canonicalDirectory))
        {
            return false;
        }

        string canonicalPrefix = Path.TrimEndingDirectorySeparator(
                canonicalTempRoot)
            + Path.DirectorySeparatorChar;
        return canonicalDirectory.StartsWith(
            canonicalPrefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static LocalPersistenceStorePath CreateFixedChild(
        string directory,
        LocalPersistenceStoreKind kind)
    {
        string fullDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        string fileName = kind switch
        {
            LocalPersistenceStoreKind.AiStyles => "ai-styles.json",
            LocalPersistenceStoreKind.FavoriteActivity =>
                "favorite-activity.sqlite3",
            LocalPersistenceStoreKind.FolderSetFavorites =>
                "folder-set-favorites.json",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        string candidate = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                fullDirectory,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(candidate),
                fileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A split local persistence path escaped its state directory.");
        }
        return new LocalPersistenceStorePath(candidate, kind);
    }
}

internal static class LocalPersistenceStoreFile
{
    public static bool TryWriteAtomicText(
        LocalPersistenceStorePath path,
        string text)
    {
        string fullPath = path.FullPath;
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                "The local persistence path did not have a directory.");
        string? tempPath = null;
        try
        {
            // `path` is a typed capability whose leaf is selected from the
            // application-owned LocalPersistenceStoreKind allowlist.
            // codeql[cs/path-injection]
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            // The generated temporary name stays beside that fixed store and
            // is atomically moved only onto the same capability target.
            // codeql[cs/path-injection]
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
            }
            // Both names are fixed children of the same capability directory;
            // overwrite replaces only the application-owned store leaf.
            // codeql[cs/path-injection]
            File.Move(tempPath, fullPath, overwrite: true);
            tempPath = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                // tempPath is the generated sibling described above.
                // codeql[cs/path-injection]
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
