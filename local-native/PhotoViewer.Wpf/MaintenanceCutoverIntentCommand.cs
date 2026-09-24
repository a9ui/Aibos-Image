using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

// Explicit local preparation/resume only. None of these operations establishes
// closure, requests OS restart, starts a worker or grants maintenance authority.
internal static class MaintenanceCutoverIntentCommand
{
    internal const string Argument = "--maintenance-cutover-intent";

    internal static int Run(Stream input, Stream output, string manifest, Func<string> resolveActualJobsPath)
    {
        string stage = "request";
        try
        {
            using JsonDocument packet = JsonDocument.Parse(ReadPacket(input).GetAwaiter().GetResult(), new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement request = packet.RootElement;
            string action = request.GetProperty("action").GetString() ?? "";
            string[] allowed = action switch
            {
                "bind" => ["action", "desktopGraph", "lifetimeProfile"],
                "prepare" => ["action", "operationId", "controlDirectory", "configuration", "bootAnchor"],
                "resume" or "admit" or "assert-admission" => ["action", "operationId", "controlDirectory", "configuration"],
                _ => throw new IOException("Unsupported cutover action."),
            };
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in request.EnumerateObject())
                if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                    throw new IOException("Unexpected cutover request field.");
            if (names.Count != allowed.Length || manifest.Length != 64 || !manifest.All(char.IsAsciiHexDigitUpper))
                throw new IOException("Incomplete cutover request.");

            stage = "shared-root";
            string jobs = Path.GetFullPath(resolveActualJobsPath());
            string root = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(jobs)!);
            string fileName = Path.GetFileName(jobs);
            if (fileName is not ("jobs.json" or "jobs.sqlite3") || !WindowsPathIdentity.TryOpenDirectoryLease(root, out var lease))
                throw new IOException("Unsupported cutover root.");
            using (lease)
            {
                if (!WindowsPathIdentity.TryGetDirectoryIdentity(lease, root, out string volume, out string file))
                    throw new IOException("Cutover root identity unavailable.");
                stage = "configuration";
                JsonElement configuration;
                if (action == "bind")
                {
                    JsonElement graph = request.GetProperty("desktopGraph");
                    if (graph.GetProperty("profile").GetString() != "aibos.pinned-desktop-graph/v1"
                        || graph.GetProperty("files").GetArrayLength() != 10
                        || graph.GetProperty("task").GetProperty("pinnedLaunchManifestSha256").GetString() != manifest)
                        throw new IOException("Unsupported cutover graph.");
                    configuration = JsonSerializer.SerializeToElement(new
                    {
                        enhancementRoot = root, rootVolumeId = volume, rootFileId = file, jobsFileName = fileName,
                        artifactManifestSha256 = manifest, lifetimeProfile = request.GetProperty("lifetimeProfile").GetString(),
                        managedEntries = new[] { graph.GetProperty("task") }, desktopGraph = graph,
                    });
                }
                else configuration = request.GetProperty("configuration");
                string binding = MaintenanceCutoverIntentStore.ComputeBinding(configuration);
                if (configuration.GetProperty("enhancementRoot").GetString() != root
                    || configuration.GetProperty("rootVolumeId").GetString() != volume
                    || configuration.GetProperty("rootFileId").GetString() != file
                    || configuration.GetProperty("jobsFileName").GetString() != fileName
                    || !string.Equals(configuration.GetProperty("artifactManifestSha256").GetString(), manifest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Actual cutover root or artifacts differ from configuration.");
                if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(lease, root)) throw new IOException("Cutover root moved.");
                if (action == "bind") return Reply(output, new { action, bindingDigest = binding, configuration, enrolled = false, maintenanceAllowed = false });

                stage = "intent";
                string operation = request.GetProperty("operationId").GetString() ?? "";
                string directory = request.GetProperty("controlDirectory").GetString() ?? "";
                byte[] saved = action == "prepare"
                    ? MaintenanceCutoverIntentStore.Create(directory, MaintenanceCutoverIntentStore.Prepare(operation, configuration, request.GetProperty("bootAnchor")))
                    : MaintenanceCutoverIntentStore.ReadForResume(directory, operation, configuration);
                using JsonDocument original = JsonDocument.Parse(saved);
                if (action is "admit" or "assert-admission")
                {
                    stage = "admission";
                    JsonElement admission = MaintenanceCutoverAdmission.Access(directory, saved, action == "admit");
                    return Reply(output, new { action, admission, enrolled = false, maintenanceAllowed = false });
                }
                return Reply(output, new { action, intent = original.RootElement, enrolled = false, maintenanceAllowed = false });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or JsonException or KeyNotFoundException or OperationCanceledException or System.ComponentModel.Win32Exception
            or FormatException or OverflowException or NotSupportedException or System.Security.SecurityException)
        { return Reject(output, stage); }
    }

    internal static int Reject(Stream output, string stage = "startup")
    {
        try { Reply(output, new { action = "rejected", reason = "cutover-intent-unavailable", stage, enrolled = false, maintenanceAllowed = false }); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return 2;
    }

    private static int Reply(Stream output, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        output.Write(bytes);
        output.Flush();
        return 0;
    }

    private static async Task<byte[]> ReadPacket(Stream input)
    {
        byte[] bytes = new byte[MaintenanceCutoverIntentStore.MaximumBytes + 1];
        int length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (length < bytes.Length)
        {
            int count = await input.ReadAsync(bytes.AsMemory(length), timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
            if (count == 0) return bytes[..length];
            length += count;
        }
        throw new IOException("Cutover request exceeds its byte bound.");
    }
}
