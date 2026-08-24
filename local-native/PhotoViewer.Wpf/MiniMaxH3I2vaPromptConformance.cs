using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

internal enum MiniMaxH3ConformanceSeverity
{
    Error,
    Warning,
}

internal enum MiniMaxH3ConformanceGroup
{
    Format,
    Timeline,
    References,
    Preservation,
}

internal sealed record MiniMaxH3ConformanceDiagnostic(
    string Code,
    MiniMaxH3ConformanceSeverity Severity,
    MiniMaxH3ConformanceGroup Group,
    string Path,
    string Reason,
    IReadOnlyList<string> EvidenceRefs);

internal sealed record MiniMaxH3ConformanceResult(
    string ProfileId,
    string GuideRevision,
    string NormalizedPrompt,
    IReadOnlyList<MiniMaxH3ConformanceDiagnostic> Diagnostics)
{
    internal bool Conformant => !Diagnostics.Any(static diagnostic =>
        diagnostic.Severity == MiniMaxH3ConformanceSeverity.Error);

    internal int ErrorCount => Diagnostics.Count(static diagnostic =>
        diagnostic.Severity == MiniMaxH3ConformanceSeverity.Error);

    internal int WarningCount => Diagnostics.Count(static diagnostic =>
        diagnostic.Severity == MiniMaxH3ConformanceSeverity.Warning);
}

internal static partial class MiniMaxH3I2vaPromptConformance
{
    internal const string ProfileId = "minimax-h3-i2va";
    internal const string GuideRepository =
        "https://github.com/MiniMax-AI/MiniMax-H3";
    internal const string GuideRevision =
        "35491cdba2adfe62a510f725e8619f8e58783ea2";
    internal const string SkillPath =
        "skills/h3-prompt-writing/SKILL.md";
    internal const string SkillBlobSha1 =
        "48d3bb470fefb96ced7e10f908c53d54d9785e62";
    internal const int SkillBytes = 1912;
    internal const string BaseGuidePath =
        "skills/h3-prompt-writing/references/base-en.txt";
    internal const string BaseGuideBlobSha1 =
        "40cf586a634d677d6b7107b367cf0ec9621be728";
    internal const int BaseGuideBytes = 15773;
    internal const string CapabilityPath = "README.md";
    internal const string CapabilityBlobSha1 =
        "f70c43ecf20d367c343d4c2998d126bfcca76220";
    internal const int CapabilityBytes = 36059;
    internal const string OfficialNoMusicLiteral = "N/A";
    internal const string CompatibleNoMusicLiteral =
        "None; do not add music.";
    internal const int MaximumRawUtf16CodeUnits = 8000;
    internal const string Opening =
        "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.";
    internal const string IntegratedPrefix =
        "\n\nintegrated_multimodal_description: [Shot 1] ";
    internal const string SoundscapePrefix =
        "\n\noverall_soundscape: ";
    internal const string MusicPrefix =
        "\n\nnon_diegetic_music: ";
    internal const string IntegratedMarker =
        "integrated_multimodal_description:";
    internal const string SoundscapeMarker = "overall_soundscape:";
    internal const string MusicMarker = "non_diegetic_music:";

