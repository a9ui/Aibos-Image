using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace PhotoViewer.Wpf;

/// <summary>Passive launch handoff only. No enrollment authority is issued here.</summary>
internal static class MaintenanceEnrollmentHandoff
{
    internal const string ProbeArgument = "--maintenance-enrollment-probe";
    internal const string ChildArgument = "--maintenance-enrollment-handoff";
    private const string Protocol = "aibos.maintenance-enrollment-handoff/v1";
    private const string GenerationVariable = "AIBOS_ENROLLMENT_LAUNCH_GENERATION";
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly string[] ArtifactNames = [
        "Microsoft.Data.Sqlite.dll", "PhotoViewer.Wpf.deps.json", "PhotoViewer.Wpf.dll",
        "PhotoViewer.Wpf.exe", "PhotoViewer.Wpf.runtimeconfig.json", "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.winsqlite3.dll",
    ];

    private sealed record RootIdentity(string Path, string VolumeId, string FileId);
    private sealed record Request(string Protocol, string AttemptId, string Nonce, string LaunchGeneration,
        string ManifestSha256, string AssemblyPath, RootIdentity Root, string JobsPath);
    private sealed record Reply(string Protocol, string AttemptId, string Nonce, string LaunchGeneration,
        string Handoff, string Enrollment, bool Enrolled, bool MaintenanceAllowed, string Reason,
        int ProcessId, long ProcessStartUtcTicks, string? ManifestSha256 = null,
        string? AssemblyPath = null, RootIdentity? Root = null, string? JobsPath = null, string? InboxPath = null);

    private sealed class ArtifactLease : IDisposable
    {
        private readonly List<FileStream> _streams = [];
        internal string AssemblyPath { get; } = System.IO.Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        internal string Digest { get; }
        internal ArtifactLease()
        {
            try
            {
                if (!string.Equals(System.IO.Path.GetFileName(AssemblyPath), "PhotoViewer.Wpf.dll", StringComparison.Ordinal))
                    throw new IOException("unsupported-artifact");
                string directory = System.IO.Path.GetDirectoryName(AssemblyPath)!;
                var lines = new List<string>();
                foreach (string name in ArtifactNames.Order(StringComparer.Ordinal))
                {
                    string file = System.IO.Path.Combine(directory, name);
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("redirected-artifact");
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _streams.Add(stream);
                    if (stream.Length < 1 || stream.Length > 64L * 1024 * 1024
                        || !WindowsPathIdentity.TryGetFinalPath(stream.SafeFileHandle, out string actual)
                        || !string.Equals(actual, file, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("invalid-artifact");
                    lines.Add($"{name}|{Convert.ToHexString(SHA256.HashData(stream))}");
                }
                Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (FileStream stream in _streams) stream.Dispose(); _streams.Clear(); }
    }

    // The same exact artifact set is used for a fixed cold launch. Holding the
    // lease prevents cooperating updates of those files until the viewer exits;
    // this alone does not establish cutover, root enrollment or external lifetime.
    internal static IDisposable PinLaunchArtifacts(string expectedManifestSha256)
    {
        if (!IsDigest(expectedManifestSha256)) throw new IOException("invalid-launch-manifest");
        var artifacts = new ArtifactLease();
        if (artifacts.Digest == expectedManifestSha256) return artifacts;
        artifacts.Dispose();
        throw new IOException("launch-artifact-mismatch");
    }

    private static string NormalizeRoot(string value)
    {
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
            throw new IOException("unsupported-root");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static RootIdentity ObserveRoot(SafeFileHandle handle, string root)
    {
        if (!WindowsPathIdentity.TryGetDirectoryIdentity(handle, root, out string volume, out string file))
            throw new IOException("root-identity-unavailable");
        return new(root, volume, file);
    }

    private static bool SameRoot(RootIdentity left, RootIdentity right)
        => string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase)
            && left.VolumeId == right.VolumeId && left.FileId == right.FileId;

    private static bool IsUuid(string? value)
        => Guid.TryParseExact(value, "D", out Guid id) && id.ToString("D") == value;

    private static bool IsDigest(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new IOException("invalid-packet");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new IOException("duplicate-packet-field");
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateObject(property.Value);
        }
    }

    private static async Task<byte[]> ReadPacket(Stream stream)
    {
        byte[] bytes = new byte[16 * 1024 + 1];
        int length = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (length < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token).AsTask()
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            if (count == 0) return bytes[..length];
            length += count;
        }
        throw new IOException("handoff-packet-too-large");
    }

