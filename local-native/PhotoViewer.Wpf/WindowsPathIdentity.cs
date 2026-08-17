using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PhotoViewer.Wpf;

internal static class WindowsPathIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;
    private const int FileDispositionInfo = 4;

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

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformation(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformation(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation information,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FILETIME CreationTime;
        internal FILETIME LastAccessTime;
        internal FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }

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

    internal static bool TryOpenDirectoryLease(
        string? candidate,
        out SafeFileHandle lease)
    {
        lease = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(
                candidate,
                out string normalized))
        {
            return false;
        }

        SafeFileHandle? handle = null;
        try
        {
            // OPEN_REPARSE_POINT rejects a junction/symlink at the selected
            // directory itself. The caller retains this identity handle and
            // rechecks its final path at every path-based mutation boundary;
            // Windows permits directory renames even without DELETE sharing.
            // codeql[cs/path-injection]
            handle = CreateFile(
                ToExtendedPath(normalized),
                FileReadAttributes | ReadControl,
                FileShareRead | FileShareWrite,
                securityAttributes: 0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                templateFile: 0);
            if (handle.IsInvalid
                || !IsDirectoryLeaseBoundTo(handle, normalized))
            {
                return false;
            }

            lease.Dispose();
            lease = handle;
            handle = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool IsDirectoryLeaseBoundTo(
        SafeFileHandle handle,
        string expectedPath)
    {
        if (handle.IsInvalid || handle.IsClosed)
            return false;
        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(expectedPath));
            if (!TryGetFinalPath(handle, out string finalPath)
                || !string.Equals(
                    normalized,
                    finalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            FileAttributes attributes = File.GetAttributes(handle);
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetHardLinkCount(
        SafeFileHandle handle,
        out uint linkCount)
    {
        linkCount = 0;
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        try
        {
            if (!GetFileInformation(handle, out ByHandleFileInformation information)
                || information.NumberOfLinks == 0)
            {
                return false;
            }

            linkCount = information.NumberOfLinks;
            return true;
        }
        catch
        {
            linkCount = 0;
            return false;
        }
    }

    internal static bool TryDeleteOpenRegularFile(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        try
        {
            FileAttributes attributes = File.GetAttributes(handle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !TryGetHardLinkCount(handle, out uint linkCount)
                || linkCount != 1)
            {
                return false;
            }

            var disposition = new FileDispositionInformation { DeleteFile = true };
            return SetFileInformation(
                handle,
                FileDispositionInfo,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInformation>()));
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryDeleteDirectFileOlderThan(
        string candidate,
        string expectedParent,
        DateTime cutoffUtc)
    {
        if (!SharedDataRootLocator.TryNormalizeAbsolutePath(
                candidate,
                out string normalizedCandidate)
            || !SharedDataRootLocator.TryNormalizeAbsolutePath(
                expectedParent,
                out string normalizedParent)
            || !string.Equals(
                Path.GetDirectoryName(normalizedCandidate),
                normalizedParent,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // The caller supplies a fixed-pattern child of a previously
            // handle-resolved directory. OPEN_REPARSE_POINT plus the exact
            // final-path check keeps deletion bound to that direct file.
            // codeql[cs/path-injection]
            using SafeFileHandle handle = CreateFile(
                ToExtendedPath(normalizedCandidate),
                DeleteAccess | FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                securityAttributes: 0,
                OpenExisting,
                FileFlagOpenReparsePoint,
                templateFile: 0);
            if (handle.IsInvalid
                || !TryGetFinalPath(handle, out string finalPath)
                || !string.Equals(
                    normalizedCandidate,
                    finalPath,
                    StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(handle)
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !TryGetHardLinkCount(handle, out uint linkCount)
                || linkCount != 1
                || File.GetLastWriteTimeUtc(handle) >= cutoffUtc)
            {
                return false;
            }

            var disposition = new FileDispositionInformation { DeleteFile = true };
            return SetFileInformation(
                handle,
                FileDispositionInfo,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInformation>()));
        }
        catch
        {
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
