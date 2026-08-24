using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace PhotoViewer.Wpf;

internal enum MotionDirectorRiskLevel
{
    Low,
    Medium,
    High,
}

internal enum MotionDirectorPhase
{
    Anticipation,
    Action,
    Settle,
    Hold,
}

internal sealed record MotionDirectorActionDefinition(
    string Id,
    int Priority,
    string LabelResourceKey,
    string EnglishLabel,
    int MinimumFrames,
    int PreferredFrames,
    int MaximumFrames,
    MotionDirectorRiskLevel Risk,
    string H3Phrase);

internal sealed record MotionDirectorCameraDefinition(
    string Id,
    string LabelResourceKey,
    string EnglishLabel,
    MotionDirectorRiskLevel Risk,
    bool IsStrong,
    string H3Phrase);

internal sealed record MotionDirectorSegment(
    MotionDirectorPhase Phase,
    string? ActionId,
    int StartFrame,
    int EndFrame,
    string H3Phrase)
{
    internal int FrameCount => EndFrame - StartFrame;
}

internal sealed record MotionDirectorPlan(
    int FrameCount,
    int PlaybackFps,
    IReadOnlyList<MotionDirectorActionDefinition> Actions,
    IReadOnlyList<MotionDirectorActionDefinition> DroppedActions,
    MotionDirectorCameraDefinition RequestedCamera,
    MotionDirectorCameraDefinition EffectiveCamera,
    MotionDirectorRiskLevel Risk,
    string? WarningResourceKey,
    IReadOnlyList<MotionDirectorSegment> Segments,
    string CandidatePrompt)
{
    internal decimal ExactDurationSeconds =>
        (decimal)FrameCount / PlaybackFps;
}

internal static class MotionDirectorPlanner
{
    internal const int PlaybackFps = 24;
    internal const int MaximumSelectedActions = 3;
    private const string SafeCameraId = "fixed";

    internal static readonly IReadOnlyList<int> SupportedFrameCounts =
        Array.AsReadOnly([124, 243, 294, 362]);

    internal static readonly IReadOnlyList<MotionDirectorActionDefinition>
        ActionCatalog = new ReadOnlyCollection<MotionDirectorActionDefinition>(
        [
            new(
                "subtle-gaze",
                10,
                "UiMotionDirectorActionGaze",
                "Subtle gaze",
                24,
                48,
                72,
                MotionDirectorRiskLevel.Low,
                "the subject makes a small, deliberate gaze shift while the face, eyes, and head remain anatomically stable"),
            new(
                "gentle-smile",
                20,
                "UiMotionDirectorActionSmile",
                "Gentle smile",
                36,
                60,
                84,
                MotionDirectorRiskLevel.Low,
                "the subject's expression gradually warms into a gentle natural smile without changing identity or facial structure"),
            new(
                "subject-turn",
                30,
                "UiMotionDirectorActionTurn",
                "Small turn",
                48,
                84,
                120,
                MotionDirectorRiskLevel.Medium,
                "the subject makes a small controlled upper-body turn with coherent shoulders, clothing, hair, and visible limbs"),
            new(
                "natural-reach",
                40,
                "UiMotionDirectorActionReach",
                "Natural reach",
                48,
                84,
                132,
                MotionDirectorRiskLevel.Medium,
                "the subject makes one restrained natural reach only within the visible scene, keeping hands, objects, and contact physically coherent"),
            new(
                "gentle-walk",
                50,
                "UiMotionDirectorActionWalk",
                "Gentle walk",
                72,
                120,
                168,
                MotionDirectorRiskLevel.High,
                "the subject takes a few gentle grounded steps with stable identity, balanced foot placement, coherent limbs, and continuous clothing"),
            new(
                "expressive-gesture",
                60,
                "UiMotionDirectorActionGesture",
                "Expressive gesture",
                60,
                96,
                144,
                MotionDirectorRiskLevel.High,
                "the subject performs one clear expressive hand gesture with stable fingers, coherent arms, and no invented contact or objects"),
        ]);

