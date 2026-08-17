using System.IO;
using System.Security.Cryptography;

namespace PhotoViewer.Wpf;

internal static class ExternalImageOpenTargetSmokeRunner
{
    internal static int Run()
    {
        string root = Directory.CreateTempSubdirectory(
            "aibos-external-image-open-target-").FullName;
        try
        {
            string validPath = Path.Combine(
                root,
                "日本語 (final)! image&100%^complete.png");
            string commandPath = Path.Combine(root, "must-not-run.cmd");
            string imageNamedDirectory = Path.Combine(root, "not-a-file.png");
            string missingImage = Path.Combine(root, "missing.png");
            File.WriteAllBytes(
                validPath,
                [137, 80, 78, 71, 13, 10, 26, 10]);
            File.WriteAllText(commandPath, "@echo off\r\nexit /b 0\r\n");
            Directory.CreateDirectory(imageNamedDirectory);
            string fingerprintBefore = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(validPath)));

            bool validAccepted = ExternalImageOpenTarget.TryCreate(
                    validPath,
                    out ExternalImageOpenTarget? target,
                    out string reason)
                && target is not null
                && string.Equals(
                    target.CanonicalPath,
                    Path.GetFullPath(validPath),
                    StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(reason);
            bool exactShellDocument = validAccepted
                && target is not null
                && target.CreateStartInfo() is { } startInfo
                && string.Equals(
                    startInfo.FileName,
                    target.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase)
                && startInfo.UseShellExecute
                && string.Equals(startInfo.Verb, "open", StringComparison.Ordinal)
                && startInfo.ArgumentList.Count == 0
                && string.IsNullOrEmpty(startInfo.Arguments);
            bool unsafeRejected =
                !ExternalImageOpenTarget.TryCreate(commandPath, out _, out _)
                && !ExternalImageOpenTarget.TryCreate(
                    imageNamedDirectory,
                    out _,
                    out _)
                && !ExternalImageOpenTarget.TryCreate(missingImage, out _, out _)
                && !ExternalImageOpenTarget.TryCreate("relative.png", out _, out _)
                && !ExternalImageOpenTarget.TryCreate("https://example.invalid/image.png", out _, out _);
            bool sourceUnchanged = string.Equals(
                fingerprintBefore,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(validPath))),
                StringComparison.Ordinal);
            return validAccepted
                && exactShellDocument
                && unsafeRejected
                && sourceUnchanged
                    ? 0
                    : 1;
        }
        catch
        {
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
