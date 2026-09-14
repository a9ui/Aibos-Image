using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PhotoViewer.Wpf;

// One native input surface: edit the notation or operate the same text.
// No persistence, image reads, inference, or jobs are started by this control.
public sealed class VideoPromptAuthoringControl : UserControl
{
    private static Brush ColorBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private static readonly Brush Ink = ColorBrush("#F0F4FF");
    private static readonly Brush Muted = ColorBrush("#B7C5DD");
    private static readonly Brush Paper = ColorBrush("#101827");
    private readonly ToggleButton _edit = new() { Content = "編集", Padding = new Thickness(16, 6, 16, 6), MinHeight = 32 };
    private readonly TextBox _input = Editor("指示の本文", 180);
    private readonly TextBox _notes = Editor("日本語訳・メモ", 70);
    private readonly Expander _notesPanel = new() { Header = "日本語訳・メモ", Foreground = Muted, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _reading = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 27, Foreground = Ink };
    private readonly TextBlock _hint = Label("青：手動選択　紫：画像AI　黄：条件判定", false);
    private readonly TextBlock _variantHint = Label("", false);
    private readonly ScrollViewer _reader;
    private readonly WrapPanel _tools = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox _source = new() { MinHeight = 30, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Expander _base = new() { Header = "元のスタイル本文", Foreground = Muted, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBox _baseInput = Editor("元のスタイル本文", 90);
    private VideoPromptProgram _program = new();
    private string? _sourcePrompt;
    private bool _loading;
    private bool _photo;
    public event Action<VideoPromptAuthoringControl, VideoPromptProgram, string?>? Changed;
    public event Action<string>? SourceChanged;
    public event Action? DetailsRequested;

    public VideoPromptAuthoringControl()
    {
        Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
        var panel = new StackPanel { Background = ColorBrush("#171C25") };
        Content = panel;
        panel.Children.Add(Label("画像の扱い", true));
        _source.ItemsSource = new[] { "自動で振り分ける", "アニメとして扱う", "実写として扱う" };
        AutomationProperties.SetName(_source, "今回の画像の扱い");
        panel.Children.Add(_source);
        panel.Children.Add(_variantHint);
        _source.SelectionChanged += (_, _) =>
        {
            if (!_loading) SourceChanged?.Invoke(_source.SelectedIndex switch { 1 => "anime", 2 => "photoreal", _ => "auto" });
        };
        _source.SetResourceReference(StyleProperty, "PhotorealSettingsComboBox");

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        DockPanel.SetDock(_edit, Dock.Right);
        header.Children.Add(_edit);
        header.Children.Add(Label("動き・カメラ・表情", true));
        panel.Children.Add(header);
        SetEditStyle();
        AutomationProperties.SetName(_edit, "本文を編集");
        _edit.ToolTip = "もう一度押すと、本文の選択操作に戻ります";
        _edit.Checked += (_, _) => RefreshMode();
        _edit.Unchecked += (_, _) => RefreshMode();
        AddButton(_tools, "［手動選択］", () => WrapSelection(false));
        AddButton(_tools, "｛画像AI｝", () => WrapSelection(true));
        panel.Children.Add(_tools);
        var field = new Grid();
        field.Children.Add(_input);
        _reader = new ScrollViewer
        {
            Content = _reading, MinHeight = 180, MaxHeight = 360, Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Paper,
        };
        field.Children.Add(new Border { BorderBrush = ColorBrush("#3F5474"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = _reader });
        panel.Children.Add(field);
        panel.Children.Add(_hint);
        _base.Content = _baseInput;
        panel.Children.Add(_base);
        var notesContent = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        notesContent.Children.Add(_notes);
        notesContent.Children.Add(Label("動画には使わない説明です。スタイルと一緒に保存できます。", false));
        _notesPanel.Content = notesContent;
        panel.Children.Add(_notesPanel);
        var footer = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddButton(footer, "自動選択・スタイルの詳細", () => DetailsRequested?.Invoke());
        panel.Children.Add(footer);

        _input.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (_program.Enabled)
            {
                if (_photo) _program.PhotorealTemplate = _input.Text;
                else _program.Template = _input.Text;
            }
            else if (_photo && _program.UseSourceVariants) _program.PhotorealBaseH3Template = _input.Text;
            else _program.BaseH3Template = _input.Text;
            Publish(_program.Enabled ? null : _input.Text);
            Render();
        };
        _notes.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (_photo && _program.UseSourceVariants) _program.PhotorealDescription = _notes.Text;
            else _program.Description = _notes.Text;
            Publish(null);
        };
        _baseInput.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (_photo) _program.PhotorealBaseH3Template = _baseInput.Text;
            else _program.BaseH3Template = _baseInput.Text;
            Publish(null);
        };
        RefreshMode();
    }