    internal static MiniMaxH3ConformanceResult Analyze(string raw)
    {
        var diagnostics = new List<MiniMaxH3ConformanceDiagnostic>();
        if (raw.Length == 0)
        {
            AddError(
                diagnostics,
                "H3_FORMAT_EMPTY",
                MiniMaxH3ConformanceGroup.Format,
                "$",
                "The candidate is empty.",
                "contract:candidateGrammar.emptySectionRule");
            return Result("", diagnostics);
        }
        if (raw.Length > MaximumRawUtf16CodeUnits)
        {
            AddError(
                diagnostics,
                "H3_FORMAT_TOO_LONG",
                MiniMaxH3ConformanceGroup.Format,
                "$",
                "The raw candidate exceeds 8,000 UTF-16 code units.",
                "contract:candidateGrammar.maximumRawUtf16CodeUnits");
            return Result("", diagnostics);
        }

        string normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r'))
        {
            AddError(
                diagnostics,
                "H3_FORMAT_CARRIAGE_RETURN",
                MiniMaxH3ConformanceGroup.Format,
                "$",
                "A carriage return remains after CRLF normalization.",
                "contract:candidateGrammar.lineEndingRule");
        }
        if (normalized.Any(static character =>
                char.IsControl(character)
                && character is not '\n' and not '\t'))
        {
            AddError(
                diagnostics,
                "H3_FORMAT_CONTROL_CHARACTER",
                MiniMaxH3ConformanceGroup.Format,
                "$",
                "The candidate contains a disallowed control character.",
                "contract:candidateGrammar.controlCharacterRule");
        }

        if (!normalized.StartsWith(
                Opening + IntegratedPrefix,
                StringComparison.Ordinal))
        {
            AddError(
                diagnostics,
                "H3_REFERENCE_FIRST_FRAME_BINDING",
                MiniMaxH3ConformanceGroup.References,
                "$.firstFrameBinding",
                "The exact I2VA first-frame binding and Shot 1 anchor are missing or misplaced.",
                "guide:i2va.first-frame-binding",
                "contract:candidateGrammar.opening");
        }

        int integratedCount = CountOccurrences(normalized, IntegratedMarker);
        int soundscapeCount = CountOccurrences(normalized, SoundscapeMarker);
        int musicCount = CountOccurrences(normalized, MusicMarker);
        if (integratedCount != 1
            || soundscapeCount != 1
            || musicCount != 1)
        {
            AddError(
                diagnostics,
                integratedCount == 0 || soundscapeCount == 0 || musicCount == 0
                    ? "H3_FORMAT_SECTION_MISSING"
                    : "H3_FORMAT_SECTION_DUPLICATE",
                MiniMaxH3ConformanceGroup.Format,
                "$.sections",
                "The three H3 sections must each occur exactly once.",
                "guide:i2va.section-order",
                "contract:candidateGrammar.markerRule");
            return Result(normalized, diagnostics);
        }

        int integratedMarkerIndex = normalized.IndexOf(
            IntegratedMarker,
            StringComparison.Ordinal);
        int soundscapeMarkerIndex = normalized.IndexOf(
            SoundscapeMarker,
            StringComparison.Ordinal);
        int musicMarkerIndex = normalized.IndexOf(
            MusicMarker,
            StringComparison.Ordinal);
        if (integratedMarkerIndex != Opening.Length + 2
            || integratedMarkerIndex >= soundscapeMarkerIndex
            || soundscapeMarkerIndex >= musicMarkerIndex)
        {
            AddError(
                diagnostics,
                "H3_FORMAT_SECTION_ORDER",
                MiniMaxH3ConformanceGroup.Format,
                "$.sections",
                "The H3 sections are not in the required I2VA order.",
                "guide:i2va.section-order",
                "contract:candidateGrammar.orderedSections");
            return Result(normalized, diagnostics);
        }

        int soundscapePrefixIndex = normalized.IndexOf(
            SoundscapePrefix,
            integratedMarkerIndex + IntegratedPrefix.Length,
            StringComparison.Ordinal);
        int musicPrefixIndex = normalized.IndexOf(
            MusicPrefix,
            soundscapeMarkerIndex + SoundscapePrefix.Length,
            StringComparison.Ordinal);
        if (soundscapePrefixIndex < 0 || musicPrefixIndex < 0)
        {
            AddError(
                diagnostics,
                "H3_FORMAT_SECTION_SEPARATOR",
                MiniMaxH3ConformanceGroup.Format,
                "$.sections",
                "The H3 section separators or labels are malformed.",
                "contract:candidateGrammar.sectionSeparator");
            return Result(normalized, diagnostics);
        }

        string integrated = normalized[
            (Opening.Length + IntegratedPrefix.Length)..soundscapePrefixIndex];
        string soundscape = normalized[
            (soundscapePrefixIndex + SoundscapePrefix.Length)..musicPrefixIndex];
        string music = normalized[(musicPrefixIndex + MusicPrefix.Length)..];
        if (string.IsNullOrWhiteSpace(integrated)
            || string.IsNullOrWhiteSpace(soundscape)
            || string.IsNullOrWhiteSpace(music))
        {
            AddError(
                diagnostics,
                "H3_FORMAT_SECTION_EMPTY",
                MiniMaxH3ConformanceGroup.Format,
                "$.sections",
                "Every H3 section must contain a non-empty body.",
                "contract:candidateGrammar.emptySectionRule");
        }

        foreach (Match match in KnownTimedPhrase().Matches(integrated))
        {
            if (match.Groups["startFraction"].Length != 2
                || match.Groups["endFraction"].Length != 2)
            {
                AddError(
                    diagnostics,
                    "H3_TIMELINE_PRECISION",
                    MiniMaxH3ConformanceGroup.Timeline,
                    "$.integrated_multimodal_description.timeline",
                    "Known H3 timed phrases must use two fractional digits.",
                    "guide:base-en.final-prompt-structure.effective-duration-format");
                break;
            }
        }
        if (!diagnostics.Any(static diagnostic =>
                string.Equals(
                    diagnostic.Code,
                    "H3_TIMELINE_PRECISION",
                    StringComparison.Ordinal)))
        {
            foreach (Match match in KnownSingleTime().Matches(normalized))
            {
                if (match.Groups["fraction"].Length != 2)
                {
                    AddError(
                        diagnostics,
                        "H3_TIMELINE_PRECISION",
                        MiniMaxH3ConformanceGroup.Timeline,
                        "$.integrated_multimodal_description.timeline",
                        "Known H3 time references must use two fractional digits.",
                        "guide:base-en.final-prompt-structure.effective-duration-format");
                    break;
                }
            }
        }

        int shotOneIndex = normalized.IndexOf(
            "[Shot 1",
            Opening.Length + IntegratedPrefix.Length,
            StringComparison.Ordinal);
        while (shotOneIndex >= 0)
        {
            int closingIndex = shotOneIndex + "[Shot 1".Length;
            bool laterMultiDigitShot = closingIndex < normalized.Length
                && char.IsAsciiDigit(normalized[closingIndex]);
            if (!laterMultiDigitShot
                && (closingIndex >= normalized.Length
                    || normalized[closingIndex] != ']'))
            {
                AddError(
                    diagnostics,
                    "H3_REFERENCE_SHOT1_TIMESTAMP",
                    MiniMaxH3ConformanceGroup.References,
                    "$.references.shot1",
                    "Shot 1 is the untimed first-frame anchor and cannot carry a timestamp.",
                    "guide:i2va.shot1-untimed");
                break;
            }
            shotOneIndex = normalized.IndexOf(
                "[Shot 1",
                closingIndex + 1,
                StringComparison.Ordinal);
        }

        return Result(normalized, diagnostics);
    }

