using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

public sealed record VideoPromptEnhancement(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("instruction")] string Instruction,
    [property: JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VideoEnrichmentOptions? Options = null)
{
    internal const string ContractId = "PV-ENHANCE-VIDEO-PROMPT-ENHANCEMENT-001";
    internal const string Protocol = "aibos.enhancement-video-prompt-enhancement/v1";
    internal const string ContractIdV2 = "PV-ENHANCE-VIDEO-PROMPT-ENHANCEMENT-002";
    internal const string ProtocolV2 = "aibos.enhancement-video-prompt-enhancement/v2";

    internal static bool IsValid(JsonElement value)
    {
        try { return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number is 1 or 2
            && value.TryGetProperty("instruction", out var instruction)
            && instruction.ValueKind == JsonValueKind.String
            && instruction.GetString() is { Length: > 0 and <= 8000 } text
            && !string.IsNullOrWhiteSpace(text)
            && VideoEnrichmentOptions.WellFormed(text)
            && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            && (number == 1 ? value.EnumerateObject().Count() == 2
                : value.EnumerateObject().Count() == 3 && value.TryGetProperty("options", out var options)
                    && VideoEnrichmentOptions.IsValid(options, text)); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    internal static string? ValidateCapability(JsonElement payload, bool preserving = false)
    {
        string key = preserving ? "videoPromptEnhancementV2" : "videoPromptEnhancementV1";
        bool valid = payload.TryGetProperty("capabilities", out var capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.EnumerateObject().Count(p => p.Name == key) == 1
            && capabilities.TryGetProperty(key, out var capability)
            && capability.ValueKind == JsonValueKind.Object
            && capability.EnumerateObject().Count() == 3
            && capability.TryGetProperty("contractId", out var contract) && contract.ValueKind == JsonValueKind.String && contract.GetString() == (preserving ? ContractIdV2 : ContractId)
            && capability.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == (preserving ? ProtocolV2 : Protocol)
            && capability.TryGetProperty("execution", out var execution) && execution.ValueKind == JsonValueKind.String && execution.GetString() == "before-generation";
        return valid ? null : "処理開始時のAI強化に対応したローカルAIサービスが必要です。更新後に追加してください。";
    }
}

public sealed record VideoEnrichmentChoice(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("alternatives")] string[] Alternatives);

public sealed record VideoEnrichmentOptions(
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("referencePrompt")] string ReferencePrompt,
    [property: JsonPropertyName("dialogue")] string Dialogue,
    [property: JsonPropertyName("speechAmount")] int SpeechAmount,
    [property: JsonPropertyName("music")] string Music,
    [property: JsonPropertyName("physicalContinuity")] bool PhysicalContinuity,
    [property: JsonPropertyName("actionSamples")] string ActionSamples,
    [property: JsonPropertyName("choices")] VideoEnrichmentChoice[] Choices)
{
    internal static bool IsValid(JsonElement value, string instruction)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 8
                || value.EnumerateObject().Select(p => p.Name).Distinct().Count() != 8) return false;
            var p = value.Deserialize<VideoEnrichmentOptions>();
            if (p is null || p.SourceKind is not ("anime" or "photoreal")
                || p.Dialogue is not ("preserve" or "off" or "auto") || p.Music is not ("preserve" or "off" or "auto")
                || p.SpeechAmount is < 1 or > 3 || !Bounded(p.ReferencePrompt, 2000) || !Bounded(p.ActionSamples, 1000)
                || p.Choices is null || p.Choices.Length > 16
                || !value.TryGetProperty("physicalContinuity", out var physics) || physics.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            int end = 0;
            foreach (var c in p.Choices)
            {
                if (c is null || c.Start < end || c.Length < 1 || c.Start > instruction.Length - c.Length
                    || c.Alternatives is null || c.Alternatives.Length is < 2 or > 16
                    || c.Alternatives.Any(s => !Bounded(s, 1000) || string.IsNullOrWhiteSpace(s))
                    || !c.Alternatives.Contains(instruction.Substring(c.Start, c.Length))) return false;
                end = c.Start + c.Length;
            }
            return value.GetProperty("choices").EnumerateArray().All(c => c.ValueKind == JsonValueKind.Object
                && c.EnumerateObject().Count() == 3
                && c.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["start", "length", "alternatives"]));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException) { return false; }
    }
    private static bool Bounded(string? text, int maximum) => text is not null && text.Length <= maximum
        && WellFormed(text)
        && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    internal static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        return true;
    }
}
