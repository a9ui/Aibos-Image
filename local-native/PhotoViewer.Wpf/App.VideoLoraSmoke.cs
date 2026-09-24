using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class App
{
    private static async Task VerifyVideoLoraFlowAsync(MainWindow window, string root,
        Dictionary<string, bool> checks, Action<string, FrameworkElement> capture)
    {
        using (var fixtureStream = typeof(App).Assembly.GetManifestResourceStream("VideoLoraFixtures.json")!)
        using (var fixtures = JsonDocument.Parse(fixtureStream))
            foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
                checks["loraProtocol_" + item.GetProperty("name").GetString()] =
                    VideoLoraSelection.TryReadList(item.GetProperty("value"), out _) == item.GetProperty("accepted").GetBoolean();

        string directory = Path.Combine(root, "video-loras"); Directory.CreateDirectory(directory);
        string Create(string name, byte fill)
        {
            byte[] header = Encoding.UTF8.GetBytes("{\"layer.lora_A.weight\":{\"dtype\":\"F32\",\"shape\":[1,2],\"data_offsets\":[0,8]},\"layer.lora_B.weight\":{\"dtype\":\"F32\",\"shape\":[2,1],\"data_offsets\":[8,16]}}");
            using var stream = File.Create(Path.Combine(directory, name));
            stream.Write(BitConverter.GetBytes((ulong)header.Length)); stream.Write(header); stream.Write(Enumerable.Repeat(fill, 16).ToArray());
            return name;
        }
        string first = Create("Camera_motion.safetensors", 0), second = Create("Natural_movement.safetensors", 1);
        int enqueues = 0, unexpectedWrites = 0;
        bool supports = true, changeDuringHealth = false;
        JsonElement requested = default;
        window.ConfigureModalEnhancementForSmoke(async (request, token) =>
        {
            string route = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && route == "/api/enhance/health")
            {
                var health = JsonNode.Parse(CreateVideoV2HealthJson(true, true, "ready", null))!;
                if (supports) health["capabilities"]!["videoLoraV1"] = JsonSerializer.SerializeToNode(new {
                    contractId = "PV-ENHANCE-VIDEO-LORA-001", protocol = "aibos.enhancement-video-lora/v1",
                    maximumCount = 8, maximumBytes = VideoLoraSelection.MaximumBytes,
                    minimumStrength = -2, maximumStrength = 2, workflowRevision = VideoLoraSelection.Workflow });
                if (changeDuringHealth) { changeDuringHealth = false; window.ConfigureVideoLoraRowForSmoke(0, true, 0.65); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(health.ToJsonString()) };
            }
            if (request.Method == HttpMethod.Post && route == "/api/enhance/jobs")
            {
                enqueues++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                requested = body.RootElement.GetProperty("video").GetProperty("requested").Clone();
                return BuildVideoToolsV2FlowAcceptedResponse(request, "synthetic-lora-" + enqueues);
            }
            if (request.Method == HttpMethod.Get && route == "/api/enhance/jobs")
                return JsonResponse(HttpStatusCode.OK, new { jobs = Array.Empty<object>() });
            if (request.Method != HttpMethod.Get) unexpectedWrites++;
            return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected route" });
        });
        window.OpenVideoGenerationBoardForSmoke("original");
        window.SetVideoEnhanceAtExecutionForSmoke(false);
        await window.SetVideoLorasForSmoke(directory, first, second);
        window.ConfigureVideoLoraRowForSmoke(1, true, 0.75, moveUp: true);
        window.ExpandVideoLorasForSmoke();
        window.CaptureVideoVariantForSmoke((_, visual) => capture("video-lora-menu", visual));
        checks["loraFolderAndControlsArePassive"] = enqueues == 0 && unexpectedWrites == 0;
        checks["multipleLorasCaptureStrengthAndOrder"] = await window.SubmitVideoGenerationForSmokeAsync()
            && requested.GetProperty("loras").GetArrayLength() == 2
            && requested.GetProperty("loras")[0].GetProperty("fileName").GetString() == second
            && requested.GetProperty("loras")[0].GetProperty("strength").GetDouble() == 0.75
            && requested.GetProperty("loras")[1].GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, first))));
        window.ConfigureVideoLoraRowForSmoke(0, false, 0.75);
        checks["disabledLoraIsOmitted"] = await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 2
            && requested.GetProperty("loras").GetArrayLength() == 1;
        window.ConfigureVideoLoraRowForSmoke(1, true, 0);
        checks["allDisabledRetainsLegacyRequest"] = await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3 && !requested.TryGetProperty("loras", out _);
        window.ConfigureVideoLoraRowForSmoke(0, true, 0.75);
        supports = false;
        checks["oldCompanionCannotSilentlyDropLora"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3;
        supports = true; changeDuringHealth = true;
        checks["loraChangeDuringPreparationCancelsSubmission"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3;
        await window.SetVideoLorasForSmoke(directory, first, first);
        checks["duplicateLoraContentCannotEnqueue"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3;
        await window.SetVideoLorasForSmoke(directory, "missing.safetensors");
        checks["missingLoraCannotEnqueue"] = !await window.SubmitVideoGenerationForSmokeAsync() && enqueues == 3;
        checks["loraSelectionPreservesSourceFiles"] = Directory.GetFiles(directory).Length == 2 && unexpectedWrites == 0;
        await window.SetVideoLorasForSmoke(directory);
    }
}
