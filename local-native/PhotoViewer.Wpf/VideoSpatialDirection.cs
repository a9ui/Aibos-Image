namespace PhotoViewer.Wpf;

// Subject movement and eye direction are independent of the camera operator.
public static class VideoSpatialDirection
{
    public static readonly VideoSubjectDirection.Choice[] Positions =
    [
        new("original", "元の位置指示を使う", ""),
        new("stay", "その場にとどまる", "The subject keeps the current place in the scene, allowing the existing action and small natural body movements."),
        new("approach-stop", "少し近づいて止まる", "The subject moves a short distance toward the viewer, then settles at that position without repeating the approach."),
        new("approach-continuous", "ゆっくり近づき続ける", "The subject continues approaching the viewer slowly through this interval without restarting, speeding up or stepping back to repeat. Ease to a stop when the available distance is used up."),
        new("retreat-stop", "少し距離を取って止まる", "The subject moves a short distance away from the viewer, then settles there without repeating the retreat."),
        new("retreat-continuous", "ゆっくり離れ続ける", "The subject continues moving slowly away from the viewer through this interval without restarting, speeding up or returning to repeat. Ease to a stop at the limit of the visible space."),
        new("lean-in", "足元はそのまま、上体を寄せる", "The subject gently leans the upper body a little toward the viewer, keeping the current footing or seated support, then holds that comfortable position."),
        new("lean-back", "足元はそのまま、上体を引く", "The subject gently leans the upper body a little away from the viewer, keeping the current footing or seated support, then settles."),
        new("offset-left", "見る側から左へ少しずれる", "The subject shifts a short distance toward the viewer's left, then settles, keeping a similar distance from the viewer."),
        new("offset-right", "見る側から右へ少しずれる", "The subject shifts a short distance toward the viewer's right, then settles, keeping a similar distance from the viewer."),
        new("face-viewer", "身体をこちらの正面へ向ける", "The subject gradually turns the body to face the viewer from the current position, without moving the camera or forcing eye contact."),
        new("three-quarter", "身体を斜めに向ける", "The subject gradually settles into a slight three-quarter body orientation relative to the viewer, without travelling or forcing a gaze direction."),
        new("side-on", "身体を横向きにする", "The subject gradually turns the body side-on to the viewer while keeping the current place and support; eye direction remains independently selected."),
    ];

    public static readonly VideoSubjectDirection.Choice[] Gazes =
    [
        new("original", "元の視線指示を使う", ""),
        new("viewer", "こちらを見つめる", "The subject looks toward the viewer's eyes at the lens, allowing natural blinks and small eye movements."),
        new("soft-viewer", "やわらかく目を合わせる", "The subject maintains gentle, relaxed eye contact with the viewer, without fixing the eyes rigidly or prescribing a smile."),
        new("avert", "目をそらす", "The subject gently averts the eyes from the viewer and lets the gaze rest away, without turning the whole body."),
        new("downcast", "伏し目にする", "The subject lowers the gaze, with the eyes naturally directed down rather than closed."),
        new("up-through-lashes", "上目遣いでこちらを見る", "With the chin slightly lowered, the subject raises the eyes toward the viewer through the lashes; do not tilt the camera or exaggerate the head pose."),
        new("sidelong", "横目でこちらを見る", "The subject gives the viewer a sidelong look through a small eye movement, keeping the body's selected orientation."),
        new("past-viewer", "こちらの少し向こうを見る", "The subject focuses just beyond the viewer rather than directly into the lens, without inventing a new person or object."),
        new("left", "見る側から左へ視線を向ける", "The subject looks toward the viewer's left, using the eyes and only a small natural head adjustment."),
        new("right", "見る側から右へ視線を向ける", "The subject looks toward the viewer's right, using the eyes and only a small natural head adjustment."),
        new("hands", "自分の手元を見る", "The subject watches their own hands during the existing hand action when visible and reachable by the gaze, without adding a hand gesture or object."),
        new("action", "している動作に視線を向ける", "The subject directs attention to the existing action or its visible target, without inventing a target or changing the action."),
        new("away-return", "一度そらして、目を合わせる", "The subject looks away once, pauses briefly, then naturally returns the gaze to the viewer and keeps it there. Do not loop the glance."),
        new("glance-return", "ちらっとこちらを見て戻す", "The subject briefly glances toward the viewer once, then returns to the previous gaze target without repeating the glance."),
        new("down-then-viewer", "伏し目から、そっと目を合わせる", "The subject lowers the gaze briefly, then slowly lifts only the eyes toward the viewer and maintains natural eye contact. Perform this transition once."),
        new("follow-viewer", "動くカメラを目で追う", "The subject follows the viewer's already selected camera movement with the eyes and small natural head adjustments; if the camera is still, maintain eye contact without inventing camera motion."),
        new("close-eyes", "ゆっくり目を閉じる", "The subject gently closes the eyes once and keeps them softly closed for this interval, without adding a nod or repeated blinking gesture."),
    ];

    public static bool ReplacesOpening(VideoPromptProgram p)
    {
        string position = p.DirectionPhases.Count == 0 ? p.PositionId : p.DirectionPhases[0].PositionId;
        return position switch
        {
            "stay" or "approach-stop" or "approach-continuous" or "retreat-stop" or "retreat-continuous" or "offset-left" or "offset-right"
                => p.OpeningMotionId is "approach" or "approach-slow" or "approach-brisk" or "step-back" or "stay" or "step-side",
            "lean-in" or "lean-back" => p.OpeningMotionId is "lean-in" or "lean-back" or "bend-forward",
            "face-viewer" or "three-quarter" or "side-on" => p.OpeningMotionId is "turn-viewer" or "turn-away" or "half-turn",
            _ => false,
        };
    }

    public static string ExpressionText(string id, bool gazeSelected)
        => gazeSelected ? id switch
        {
            "flustered" => "A bashful expression with a small self-conscious smile.",
            "dazed" => "A dreamy expression with relaxed facial muscles.",
            _ => VideoSubjectDirection.Expressions.Single(c => c.Id == id).Text,
        } : VideoSubjectDirection.Expressions.Single(c => c.Id == id).Text;

    public static string Boundaries(VideoPromptProgram p)
    {
        var parts = new List<string>();
        if (VideoDirectionTimeline.Overrides(p, "position"))
            parts.Add("Subject positioning controls only the subject's location and body orientation relative to the viewer; camera moves belong only to the camera directions. Start from the actual pose and respect available space, support and contact needed by the main action. Do not teleport, collide, add people or reset a completed move. An explicit positioning choice takes precedence over incidental travel in an opening preset, only for the overlapping aspect and interval.");
        if (VideoDirectionTimeline.Overrides(p, "gaze"))
            parts.Add("An explicit gaze choice controls eye direction and takes precedence over incidental gaze in expression or mood presets for that interval, while keeping their emotion. Camera tilts do not direct the subject's eyes. Respect natural eye and neck range; do not turn the whole body to force eye contact.");
        return string.Join("\n", parts);
    }
}
