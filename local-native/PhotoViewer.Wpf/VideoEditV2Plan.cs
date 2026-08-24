using System.Globalization;

namespace PhotoViewer.Wpf;

internal enum VideoEditV2PlanError
{
    None,
    UnsupportedFps,
    SourceOutOfBounds,
    InvalidRange,
    SelectionTooLong,
}

internal sealed record VideoEditV2SelectionPlan(
    int SourceFrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int StartFrame,
    int EndFrameExclusive,
    int SelectedFrameCount,
    int MaximumSelectionFrames,
    int StartPreviewFrame,
    int MiddlePreviewFrame,
    int EndPreviewFrame,
    double StartSeconds,
    double EndSeconds,
    double DurationSeconds,
    double SourceDurationSeconds)
{
    internal string FrameRateLabel => FpsDenominator == 1
        ? FpsNumerator.ToString(CultureInfo.InvariantCulture)
        : $"{FpsNumerator}/{FpsDenominator}";
}

internal static class VideoEditV2Planner
{
    internal const int MaximumSourceFrames = 18_000;
    internal const int MaximumSourceSeconds = 300;
    internal const int MaximumSelectionSeconds = 5;

    private static readonly int[] SupportedSourceFpsValues = [24, 30, 60];

    internal static IReadOnlyList<int> SupportedSourceFps =>
        SupportedSourceFpsValues;

    internal static bool IsSupportedFps(int numerator, int denominator)
        => denominator == 1
            && SupportedSourceFpsValues.Contains(numerator);

    internal static int MaximumSelectionFrameCount(
        int fpsNumerator,
        int fpsDenominator)
        => IsSupportedFps(fpsNumerator, fpsDenominator)
            ? checked((int)(
                (long)MaximumSelectionSeconds
                    * fpsNumerator
                    / fpsDenominator))
            : 0;

    internal static bool TryPlan(
        int sourceFrameCount,
        int fpsNumerator,
        int fpsDenominator,
        int startFrame,
        int endFrameExclusive,
        out VideoEditV2SelectionPlan plan,
        out VideoEditV2PlanError error)
    {
        plan = null!;
        if (!IsSupportedFps(fpsNumerator, fpsDenominator))
        {
            error = VideoEditV2PlanError.UnsupportedFps;
            return false;
        }

        if (sourceFrameCount <= 0
            || sourceFrameCount > MaximumSourceFrames
            || (long)sourceFrameCount * fpsDenominator
                > (long)MaximumSourceSeconds * fpsNumerator)
        {
            error = VideoEditV2PlanError.SourceOutOfBounds;
            return false;
        }

        if (startFrame < 0
            || endFrameExclusive <= startFrame
            || endFrameExclusive > sourceFrameCount)
        {
            error = VideoEditV2PlanError.InvalidRange;
            return false;
        }

        int selectedFrameCount = endFrameExclusive - startFrame;
        int maximumSelectionFrames = MaximumSelectionFrameCount(
            fpsNumerator,
            fpsDenominator);
        if (selectedFrameCount > maximumSelectionFrames)
        {
            error = VideoEditV2PlanError.SelectionTooLong;
            return false;
        }

        int endPreviewFrame = endFrameExclusive - 1;
        int middlePreviewFrame = startFrame + (selectedFrameCount - 1) / 2;
        double secondsPerFrame = (double)fpsDenominator / fpsNumerator;
        plan = new VideoEditV2SelectionPlan(
            sourceFrameCount,
            fpsNumerator,
            fpsDenominator,
            startFrame,
            endFrameExclusive,
            selectedFrameCount,
            maximumSelectionFrames,
            startFrame,
            middlePreviewFrame,
            endPreviewFrame,
            startFrame * secondsPerFrame,
            endFrameExclusive * secondsPerFrame,
            selectedFrameCount * secondsPerFrame,
            sourceFrameCount * secondsPerFrame);
        error = VideoEditV2PlanError.None;
        return true;
    }

    internal static bool TryFrameCountFromDuration(
        double durationSeconds,
        int fpsNumerator,
        int fpsDenominator,
        out int frameCount)
    {
        frameCount = 0;
        if (!IsSupportedFps(fpsNumerator, fpsDenominator)
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0
            || durationSeconds > MaximumSourceSeconds)
        {
            return false;
        }

        double measured = durationSeconds * fpsNumerator / fpsDenominator;
        if (!double.IsFinite(measured)
            || measured < 1
            || measured > MaximumSourceFrames + 0.5)
        {
            return false;
        }

        frameCount = checked((int)Math.Round(
            measured,
            MidpointRounding.AwayFromZero));
        return frameCount is > 0 and <= MaximumSourceFrames;
    }

    internal static string FormatFrameTime(
        int frame,
        int fpsNumerator,
        int fpsDenominator)
    {
        if (frame < 0 || !IsSupportedFps(fpsNumerator, fpsDenominator))
            return "--";
        double seconds = (double)frame * fpsDenominator / fpsNumerator;
        return seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
