using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

// Style timing is relative to the clip. Only the compiler owns millisecond
// boundaries; a model never independently invents clocks for each aspect.
public sealed class VideoDirectionPhase
{
    public int EndMillionths { get; set; } = 1_000_000;
    public string CameraId { get; set; } = "original";
    public string ArmsId { get; set; } = "original";
    public string ExpressionId { get; set; } = "original";
    public string MoodId { get; set; } = "original";
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string For(string aspect) => aspect switch
    { "camera" => CameraId, "arms" => ArmsId, "expression" => ExpressionId, "mood" => MoodId, _ => "original" };
    public void Set(string aspect, string id)
    {
        switch (aspect)
        { case "camera": CameraId = id; break; case "arms": ArmsId = id; break;
          case "expression": ExpressionId = id; break; case "mood": MoodId = id; break; }
    }
    public VideoDirectionPhase Copy() => JsonSerializer.Deserialize<VideoDirectionPhase>(JsonSerializer.Serialize(this))!;
}

public static class VideoDirectionTimeline
{
    public static readonly VideoSubjectDirection.Choice[] CaptureModes =
    [
        new("original", "元の撮り方", ""),
        new("pov", "一人称視点（POV）", "Maintain the same observer's first-person viewpoint facing the visible subject, with small natural head sway. Selected camera moves are movements of this observer relative to the subject, not movements of the subject. Do not switch identities or invent visible hands."),
        new("handheld", "手持ちカメラ", "Use a handheld camera with restrained irregular micro-movement, while keeping the subject readable."),
        new("stabilized", "なめらかな撮影", "Use a stabilized camera with smooth, controlled movement and no incidental shake. Stabilization does not mean a static camera."),
        new("phone", "スマートフォン撮影風", "Use restrained smartphone-like handheld framing with small natural corrections, without adding interface graphics or changing the image medium."),
        new("documentary", "ドキュメンタリー風", "Use observational documentary camera handling with subtle responsive framing, without introducing cuts or unselected camera travel."),
        new("shoulder", "肩載せカメラ風", "Use measured shoulder-mounted camera handling with slight operator sway and deliberate framing."),
        new("body-mounted", "身体に装着したカメラ風", "Maintain the same observer's body-mounted viewpoint; any sway follows that observer's existing movement, without inventing walking, impacts or visible equipment."),
    ];
    public static readonly VideoSubjectDirection.Choice[] Cameras =
    [
        new("original", "元のカメラ指示", ""),
        new("fixed", "構図を保つ", "Hold the current camera position and framing, without a pan, zoom or travelling move."),
        new("push", "ゆっくり寄る", "The camera slowly moves closer to the subject with small amplitude."),
        new("pull", "ゆっくり引く", "The camera slowly moves back from the subject with small amplitude."),
        new("arc-left", "左へ回り込む", "The camera slowly arcs left around the subject, preserving continuous spatial relationships."),
        new("arc-right", "右へ回り込む", "The camera slowly arcs right around the subject, preserving continuous spatial relationships."),
        new("front", "正面へ回り込む", "The camera moves smoothly toward a frontal view of the subject from its current angle."),
        new("three-quarter", "斜め前へ回り込む", "The camera moves smoothly toward a three-quarter view of the subject."),
        new("track", "被写体を追う", "The camera tracks the subject's existing movement, keeping a consistent distance without adding movement to the subject."),
        new("pan-left", "左へパン", "The camera pans slowly to the left with small amplitude while its position stays unchanged."),
        new("pan-right", "右へパン", "The camera pans slowly to the right with small amplitude while its position stays unchanged."),
        new("truck-left", "左へ平行移動", "The camera translates a short distance left, maintaining its viewing direction."),
        new("truck-right", "右へ平行移動", "The camera translates a short distance right, maintaining its viewing direction."),
        new("tilt-up", "上へ視線を移す", "The camera tilts upward slowly from its current composition."),
        new("tilt-down", "下へ視線を移す", "The camera tilts downward slowly from its current composition."),
        new("eye-level", "目線の高さへ移る", "The camera moves gradually toward the subject's eye level, without a cut."),
        new("low-angle", "低い位置から見上げる", "The camera gradually lowers into a modest low angle looking toward the subject."),
        new("high-angle", "高い位置から見下ろす", "The camera gradually rises into a modest high angle looking toward the subject."),
        new("face", "顔に寄る", "The framing gradually tightens toward the subject's visible face, keeping it legible."),
        new("hands", "手元に寄る", "The framing gradually tightens toward the subject's visible hands, without inventing objects or changing their action."),
        new("upper-body", "上半身に寄る", "The framing gradually tightens toward the subject's upper body."),
        new("full-body", "全身を収める", "The framing gradually widens to include the subject's whole body when the visible space allows."),
        new("zoom-in", "その場からズームイン", "The lens slowly zooms in with small amplitude while the camera position remains unchanged."),
        new("zoom-out", "その場からズームアウト", "The lens slowly zooms out with small amplitude while the camera position remains unchanged."),
    ];
    public static readonly string[] Aspects = ["camera", "arms", "expression", "mood"];
    public static VideoSubjectDirection.Choice[] Choices(string aspect) => aspect switch
    { "camera" => Cameras, "arms" => VideoSubjectDirection.Arms, "expression" => VideoSubjectDirection.Expressions, _ => VideoSubjectDirection.Moods };
    public static string Global(VideoPromptProgram p, string aspect) => aspect switch
    { "camera" => p.CameraMotionId, "arms" => p.ArmMotionId, "expression" => p.ExpressionId, "mood" => p.MoodId, _ => "original" };
    public static bool Overrides(VideoPromptProgram p, string aspect)
        => p.DirectionPhases.Count > 0 ? p.DirectionPhases.Any(s => s.For(aspect) != "original") : Global(p, aspect) != "original";
    public static bool IsValid(VideoPromptProgram p)
    {
        if (p.DirectionPhases is null || p.DirectionPhases.Count > 3
            || !CaptureModes.Any(c => c.Id == p.CaptureModeId) || !Cameras.Any(c => c.Id == p.CameraMotionId)) return false;
        int end = 0;
        foreach (var phase in p.DirectionPhases)
        {
            if (phase is null || phase.EndMillionths <= end || phase.EndMillionths > 1_000_000
                || Aspects.Any(a => !Choices(a).Any(c => c.Id == phase.For(a)))) return false;
            end = phase.EndMillionths;
        }
        return p.DirectionPhases.Count == 0 || end == 1_000_000;
    }
    public static int EndMs(VideoDirectionPhase phase, int durationMs)
        => (int)Math.Round((long)phase.EndMillionths * durationMs / 1_000_000d, MidpointRounding.AwayFromZero);
    public static string Anchor(int startMs, int endMs) => string.Create(CultureInfo.InvariantCulture,
        $"From {startMs / 1000d:F2} to {endMs / 1000d:F2} seconds:");
    public static VideoEnrichmentTimingPhase[] Capture(VideoPromptProgram p, int durationMs)
    {
        int start = 0;
        return p.DirectionPhases.Select(phase =>
        {
            int end = EndMs(phase, durationMs);
            var result = new VideoEnrichmentTimingPhase(start, end, Anchor(start, end));
            start = end;
            return result;
        }).ToArray();
    }
    public static VideoDirectionPhase FromGlobal(VideoPromptProgram p) => new()
    { CameraId = p.CameraMotionId, ArmsId = p.ArmMotionId, ExpressionId = p.ExpressionId, MoodId = p.MoodId };

