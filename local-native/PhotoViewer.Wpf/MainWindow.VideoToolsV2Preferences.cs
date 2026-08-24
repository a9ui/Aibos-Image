using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int VideoEditV2MaximumStyleCount = 32;
    private const int VideoEditV2MaximumStyleNameLength = 40;
    private const int VideoEditV2MaximumInstructionTemplateLength = 2000;

    private readonly List<VideoEditV2StyleState> _videoEditV2Styles = [];
    private VideoToolsV2PreferenceState _videoToolsV2Preferences =
        VideoToolsV2PreferenceState.CreateDefaults();
    private bool _videoToolsV2PreferencesSyncing;
    private bool _videoEditV2SessionInputsInitialized;
    private bool _videoFinishV2SessionInputsInitialized;

    private static VideoEditV2StyleState? NormalizeVideoEditV2Style(
        VideoEditV2StyleState? candidate)
    {
        if (candidate is null)
            return null;
        string name = candidate.Name.Trim();
        string instruction = NormalizeVideoEditV2InstructionTemplate(
            candidate.InstructionTemplate);
        if (!IsSafeVideoEditV2StyleName(name)
            || instruction.Length > VideoEditV2MaximumInstructionTemplateLength
            || !IsSafeVideoEditV2InstructionTemplate(instruction)
            || candidate.AudioPolicy is not ("preserve" or "mute")
            || candidate.StrengthTag is not ("light" or "balanced" or "strong")
            || candidate.MaximumPixelAreaTier is not ("light" or "standard" or "high")
            || candidate.Steps is < VideoEditV2MinimumSteps or > VideoEditV2MaximumSteps
            || candidate.StyleTag is not ("none" or "source-faithful" or "cinematic"))
        {
            return null;
        }
        return new VideoEditV2StyleState
        {
            Name = name,
            InstructionTemplate = instruction,
            AudioPolicy = candidate.AudioPolicy,
            StrengthTag = candidate.StrengthTag,
            MaximumPixelAreaTier = candidate.MaximumPixelAreaTier,
            Steps = candidate.Steps,
            StyleTag = candidate.StyleTag,
            ExtensionData = CloneExtensionData(candidate.ExtensionData),
        };
    }

    private static string NormalizeVideoEditV2InstructionTemplate(string? value)
        => (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static bool IsSafeVideoEditV2StyleName(string value)
        => value.Length is >= 1 and <= VideoEditV2MaximumStyleNameLength
            && !value.Any(char.IsControl);

    private static bool IsSafeVideoEditV2InstructionTemplate(string value)
        => value.All(character => !char.IsControl(character)
            || character is '\n' or '\t');

    private static VideoToolsV2PreferenceState NormalizeVideoToolsV2Preferences(
        VideoToolsV2PreferenceState? state)
    {
        VideoToolsV2PreferenceState defaults =
            VideoToolsV2PreferenceState.CreateDefaults();
        VideoEditV2PreferenceState edit = state?.Edit ?? defaults.Edit!;
        VideoFinishV2PreferenceState finish = state?.Finish ?? defaults.Finish!;
        return new VideoToolsV2PreferenceState
        {
            Edit = new VideoEditV2PreferenceState
            {
                AudioPolicy = edit.AudioPolicy is "preserve" or "mute"
                    ? edit.AudioPolicy
                    : defaults.Edit!.AudioPolicy,
                StrengthTag = edit.StrengthTag is "light" or "balanced" or "strong"
                    ? edit.StrengthTag
                    : defaults.Edit!.StrengthTag,
                MaximumPixelAreaTier = edit.MaximumPixelAreaTier
                    is "light" or "standard" or "high"
                    ? edit.MaximumPixelAreaTier
                    : defaults.Edit!.MaximumPixelAreaTier,
                Steps = edit.Steps is >= VideoEditV2MinimumSteps
                    and <= VideoEditV2MaximumSteps
                    ? edit.Steps
                    : defaults.Edit!.Steps,
                SkipReview = edit.SkipReview,
                ExtensionData = CloneExtensionData(edit.ExtensionData),
            },
            Finish = new VideoFinishV2PreferenceState
            {
                Mode = finish.Mode is "fast" or "standard" or "quality"
                    ? finish.Mode
                    : defaults.Finish!.Mode,
                Scale = finish.Scale is 2 or 4
                    ? finish.Scale
                    : defaults.Finish!.Scale,
                ExtensionData = CloneExtensionData(finish.ExtensionData),
            },
            ExtensionData = CloneExtensionData(state?.ExtensionData),
        };
    }

    private void RestoreVideoToolsV2Preferences(ViewerState? state)
    {
        _videoToolsV2Preferences = NormalizeVideoToolsV2Preferences(
            state?.VideoToolsV2);
        _videoEditV2SessionInputsInitialized = false;
        _videoFinishV2SessionInputsInitialized = false;
        SyncVideoToolsV2PreferenceControls();
    }

    private VideoToolsV2PreferenceState SnapshotVideoToolsV2Preferences()
        => NormalizeVideoToolsV2Preferences(_videoToolsV2Preferences);

    private static void MergeLatestVideoToolsV2PreferenceExtensionData(
        VideoToolsV2PreferenceState? current,
        VideoToolsV2PreferenceState? latest)
    {
        if (current is null || latest is null)
            return;
        current.ExtensionData = CloneExtensionData(latest.ExtensionData);
        if (current.Edit is not null && latest.Edit is not null)
            current.Edit.ExtensionData = CloneExtensionData(
                latest.Edit.ExtensionData);
        if (current.Finish is not null && latest.Finish is not null)
            current.Finish.ExtensionData = CloneExtensionData(
                latest.Finish.ExtensionData);
    }

    private void ApplySavedVideoToolsV2PreferenceExtensionData(
        VideoToolsV2PreferenceState? saved)
    {
        if (saved is null)
            return;
        MergeLatestVideoToolsV2PreferenceExtensionData(
            _videoToolsV2Preferences,
            saved);
    }

    private void ApplyVideoEditV2DefaultsToBoardIfNeeded()
    {
        if (_videoEditV2SessionInputsInitialized)
            return;
        VideoEditV2PreferenceState edit = _videoToolsV2Preferences.Edit
            ?? VideoToolsV2PreferenceState.CreateDefaults().Edit!;
        _videoEditV2Syncing = true;
        try
        {
            SelectComboBoxItemByTag(
                ModalVideoEditV2AudioComboBox,
                edit.AudioPolicy,
                fallbackIndex: 0);
            SelectComboBoxItemByTag(
                ModalVideoEditV2StrengthComboBox,
                edit.StrengthTag,
                fallbackIndex: 1);
            SelectComboBoxItemByTag(
                ModalVideoEditV2CanvasComboBox,
                VideoEditV2PixelAreaTierToTag(edit.MaximumPixelAreaTier),
                fallbackIndex: 2);
            ModalVideoEditV2StyleComboBox.SelectedIndex = 0;
            ModalVideoEditV2StepsSlider.Value = edit.Steps;
            ModalVideoEditV2StepsTextBox.Text = edit.Steps.ToString(
                CultureInfo.InvariantCulture);
            ModalVideoEditV2SkipReviewCheckBox.IsChecked = edit.SkipReview;
        }
        finally
        {
            _videoEditV2Syncing = false;
        }
        _videoEditV2SessionInputsInitialized = true;
    }

    private void ApplyVideoFinishV2DefaultsToBoardIfNeeded()
    {
        if (_videoFinishV2SessionInputsInitialized)
            return;
        VideoFinishV2PreferenceState finish = _videoToolsV2Preferences.Finish
            ?? VideoToolsV2PreferenceState.CreateDefaults().Finish!;
        _videoFinishV2Syncing = true;
        try
        {
            SelectComboBoxItemByTag(
                ModalVideoFinishV2ModeComboBox,
                finish.Mode,
                fallbackIndex: 1);
            SelectComboBoxItemByTag(
                ModalVideoFinishV2ScaleComboBox,
                finish.Scale.ToString(CultureInfo.InvariantCulture),
                fallbackIndex: 0);
        }
        finally
        {
            _videoFinishV2Syncing = false;
        }
        _videoFinishV2SessionInputsInitialized = true;
    }

    private static void SelectComboBoxItemByTag(
        ComboBox comboBox,
        string? tag,
        int fallbackIndex)
    {
        ComboBoxItem? selected = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                tag,
                StringComparison.Ordinal));
        comboBox.SelectedItem = selected;
        if (selected is null)
            comboBox.SelectedIndex = fallbackIndex;
    }

    private static string VideoEditV2PixelAreaTierToTag(string tier)
        => tier switch
        {
            "light" => "230400",
            "standard" => "307200",
            _ => "414720",
        };

    private static string VideoEditV2PixelAreaTagToTier(string? tag)
        => tag switch
        {
            "230400" => "light",
            "307200" => "standard",
            _ => "high",
        };

    private void RestoreVideoEditV2Styles(IEnumerable<VideoEditV2StyleState>? styles)
    {
        _videoEditV2Styles.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VideoEditV2StyleState? candidate in styles ?? [])
        {
            VideoEditV2StyleState? normalized = NormalizeVideoEditV2Style(candidate);
            if (normalized is null
                || !names.Add(normalized.Name)
                || _videoEditV2Styles.Count >= VideoEditV2MaximumStyleCount)
            {
                continue;
            }
            _videoEditV2Styles.Add(normalized);
        }
        RefreshVideoEditV2SavedStyleControls();
    }

    private List<VideoEditV2StyleState>? SnapshotVideoEditV2Styles()
        => _videoEditV2Styles.Count == 0
            ? null
            : _videoEditV2Styles
                .Select(style => NormalizeVideoEditV2Style(style)!)
                .ToList();

    private void RefreshVideoEditV2SavedStyleControls()
    {
        if (ModalVideoEditV2SavedStyleComboBox is not null)
        {
            string? selectedName =
                (ModalVideoEditV2SavedStyleComboBox.SelectedItem as ComboBoxItem)
                    ?.Tag?.ToString();
            _videoToolsV2PreferencesSyncing = true;
            try
            {
                ModalVideoEditV2SavedStyleComboBox.Items.Clear();
                ModalVideoEditV2SavedStyleComboBox.Items.Add(new ComboBoxItem
                {
                    Content = VideoEditV2Text("UiVideoEditV2SavedStyleNone"),
                    Tag = "",
                });
                foreach (VideoEditV2StyleState style in _videoEditV2Styles)
                {
                    ModalVideoEditV2SavedStyleComboBox.Items.Add(
                        new ComboBoxItem
                        {
                            Content = style.Name,
                            Tag = style.Name,
                        });
                }
                ComboBoxItem? selected =
                    ModalVideoEditV2SavedStyleComboBox.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(
                            item.Tag?.ToString(),
                            selectedName,
                            StringComparison.OrdinalIgnoreCase));
                ModalVideoEditV2SavedStyleComboBox.SelectedItem = selected;
                if (selected is null)
                    ModalVideoEditV2SavedStyleComboBox.SelectedIndex = 0;
            }
            finally
            {
                _videoToolsV2PreferencesSyncing = false;
            }
        }

        if (AppVideoEditV2StyleListBox is not null)
        {
            AppVideoEditV2StyleListBox.ItemsSource = _videoEditV2Styles
                .Select(style => style.Name)
                .ToList();
            AppVideoEditV2DeleteStyleButton.IsEnabled = false;
            AppVideoEditV2StyleCountText.Text = VideoEditV2Format(
                "UiVideoEditV2StyleCountFormat",
                _videoEditV2Styles.Count,
                VideoEditV2MaximumStyleCount);
        }
    }

    private void ModalVideoEditV2SavedStyle_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_videoToolsV2PreferencesSyncing)
            return;
        string? name =
            (ModalVideoEditV2SavedStyleComboBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return;
        VideoEditV2StyleState? style = _videoEditV2Styles.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
        if (style is not null)
            ApplyVideoEditV2Style(style);
    }

    private void ApplyVideoEditV2Style(VideoEditV2StyleState style)
    {
        ForceInvalidateModalVideoEditV2CandidateForStyleChange();
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2InstructionTextBox.Text =
                style.InstructionTemplate;
            SelectComboBoxItemByTag(
                ModalVideoEditV2AudioComboBox,
                style.AudioPolicy,
                fallbackIndex: 0);
            SelectComboBoxItemByTag(
                ModalVideoEditV2StrengthComboBox,
                style.StrengthTag,
                fallbackIndex: 1);
            SelectComboBoxItemByTag(
                ModalVideoEditV2CanvasComboBox,
                VideoEditV2PixelAreaTierToTag(style.MaximumPixelAreaTier),
                fallbackIndex: 2);
            SelectComboBoxItemByTag(
                ModalVideoEditV2StyleComboBox,
                style.StyleTag,
                fallbackIndex: 0);
            ModalVideoEditV2StepsSlider.Value = style.Steps;
            ModalVideoEditV2StepsTextBox.Text = style.Steps.ToString(
                CultureInfo.InvariantCulture);
        }
        finally
        {
            _videoEditV2Syncing = false;
        }
        RefreshModalVideoEditV2Plan(markCandidateStale: false);
        ResetModalVideoEditV2DurableState();
        RefreshModalVideoEditV2ActionControls();
        ModalVideoEditV2StyleStatusText.Text = VideoEditV2Format(
            "UiVideoEditV2StyleApplied",
            style.Name);
    }

    private void ForceInvalidateModalVideoEditV2CandidateForStyleChange()
    {
        _videoEditV2CompileGeneration++;
        CancellationTokenSource? active = Interlocked.Exchange(
            ref _videoEditV2CompileCts,
            null);
        TryCancelModalVideoEditV2Token(active);
        active?.Dispose();
        _videoEditV2CompilePending = false;
        if (_videoEditV2Candidate is not null)
        {
            _videoEditV2CandidateStale = true;
            _videoEditV2CandidateApproved = false;
            ModalVideoEditV2CompileStatusText.Text = VideoEditV2Text(
                "UiVideoEditV2CandidateStale");
        }
        _videoEditV2WriterReady = false;
    }

    private void ModalVideoEditV2SaveStyle_Click(
        object sender,
        RoutedEventArgs e)
    {
        string name = ModalVideoEditV2StyleNameTextBox.Text.Trim();
        VideoEditV2StyleState? style = BuildCurrentVideoEditV2Style(name);
        if (style is null)
        {
            ModalVideoEditV2StyleStatusText.Text = VideoEditV2Text(
                "UiVideoEditV2StyleInvalid");
            return;
        }
        bool overwritten = _videoEditV2Styles.Any(candidate =>
            string.Equals(
                candidate.Name,
                style.Name,
                StringComparison.OrdinalIgnoreCase));
        if (!TryMutateVideoEditV2Styles(styles =>
            {
                int index = styles.FindIndex(candidate => string.Equals(
                    candidate.Name,
                    style.Name,
                    StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    style.ExtensionData = CloneExtensionData(
                        styles[index].ExtensionData);
                    styles[index] = style;
                    return true;
                }
                if (styles.Count >= VideoEditV2MaximumStyleCount)
                    return false;
                styles.Add(style);
                return true;
            },
            out _))
        {
            ModalVideoEditV2StyleStatusText.Text = VideoEditV2Text(
                _videoEditV2Styles.Count >= VideoEditV2MaximumStyleCount
                    ? "UiVideoEditV2StyleLimit"
                    : "UiVideoEditV2StyleSaveFailed");
            return;
        }
        RefreshVideoEditV2SavedStyleControls();
        SelectSavedVideoEditV2Style(name);
        ModalVideoEditV2StyleStatusText.Text = VideoEditV2Format(
            overwritten
                ? "UiVideoEditV2StyleUpdated"
                : "UiVideoEditV2StyleSaved",
            name);
    }

    private VideoEditV2StyleState? BuildCurrentVideoEditV2Style(string name)
    {
        if (!int.TryParse(
                ModalVideoEditV2StepsTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int steps))
        {
            return null;
        }
        return NormalizeVideoEditV2Style(new VideoEditV2StyleState
        {
            Name = name,
            InstructionTemplate = ModalVideoEditV2InstructionTextBox.Text,
            AudioPolicy = ReadComboBoxTag(ModalVideoEditV2AudioComboBox),
            StrengthTag = ReadComboBoxTag(ModalVideoEditV2StrengthComboBox),
            MaximumPixelAreaTier = VideoEditV2PixelAreaTagToTier(
                ReadComboBoxTag(ModalVideoEditV2CanvasComboBox)),
            Steps = steps,
            StyleTag = ReadComboBoxTag(ModalVideoEditV2StyleComboBox),
        });
    }

    private static string ReadComboBoxTag(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void SelectSavedVideoEditV2Style(string name)
    {
        ComboBoxItem? item = ModalVideoEditV2SavedStyleComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                name,
                StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            ModalVideoEditV2SavedStyleComboBox.SelectedItem = item;
    }

    private bool TryMutateVideoEditV2Styles(
        Func<List<VideoEditV2StyleState>, bool> mutation,
        out string? error)
    {
        error = null;
        if (!_aiStyleStoreReady || _aiStyleWriteBlocked)
        {
            error = "protected";
            return false;
        }

        AiStyleDocument? saved = null;
        bool protectedFile = false;
        bool rejected = false;
        bool result = TryWithPersistenceLock(
            ResolvedAiStylePath,
            () =>
            {
                AiStyleReadResult latest = ReadAiStyleDocument(
                    ResolvedAiStyleStorePath);
                if (latest.State == AiStyleReadState.Protected)
                {
                    protectedFile = true;
                    return false;
                }
                AiStyleDocument current = latest.Document
                    ?? CreateCurrentAiStyleDocument();
                var styles = (current.VideoEditV2Styles ?? [])
                    .Select(NormalizeVideoEditV2Style)
                    .Where(static style => style is not null)
                    .Cast<VideoEditV2StyleState>()
                    .ToList();
                if (!mutation(styles))
                {
                    rejected = true;
                    return false;
                }
                current.VideoEditV2Styles = styles.Count == 0
                    ? null
                    : styles;
                if (!AreAiStyleCollectionsSupported(current))
                {
                    rejected = true;
                    return false;
                }
                string json = JsonSerializer.Serialize(
                    current,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        MaxDepth = 64,
                    });
                if (System.Text.Encoding.UTF8.GetByteCount(json)
                    > MaximumAiStyleDocumentBytes)
                {
                    rejected = true;
                    return false;
                }
                if (!LocalPersistenceStoreFile.TryWriteAtomicText(
                        ResolvedAiStyleStorePath,
                        json))
                {
                    return false;
                }
                AiStyleReadResult verification = ReadAiStyleDocument(
                    ResolvedAiStyleStorePath);
                saved = verification.Document;
                return verification.State == AiStyleReadState.Loaded;
            });
        if (!result || saved is null)
        {
            _aiStyleWriteBlocked = protectedFile;
            error = protectedFile
                ? "protected"
                : rejected
                    ? "rejected"
                    : "write-failed";
            return false;
        }

        _restoredAiStyleDocument = saved;
        _aiStyleKnownFingerprint = ComputeAiStyleKnownFingerprint(saved);
        _aiStyleExtensionData = CloneExtensionData(saved.ExtensionData);
        // The mutation started from the latest complete document. Reload every
        // known collection as the new in-memory baseline so a later save in a
        // different Style lane cannot overwrite a concurrent known-field edit.
        RestoreAiStyles(legacyState: null);
        return true;
    }

    private void AppVideoEditV2Style_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => AppVideoEditV2DeleteStyleButton.IsEnabled =
            AppVideoEditV2StyleListBox.SelectedItem is string;

    private void AppVideoEditV2DeleteStyle_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (AppVideoEditV2StyleListBox.SelectedItem is not string name)
            return;
        if (!TryMutateVideoEditV2Styles(styles =>
            styles.RemoveAll(candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase)) == 1,
            out _))
        {
            AppVideoEditV2StyleStatusText.Text = VideoEditV2Text(
                "UiVideoEditV2StyleDeleteFailed");
            return;
        }
        AppVideoEditV2StyleStatusText.Text = VideoEditV2Format(
            "UiVideoEditV2StyleDeleted",
            name);
    }

    private void SyncVideoToolsV2PreferenceControls()
    {
        if (AppVideoEditV2DefaultAudioComboBox is null)
            return;
        VideoToolsV2PreferenceState normalized =
            NormalizeVideoToolsV2Preferences(_videoToolsV2Preferences);
        _videoToolsV2PreferencesSyncing = true;
        try
        {
            SelectComboBoxItemByTag(
                AppVideoEditV2DefaultAudioComboBox,
                normalized.Edit!.AudioPolicy,
                0);
            SelectComboBoxItemByTag(
                AppVideoEditV2DefaultStrengthComboBox,
                normalized.Edit.StrengthTag,
                1);
            SelectComboBoxItemByTag(
                AppVideoEditV2DefaultCanvasComboBox,
                normalized.Edit.MaximumPixelAreaTier,
                2);
            AppVideoEditV2DefaultStepsSlider.Value = normalized.Edit.Steps;
            AppVideoEditV2DefaultStepsText.Text = normalized.Edit.Steps
                .ToString(CultureInfo.InvariantCulture);
            AppVideoEditV2DefaultSkipReviewCheckBox.IsChecked =
                normalized.Edit.SkipReview;
            SelectComboBoxItemByTag(
                AppVideoFinishV2DefaultModeComboBox,
                normalized.Finish!.Mode,
                1);
            SelectComboBoxItemByTag(
                AppVideoFinishV2DefaultScaleComboBox,
                normalized.Finish.Scale.ToString(CultureInfo.InvariantCulture),
                0);
        }
        finally
        {
            _videoToolsV2PreferencesSyncing = false;
        }
        RefreshVideoEditV2SavedStyleControls();
    }

    private void AppVideoToolsV2Preference_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_initializing || _videoToolsV2PreferencesSyncing)
            return;
        SaveVideoToolsV2PreferencesFromControls();
    }

    private void AppVideoEditV2DefaultSteps_Changed(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoToolsV2PreferencesSyncing
            || AppVideoEditV2DefaultStepsText is null)
        {
            return;
        }
        AppVideoEditV2DefaultStepsText.Text = checked((int)Math.Round(e.NewValue))
            .ToString(CultureInfo.InvariantCulture);
        SaveVideoToolsV2PreferencesFromControls();
    }

    private void SaveVideoToolsV2PreferencesFromControls()
    {
        if (_initializing || _videoToolsV2PreferencesSyncing)
            return;
        int steps = checked((int)Math.Round(
            AppVideoEditV2DefaultStepsSlider.Value));
        VideoToolsV2PreferenceState current = _videoToolsV2Preferences;
        _videoToolsV2Preferences = NormalizeVideoToolsV2Preferences(
            new VideoToolsV2PreferenceState
            {
                Edit = new VideoEditV2PreferenceState
                {
                    AudioPolicy = ReadComboBoxTag(
                        AppVideoEditV2DefaultAudioComboBox),
                    StrengthTag = ReadComboBoxTag(
                        AppVideoEditV2DefaultStrengthComboBox),
                    MaximumPixelAreaTier = ReadComboBoxTag(
                        AppVideoEditV2DefaultCanvasComboBox),
                    Steps = steps,
                    SkipReview =
                        AppVideoEditV2DefaultSkipReviewCheckBox.IsChecked == true,
                    ExtensionData = CloneExtensionData(
                        current.Edit?.ExtensionData),
                },
                Finish = new VideoFinishV2PreferenceState
                {
                    Mode = ReadComboBoxTag(
                        AppVideoFinishV2DefaultModeComboBox),
                    Scale = int.TryParse(
                            ReadComboBoxTag(
                                AppVideoFinishV2DefaultScaleComboBox),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out int scale)
                        ? scale
                        : 2,
                    ExtensionData = CloneExtensionData(
                        current.Finish?.ExtensionData),
                },
                ExtensionData = CloneExtensionData(current.ExtensionData),
            });
        SaveState();
        AppVideoToolsV2PreferenceStatusText.Text = VideoEditV2Text(
            "UiVideoToolsV2DefaultsSaved");
    }

    public bool SaveVideoEditV2StyleForSmoke(
        string name,
        string instruction,
        string audio,
        string strength,
        string canvasTier,
        int steps,
        string styleTag)
    {
        ModalVideoEditV2StyleNameTextBox.Text = name;
        ModalVideoEditV2InstructionTextBox.Text = instruction;
        SelectComboBoxItemByTag(ModalVideoEditV2AudioComboBox, audio, 0);
        SelectComboBoxItemByTag(ModalVideoEditV2StrengthComboBox, strength, 1);
        SelectComboBoxItemByTag(
            ModalVideoEditV2CanvasComboBox,
            VideoEditV2PixelAreaTierToTag(canvasTier),
            2);
        SelectComboBoxItemByTag(ModalVideoEditV2StyleComboBox, styleTag, 0);
        ModalVideoEditV2StepsSlider.Value = steps;
        ModalVideoEditV2StepsTextBox.Text = steps.ToString(
            CultureInfo.InvariantCulture);
        ModalVideoEditV2SaveStyle_Click(
            ModalVideoEditV2SaveStyleButton,
            new RoutedEventArgs(Button.ClickEvent, ModalVideoEditV2SaveStyleButton));
        return _videoEditV2Styles.Any(style => string.Equals(
            style.Name,
            name.Trim(),
            StringComparison.OrdinalIgnoreCase));
    }

    public bool ApplyVideoEditV2StyleForSmoke(string name)
    {
        VideoEditV2StyleState? style = _videoEditV2Styles.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
        if (style is null)
            return false;
        ApplyVideoEditV2Style(style);
        return string.Equals(
                ModalVideoEditV2InstructionTextBox.Text,
                style.InstructionTemplate,
                StringComparison.Ordinal)
            && ReadComboBoxTag(ModalVideoEditV2AudioComboBox)
                == style.AudioPolicy
            && ReadComboBoxTag(ModalVideoEditV2StrengthComboBox)
                == style.StrengthTag
            && VideoEditV2PixelAreaTagToTier(
                ReadComboBoxTag(ModalVideoEditV2CanvasComboBox))
                == style.MaximumPixelAreaTier
            && ReadComboBoxTag(ModalVideoEditV2StyleComboBox)
                == style.StyleTag
            && (int)ModalVideoEditV2StepsSlider.Value == style.Steps;
    }

    public bool DeleteVideoEditV2StyleForSmoke(string name)
        => TryMutateVideoEditV2Styles(styles =>
            styles.RemoveAll(candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase)) == 1,
            out _);

    public void ArmVideoEditV2StyleInvalidationForSmoke()
    {
        _videoEditV2Candidate = new VideoEditV2CompiledCandidate(
            "compiled-marker-must-not-persist",
            "保存禁止の要約",
            "compiler-marker-must-not-persist",
            new VideoEditV2RendererSidecar(
                "v2v",
                "guidance-marker-must-not-persist",
                "renderer-compiler-marker-must-not-persist",
                new string('b', 64)),
            new string('a', 64),
            "synthetic-source",
            "synthetic-context");
        _videoEditV2CandidateStale = false;
        _videoEditV2CandidateApproved = true;
        _videoEditV2WriterReady = true;
    }

    public int VideoEditV2StyleCountForSmoke => _videoEditV2Styles.Count;
    public bool VideoEditV2CandidateStaleForStyleSmoke
        => _videoEditV2Candidate is null || _videoEditV2CandidateStale;
    public bool VideoEditV2ReadinessStaleForStyleSmoke
        => !_videoEditV2WriterReady;
    public string VideoToolsV2DefaultsForSmoke
        => string.Join(
            '/',
            _videoToolsV2Preferences.Edit!.AudioPolicy,
            _videoToolsV2Preferences.Edit.StrengthTag,
            _videoToolsV2Preferences.Edit.MaximumPixelAreaTier,
            _videoToolsV2Preferences.Edit.Steps.ToString(CultureInfo.InvariantCulture),
            _videoToolsV2Preferences.Edit.SkipReview ? "skip" : "review",
            _videoToolsV2Preferences.Finish!.Mode,
            _videoToolsV2Preferences.Finish.Scale.ToString(CultureInfo.InvariantCulture));

    public void SetVideoToolsV2DefaultsForSmoke(
        string audio,
        string strength,
        string canvasTier,
        int steps,
        bool skipReview,
        string finishMode,
        int finishScale)
    {
        VideoToolsV2PreferenceState current = _videoToolsV2Preferences;
        _videoToolsV2Preferences = NormalizeVideoToolsV2Preferences(new()
        {
            Edit = new VideoEditV2PreferenceState
            {
                AudioPolicy = audio,
                StrengthTag = strength,
                MaximumPixelAreaTier = canvasTier,
                Steps = steps,
                SkipReview = skipReview,
                ExtensionData = CloneExtensionData(current.Edit?.ExtensionData),
            },
            Finish = new VideoFinishV2PreferenceState
            {
                Mode = finishMode,
                Scale = finishScale,
                ExtensionData = CloneExtensionData(current.Finish?.ExtensionData),
            },
            ExtensionData = CloneExtensionData(current.ExtensionData),
        });
        SyncVideoToolsV2PreferenceControls();
        SaveState();
    }
}

public sealed class VideoEditV2StyleState
{
    public string Name { get; set; } = "";
    public string InstructionTemplate { get; set; } = "";
    public string AudioPolicy { get; set; } = "preserve";
    public string StrengthTag { get; set; } = "balanced";
    public string MaximumPixelAreaTier { get; set; } = "high";
    public int Steps { get; set; } = 20;
    public string StyleTag { get; set; } = "none";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class VideoToolsV2PreferenceState
{
    public VideoEditV2PreferenceState? Edit { get; set; }
    public VideoFinishV2PreferenceState? Finish { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static VideoToolsV2PreferenceState CreateDefaults()
        => new()
        {
            Edit = new VideoEditV2PreferenceState(),
            Finish = new VideoFinishV2PreferenceState(),
        };
}

public sealed class VideoEditV2PreferenceState
{
    public string AudioPolicy { get; set; } = "preserve";
    public string StrengthTag { get; set; } = "balanced";
    public string MaximumPixelAreaTier { get; set; } = "high";
    public int Steps { get; set; } = 20;
    public bool SkipReview { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class VideoFinishV2PreferenceState
{
    public string Mode { get; set; } = "standard";
    public int Scale { get; set; } = 2;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
