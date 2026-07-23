using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoViewer.Wpf;

internal static class WindowsPathIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    internal static bool TryGetFinalPath(
        SafeFileHandle handle,
        out string finalPath)
    {
        finalPath = "";
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        try
        {
            var buffer = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                FileNameNormalized);
            if (length == 0)
                return false;

            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    checked((uint)buffer.Capacity),
                    FileNameNormalized);
                if (length == 0 || length >= buffer.Capacity)
                    return false;
            }

            finalPath = NormalizeWin32FinalPath(buffer.ToString());
            return Path.IsPathFullyQualified(finalPath);
        }
        catch
        {
            finalPath = "";
            return false;
        }
    }

    internal static bool TryResolveExistingDirectory(
        string? candidate,
        out string finalPath)
    {
        finalPath = "";
        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(
                candidate,
                out string normalized))
        {
            return false;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(normalized);
            if (!attributes.HasFlag(FileAttributes.Directory))
                return false;

            using SafeFileHandle handle = CreateFile(
                ToExtendedPath(normalized),
                desiredAccess: 0,
                FileShareRead | FileShareWrite | FileShareDelete,
                securityAttributes: 0,
                OpenExisting,
                FileFlagBackupSemantics,
                templateFile: 0);
            if (handle.IsInvalid)
                return false;
            return TryGetFinalPath(handle, out finalPath);
        }
        catch
        {
            finalPath = "";
            return false;
        }
    }

    private static string NormalizeWin32FinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        string normalized = path.StartsWith(
                uncPrefix,
                StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[uncPrefix.Length..]
            : path.StartsWith(
                    devicePrefix,
                    StringComparison.OrdinalIgnoreCase)
                ? path[devicePrefix.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }
}
