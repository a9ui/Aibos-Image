using System.IO;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class WpfLocalPromptPolicy
{
    private const int MaxPolicyBytes = 1_048_576;
    private const int MaxPromptLength = 2_000;
    private const int MaxMappings = 256;
    private const string PolicyEnvironmentVariable =
        "AIBOS_WPF_PROMPT_POLICY_PATH";
    private const string PolicyFileName = "wpf-prompts.local.json";

    internal static WpfLocalPromptPolicyDocument Current { get; } = Load();

    private static WpfLocalPromptPolicyDocument Load()
    {
        string? configured = Environment.GetEnvironmentVariable(
            PolicyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return TryLoad(configured.Trim()) ?? CreateFallback();

        string appCandidate = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            PolicyFileName);
        string currentCandidate = Path.Combine(
            Environment.CurrentDirectory,
            "config",
            PolicyFileName);
        foreach (string candidate in new[] { appCandidate, currentCandidate }
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;
            return TryLoad(candidate) ?? CreateFallback();
        }

        return CreateFallback();
    }

    private static WpfLocalPromptPolicyDocument? TryLoad(string candidate)
    {
        try
        {
            string path = Path.GetFullPath(candidate);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > MaxPolicyBytes)
                return null;

            byte[] bytes = File.ReadAllBytes(path);
            string json = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            WpfLocalPromptPolicyDocument? policy =
                JsonSerializer.Deserialize<WpfLocalPromptPolicyDocument>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            return IsValid(policy) ? policy : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValid(WpfLocalPromptPolicyDocument? policy)
    {
        if (policy is null
            || policy.SchemaVersion != 1
            || policy.Revision < 0
            || policy.Photoreal is null
            || policy.Video is null
            || policy.Mappings is null
            || policy.Mappings.Count > MaxMappings
            || policy.Mappings.Any(static row => row is null))
        {
            return false;
        }

        return IsBounded(policy.Photoreal.Prompt)
            && IsBounded(policy.Photoreal.EmptyPrompt)
            && IsBounded(policy.Photoreal.NegativePrompt)
            && IsBounded(policy.Video.PreservationPreamble)
            && IsBounded(policy.Video.BlankPromptMotion)
            && IsBounded(policy.Video.NegativePrompt);
    }

    private static bool IsBounded(string? value)
        => value is not null && value.Length <= MaxPromptLength;

    private static WpfLocalPromptPolicyDocument CreateFallback()
        => new()
        {
            SchemaVersion = 1,
            Revision = 0,
            Photoreal = new WpfPhotorealPromptPolicy
            {
                Prompt = "Convert the supplied image into a faithful realistic photograph while preserving its visible subject and composition.",
                EmptyPrompt = "Convert the supplied image into a faithful realistic photograph while preserving its visible subject and composition.",
                NegativePrompt = "",
            },
            Video = new WpfVideoPromptPolicy
            {
                PreservationPreamble = "Animate the supplied image while preserving its visible subject and composition.",
                BlankPromptMotion = "Use subtle natural motion.",
                NegativePrompt = "",
            },
            Mappings = [],
        };
}

internal sealed class WpfLocalPromptPolicyDocument
{
    public int SchemaVersion { get; set; }
    public int Revision { get; set; }
    public WpfPhotorealPromptPolicy? Photoreal { get; set; }
    public WpfVideoPromptPolicy? Video { get; set; }
    public List<PhotorealPromptMappingState>? Mappings { get; set; }
}

internal sealed class WpfPhotorealPromptPolicy
{
    public string Prompt { get; set; } = "";
    public string EmptyPrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
}

internal sealed class WpfVideoPromptPolicy
{
    public string PreservationPreamble { get; set; } = "";
    public string BlankPromptMotion { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
}
