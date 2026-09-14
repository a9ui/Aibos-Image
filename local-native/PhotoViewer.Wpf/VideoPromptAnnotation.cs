using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

// Only annotates reviewed spans. Never infers actions from arbitrary user text.
public static class VideoPromptAnnotation
{
    public sealed record CameraChoice(string label, string text);
    public static readonly IReadOnlyList<CameraChoice> CameraChoices = ReadCameraChoices();

    private static List<CameraChoice> ReadCameraChoices()
    {
        using var stream = typeof(VideoPromptAnnotation).Assembly.GetManifestResourceStream("PhotoViewer.Wpf.VideoCameraChoices.json")!;
        return JsonSerializer.Deserialize<List<CameraChoice>>(stream)!;
    }

    public static VideoPromptProgram BuiltIn(string prompt)
    {
        // These starts refer to repository-owned built-ins. Mixed clauses are
        // intentionally narrower, retaining the accompanying subject direction.
        string[] starts = ["Let the camera ", "Move the camera ", "Use a slow, composition-aware camera move",
            "use a gentle camera approach", "a gentle image-compatible camera approach"];
        Match camera = Regex.Matches(prompt, @"[^.\r\n]+\.")
            .FirstOrDefault(match => starts.Any(start => match.Value.Contains(start, StringComparison.Ordinal))) ?? Match.Empty;
        int start = camera.Success ? starts.Select(s => prompt.IndexOf(s, camera.Index, camera.Length, StringComparison.Ordinal)).Where(i => i >= 0).Min()
            : prompt.IndexOf(MiniMaxH3I2vaPromptConformance.IntegratedPrefix, StringComparison.Ordinal) + MiniMaxH3I2vaPromptConformance.IntegratedPrefix.Length;
        int length = camera.Success ? camera.Index + camera.Length - start : 0;
        string original = prompt.Substring(start, length);
        var choices = CameraChoices.Select(c => c.text).ToList();
        var labels = CameraChoices.Select(c => c.label).ToList();
        if (length > 0) { choices.Insert(0, original); labels.Insert(0, "元のカメラ指示"); }
        string body = string.Join(" / ", choices);
        var program = new VideoPromptProgram
        {
            Enabled = true, AnnotatedH3 = true, BaseH3Template = prompt,
            Template = VideoPromptAuthoringControl.EscapeLiteral(prompt[..start])
                + "[" + string.Join(" / ", choices.Select(VideoPromptAuthoringControl.EscapeLiteral)) + "]"
                + VideoPromptAuthoringControl.EscapeLiteral(prompt[(start + length)..]),
        };
        program.Options["[" + body + "]"] = new() { Label = "カメラワーク", Category = "camera",
            Mode = length == 0 ? "off" : "on", DefaultOn = length != 0, ChoiceLabels = labels };
        BindBuiltInDirections(program);
        if (!program.TryGetUnchangedH3("anime", out string resolved) || resolved != prompt)
            throw new InvalidOperationException("Built-in camera annotation must preserve the complete original prompt.");
        return program;
    }

    private static void BindBuiltInDirections(VideoPromptProgram program)
    {
        // Repository-owned phrases only. Replacements keep mixed sentences
        // grammatical without removing the main action or its constraints.
        (string Text, string Aspect, string Replacement)[] reviewed =
        [
            ("with a bright, energetic, and playful mood", "mood", "with the selected performance mood"),
            ("with a cute, mature, and tastefully alluring mood", "mood", "with the selected performance mood"),
            ("with an intensely sensual, magnetic, and deliberately provocative adult mood", "mood", "with the selected performance mood"),
            ("with drama, depth, and a strong sense of atmosphere", "mood", "with depth and the selected performance mood"),
            ("that feels calm and natural but is clearly alive rather than frozen", "mood", "that follows the selected performance mood and is clearly alive rather than frozen"),
            ("with a soft, dreamlike, and unhurried mood", "mood", "with the selected performance mood"),
            ("with a warm, tender, and quietly romantic mood", "mood", "with the selected performance mood"),
            ("Build a calm progression", "mood", "Build a progression suited to the selected performance mood"),
            ("convey warmth and affectionate confidence", "mood", "convey the selected performance mood"),
            ("Build erotic tension", "mood", "Develop the selected performance mood"),
            ("Let confidence grow into a commanding, self-possessed finish.", "mood", "Let the selected performance mood develop naturally toward the finish."),
            ("with lively expression changes", "expression", "with the selected expression"),
            ("slightly reserved or shy composure", "expression", "the selected expression"),
            ("a more confident expression", "expression", "the selected expression"),
            ("one coherent expression that suits the pictured situation", "expression", "the selected expression"),
            ("gentle expression changes", "expression", "the selected expression"),
            ("subtle synchronized movement and expression", "expression", "subtle synchronized movement and the selected expression"),
            ("an appealing, playful gesture", "arms", "the selected arm and hand direction"),
            ("Do not add a new gesture, person, object, contact, event, or cut.", "arms", "Do not add a new person, object, contact, event, or cut."),
            ("Begin with restraint", "opening", "Begin with the selected opening movement"),
        ];
        foreach (var binding in reviewed)
        {
            string escaped = VideoPromptAuthoringControl.EscapeLiteral(binding.Text);
            int at = program.Template.IndexOf(escaped, StringComparison.Ordinal);
            if (at < 0) continue;
            if (program.Template.IndexOf(escaped, at + escaped.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("A reviewed built-in direction must be unique.");
            // A reviewed phrase must remain literal, never inside a camera or
            // other manual choice. Escaped H3 brackets are literal characters.
            bool inOption = false;
            for (int i = 0; i < at; i++)
            {
                if (program.Template[i] == '\\') { i++; continue; }
                if (program.Template[i] is '[' or '{') inOption = true;
                if (program.Template[i] is ']' or '}') inOption = false;
            }
            if (inOption) throw new InvalidOperationException("Reviewed direction overlaps another option.");
            program.Template = program.Template[..at] + "[" + escaped + "]" + program.Template[(at + escaped.Length)..];
            program.Options["[" + binding.Text + "]"] = new()
            {
                Label = binding.Aspect switch { "opening" => "元の冒頭動作", "arms" => "元の腕・手の指示", "expression" => "元の表情", _ => "元の雰囲気" },
                Category = binding.Aspect == "expression" ? "expression" : "detail",
                DirectionAspect = binding.Aspect, DirectionReplacement = binding.Replacement,
            };
        }
    }
}
