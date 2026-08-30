using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoToolsV2InventorySmoke(
        string resultPath,
        string[] args)
    {
        string fullResultPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-tools-v2-inventory-smoke-")
            .FullName;
        object result;
        bool succeeded = false;
        MainWindow? window = null;
        string? previousOutputRoot = Environment.GetEnvironmentVariable(
            "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT");
        try
        {
            string fixturePath = RequireVideoToolsV2ReaderArgument(
                args,
                "--fixture");
            byte[] fixtureBefore = File.ReadAllBytes(fixturePath);
            using JsonDocument fixtureDocument = JsonDocument.Parse(
                fixtureBefore);
            JsonElement fixtures = fixtureDocument.RootElement
                .GetProperty("readerFixtures");
            JsonElement editFixture = fixtures.GetProperty("edit");
            JsonElement finishFixture = fixtures.GetProperty("finish");

            string outputRoot = Path.Combine(smokeRoot, "managed");
            string videosRoot = Path.Combine(outputRoot, "Videos", "2026-08-24");
            string stagingRoot = Path.Combine(smokeRoot, "staging");
            string originalPath = Path.GetFullPath(Path.Combine(smokeRoot, "original.mp4"));
            string stagingPath = Path.GetFullPath(Path.Combine(stagingRoot, "source.mp4"));
            Directory.CreateDirectory(videosRoot);
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllBytes(originalPath, [1, 2, 3, 4]);
            File.WriteAllBytes(stagingPath, [1, 2, 3, 4]);

            const string rootId = "10000000-0000-4000-8000-000000000001";
            const string editId = "10000000-0000-4000-8000-000000000002";
            const string finishId = "10000000-0000-4000-8000-000000000003";
            string rootOutput = Path.GetFullPath(Path.Combine(videosRoot, "root.mp4"));
            string editOutput = Path.GetFullPath(Path.Combine(videosRoot, "edit.mp4"));
            string finishOutput = Path.GetFullPath(Path.Combine(videosRoot, "finish.mp4"));
            using JsonDocument root = CreateInventoryJob(
                finishFixture,
                rootId,
                "succeeded",
                rootOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            rootOutput = MaterializeInventoryOutput(
                root,
                [9, 8, 7, 6]);
            using JsonDocument edit = CreateInventoryJob(
                editFixture,
                editId,
                "succeeded",
                editOutput,
                video =>
                {
                    video["source"]!["producerJobId"] = rootId;
                    video["source"]!["canonicalPath"] = rootOutput;
                    ApplyInventorySourceSignature(
                        video["source"]!["signature"]!.AsObject(),
                        rootOutput);
                    video["requested"]!["source"]!["sourceVideoJobId"] = rootId;
                },
                job => job["sourceVideoJobId"] = rootId);
            editOutput = MaterializeInventoryOutput(
                edit,
                [9, 8, 7, 6]);
            using JsonDocument finish = CreateManagedFinishInventoryJob(
                finishFixture,
                finishId,
                editId,
                editOutput,
                finishOutput,
                "succeeded");
            finishOutput = MaterializeInventoryOutput(
                finish,
                [9, 8, 7, 6]);

            using JsonDocument presentation = JsonDocument.Parse(
                finish.RootElement.GetRawText());
            bool presented = PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                    presentation.RootElement,
                    out _, out _, out _, out _, out _, out _,
                    out bool writerMutation,
                    out bool canUseOutput,
                    out string[] actionKinds)
                && writerMutation
                && canUseOutput
                && actionKinds.SequenceEqual(
                    ["open-output", "delete-output"],
                    StringComparer.Ordinal);
            bool passiveRead = presented
                && fixtureBefore.AsSpan().SequenceEqual(File.ReadAllBytes(fixturePath))
                && File.ReadAllBytes(finishOutput).SequenceEqual(new byte[] { 9, 8, 7, 6 });

            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                outputRoot);
            window = HiddenWindow();
            using JsonDocument exactJobs = JsonDocument.Parse(
                "[" + string.Join(",", new[]
                {
                    finish.RootElement.GetRawText(),
                    edit.RootElement.GetRawText(),
                    root.RootElement.GetRawText(),
                }) + "]");
            string[] roots = window.ResolveVideoToolsV2ManagedInventoryForSmoke(
                exactJobs.RootElement,
                out string[] kinds,
                out string[] outputs,
                out string[] labels);
            bool exactInventory = roots.Length == 3
                && roots.All(path => string.Equals(
                    path,
                    originalPath,
                    StringComparison.OrdinalIgnoreCase))
                && outputs.All(path => path.StartsWith(
                    videosRoot,
                    StringComparison.OrdinalIgnoreCase));
            bool ancestry = kinds.OrderBy(static kind => kind, StringComparer.Ordinal)
                    .SequenceEqual(["edit", "finish", "finish"], StringComparer.Ordinal)
                && outputs.Contains(editOutput, StringComparer.OrdinalIgnoreCase)
                && outputs.Contains(finishOutput, StringComparer.OrdinalIgnoreCase);
            bool labelsExact = window.OriginalVideoVersionLabelForSmoke(
                    originalPath) == "元動画"
                && labels.Contains("AI編集 1/1", StringComparer.Ordinal)
                && labels.Contains("AI高画質化 1/2", StringComparer.Ordinal)
                && labels.Contains("AI高画質化 2/2", StringComparer.Ordinal);

            string emptyOutput = Path.Combine(videosRoot, "empty.mp4");
            File.WriteAllBytes(emptyOutput, []);
            using JsonDocument missing = CreateInventoryJob(
                editFixture,
                "20000000-0000-4000-8000-000000000001",
                "succeeded",
                Path.Combine(videosRoot, "missing-producer.mp4"),
                video =>
                {
                    video["source"]!["producerJobId"] =
                        "20000000-0000-4000-8000-000000000099";
                    video["source"]!["canonicalPath"] = Path.Combine(videosRoot, "unknown.mp4");
                    video["requested"]!["source"]!["sourceVideoJobId"] =
                        "20000000-0000-4000-8000-000000000099";
                },
                job => job["sourceVideoJobId"] =
                    "20000000-0000-4000-8000-000000000099");
            File.WriteAllBytes(Path.Combine(videosRoot, "missing-producer.mp4"), [1]);
            using JsonDocument failed = CreateInventoryJob(
                finishFixture,
                "20000000-0000-4000-8000-000000000002",
                "failed",
                Path.Combine(videosRoot, "failed.mp4"),
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            File.WriteAllBytes(Path.Combine(videosRoot, "failed.mp4"), [1]);
            using JsonDocument running = CreateInventoryJob(
                finishFixture,
                "20000000-0000-4000-8000-000000000008",
                "running",
                Path.Combine(videosRoot, "running.mp4"),
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            File.WriteAllBytes(Path.Combine(videosRoot, "running.mp4"), [1]);
            using JsonDocument empty = CreateInventoryJob(
                finishFixture,
                "20000000-0000-4000-8000-000000000003",
                "succeeded",
                emptyOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            string outsideOutput = Path.Combine(smokeRoot, "outside.mp4");
            File.WriteAllBytes(outsideOutput, [1]);
            using JsonDocument outside = CreateInventoryJob(
                finishFixture,
                "20000000-0000-4000-8000-000000000009",
                "succeeded",
                outsideOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            const string cycleAId = "20000000-0000-4000-8000-000000000004";
            const string cycleBId = "20000000-0000-4000-8000-000000000005";
            string cycleAOutput = Path.Combine(videosRoot, "cycle-a.mp4");
            string cycleBOutput = Path.Combine(videosRoot, "cycle-b.mp4");
            File.WriteAllBytes(cycleAOutput, [1]);
            File.WriteAllBytes(cycleBOutput, [1]);
            using JsonDocument cycleA = CreateInventoryJob(
                editFixture,
                cycleAId,
                "succeeded",
                cycleAOutput,
                video =>
                {
                    video["source"]!["producerJobId"] = cycleBId;
                    video["source"]!["canonicalPath"] = cycleBOutput;
                    video["requested"]!["source"]!["sourceVideoJobId"] = cycleBId;
                },
                job => job["sourceVideoJobId"] = cycleBId);
            using JsonDocument cycleB = CreateInventoryJob(
                editFixture,
                cycleBId,
                "succeeded",
                cycleBOutput,
                video =>
                {
                    video["source"]!["producerJobId"] = cycleAId;
                    video["source"]!["canonicalPath"] = cycleAOutput;
                    video["requested"]!["source"]!["sourceVideoJobId"] = cycleAId;
                },
                job => job["sourceVideoJobId"] = cycleAId);
            const string ambiguousId = "20000000-0000-4000-8000-000000000006";
            string ambiguousAOutput = Path.Combine(videosRoot, "ambiguous-a.mp4");
            string ambiguousBOutput = Path.Combine(videosRoot, "ambiguous-b.mp4");
            File.WriteAllBytes(ambiguousAOutput, [1]);
            File.WriteAllBytes(ambiguousBOutput, [1]);
            using JsonDocument ambiguousA = CreateInventoryJob(
                finishFixture,
                ambiguousId,
                "succeeded",
                ambiguousAOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            using JsonDocument ambiguousB = CreateInventoryJob(
                finishFixture,
                ambiguousId,
                "succeeded",
                ambiguousBOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            string futureOutput = Path.Combine(videosRoot, "future.mp4");
            File.WriteAllBytes(futureOutput, [1]);
            using JsonDocument future = CreateInventoryJob(
                finishFixture,
                "20000000-0000-4000-8000-000000000007",
                "succeeded",
                futureOutput,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                    video["schemaVersion"] = 3;
                });
            using JsonDocument rejectedJobs = JsonDocument.Parse(
                "[" + string.Join(",", new[]
                {
                    missing.RootElement.GetRawText(),
                    failed.RootElement.GetRawText(),
                    running.RootElement.GetRawText(),
                    empty.RootElement.GetRawText(),
                    outside.RootElement.GetRawText(),
                    cycleA.RootElement.GetRawText(),
                    cycleB.RootElement.GetRawText(),
                    ambiguousA.RootElement.GetRawText(),
                    ambiguousB.RootElement.GetRawText(),
                    future.RootElement.GetRawText(),
                }) + "]");
            string[] rejected = window.ResolveVideoToolsV2ManagedInventoryForSmoke(
                rejectedJobs.RootElement,
                out _, out _, out _);
            bool failClosed = rejected.Length == 0;

            const string duplicateOutputAId =
                "30000000-0000-4000-8000-000000000001";
            const string duplicateOutputBId =
                "30000000-0000-4000-8000-000000000002";
            string duplicateOutput = Path.Combine(
                videosRoot,
                "duplicate-output.mp4");
            File.WriteAllBytes(duplicateOutput, [4, 3, 2, 1]);
            string duplicateOutputAliasA = Path.Combine(
                videosRoot,
                "..",
                "2026-08-24",
                "duplicate-output.mp4");
            string duplicateOutputAliasB = duplicateOutput
                .Replace("Videos", "videos", StringComparison.Ordinal)
                .Replace('\\', '/');
            using JsonDocument duplicateOutputA = CreateInventoryJob(
                finishFixture,
                duplicateOutputAId,
                "succeeded",
                duplicateOutputAliasA,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            using JsonDocument duplicateOutputB = CreateInventoryJob(
                finishFixture,
                duplicateOutputBId,
                "succeeded",
                duplicateOutputAliasB,
                video =>
                {
                    video["source"]!["originalCanonicalPath"] = originalPath;
                    video["source"]!["stagingCanonicalPath"] = stagingPath;
                });
            using JsonDocument duplicateOutputJobs = JsonDocument.Parse(
                "[" + duplicateOutputA.RootElement.GetRawText()
                + "," + duplicateOutputB.RootElement.GetRawText() + "]");
            string[] duplicateOutputInventory = window
                .ResolveVideoToolsV2ManagedInventoryForSmoke(
                    duplicateOutputJobs.RootElement,
                    out _, out _, out _);
            bool ambiguousOutputProtected = duplicateOutputInventory.Length == 0
                && !window.RevealResolvedVideoToolsV2OutputForSmoke(
                    duplicateOutputAId,
                    out _,
                    out _)
                && !window.RevealResolvedVideoToolsV2OutputForSmoke(
                    duplicateOutputBId,
                    out _,
                    out _);
            failClosed &= ambiguousOutputProtected;

            _ = window.ResolveVideoToolsV2ManagedInventoryForSmoke(
                exactJobs.RootElement,
                out _, out _, out _);
            bool launched = window.RevealResolvedVideoToolsV2OutputForSmoke(
                finishId,
                out string explorer,
                out string[] arguments);
            bool openOutput = launched
                && string.Equals(explorer, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                && arguments.SequenceEqual(
                    [$"/select,{finishOutput}"],
                    StringComparer.Ordinal);
            bool writerCapabilityExact = writerMutation;
            bool ok = exactInventory && ancestry && failClosed && labelsExact
                && openOutput && passiveRead && writerCapabilityExact;
            succeeded = ok;
            result = new
            {
                ok,
                exactInventory,
                ancestry,
                failClosed,
                ambiguousOutputProtected,
                labels = labelsExact,
                openOutput,
                passiveRead,
                writerCapabilityExact,
                kinds,
                roots,
                outputs,
            };
        }
        catch (Exception ex)
        {
            result = new { ok = false, error = ex.ToString() };
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable(
                "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
                previousOutputRoot);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
        File.WriteAllText(
            fullResultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        try
        {
            if (Directory.Exists(smokeRoot))
                Directory.Delete(smokeRoot, recursive: true);
        }
        catch
        {
        }
        Shutdown(succeeded ? 0 : 1);
    }

    private static JsonDocument CreateInventoryJob(
        JsonElement fixture,
        string id,
        string status,
        string outputPath,
        Action<JsonObject> mutateVideo,
        Action<JsonObject>? mutateJob = null)
    {
        using JsonDocument envelope = CreateVideoToolsV2WorkspaceJob(
            fixture,
            id,
            status,
            mutateVideo,
            mutateJob,
            refreshPresetHash: true);
        JsonObject job = JsonNode.Parse(envelope.RootElement.GetRawText())!
            .AsObject();
        if (status == "succeeded")
        {
            string generatedOutput = job["outputPath"]!.GetValue<string>();
            string outputDirectory = Path.GetFullPath(
                Path.GetDirectoryName(outputPath)
                    ?? throw new InvalidDataException(
                        "Inventory output directory is missing."));
            job["outputPath"] = Path.Combine(
                outputDirectory,
                Path.GetFileName(generatedOutput));
        }
        else
        {
            job["outputPath"] = outputPath;
        }
        return JsonDocument.Parse(job.ToJsonString());
    }

    private static string MaterializeInventoryOutput(
        JsonDocument job,
        byte[] contents)
    {
        string path = job.RootElement.GetProperty("outputPath").GetString()
            ?? throw new InvalidDataException(
                "Inventory output path is missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static JsonDocument CreateManagedFinishInventoryJob(
        JsonElement fixture,
        string id,
        string sourceJobId,
        string sourcePath,
        string outputPath,
        string status)
        => CreateInventoryJob(
            fixture,
            id,
            status,
            outputPath,
            video =>
            {
                JsonObject staged = video["source"]!.AsObject();
                JsonObject signature = staged["stagingSignature"]!
                    .DeepClone().AsObject();
                ApplyInventorySourceSignature(signature, sourcePath);
                video["source"] = new JsonObject
                {
                    ["kind"] = "managed-video-job",
                    ["producerJobId"] = sourceJobId,
                    ["canonicalPath"] = sourcePath,
                    ["signature"] = signature,
                    ["sha256"] = staged["stagingSha256"]!.DeepClone(),
                    ["probe"] = staged["probe"]!.DeepClone(),
                };
                video["requested"]!["source"] = new JsonObject
                {
                    ["kind"] = "managed-video-job",
                    ["sourceVideoJobId"] = sourceJobId,
                };
            },
            job => job["sourceVideoJobId"] = sourceJobId);

    private static void ApplyInventorySourceSignature(
        JsonObject signature,
        string path)
    {
        var info = new FileInfo(path);
        signature["size"] = info.Length;
        signature["mtimeMs"] = new DateTimeOffset(
            info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
    }
}
