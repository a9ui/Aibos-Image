using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

// Immutable pre-request intent only. A saved intent is not proof that launch
// paths are closed, that a restart happened, or that maintenance is authorized.
internal static class MaintenanceCutoverIntentStore
{
    internal const int MaximumBytes = 128 * 1024;
    internal const string FileName = "intent.json";
    internal static Action<string>? PublicationPointForSmoke { get; set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    internal static string ComputeBinding(JsonElement configuration)
    {
        ValidateConfiguration(configuration);
        return Convert.ToHexString(SHA256.HashData(Canonical(configuration))).ToLowerInvariant();
    }

    internal static byte[] Prepare(string operationId, JsonElement configuration, JsonElement bootAnchor)
    {
        RequireGuid(operationId);
        string binding = ComputeBinding(configuration);
        ValidateAnchor(bootAnchor, operationId, binding);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            protocol = "aibos.initial-cutover-intent/v1",
            operationId,
            bindingDigest = binding,
            configuration,
            bootAnchor,
            restartRequest = new
            {
                api = "Win32_OperatingSystem.Win32ShutdownTracker",
                flags = 2,
                timeoutSeconds = 30,
                reasonCode = 0,
                comment = Comment(operationId, binding),
            },
        });
        if (bytes.Length > MaximumBytes) throw new IOException("Cutover intent size is invalid.");
        return bytes;
    }

    internal static byte[] Create(string controlDirectory, byte[] proposed)
    {
        if (proposed.Length <= 0 || proposed.Length > MaximumBytes) throw new IOException("Cutover intent size is invalid.");
        proposed = proposed.ToArray();
        using JsonDocument proposedDocument = Parse(proposed);
        Validate(proposedDocument.RootElement);
        using DirectoryBinding root = BindEnhancementRoot(proposedDocument.RootElement.GetProperty("configuration"));
        using DirectoryBinding directory = new(controlDirectory);
        using FileStream writer = AcquireWriter(directory);
        root.Check();
        directory.Check();
        string target = Path.Combine(directory.Path, FileName);
        if (Path.Exists(target))
        {
            byte[] existing = ReadFile(directory, target);
            using JsonDocument previous = Parse(existing);
            Validate(previous.RootElement);
            if (!Canonical(previous.RootElement).AsSpan().SequenceEqual(Canonical(proposedDocument.RootElement)))
                throw new IOException("The existing cutover intent cannot be changed or replaced.");
            root.Check();
            return existing;
        }

        int count = 0, temporaryCount = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            if (++count > 64) throw new IOException("Cutover control directory entry bound exceeded.");
            string name = Path.GetFileName(entry);
            if (name == "writer.lock") continue;
            if (!Regex.IsMatch(name, "^\\.intent-[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant)
                || ++temporaryCount >= 16)
                throw new IOException("Unknown or excessive cutover preparation entries.");
        }

        // Uncommitted files are retained on interruption and never promoted by
        // a reader. Only an explicit create can publish a newly flushed intent.
        string temporary = Path.Combine(directory.Path, $".intent-{Guid.NewGuid():N}.tmp");
        root.Check();
        directory.Check();
        using (var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            Options = FileOptions.WriteThrough,
        }))
        {
            CheckRegularFile(stream, temporary);
            stream.Write(proposed);
            PublicationPointForSmoke?.Invoke("before-flush");
            stream.Flush(flushToDisk: true);
        }
        PublicationPointForSmoke?.Invoke("before-publish");
        root.Check();
        directory.Check();
        // MOVEFILE_WRITE_THROUGH without REPLACE_EXISTING; another intent is
        // never overwritten, including an unsupported or malformed document.
        if (!MoveFileEx(temporary, target, 0x00000008))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        PublicationPointForSmoke?.Invoke("after-publish");
        root.Check();
        directory.Check();
        byte[] saved = ReadFile(directory, target);
        if (!saved.AsSpan().SequenceEqual(proposed)) throw new IOException("Published cutover intent changed.");
        root.Check();
        return saved;
    }

    internal static byte[] ReadForResume(string controlDirectory, string operationId, JsonElement currentConfiguration)
    {
        RequireGuid(operationId);
        string binding = ComputeBinding(currentConfiguration);
        using DirectoryBinding root = BindEnhancementRoot(currentConfiguration);
        using DirectoryBinding directory = new(controlDirectory);
        byte[] original = ReadFile(directory, Path.Combine(directory.Path, FileName));
        using JsonDocument document = Parse(original);
        JsonElement intent = document.RootElement;
        Validate(intent);
        if (Text(intent, "operationId") != operationId || Text(intent, "bindingDigest") != binding)
            throw new IOException("The cutover operation or current configuration no longer matches.");
        // Return the saved bytes, including its original anchor and compatible
        // additive fields. Resume must not call Prepare with a fresh OS anchor.
        root.Check();
        return original;
    }

    internal static T WithControlWriter<T>(string controlDirectory, JsonElement configuration, Func<string, T> action)
    {
        using DirectoryBinding root = BindEnhancementRoot(configuration);
        using DirectoryBinding directory = new(controlDirectory);
        using FileStream writer = AcquireWriter(directory);
        root.Check(); directory.Check();
        T result = action(directory.Path);
        root.Check(); directory.Check();
        return result;
    }

    internal static byte[] ReadBoundPlainFile(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckRegularFile(stream, path);
        if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new IOException("Invalid cutover record size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Cutover record grew during read.");
        return bytes;
    }

    // Caller holds the shared writer and a native lease on the parent directory.
    // Retain interrupted temporary files; never overwrite an existing record.
    internal static void PublishNewPlainFile(string path, byte[] bytes)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumBytes) throw new IOException("Invalid cutover record size.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temporary, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough }))
        {
            CheckRegularFile(stream, temporary);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (!MoveFileEx(temporary, path, 0x00000008)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static FileStream AcquireWriter(DirectoryBinding directory)
    {
        string path = Path.Combine(directory.Path, "writer.lock");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            directory.Check();
            FileStream stream;
            try { stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && watch.ElapsedMilliseconds < 3000)
            { Thread.Sleep(25); continue; }
            try
            {
                CheckRegularFile(stream, path);
                if (stream.Length != 0) throw new IOException("Unknown cutover writer lock contents.");
                directory.Check();
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
    }

    private static byte[] ReadFile(DirectoryBinding directory, string path)
    {
        directory.Check();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckRegularFile(stream, path);
        if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new IOException("Cutover intent size is invalid.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Cutover intent grew during read.");
        directory.Check();
        return bytes;
    }

    private static void CheckRegularFile(FileStream stream, string path)
    {
        if (!WindowsPathIdentity.TryGetFinalPath(stream.SafeFileHandle, out string actual)
            || !string.Equals(path, actual, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(stream.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || !WindowsPathIdentity.TryGetHardLinkCount(stream.SafeFileHandle, out uint links) || links != 1)
            throw new IOException("Cutover intent storage is redirected or aliased.");
    }

    // A caller-supplied copy of the saved configuration is not a live root observation.
    // This handle covers intent I/O only; later cutover actions need their own live checks.
    private static DirectoryBinding BindEnhancementRoot(JsonElement configuration)
        => new(Text(configuration, "enhancementRoot"),
            Text(configuration, "rootVolumeId"), Text(configuration, "rootFileId"));

    private sealed class DirectoryBinding : IDisposable
    {
        internal string Path { get; }
        private readonly SafeFileHandle _handle;
        private readonly string? _volumeId;
        private readonly string? _fileId;
        internal DirectoryBinding(string directory, string? volumeId = null, string? fileId = null)
        {
            _volumeId = volumeId;
            _fileId = fileId;
            Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
            if (!WindowsPathIdentity.TryOpenDirectoryLease(Path, out _handle))
            { _handle.Dispose(); throw new IOException("Cutover directory is unavailable or redirected."); }
            try { Check(); }
            catch { _handle.Dispose(); throw; }
        }
        internal void Check()
        {
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(_handle, Path))
                throw new IOException("Cutover directory moved during access.");
            if (_volumeId is not null
                && (!WindowsPathIdentity.TryGetDirectoryIdentity(_handle, Path, out string volume, out string file)
                    || volume != _volumeId || file != _fileId))
                throw new IOException("Cutover Enhancement root identity changed.");
        }
        public void Dispose() => _handle.Dispose();
    }

    internal static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumBytes) throw new IOException("Cutover intent size is invalid.");
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        try { ValidateNames(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void ValidateNames(JsonElement value, int depth = 0)
    {
        if (depth > 12) throw new IOException("Cutover intent depth bound exceeded.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate cutover intent property.");
                if (names.Count > 128) throw new IOException("Cutover object property bound exceeded.");
                ValidateNames(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) ValidateNames(item, depth + 1);
    }

    private static void Validate(JsonElement intent)
    {
        if (intent.GetProperty("schemaVersion").GetInt32() != 1
            || Text(intent, "protocol") != "aibos.initial-cutover-intent/v1")
            throw new IOException("Unsupported cutover intent.");
        string operation = Text(intent, "operationId");
        RequireGuid(operation);
        string binding = ComputeBinding(intent.GetProperty("configuration"));
        if (Text(intent, "bindingDigest") != binding) throw new IOException("Cutover intent configuration digest mismatch.");
        ValidateAnchor(intent.GetProperty("bootAnchor"), operation, binding);
        JsonElement request = intent.GetProperty("restartRequest");
        if (Text(request, "api") != "Win32_OperatingSystem.Win32ShutdownTracker"
            || request.GetProperty("flags").GetInt32() != 2 || request.GetProperty("timeoutSeconds").GetInt32() != 30
            || request.GetProperty("reasonCode").GetInt32() != 0 || Text(request, "comment") != Comment(operation, binding))
            throw new IOException("Unsupported cutover restart request.");
    }

    private static void ValidateConfiguration(JsonElement configuration)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(configuration.GetRawText()) > MaximumBytes / 2)
            throw new IOException("Cutover configuration is too large.");
        ValidateNames(configuration);
        string root = Text(configuration, "enhancementRoot");
        if (!Path.IsPathFullyQualified(root) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) != root
            || !Regex.IsMatch(Text(configuration, "rootVolumeId"), "^[0-9A-F]{8}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(Text(configuration, "rootFileId"), "^[0-9A-F]{16}$", RegexOptions.CultureInvariant)
            || Text(configuration, "jobsFileName") is not ("jobs.json" or "jobs.sqlite3"))
            throw new IOException("Invalid cutover root binding.");
        RequireDigest(Text(configuration, "artifactManifestSha256").ToLowerInvariant());
        if (!Regex.IsMatch(Text(configuration, "lifetimeProfile"), "^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant))
            throw new IOException("Invalid cutover lifetime profile identity.");
        JsonElement entries = configuration.GetProperty("managedEntries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 32)
            throw new IOException("Invalid managed entry inventory bound.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string kind = Text(entry, "kind"), identity = Text(entry, "identity");
            if (kind is not ("task" or "shortcut" or "launcher") || identity.Length is < 1 or > 1024
                || !identities.Add(kind + "|" + identity)) throw new IOException("Invalid or duplicate managed entry.");
            RequireDigest(Text(entry, "beforeSha256"));
            RequireDigest(Text(entry, "closedSha256"));
        }
        if (Canonical(configuration).Length > MaximumBytes / 2) throw new IOException("Cutover configuration is too large.");
    }

    private static void ValidateAnchor(JsonElement anchor, string operation, string binding)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(anchor.GetRawText()) > MaximumBytes / 2)
            throw new IOException("Cutover anchor is too large.");
        ValidateNames(anchor);
        if (anchor.GetProperty("SchemaVersion").GetInt32() != 1 || Text(anchor, "Profile") != "windows-system-reboot-v1"
            || Text(anchor, "OperationId") != operation || Text(anchor, "BindingDigest") != binding
            || anchor.GetProperty("RecordId").GetInt64() <= 0)
            throw new IOException("Cutover boot anchor binding is invalid.");
        RequireDigest(Text(anchor, "RecordDigest"));
        JsonElement windows = anchor.GetProperty("Windows");
        RequireDigest(Text(windows, "HostDigest"));
        if (Text(windows, "Computer").Length is < 1 or > 255 || Text(windows, "OsVersion").Length is < 1 or > 64
            || !Regex.IsMatch(Text(windows, "OsBuild"), "^[0-9]{1,10}$", RegexOptions.CultureInvariant))
            throw new IOException("Invalid cutover OS identity.");
        DateTimeOffset boot = Utc(Text(windows, "BootTimeUtc")), captured = Utc(Text(anchor, "CapturedAtUtc"));
        if (captured < boot) throw new IOException("Cutover anchor predates its OS boot.");
    }

    private static DateTimeOffset Utc(string text)
    {
        if (text.Length > 40 || !Regex.IsMatch(text, "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,9})?Z$", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(text,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var value))
            throw new IOException("Invalid cutover UTC time.");
        return value;
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new IOException("Null cutover intent property.");
    private static string Comment(string operation, string binding) => $"Aibos initial cutover {operation} {binding}";
    private static void RequireDigest(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)) throw new IOException("Invalid cutover digest.");
    }
    private static void RequireGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != value)
            throw new IOException("Invalid cutover operation identity.");
    }
    private static byte[] Canonical(JsonElement value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(writer, value);
        return bytes.ToArray();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
