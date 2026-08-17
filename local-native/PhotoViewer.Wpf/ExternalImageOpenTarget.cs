using System.Diagnostics;
using System.IO;

namespace PhotoViewer.Wpf;

/// <summary>
/// A capability for one existing image document that may be handed to the
/// Windows shell's fixed <c>open</c> verb. Arbitrary paths never reach a
/// ProcessStartInfo through this type.
/// </summary>
internal sealed class ExternalImageOpenTarget
{
    private static readonly HashSet<string> AllowedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".gif", ".tif", ".tiff",
    };

    private ExternalImageOpenTarget(string canonicalPath)
    {
        CanonicalPath = canonicalPath;
    }

    internal string CanonicalPath { get; }

    internal static bool TryCreate(
        string? candidate,
        out ExternalImageOpenTarget? target,
        out string reason)
    {
        target = null;
        reason = "selected image could not be verified";
        if (string.IsNullOrWhiteSpace(candidate)
            || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            string lexicalPath = Path.GetFullPath(candidate);
            using var stream = new FileStream(
                lexicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !WindowsPathIdentity.TryGetFinalPath(
                    stream.SafeFileHandle,
                    out string canonicalPath)
                || !Path.IsPathFullyQualified(canonicalPath)
                || !AllowedExtensions.Contains(Path.GetExtension(canonicalPath)))
            {
                return false;
            }

            target = new ExternalImageOpenTarget(canonicalPath);
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        // CanonicalPath is issued only after opening an existing regular file,
        // resolving its final handle identity, and accepting a fixed image
        // document extension. No command or shell arguments are accepted.
        // codeql[cs/command-line-injection]
        return new ProcessStartInfo(CanonicalPath)
        {
            UseShellExecute = true,
            Verb = "open",
        };
    }
}