    [GeneratedRegex(
        @"\bAt\s+\d+\.(?<startFraction>\d+)[–-]\d+\.(?<endFraction>\d+)\s+seconds\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTimedPhrase();

    [GeneratedRegex(
        @"\bat\s+\d+(?:\.(?<fraction>\d+))?\s+seconds\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KnownSingleTime();

    private static int CountOccurrences(string value, string marker)
    {
        int count = 0;
        int cursor = 0;
        while (cursor < value.Length)
        {
            int index = value.IndexOf(marker, cursor, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            cursor = index + marker.Length;
        }
        return count;
    }

    private static void AddError(
        ICollection<MiniMaxH3ConformanceDiagnostic> diagnostics,
        string code,
        MiniMaxH3ConformanceGroup group,
        string path,
        string reason,
        params string[] evidenceRefs)
        => diagnostics.Add(new(
            code,
            MiniMaxH3ConformanceSeverity.Error,
            group,
            path,
            reason,
            Array.AsReadOnly(evidenceRefs)));

    private static MiniMaxH3ConformanceResult Result(
        string normalized,
        List<MiniMaxH3ConformanceDiagnostic> diagnostics)
        => new(
            ProfileId,
            GuideRevision,
            normalized,
            new ReadOnlyCollection<MiniMaxH3ConformanceDiagnostic>(
                diagnostics));
}