    public void Load(VideoPromptProgram program, string rawPrompt, string sourceOverride, string effectiveKind, string? sourcePrompt)
    {
        _loading = true;
        try
        {
            _program = program.Clone();
            _photo = effectiveKind == "photoreal" && (program.UseSourceVariants || !string.IsNullOrEmpty(program.PhotorealTemplate));
            _sourcePrompt = sourcePrompt;
            _source.SelectedIndex = sourceOverride switch { "anime" => 1, "photoreal" => 2, _ => 0 };
            SetText(_input, program.Enabled ? program.TemplateFor(effectiveKind) : rawPrompt);
            SetText(_notes, program.DescriptionFor(effectiveKind));
            _variantHint.Visibility = program.UseSourceVariants ? Visibility.Visible : Visibility.Collapsed;
            _variantHint.Text = $"今回は{(effectiveKind == "photoreal" ? "実写" : "アニメ")}用の本文を使用";
            SetText(_baseInput, program.BaseTemplateFor(effectiveKind));
            _base.Visibility = program.Enabled && !program.AnnotatedH3 && !string.IsNullOrEmpty(_baseInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loading = false; }
        Render();
    }

    private static void SetText(TextBox box, string text)
    {
        if (box.Text == text) return;
        int caret = box.CaretIndex;
        box.Text = text;
        box.CaretIndex = Math.Min(caret, text.Length);
    }

    private void Publish(string? rawPrompt) => Changed?.Invoke(this, _program.Clone(), rawPrompt);

    private void RefreshMode()
    {
        bool editing = _edit.IsChecked == true;
        _input.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ((UIElement)_reader.Parent).Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _tools.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        _reading.Inlines.Clear();
        if (!_program.Enabled)
        {
            _reading.Inlines.Add(new Run(_input.Text.Length == 0 ? "スタイルを選ぶか、編集から本文を入力してください。" : ReadableH3Literal(_input.Text)));
            _hint.Text = "編集で文章を選び、［手動選択］や｛画像AI｝にできます。";
            return;
        }
        if (!VideoPromptLanguage.TryParse(_input.Text, out var tokens, out string error))
        {
            _reading.Inlines.Add(new Run(_input.Text));
            _hint.Text = error;
            return;
        }
        _hint.Text = _edit.IsChecked == true
            ? "候補は / で区切ります。編集を閉じると本文から選べます。"
            : "色付きの語句から選択 · 取り消し線は今回は使わない";
        _hint.ToolTip = "緑：カメラ　青：動作　桃：表情　橙：結末　紫：画像AI";
        foreach (VideoPromptToken token in tokens)
        {
            if (token.Kind == 't') { _reading.Inlines.Add(new Run(_program.AnnotatedH3 ? ReadableH3Literal(token.Text) : token.Text)); continue; }
            VideoPromptOption option = _program.OptionFor(token);
            bool on = token.Kind == '[' ? _program.IsOn(option, _sourcePrompt) : option.Mode != "off";
            bool automatic = token.Kind == '{' && option.Mode == "auto" && _program.ImageChoices;
            string shown = automatic ? string.Join(" / ", token.Choices)
                : token.Choices.Count > 0 ? token.Choices.ElementAtOrDefault(option.ChoiceIndex) ?? "選び直す" : token.Text;
            bool camera = option.Category is "camera" or "viewpoint" || (option.Category == "" && Regex.IsMatch(token.Text, @"カメラ|camera|POV|angle|focus|視点", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            string color = token.Kind == '{' ? "#DEC5FF" : option.Mode == "auto" ? "#FFDD95" : camera ? "#A0EEDA" : option.Category == "expression" ? "#FFC8E3" : option.Category == "ending" ? "#FFD1A1" : "#B8D9FF";
            string fill = token.Kind == '{' ? "#493063" : option.Mode == "auto" ? "#57421A" : camera ? "#164D48" : option.Category == "expression" ? "#612843" : option.Category == "ending" ? "#623919" : "#203F6B";
            var link = new Hyperlink(new Run(shown))
            {
                Foreground = ColorBrush(color), Background = ColorBrush(fill),
                TextDecorations = on ? null : System.Windows.TextDecorations.Strikethrough,
                ToolTip = (option.Label.Length > 0 ? option.Label + " · " : "") + (on ? "クリックして候補や使い方を選ぶ" : "今回は使いません。クリックですぐ戻せます"),
            };
            AutomationProperties.SetName(link, shown + (on ? " 使用する" : " 使用しない"));
            link.Click += (_, _) => OpenOption(token, link);
            _reading.Inlines.Add(link);
        }
    }

    // Display-only: edit mode and the compiled prompt retain every character.
    // Only the pinned structural prefix and section labels are abbreviated.
    private static string ReadableH3Literal(string text)
    {
        const string prefix = "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\nintegrated_multimodal_description: [Shot 1] ";
        string shown = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (shown.StartsWith(prefix, StringComparison.Ordinal)) shown = shown[prefix.Length..];
        return shown.Replace("\n\noverall_soundscape: ", "\n\n音・声：", StringComparison.Ordinal)
            .Replace("\n\nnon_diegetic_music: ", "\n\n音楽：", StringComparison.Ordinal);
    }

    public bool ReadableH3PreservesSourceForSmoke()
    {
        string before = _input.Text;
        Render();
        string reading = new TextRange(_reading.ContentStart, _reading.ContentEnd).Text;
        return before == _input.Text && !reading.Contains("integrated_multimodal_description:", StringComparison.Ordinal)
            && !reading.Contains("For the target video,", StringComparison.Ordinal) && reading.Contains("音・声：", StringComparison.Ordinal);
    }

    private void OpenOption(VideoPromptToken token, Hyperlink link)
    {
        var menu = new ContextMenu { Background = Paper, Foreground = Ink, BorderBrush = ColorBrush("#526887") };
        void Item(string label, bool selected, Action<VideoPromptOption> change)
        {
            var item = new MenuItem { Header = new TextBlock { Text = label, Foreground = Ink }, IsCheckable = true, IsChecked = selected, Background = Paper, Foreground = Ink };
            item.Click += (_, _) =>
            {
                VideoPromptOption option = JsonSerializer.Deserialize<VideoPromptOption>(JsonSerializer.Serialize(_program.OptionFor(token)))!;
                if (!_program.Options.ContainsKey(token.Key) && _program.Options.Count >= 128) return;
                change(option);
                _program.SetManualOption(token, option, _photo ? "photoreal" : "anime");
                Publish(null);
                Render();
            };
            menu.Items.Add(item);
        }
        VideoPromptOption current = _program.OptionFor(token);
        Item(current.Group.Length > 0 ? "これを使う（同じ組の他候補は外す）" : "使う", current.Mode == "on", option => option.Mode = "on");
        Item("今回は使わない", current.Mode == "off", option => option.Mode = "off");
        if (token.Kind == '{') Item("画像AIに選んでもらう", current.Mode == "auto", option => { option.Mode = "auto"; _program.ImageChoices = true; });
        for (int i = 0; i < token.Choices.Count; i++)
        {
            int index = i;
            Item(current.ChoiceLabels.ElementAtOrDefault(i) ?? token.Choices[i], current.Mode == "on" && current.ChoiceIndex == i, option => { option.Mode = "on"; option.ChoiceIndex = index; });
        }
        menu.PlacementTarget = _reading;
        menu.Placement = PlacementMode.MousePoint;
        link.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void WrapSelection(bool automatic)
    {
        string selected = _input.SelectedText;
        int start = _input.SelectionStart;
        string before = _input.Text[..start];
        string after = _input.Text[(start + _input.SelectionLength)..];
        if (!_program.Enabled)
        {
            // Existing H3 references and literal punctuation are escaped, so
            // conversion never turns [Shot 1] into a switch or changes its text.
            before = EscapeLiteral(before);
            after = EscapeLiteral(after);
            selected = EscapeLiteral(selected);
        }
        string body = selected.Length > 0 ? selected : "候補1 / 候補2";
        string next = before + (automatic ? "{" : "[") + body + (automatic ? "}" : "]") + after;
        if (next.Length > 8000) { _hint.Text = "選択式にした本文が8000文字を超えます。"; return; }
        if (!_program.Enabled)
        {
            if (_program.UseSourceVariants)
            {
                string anime = EscapeLiteral(_program.BaseH3Template);
                string photo = EscapeLiteral(_program.PhotorealBaseH3Template);
                if (anime.Length > 8000 || photo.Length > 8000)
                { _hint.Text = "選択式にした本文が8000文字を超えます。"; return; }
                _program.Template = anime;
                _program.PhotorealTemplate = photo;
            }
            _program.BaseH3Template = "";
            _program.PhotorealBaseH3Template = "";
        }
        _program.Enabled = true;
        if (automatic) _program.ImageChoices = true;
        if (_photo) _program.PhotorealTemplate = next;
        else _program.Template = next;
        _input.Text = next;
    }

    public static string EscapeLiteral(string value)
    {
        var text = new System.Text.StringBuilder();
        foreach (char c in value)
        {
            if (c is '\\' or '[' or ']' or '{' or '}' or '［' or '］' or '｛' or '｝' or '/') text.Append('\\');
            text.Append(c);
        }
        return text.ToString();
    }

    private void SetEditStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Frame";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, ColorBrush("#657CA0"));
        border.SetValue(Border.BackgroundProperty, Paper);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(16, 6, 16, 6));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
        var active = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        active.Setters.Add(new Setter(Border.BackgroundProperty, ColorBrush("#245CC2"), "Frame"));
        active.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#99C9FF"), "Frame"));
        active.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { Color = Color.FromRgb(66, 145, 255), BlurRadius = 12, ShadowDepth = 0, Opacity = .65 }, "Frame"));
        template.Triggers.Add(active);
        _edit.Template = template;
        _edit.Foreground = Ink;
    }

