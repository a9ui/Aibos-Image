using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PhotoViewer.Wpf;

internal static class FixtureEntry
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is MaintenanceEnrollmentHandoff.ProbeArgument or MaintenanceEnrollmentHandoff.ChildArgument)
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                if (SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
                {
                    Console.Error.WriteLine("Fixture startup has no WPF Dispatcher context.");
                    app.Shutdown(1);
                    return;
                }
                // Force the parent pipe read to suspend before the child can reply.
                if (args[0] == MaintenanceEnrollmentHandoff.ChildArgument) Thread.Sleep(200);
                app.Shutdown(Run(args));
            };
            return app.Run();
        }
        return Run(args);
    }

    private static int Run(string[] args)
    {
try
{
const string identityVariable = "AIBOS_FIXTURE_HANDOFF_IDENTITY";
const string jobsVariable = "AIBOS_FIXTURE_HANDOFF_JOBS";
const string modeVariable = "AIBOS_FIXTURE_HANDOFF_MODE";
if (args[0] == "--pinned-launch-manifest")
{
    if (args.Length != 2 || Environment.GetEnvironmentVariable("AIBOS_COMPANION_START_ON_LAUNCH") != "0")
        return 2;
    using var pinned = MaintenanceEnrollmentHandoff.PinLaunchArtifacts(args[1]);
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixed-launch.pinned"), args[1]);
    return 0;
}
if (args[0] == MaintenanceEnrollmentHandoff.ProbeArgument)
    return MaintenanceEnrollmentHandoff.RunProbe(args[1], args[2], args[3], Console.OpenStandardOutput());
if (args[0] == MaintenanceEnrollmentHandoff.ChildArgument)
{
    using var output = new MemoryStream();
    int code = MaintenanceEnrollmentHandoff.RunChild(Console.OpenStandardInput(), output,
        () => SingleInstanceCoordinator.CreateForSmoke(Environment.GetEnvironmentVariable(identityVariable)!, false),
        () => Environment.GetEnvironmentVariable(jobsVariable)!);
    JsonObject reply = JsonNode.Parse(output.ToArray())!.AsObject();
    switch (Environment.GetEnvironmentVariable(modeVariable))
    {
        case "wrong-pid": reply["processId"] = 1; break;
        case "wrong-nonce": reply["nonce"] = new string('A', 64); break;
        case "authority": reply["enrolled"] = true; break;
        case "wrong-root": reply["root"]!["fileId"] = "0000000000000000"; break;
    }
    Console.Write(reply.ToJsonString());
    return code;
}
if (args[0] == "--old-worker")
{
    File.WriteAllText(args[1] + ".ready", "ready");
    if (!SpinWait.SpinUntil(() => File.Exists(args[1] + ".release"), 15000)) return 2;
    File.WriteAllText(args[1], "late synthetic output");
    return 0;
}

string root = Path.Combine(args[0], "enhance");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable(identityVariable, Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable(jobsVariable, Path.Combine(root, "jobs.json"));
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
using (var brokenPipe = new BrokenOutputPipe())
    Check(MaintenanceEnrollmentHandoff.RejectArguments(brokenPipe) == 2, "Disconnected output escaped the passive branch.");
using (var closedPipe = new MemoryStream())
{
    closedPipe.Dispose();
    Check(MaintenanceEnrollmentHandoff.RejectArguments(closedPipe) == 2, "Disposed output escaped the passive branch.");
}
Process Start(params string[] arguments)
{
    var process = new Process { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false,
        CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath)!.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        process.StartInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    return process;
}
(int Code, JsonObject Reply) Collect(Process process, string input = "")
{
    try
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit(20000)) throw new TimeoutException("Synthetic child exceeded deadline.");
        if (stderr.GetAwaiter().GetResult().Length != 0) throw new Exception("Unexpected synthetic stderr.");
        return (process.ExitCode, JsonNode.Parse(stdout.GetAwaiter().GetResult())!.AsObject());
    }
    finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } process.Dispose(); }
}
string[] artifacts = ["Microsoft.Data.Sqlite.dll", "PhotoViewer.Wpf.deps.json", "PhotoViewer.Wpf.dll", "PhotoViewer.Wpf.exe",
    "PhotoViewer.Wpf.runtimeconfig.json", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.winsqlite3.dll"];
string manifest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', artifacts.Order(StringComparer.Ordinal)
    .Select(name => name + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name)))))))));
(int Code, JsonObject Reply) Probe() => Collect(Start(MaintenanceEnrollmentHandoff.ProbeArgument, root, "jobs.json", manifest));
string pinnedArtifact = Path.Combine(AppContext.BaseDirectory, "Microsoft.Data.Sqlite.dll");
bool CanOpenArtifactForWrite()
{
    try { using var file = new FileStream(pinnedArtifact, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); return true; }
    catch (IOException) { return false; }
}
using (MaintenanceEnrollmentHandoff.PinLaunchArtifacts(manifest))
    Check(!CanOpenArtifactForWrite(), "Pinned artifact allowed an overlapping writer.");
