using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class EnhancementCompanionLifetimeSmokeRunner
{
    internal static int Run(string resultPath)
    {
        string? fullPath = null;
        object result;
        int exitCode;
        try
        {
            fullPath = Path.GetFullPath(resultPath);
            string tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(
                    tempRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Companion lifetime smoke output must stay below TEMP.");
            }

            EnhancementCompanionCloseLifecycleSmokeSnapshot snapshot =
                MainWindow.EnhancementCompanionCloseLifecycleForSmoke();
            result = snapshot;
            exitCode = snapshot.AllPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            result = new
            {
                AllPassed = false,
                Message = ex.GetType().Name,
            };
            exitCode = 1;
        }

        try
        {
            if (fullPath is null)
                return 1;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(
                fullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            return 1;
        }
        return exitCode;
    }
}
