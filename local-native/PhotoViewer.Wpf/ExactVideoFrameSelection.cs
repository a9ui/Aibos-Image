using System.Numerics;

namespace PhotoViewer.Wpf;

internal enum ExactVideoFrameSelectionError
{
    None,
    UnsupportedFps,
    SourceOutOfBounds,
    InvalidPtsMetadata,
    InvalidRange,
}

internal sealed record ExactVideoPtsMetadata(
    int TimeBaseNumerator,
    int TimeBaseDenominator,
    long StartTimestamp,
    string SequenceSha256);

internal sealed record ExactVideoFrameSource(
    int FrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int DurationMs,
    ExactVideoPtsMetadata Pts);

internal readonly record struct ExactVideoRational(
    long Numerator,
    long Denominator);

internal sealed record ExactVideoFrameSelection(
    ExactVideoFrameSource Source,
    int StartFrame,
    int EndFrameExclusive,
    int SelectedFrameCount,
    ExactVideoRational Duration,
    int StartPreviewFrame,
    int MiddlePreviewFrame,
    int EndPreviewFrame);

internal static class ExactVideoFrameSelector
{
    internal const int MaximumSourceFrames = 18_000;
    internal const int MaximumSourceDurationMs = 300_000;

    private static readonly int[] SupportedFpsNumerators = [24, 30, 60];

    internal static bool TrySelect(
        ExactVideoFrameSource? source,
        int startFrame,
        int endFrameExclusive,
        out ExactVideoFrameSelection selection,
        out ExactVideoFrameSelectionError error)
    {
        selection = null!;
        if (!TryValidateSource(source, out error))
            return false;
        if (startFrame < 0
            || startFrame >= source!.FrameCount
            || endFrameExclusive <= startFrame
            || endFrameExclusive > source.FrameCount)
        {
            error = ExactVideoFrameSelectionError.InvalidRange;
            return false;
        }

        // Subtraction is safe only after both half-open endpoints have been
        // proven to be inside the bounded source timeline.
        int selectedFrameCount = endFrameExclusive - startFrame;
        int endPreviewFrame = endFrameExclusive - 1;
        int middlePreviewFrame =
            startFrame + (selectedFrameCount - 1) / 2;
        ExactVideoRational duration = Reduce(
            checked((long)selectedFrameCount * source.FpsDenominator),
            source.FpsNumerator);

        selection = new(
            source,
            startFrame,
            endFrameExclusive,
            selectedFrameCount,
            duration,
            startFrame,
            middlePreviewFrame,
            endPreviewFrame);
        error = ExactVideoFrameSelectionError.None;
        return true;
    }

    internal static bool FitsPolicy(
        ExactVideoFrameSelection? selection,
        int maximumSelectedFrames,
        ExactVideoRational maximumDuration)
    {
        if (selection is null
            || maximumSelectedFrames <= 0
            || selection.SelectedFrameCount > maximumSelectedFrames
            || maximumDuration.Numerator < 0
            || maximumDuration.Denominator <= 0
            || selection.Duration.Numerator < 0
            || selection.Duration.Denominator <= 0)
        {
            return false;
        }

        return (BigInteger)selection.Duration.Numerator
                * maximumDuration.Denominator
            <= (BigInteger)maximumDuration.Numerator
                * selection.Duration.Denominator;
    }

    internal static bool IsSupportedFps(int numerator, int denominator)
        => denominator == 1
            && SupportedFpsNumerators.Contains(numerator);

    internal static bool TryValidateSource(
        ExactVideoFrameSource? source,
        out ExactVideoFrameSelectionError error)
    {
        if (source is null || !IsSourceWithinBounds(source))
        {
            error = ExactVideoFrameSelectionError.SourceOutOfBounds;
            return false;
        }
        if (!IsSupportedFps(source.FpsNumerator, source.FpsDenominator))
        {
            error = ExactVideoFrameSelectionError.UnsupportedFps;
            return false;
        }
        if (!IsValidPtsMetadata(source.Pts))
        {
            error = ExactVideoFrameSelectionError.InvalidPtsMetadata;
            return false;
        }

        error = ExactVideoFrameSelectionError.None;
        return true;
    }

    private static bool IsSourceWithinBounds(ExactVideoFrameSource source)
        => source.FrameCount is > 0 and <= MaximumSourceFrames
            && source.DurationMs is > 0 and <= MaximumSourceDurationMs
            && source.FpsNumerator > 0
            && source.FpsDenominator > 0
            && (long)source.FrameCount * source.FpsDenominator
                <= (long)MaximumSourceDurationMs
                    * source.FpsNumerator
                    / 1_000;

    private static bool IsValidPtsMetadata(ExactVideoPtsMetadata? pts)
        => pts is not null
            && pts.TimeBaseNumerator > 0
            && pts.TimeBaseDenominator > 0
            && IsLowerSha256(pts.SequenceSha256);

    private static bool IsLowerSha256(string? value)
        => value is not null
            && value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private static ExactVideoRational Reduce(long numerator, long denominator)
    {
        long divisor = GreatestCommonDivisor(numerator, denominator);
        return new(numerator / divisor, denominator / divisor);
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            long remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }
}