Check(CanOpenArtifactForWrite(), "Released launch retained an artifact handle.");
foreach (string rejected in new[] { new string('0', 64), "invalid", manifest.ToLowerInvariant() })
{
    bool refused = false;
    try { using var unexpected = MaintenanceEnrollmentHandoff.PinLaunchArtifacts(rejected); }
    catch (IOException) { refused = true; }
    Check(refused && CanOpenArtifactForWrite(), "Invalid manifest was accepted or leaked an artifact handle.");
}
Check(Collect(Start(MaintenanceEnrollmentHandoff.ProbeArgument, root, "jobs.json", new string('0', 64))).Code == 2,
    "Accepted artifacts different from the launcher's verified manifest.");
var baseline = Probe();
Check(baseline.Code == 10 && (string?)baseline.Reply["handoff"] == "verified", "Cold handoff did not verify.");
Check((bool?)baseline.Reply["enrolled"] == false && (bool?)baseline.Reply["maintenanceAllowed"] == false,
    "A handoff granted enrollment authority.");
Check(Directory.GetFileSystemEntries(root).Length == 0, "Handoff created durable state.");
using (var primary = SingleInstanceCoordinator.CreateForSmoke(Environment.GetEnvironmentVariable(identityVariable)!))
{
    using var activated = new AutoResetEvent(false);
    primary.StartListening(() => activated.Set());
    var blocked = Probe();
    Check(blocked.Code == 2 && (string?)blocked.Reply["reason"] == "existing-primary", "Existing primary was reused or bypassed.");
    Check(!activated.WaitOne(150), "Passive handoff signaled an existing primary.");
}
foreach (string mode in new[] { "wrong-pid", "wrong-nonce", "authority", "wrong-root" })
{
    Environment.SetEnvironmentVariable(modeVariable, mode);
    Check(Probe().Code == 2, "Parent accepted invalid reply: " + mode);
}
Environment.SetEnvironmentVariable(modeVariable, null);
var request = new JsonObject();
foreach (string name in new[] { "protocol", "attemptId", "nonce", "launchGeneration", "manifestSha256", "assemblyPath", "root", "jobsPath" })
    request[name] = baseline.Reply[name]!.DeepClone();
Environment.SetEnvironmentVariable("AIBOS_ENROLLMENT_LAUNCH_GENERATION", (string?)request["launchGeneration"]);
foreach (string name in new[] { "nonce", "manifestSha256", "launchGeneration", "jobsPath", "assemblyPath" })
{
    var changed = request.DeepClone().AsObject();
    changed[name] = name is "nonce" ? null : name is "manifestSha256" ? new string('0', 64)
        : name is "launchGeneration" ? Guid.NewGuid().ToString("D") : Path.Combine(root, "wrong");
    Check(Collect(Start(MaintenanceEnrollmentHandoff.ChildArgument), changed.ToJsonString()).Code == 2, "Accepted bad request: " + name);
}
foreach (string raw in new[] { "{", new string(' ', 16385), request.ToJsonString().Replace("\"volumeId\":", "\"volumeId\":\"bad\",\"volumeId\":"),
    request.ToJsonString().Insert(1, "\"unsupported\":true,") })
    Check(Collect(Start(MaintenanceEnrollmentHandoff.ChildArgument), raw).Code == 2, "Accepted malformed or oversized request.");

string lateOutput = Path.Combine(root, "old-generation-output");
using (Process old = Start("--old-worker", lateOutput))
{
    try
    {
        Check(SpinWait.SpinUntil(() => File.Exists(lateOutput + ".ready"), 5000), "Old fixture did not start.");
        var observed = Probe();
        Check(!old.HasExited && observed.Code == 10 && (bool?)observed.Reply["enrolled"] == false
            && (bool?)observed.Reply["maintenanceAllowed"] == false, "New handoff certified an interfering old generation.");
        File.WriteAllText(lateOutput + ".release", "release");
        Check(old.WaitForExit(5000) && old.ExitCode == 0 && File.Exists(lateOutput), "Old execution counterexample did not actually publish.");
    }
    finally { if (!old.HasExited) { old.Kill(true); old.WaitForExit(5000); } }
}
Console.WriteLine($"{{\"ok\":true,\"checks\":{checks},\"wpfStartupContext\":true,\"syntheticOnly\":true,\"productionEnrollment\":false}}");
return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}

sealed class BrokenOutputPipe : MemoryStream
{
    public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Synthetic caller disconnected.");
}