    internal static readonly IReadOnlyList<MotionDirectorCameraDefinition>
        CameraCatalog = new ReadOnlyCollection<MotionDirectorCameraDefinition>(
        [
            new(
                "fixed",
                "UiMotionDirectorCameraFixed",
                "Fixed",
                MotionDirectorRiskLevel.Low,
                false,
                "The camera remains fixed in one continuous shot with the original framing and perspective preserved."),
            new(
                "slow-push",
                "UiMotionDirectorCameraPush",
                "Slow push",
                MotionDirectorRiskLevel.Medium,
                false,
                "The camera makes a very slow, smooth push in while preserving the original perspective, subject proportions, and scene geometry."),
            new(
                "slow-pull",
                "UiMotionDirectorCameraPull",
                "Slow pull",
                MotionDirectorRiskLevel.Medium,
                false,
                "The camera makes a very slow, smooth pull back without revealing new people, objects, or scene areas unsupported by the source image."),
            new(
                "gentle-track",
                "UiMotionDirectorCameraTrack",
                "Gentle track",
                MotionDirectorRiskLevel.High,
                true,
                "The camera makes one gentle lateral tracking move while keeping the subject centered and the visible scene geometry coherent."),
        ]);

    internal static bool TryBuild(
        int frameCount,
        int playbackFps,
        IEnumerable<string> selectedActionIds,
        string cameraId,
        out MotionDirectorPlan plan,
        out string error)
    {
        plan = null!;
        error = "";
        if (playbackFps != PlaybackFps
            || !SupportedFrameCounts.Contains(frameCount))
        {
            error = "unsupported-profile";
            return false;
        }

        if (selectedActionIds is null)
        {
            error = "actions-required";
            return false;
        }

        var requestedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? rawId in selectedActionIds)
        {
            string id = rawId?.Trim() ?? "";
            if (id.Length == 0)
                continue;
            if (!ActionCatalog.Any(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal)))
            {
                error = "unknown-action";
                return false;
            }
            requestedIds.Add(id);
        }

        if (requestedIds.Count is < 1 or > MaximumSelectedActions)
        {
            error = requestedIds.Count == 0
                ? "actions-required"
                : "too-many-actions";
            return false;
        }

        MotionDirectorCameraDefinition? requestedCamera = CameraCatalog
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                cameraId?.Trim(),
                StringComparison.Ordinal));
        if (requestedCamera is null)
        {
            error = "unknown-camera";
            return false;
        }

        var kept = ActionCatalog
            .Where(candidate => requestedIds.Contains(candidate.Id))
            .OrderBy(static candidate => candidate.Priority)
            .ToList();
        var dropped = new List<MotionDirectorActionDefinition>();
        // The current 124-frame H3 profile carries at most two useful motion
        // beats. Longer exact profiles carry up to three.
        int profileActionLimit = frameCount == 124 ? 2 : 3;
        while (kept.Count > profileActionLimit)
        {
            MotionDirectorActionDefinition removed = kept[^1];
            kept.RemoveAt(kept.Count - 1);
            dropped.Insert(0, removed);
        }
        int actionCapacity = frameCount - 1;
        while (kept.Sum(static action => action.MinimumFrames) > actionCapacity)
        {
            MotionDirectorActionDefinition removed = kept[^1];
            kept.RemoveAt(kept.Count - 1);
            dropped.Insert(0, removed);
        }
        if (kept.Count == 0)
        {
            error = "profile-too-short";
            return false;
        }

        var allocation = kept.ToDictionary(
            static action => action.Id,
            static action => action.MinimumFrames,
            StringComparer.Ordinal);
        int remaining = actionCapacity - allocation.Values.Sum();
        GrowAllocations(
            kept,
            allocation,
            static action => action.PreferredFrames,
            ref remaining);
        GrowAllocations(
            kept,
            allocation,
            static action => action.MaximumFrames,
            ref remaining);

        bool highRiskAction = kept.Any(static action =>
            action.Risk == MotionDirectorRiskLevel.High);
        MotionDirectorCameraDefinition effectiveCamera =
            highRiskAction && requestedCamera.IsStrong
                ? CameraCatalog.First(candidate => string.Equals(
                    candidate.Id,
                    SafeCameraId,
                    StringComparison.Ordinal))
                : requestedCamera;
        string? warningResourceKey =
            highRiskAction && requestedCamera.IsStrong
                ? "UiMotionDirectorWarningFallback"
                : dropped.Count > 0
                    ? "UiMotionDirectorWarningDropped"
                    : null;

        var segments = new List<MotionDirectorSegment>();
        int cursor = 0;
        foreach (MotionDirectorActionDefinition action in kept)
        {
            int duration = allocation[action.Id];
            int anticipationFrames = Math.Max(1, duration / 5);
            int settleFrames = Math.Max(1, duration / 5);
            int actionFrames = duration - anticipationFrames - settleFrames;
            int anticipationEnd = cursor + anticipationFrames;
            int actionEnd = anticipationEnd + actionFrames;
            int settleEnd = actionEnd + settleFrames;
            segments.Add(new(
                MotionDirectorPhase.Anticipation,
                action.Id,
                cursor,
                anticipationEnd,
                $"the subject prepares for {action.EnglishLabel.ToLowerInvariant()} with restrained natural micro-movement"));
            segments.Add(new(
                MotionDirectorPhase.Action,
                action.Id,
                anticipationEnd,
                actionEnd,
                action.H3Phrase));
            segments.Add(new(
                MotionDirectorPhase.Settle,
                action.Id,
                actionEnd,
                settleEnd,
                $"the {action.EnglishLabel.ToLowerInvariant()} eases out and the subject settles naturally without a pose jump"));
            cursor = settleEnd;
        }
        segments.Add(new(
            MotionDirectorPhase.Hold,
            null,
            cursor,
            frameCount,
            "the subject holds the final source-faithful pose with only natural breathing, hair, and fabric micro-motion"));

        MotionDirectorRiskLevel risk = kept
            .Select(static action => action.Risk)
            .Append(effectiveCamera.Risk)
            .Max();
        IReadOnlyList<MotionDirectorSegment> frozenSegments =
            new ReadOnlyCollection<MotionDirectorSegment>(segments);
        string candidatePrompt = CompileH3Prompt(
            frameCount,
            playbackFps,
            effectiveCamera,
            frozenSegments);
        plan = new(
            frameCount,
            playbackFps,
            new ReadOnlyCollection<MotionDirectorActionDefinition>(kept),
            new ReadOnlyCollection<MotionDirectorActionDefinition>(dropped),
            requestedCamera,
            effectiveCamera,
            risk,
            warningResourceKey,
            frozenSegments,
            candidatePrompt);
        return true;
    }

    internal static string FormatSeconds(int frame, int playbackFps)
        => ((decimal)frame / playbackFps).ToString(
            "0.000",
            CultureInfo.InvariantCulture);

    private static string FormatPromptSeconds(int frame, int playbackFps)
        => ((decimal)frame / playbackFps).ToString(
            "0.00",
            CultureInfo.InvariantCulture);

    private static void GrowAllocations(
        IReadOnlyList<MotionDirectorActionDefinition> actions,
        IDictionary<string, int> allocation,
        Func<MotionDirectorActionDefinition, int> target,
        ref int remaining)
    {
        bool grew = true;
        while (remaining > 0 && grew)
        {
            grew = false;
            foreach (MotionDirectorActionDefinition action in actions)
            {
                if (remaining == 0)
                    break;
                if (allocation[action.Id] >= target(action))
                    continue;
                allocation[action.Id]++;
                remaining--;
                grew = true;
            }
        }
    }

    private static string CompileH3Prompt(
        int frameCount,
        int playbackFps,
        MotionDirectorCameraDefinition camera,
        IReadOnlyList<MotionDirectorSegment> segments)
    {
        var integrated = new StringBuilder();
        integrated.Append(
            "Preserve the exact identity, face, body proportions, clothing, visible objects, lighting, palette, and scene layout from <Picture 1>. Keep one continuous shot; do not add people, objects, contact, text, cuts, or scene changes. The shot begins in one continuous take. ");
        integrated.Append(camera.H3Phrase);
        integrated.Append(" The motion develops continuously. Timed motion plan: ");
        for (int index = 0; index < segments.Count; index++)
        {
            MotionDirectorSegment segment = segments[index];
            if (index > 0)
                integrated.Append(' ');
            integrated.Append("At ");
            integrated.Append(FormatPromptSeconds(
                segment.StartFrame,
                playbackFps));
            integrated.Append('–');
            integrated.Append(FormatPromptSeconds(
                segment.EndFrame,
                playbackFps));
            integrated.Append(" seconds, ");
            integrated.Append(segment.H3Phrase);
            integrated.Append('.');
        }
        integrated.Append(
            " By the end, the movement settles naturally without a cut. The final frame at ");
        integrated.Append(FormatPromptSeconds(frameCount, playbackFps));
        integrated.Append(" seconds remains visually consistent with the source image.");

        return "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\n"
            + "integrated_multimodal_description: [Shot 1] "
            + integrated
            + "\n\noverall_soundscape: Quiet image-consistent diegetic ambience with subtle movement and fabric sounds only when visually supported; no invented dialogue or prominent effects."
            + "\n\nnon_diegetic_music: None; do not add music.";
    }
}
