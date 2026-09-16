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
    // The template is a lossless annotation of the complete base H3 body.
    // Manual selections can be resolved locally; image-driven enhancement is
    // a separate explicit choice and never a prerequisite for enqueue.
    public bool AnnotatedH3 { get; set; }
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
    public string OpeningMotionId { get; set; } = "original";
    public string ArmMotionId { get; set; } = "original";
    public string ExpressionId { get; set; } = "original";
    public string MoodId { get; set; } = "original";
    public string PositionId { get; set; } = "original";
    public string GazeId { get; set; } = "original";
    public string CameraMotionId { get; set; } = "original";
    public string CaptureModeId { get; set; } = "original";
    public List<VideoDirectionPhase> DirectionPhases { get; set; } = [];
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
        if (!VideoSubjectDirection.IsValid(this) || Version != 1 || OriginalDefault is not ("anime" or "photoreal")
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
            else if (!Bounded(option.Label, 120) || !Bounded(option.Group, 120)
                || !Bounded(option.DirectionAspect, 16)
                || !Bounded(option.DirectionReplacement, 1000)
                || (option.DirectionAspect.Length == 0 && option.DirectionReplacement.Length > 0)
                || option.DirectionAspect is not ("" or "opening" or "arms" or "expression" or "mood" or "camera" or "capture" or "position" or "gaze")
                || option.Category is not ("" or "camera" or "action" or "expression" or "viewpoint" or "ending" or "sound" or "detail")
                || option.ChoiceLabels is null || option.ChoiceLabels.Count > 16
                || option.ChoiceLabels.Any(label => !Bounded(label, 120)))
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

    public void SetManualOption(VideoPromptToken token, VideoPromptOption option, string sourceKind)
    {
        // Mutually exclusive alternatives are resolved only by an explicit
        // selection. Importing a document never silently changes its defaults.
        if (option.Mode == "on" && option.Group.Length > 0
            && VideoPromptLanguage.TryParse(TemplateFor(sourceKind), out var active, out _))
            foreach (var pair in Options)
                if (pair.Key != token.Key && pair.Value.Group == option.Group && active.Any(t => t.Key == pair.Key))
                    pair.Value.Mode = "off";
        Options[token.Key] = option;
    }

    public bool TryGetUnchangedH3(string kind, out string prompt)
    {
        prompt = "";
        if (VideoSubjectDirection.IsSelected(this) || !Enabled || !AnnotatedH3 || SourceRules || ImageChoices || ActionPlot || PhysicalContinuity
            || !Validate(out _) || !VideoPromptLanguage.TryParse(TemplateFor(kind), out var tokens, out _))
            return false;
        var resolved = new StringBuilder();
        foreach (VideoPromptToken token in tokens)
        {
            if (token.Kind == 't') { resolved.Append(token.Text); continue; }
            var option = OptionFor(token);
            if (token.Kind != '[' || option.Mode == "auto") return false;
            if (option.Mode == "off") continue;
            if (token.Choices.Count > 0 && option.ChoiceIndex >= token.Choices.Count) return false;
            resolved.Append(token.Choices.Count == 0 ? token.Text : token.Choices[option.ChoiceIndex]);
        }
        string original = BaseTemplateFor(kind);
        if (string.IsNullOrWhiteSpace(original) || resolved.ToString() != original) return false;
        prompt = original;
        return true;
    }

    public static string DirectionAspectFor(VideoPromptOption option)
        => option.DirectionAspect.Length > 0 ? option.DirectionAspect : option.Category == "camera" ? "camera" : "";

    public bool IsDirectionReplaced(VideoPromptOption option)
        => DirectionAspectFor(option) switch
        {
            "opening" => OpeningMotionId != "original",
            "capture" => CaptureModeId != "original",
            "camera" or "arms" or "expression" or "mood" or "position" or "gaze" => VideoDirectionTimeline.Overrides(this, DirectionAspectFor(option)),
            _ => false,
        };

    public void RestoreOriginalDirection(string aspect)
    {
        foreach (var phase in DirectionPhases) phase.Set(aspect, "original");
        switch (aspect)
        {
            case "opening": OpeningMotionId = "original"; break;
            case "arms": ArmMotionId = "original"; break;
            case "expression": ExpressionId = "original"; break;
            case "mood": MoodId = "original"; break;
            case "camera": CameraMotionId = "original"; break;
            case "capture": CaptureModeId = "original"; break;
            case "position": PositionId = "original"; break;
            case "gaze": GazeId = "original"; break;
        }
    }

    public bool IsOn(VideoPromptOption option, string? sourcePrompt, bool ignoreDirection = false)
    {
        if (!ignoreDirection && IsDirectionReplaced(option) && option.DirectionReplacement.Length == 0) return false;
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

    private Dictionary<string, string> OriginalDirections(string kind, string? sourcePrompt)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!VideoPromptLanguage.TryParse(TemplateFor(kind), out var tokens, out _)) return result;
        foreach (var token in tokens.Where(t => t.Kind != 't'))
        {
            var option = OptionFor(token);
            string aspect = DirectionAspectFor(option);
            if (aspect.Length == 0 || !IsOn(option, sourcePrompt, ignoreDirection: true)
                || token.Choices.Count > 0 && option.ChoiceIndex >= token.Choices.Count) continue;
            string text = token.Choices.Count > 0 ? token.Choices[option.ChoiceIndex] : token.Text;
            if (aspect == "camera") text = VideoDirectionTimeline.ApplyCaptureOverride(text, this);
            result[aspect] = result.TryGetValue(aspect, out string? previous) ? previous + " " + text : text;
        }
        return result;
    }

    public bool TryResolveH3(string kind, string? sourcePrompt, out string prompt, out string error, int durationMs = 15083)
    {
        prompt = "";
        if (!Validate(out error) || !VideoPromptLanguage.TryParse(TemplateFor(kind), out var tokens, out error)) return false;
        var resolved = new StringBuilder();
        foreach (VideoPromptToken token in tokens)
        {
            if (token.Kind == 't') { resolved.Append(token.Text); continue; }
            VideoPromptOption option = OptionFor(token);
            if ((IsDirectionReplaced(option) && option.DirectionReplacement.Length == 0) || (token.Kind == '[' ? !IsOn(option, sourcePrompt) : option.Mode == "off")) continue;
            if (token.Choices.Count > 0 && option.ChoiceIndex >= token.Choices.Count)
            { error = "候補が編集されています。色付きの部分から選び直してください。"; return false; }
            // With enhancement off, image choices use the selected/default
            // alternative. No AI instructions or optional planning are emitted.
            string selected = IsDirectionReplaced(option) ? option.DirectionReplacement
                : token.Choices.Count > 0 ? token.Choices[option.ChoiceIndex] : token.Text;
            resolved.Append(DirectionAspectFor(option) == "camera" ? VideoDirectionTimeline.ApplyCaptureOverride(selected, this) : selected);
        }
        string body = resolved.ToString().Trim();
        if (body.Length == 0) { error = "本文か使用する候補を入力してください。"; return false; }
        string baseline = AnnotatedH3 ? "" : BaseTemplateFor(kind).Trim();
        if (baseline.Length > 0)
        {
            if (!VideoPromptSections.TryInsertVisual(baseline, body, out body, out error)) return false;
        }
        if (!body.Contains(MiniMaxH3I2vaPromptConformance.IntegratedMarker, StringComparison.Ordinal)
            && !body.Contains(MiniMaxH3I2vaPromptConformance.SoundscapeMarker, StringComparison.Ordinal)
            && !body.Contains(MiniMaxH3I2vaPromptConformance.MusicMarker, StringComparison.Ordinal)
            && !body.Contains("For the target video,", StringComparison.Ordinal))
            body = MiniMaxH3I2vaPromptConformance.Opening + MiniMaxH3I2vaPromptConformance.IntegratedPrefix + body
                + MiniMaxH3I2vaPromptConformance.SoundscapePrefix + "N/A"
                + MiniMaxH3I2vaPromptConformance.MusicPrefix + "N/A";
        string direction = VideoDirectionTimeline.Instruction(this, durationMs, OriginalDirections(kind, sourcePrompt));
        if (!VideoPromptSections.TryInsertVisual(body, direction, out body, out error)) return false;
        if (body.Length > 8000) { error = "生成用の本文を8,000文字以内にしてください。"; return false; }
        // Conformance remains available for AI candidates. Direct enqueue does
        // not force rewriting or alter the user's existing H3 sections.
        prompt = body;
        error = "";
        return true;
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
        string baseTemplate = AnnotatedH3 ? "" : BaseTemplateFor(sourceKind);
        if (!string.IsNullOrWhiteSpace(baseTemplate))
            text.Append(baseTemplate).Append("\n\nAdditional resolved direction:\n");
        int choiceCount = 0;
        foreach (VideoPromptToken token in tokens)
        {
            VideoPromptOption option = OptionFor(token);
            if (token.Kind != 't' && IsDirectionReplaced(option))
            {
                if (token.Kind == '[' ? IsOn(option, sourcePrompt) : option.Mode != "off")
                    text.Append(option.DirectionReplacement);
                continue;
            }
            if (token.Kind == 't') text.Append(token.Text);
            else if (token.Kind == '[')
            {
                if (IsOn(option, sourcePrompt))
                {
                    if (token.Choices.Count > 0 && option.ChoiceIndex >= token.Choices.Count)
                    { error = "手動選択肢が編集されています。選び直してください。"; return false; }
                    string selected = token.Choices.Count > 0 ? token.Choices[option.ChoiceIndex] : token.Text;
                    text.Append(DirectionAspectFor(option) == "camera" ? VideoDirectionTimeline.ApplyCaptureOverride(selected, this) : selected);
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
                string selected = token.Choices[option.ChoiceIndex];
                text.Append(DirectionAspectFor(option) == "camera" ? VideoDirectionTimeline.ApplyCaptureOverride(selected, this) : selected);
            }
        }
        if (string.IsNullOrWhiteSpace(baseTemplate) && string.IsNullOrWhiteSpace(text.ToString()))
        {
            error = "有効な指示がありません。本文かONのオプションを入力してください。";
            return false;
        }
        text.Append("\n\nAuthoring policy: The text above is the resolved user direction. Preserve its intent. Do not add alternative actions or editorial explanations to the final H3 prompt.");
        if (AnnotatedH3)
            text.Append(" This is an existing H3 prompt with the user's manual selections already resolved. Preserve the remaining wording and content. Repair only punctuation, conjunctions and dependent references affected by omitted options. Use only the selected camera direction; do not reinstate an omitted option.");
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
        if (VideoSubjectDirection.IsSelected(this)) text.Append("\n\n" + VideoDirectionTimeline.Instruction(this, frameCount * 1000 / 24, OriginalDirections(sourceKind, sourcePrompt)));
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

    // Pin exact source spans for image-selected alternatives. Unique markers
    // pass through the existing local resolver, including acting replacements;
    // no substring guessing against the user's prose or dialogue is involved.
    public bool TryResolveEnrichment(string kind, string? sourcePrompt, out string prompt,
        out VideoEnrichmentChoice[] choices, out string error, int durationMs = 15083)
    {
        choices = [];
        if (!TryResolveH3(kind, sourcePrompt, out prompt, out error, durationMs)) return false;
        if (!ImageChoices) return true;
        if (!VideoPromptLanguage.TryParse(TemplateFor(kind), out var tokens, out error)) return false;
        var shadow = Clone();
        var text = new StringBuilder();
        var slots = new List<(string Marker, string[] Values, int Selected)>();
        foreach (var token in tokens)
        {
            var option = OptionFor(token);
            if (token.Kind == '{' && option.Mode == "auto" && !IsDirectionReplaced(option))
            {
                if (slots.Count >= 16) { error = "画像で選ぶ候補は16組以内にしてください。"; return false; }
                string marker = "AIBOSCHOICE" + Guid.NewGuid().ToString("N");
                slots.Add((marker, token.Choices.ToArray(), option.ChoiceIndex));
                text.Append(marker);
            }
            else if (token.Kind == 't') text.Append(VideoPromptAuthoringControl.EscapeLiteral(token.Text));
            else text.Append(token.Key);
        }
        if (slots.Count == 0) return true;
        if (kind == "photoreal" && !string.IsNullOrWhiteSpace(PhotorealTemplate)) shadow.PhotorealTemplate = text.ToString();
        else shadow.Template = text.ToString();
        if (!shadow.TryResolveH3(kind, sourcePrompt, out string marked, out error, durationMs)) return false;
        var captured = new List<VideoEnrichmentChoice>();
        foreach (var slot in slots)
        {
            int start = marked.IndexOf(slot.Marker, StringComparison.Ordinal);
            if (start < 0) { error = "自動候補の位置を確定できません。"; return false; }
            string selected = slot.Values[slot.Selected];
            marked = marked.Remove(start, slot.Marker.Length).Insert(start, selected);
            captured.Add(new(start, selected.Length, slot.Values));
        }
        if (marked != prompt) { error = "候補の解決結果が一致しません。本文は変更していません。"; return false; }
        choices = captured.ToArray();
        return true;
    }
}

public sealed class VideoPromptOption
{
    public string Label { get; set; } = "";
    public string Category { get; set; } = "";
    // An explicitly reviewed style clause owned by a shared acting selector.
    // Only the resolved copy omits it when that selector overrides the style.
    public string DirectionAspect { get; set; } = "";
    public string DirectionReplacement { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> ChoiceLabels { get; set; } = [];
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
