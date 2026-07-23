using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoViewer.Wpf;

namespace Aibos.SharedRootSetup;

internal static class Program
{
    private const string ConfirmationToken = "CREATE";

    private static int Main(string[] args)
    {
        string? smokeResultPath = Value(args, "--smoke");
        if (!string.IsNullOrWhiteSpace(smokeResultPath))
            return SharedRootSetupSmoke.Run(smokeResultPath);

        string? root = Value(args, "--root");
        bool apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        string? confirmation = Value(args, "--confirm");

        if (string.IsNullOrWhiteSpace(root))
        {
            WriteUsage();
            return 2;
        }

        if (apply
            && !string.Equals(
                confirmation,
                ConfirmationToken,
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Apply requires the explicit confirmation: --confirm {ConfirmationToken}");
            return 2;
        }

        SharedDataRootSetupResult result;
        try
        {
            result = apply
                ? SharedDataRootConfigurator.ApplyDefault(root)
                : SharedDataRootConfigurator.InspectDefault(root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"shared-root-setup-unavailable: {ex.Message}");
            return 1;
        }
        if (json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            Console.WriteLine(JsonSerializer.Serialize(result, options));
        }
        else
        {
            Console.WriteLine($"Status: {result.Status}");
            Console.WriteLine($"Locator: {result.LocatorPath}");
            Console.WriteLine($"Shared data root: {result.SharedDataRoot ?? "(unavailable)"}");
            Console.WriteLine($"Locator changed: {result.Changed}");
            Console.WriteLine(
                $"Present stores: {result.Stores.Count(static store => store.Exists)}"
                    + $"/{result.Stores.Count}");
            if (result.Outputs is not null)
            {
                Console.WriteLine(
                    $"Managed outputs: {result.Outputs.FileCount:N0} file(s), "
                        + $"{result.Outputs.TotalBytes:N0} byte(s)");
            }
            if (!result.Ok)
                Console.Error.WriteLine($"{result.ErrorCode}: {result.Error}");
            else if (!apply && result.Status == SharedDataRootSetupStatus.Ready)
                Console.WriteLine(
                    $"Inspection passed. No files changed. Re-run with "
                        + $"--apply --confirm {ConfirmationToken} to create the locator.");
        }

        return result.Ok ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: Aibos.SharedRootSetup --root <existing-shared-data-root> [--json]");
        Console.Error.WriteLine(
            $"Apply: Aibos.SharedRootSetup --root <existing-shared-data-root> "
                + $"--apply --confirm {ConfirmationToken} [--json]");
    }
}
