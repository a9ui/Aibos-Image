using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureI2iV2ReaderSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        try
        {
            string validJson = CreateValidI2iV2JobJson();
            using JsonDocument validJob = JsonDocument.Parse(validJson);
            using JsonDocument unknownTarget = JsonDocument.Parse(
                validJson.Replace(
                    "\"target\":\"outfit\"",
                    "\"target\":\"future-target\"",
                    StringComparison.Ordinal));
            using JsonDocument futureSchema = JsonDocument.Parse(
                validJson.Replace(
                    "\"i2iSchemaVersion\":2",
                    "\"i2iSchemaVersion\":3",
                    StringComparison.Ordinal));
            using JsonDocument wrongAdapter = JsonDocument.Parse(
                validJson.Replace(
                    "comfyui-flux2-i2i-v2",
                    "future-i2i-adapter",
                    StringComparison.Ordinal));
            using JsonDocument promptDrift = JsonDocument.Parse(
                validJson.Replace(
                    "derived-prompt-fixture",
                    "tampered-derived-prompt-fixture",
                    StringComparison.Ordinal));
            using JsonDocument wrongHash = JsonDocument.Parse(
                validJson.Replace(
                    "\"presetHash\":\"",
                    "\"presetHash\":\"000000000000\",\"ignoredOriginalHash\":\"",
                    StringComparison.Ordinal));
            using JsonDocument duplicateStatus = JsonDocument.Parse(
                validJson.Replace(
                    "\"status\":\"queued\"",
                    "\"status\":\"failed\",\"status\":\"queued\"",
                    StringComparison.Ordinal));
            using JsonDocument malformedWithPrivateError = JsonDocument.Parse(
                validJson.Replace(
                    "\"status\":\"queued\",",
                    "\"status\":\"failed\",\"status\":\"queued\",\"errorMessage\":\"derived-prompt-fixture\",",
                    StringComparison.Ordinal));
            using JsonDocument duplicateOutputPath = JsonDocument.Parse(
                validJson.Replace(
                    "\"status\":\"queued\",",
                    "\"status\":\"queued\",\"outputPath\":\"C:/synthetic/one.png\",\"outputPath\":\"C:/synthetic/two.png\",",
                    StringComparison.Ordinal));
            using JsonDocument succeededV2 = JsonDocument.Parse(
                validJson.Replace(
                    "\"status\":\"queued\",",
                    "\"status\":\"succeeded\",\"outputPath\":\"C:/synthetic/Edited/output.png\",",
                    StringComparison.Ordinal));
            using JsonDocument failedWithPrivateError = JsonDocument.Parse(
                validJson.Replace(
                    "\"status\":\"queued\",",
                    "\"status\":\"failed\",\"errorMessage\":\"derived-prompt-fixture\",",
                    StringComparison.Ordinal));
            using JsonDocument validV1Job = JsonDocument.Parse(ValidI2iJobJson);

            using JsonDocument readyHealth = JsonDocument.Parse(
                CreateI2iV2HealthJson(
                    writerEnabled: true,
                    supportedTargets: ["outfit", "expression", "background", "pose"],
                    issueCode: null));
            using JsonDocument partialHealth = JsonDocument.Parse(
                CreateI2iV2HealthJson(
                    writerEnabled: true,
                    supportedTargets: ["outfit", "expression", "background"],
                    issueCode: "POSE_UNAVAILABLE"));
            using JsonDocument disabledHealth = JsonDocument.Parse(
                CreateI2iV2HealthJson(
                    writerEnabled: false,
                    supportedTargets: ["outfit", "expression", "background", "pose"],
                    issueCode: "WRITER_DISABLED"));
            using JsonDocument duplicateTargetHealth = JsonDocument.Parse(
                CreateI2iV2HealthJson(
                    writerEnabled: true,
                    supportedTargets: ["outfit", "outfit"],
                    issueCode: null));

            bool validSafe = PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(
                validJob.RootElement);
            bool summarySafe = PhotoViewer.Wpf.MainWindow.TryReadI2iV2JobInfoForSmoke(
                    validJob.RootElement,
                    out int schemaVersion,
                    out string target,
                    out string instructionSummary)
                && schemaVersion == 2
                && string.Equals(target, "outfit", StringComparison.Ordinal)
                && instructionSummary.Contains(
                    "opaque dark teal athletic top",
                    StringComparison.Ordinal)
                && !instructionSummary.Contains(
                    "derived-prompt-fixture",
                    StringComparison.Ordinal);
            bool workspacePresentationSafe =
                PhotoViewer.Wpf.MainWindow.TryReadI2iV2WorkspacePresentationForSmoke(
                    validJob.RootElement,
                    out string workspaceOperation,
                    out string presetSummary,
                    out string detailText,
                    out bool supportedMutation)
                && string.Equals(workspaceOperation, "i2i", StringComparison.Ordinal)
                && supportedMutation
                && presetSummary.Contains("Schema v2", StringComparison.Ordinal)
                && presetSummary.Contains("服装", StringComparison.Ordinal)
                && detailText.Contains(
                    "opaque dark teal athletic top",
                    StringComparison.Ordinal)
                && !detailText.Contains(
                    "derived-prompt-fixture",
                    StringComparison.Ordinal);
            bool futureWorkspaceProtected =
                PhotoViewer.Wpf.MainWindow.TryReadI2iV2WorkspacePresentationForSmoke(
                    futureSchema.RootElement,
                    out string futureOperation,
                    out _,
                    out string futureDetail,
                    out bool futureSupportedMutation)
                && string.Equals(
                    futureOperation,
                    "unsupported",
                    StringComparison.Ordinal)
                && !futureSupportedMutation
                && !futureDetail.Contains("Schema v2", StringComparison.Ordinal)
                && !futureDetail.Contains("schema v2", StringComparison.Ordinal);
            bool failedErrorPrivacySafe =
                PhotoViewer.Wpf.MainWindow.TryReadI2iV2WorkspacePresentationForSmoke(
                    failedWithPrivateError.RootElement,
                    out string failedOperation,
                    out _,
                    out string failedDetail,
                    out bool failedSupportedMutation)
                && string.Equals(failedOperation, "i2i", StringComparison.Ordinal)
                && failedSupportedMutation
                && failedDetail.Contains(
                    "opaque dark teal athletic top",
                    StringComparison.Ordinal)
                && !failedDetail.Contains(
                    "derived-prompt-fixture",
                    StringComparison.Ordinal);
            bool malformedErrorPrivacySafe =
                PhotoViewer.Wpf.MainWindow.TryReadI2iV2WorkspacePresentationForSmoke(
                    malformedWithPrivateError.RootElement,
                    out string malformedOperation,
                    out _,
                    out string malformedDetail,
                    out bool malformedSupportedMutation)
                && string.Equals(
                    malformedOperation,
                    "unsupported",
                    StringComparison.Ordinal)
                && !malformedSupportedMutation
                && !malformedDetail.Contains(
                    "derived-prompt-fixture",
                    StringComparison.Ordinal);
            bool managedVersionCandidatesSafe =
                PhotoViewer.Wpf.MainWindow.IsManagedI2iVersionCandidateForSmoke(
                    validV1Job.RootElement)
                && PhotoViewer.Wpf.MainWindow.IsManagedI2iVersionCandidateForSmoke(
                    succeededV2.RootElement)
                && !PhotoViewer.Wpf.MainWindow.IsManagedI2iVersionCandidateForSmoke(
                    futureSchema.RootElement);
            bool unknownTargetProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(unknownTarget.RootElement);
            bool futureSchemaProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(futureSchema.RootElement);
            bool wrongAdapterProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(wrongAdapter.RootElement);
            bool promptDriftProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(promptDrift.RootElement);
            bool wrongHashProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(wrongHash.RootElement);
            bool duplicateStatusProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(
                    duplicateStatus.RootElement);
            bool duplicateOutputPathProtected =
                !PhotoViewer.Wpf.MainWindow.IsI2iV2MutationSafeForSmoke(
                    duplicateOutputPath.RootElement);
            bool readyAccepted = PhotoViewer.Wpf.MainWindow.TryParseI2iV2CapabilityForSmoke(
                    readyHealth.RootElement,
                    "pose",
                    out bool poseReady,
                    out _)
                && poseReady;
            bool partialOutfitParsed =
                PhotoViewer.Wpf.MainWindow.TryParseI2iV2CapabilityForSmoke(
                    partialHealth.RootElement,
                    "outfit",
                    out bool outfitReady,
                    out _);
            bool partialPoseParsed =
                PhotoViewer.Wpf.MainWindow.TryParseI2iV2CapabilityForSmoke(
                    partialHealth.RootElement,
                    "pose",
                    out bool missingPoseReady,
                    out string partialIssue);
            bool targetGateAccepted = partialOutfitParsed
                && outfitReady
                && partialPoseParsed
                && !missingPoseReady
                && string.Equals(
                    partialIssue,
                    "POSE_UNAVAILABLE",
                    StringComparison.Ordinal);
            bool writerGateAccepted = PhotoViewer.Wpf.MainWindow.TryParseI2iV2CapabilityForSmoke(
                    disabledHealth.RootElement,
                    "outfit",
                    out bool disabledReady,
                    out string disabledIssue)
                && !disabledReady
                && string.Equals(
                    disabledIssue,
                    "WRITER_DISABLED",
                    StringComparison.Ordinal);
            bool duplicateTargetRejected =
                !PhotoViewer.Wpf.MainWindow.TryParseI2iV2CapabilityForSmoke(
                    duplicateTargetHealth.RootElement,
                    "outfit",
                    out _,
                    out _);

            ok = validSafe
                && summarySafe
                && workspacePresentationSafe
                && futureWorkspaceProtected
                && failedErrorPrivacySafe
                && malformedErrorPrivacySafe
                && managedVersionCandidatesSafe
                && unknownTargetProtected
                && futureSchemaProtected
                && wrongAdapterProtected
                && promptDriftProtected
                && wrongHashProtected
                && duplicateStatusProtected
                && duplicateOutputPathProtected
                && readyAccepted
                && targetGateAccepted
                && writerGateAccepted
                && duplicateTargetRejected;
            result = new
            {
                ok,
                validSafe,
                summarySafe,
                workspacePresentationSafe,
                futureWorkspaceProtected,
                failedErrorPrivacySafe,
                malformedErrorPrivacySafe,
                managedVersionCandidatesSafe,
                unknownTargetProtected,
                futureSchemaProtected,
                wrongAdapterProtected,
                promptDriftProtected,
                wrongHashProtected,
                duplicateStatusProtected,
                duplicateOutputPathProtected,
                readyAccepted,
                targetGateAccepted,
                writerGateAccepted,
                duplicateTargetRejected,
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
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(ok ? 0 : 1);
    }

    private static string CreateValidI2iV2JobJson()
    {
        const string prompt = "derived-prompt-fixture";
        string promptSha = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))
            .ToLowerInvariant();
        var options = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["i2iSchemaVersion"] = 2,
            ["target"] = "outfit",
            ["instruction"] = "Change the outfit to an opaque dark teal athletic top.",
            ["details"] = "Keep the existing garment silhouette.",
            ["preserveIdentity"] = true,
            ["preserveComposition"] = true,
            ["prompt"] = prompt,
            ["negativePrompt"] = "",
            ["steps"] = 8,
            ["cfgScale"] = 1,
            ["maxDimension"] = 1280,
            ["seed"] = 42,
            ["workflowRevision"] = "i2i-flux2-klein9b-multitarget-v2",
            ["maskRevision"] = "sam31-mediapipe-target-v2",
            ["promptPolicyRevision"] = "i2i-prompt-policy-v2-fixture",
            ["promptPolicySha256"] = new string('a', 64),
            ["promptSnapshotSha256"] = promptSha,
            ["loraEnabled"] = false,
            ["futureField"] = "preserved",
        };
        var preset = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "flux2-i2i-edit-v2",
            ["label"] = "FLUX.2 guided edit v2",
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
        using JsonDocument presetDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(preset));
        string presetHash = PhotoViewer.Wpf.MainWindow.ComputeI2iV2PresetHashForSmoke(
            presetDocument.RootElement);
        var job = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "synthetic-i2i-v2-job",
            ["operation"] = "i2i",
            ["mediaKind"] = "image",
            ["adapterId"] = "comfyui-flux2-i2i-v2",
            ["presetId"] = "flux2-i2i-edit-v2",
            ["presetHash"] = presetHash,
            ["sourceId"] = "C:/synthetic/source.png",
            ["sourcePath"] = "C:/synthetic/source.png",
            ["sourceSignature"] = new Dictionary<string, object?>
            {
                ["size"] = 123,
                ["mtimeMs"] = 456,
            },
            ["status"] = "queued",
            ["progress"] = 0,
            ["createdAt"] = "2026-08-06T00:00:00.000Z",
            ["updatedAt"] = "2026-08-06T00:00:00.000Z",
            ["preset"] = preset,
        };
        return JsonSerializer.Serialize(job);
    }

    private static string CreateI2iV2HealthJson(
        bool writerEnabled,
        string[] supportedTargets,
        string? issueCode)
    {
        var capability = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contractId"] = "PV-ENHANCE-I2I-002",
            ["operation"] = "i2i",
            ["schemaVersion"] = 2,
            ["readerReady"] = true,
            ["writerEnabled"] = writerEnabled,
            ["backendConfigured"] = true,
            ["promptPolicyConfigured"] = true,
            ["ready"] = writerEnabled,
            ["supportedTargets"] = supportedTargets,
            ["backendId"] = "comfyui-flux2-i2i-v2",
            ["workflowRevision"] = "i2i-flux2-klein9b-multitarget-v2",
            ["maskRevision"] = "sam31-mediapipe-target-v2",
            ["promptPolicyRevision"] = "i2i-prompt-policy-v2-fixture",
            ["promptPolicySha256"] = new string('a', 64),
            ["issueCode"] = issueCode,
        };
        return JsonSerializer.Serialize(new
        {
            capabilities = new Dictionary<string, object?>
            {
                ["i2iV2"] = capability,
            },
        });
    }
}
