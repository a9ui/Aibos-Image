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

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxPhotorealPromptMappingCount = 256;
    private const int MaxPhotorealPromptMappingTagLength = 160;
    private const int MaxPhotorealPromptMappingOutputLength = 400;
    private const int DefaultPhotorealPromptPolicyRevision = 0;

    private readonly List<PhotorealPromptMappingState> _photorealPromptMappings = [];
    private int _photorealPromptMappingDefaultsRevision =
        DefaultPhotorealPromptPolicyRevision;

    private static List<PhotorealPromptMappingState> CreateDefaultPhotorealPromptMappings()
    {
        List<PhotorealPromptMappingState> mappings =
            WpfLocalPromptPolicy.Current.Mappings ?? [];
        return TryValidatePhotorealPromptMappingsForEditor(mappings, out _)
            ? mappings.Select(static row => row.Clone()).ToList()
            : [];
    }

    private void RestorePhotorealPromptMappings(
        IReadOnlyList<PhotorealPromptMappingState>? persisted,
        int persistedDefaultsRevision)
    {
        _photorealPromptMappings.Clear();
        List<PhotorealPromptMappingState> configuredDefaults =
            WpfLocalPromptPolicy.Current.Mappings ?? [];
        bool policyMappingsValid =
            TryValidatePhotorealPromptMappingsForEditor(
                configuredDefaults,
                out _);
        int policyRevision = policyMappingsValid
            ? WpfLocalPromptPolicy.Current.Revision
            : DefaultPhotorealPromptPolicyRevision;
        List<PhotorealPromptMappingState> defaults = policyMappingsValid
            ? configuredDefaults.Select(static row => row.Clone()).ToList()
            : [];

        IReadOnlyList<PhotorealPromptMappingState> source;
        if (persisted is null)
        {
            source = defaults;
        }
        else if (persisted.Count == 0 || persistedDefaultsRevision >= policyRevision)
        {
            source = persisted;
        }
        else
        {
            var upgraded = persisted
                .Where(static row => row is not null)
                .Select(static row => row.Clone())
                .ToList();
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

        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealPromptMappingState? candidate in
            source.Take(MaxPhotorealPromptMappingCount))
        {
            if (candidate is null)
                continue;

            string category = candidate.Category?.Trim() ?? "";
            string sourceTag = candidate.SourceTag?.Trim() ?? "";
            string outputPrompt = candidate.OutputPrompt?.Trim() ?? "";
            if (sourceTag.Length is < 1 or > MaxPhotorealPromptMappingTagLength
                || outputPrompt.Length is < 1 or > MaxPhotorealPromptMappingOutputLength)
            {
                continue;
            }

            string normalizedSource = NormalizeA1111PromptTag(sourceTag);
            if (normalizedSource.Length == 0
                || !seenSources.Add(normalizedSource))
            {
                continue;
            }

            _photorealPromptMappings.Add(new PhotorealPromptMappingState
            {
                Enabled = candidate.Enabled,
                Category = string.IsNullOrWhiteSpace(category)
                    ? "Custom"
                    : category,
                SourceTag = sourceTag,
                OutputPrompt = outputPrompt,
                ExtensionData = candidate.ExtensionData is null
                    ? null
                    : new Dictionary<string, JsonElement>(
                        candidate.ExtensionData,
                        StringComparer.Ordinal),
            });
        }

        _photorealPromptMappingDefaultsRevision = policyRevision;
        RefreshPhotorealPromptMappingSummary();
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
        foreach (PhotorealPromptMappingState? row in rows)
        {
            if (row is null)
            {
                error = "変換表に空の行があります。";
                return false;
            }
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

        foreach (string rawTag in SplitA1111PromptTags(sourcePrompt))
        {
            string key = NormalizeA1111PromptTag(rawTag);
            if (key.Length == 0)
                continue;

            string? exactOutput = lookup.GetValueOrDefault(key);
            TryAppendOutput(exactOutput);
            foreach (PhotorealPromptMappingState mapping in mappings)
            {
                if (MappingMatchesRelatedTokens(mapping, key))
                    TryAppendOutput(mapping.OutputPrompt);
            }
        }
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

    private static bool MappingMatchesRelatedTokens(
        PhotorealPromptMappingState mapping,
        string normalizedTag)
    {
        string[] required = ReadMappingTokenList(mapping, "matchTokens");
        if (required.Length == 0)
            return false;

        var sourceTokens = new HashSet<string>(
            EnumeratePromptWordTokens(normalizedTag),
            StringComparer.OrdinalIgnoreCase);
        if (!required.Any(sourceTokens.Contains))
            return false;

        string[] excluded = ReadMappingTokenList(mapping, "excludeTokens");
        return !excluded.Any(sourceTokens.Contains);
    }

    private static string[] ReadMappingTokenList(
        PhotorealPromptMappingState mapping,
        string propertyName)
    {
        if (mapping.ExtensionData is null
            || !mapping.ExtensionData.TryGetValue(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => NormalizeA1111PromptTag(item.GetString() ?? ""))
            .Where(static item => item.Length > 0)
            .Take(64)
            .ToArray();
    }

    private static IEnumerable<string> EnumeratePromptWordTokens(string value)
    {
        int tokenStart = -1;
        for (int index = 0; index <= value.Length; index++)
        {
            bool inToken = index < value.Length && char.IsLetterOrDigit(value[index]);
            if (inToken && tokenStart < 0)
            {
                tokenStart = index;
                continue;
            }

            if (!inToken && tokenStart >= 0)
            {
                yield return value[tokenStart..index];
                tokenStart = -1;
            }
        }
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
        int defaultsRevision = DefaultPhotorealPromptPolicyRevision)
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
        _filter.TextChanged += (_, _) => CollectionViewSource.GetDefaultView(_grid.ItemsSource).Refresh();
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
        CollectionViewSource.GetDefaultView(_grid.ItemsSource).Filter = FilterRow;

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
        foreach (PhotorealPromptMappingEditorRow row in _grid.SelectedItems.Cast<PhotorealPromptMappingEditorRow>().ToArray())
        {
            row.PropertyChanged -= EditorRow_PropertyChanged;
            _rows.Remove(row);
        }
        RefreshStatus();
    }

    private void ResetDefaults()
    {
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
            CollectionViewSource.GetDefaultView(_grid.ItemsSource).Refresh();
    }

    private void SaveAndClose()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
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
