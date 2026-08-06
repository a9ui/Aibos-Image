using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureI2iReaderSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        try
        {
            using JsonDocument validJob = JsonDocument.Parse(ValidI2iJobJson);
            using JsonDocument blankHairJob = JsonDocument.Parse(
                ValidI2iJobJson.Replace(
                    "\"hairColor\": \"dark auburn\"",
                    "\"hairColor\": \" \"",
                    StringComparison.Ordinal));
            using JsonDocument wrongWorkflowJob = JsonDocument.Parse(
                ValidI2iJobJson.Replace(
                    "i2i-flux2-klein9b-sam31-v1",
                    "future-workflow-v2",
                    StringComparison.Ordinal));
            using JsonDocument wrongHashJob = JsonDocument.Parse(
                ValidI2iJobJson.Replace(
                    "\"presetHash\": \"54b9b0e54119\"",
                    "\"presetHash\": \"000000000000\"",
                    StringComparison.Ordinal));
            using JsonDocument recoveredSourceJob = JsonDocument.Parse(
                ValidI2iJobJson.Replace(
                    "\"preset\": {",
                    "\"sourceRecoveredOutputPath\": \"synthetic.png\",\n  \"preset\": {",
                    StringComparison.Ordinal));
            using JsonDocument unknownOperation = JsonDocument.Parse(
                ValidI2iJobJson.Replace(
                    "\"operation\": \"i2i\"",
                    "\"operation\": \"future-edit\"",
                    StringComparison.Ordinal));
            using JsonDocument readyHealth = JsonDocument.Parse(
                HealthJson(
                    readerReady: true,
                    writerEnabled: true,
                    backendConfigured: true,
                    ready: true,
                    issueCode: null));
            using JsonDocument disabledHealth = JsonDocument.Parse(
                HealthJson(
                    readerReady: true,
                    writerEnabled: false,
                    backendConfigured: false,
                    ready: false,
                    issueCode: "WORKFLOW_UNVERIFIED"));
            using JsonDocument wrongRevisionHealth = JsonDocument.Parse(
                HealthJson(
                    readerReady: true,
                    writerEnabled: true,
                    backendConfigured: true,
                    ready: true,
                    issueCode: null).Replace(
                        "i2i-flux2-klein9b-sam31-v1",
                        "future-workflow-v2",
                        StringComparison.Ordinal));

            bool validSafe = PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                validJob.RootElement);
            bool validReader = string.Equals(
                PhotoViewer.Wpf.MainWindow.ReadEnhancementOperationForI2iSmoke(
                    validJob.RootElement),
                "i2i",
                StringComparison.Ordinal);
            bool blankProtected = !PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                    blankHairJob.RootElement)
                && !string.Equals(
                    PhotoViewer.Wpf.MainWindow.ReadEnhancementOperationForI2iSmoke(
                        blankHairJob.RootElement),
                    "i2i",
                    StringComparison.Ordinal);
            bool revisionProtected = !PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                wrongWorkflowJob.RootElement);
            bool hashProtected = !PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                wrongHashJob.RootElement);
            bool recoveredProtected = !PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                recoveredSourceJob.RootElement);
            bool unknownProtected = !PhotoViewer.Wpf.MainWindow.IsI2iMutationSafeForSmoke(
                unknownOperation.RootElement);
            bool readyAccepted = PhotoViewer.Wpf.MainWindow.TryParseI2iCapabilityForSmoke(
                    readyHealth.RootElement,
                    out bool ready,
                    out _)
                && ready;
            bool disabledParsed = PhotoViewer.Wpf.MainWindow.TryParseI2iCapabilityForSmoke(
                    disabledHealth.RootElement,
                    out bool disabledReady,
                    out string disabledIssue)
                && !disabledReady
                && string.Equals(
                    disabledIssue,
                    "WORKFLOW_UNVERIFIED",
                    StringComparison.Ordinal);
            bool revisionRejected = !PhotoViewer.Wpf.MainWindow.TryParseI2iCapabilityForSmoke(
                wrongRevisionHealth.RootElement,
                out _,
                out _);

            ok = validSafe
                && validReader
                && blankProtected
                && revisionProtected
                && hashProtected
                && recoveredProtected
                && unknownProtected
                && readyAccepted
                && disabledParsed
                && revisionRejected;
            result = new
            {
                ok,
                validSafe,
                validReader,
                blankProtected,
                revisionProtected,
                hashProtected,
                recoveredProtected,
                unknownProtected,
                readyAccepted,
                disabledParsed,
                disabledIssue,
                revisionRejected,
            };
        }
        catch (Exception ex)
        {
            result = new { ok = false, message = ex.ToString() };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(ok ? 0 : 1);
    }

    private static string HealthJson(
        bool readerReady,
        bool writerEnabled,
        bool backendConfigured,
        bool ready,
        string? issueCode)
        => $$"""
        {
          "capabilities": {
            "i2i": {
              "contractId": "PV-ENHANCE-I2I-001",
              "operation": "i2i",
              "readerReady": {{readerReady.ToString().ToLowerInvariant()}},
              "writerEnabled": {{writerEnabled.ToString().ToLowerInvariant()}},
              "backendConfigured": {{backendConfigured.ToString().ToLowerInvariant()}},
              "ready": {{ready.ToString().ToLowerInvariant()}},
              "supportedTargets": ["hair-color"],
              "backendId": "comfyui-flux2-i2i",
              "workflowRevision": "i2i-flux2-klein9b-sam31-v1",
              "maskRevision": "sam31-hair-mediapipe-face-v1"{{(issueCode is null ? "" : $",\n      \"issueCode\": \"{issueCode}\"")}}
            }
          }
        }
        """;

    private const string ValidI2iJobJson = """
        {
          "operation": "i2i",
          "mediaKind": "image",
          "adapterId": "comfyui-flux2-i2i",
          "presetId": "flux2-i2i-hair-v1",
          "presetHash": "54b9b0e54119",
          "sourceId": "C:/synthetic/source.png",
          "sourcePath": "C:/synthetic/source.png",
          "sourceSignature": {
            "size": 123,
            "mtimeMs": 456
          },
          "preset": {
            "id": "flux2-i2i-hair-v1",
            "label": "FLUX.2 hair-color edit v1",
            "modelFamily": "photo",
            "modelName": "flux-2-klein-9b-Q4_K_M.gguf",
            "scale": 1,
            "outputFormat": "png",
            "denoise": 0,
            "sharpen": 0,
            "detail": 0,
            "smoothness": 0,
            "colorBrightness": 0,
            "colorContrast": 0,
            "colorSaturation": 0,
            "options": {
              "i2iSchemaVersion": 1,
              "target": "hair-color",
              "hairColor": "dark auburn",
              "details": "retain the existing highlights",
              "preserveIdentity": true,
              "prompt": "Change only the hair color to dark auburn. Hair-color details: retain the existing highlights. Retain the same recognizable person, face geometry and proportions, expression, gaze, age, skin appearance, and every unrequested facial detail. Retain the existing hairstyle, hair length and shape, pose, body proportions, hands, camera, crop, background, lighting, visual medium, and every other unrequested detail.",
              "negativePrompt": "",
              "steps": 8,
              "cfgScale": 1,
              "maxDimension": 1280,
              "seed": 42,
              "workflowRevision": "i2i-flux2-klein9b-sam31-v1",
              "maskRevision": "sam31-hair-mediapipe-face-v1",
              "loraEnabled": false,
              "futureField": "preserved"
            }
          }
        }
        """;
}
