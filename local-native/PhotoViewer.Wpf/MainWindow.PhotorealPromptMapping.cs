using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxPhotorealPromptMappingCount = 256;
    private const int MaxPhotorealPromptMappingTagLength = 160;
    private const int MaxPhotorealPromptMappingOutputLength = 400;
    private const int CurrentPhotorealPromptMappingDefaultsRevision = 9;
    private readonly List<PhotorealPromptMappingState> _photorealPromptMappings = [];
    private int _photorealPromptMappingDefaultsRevision =
        CurrentPhotorealPromptMappingDefaultsRevision;

    private static List<PhotorealPromptMappingState> CreateDefaultPhotorealPromptMappings()
    {
        var rows = new List<PhotorealPromptMappingState>();
        void Add(string category, bool enabled, params string[] tags)
        {
            rows.AddRange(tags.Select(tag => new PhotorealPromptMappingState
            {
                Category = category,
                Enabled = enabled,
                SourceTag = tag,
                OutputPrompt = tag,
            }));
        }
        void AddMapped(
            string category,
            bool enabled,
            params (string SourceTag, string OutputPrompt)[] mappings)
        {
            rows.AddRange(mappings.Select(mapping => new PhotorealPromptMappingState
            {
                Category = category,
                Enabled = enabled,
                SourceTag = mapping.SourceTag,
                OutputPrompt = mapping.OutputPrompt,
            }));
        }

        const string worriedBrowsPrompt =
            "preserve the exact source eyebrow curvature and inner-brow height; do not neutralize or intensify the expression or add forehead wrinkles";
        const string torogaoPrompt =
            "do not change the facial expression already visible in the source: preserve the exact eyebrow curvature, eyelid openness, gaze, mouth opening, and cheek flush; treat torogao only as metadata describing the source, not as an instruction to add fear, surprise, smiling, blankness, or forehead wrinkles";
        const string anguishPrompt =
            "preserve the exact source anguish through the existing eyebrow curvature, eyelid tension, gaze, and mouth shape; do not intensify, neutralize, smile, or add forehead wrinkles";
        const string invertedNipplesPrompt =
            "preserve the exact visible source nipple and areola geometry, including any central nipple inversion; do not flatten, erase, blur, merge, protrude, or otherwise redesign it";
        AddMapped("表情・顔（現実的な感情変換）", true,
            ("troubled eyebrows", worriedBrowsPrompt),
            ("trouble_eyebrows", worriedBrowsPrompt),
            ("torogao", torogaoPrompt),
            ("anguish", anguishPrompt));
        AddMapped("表情・顔（弱い形状）", true,
            ("narrowed eyes", "slightly narrowed eyes"),
            ("upturned eyes", "slightly upward-tilted outer eye corners"),
            ("open mouth", "lips slightly parted with a small natural opening"),
            ("wince", "subtle tension around the eyes and mouth"),
            ("blush", "subtle natural cheek flush"),
            ("full-face blush", "soft diffuse warmth on cheeks and nose"),
            ("profile", "face shown in side profile"),
            ("head back", "head tilted backward"),
            ("looking at another", "eyes directed toward another person"),
            ("light frown", "slightly downturned lip corners"),
            ("tongue out", "tongue visibly extended"),
            ("closed eyes", "eyes fully closed"),
            ("round eyes", "wide round eyes"),
            ("droopy eyes", "softly drooping outer eye corners"),
            ("thin_eyebrows", "thin natural eyebrows"),
            ("surprised", "slightly widened eyes and gently raised eyebrows"));
        AddMapped("表情・顔（比較用）", false,
            ("light smile", "a faint closed-lip smile"),
            ("cheerfully smile", "a cheerful natural smile"),
            ("smile", "a natural smile"),
            ("grimace", "a tense grimace"),
            ("tearing up", "slightly watery eyes"),
            ("flustered", "subtle cheek warmth and mild facial tension"),
            ("confused", "slightly uncertain gaze and mild brow tension"),
            ("seductive smile", "a subtle suggestive smile"),
            ("female_orgasm", "female orgasm"),
            ("long eyelashes", "long natural eyelashes"));
        AddMapped("表情・視線（形状）", true,
            ("frustrated", "slightly furrowed brows, narrowed eyes, and a tense mouth"),
            ("looking at viewer", "both irises directed toward the camera, direct eye contact"),
            ("lookingatviewer", "both irises directed toward the camera, direct eye contact"),
            ("averting eyes", "gaze visibly directed away from the viewer"),
            ("looking away", "gaze visibly directed away from the viewer"),
            ("one eye closed", "one eye fully closed and the other eye open"),
            ("covered mouth", "mouth visibly occluded"));
        Add("表情・顔（比較用）", false,
            "embarrassed", "shy", "humiliation", "female orgasm",
            "craving", "in_heat", "in heat", "forced orgasm",
            "aroused", "fucked silly", "heavy breathing",
            "claving", "cute", "seductive", "CuteSeductive");
        AddMapped("成人形状（実写形状変換）", true,
            ("inverted nipples", invertedNipplesPrompt));
        Add("成人形状", true,
            "puffy nipples", "huge nipples", "erect_nipple",
            "covered nipples", "areola slip", "large penis", "clitoris",
            "spread pussy");
        Add("成人形状（参照優先）", false,
            "sagging breasts", "unaligned breasts", "medium breasts", "large breasts",
            "huge breasts", "small breasts", "gigantic breasts");
        Add("液体・濡れ", true,
            "breast milk", "lactation", "pussy juice puddle", "pussy juice",
            "pussy juice trail", "wet_skin", "wet skin", "mucus", "mucus on body",
            "mucus on face", "mucus on hair", "mucus trail", "cum pool",
            "cum on body", "cum on breasts", "cum on clothes", "cum on hair",
            "cum overflow", "cum string", "cum_in_armpit", "cum_trail", "cum trail",
            "cumdrip", "precum", "cum in pussy");
        AddMapped("液体・濡れ（可視形状）", true,
            ("wet", "visibly wet skin with water droplets"),
            ("shiny skin", "subtle natural skin sheen with soft realistic highlights and visible skin texture"),
            ("wet hair", "wet hair clumped into damp strands"),
            ("wet clothes", "visibly soaked fabric"),
            ("cum_pool", "visible pool of fluid"),
            ("dripping", "visible liquid dripping"),
            ("cum on stomach", "visible fluid on the abdomen"),
            ("cum in mouth", "visible fluid in the mouth"),
            ("saliva trail", "a visible strand of saliva"),
            ("saliva", "visible saliva"),
            ("breast milk in container", "visible milk collected in a container"),
            ("pouring milk from nipple to a cup", "visible milk pouring into a cup"),
            ("pouring breast-milk into a teacup on table", "visible milk pouring into a cup"),
            ("lactating into container", "visible milk collected in a container"));
        Add("液体・濡れ（増量語）", false,
            "forced lactation", "projectile lactation", "bukkake", "excessive cum",
            "projectile cum", "cum_everywhere");
        AddMapped("液体・濡れ（比較用）", false,
            ("projectile cumdrip", "projectile fluid droplets"));
        AddMapped("ガラス・表面への圧迫", true,
            ("glass wall", "a clear glass wall visibly between the subject and camera"),
            ("stuck in a glass box", "body visibly enclosed inside a transparent glass box"),
            ("against glass", "body visibly pressed against glass"),
            ("breasts on glass", "breasts visibly compressed against glass"),
            ("hands on glass", "hands visibly pressed flat against glass"),
            ("ass on glass", "buttocks visibly compressed against glass"),
            ("pussy on glass", "vulva visibly pressed against glass"),
            ("breast press", "breasts visibly compressed against a surface"),
            ("ass press", "buttocks visibly compressed against a surface"));
        AddMapped("ガラス・表面への圧迫（比較用）", false,
            ("against fourth wall", "body pressed toward the camera-facing surface"));
        AddMapped("身体の局所形状", true,
            ("nipple", "realistic nipple and areola texture with subtle Montgomery glands, fine natural creases, and shallow indentations"),
            ("nipples", "realistic nipple and areola texture with subtle Montgomery glands, fine natural creases, and shallow indentations"),
            ("wrinkled nipples", "visible natural wrinkles on the nipples"),
            ("unaligned nipples", "nipples visibly asymmetric in height"),
            ("spread nipple", "a nipple visibly stretched sideways"),
            ("light areolae", "light-colored areolae"),
            ("nipple press", "a nipple visibly flattened by pressure"),
            ("nipple pinch", "nipples visibly pinched between fingers"),
            ("nipples pinch", "nipples visibly pinched between fingers"),
            ("nipple between fingers", "a nipple visibly held between fingers"),
            ("nipple pull", "a nipple visibly pulled outward by fingers"),
            ("nipple rub", "fingertips visibly rubbing the nipple"),
            ("nipple flick", "a fingertip visibly touching the nipple"),
            ("nipple tweak", "nipples visibly pinched between fingertips"));
        AddMapped("ポーズ・身体配置", true,
            ("arched back", "back visibly arched"),
            ("spread legs", "legs visibly spread apart"),
            ("wide spread legs", "legs visibly spread apart"),
            ("legs apart", "legs visibly spread apart"),
            ("arms up", "both arms visibly raised"),
            ("bent over", "torso bent forward at the waist"),
            ("wariza", "seated with knees forward and lower legs folded beside the hips"),
            ("on back", "lying on the back"),
            ("leaning forward", "torso visibly leaning forward"),
            ("kneeling", "kneeling on both knees"),
            ("leaning back", "torso visibly leaning backward"),
            ("arms behind back", "both arms held behind the back"),
            ("all fours", "supported on both hands and knees"),
            ("arms behind head", "both arms raised with hands behind the head"));
        AddMapped("ポーズ・身体配置（比較用）", false,
            ("standing", "standing upright"),
            ("lying", "body in a lying position"));
        Add("拘束具", true,
            "bound arms", "bound legs", "stationary restraints", "x-cross (bdsm)",
            "shackle", "strappado");
        AddMapped("拘束具（可視形状）", true,
            ("ball gag", "a clearly visible spherical ball between the teeth with a strap around the back of the head"),
            ("ballgag", "a clearly visible spherical ball between the teeth with a strap around the back of the head"),
            ("blindfold", "an opaque blindfold completely covering both eyes and secured around the head"),
            ("black blindfold", "an opaque black blindfold completely covering both eyes and secured around the head"),
            ("bound thighs", "thighs visibly secured together with rope or straps"),
            ("bound torso", "torso visibly secured with rope or straps"),
            ("suspension", "body visibly suspended by restraints"),
            ("spreader bar", "a visible spreader bar securing the ankles apart"),
            ("bound ankles", "ankles visibly tied together"),
            ("chained wrists", "wrists visibly secured by metal chains"),
            ("bound wrists", "wrists visibly tied together"),
            ("handcuffs", "metal handcuffs visibly securing the wrists"));
        AddMapped("可視物・装置", true,
            ("hold in mouth", "an object visibly held in the mouth"),
            ("milking machine", "clearly visible milking-machine apparatus"),
            ("breast pump", "clearly visible breast-pump apparatus"),
            ("vibrator on nipple", "a visible vibrator touching the nipple"),
            ("tentacle wall", "clearly visible tentacles with suction cups"),
            ("tentacle pit", "clearly visible tentacles with suction cups"),
            ("suction tentacles", "clearly visible tentacles with suction cups"),
            ("suction cups", "clearly visible tentacles with suction cups"),
            ("veiny tentacles", "clearly visible tentacles with suction cups"));
        AddMapped("液体・位置と動き", true,
            ("projectile trail", "a visible airborne trail of liquid droplets"),
            ("cum bath", "body visibly immersed in a large pool of fluid"),
            ("wading semen", "lower legs visibly wading through a pool of fluid"),
            ("cum on crotch", "visible fluid on the crotch"),
            ("cum on legs", "visible fluid on the legs"),
            ("cum on arm", "visible fluid on the arm"),
            ("in cum container", "body visibly inside a container filled with fluid"),
            ("cum shower", "visible streams and droplets of fluid falling over the body"),
            ("cum string between breasts", "a visible strand of fluid stretched between the breasts"));
        AddMapped("液体・位置と動き（比較用）", false,
            ("cumdump", "visible fluid on and around the body"),
            ("unusual bodily fluids", "visible bodily fluid"));
        Add("拘束（広義）", false, "restrained", "bdsm");
        var normalizedSources = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(row => normalizedSources.Add(
                NormalizeA1111PromptTag(row.SourceTag)))
            .ToList();
    }

    private void RestorePhotorealPromptMappings(
        IReadOnlyList<PhotorealPromptMappingState>? persisted,
        int persistedDefaultsRevision)
    {
        _photorealPromptMappings.Clear();
        List<PhotorealPromptMappingState> defaults =
            CreateDefaultPhotorealPromptMappings();
        IReadOnlyList<PhotorealPromptMappingState> source;
        if (persisted is null)
        {
            source = defaults;
        }
        else if (persisted.Count == 0
            || persistedDefaultsRevision >=
                CurrentPhotorealPromptMappingDefaultsRevision)
        {
            source = persisted;
        }
        else
        {
            var upgraded = persisted.Select(static row => row.Clone()).ToList();
            if (persistedDefaultsRevision < 1)
                ApplyPromptMappingDefaultsRevision1(upgraded);
            if (persistedDefaultsRevision < 2)
                ApplyPromptMappingDefaultsRevision2(upgraded);
            if (persistedDefaultsRevision < 3)
                ApplyPromptMappingDefaultsRevision3(upgraded);
            if (persistedDefaultsRevision < 4)
                ApplyPromptMappingDefaultsRevision4(upgraded);
            if (persistedDefaultsRevision < 9)
                ApplyPromptMappingDefaultsRevision9(upgraded);
            var knownSources = new HashSet<string>(
                upgraded.Select(static row =>
                    NormalizeA1111PromptTag(row.SourceTag)),
                StringComparer.OrdinalIgnoreCase);
            upgraded.AddRange(defaults
                .Where(row => knownSources.Add(
                    NormalizeA1111PromptTag(row.SourceTag)))
                .Select(static row => row.Clone()));
            source = upgraded;
        }
        var normalizedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealPromptMappingState? candidate in source.Take(MaxPhotorealPromptMappingCount))
        {
            if (candidate is null)
                continue;
            string sourceTag = (candidate.SourceTag ?? "").Trim();
            string outputPrompt = (candidate.OutputPrompt ?? "").Trim();
            if (sourceTag.Length is < 1 or > MaxPhotorealPromptMappingTagLength
                || outputPrompt.Length is < 1 or > MaxPhotorealPromptMappingOutputLength)
            {
                continue;
            }
            string normalizedSource = NormalizeA1111PromptTag(sourceTag);
            if (normalizedSource.Length == 0
                || !normalizedSources.Add(normalizedSource))
            {
                continue;
            }
            string category = string.IsNullOrWhiteSpace(candidate.Category)
                ? "カスタム"
                : candidate.Category.Trim();
            _photorealPromptMappings.Add(new PhotorealPromptMappingState
            {
                Enabled = candidate.Enabled,
                Category = category[..Math.Min(category.Length, 40)],
                SourceTag = sourceTag,
                OutputPrompt = outputPrompt,
                ExtensionData = candidate.ExtensionData is null
                    ? null
                    : new Dictionary<string, JsonElement>(
                        candidate.ExtensionData,
                StringComparer.Ordinal),
            });
        }
        _photorealPromptMappingDefaultsRevision =
            CurrentPhotorealPromptMappingDefaultsRevision;
        RefreshPhotorealPromptMappingSummary();
    }

    private static void ApplyPromptMappingDefaultsRevision1(
        List<PhotorealPromptMappingState> mappings)
    {
        PhotorealPromptMappingState? troubled = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                "troubled eyebrows",
                StringComparison.OrdinalIgnoreCase));
        if (troubled is not null
            && (string.Equals(
                    troubled.OutputPrompt.Trim(),
                    "troubled eyebrows",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    troubled.OutputPrompt.Trim(),
                    "slightly raised inner eyebrow ends",
                    StringComparison.OrdinalIgnoreCase)))
        {
            troubled.OutputPrompt =
                "barely raised inner brow ends, relaxed forehead";
        }

        DisableLegacyDirectMapping(mappings, "light smile");
        DisableLegacyDirectMapping(mappings, "cheerfully smile");
        DisableLegacyDirectMapping(mappings, "smile");
    }

    private static void ApplyPromptMappingDefaultsRevision2(
        List<PhotorealPromptMappingState> mappings)
    {
        ReplaceLegacyDirectMapping(
            mappings,
            "ball gag",
            "a clearly visible spherical ball between the teeth with a strap around the back of the head");
        ReplaceLegacyDirectMapping(
            mappings,
            "ballgag",
            "a clearly visible spherical ball between the teeth with a strap around the back of the head");
        ReplaceLegacyDirectMapping(
            mappings,
            "black blindfold",
            "an opaque black blindfold completely covering both eyes and secured around the head");
        ReplaceAndEnableLegacyDirectMapping(
            mappings,
            "looking at viewer",
            "both irises directed toward the camera, direct eye contact");
        ReplaceAndEnableLegacyMapping(
            mappings,
            "shiny skin",
            "moist skin with realistic highlights",
            "subtle natural skin sheen with soft realistic highlights and visible skin texture");
    }

    private static void ApplyPromptMappingDefaultsRevision3(
        List<PhotorealPromptMappingState> mappings)
    {
        const string replacement =
            "lips slightly parted with a small natural opening";
        ReplaceLegacyDirectMapping(mappings, "open mouth", replacement);
        ReplaceLegacyMapping(
            mappings,
            "open mouth",
            "mouth visibly open with separated lips",
            replacement);
    }

    private static void ApplyPromptMappingDefaultsRevision4(
        List<PhotorealPromptMappingState> mappings)
    {
        DisableKnownDefaultMapping(
            mappings,
            "troubled eyebrows",
            "troubled eyebrows",
            "slightly raised inner eyebrow ends",
            "barely raised inner brow ends, relaxed forehead");
        DisableKnownDefaultMapping(
            mappings,
            "trouble eyebrows",
            "trouble eyebrows",
            "barely raised inner brow ends, relaxed forehead");
    }

    private static void ApplyPromptMappingDefaultsRevision9(
        List<PhotorealPromptMappingState> mappings)
    {
        ReplaceKnownDefaultMapping(
            mappings,
            "troubled eyebrows",
            "preserve the exact source eyebrow curvature and inner-brow height; do not neutralize or intensify the expression or add forehead wrinkles",
            "troubled eyebrows",
            "slightly raised inner eyebrow ends",
            "barely raised inner brow ends, relaxed forehead",
            "clearly worried eyebrows with raised inner brow ends and mild brow tension, preserving the source emotion without deep forehead wrinkles");
        ReplaceKnownDefaultMapping(
            mappings,
            "trouble_eyebrows",
            "preserve the exact source eyebrow curvature and inner-brow height; do not neutralize or intensify the expression or add forehead wrinkles",
            "trouble_eyebrows",
            "barely raised inner brow ends, relaxed forehead",
            "clearly worried eyebrows with raised inner brow ends and mild brow tension, preserving the source emotion without deep forehead wrinkles");
        ReplaceKnownDefaultMapping(
            mappings,
            "torogao",
            "do not change the facial expression already visible in the source: preserve the exact eyebrow curvature, eyelid openness, gaze, mouth opening, and cheek flush; treat torogao only as metadata describing the source, not as an instruction to add fear, surprise, smiling, blankness, or forehead wrinkles",
            "torogao",
            "a source-faithful strained and overwhelmed adult expression with worried brows, tense eyelids, the source eye directions and openness, naturally parted or open lips matching the source, and diffuse cheek flushing; no blank stare, broad smile, or deep forehead wrinkles");
        ReplaceKnownDefaultMapping(
            mappings,
            "anguish",
            "preserve the exact source anguish through the existing eyebrow curvature, eyelid tension, gaze, and mouth shape; do not intensify, neutralize, smile, or add forehead wrinkles",
            "anguish",
            "a clearly visible but natural anguished expression with worried brows, tense eyelids, and mouth tension matching the source; no blank stare or exaggerated forehead wrinkles");
        ReplaceKnownDefaultMapping(
            mappings,
            "inverted nipples",
            "preserve the exact visible source nipple and areola geometry, including any central nipple inversion; do not flatten, erase, blur, merge, protrude, or otherwise redesign it",
            "inverted nipples",
            "anatomically realistic inverted nipples with a visible central indentation, preserving the source-side inversion without flattening, erasing, or merging the nipple and areola structure");
    }

    private static void ReplaceKnownDefaultMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        string replacement,
        params string[] knownOutputs)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                NormalizeA1111PromptTag(sourceTag),
                StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
            return;

        string output = NormalizeA1111PromptTag(mapping.OutputPrompt);
        if (!knownOutputs.Any(known => string.Equals(
                output,
                NormalizeA1111PromptTag(known),
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        mapping.OutputPrompt = replacement;
    }

    private static void DisableKnownDefaultMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        params string[] knownOutputs)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
            return;

        string output = NormalizeA1111PromptTag(mapping.OutputPrompt);
        if (knownOutputs.Any(known => string.Equals(
                output,
                NormalizeA1111PromptTag(known),
                StringComparison.OrdinalIgnoreCase)))
        {
            mapping.Enabled = false;
        }
    }

    private static void ReplaceLegacyDirectMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        string replacement)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is not null
            && string.Equals(
                NormalizeA1111PromptTag(mapping.OutputPrompt),
                sourceTag,
                StringComparison.OrdinalIgnoreCase))
        {
            mapping.OutputPrompt = replacement;
        }
    }

    private static void ReplaceAndEnableLegacyDirectMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        string replacement)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is not null
            && string.Equals(
                NormalizeA1111PromptTag(mapping.OutputPrompt),
                sourceTag,
                StringComparison.OrdinalIgnoreCase))
        {
            mapping.OutputPrompt = replacement;
            mapping.Enabled = true;
        }
    }

    private static void ReplaceLegacyMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        string legacyOutput,
        string replacement)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is not null
            && string.Equals(
                NormalizeA1111PromptTag(mapping.OutputPrompt),
                NormalizeA1111PromptTag(legacyOutput),
                StringComparison.OrdinalIgnoreCase))
        {
            mapping.OutputPrompt = replacement;
        }
    }

    private static void ReplaceAndEnableLegacyMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag,
        string legacyOutput,
        string replacement)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is not null
            && string.Equals(
                mapping.OutputPrompt.Trim(),
                legacyOutput,
                StringComparison.OrdinalIgnoreCase))
        {
            mapping.OutputPrompt = replacement;
            mapping.Enabled = true;
        }
    }

    private static void DisableLegacyDirectMapping(
        IEnumerable<PhotorealPromptMappingState> mappings,
        string sourceTag)
    {
        PhotorealPromptMappingState? mapping = mappings.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (mapping is not null
            && string.Equals(
                mapping.OutputPrompt.Trim(),
                sourceTag,
                StringComparison.OrdinalIgnoreCase))
        {
            mapping.Enabled = false;
        }
    }

    private List<PhotorealPromptMappingState> SnapshotPhotorealPromptMappings()
        => _photorealPromptMappings.Select(static row => new PhotorealPromptMappingState
        {
            Enabled = row.Enabled,
            Category = row.Category,
            SourceTag = row.SourceTag,
            OutputPrompt = row.OutputPrompt,
            ExtensionData = row.ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(row.ExtensionData, StringComparer.Ordinal),
        }).ToList();

    private void RefreshPhotorealPromptMappingSummary()
    {
        if (AppPhotorealPromptMappingSummaryText is null)
            return;
        int enabled = _photorealPromptMappings.Count(static row => row.Enabled);
        AppPhotorealPromptMappingSummaryText.Text =
            $"{enabled}/{_photorealPromptMappings.Count}件を使用。PNGのA1111 Positiveに完全一致したタグだけを、現在のPositive末尾へ追加します。";
    }

    private void OpenPhotorealPromptMappings_Click(object sender, RoutedEventArgs e)
        => OpenPhotorealPromptMappingsEditor();

    private void OpenPhotorealPromptMappingsEditor(string? modalPromptTag = null)
    {
        List<PhotorealPromptMappingState> rows =
            SnapshotPhotorealPromptMappings();
        string? focusedSourceTag = null;
        bool added = false;
        if (!string.IsNullOrWhiteSpace(modalPromptTag)
            && !TryPrepareModalPromptMappingRow(
                rows,
                modalPromptTag,
                out focusedSourceTag,
                out added,
                out string preparationError))
        {
            ShowModalInteractionFeedback(preparationError);
            return;
        }

        var editor = new PhotorealPromptMappingEditorWindow(
            rows,
            CreateDefaultPhotorealPromptMappings(),
            focusedSourceTag)
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
            return;
        if (!TryValidatePhotorealPromptMappingsForEditor(editor.Result, out string validationError))
        {
            SetPhotorealPromptStatus(validationError);
            return;
        }

        _photorealPromptMappings.Clear();
        _photorealPromptMappings.AddRange(editor.Result.Select(static row => row.Clone()));
        RefreshPhotorealPromptMappingSummary();
        SetPhotorealPromptStatus("PNG Prompt引き継ぎ設定を保存しました。");
        if (!_initializing)
            SaveState();
        if (focusedSourceTag is not null)
        {
            ShowModalInteractionFeedback(added
                ? $"{focusedSourceTag} を実写化Prompt変換表へ追加しました"
                : $"{focusedSourceTag} の実写化Prompt変換を更新しました");
        }
    }

    private static bool TryPrepareModalPromptMappingRow(
        List<PhotorealPromptMappingState> rows,
        string rawTag,
        out string? focusedSourceTag,
        out bool added,
        out string error)
    {
        focusedSourceTag = null;
        added = false;
        error = "";
        string normalizedTag = NormalizeA1111PromptTag($"({rawTag})");
        if (normalizedTag.Length is < 1 or > MaxPhotorealPromptMappingTagLength)
        {
            error = "このPromptタグは長すぎるため変換表へ追加できません";
            return false;
        }

        PhotorealPromptMappingState? existing = rows.FirstOrDefault(row =>
            string.Equals(
                NormalizeA1111PromptTag(row.SourceTag),
                normalizedTag,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            focusedSourceTag = existing.SourceTag;
            return true;
        }
        if (rows.Count >= MaxPhotorealPromptMappingCount)
        {
            error = $"実写化Prompt変換表は最大{MaxPhotorealPromptMappingCount}件です";
            return false;
        }

        rows.Add(new PhotorealPromptMappingState
        {
            Enabled = true,
            Category = "カスタム",
            SourceTag = normalizedTag,
            OutputPrompt = normalizedTag,
        });
        focusedSourceTag = normalizedTag;
        added = true;
        return true;
    }

    private bool CanEditDisplayedOriginalPromptMapping()
        => Modal.Visibility == Visibility.Visible
            && !_modalShowingVideo
            && !_modalShowingEnhanced
            && !string.IsNullOrWhiteSpace(_modalDisplayPath)
            && string.Equals(
                _modalDisplayPath,
                _modalSourceTilePath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetExtension(_modalDisplayPath),
                ".png",
                StringComparison.OrdinalIgnoreCase)
            && CurrentDisplayedModalPngMetadata() is { Prompt.Length: > 0 };

    private void AttachModalPromptMappingContextMenu(Button chip, string tag)
    {
        var editItem = new MenuItem
        {
            Header = "実写化Prompt変換表に追加して編集…",
            Tag = tag,
        };
        System.Windows.Automation.AutomationProperties.SetName(
            editItem,
            $"Add prompt tag {tag} to photoreal prompt mapping editor");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            editItem,
            "Add this original PNG prompt tag to the photoreal prompt mapping table and edit it.");
        editItem.Click += ModalPromptMappingContext_Click;

        var menu = new ContextMenu();
        menu.Items.Add(editItem);
        menu.Opened += (_, _) =>
        {
            editItem.IsEnabled = CanEditDisplayedOriginalPromptMapping();
            editItem.ToolTip = editItem.IsEnabled
                ? "同じ文を初期値として追加し、変換表で編集します"
                : "オリジナルPNGのPrompt表示時だけ使用できます";
        };
        chip.ContextMenu = menu;
    }

    private void ModalPromptMappingContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !CanEditDisplayedOriginalPromptMapping())
        {
            return;
        }
        OpenPhotorealPromptMappingsEditor(tag);
    }

    public bool ModalPromptMappingContextReadyForSmoke(string tag)
    {
        RealizeAllModalPromptChipsForSmoke();
        Button? chip = ModalPromptChips.Children
            .OfType<Button>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag as string,
                tag,
                StringComparison.OrdinalIgnoreCase));
        MenuItem? item = chip?.ContextMenu?.Items
            .OfType<MenuItem>()
            .FirstOrDefault();
        var rows = SnapshotPhotorealPromptMappings();
        bool prepared = TryPrepareModalPromptMappingRow(
            rows,
            tag,
            out string? focusedSourceTag,
            out bool added,
            out _);
        PhotorealPromptMappingState? addedRow = rows.FirstOrDefault(row =>
            string.Equals(
                row.SourceTag,
                focusedSourceTag,
                StringComparison.OrdinalIgnoreCase));
        return item is not null
            && CanEditDisplayedOriginalPromptMapping()
            && prepared
            && added
            && addedRow is
            {
                Enabled: true,
                Category: "カスタム",
            }
            && string.Equals(
                addedRow.SourceTag,
                addedRow.OutputPrompt,
                StringComparison.Ordinal);
    }

    internal static bool TryValidatePhotorealPromptMappingsForEditor(
        IReadOnlyList<PhotorealPromptMappingState> rows,
        out string error)
    {
        error = "";
        if (rows.Count > MaxPhotorealPromptMappingCount)
        {
            error = $"変換表は最大{MaxPhotorealPromptMappingCount}件です。";
            return false;
        }
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealPromptMappingState row in rows)
        {
            string category = row.Category?.Trim() ?? "";
            string source = row.SourceTag?.Trim() ?? "";
            string output = row.OutputPrompt?.Trim() ?? "";
            if (category.Length > 40
                || source.Length is < 1 or > MaxPhotorealPromptMappingTagLength
                || output.Length is < 1 or > MaxPhotorealPromptMappingOutputLength)
            {
                error = "分類は40文字以内、元タグと追加文は空欄にせず長すぎない値にしてください。";
                return false;
            }
            string key = NormalizeA1111PromptTag(source);
            if (key.Length == 0 || !normalized.Add(key))
            {
                error = $"正規化後に重複する元タグがあります: {source}";
                return false;
            }
        }
        return true;
    }

    private async Task<ModalPhotorealRequestSettings> ResolvePhotorealRequestSettingsAsync(
        ModalPhotorealRequestSettings settings,
        string sourcePath,
        CancellationToken token = default)
    {
        PhotorealPromptMappingState[] mappings = _photorealPromptMappings
            .Where(static row => row.Enabled)
            .Select(static row => row.Clone())
            .ToArray();
        if (mappings.Length == 0
            || !string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return settings;
        }

        PngParametersMetadata? metadata;
        try
        {
            metadata = await Task.Run(
                () => ReadPngParametersMetadata(sourcePath, token),
                token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return settings;
        }
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Prompt))
            return settings;

        string prompt = ComposeMappedPhotorealPrompt(
            settings.Prompt,
            metadata.Prompt,
            mappings);
        return settings with { Prompt = prompt };
    }

    private static string ComposeMappedPhotorealPrompt(
        string basePrompt,
        string sourcePrompt,
        IReadOnlyList<PhotorealPromptMappingState> mappings)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealPromptMappingState mapping in mappings)
        {
            string key = NormalizeA1111PromptTag(mapping.SourceTag);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(mapping.OutputPrompt))
                continue;
            if (!lookup.TryAdd(key, mapping.OutputPrompt.Trim()))
                throw new InvalidOperationException($"正規化後に重複する元タグがあります: {mapping.SourceTag}");
        }

        string normalizedBase = basePrompt.TrimEnd().TrimEnd(',');
        var appended = new List<string>();
        int composedLength = normalizedBase.Length;
        var outputs = new HashSet<string>(
            SplitA1111PromptTags(normalizedBase)
                .Select(NormalizeA1111PromptTag)
                .Where(static value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        bool TryAppendOutput(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            string output = candidate.Trim();
            string[] outputKeys = SplitA1111PromptTags(output)
                .Select(NormalizeA1111PromptTag)
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (outputKeys.Length == 0
                || outputKeys.All(outputs.Contains))
                return false;

            int separatorLength = normalizedBase.Length > 0 || appended.Count > 0
                ? 2
                : 0;
            if (composedLength + separatorLength + output.Length
                > MaxPhotorealPromptLength)
            {
                return false;
            }

            foreach (string outputKey in outputKeys)
                outputs.Add(outputKey);
            appended.Add(output);
            composedLength += separatorLength + output.Length;
            return true;
        }

        string? nippleTextureOutput = lookup.GetValueOrDefault("nipple")
            ?? lookup.GetValueOrDefault("nipples");
        bool appendNippleTexture = false;
        foreach (string rawTag in SplitA1111PromptTags(sourcePrompt))
        {
            string key = NormalizeA1111PromptTag(rawTag);
            if (key.Length == 0)
                continue;

            string? exactOutput = lookup.GetValueOrDefault(key);
            TryAppendOutput(exactOutput);
            appendNippleTexture |= nippleTextureOutput is not null
                && IsNippleOrAreolaPromptTag(key);
        }
        if (appendNippleTexture)
            TryAppendOutput(nippleTextureOutput);
        if (appended.Count == 0)
            return basePrompt;

        string composed = normalizedBase.Length == 0
            ? string.Join(", ", appended)
            : $"{normalizedBase}, {string.Join(", ", appended)}";
        if (composed.Length > MaxPhotorealPromptLength)
        {
            throw new InvalidOperationException(
                $"PNG Prompt引き継ぎ後のPositiveが{MaxPhotorealPromptLength}文字を超えます。変換表かPositiveを短くしてください。");
        }
        return composed;
    }

    private static bool IsNippleOrAreolaPromptTag(string normalizedTag)
    {
        int tokenStart = 0;
        bool anatomyTokenFound = false;
        bool absenceTokenFound = false;
        for (int index = 0; index <= normalizedTag.Length; index++)
        {
            if (index < normalizedTag.Length
                && char.IsLetterOrDigit(normalizedTag[index]))
            {
                continue;
            }

            if (index > tokenStart)
            {
                ReadOnlySpan<char> token = normalizedTag.AsSpan(
                    tokenStart,
                    index - tokenStart);
                if (token.Equals("nipple".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("nipples".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("areola".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("areolae".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("areolas".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("areolar".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    anatomyTokenFound = true;
                }
                else if (token.Equals("anti".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("no".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("without".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("missing".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || token.Equals("absent".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    absenceTokenFound = true;
                }
            }
            tokenStart = index + 1;
        }
        return anatomyTokenFound && !absenceTokenFound;
    }

    private static IEnumerable<string> SplitA1111PromptTags(string prompt)
    {
        foreach (string segment in SplitA1111PromptTagsAtTopLevel(prompt))
        {
            foreach (string expanded in ExpandA1111PromptTagSegment(segment))
                yield return expanded;
        }
    }

    private static IEnumerable<string> ExpandA1111PromptTagSegment(string value)
    {
        string segment = CollapsePromptWhitespace(value);
        if (TryStripA1111OuterWrapper(segment, out string inner, out char wrapper))
        {
            if (wrapper == '(')
                inner = StripA1111NumericWeight(inner);
            string[] nested = SplitA1111PromptTagsAtTopLevel(inner).ToArray();
            if (nested.Length > 1)
            {
                foreach (string child in nested)
                {
                    foreach (string expanded in ExpandA1111PromptTagSegment(child))
                        yield return expanded;
                }
                yield break;
            }
        }
        yield return segment;
    }

    private static IEnumerable<string> SplitA1111PromptTagsAtTopLevel(string prompt)
    {
        var current = new StringBuilder();
        bool escaped = false;
        int roundDepth = 0;
        int squareDepth = 0;
        for (int index = 0; index < prompt.Length; index++)
        {
            char character = prompt[index];
            if (escaped)
            {
                current.Append('\\');
                current.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (roundDepth == 0
                && squareDepth == 0
                && IsA1111BreakSeparator(prompt, index))
            {
                yield return current.ToString();
                current.Clear();
                index += "BREAK".Length - 1;
                continue;
            }
            if (character == '(')
                roundDepth++;
            else if (character == ')' && roundDepth > 0)
                roundDepth--;
            else if (character == '[')
                squareDepth++;
            else if (character == ']' && squareDepth > 0)
                squareDepth--;
            if (character == ',' && roundDepth == 0 && squareDepth == 0)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        if (escaped)
            current.Append('\\');
        yield return current.ToString();
    }

    private static bool IsA1111BreakSeparator(string prompt, int index)
    {
        const string separator = "BREAK";
        if (index + separator.Length > prompt.Length
            || !prompt.AsSpan(index, separator.Length).SequenceEqual(separator))
        {
            return false;
        }

        bool startsAtBoundary = index == 0
            || char.IsWhiteSpace(prompt[index - 1])
            || prompt[index - 1] == ',';
        int after = index + separator.Length;
        bool endsAtBoundary = after == prompt.Length
            || char.IsWhiteSpace(prompt[after])
            || prompt[after] == ',';
        return startsAtBoundary && endsAtBoundary;
    }

    private static string NormalizeA1111PromptTag(string value)
    {
        string current = CollapsePromptWhitespace(value);
        while (TryStripA1111OuterWrapper(current, out string inner, out char wrapper))
        {
            if (wrapper == '(')
                inner = StripA1111NumericWeight(inner);
            current = CollapsePromptWhitespace(inner);
        }
        current = current
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\[", "[", StringComparison.Ordinal)
            .Replace("\\]", "]", StringComparison.Ordinal)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace('_', ' ');
        return CollapsePromptWhitespace(current);
    }

    private static bool TryStripA1111OuterWrapper(
        string value,
        out string inner,
        out char wrapper)
    {
        inner = value;
        wrapper = '\0';
        if (value.Length < 2)
            return false;
        char open = value[0];
        char close = value[^1];
        if ((open, close) is not (('(', ')') or ('[', ']')))
            return false;
        int depth = 0;
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == open)
                depth++;
            else if (character == close)
                depth--;
            if (depth == 0 && index < value.Length - 1)
                return false;
            if (depth < 0)
                return false;
        }
        if (depth != 0)
            return false;
        wrapper = open;
        inner = value[1..^1];
        return true;
    }

    private static string StripA1111NumericWeight(string value)
    {
        int separator = value.LastIndexOf(':');
        if (separator <= 0)
            return value;
        string weight = value[(separator + 1)..].Trim();
        return double.TryParse(
            weight,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
            && double.IsFinite(parsed)
            ? value[..separator]
            : value;
    }

    private static string CollapsePromptWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        bool inWhitespace = true;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                inWhitespace = true;
                continue;
            }
            if (inWhitespace && result.Length > 0)
                result.Append(' ');
            result.Append(character);
            inWhitespace = false;
        }
        return result.ToString();
    }

    public static string NormalizeA1111PromptTagForSmoke(string value)
        => NormalizeA1111PromptTag(value);

    public static string ComposeMappedPhotorealPromptForSmoke(
        string basePrompt,
        string sourcePrompt,
        IReadOnlyList<PhotorealPromptMappingState> mappings)
        => ComposeMappedPhotorealPrompt(basePrompt, sourcePrompt, mappings);

    public void RestorePhotorealPromptMappingsForSmoke(
        IReadOnlyList<PhotorealPromptMappingState>? persisted,
        int defaultsRevision = CurrentPhotorealPromptMappingDefaultsRevision)
        => RestorePhotorealPromptMappings(persisted, defaultsRevision);

    public int PhotorealPromptMappingCountForSmoke =>
        _photorealPromptMappings.Count;

    public IReadOnlyList<PhotorealPromptMappingState>
        SnapshotPhotorealPromptMappingsForSmoke()
        => SnapshotPhotorealPromptMappings();
}

public sealed class PhotorealPromptMappingState
{
    public bool Enabled { get; set; }
    public string Category { get; set; } = "カスタム";
    public string SourceTag { get; set; } = "";
    public string OutputPrompt { get; set; } = "";
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public PhotorealPromptMappingState Clone()
        => new()
        {
            Enabled = Enabled,
            Category = Category,
            SourceTag = SourceTag,
            OutputPrompt = OutputPrompt,
            ExtensionData = ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal),
        };
}

internal sealed class PhotorealPromptMappingEditorRow : INotifyPropertyChanged
{
    private bool _enabled;
    private string _category = "カスタム";
    private string _sourceTag = "";
    private string _outputPrompt = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); } }
    public string Category { get => _category; set { _category = value; Changed(nameof(Category)); } }
    public string SourceTag { get => _sourceTag; set { _sourceTag = value; Changed(nameof(SourceTag)); } }
    public string OutputPrompt { get => _outputPrompt; set { _outputPrompt = value; Changed(nameof(OutputPrompt)); } }
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));

    public PhotorealPromptMappingState ToState()
        => new()
        {
            Enabled = Enabled,
            Category = Category,
            SourceTag = SourceTag,
            OutputPrompt = OutputPrompt,
            ExtensionData = ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal),
        };
}

internal sealed class PhotorealPromptMappingEditorWindow : Window
{
    private readonly ObservableCollection<PhotorealPromptMappingEditorRow> _rows;
    private readonly IReadOnlyList<PhotorealPromptMappingState> _defaults;
    private readonly DataGrid _grid;
    private readonly TextBox _filter;
    private readonly TextBlock _status;
    private readonly ICollectionView _rowsView;
    private bool _filterRefreshDirty;
    private bool _filterRefreshScheduled;

    public IReadOnlyList<PhotorealPromptMappingState> Result { get; private set; } = [];

    public PhotorealPromptMappingEditorWindow(
        IReadOnlyList<PhotorealPromptMappingState> current,
        IReadOnlyList<PhotorealPromptMappingState> defaults,
        string? focusSourceTag = null)
    {
        _defaults = defaults.Select(static row => row.Clone()).ToArray();
        _rows = new(current.Select(ToEditorRow));
        foreach (PhotorealPromptMappingEditorRow row in _rows)
            row.PropertyChanged += EditorRow_PropertyChanged;
        Title = "PNG Prompt引き継ぎ設定";
        Width = 980;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 31));
        Foreground = Brushes.WhiteSmoke;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var title = new TextBlock
        {
            Text = "PNGメタデータの元タグ → 実写化Positiveへ追加する文",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(title);

        var hintRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(hintRow, 1);
        root.Children.Add(hintRow);
        _filter = new TextBox
        {
            Width = 280,
            Padding = new Thickness(7, 5, 7, 5),
            ToolTip = "元タグ・追加文・分類を検索",
        };
        DockPanel.SetDock(_filter, Dock.Right);
        hintRow.Children.Add(_filter);
        hintRow.Children.Add(new TextBlock
        {
            Text = "完全一致・大文字小文字無視。A1111の外側括弧とweightは照合時だけ除去し、追加文へweightは再付与しません。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });

        _grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Background = new SolidColorBrush(Color.FromRgb(30, 33, 39)),
            Foreground = Brushes.WhiteSmoke,
            RowBackground = new SolidColorBrush(Color.FromRgb(30, 33, 39)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(36, 39, 46)),
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(55, 59, 68)),
        };
        _rowsView = CollectionViewSource.GetDefaultView(_grid.ItemsSource);
        _filter.TextChanged += (_, _) => RequestFilterRefresh();
        _grid.CellEditEnding += (_, _) => SchedulePendingFilterRefresh();
        _grid.RowEditEnding += (_, _) => SchedulePendingFilterRefresh();
        _grid.Columns.Add(CreateDirectEnabledColumn());
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "分類",
            Binding = new Binding(nameof(PhotorealPromptMappingEditorRow.Category)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 140,
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "PNGの元タグ",
            Binding = new Binding(nameof(PhotorealPromptMappingEditorRow.SourceTag)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "実写化Positiveへ追加",
            Binding = new Binding(nameof(PhotorealPromptMappingEditorRow.OutputPrompt)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1.3, DataGridLengthUnitType.Star),
        });
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);
        _rowsView.Filter = FilterRow;

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        actions.Children.Add(CreateButton("追加", (_, _) => AddRow()));
        actions.Children.Add(CreateButton("選択行を削除", (_, _) => DeleteSelected()));
        actions.Children.Add(CreateButton("初期表へ戻す", (_, _) => ResetDefaults()));
        actions.Children.Add(CreateButton("キャンセル", (_, _) => Close()));
        actions.Children.Add(CreateButton("保存して閉じる", (_, _) => SaveAndClose(), primary: true));
        _status = new TextBlock
        {
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
        };
        footer.Children.Add(_status);
        RefreshStatus();
        if (!string.IsNullOrWhiteSpace(focusSourceTag))
        {
            string requestedSourceTag = focusSourceTag.Trim();
            _filter.Text = requestedSourceTag;
            Loaded += (_, _) => FocusSourceTag(requestedSourceTag);
        }
    }

    private void FocusSourceTag(string sourceTag)
    {
        PhotorealPromptMappingEditorRow? row = _rows.FirstOrDefault(candidate =>
            string.Equals(
                candidate.SourceTag,
                sourceTag,
                StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;

        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
        _grid.CurrentCell = new DataGridCellInfo(row, _grid.Columns[3]);
        _grid.Focus();
        Dispatcher.BeginInvoke(() =>
        {
            _grid.ScrollIntoView(row);
            _grid.BeginEdit();
        });
    }

    private static DataGridCheckBoxColumn CreateDirectEnabledColumn()
    {
        var directToggleStyle = new Style(typeof(CheckBox));
        directToggleStyle.Setters.Add(new Setter(
            UIElement.IsHitTestVisibleProperty,
            true));
        directToggleStyle.Setters.Add(new Setter(
            UIElement.FocusableProperty,
            false));
        directToggleStyle.Setters.Add(new Setter(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center));
        directToggleStyle.Setters.Add(new Setter(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center));
        directToggleStyle.Setters.Add(new Setter(
            FrameworkElement.ToolTipProperty,
            "クリックで直接ON/OFF"));

        return new DataGridCheckBoxColumn
        {
            Header = "使用",
            Binding = new Binding(nameof(PhotorealPromptMappingEditorRow.Enabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            },
            ElementStyle = directToggleStyle,
            EditingElementStyle = directToggleStyle,
            Width = 54,
        };
    }

    internal bool DirectEnabledToggleContractForSmoke()
    {
        if (_grid.Columns.FirstOrDefault() is not DataGridCheckBoxColumn
            {
                Binding: Binding columnBinding,
                ElementStyle: { } style,
            }
            || columnBinding.Mode != BindingMode.TwoWay
            || columnBinding.UpdateSourceTrigger != UpdateSourceTrigger.PropertyChanged
            || !style.Setters.OfType<Setter>().Any(static setter =>
                setter.Property == UIElement.IsHitTestVisibleProperty
                && setter.Value is true))
        {
            return false;
        }

        var row = new PhotorealPromptMappingEditorRow { Enabled = false };
        var checkBox = new CheckBox { DataContext = row, Style = style };
        checkBox.SetBinding(
            CheckBox.IsCheckedProperty,
            new Binding(nameof(PhotorealPromptMappingEditorRow.Enabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
        BindingExpression? bindingExpression = checkBox.GetBindingExpression(
            CheckBox.IsCheckedProperty);
        checkBox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
        bindingExpression?.UpdateSource();
        return checkBox.IsHitTestVisible
            && bindingExpression is not null
            && row.Enabled;
    }

    internal async Task<bool> FilterRefreshDuringEditContractForSmokeAsync()
    {
        if (_rows.Count == 0
            || CollectionViewSource.GetDefaultView(_grid.ItemsSource)
                is not IEditableCollectionView editableView)
        {
            return false;
        }

        PhotorealPromptMappingEditorRow row = _rows[0];
        string originalFilter = _filter.Text;
        string matchingFilter = row.SourceTag;
        try
        {
            _filter.Text = matchingFilter;
            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);
            _grid.SelectedItem = row;

            editableView.EditItem(row);
            if (!editableView.IsEditingItem)
                return false;

            row.OutputPrompt = "";
            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);
            bool rowEditStayedActive = editableView.IsEditingItem;
            editableView.CommitEdit();

            editableView.EditItem(row);
            _filter.Text = matchingFilter + " ";
            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);
            bool filterEditStayedActive = editableView.IsEditingItem;
            editableView.CommitEdit();

            _filter.Text = matchingFilter;
            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);
            return rowEditStayedActive
                && filterEditStayedActive
                && ReferenceEquals(_grid.SelectedItem, row)
                && CollectionViewSource.GetDefaultView(_grid.ItemsSource).Contains(row);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (editableView.IsAddingNew)
                editableView.CancelNew();
            if (editableView.IsEditingItem)
                editableView.CancelEdit();
            _filter.Text = originalFilter;
        }
    }

    private static PhotorealPromptMappingEditorRow ToEditorRow(PhotorealPromptMappingState row)
        => new()
        {
            Enabled = row.Enabled,
            Category = row.Category,
            SourceTag = row.SourceTag,
            OutputPrompt = row.OutputPrompt,
            ExtensionData = row.ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(row.ExtensionData, StringComparer.Ordinal),
        };

    private bool FilterRow(object candidate)
    {
        if (candidate is not PhotorealPromptMappingEditorRow row)
            return false;
        string query = _filter.Text.Trim();
        return query.Length == 0
            || row.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.SourceTag.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.OutputPrompt.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RequestFilterRefresh()
    {
        _filterRefreshDirty = true;
        SchedulePendingFilterRefresh();
    }

    private void SchedulePendingFilterRefresh()
    {
        if (!_filterRefreshDirty || _filterRefreshScheduled)
            return;

        _filterRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(RefreshFilterWhenEditCompletes));
    }

    private void RefreshFilterWhenEditCompletes()
    {
        _filterRefreshScheduled = false;
        if (!_filterRefreshDirty)
            return;
        if (_rowsView is IEditableCollectionView editableView
            && (editableView.IsAddingNew || editableView.IsEditingItem))
        {
            return;
        }

        object? selectedItem = _grid.SelectedItem;
        try
        {
            _rowsView.Refresh();
            _filterRefreshDirty = false;
            if (selectedItem is not null && _rowsView.Contains(selectedItem))
                _grid.SelectedItem = selectedItem;
        }
        catch (InvalidOperationException) when (
            _rowsView is IEditableCollectionView retryView
            && (retryView.IsAddingNew || retryView.IsEditingItem))
        {
            _filterRefreshDirty = true;
        }
    }

    private static Button CreateButton(string label, RoutedEventHandler click, bool primary = false)
    {
        Color background = primary
            ? Color.FromRgb(93, 63, 211)
            : Color.FromRgb(52, 59, 72);
        Color border = primary
            ? Color.FromRgb(167, 139, 250)
            : Color.FromRgb(112, 122, 140);
        var button = new Button
        {
            Content = label,
            Background = new SolidColorBrush(background),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = primary ? 112 : 76,
            MinHeight = 32,
            IsDefault = primary,
        };
        button.Click += click;
        return button;
    }

    internal static bool ActionButtonContrastContractForSmoke()
    {
        Button normal = CreateButton("通常", static (_, _) => { });
        Button primary = CreateButton(
            "保存して閉じる",
            static (_, _) => { },
            primary: true);
        return HasReadableButtonContrast(normal)
            && HasReadableButtonContrast(primary)
            && normal.Background is SolidColorBrush normalBackground
            && primary.Background is SolidColorBrush primaryBackground
            && normalBackground.Color != primaryBackground.Color;
    }

    private static bool HasReadableButtonContrast(Button button)
    {
        if (button.Foreground is not SolidColorBrush foreground
            || button.Background is not SolidColorBrush background)
        {
            return false;
        }

        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            0.2126 * Linear(color.R)
            + 0.7152 * Linear(color.G)
            + 0.0722 * Linear(color.B);

        double lighter = Math.Max(
            Luminance(foreground.Color),
            Luminance(background.Color));
        double darker = Math.Min(
            Luminance(foreground.Color),
            Luminance(background.Color));
        return (lighter + 0.05) / (darker + 0.05) >= 4.5;
    }

    private void AddRow()
    {
        if (!CommitPendingGridEdit())
            return;
        var row = new PhotorealPromptMappingEditorRow
        {
            Enabled = true,
            Category = "カスタム",
            SourceTag = "new tag",
            OutputPrompt = "new tag",
        };
        row.PropertyChanged += EditorRow_PropertyChanged;
        _rows.Add(row);
        _filter.Clear();
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
        RefreshStatus();
    }

    private void DeleteSelected()
    {
        if (!CommitPendingGridEdit())
            return;
        foreach (PhotorealPromptMappingEditorRow row in _grid.SelectedItems.Cast<PhotorealPromptMappingEditorRow>().ToArray())
        {
            row.PropertyChanged -= EditorRow_PropertyChanged;
            _rows.Remove(row);
        }
        RefreshStatus();
    }

    private void ResetDefaults()
    {
        if (!CommitPendingGridEdit())
            return;
        foreach (PhotorealPromptMappingEditorRow row in _rows)
            row.PropertyChanged -= EditorRow_PropertyChanged;
        _rows.Clear();
        foreach (PhotorealPromptMappingState row in _defaults)
        {
            PhotorealPromptMappingEditorRow editorRow = ToEditorRow(row);
            editorRow.PropertyChanged += EditorRow_PropertyChanged;
            _rows.Add(editorRow);
        }
        _filter.Clear();
        RefreshStatus();
    }

    private void RefreshStatus()
        => _status.Text = $"{_rows.Count(static row => row.Enabled)}/{_rows.Count}件を使用";

    private void EditorRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshStatus();
        if (_filter.Text.Length > 0)
            RequestFilterRefresh();
    }

    private bool CommitPendingGridEdit()
    {
        bool cellCommitted = _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        bool rowCommitted = _grid.CommitEdit(DataGridEditingUnit.Row, true);
        SchedulePendingFilterRefresh();
        if (cellCommitted && rowCommitted)
            return true;

        _status.Text = "編集中の行を確定できません。入力内容を確認してください。";
        _status.Foreground = Brushes.OrangeRed;
        return false;
    }

    private void SaveAndClose()
    {
        if (!CommitPendingGridEdit())
            return;
        PhotorealPromptMappingState[] result = _rows.Select(static row => row.ToState()).ToArray();
        if (!MainWindow.TryValidatePhotorealPromptMappingsForEditor(result, out string error))
        {
            _status.Text = error;
            _status.Foreground = Brushes.OrangeRed;
            return;
        }
        Result = result;
        DialogResult = true;
    }
}
