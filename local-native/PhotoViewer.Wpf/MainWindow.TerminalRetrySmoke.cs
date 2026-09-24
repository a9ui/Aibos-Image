using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    internal static bool VerifyTerminalRetryReceiptsForSmoke()
    {
        using Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream(
            "TerminalRetryReceiptFixtures.json")
            ?? throw new InvalidOperationException("Terminal retry fixture is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement vector in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            bool valid = TryParseTerminalHistoryBatchRetryResponse(
                vector.GetProperty("response"), "synthetic-terminal-retry", ["source-job"],
                out _, out _, out _, out _, out _, out int retained);
            if (valid != vector.GetProperty("valid").GetBoolean()
                || retained != vector.GetProperty("retainedAcceptedCount").GetInt32())
                throw new InvalidOperationException(
                    $"Terminal retry receipt failed: {vector.GetProperty("id").GetString()}");
        }
        return true;
    }
}
