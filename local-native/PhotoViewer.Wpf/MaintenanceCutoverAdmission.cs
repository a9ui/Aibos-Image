using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoViewer.Wpf;

// Owns only cutover admission records. The opaque existing maintenance marker
// blocks normal cooperating intake; it never authorizes repair or removes gates.
internal static class MaintenanceCutoverAdmission
{
    private const string MarkerSchema = "aibos.initial-cutover-admission/v1";
    private const string ReceiptSchema = "aibos.initial-cutover-admission-receipt/v1";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static JsonElement Access(string controlDirectory, byte[] savedIntent, bool publish)
    {
        using JsonDocument original = MaintenanceCutoverIntentStore.Parse(savedIntent);
        JsonElement intent = original.RootElement, configuration = intent.GetProperty("configuration");
        string root = configuration.GetProperty("enhancementRoot").GetString()!;
        string jobs = Path.Combine(root, configuration.GetProperty("jobsFileName").GetString()!);
        string operation = intent.GetProperty("operationId").GetString()!;
        string binding = intent.GetProperty("bindingDigest").GetString()!;
        string intentHash = Hash(savedIntent);
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, schemaId = MarkerSchema, operationId = operation, bindingDigest = binding, intentSha256 = intentHash,
        });
        return MaintenanceCutoverIntentStore.WithControlWriter(controlDirectory, configuration, directory =>
        {
            using IDisposable jobsWriter = EnhancementEnqueueInboxStore.AcquireJobsWriteLease(jobs);
            string inbox = Path.Combine(root, "enqueue-inbox");
            string receiptPath = Path.Combine(directory, "admission.json");
            byte[]? receipt = ReadOptional(receiptPath);
            if (publish && receipt is null) Directory.CreateDirectory(inbox);
            if (!WindowsPathIdentity.TryOpenDirectoryLease(inbox, out var inboxLease)) throw new IOException("Cutover inbox is unavailable.");
            using (inboxLease)
            {
                string markerPath = Path.Combine(inbox, EnhancementEnqueueInboxStore.MaintenanceMarkerFileName);
                byte[]? marker = ReadOptional(markerPath);
                if (marker is null)
                {
                    // Once completion was recorded, absence breaks continuity.
                    // An Arm retry must never silently rebuild a lost gate.
                    if (!publish || receipt is not null) throw new IOException("Cutover admission continuity is unavailable.");
                    MaintenanceCutoverIntentStore.PublishNewPlainFile(markerPath, expected);
                    marker = MaintenanceCutoverIntentStore.ReadBoundPlainFile(markerPath);
                }
                using JsonDocument markerDocument = MaintenanceCutoverIntentStore.Parse(marker);
                Require(markerDocument.RootElement, MarkerSchema, operation, binding, intentHash, "intentSha256");
                string markerHash = Hash(marker);
                if (receipt is null)
                {
                    if (!publish) throw new IOException("Cutover admission publication was not completed.");
                    // Marker-first publication: a crash before this receipt can
                    // resume by verifying the same marker, without replacing it.
                    receipt = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1, schemaId = ReceiptSchema, operationId = operation, bindingDigest = binding,
                        markerSha256 = markerHash, committedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    });
                    MaintenanceCutoverIntentStore.PublishNewPlainFile(receiptPath, receipt);
                    receipt = MaintenanceCutoverIntentStore.ReadBoundPlainFile(receiptPath);
                }
                using JsonDocument document = MaintenanceCutoverIntentStore.Parse(receipt);
                Require(document.RootElement, ReceiptSchema, operation, binding, markerHash, "markerSha256");
                if (!DateTime.TryParseExact(document.RootElement.GetProperty("committedAtUtc").GetString(), "O",
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime committed) || committed.Kind != DateTimeKind.Utc)
                    throw new IOException("Invalid cutover admission time.");
                if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(inboxLease, inbox)
                    || !MaintenanceCutoverIntentStore.ReadBoundPlainFile(markerPath).AsSpan().SequenceEqual(marker))
                    throw new IOException("Cutover admission changed during access.");
                return document.RootElement.Clone();
            }
        });
    }

    private static byte[]? ReadOptional(string path)
    {
        try { return MaintenanceCutoverIntentStore.ReadBoundPlainFile(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static void Require(JsonElement value, string schema, string operation, string binding, string hash, string hashProperty)
    {
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("schemaId").GetString() != schema
            || value.GetProperty("operationId").GetString() != operation || value.GetProperty("bindingDigest").GetString() != binding
            || value.GetProperty(hashProperty).GetString() != hash)
            throw new IOException("Cutover admission belongs to another or unsupported operation.");
        // Compatible additive fields remain in the original marker/receipt bytes.
    }
}
