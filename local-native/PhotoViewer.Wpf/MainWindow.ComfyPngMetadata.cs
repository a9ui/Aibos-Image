using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
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
                        "Flux2Scheduler",
                        out JsonElement candidateScheduler))
                {
                    continue;
                }

                // Multiple complete output pipelines are ambiguous. Do not
                // guess which branch produced the displayed PNG.
                if (samplerNode.HasValue)
                    return null;
                samplerNode = candidateSamplerNode;
                noiseNode = candidateNoise;
                guiderNode = candidateGuider;
                samplerSelectNode = candidateSampler;
                schedulerNode = candidateScheduler;
            }

            if (!samplerNode.HasValue
                || !noiseNode.HasValue
                || !guiderNode.HasValue
                || !samplerSelectNode.HasValue
                || !schedulerNode.HasValue
                || !TryResolveConditioningText(
                    graph,
                    guiderNode.Value,
                    "positive",
                    out string positive)
                || !TryResolveConditioningText(
                    graph,
                    guiderNode.Value,
                    "negative",
                    out string negative)
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

            string? width = null;
            string? height = null;
            if (TryGetInputInt64(
                    schedulerNode.Value,
                    "width",
                    1,
                    32_768,
                    out long schedulerWidth)
                && TryGetInputInt64(
                    schedulerNode.Value,
                    "height",
                    1,
                    32_768,
                    out long schedulerHeight)
                && TryResolveLinkedNode(
                    graph,
                    samplerNode.Value,
                    "latent_image",
                    "EmptyFlux2LatentImage",
                    out JsonElement latentNode)
                && TryGetInputInt64(
                    latentNode,
                    "width",
                    1,
                    32_768,
                    out long latentWidth)
                && TryGetInputInt64(
                    latentNode,
                    "height",
                    1,
                    32_768,
                    out long latentHeight)
                && schedulerWidth == latentWidth
                && schedulerHeight == latentHeight)
            {
                width = schedulerWidth.ToString(CultureInfo.InvariantCulture);
                height = schedulerHeight.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                return null;
            }

            if (!TryResolveModelSettings(
                    graph,
                    guiderNode.Value,
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
            settings["Scheduler"] = "Flux2Scheduler";
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
        out string text)
    {
        text = "";
        if (!TryResolveLinkedNode(
                graph,
                guiderNode,
                inputName,
                expectedClass: null,
                out JsonElement conditioning))
        {
            return false;
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

    private static bool TryResolveModelSettings(
        JsonElement graph,
        JsonElement guiderNode,
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

        if (NodeHasClass(modelNode, "LoraLoaderModelOnly"))
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
    {
        linkedNode = default;
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
        string? id = idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.GetRawText(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id)
            || !graph.TryGetProperty(id, out linkedNode)
            || (expectedClass is not null
                && !NodeHasClass(linkedNode, expectedClass)))
        {
            linkedNode = default;
            return false;
        }
        return true;
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

    private static void AddSetting(
        IDictionary<string, string> settings,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            settings[name] = value.Trim();
    }
}
