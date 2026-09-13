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
        if (!program.TryGetUnchangedH3("anime", out string resolved) || resolved != prompt)
            throw new InvalidOperationException("Built-in camera annotation must preserve the complete original prompt.");
        return program;
    }
}
