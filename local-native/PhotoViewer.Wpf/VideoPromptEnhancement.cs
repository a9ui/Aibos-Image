using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

public sealed record VideoPromptEnhancement(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("instruction")] string Instruction)
{
    internal const string ContractId = "PV-ENHANCE-VIDEO-PROMPT-ENHANCEMENT-001";
    internal const string Protocol = "aibos.enhancement-video-prompt-enhancement/v1";

    internal static bool IsValid(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Count() == 2
            && value.TryGetProperty("schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number == 1
            && value.TryGetProperty("instruction", out var instruction)
            && instruction.ValueKind == JsonValueKind.String
            && instruction.GetString() is { Length: > 0 and <= 8000 } text
            && !string.IsNullOrWhiteSpace(text)
            && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    internal static string? ValidateCapability(JsonElement payload)
    {
        bool valid = payload.TryGetProperty("capabilities", out var capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.EnumerateObject().Count(p => p.Name == "videoPromptEnhancementV1") == 1
            && capabilities.TryGetProperty("videoPromptEnhancementV1", out var capability)
            && capability.ValueKind == JsonValueKind.Object
            && capability.EnumerateObject().Count() == 3
            && capability.TryGetProperty("contractId", out var contract) && contract.ValueKind == JsonValueKind.String && contract.GetString() == ContractId
            && capability.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == Protocol
            && capability.TryGetProperty("execution", out var execution) && execution.ValueKind == JsonValueKind.String && execution.GetString() == "before-generation";
        return valid ? null : "処理開始時のAI強化に対応したローカルAIサービスが必要です。更新後に追加してください。";
    }
}
