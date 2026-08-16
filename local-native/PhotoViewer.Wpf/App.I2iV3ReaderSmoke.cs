using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureI2iV3ReaderSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        try
        {
            string validJson = CreateValidI2iV3JobJson();
            using JsonDocument valid = JsonDocument.Parse(validJson);
            using JsonDocument future = JsonDocument.Parse(validJson.Replace(
                "\"i2iSchemaVersion\":3",
                "\"i2iSchemaVersion\":4",
                StringComparison.Ordinal));
            using JsonDocument promptDrift = JsonDocument.Parse(validJson.Replace(
                "Outfit: Change to a tailored navy jacket.",
                "Outfit: Change to an unverified future garment.",
                StringComparison.Ordinal));
            using JsonDocument wrongAutoExpansion = JsonDocument.Parse(validJson.Replace(
                "\"outfitMaskExpandPixels\":64",
                "\"outfitMaskExpandPixels\":32",
                StringComparison.Ordinal));
            using JsonDocument duplicateStatus = JsonDocument.Parse(validJson.Replace(
                "\"status\":\"succeeded\"",
                "\"status\":\"failed\",\"status\":\"succeeded\"",
                StringComparison.Ordinal));
            using JsonDocument wrongAdapter = JsonDocument.Parse(validJson.Replace(
                "comfyui-flux2-i2i-v3",
                "future-i2i-adapter",
                StringComparison.Ordinal));
            using JsonDocument readyHealth = JsonDocument.Parse(
                CreateI2iV3HealthJson(writerEnabled: true, ready: true, issueCode: null));
            using JsonDocument disabledHealth = JsonDocument.Parse(
                CreateI2iV3HealthJson(
                    writerEnabled: false,
                    ready: false,
                    issueCode: "WORKFLOW_UNVERIFIED"));
            using JsonDocument inconsistentHealth = JsonDocument.Parse(
                CreateI2iV3HealthJson(
                    writerEnabled: false,
                    ready: true,
                    issueCode: null));

            bool validSafe = PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(valid.RootElement);
            bool exactSnapshot = PhotoViewer.Wpf.MainWindow.TryReadI2iV3JobInfoForSmoke(
                    valid.RootElement,
                    out string summary,
                    out int steps,
                    out double cfg)
                && summary == "全体・服装・背景"
                && steps == 11
                && cfg == 1.4d;
            bool workspaceSafe = PhotoViewer.Wpf.MainWindow.TryReadI2iV3WorkspacePresentationForSmoke(
                    valid.RootElement,
                    out string operation,
                    out string presetSummary,
                    out string detailText,
                    out bool supportedMutation,
                    out string[] actions)
                && operation == "i2i"
                && supportedMutation
                && presetSummary.Contains("Schema v3", StringComparison.Ordinal)
                && presetSummary.Contains("STEP 11", StringComparison.Ordinal)
                && detailText.Contains("服装マスク auto 64px", StringComparison.Ordinal)
                && actions.SequenceEqual(
                    [
                        "i2i-v3-rerun",
                        "i2i-v3-rerun-next",
                        "i2i-v3-edit",
                        "open-output",
                        "delete-output",
                    ],
                    StringComparer.Ordinal);
            bool futureProtected = !PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(future.RootElement);
            bool promptDriftProtected = !PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(promptDrift.RootElement);
            bool expansionProtected = !PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(wrongAutoExpansion.RootElement);
            bool duplicateProtected = !PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(duplicateStatus.RootElement);
            bool wrongAdapterProtected = !PhotoViewer.Wpf.MainWindow.IsI2iV3MutationSafeForSmoke(wrongAdapter.RootElement);
            bool managedCandidate = PhotoViewer.Wpf.MainWindow.IsManagedI2iVersionCandidateForSmoke(valid.RootElement);
            bool readyAccepted = PhotoViewer.Wpf.MainWindow.TryParseI2iV3CapabilityForSmoke(
                    readyHealth.RootElement,
                    out bool ready,
                    out string readyIssue)
                && ready
                && readyIssue.Length == 0;
            bool disabledAccepted = PhotoViewer.Wpf.MainWindow.TryParseI2iV3CapabilityForSmoke(
                    disabledHealth.RootElement,
                    out bool disabledReady,
                    out string disabledIssue)
                && !disabledReady
                && disabledIssue == "WORKFLOW_UNVERIFIED";
            bool inconsistentRejected = !PhotoViewer.Wpf.MainWindow.TryParseI2iV3CapabilityForSmoke(
                inconsistentHealth.RootElement,
                out _,
                out _);

            ok = validSafe
                && exactSnapshot
                && workspaceSafe
                && futureProtected
                && promptDriftProtected
                && expansionProtected
                && duplicateProtected
                && wrongAdapterProtected
                && managedCandidate
                && readyAccepted
                && disabledAccepted
                && inconsistentRejected;
            result = new
            {
                ok,
                validSafe,
                exactSnapshot,
                workspaceSafe,
                futureProtected,
                promptDriftProtected,
                expansionProtected,
                duplicateProtected,
                wrongAdapterProtected,
                managedCandidate,
                readyAccepted,
                disabledAccepted,
                inconsistentRejected,
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(ok ? 0 : 1);
    }

    private static string CreateValidI2iV3JobJson()
    {
        const string prompt =
            "Overall: Preserve a clean editorial photographic finish.\n"
            + "Outfit: Change to a tailored navy jacket.\n"
            + "Background: Change to a softly lit neutral studio.";
        string promptSha = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))
            .ToLowerInvariant();
        var options = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["i2iSchemaVersion"] = 3,
            ["overallInstruction"] = "Preserve a clean editorial photographic finish.",
            ["expressionInstruction"] = "",
            ["outfitInstruction"] = "Change to a tailored navy jacket.",
            ["backgroundInstruction"] = "Change to a softly lit neutral studio.",
            ["poseInstruction"] = "",
            ["prompt"] = prompt,
            ["negativePrompt"] = "",
            ["steps"] = 11,
            ["cfgScale"] = 1.4,
            ["maxDimension"] = 1280,
            ["seed"] = 424242,
            ["outfitMaskMode"] = "auto",
            ["outfitMaskExpandPixels"] = 64,
            ["workflowRevision"] = "i2i-flux2-klein9b-unified-v3",
            ["maskRevision"] = "sam31-mediapipe-multiregion-envelope-v3",
            ["promptSnapshotSha256"] = promptSha,
            ["loraEnabled"] = false,
            ["futureField"] = "preserved",
        };
        var preset = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "flux2-i2i-edit-v3",
            ["label"] = "FLUX.2 unified guided edit v3",
            ["modelFamily"] = "photo",
            ["modelName"] = "flux-2-klein-9b-Q4_K_M.gguf",
            ["scale"] = 1,
            ["outputFormat"] = "png",
            ["denoise"] = 0,
            ["sharpen"] = 0,
            ["detail"] = 0,
            ["smoothness"] = 0,
            ["colorBrightness"] = 0,
            ["colorContrast"] = 0,
            ["colorSaturation"] = 0,
            ["options"] = options,
        };
        using JsonDocument presetDocument = JsonDocument.Parse(JsonSerializer.Serialize(preset));
        string presetHash = PhotoViewer.Wpf.MainWindow.ComputeI2iV2PresetHashForSmoke(presetDocument.RootElement);
        var job = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "synthetic-i2i-v3-job",
            ["operation"] = "i2i",
            ["mediaKind"] = "image",
            ["adapterId"] = "comfyui-flux2-i2i-v3",
            ["presetId"] = "flux2-i2i-edit-v3",
            ["presetHash"] = presetHash,
            ["sourceId"] = "C:/synthetic/source.png",
            ["sourcePath"] = "C:/synthetic/source.png",
            ["sourceSignature"] = new Dictionary<string, object?>
            {
                ["size"] = 123,
                ["mtimeMs"] = 456,
            },
            ["status"] = "succeeded",
            ["progress"] = 100,
            ["outputPath"] = "C:/synthetic/Edited/output.png",
            ["createdAt"] = "2026-08-16T00:00:00.000Z",
            ["updatedAt"] = "2026-08-16T00:01:00.000Z",
            ["preset"] = preset,
        };
        return JsonSerializer.Serialize(job);
    }

    private static string CreateI2iV3HealthJson(
        bool writerEnabled,
        bool ready,
        string? issueCode)
    {
        var capability = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contractId"] = "PV-ENHANCE-I2I-003",
            ["operation"] = "i2i",
            ["schemaVersion"] = 3,
            ["readerReady"] = true,
            ["writerEnabled"] = writerEnabled,
            ["backendConfigured"] = true,
            ["ready"] = ready,
            ["directiveKeys"] = new[] { "overall", "expression", "outfit", "background", "pose" },
            ["steps"] = new { min = 4, max = 20, @default = 8 },
            ["cfgScale"] = new { min = 0.5, max = 3.0, @default = 1.0 },
            ["outfitMask"] = new
            {
                modes = new[] { "auto", "manual" },
                manualExpandPixels = new { min = 0, max = 160, @default = 32 },
                automaticExpandPixels = 64,
            },
            ["backendId"] = "comfyui-flux2-i2i-v3",
            ["workflowRevision"] = "i2i-flux2-klein9b-unified-v3",
            ["maskRevision"] = "sam31-mediapipe-multiregion-envelope-v3",
            ["issueCode"] = issueCode,
        };
        return JsonSerializer.Serialize(new
        {
            capabilities = new Dictionary<string, object?>
            {
                ["i2iV3"] = capability,
            },
        });
    }
}
