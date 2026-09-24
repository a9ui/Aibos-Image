using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Xml.Linq;
using PhotoViewer.Wpf;

internal static class CutoverIntentFixture
{
    private const string Operation = "11111111-1111-4111-8111-111111111111";
    private static int _checks;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, nint reserved);

    private static JsonElement ManagedEntry(string parent)
    {
        string planPath = Path.Combine(parent, "managed-task-plan.json");
        if (File.Exists(planPath))
        {
            using var plan = JsonDocument.Parse(File.ReadAllText(planPath, Encoding.UTF8));
            return plan.RootElement.Clone();
        }
        return JsonSerializer.SerializeToElement(new
        {
            kind = "task", identity = "Synthetic Task",
            beforeSha256 = new string('b', 64), closedSha256 = new string('c', 64),
        });
    }

    private static JsonElement Configuration(string parent)
    {
        string root = Path.Combine(parent, "enhance");
        Directory.CreateDirectory(root);
        if (!WindowsPathIdentity.TryOpenDirectoryLease(root, out var lease)) throw new Exception("Fixture root lease failed.");
        using (lease)
        {
            if (!WindowsPathIdentity.TryGetDirectoryIdentity(lease, root, out string volume, out string file))
                throw new Exception("Fixture root identity failed.");
            return JsonSerializer.SerializeToElement(new
            {
                enhancementRoot = root, rootVolumeId = volume, rootFileId = file, jobsFileName = "jobs.json",
                artifactManifestSha256 = new string('a', 64), lifetimeProfile = "synthetic-local-v1",
                managedEntries = new[] { ManagedEntry(parent) },
            });
        }
    }

    private static JsonElement Anchor(JsonElement configuration, string operation = Operation, string captured = "2026-01-01T00:10:00Z")
        => JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = 1, Profile = "windows-system-reboot-v1", OperationId = operation,
            BindingDigest = MaintenanceCutoverIntentStore.ComputeBinding(configuration),
            Windows = new { HostDigest = new string('d', 64), Computer = "SYNTHETIC-HOST", BootTimeUtc = "2026-01-01T00:00:00Z", OsVersion = "10.0.99999", OsBuild = "99999" },
            CapturedAtUtc = captured, RecordId = 100,
            RecordDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AnchorEvent()))).ToLowerInvariant(),
        });

    private static string Event(long record, string provider, string providerId, int id, string time, Dictionary<string, string> data)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        return new XElement(ns + "Event",
            new XElement(ns + "System", new XElement(ns + "Provider", new XAttribute("Name", provider), new XAttribute("Guid", providerId)),
                new XElement(ns + "EventID", id), new XElement(ns + "Version", 0), new XElement(ns + "TimeCreated", new XAttribute("SystemTime", time)),
                new XElement(ns + "EventRecordID", record), new XElement(ns + "Channel", "System"), new XElement(ns + "Computer", "SYNTHETIC-HOST")),
            new XElement(ns + "EventData", data.Select(entry => new XElement(ns + "Data", new XAttribute("Name", entry.Key), entry.Value))))
            .ToString(SaveOptions.DisableFormatting);
    }
    private static string AnchorEvent() => Event(100, "Synthetic", "{00000000-0000-0000-0000-000000000001}", 1, "2026-01-01T00:09:00Z", []);

    private static void Check(bool condition, string failure)
    { if (!condition) throw new Exception(failure); _checks++; }
    private static void Refuses(Action action, string failure)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { _checks++; return; }
        throw new Exception(failure);
    }

    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Run(string[] args)
    {
        if (args[0] == "crash")
        {
            string childControl = args[1], phase = args[2];
            var configuration = Configuration(Path.GetDirectoryName(childControl)!);
            byte[] payload = MaintenanceCutoverIntentStore.Prepare(Operation, configuration, Anchor(configuration));
            MaintenanceCutoverIntentStore.PublicationPointForSmoke = point => { if (point == phase) Environment.Exit(91); };
            MaintenanceCutoverIntentStore.Create(childControl, payload);
            return 92;
        }
        string runRoot = args[0];
        var config = Configuration(runRoot);
        byte[] original = MaintenanceCutoverIntentStore.Prepare(Operation, config, Anchor(config));
        string Fresh(string name) { string directory = Path.Combine(runRoot, name); Directory.CreateDirectory(directory); return directory; }
        string control = Fresh("main");
        string intentPath = Path.Combine(control, MaintenanceCutoverIntentStore.FileName);
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(control, Operation, config), "Missing intent was invented.");
        Check(!Directory.EnumerateFileSystemEntries(control).Any(), "Passive read created control files.");
        byte[] saved = MaintenanceCutoverIntentStore.Create(control, original);
        Check(saved.AsSpan().SequenceEqual(original), "Create did not preserve the prepared intent.");
        var timestamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(intentPath, timestamp);
        Check(MaintenanceCutoverIntentStore.Create(control, original).AsSpan().SequenceEqual(original)
            && File.GetLastWriteTimeUtc(intentPath) == timestamp, "Same-operation replay rewrote its intent.");
        byte[] recaptured = MaintenanceCutoverIntentStore.Prepare(Operation, config, Anchor(config, captured: "2026-01-01T00:20:00Z"));
        Refuses(() => MaintenanceCutoverIntentStore.Create(control, recaptured), "A newly captured anchor replaced the first one.");
        const string another = "22222222-2222-4222-8222-222222222222";
        Refuses(() => MaintenanceCutoverIntentStore.Create(control,
            MaintenanceCutoverIntentStore.Prepare(another, config, Anchor(config, another))), "A different operation replaced the first one.");
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(control, another, config), "Resume accepted a different operation.");
        var changedConfig = JsonNode.Parse(config.GetRawText())!;
        changedConfig["jobsFileName"] = "jobs.sqlite3";
        var changedElement = JsonSerializer.SerializeToElement(changedConfig);
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(control, Operation, changedElement), "Resume accepted a different binding.");
        Refuses(() => MaintenanceCutoverIntentStore.Prepare(Operation, changedElement, Anchor(config)), "Prepare accepted an anchor for another configuration.");
        Check(File.ReadAllBytes(intentPath).AsSpan().SequenceEqual(original), "Refusal changed the original intent.");

        // A caller can accidentally replay the saved configuration instead of
        // collecting a new one. Native identity must still reject a same-path replacement.
        string rootParent = Fresh("root-replacement");
        var rootConfig = Configuration(rootParent);
        byte[] rootIntent = MaintenanceCutoverIntentStore.Prepare(Operation, rootConfig, Anchor(rootConfig));
        string rootControl = Path.Combine(rootParent, "control");
        Directory.CreateDirectory(rootControl);
        MaintenanceCutoverIntentStore.Create(rootControl, rootIntent);
        string originalRoot = Path.Combine(rootParent, "enhance"), retainedRoot = originalRoot + "-retained";
        File.WriteAllText(Path.Combine(originalRoot, "source.keep"), "original source");
        Directory.Move(originalRoot, retainedRoot);
        Directory.CreateDirectory(originalRoot);
        File.WriteAllText(Path.Combine(originalRoot, "unrelated.keep"), "replacement source");
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(rootControl, Operation, rootConfig),
            "Same-path root replacement resumed a stale configuration.");
        Refuses(() => MaintenanceCutoverIntentStore.Create(rootControl, rootIntent),
            "Same-operation create replay ignored replaced root identity.");
        string freshRootControl = Path.Combine(rootParent, "new-control");
        Directory.CreateDirectory(freshRootControl);
        Refuses(() => MaintenanceCutoverIntentStore.Create(freshRootControl, rootIntent),
            "A new control location published an intent for the replaced root.");
        Check(!Directory.EnumerateFileSystemEntries(freshRootControl).Any()
            && File.ReadAllBytes(Path.Combine(rootControl, MaintenanceCutoverIntentStore.FileName)).AsSpan().SequenceEqual(rootIntent),
            "Root refusal wrote control state or rewrote saved evidence.");
        Check(File.ReadAllText(Path.Combine(retainedRoot, "source.keep")) == "original source"
            && File.ReadAllText(Path.Combine(originalRoot, "unrelated.keep")) == "replacement source",
            "Root refusal changed source data.");

        foreach (string boundary in new[] { "before-publish", "after-publish" })
        {
            string parent = Fresh("root-move-" + boundary);
            var boundaryConfig = Configuration(parent);
            string boundaryRoot = Path.Combine(parent, "enhance");
            string boundaryControl = Path.Combine(parent, "control");
            Directory.CreateDirectory(boundaryControl);
            byte[] proposal = MaintenanceCutoverIntentStore.Prepare(Operation, boundaryConfig, Anchor(boundaryConfig));
            bool replaced = false;
            MaintenanceCutoverIntentStore.PublicationPointForSmoke = point =>
            {
                if (point != boundary) return;
                Directory.Move(boundaryRoot, boundaryRoot + "-retained");
                Directory.CreateDirectory(boundaryRoot);
                replaced = true;
            };
            try { Refuses(() => MaintenanceCutoverIntentStore.Create(boundaryControl, proposal),
                "Root moved during intent publication without refusal."); }
            finally { MaintenanceCutoverIntentStore.PublicationPointForSmoke = null; }
            string published = Path.Combine(boundaryControl, MaintenanceCutoverIntentStore.FileName);
            Check(File.Exists(published) == (boundary == "after-publish"),
                "Root move changed the expected intent publication boundary.");
            if (File.Exists(published))
                Check(File.ReadAllBytes(published).AsSpan().SequenceEqual(proposal), "Published evidence was erased or rebuilt.");
            if (replaced)
                Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(boundaryControl, Operation, boundaryConfig),
                    "Interrupted publication resumed against a replacement root.");
        }

        string mutableControl = Fresh("mutable-input");
        byte[] mutable = original.ToArray();
        MaintenanceCutoverIntentStore.PublicationPointForSmoke = point => { if (point == "before-flush") mutable[0] = 0; };
        try { Check(MaintenanceCutoverIntentStore.Create(mutableControl, mutable).AsSpan().SequenceEqual(original), "Caller mutation changed the captured intent bytes."); }
        finally { MaintenanceCutoverIntentStore.PublicationPointForSmoke = null; }
        string missingControl = Path.Combine(runRoot, "missing-control");
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(missingControl, Operation, config), "Missing control directory was accepted.");
        Check(!Directory.Exists(missingControl), "Passive read created a control directory.");

        string bounded = Fresh("bounded");
        for (int i = 0; i < 16; i++) File.WriteAllText(Path.Combine(bounded, $".intent-{i:x32}.tmp"), "retain");
        Refuses(() => MaintenanceCutoverIntentStore.Create(bounded, original), "Excessive interrupted preparations were ignored.");
        Check(Directory.EnumerateFiles(bounded, ".intent-*.tmp").Count() == 16 && !File.Exists(Path.Combine(bounded, MaintenanceCutoverIntentStore.FileName)),
            "Preparation bound removed evidence or published an intent.");

        var additive = JsonNode.Parse(original)!;
        additive["futureNote"] = new JsonObject { ["keep"] = "unchanged" };
        string additiveControl = Fresh("additive");
        byte[] additiveBytes = Encoding.UTF8.GetBytes(additive.ToJsonString());
        MaintenanceCutoverIntentStore.Create(additiveControl, additiveBytes);
        Check(MaintenanceCutoverIntentStore.ReadForResume(additiveControl, Operation, config).AsSpan().SequenceEqual(additiveBytes),
            "Resume dropped compatible unknown fields or rebuilt the anchor.");

        var future = JsonNode.Parse(original)!; future["schemaVersion"] = 999;
        var badRequest = JsonNode.Parse(original)!; badRequest["restartRequest"]!["flags"] = 6;
        var badDigest = JsonNode.Parse(original)!; badDigest["bindingDigest"] = new string('f', 64);
        byte[][] invalid = [Encoding.UTF8.GetBytes("{"), Encoding.UTF8.GetBytes(future.ToJsonString()),
            Encoding.UTF8.GetBytes(badRequest.ToJsonString()), Encoding.UTF8.GetBytes(badDigest.ToJsonString()),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"SchemaVersion\":1")),
            new byte[MaintenanceCutoverIntentStore.MaximumBytes + 1]];
        for (int i = 0; i < invalid.Length; i++)
        {
            string badControl = Fresh("bad-" + i), file = Path.Combine(badControl, MaintenanceCutoverIntentStore.FileName);
            File.WriteAllBytes(file, invalid[i]);
            Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(badControl, Operation, config), "Protected state was accepted.");
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(invalid[i]) && Directory.EnumerateFileSystemEntries(badControl).Count() == 1,
                "Protected read rewrote or created files.");
            Refuses(() => MaintenanceCutoverIntentStore.Create(badControl, original), "Protected state was overwritten.");
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(invalid[i]), "Create replaced protected state.");
        }

        string aliasControl = Fresh("alias"), aliasFile = Path.Combine(aliasControl, MaintenanceCutoverIntentStore.FileName);
        File.WriteAllBytes(aliasFile, original);
        if (!CreateHardLink(Path.Combine(aliasControl, "second-name.json"), aliasFile, 0)) throw new Exception("Fixture hardlink failed.");
        Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(aliasControl, Operation, config), "Aliased intent was accepted.");
        string directoryControl = Fresh("directory-entry");
        Directory.CreateDirectory(Path.Combine(directoryControl, MaintenanceCutoverIntentStore.FileName));
        bool directoryRejected = false;
        try { MaintenanceCutoverIntentStore.ReadForResume(directoryControl, Operation, config); }
        catch (UnauthorizedAccessException) { directoryRejected = true; }
        catch (IOException) { directoryRejected = true; }
        Check(directoryRejected, "A directory was treated as an intent.");

        string movedControl = Fresh("moving"), moved = movedControl + "-moved";
        MaintenanceCutoverIntentStore.PublicationPointForSmoke = point =>
        {
            if (point == "before-publish") { Directory.Move(movedControl, moved); Directory.CreateDirectory(movedControl); }
        };
        try { Refuses(() => MaintenanceCutoverIntentStore.Create(movedControl, original), "Moved control directory was followed."); }
        finally { MaintenanceCutoverIntentStore.PublicationPointForSmoke = null; }
        Check(!File.Exists(Path.Combine(movedControl, MaintenanceCutoverIntentStore.FileName))
            && !File.Exists(Path.Combine(moved, MaintenanceCutoverIntentStore.FileName))
            && Directory.EnumerateFiles(Directory.Exists(moved) ? moved : movedControl, ".intent-*.tmp").Count() == 1,
            "Prevented or detected directory move published or removed preparation evidence.");

        string concurrent = Fresh("concurrent");
        Task.WaitAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => MaintenanceCutoverIntentStore.Create(concurrent, original))).ToArray());
        Check(File.ReadAllBytes(Path.Combine(concurrent, MaintenanceCutoverIntentStore.FileName)).AsSpan().SequenceEqual(original)
            && !Directory.EnumerateFiles(concurrent, ".intent-*.tmp").Any(), "Concurrent preparation produced more than one intent.");

        foreach (string phase in new[] { "before-flush", "before-publish", "after-publish" })
        {
            string crashParent = Fresh("crash-" + phase), crashControl = Path.Combine(crashParent, "control");
            Directory.CreateDirectory(crashControl);
            var crashConfig = Configuration(crashParent);
            byte[] expected = MaintenanceCutoverIntentStore.Prepare(Operation, crashConfig, Anchor(crashConfig));
            using var child = new Process();
            child.StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                child.StartInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (string argument in new[] { "crash", crashControl, phase }) child.StartInfo.ArgumentList.Add(argument);
            child.Start();
            var errors = child.StandardError.ReadToEndAsync(); var output = child.StandardOutput.ReadToEndAsync();
            try
            {
                if (!child.WaitForExit(15000)) throw new Exception("Intent crash fixture timeout.");
                Check(child.ExitCode == 91 && errors.GetAwaiter().GetResult().Length == 0, "Child did not stop at the intended publication boundary.");
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
            if (phase == "after-publish")
            {
                Check(MaintenanceCutoverIntentStore.ReadForResume(crashControl, Operation, crashConfig).AsSpan().SequenceEqual(expected),
                    "Lost response after publication could not resume the original intent.");
            }
            else
            {
                Refuses(() => MaintenanceCutoverIntentStore.ReadForResume(crashControl, Operation, crashConfig), "Uncommitted temp was automatically promoted.");
                Check(Directory.EnumerateFiles(crashControl, ".intent-*.tmp").Count() == 1, "Interrupted preparation evidence disappeared.");
                MaintenanceCutoverIntentStore.Create(crashControl, expected);
                Check(Directory.EnumerateFiles(crashControl, ".intent-*.tmp").Count() == 1
                    && MaintenanceCutoverIntentStore.ReadForResume(crashControl, Operation, crashConfig).AsSpan().SequenceEqual(expected),
                    "Explicit retry deleted old preparation evidence or changed the intent.");
            }
        }
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(runRoot, "enhance")).Any(), "Intent persistence touched Jobs or Inbox resources.");
        // Feed the exact saved intent into the actual PowerShell OS predicate.
        // These raw event strings are synthetic, not exported host logs.
        var intent = JsonNode.Parse(original)!;
        string[] events = [AnchorEvent(),
            Event(101, "User32", "{b0aa8734-56f7-41cc-b2f4-de228e98b946}", 1074, "2026-01-01T00:59:00Z", new()
            { ["param1"]="synthetic", ["param2"]="SYNTHETIC-HOST", ["param3"]="synthetic", ["param4"]="0x0", ["param5"]="not parsed", ["param6"]=intent["restartRequest"]!["comment"]!.GetValue<string>(), ["param7"]="synthetic" }),
            Event(102, "Microsoft-Windows-Kernel-General", "{a68ca8b7-004f-d7b6-a698-07e2de0f1f5d}", 13, "2026-01-01T00:59:30Z", new() { ["StopTime"]="2026-01-01T00:59:30Z" }),
            Event(103, "Microsoft-Windows-Kernel-General", "{a68ca8b7-004f-d7b6-a698-07e2de0f1f5d}", 12, "2026-01-01T01:00:01Z", new()
            { ["MajorVersion"]="10", ["MinorVersion"]="0", ["BuildVersion"]="99999", ["QfeVersion"]="1", ["ServiceVersion"]="0", ["BootMode"]="0", ["StartTime"]="2026-01-01T01:00:00Z" })];
        File.WriteAllText(Path.Combine(runRoot, "observer-events.json"), JsonSerializer.Serialize(events));
        var current = intent["bootAnchor"]!["Windows"]!.DeepClone();
        current["BootTimeUtc"] = "2026-01-01T01:00:00Z";
        File.WriteAllText(Path.Combine(runRoot, "observer-current.json"), current.ToJsonString());
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks = _checks, realChildExits = 3, restartRequests = 0, enrollmentWrites = 0, fixtureRoot = runRoot }));
        return 0;
    }
}
