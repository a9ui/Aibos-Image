using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private static void VerifyDirectionTimeline(Dictionary<string, bool> checks)
    {
        var p = new VideoPromptProgram
        {
            Enabled = true, AnnotatedH3 = true,
            Template = "[The camera moves back with continuous natural micro-shake.] The presenter walks along the path [with a stern expression]. [Her arms remain folded.] [The mood is serious.] She says <d>\\[Japanese\\]こんにちは。</d>",
            Options = new()
            {
                ["[The camera moves back with continuous natural micro-shake.]"] = new() { Category = "camera" },
                ["[with a stern expression]"] = new() { DirectionAspect = "expression", DirectionReplacement = "with the selected expression" },
                ["[Her arms remain folded.]"] = new() { DirectionAspect = "arms" },
                ["[The mood is serious.]"] = new() { DirectionAspect = "mood" },
            },
        };
        p.TryResolveH3("anime", null, out string original, out _);
        p.BaseH3Template = original;
        p.CaptureModeId = "stabilized";
        checks["captureOverrideRetainsCameraTravelAndRemovesOnlyKnownHandling"] = p.TryResolveH3("anime", null, out string captured, out _)
            && captured.Contains("The camera moves back with the selected camera handling.") && !captured.Contains("micro-shake")
            && captured.Contains("Stabilization does not mean a static camera");
        p.DirectionPhases =
        [
            new() { EndMillionths = 198899, CameraId = "front", ArmsId = "lower", ExpressionId = "original", MoodId = "dramatic" },
            new() { EndMillionths = 596698, CameraId = "face", ArmsId = "lower", ExpressionId = "suspicious", MoodId = "dramatic" },
            new() { CameraId = "original", ArmsId = "still", ExpressionId = "calm", MoodId = "original" },
        ];
        checks["sharedTimelineKeepsMainActionAndDialogueOnce"] = p.TryResolveH3("anime", null, out string result, out _, 15083)
            && result.Split("The presenter walks along the path").Length == 2
            && result.Split("<d>[Japanese]こんにちは。</d>").Length == 2
            && result.Contains("From 0.00 to 3.00 seconds:") && result.Contains("From 3.00 to 9.00 seconds:")
            && result.Contains("From 9.00 to 15.08 seconds:") && !result.Contains("[Shot 2]");
        int first = result.IndexOf("From 0.00", StringComparison.Ordinal), second = result.IndexOf("From 3.00", StringComparison.Ordinal), last = result.IndexOf("From 9.00", StringComparison.Ordinal);
        checks["originalAspectBelongsOnlyToItsUnchangedInterval"] = first > 0 && second > first && last > second
            && !result[..first].Contains("stern") && result[first..second].Contains("stern") && !result[second..].Contains("stern")
            && !result[..last].Contains("The mood is serious") && result[last..].Contains("The mood is serious")
            && !result[..last].Contains("The camera moves back") && result[last..].Contains("The camera moves back");
        checks["continuedGestureDoesNotRestartAtOtherAspectChanges"] = result.Split("She smoothly lowers her free arms").Length == 2
            && result.Contains("An unchanged direction continues without restarting");
        var clock = VideoDirectionTimeline.Capture(p, 5166);
        checks["durationChangeUsesOneSharedAppClock"] = clock[0].StartMs == 0 && clock[^1].EndMs == 5166
            && clock.Zip(clock.Skip(1)).All(pair => pair.First.EndMs == pair.Second.StartMs)
            && p.TryResolveH3("anime", null, out string shortPrompt, out _, 5166) && clock.All(c => shortPrompt.Contains(c.Anchor))
            && !shortPrompt.Contains("15.08 seconds");
        p.DirectionPhases[0].ExtensionData = new() { ["FutureNote"] = JsonSerializer.SerializeToElement("kept") };
        checks["timelineRoundTripsAndKeepsCompatibleUnknownData"] = VideoPromptProgram.TryRead(p.Snapshot(), out var read)
            && read.DirectionPhases[0].ExtensionData!["FutureNote"].GetString() == "kept" && read.CaptureModeId == "stabilized";
        var invalid = p.Clone(); invalid.DirectionPhases.Add(new());
        checks["timelineBoundsProtectStoredStyles"] = !invalid.Validate(out _);
        invalid = p.Clone(); invalid.DirectionPhases[1].EndMillionths = invalid.DirectionPhases[0].EndMillionths;
        checks["timelineRejectsCrossedBoundaries"] = !invalid.Validate(out _);
        foreach (string aspect in VideoDirectionTimeline.Aspects) p.RestoreOriginalDirection(aspect);
        p.DirectionPhases.Clear(); p.CaptureModeId = "original";
        checks["resetTimelineRestoresSourceExactly"] = p.TryResolveH3("anime", null, out string reset, out _) && reset == original;

        var spatial = new VideoPromptProgram
        {
            Enabled = true, AnnotatedH3 = true,
            Template = "The subject gives one small wave. [She looks past the lens.] [She remains at the original spot.]",
            OpeningMotionId = "step-back", ExpressionId = "flustered", PositionId = "approach-continuous", GazeId = "viewer",
            CameraMotionId = "fixed",
            Options = new()
            {
                ["[She looks past the lens.]"] = new() { DirectionAspect = "gaze" },
                ["[She remains at the original spot.]"] = new() { DirectionAspect = "position" },
            },
        };
        string savedSpatialTemplate = spatial.Template;
        checks["positionAndGazeResolveWithoutAiOrConflictingOpening"] = spatial.TryResolveH3("anime", null, out string spatialPrompt, out _)
            && spatialPrompt.Contains("subject continues approaching") && spatialPrompt.Contains("Hold the current camera position")
            && spatialPrompt.Contains("looks toward the viewer's eyes") && !spatialPrompt.Contains("small step backward")
            && !spatialPrompt.Contains("briefly lowered eyes") && !spatialPrompt.Contains("She looks past the lens")
            && spatial.Template == savedSpatialTemplate;
        spatial.DirectionPhases =
        [
            new() { EndMillionths = 333333, PositionId = "approach-continuous", GazeId = "viewer", ExpressionId = "flustered" },
            new() { EndMillionths = 666667, PositionId = "approach-continuous", GazeId = "downcast", ExpressionId = "flustered" },
            new() { PositionId = "original", GazeId = "original", ExpressionId = "flustered" },
        ];
        checks["spatialIntervalsRestoreOwnedSourceAndDoNotLoopApproach"] = spatial.TryResolveH3("anime", null, out spatialPrompt, out _, 15000)
            && spatialPrompt.Split("subject continues approaching").Length == 2
            && spatialPrompt.Contains("From 10.00 to 15.00 seconds:")
            && spatialPrompt.IndexOf("She looks past the lens", StringComparison.Ordinal) > spatialPrompt.IndexOf("From 10.00", StringComparison.Ordinal)
            && spatialPrompt.IndexOf("She remains at the original spot", StringComparison.Ordinal) > spatialPrompt.IndexOf("From 10.00", StringComparison.Ordinal)
            && spatialPrompt.Split("The subject gives one small wave").Length == 2
            && spatial.TryResolveEnrichment("anime", null, out string enrichedInput, out _, out _, 15000) && enrichedInput == spatialPrompt;
        var spatialRoundTrip = spatial.Snapshot();
        checks["spatialChoicesPersistAndOldStylesStayUnchanged"] = VideoPromptProgram.TryRead(spatialRoundTrip, out var spatialRead)
            && spatialRead.PositionId == "approach-continuous" && spatialRead.GazeId == "viewer"
            && spatialRead.DirectionPhases[1].GazeId == "downcast"
            && VideoPromptProgram.TryRead(JsonDocument.Parse("{\"Version\":1,\"Template\":\"Untouched source\"}").RootElement, out var oldStyle)
            && oldStyle.PositionId == "original" && oldStyle.GazeId == "original" && !VideoSubjectDirection.IsSelected(oldStyle);
        spatial.PositionId = "future-position";
        checks["unknownSpatialChoiceProtectsStoredStyle"] = !VideoPromptProgram.TryRead(spatial.Snapshot(), out _);

        var editor = new VideoPromptAuthoringControl();
        VideoPromptProgram? changed = null;
        editor.Changed += (_, value, _) => changed = value;
        editor.Load(p, original, "anime", "anime", null, durationMs: 5166);
        editor.AddTimelinePhaseForSmoke(); editor.AddTimelinePhaseForSmoke(); editor.AddTimelinePhaseForSmoke();
        editor.SelectTimelineForSmoke(0, "expression", "surprised");
        editor.SelectTimelineForSmoke(1, "expression", "suspicious");
        editor.SelectTimelineForSmoke(2, "expression", "calm");
        editor.SelectTimelineForSmoke(0, "position", "approach-stop");
        editor.SelectTimelineForSmoke(1, "position", "stay");
        editor.SelectTimelineForSmoke(0, "gaze", "viewer");
        editor.SelectTimelineForSmoke(1, "gaze", "downcast");
        editor.SelectTimelineForSmoke(2, "gaze", "down-then-viewer");
        checks["timelineUiAddsAtMostThreeIndependentSelections"] = editor.TimelinePhaseCountForSmoke == 3
            && changed?.DirectionPhases.Select(x => x.ExpressionId).SequenceEqual(["surprised", "suspicious", "calm"]) == true
            && changed.DirectionPhases.Select(x => x.GazeId).SequenceEqual(["viewer", "downcast", "down-then-viewer"])
            && changed.DirectionPhases.Select(x => x.PositionId).SequenceEqual(["approach-stop", "stay", "original"]);
    }
}
