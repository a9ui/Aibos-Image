using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

// Authoring data is deliberately separate from the H3 wire prompt. No I/O,
// inference, job mutation, or source-media mutation belongs in this compiler.
public sealed class VideoPromptProgram
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool UseSourceVariants { get; set; }
    public string Template { get; set; } = "";
    public string PhotorealTemplate { get; set; } = "";
    public string BaseH3Template { get; set; } = "";
    public string PhotorealBaseH3Template { get; set; } = "";
    public string Description { get; set; } = "";
    public string PhotorealDescription { get; set; } = "";
    public string ActionSamples { get; set; } = "";
    public bool SourceRules { get; set; }
    public bool ImageChoices { get; set; }
    public bool ActionPlot { get; set; }
    public bool PhysicalContinuity { get; set; }
    public string OriginalDefault { get; set; } = "anime";
    public string PreferredLoraId { get; set; } = "";
    public Dictionary<string, VideoPromptOption> Options { get; set; } = new(StringComparer.Ordinal);
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public VideoPromptProgram Clone() => JsonSerializer.Deserialize<VideoPromptProgram>(JsonSerializer.Serialize(this))!;
    public JsonElement Snapshot() => JsonSerializer.SerializeToElement(this);

    public static bool TryRead(JsonElement? element, out VideoPromptProgram program)
    {
        program = new();
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            return true;
        try
        {
            if (element.Value.ValueKind != JsonValueKind.Object || HasDuplicateMembers(element.Value))
                return false;
            program = element.Value.Deserialize<VideoPromptProgram>()!;
            return program is not null && program.Validate(out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool HasDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateMembers(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
                if (HasDuplicateMembers(item)) return true;
        return false;
    }

    public bool Validate(out string error)
    {
        error = "未対応または不正な指示言語の設定です。保存内容は変更しません。";
        if (Version != 1 || OriginalDefault is not ("anime" or "photoreal")
            || !Bounded(Template, 8000) || !Bounded(PhotorealTemplate, 8000)
            || !Bounded(BaseH3Template, 8000) || !Bounded(PhotorealBaseH3Template, 8000)
            || !Bounded(Description, 8000) || !Bounded(PhotorealDescription, 8000) || !Bounded(ActionSamples, 4000)
            || !Bounded(PreferredLoraId, 200) || Options is null || Options.Count > 128)
            return false;
        foreach ((string key, VideoPromptOption option) in Options)
            if (!Bounded(key, 1002) || option is null
                || option.Mode is not ("on" or "off" or "auto")
                || option.Condition is not ("contains" or "absent" or "has-prompt" or "no-prompt")
                || !Bounded(option.Keyword, 200) || option.ChoiceIndex is < 0 or > 15)
                return false;
        // An unfinished template may be saved as a draft, but compilation
        // separately refuses invalid syntax before making an inference call.
        error = "";
        return true;
    }

    private static bool Bounded(string? text, int maximum)
        => text is not null && text.Length <= maximum
            && !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    public string TemplateFor(string sourceKind)
        => sourceKind == "photoreal" && !string.IsNullOrWhiteSpace(PhotorealTemplate)
            ? PhotorealTemplate : Template;

    public string BaseTemplateFor(string sourceKind)
        => sourceKind == "photoreal" && !string.IsNullOrWhiteSpace(PhotorealBaseH3Template)
            ? PhotorealBaseH3Template : BaseH3Template;

    public string DescriptionFor(string sourceKind)
        => UseSourceVariants && sourceKind == "photoreal" ? PhotorealDescription : Description;

    public VideoPromptOption OptionFor(VideoPromptToken token)
        => Options.TryGetValue(token.Key, out VideoPromptOption? option) ? option
            : new() { Mode = token.Kind == '{' ? "auto" : "on" };

    public bool IsOn(VideoPromptOption option, string? sourcePrompt)
    {
        if (option.Mode != "auto" || !SourceRules)
            return option.Mode == "on" || (option.Mode == "auto" && option.DefaultOn);
        // Unknown metadata is not evidence that a word or a prompt is absent.
        if (sourcePrompt is null) return option.DefaultOn;
        return option.Condition switch
        {
            "has-prompt" => !string.IsNullOrWhiteSpace(sourcePrompt),
            "no-prompt" => string.IsNullOrWhiteSpace(sourcePrompt),
            "contains" => option.Keyword.Length > 0 && sourcePrompt.Contains(option.Keyword, StringComparison.OrdinalIgnoreCase),
            "absent" => option.Keyword.Length > 0 && !sourcePrompt.Contains(option.Keyword, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public bool TryCompile(string sourceKind, string? sourcePrompt, int frameCount,
        out string instruction, out string error)
    {
        instruction = "";
        if (!Validate(out error)) return false;
        if (frameCount is not (124 or 243 or 294 or 362))
        {
            error = "未対応のH3秒数です。";
            return false;
        }
        if (!VideoPromptLanguage.TryParse(TemplateFor(sourceKind), out var tokens, out error))
            return false;
        var text = new StringBuilder();
        string baseTemplate = BaseTemplateFor(sourceKind);
        if (!string.IsNullOrWhiteSpace(baseTemplate))
            text.Append(baseTemplate).Append("\n\nAdditional resolved direction:\n");
        int choiceCount = 0;
        foreach (VideoPromptToken token in tokens)
        {
            VideoPromptOption option = OptionFor(token);
            if (token.Kind == 't') text.Append(token.Text);
            else if (token.Kind == '[')
            {
                if (IsOn(option, sourcePrompt))
                {
                    if (token.Choices.Count > 0 && option.ChoiceIndex >= token.Choices.Count)
                    { error = "手動選択肢が編集されています。選び直してください。"; return false; }
                    text.Append(token.Choices.Count > 0 ? token.Choices[option.ChoiceIndex] : token.Text);
                }
            }
            else if (ImageChoices && option.Mode == "auto")
            {
                choiceCount++;
                text.Append(" (Choose exactly one image-supported alternative here: ");
                text.Append(string.Join(" OR ", token.Choices.Select(s => JsonSerializer.Serialize(s))));
                text.Append("; write only the selected action in the final prompt.) ");
            }
            else if (option.Mode != "off")
            {
                if (option.ChoiceIndex >= token.Choices.Count)
                {
                    error = "選択肢が編集されています。色付き部分を押して選び直してください。";
                    return false;
                }
                text.Append(token.Choices[option.ChoiceIndex]);
            }
        }
        if (string.IsNullOrWhiteSpace(baseTemplate) && string.IsNullOrWhiteSpace(text.ToString()))
        {
            error = "有効な指示がありません。本文かONのオプションを入力してください。";
            return false;
        }
        text.Append("\n\nAuthoring policy: The text above is the resolved user direction. Preserve its intent. Do not add alternative actions or editorial explanations to the final H3 prompt.");
        text.Append(sourceKind == "photoreal"
            ? " Use a live-action visual treatment consistent with the reference image."
            : " Preserve the reference image's illustrated or animated visual treatment.");
        if (choiceCount == 0)
            text.Append(" Do not choose additional actions on the user's behalf.");
        if (ActionPlot)
        {
            text.Append(" Plan an image-compatible beginning, development, and finish within ");
            text.Append((frameCount / 24d).ToString("F3", CultureInfo.InvariantCulture));
            text.Append(" seconds. Specify concrete movement timing, with at most three action beats. The following action samples are optional examples, not additional mandatory actions. Retain the resolved user direction and select only compatible details from these samples:\n");
            text.Append(ActionSamples);
        }
        else
            text.Append(" Keep the requested actions; do not invent an additional action plot.");
        if (PhysicalContinuity)
            text.Append("\nPhysical continuity: Treat the reference as the initial frame, not a frozen pose throughout the clip. Preserve visible support, attachment points, and contact constraints. After an explicitly requested and visibly plausible release, allow unsupported soft tissue, fabric, hair, and objects to move continuously under gravity, inertia, and damped settling. If already unsupported and clearly temporarily displaced, continue that existing state smoothly without inventing a release event or an initial velocity direction. Do not force supported parts downward, change anatomy, or snap to a guessed resting pose. If support or displacement is uncertain, add no inferred physical action. Describe any supported transition in time order within the selected duration.");
        instruction = text.ToString();
        if (instruction.Length > 8000 || Encoding.UTF8.GetByteCount(instruction) > 14000)
        {
            instruction = "";
            error = "展開後の指示が長すぎます。本文やアクション例を短くしてください。";
            return false;
        }
        error = SourceRules && sourcePrompt is null
            ? "元画像プロンプトが未取得のため、条件は各オプションの既定値を使います。" : "";
        return true;
    }
}

public sealed class VideoPromptOption
{
    public string Mode { get; set; } = "on";
    public bool DefaultOn { get; set; } = true;
    public string Condition { get; set; } = "contains";
    public string Keyword { get; set; } = "";
    public int ChoiceIndex { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record VideoPromptToken(char Kind, string Text, string Key, IReadOnlyList<string> Choices);

public static class VideoPromptLanguage
{
    public static bool TryParse(string template, out List<VideoPromptToken> tokens, out string error)
    {
        var parsed = new List<VideoPromptToken>();
        tokens = parsed;
        error = "";
        if (template.Length > 8000) { error = "本文は8000文字以内です。"; return false; }
        var literal = new StringBuilder();
        int options = 0;
        void Flush()
        {
            if (literal.Length == 0) return;
            parsed.Add(new('t', literal.ToString(), "", []));
            literal.Clear();
        }
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '\\' && i + 1 < template.Length)
            {
                literal.Append(template[++i]);
                continue;
            }
            if (c is not ('[' or '［' or '{' or '｛'))
            {
                if (c is ']' or '］' or '}' or '｝')
                { error = "対応する開き括弧がありません。文字として使う括弧は \\ でエスケープしてください。"; return false; }
                literal.Append(c);
                continue;
            }
            Flush();
            char kind = c is '[' or '［' ? '[' : '{';
            var body = new StringBuilder();
            bool closed = false;
            for (++i; i < template.Length; i++)
            {
                c = template[i];
                if (c == '\\' && i + 1 < template.Length) { body.Append(template[++i]); continue; }
                if ((kind == '[' && c is ']' or '］') || (kind == '{' && c is '}' or '｝')) { closed = true; break; }
                if (c is '[' or '［' or '{' or '｛' or ']' or '］' or '}' or '｝')
                { error = "オプションの入れ子や異なる種類の括弧は使えません。"; return false; }
                body.Append(c);
            }
            string value = body.ToString().Trim();
            if (!closed || value.Length is 0 or > 1000 || ++options > 128)
            { error = "括弧を閉じ、空でない1000文字以内のオプションを128個以内で指定してください。"; return false; }
            // H3 is a different language: fail explicitly, never consume its
            // reference labels as authoring switches.
            if (kind == '[' && (value.StartsWith("Shot ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Picture ", StringComparison.OrdinalIgnoreCase)))
            { error = "H3の参照記号が含まれています。ここには整形前の指示を入力してください。"; return false; }
            string[] choices = kind == '{' || value.Contains(" / ", StringComparison.Ordinal) || value.Contains(" ／ ", StringComparison.Ordinal)
                ? value.Split(['/', '／'], StringSplitOptions.TrimEntries) : [];
            if (choices.Length > 0 && (choices.Length is < 2 or > 16 || choices.Any(string.IsNullOrWhiteSpace)))
            { error = "{候補 / 候補} は空でない2～16個の選択肢にしてください。"; return false; }
            tokens.Add(new(kind, value, kind + value + (kind == '[' ? ']' : '}'), choices));
        }
        Flush();
        return true;
    }
}
