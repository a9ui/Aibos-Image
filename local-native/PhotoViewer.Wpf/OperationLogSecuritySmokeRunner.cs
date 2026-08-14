using System.IO;

namespace PhotoViewer.Wpf;

internal static class OperationLogSecuritySmokeRunner
{
    internal const string MarkerLine =
        "{\"operation\":\"security_smoke\",\"outcome\":\"accepted\"}";

    internal static int Run(string? localAppDataRoot, string? expectation)
    {
        try
        {
            if (!TryValidateFixtureRoot(localAppDataRoot, out string fixtureRoot)
                || expectation is not ("accept" or "reject"))
            {
                return 2;
            }

            bool wrote = AibosOperationLog.TryWriteBatchForSecuritySmoke(
                fixtureRoot,
                DateTime.UtcNow,
                MarkerLine);
            return wrote == string.Equals(
                expectation,
                "accept",
                StringComparison.Ordinal)
                    ? 0
                    : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static bool TryValidateFixtureRoot(
        string? candidate,
        out string fixtureRoot)
    {
        fixtureRoot = "";
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            string expectedTemp = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            string expectedFixture = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate));
            if (!WindowsPathIdentity.TryResolveExistingDirectory(
                    expectedTemp,
                    out string canonicalTemp)
                || !WindowsPathIdentity.TryResolveExistingDirectory(
                    expectedFixture,
                    out string canonicalFixture)
                || !string.Equals(
                    expectedTemp,
                    canonicalTemp,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    expectedFixture,
                    canonicalFixture,
                    StringComparison.OrdinalIgnoreCase)
                || !canonicalFixture.StartsWith(
                    canonicalTemp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(canonicalFixture) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            fixtureRoot = canonicalFixture;
            return true;
        }
        catch
        {
            fixtureRoot = "";
            return false;
        }
    }
}