    private static TextBox Editor(string name, double height)
    {
        var editor = new TextBox { MinHeight = height, MaxHeight = 360, MaxLength = 8000, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12),
            Background = Paper, Foreground = Ink, CaretBrush = Ink, SelectionBrush = ColorBrush("#315D9F"),
            BorderBrush = ColorBrush("#3F5474"), FontSize = 14 };
        AutomationProperties.SetName(editor, name);
        return editor;
    }

    private static TextBlock Label(string text, bool heading) => new()
    {
        Text = text, Foreground = heading ? Ink : Muted, TextWrapping = TextWrapping.Wrap,
        FontSize = heading ? 13 : 11, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, heading ? 8 : 5, 0, 6),
    };

    private void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 5, 10, 5), MinHeight = 30 };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    public bool ExerciseInlineForSmoke(Action<FrameworkElement> capture)
    {
        _edit.IsChecked = true;
        bool notation = _input.IsVisible && _input.Text.Contains('[') && !_reader.IsVisible && (string)_edit.Content == "編集";
        capture(this);
        _edit.IsChecked = false;
        Hyperlink? link = _reading.Inlines.OfType<Hyperlink>().FirstOrDefault();
        if (link is null) return false;
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        var menu = link.ContextMenu!;
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        bool off = _reading.Inlines.OfType<Hyperlink>().First().TextDecorations == System.Windows.TextDecorations.Strikethrough;
        link = _reading.Inlines.OfType<Hyperlink>().First();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        menu = link.ContextMenu!;
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        return notation && off && _program.Options.Values.Any(option => option.Mode == "on");
    }

    public void SelectSourceForSmoke(string kind) => _source.SelectedIndex = kind == "photoreal" ? 2 : kind == "anime" ? 1 : 0;
    public bool SelectOptionForSmoke(string category, int choiceIndex, Action<FrameworkElement>? capture = null)
    {
        _edit.IsChecked = false;
        if (!VideoPromptLanguage.TryParse(_input.Text, out var tokens, out _)) return false;
        var interactive = tokens.Where(t => t.Kind != 't').ToList();
        int index = interactive.FindIndex(t => _program.OptionFor(t).Category == category);
        if (index < 0) return false;
        var link = _reading.Inlines.OfType<Hyperlink>().ElementAt(index);
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        var menu = link.ContextMenu!;
        menu.UpdateLayout();
        capture?.Invoke(menu);
        int itemIndex = choiceIndex < 0 ? 1 : interactive[index].Choices.Count > 0 ? 2 + choiceIndex : 0;
        ((MenuItem)menu.Items[itemIndex]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        return true;
    }
    public void EditVariantForSmoke(string body, string note) { _input.Text = body; _notes.Text = note; }
    public void WrapPhraseForSmoke(string phrase)
    {
        _input.Select(_input.Text.IndexOf(phrase, StringComparison.Ordinal), phrase.Length);
        WrapSelection(false);
    }
}
