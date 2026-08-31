using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private enum ComfyPromptGraphPipeline
    {
        Flux2,
        KreaEdit,
    }

    private sealed record KreaConditioningIdentity(
        string NodeId,
        string ClipNodeId,
        string VaeNodeId,
        string ImageNodeId);

    private const int MaxComfyPromptGraphNodes = 256;
    private const int MaxComfyPromptGraphBytes = 256 * 1024;
    private const int MaxComfyPromptCharacters = 16 * 1024;

    private static PngParametersMetadata? ParseComfyPromptGraph(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || Encoding.UTF8.GetByteCount(raw) > MaxComfyPromptGraphBytes)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement graph = document.RootElement;
            if (graph.ValueKind != JsonValueKind.Object
                || graph.EnumerateObject().Take(MaxComfyPromptGraphNodes + 1).Count()
                    > MaxComfyPromptGraphNodes)
            {
                return null;
            }

            JsonElement? samplerNode = null;
            JsonElement? noiseNode = null;
            JsonElement? guiderNode = null;
            JsonElement? samplerSelectNode = null;
            JsonElement? schedulerNode = null;
            ComfyPromptGraphPipeline? pipeline = null;
            foreach (JsonProperty property in graph.EnumerateObject())
            {
                JsonElement candidate = property.Value;
                if (!NodeHasClass(candidate, "SaveImage")
                    || !TryResolveLinkedNode(
                        graph,
                        candidate,
                        "images",
                        "VAEDecode",
                        out JsonElement decoder)
                    || !TryResolveLinkedNode(
                        graph,
                        decoder,
                        "samples",
                        "SamplerCustomAdvanced",
                        out JsonElement candidateSamplerNode)
                    || !TryResolveLinkedNode(
                        graph,
                        candidateSamplerNode,
                        "noise",
                        "RandomNoise",
                        out JsonElement candidateNoise)
                    || !TryResolveLinkedNode(
                        graph,
                        candidateSamplerNode,
                        "guider",
                        "CFGGuider",
                        out JsonElement candidateGuider)
                    || !TryResolveLinkedNode(
                        graph,
                        candidateSamplerNode,
                        "sampler",
                        "KSamplerSelect",
                        out JsonElement candidateSampler)
                    || !TryResolveLinkedNode(
                        graph,
                        candidateSamplerNode,
                        "sigmas",
                        expectedClass: null,
                        out JsonElement candidateScheduler))
                {
                    continue;
                }

                ComfyPromptGraphPipeline? candidatePipeline =
                    NodeHasClass(candidateScheduler, "Flux2Scheduler")
                        ? ComfyPromptGraphPipeline.Flux2
                        : NodeHasClass(candidateScheduler, "BetaSamplingScheduler")
                            ? ComfyPromptGraphPipeline.KreaEdit
                            : null;
                if (!candidatePipeline.HasValue)
                    continue;

                // Multiple complete output pipelines are ambiguous. Do not
                // guess which branch produced the displayed PNG.
                if (samplerNode.HasValue)
                    return null;
                samplerNode = candidateSamplerNode;
                noiseNode = candidateNoise;
                guiderNode = candidateGuider;
                samplerSelectNode = candidateSampler;
                schedulerNode = candidateScheduler;
                pipeline = candidatePipeline;
            }

            if (!samplerNode.HasValue
                || !noiseNode.HasValue
                || !guiderNode.HasValue
                || !samplerSelectNode.HasValue
                || !schedulerNode.HasValue
                || !pipeline.HasValue
                || !TryResolveConditioningText(
                    graph,
                    guiderNode.Value,
                    "positive",
                    pipeline.Value,
                    out string positive,
                    out KreaConditioningIdentity? positiveIdentity)
                || !TryResolveConditioningText(
                    graph,
                    guiderNode.Value,
                    "negative",
                    pipeline.Value,
                    out string negative,
                    out KreaConditioningIdentity? negativeIdentity)
                || string.IsNullOrWhiteSpace(positive)
                || positive.Length > MaxComfyPromptCharacters
                || negative.Length > MaxComfyPromptCharacters
                || !TryGetInputScalar(noiseNode.Value, "noise_seed", out string seed)
                || !TryGetInputScalar(guiderNode.Value, "cfg", out string cfg)
                || !TryGetInputScalar(samplerSelectNode.Value, "sampler_name", out string sampler)
                || !TryGetInputScalar(schedulerNode.Value, "steps", out string steps)
                || !TryGetInputInt64(schedulerNode.Value, "steps", 1, 1_000, out _)
                || !TryGetInputDouble(guiderNode.Value, "cfg", 0, 100, out _)
                || !TryGetInputInt64(noiseNode.Value, "noise_seed", 0, long.MaxValue, out _)
                || sampler.Length > 128)
            {
                return null;
            }
            if (pipeline.Value == ComfyPromptGraphPipeline.KreaEdit
                && (positiveIdentity is null
                    || negativeIdentity is null
                    || string.Equals(
                        positiveIdentity.NodeId,
                        negativeIdentity.NodeId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        positiveIdentity.ClipNodeId,
                        negativeIdentity.ClipNodeId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        positiveIdentity.VaeNodeId,
                        negativeIdentity.VaeNodeId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        positiveIdentity.ImageNodeId,
                        negativeIdentity.ImageNodeId,
                        StringComparison.Ordinal)
                    || !TryGetLinkedNodeId(
                        guiderNode.Value,
                        "model",
                        out string guiderModelId)
                    || !TryGetLinkedNodeId(
                        schedulerNode.Value,
                        "model",
                        out string schedulerModelId)
                    || !string.Equals(
                        guiderModelId,
                        schedulerModelId,
                        StringComparison.Ordinal)))
            {
                return null;
            }

            string latentClass = pipeline.Value == ComfyPromptGraphPipeline.Flux2
                ? "EmptyFlux2LatentImage"
                : "EmptySD3LatentImage";
            if (!TryResolveLinkedNode(
                    graph,
                    samplerNode.Value,
                    "latent_image",
                    latentClass,
                    out JsonElement latentNode)
                || !TryGetInputInt64(
                    latentNode,
                    "width",
                    1,
                    32_768,
                    out long latentWidth)
                || !TryGetInputInt64(
                    latentNode,
                    "height",
                    1,
                    32_768,
                    out long latentHeight))
            {
                return null;
            }
            if (pipeline.Value == ComfyPromptGraphPipeline.Flux2
                && (!TryGetInputInt64(
                        schedulerNode.Value,
                        "width",
                        1,
                        32_768,
                        out long schedulerWidth)
                    || !TryGetInputInt64(
                        schedulerNode.Value,
                        "height",
                        1,
                        32_768,
                        out long schedulerHeight)
                    || schedulerWidth != latentWidth
                    || schedulerHeight != latentHeight))
            {
                return null;
            }
            string width = latentWidth.ToString(CultureInfo.InvariantCulture);
            string height = latentHeight.ToString(CultureInfo.InvariantCulture);

            if (!TryResolveModelSettings(
                    graph,
                    guiderNode.Value,
                    pipeline.Value,
                    out string? model,
                    out string? lora,
                    out string? loraStrength))
            {
                return null;
            }

            var settings = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Metadata format"] = "ComfyUI prompt graph",
            };

            AddSetting(settings, "Model", model);
            AddSetting(settings, "LoRA", lora ?? "OFF");
            AddSetting(settings, "LoRA strength", loraStrength);

            settings["Steps"] = steps;
            settings["Sampler"] = sampler;
            settings["Scheduler"] = pipeline.Value == ComfyPromptGraphPipeline.Flux2
                ? "Flux2Scheduler"
                : "BetaSamplingScheduler";
            if (pipeline.Value == ComfyPromptGraphPipeline.KreaEdit)
            {
                if (!TryGetInputDouble(
                        schedulerNode.Value,
                        "alpha",
                        0,
                        100,
                        out _)
                    || !TryGetInputDouble(
                        schedulerNode.Value,
                        "beta",
                        0,
                        100,
                        out _)
                    || !TryGetInputScalar(
                        schedulerNode.Value,
                        "alpha",
                        out string alpha)
                    || !TryGetInputScalar(
                        schedulerNode.Value,
                        "beta",
                        out string beta))
                {
                    return null;
                }
                settings["Scheduler alpha"] = alpha;
                settings["Scheduler beta"] = beta;
            }
            settings["CFG scale"] = cfg;
            settings["Seed"] = seed;
            settings["Generation size"] = $"{width} x {height}";
            settings["Output max edge"] = Math.Max(
                    long.Parse(width, CultureInfo.InvariantCulture),
                    long.Parse(height, CultureInfo.InvariantCulture))
                .ToString(CultureInfo.InvariantCulture);

            return new PngParametersMetadata(
                positive.Trim(),
                negative.Trim(),
                settings,
                "ComfyUI prompt graph (structured settings recovered)");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryResolveConditioningText(
        JsonElement graph,
        JsonElement guiderNode,
        string inputName,
        ComfyPromptGraphPipeline pipeline,
        out string text,
        out KreaConditioningIdentity? kreaIdentity)
    {
        text = "";
        kreaIdentity = null;
        if (!TryResolveLinkedNode(
                graph,
                guiderNode,
                inputName,
                expectedClass: null,
                out JsonElement conditioning,
                out string conditioningNodeId))
        {
            return false;
        }

        if (pipeline == ComfyPromptGraphPipeline.KreaEdit)
        {
            return NodeHasClass(conditioning, "TextEncodeKrea2OstrisEdit")
                && TryResolveLinkedNode(
                    graph,
                    conditioning,
                    "clip",
                    "CLIPLoader",
                    out _,
                    out string clipNodeId)
                && TryResolveLinkedNode(
                    graph,
                    conditioning,
                    "vae",
                    "VAELoader",
                    out _,
                    out string vaeNodeId)
                && TryResolveLinkedNode(
                    graph,
                    conditioning,
                    "image1",
                    "LoadImage",
                    out _,
                    out string imageNodeId)
                && TryGetInputString(conditioning, "prompt", out text)
                && SetKreaConditioningIdentity(
                    conditioningNodeId,
                    clipNodeId,
                    vaeNodeId,
                    imageNodeId,
                    out kreaIdentity);
        }

        if (NodeHasClass(conditioning, "ReferenceLatent"))
        {
            if (!TryResolveLinkedNode(
                    graph,
                    conditioning,
                    "conditioning",
                    "CLIPTextEncode",
                    out conditioning))
            {
                return false;
            }
        }
        else if (!NodeHasClass(conditioning, "CLIPTextEncode"))
        {
            return false;
        }

        return TryGetInputString(conditioning, "text", out text);
    }

    private static bool SetKreaConditioningIdentity(
        string nodeId,
        string clipNodeId,
        string vaeNodeId,
        string imageNodeId,
        out KreaConditioningIdentity? identity)
    {
        identity = new KreaConditioningIdentity(
            nodeId,
            clipNodeId,
            vaeNodeId,
            imageNodeId);
        return true;
    }

    private static bool TryResolveModelSettings(
        JsonElement graph,
        JsonElement guiderNode,
        ComfyPromptGraphPipeline pipeline,
        out string? model,
        out string? lora,
        out string? loraStrength)
    {
        model = null;
        lora = null;
        loraStrength = null;
        if (!TryResolveLinkedNode(
                graph,
                guiderNode,
                "model",
                expectedClass: null,
                out JsonElement modelNode))
        {
            return false;
        }

        bool loraEnabled = NodeHasClass(modelNode, "LoraLoaderModelOnly");
        if (loraEnabled)
        {
            if (!TryGetInputString(modelNode, "lora_name", out string loraName)
                || string.IsNullOrWhiteSpace(loraName)
                || loraName.Length > 512
                || !TryGetInputDouble(
                    modelNode,
                    "strength_model",
                    -10,
                    10,
                    out _)
                || !TryGetInputScalar(
                    modelNode,
                    "strength_model",
                    out string strength))
            {
                return false;
            }
            lora = loraName;
            loraStrength = strength;
            if (!TryResolveLinkedNode(
                    graph,
                    modelNode,
                    "model",
                    expectedClass: null,
                    out modelNode))
            {
                return false;
            }
        }

        if (pipeline == ComfyPromptGraphPipeline.KreaEdit)
        {
            if (!loraEnabled
                || !NodeHasClass(modelNode, "Krea2OstrisEditModelPatch")
                || !TryGetInputBoolean(modelNode, "kv_cache", out bool kvCache)
                || !kvCache
                || !TryResolveLinkedNode(
                    graph,
                    modelNode,
                    "model",
                    "UNETLoader",
                    out modelNode))
            {
                return false;
            }
        }

        if (!NodeHasClass(modelNode, "UnetLoaderGGUF")
            && !NodeHasClass(modelNode, "UNETLoader"))
        {
            return false;
        }

        if (TryGetInputString(modelNode, "unet_name", out string unetName))
            model = unetName;
        return !string.IsNullOrWhiteSpace(model) && model.Length <= 512;
    }

    private static bool TryResolveLinkedNode(
        JsonElement graph,
        JsonElement sourceNode,
        string inputName,
        string? expectedClass,
        out JsonElement linkedNode)
        => TryResolveLinkedNode(
            graph,
            sourceNode,
            inputName,
            expectedClass,
            out linkedNode,
            out _);

    private static bool TryResolveLinkedNode(
        JsonElement graph,
        JsonElement sourceNode,
        string inputName,
        string? expectedClass,
        out JsonElement linkedNode,
        out string linkedNodeId)
    {
        linkedNode = default;
        linkedNodeId = "";
        if (!TryGetLinkedNodeId(sourceNode, inputName, out string id))
        {
            return false;
        }
        if (!graph.TryGetProperty(id, out linkedNode)
            || (expectedClass is not null
                && !NodeHasClass(linkedNode, expectedClass)))
        {
            linkedNode = default;
            return false;
        }
        linkedNodeId = id;
        return true;
    }

    private static bool TryGetLinkedNodeId(
        JsonElement sourceNode,
        string inputName,
        out string nodeId)
    {
        nodeId = "";
        if (!TryGetNodeInputs(sourceNode, out JsonElement inputs)
            || !inputs.TryGetProperty(inputName, out JsonElement link)
            || link.ValueKind != JsonValueKind.Array
            || link.GetArrayLength() != 2
            || link[1].ValueKind != JsonValueKind.Number
            || !link[1].TryGetInt32(out int outputIndex)
            || outputIndex != 0)
        {
            return false;
        }

        JsonElement idElement = link[0];
        nodeId = idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? "",
            JsonValueKind.Number => idElement.GetRawText(),
            _ => "",
        };
        return !string.IsNullOrWhiteSpace(nodeId);
    }

    private static bool NodeHasClass(JsonElement node, string expected)
        => node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("class_type", out JsonElement classType)
            && classType.ValueKind == JsonValueKind.String
            && string.Equals(
                classType.GetString(),
                expected,
                StringComparison.Ordinal);

    private static bool TryGetNodeInputs(
        JsonElement node,
        out JsonElement inputs)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("inputs", out inputs)
            && inputs.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        inputs = default;
        return false;
    }

    private static bool TryGetInputString(
        JsonElement node,
        string name,
        out string value)
    {
        value = "";
        if (!TryGetNodeInputs(node, out JsonElement inputs)
            || !inputs.TryGetProperty(name, out JsonElement input)
            || input.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = input.GetString() ?? "";
        return true;
    }

    private static bool TryGetInputScalar(
        JsonElement node,
        string name,
        out string value)
    {
        value = "";
        if (!TryGetNodeInputs(node, out JsonElement inputs)
            || !inputs.TryGetProperty(name, out JsonElement input))
        {
            return false;
        }

        value = input.ValueKind switch
        {
            JsonValueKind.String => input.GetString() ?? "",
            JsonValueKind.Number => input.GetRawText(),
            _ => "",
        };
        return value.Length is > 0 and <= 512
            && !value.Contains('\r')
            && !value.Contains('\n');
    }

    private static bool TryGetInputInt64(
        JsonElement node,
        string name,
        long minimum,
        long maximum,
        out long value)
    {
        value = 0;
        if (!TryGetNodeInputs(node, out JsonElement inputs)
            || !inputs.TryGetProperty(name, out JsonElement input)
            || input.ValueKind != JsonValueKind.Number
            || !input.TryGetInt64(out value))
        {
            return false;
        }
        return value >= minimum && value <= maximum;
    }

    private static bool TryGetInputDouble(
        JsonElement node,
        string name,
        double minimum,
        double maximum,
        out double value)
    {
        value = 0;
        if (!TryGetNodeInputs(node, out JsonElement inputs)
            || !inputs.TryGetProperty(name, out JsonElement input)
            || input.ValueKind != JsonValueKind.Number
            || !input.TryGetDouble(out value)
            || !double.IsFinite(value))
        {
            return false;
        }
        return value >= minimum && value <= maximum;
    }

    private static bool TryGetInputBoolean(
        JsonElement node,
        string name,
        out bool value)
    {
        value = false;
        if (!TryGetNodeInputs(node, out JsonElement inputs)
            || !inputs.TryGetProperty(name, out JsonElement input)
            || input.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = input.GetBoolean();
        return true;
    }

    private static void AddSetting(
        IDictionary<string, string> settings,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            settings[name] = value.Trim();
    }
}