    private static Request ReadRequest(Stream input)
    {
        byte[] bytes = ReadPacket(input).GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        ValidateObject(document.RootElement);
        Request request = JsonSerializer.Deserialize<Request>(bytes, Json) ?? throw new IOException("invalid-request");
        if (request.Protocol != Protocol || !IsUuid(request.AttemptId) || !IsUuid(request.LaunchGeneration)
            || !IsDigest(request.Nonce) || !IsDigest(request.ManifestSha256)
            || request.Root is null || string.IsNullOrWhiteSpace(request.Root.Path)
            || request.Root.VolumeId is not { Length: 8 } || request.Root.FileId is not { Length: 16 }
            || string.IsNullOrWhiteSpace(request.AssemblyPath)
            || string.IsNullOrWhiteSpace(request.JobsPath)) throw new IOException("invalid-request");
        return request;
    }

    private static Reply Rejected(Request? request, string reason)
    {
        using Process process = Process.GetCurrentProcess();
        return new(Protocol, request?.AttemptId ?? "", request?.Nonce ?? "", request?.LaunchGeneration ?? "",
            "rejected", "evidence-unavailable", false, false, reason, Environment.ProcessId,
            process.StartTime.ToUniversalTime().Ticks);
    }

    private static int WriteReply(Stream output, Reply reply)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Json);
        try
        {
            output.Write(bytes);
            output.Flush();
            return reply.Handoff == "verified" ? 10 : 2;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A disconnected caller cannot receive a fallback reply either.
            return 2;
        }
    }

    internal static int RejectArguments(Stream output)
        => WriteReply(output, Rejected(null, "invalid-arguments"));

    internal static int RunChild(Stream input, Stream output,
        Func<SingleInstanceCoordinator> acquireInstance, Func<string> resolveActualJobsPath)
    {
        Request? request = null;
        try
        {
            request = ReadRequest(input);
            using SingleInstanceCoordinator instance = acquireInstance();
            if (!instance.IsPrimary) return WriteReply(output, Rejected(request, "existing-primary"));
            string? generation = Environment.GetEnvironmentVariable(GenerationVariable);
            if (generation != request.LaunchGeneration) return WriteReply(output, Rejected(request, "generation-mismatch"));
            string jobs = Path.GetFullPath(resolveActualJobsPath());
            string root = NormalizeRoot(Path.GetDirectoryName(jobs)!);
            if (!string.Equals(jobs, request.JobsPath, StringComparison.OrdinalIgnoreCase)
                || !WindowsPathIdentity.TryOpenDirectoryLease(root, out SafeFileHandle rootLease))
                return WriteReply(output, Rejected(request, "root-binding-mismatch"));
            using (rootLease)
            using (var artifacts = new ArtifactLease())
            {
                RootIdentity observed = ObserveRoot(rootLease, root);
                if (!SameRoot(observed, request.Root) || artifacts.Digest != request.ManifestSha256
                    || !string.Equals(artifacts.AssemblyPath, request.AssemblyPath, StringComparison.OrdinalIgnoreCase))
                    return WriteReply(output, Rejected(request, "configuration-mismatch"));
                using Process process = Process.GetCurrentProcess();
                return WriteReply(output, new(Protocol, request.AttemptId, request.Nonce, generation,
                    "verified", "evidence-unavailable", false, false, "independent-cutover-and-lifetime-evidence-required",
                    Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, artifacts.Digest,
                    artifacts.AssemblyPath, ObserveRoot(rootLease, root), jobs, Path.Combine(root, "enqueue-inbox")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return WriteReply(output, Rejected(request, "handoff-unavailable")); }
    }

    internal static int RunProbe(string expectedRoot, string jobsFileName, string expectedManifestSha256, Stream output)
    {
        Request? request = null;
        try
        {
            string root = NormalizeRoot(expectedRoot);
            if (jobsFileName is not ("jobs.json" or "jobs.sqlite3")
                || !WindowsPathIdentity.TryOpenDirectoryLease(root, out SafeFileHandle rootLease))
                return WriteReply(output, Rejected(null, "root-binding-unavailable"));
            using (rootLease)
            using (var artifacts = new ArtifactLease())
            using (var child = new Process())
            {
                if (!IsDigest(expectedManifestSha256) || artifacts.Digest != expectedManifestSha256)
                    return WriteReply(output, Rejected(null, "launch-artifact-mismatch"));
                request = new(Protocol, Guid.NewGuid().ToString("D"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Guid.NewGuid().ToString("D"), artifacts.Digest, artifacts.AssemblyPath,
                    ObserveRoot(rootLease, root), Path.Combine(root, jobsFileName));
                string host = Environment.ProcessPath ?? throw new IOException("host-unavailable");
                child.StartInfo = new(host) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
                    child.StartInfo.ArgumentList.Add(artifacts.AssemblyPath);
                child.StartInfo.ArgumentList.Add(ChildArgument);
                child.StartInfo.Environment[GenerationVariable] = request.LaunchGeneration;
                child.StartInfo.Environment["AIBOS_COMPANION_START_ON_LAUNCH"] = "0";
                child.Start();
                try
                {
                    long started = child.StartTime.ToUniversalTime().Ticks;
                    Task<byte[]> response = ReadPacket(child.StandardOutput.BaseStream);
                    Task<byte[]> errors = ReadPacket(child.StandardError.BaseStream);
                    child.StandardInput.BaseStream.Write(JsonSerializer.SerializeToUtf8Bytes(request, Json));
                    child.StandardInput.Close();
                    if (!child.WaitForExit(15000)) throw new IOException("handoff-timeout");
                    if (errors.GetAwaiter().GetResult().Length != 0) throw new IOException("handoff-stderr");
                    byte[] responseBytes = response.GetAwaiter().GetResult();
                    using JsonDocument responseDocument = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 4 });
                    ValidateObject(responseDocument.RootElement);
                    Reply reply = JsonSerializer.Deserialize<Reply>(responseBytes, Json)
                        ?? throw new IOException("invalid-reply");
                    if (reply.Protocol != Protocol || reply.AttemptId != request.AttemptId || reply.Nonce != request.Nonce
                        || reply.LaunchGeneration != request.LaunchGeneration || reply.ProcessId != child.Id
                        || reply.ProcessStartUtcTicks != started || reply.Enrolled || reply.MaintenanceAllowed
                        || reply.Enrollment != "evidence-unavailable") throw new IOException("reply-binding-mismatch");
                    if (reply.Handoff == "verified" && (child.ExitCode != 10 || reply.Root is null
                        || !SameRoot(reply.Root, ObserveRoot(rootLease, root)) || reply.ManifestSha256 != artifacts.Digest
                        || !string.Equals(reply.AssemblyPath, artifacts.AssemblyPath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(reply.JobsPath, request.JobsPath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(reply.InboxPath, Path.Combine(root, "enqueue-inbox"), StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("reply-configuration-mismatch");
                    if (reply.Handoff != "verified" && (reply.Handoff != "rejected" || child.ExitCode != 2))
                        throw new IOException("invalid-handoff-outcome");
                    return WriteReply(output, reply);
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return WriteReply(output, Rejected(request, "handoff-unavailable")); }
    }
}