    public static string ApplyCaptureOverride(string text, VideoPromptProgram p)
    {
        if (p.CaptureModeId == "original") return text;
        // Known handling phrases within an explicitly owned camera span only.
        // Keep its framing/travel clauses and never scan arbitrary action prose.
        foreach (string phrase in new[] { "with continuous natural micro-shake", "with continuous natural micro shake",
            "with subtle handheld shake", "with natural micro-shake" })
            text = text.Replace(phrase, "with the selected camera handling", StringComparison.OrdinalIgnoreCase);
        foreach (string phrase in new[] { "Use restrained handheld camera sway.",
            "Use first-person camera movement with subtle head sway and gaze shifts.", "手持ちカメラによる撮影風。" })
            text = text.Replace(phrase, "Use the selected camera handling.", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    public static string Instruction(VideoPromptProgram p, int durationMs, IReadOnlyDictionary<string, string>? originals)
    {
        var parts = new List<string>();
        var legacy = p.Clone();
        legacy.DirectionPhases.Clear(); legacy.CaptureModeId = legacy.CameraMotionId = "original";
        if (p.DirectionPhases.Count > 0) legacy.ArmMotionId = legacy.ExpressionId = legacy.MoodId = "original";
        string opening = VideoSubjectDirection.Instruction(legacy);
        if (p.CaptureModeId != "original" || Overrides(p, "camera"))
            opening = opening.Replace("existing people, camera, setting", "existing people, setting", StringComparison.Ordinal);
        if (opening.Length > 0) parts.Add(opening);
        if (p.CaptureModeId != "original") parts.Add("Camera handling throughout the same shot: " + CaptureModes.Single(c => c.Id == p.CaptureModeId).Text);
        if (p.DirectionPhases.Count == 0)
        {
            if (p.CameraMotionId != "original") parts.Add("Camera movement: " + Cameras.Single(c => c.Id == p.CameraMotionId).Text);
            return string.Join("\n", parts);
        }
        parts.Add("These intervals refine the same ongoing main action above, without introducing repetitions or cuts. Preserve any explicitly authored shots. Start from the actual reference pose and expression, developing toward the first interval naturally. At each boundary, continue the action and camera from their current state. An unchanged direction continues without restarting; perform a one-off hand gesture only once. Respect held objects, required contact, available hands and weight-bearing support. Explicit facial direction takes precedence over mood. Existing vocal content continues across interval boundaries without replay or interruption; other selected details still apply.");
        var clock = Capture(p, durationMs);
        for (int i = 0; i < p.DirectionPhases.Count; i++)
        {
            var phase = p.DirectionPhases[i];
            var directions = new List<string>();
            foreach (string aspect in Aspects)
            {
                string id = phase.For(aspect);
                if (i > 0 && p.DirectionPhases[i - 1].For(aspect) == id) continue;
                string text = id == "original"
                    ? Overrides(p, aspect) && originals?.TryGetValue(aspect, out string? saved) == true ? saved : ""
                    : Choices(aspect).Single(c => c.Id == id).Text;
                if (text.Length == 0 && i > 0 && id == "original") text = aspect switch
                {
                    "camera" => "Resume camera framing appropriate to the source scene, without restarting its initial position.",
                    "arms" => "Let the arms follow the main action naturally, without repeating the preceding gesture.",
                    "expression" => "Let the expression follow the original scene and ongoing action naturally.",
                    _ => "Return to the manner of the original scene and main action.",
                };
                if (text.Length > 0) directions.Add(aspect switch { "camera" => "Camera: ", "arms" => "Hands and arms: ", "expression" => "Expression: ", _ => "Mood: " } + text);
            }
            parts.Add(clock[i].Anchor + " " + (directions.Count > 0 ? string.Join(" ", directions) : "Continue the ongoing action and current directions naturally."));
        }
        return string.Join("\n\n", parts);
    }
}
