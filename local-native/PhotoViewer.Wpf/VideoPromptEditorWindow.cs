using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

// A transient authoring surface. Its caller owns persistence and inference.
public sealed class VideoPromptEditorWindow : Window
{
    public VideoPromptProgram Program { get; }
    public string SourceOverride { get; private set; }
    public bool CreateCandidate { get; private set; }
    private readonly string _automaticKind;
    private readonly string? _sourcePrompt;
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, LineHeight = 28, Foreground = InkBrush };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange };
    private readonly TextBox _template;
    private readonly TextBox _photoreal;
    private readonly ComboBox _sourceMode;
    private readonly ComboBox _variant;
    private bool _ready;
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(28, 32, 40));
    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(230, 234, 242));

    public VideoPromptEditorWindow(VideoPromptProgram program, string sourceOverride, string automaticKind, string? sourcePrompt,
        IReadOnlyDictionary<string, string>? existingStyles = null)
    {
        Program = program.Clone();
        SourceOverride = sourceOverride;
        _automaticKind = automaticKind;
        _sourcePrompt = sourcePrompt;
        Title = "Aibos · 指示プロンプトを編集";
        Width = 900; Height = 720; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = PanelBrush; Foreground = InkBrush; FontSize = 13;
        // The main viewer has an implicit light-ink TextBlock style. Let
        // generated control labels inherit their own control's ink here.
        Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
        var root = new DockPanel { Margin = new Thickness(20), Background = PanelBrush };
        Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(buttons);
        AddButton(buttons, "キャンセル", () => DialogResult = false);
        AddButton(buttons, "この設定を使う", () => Accept(false));
        AddButton(buttons, "H3候補を作成", () => Accept(true));

        var tabs = new TabControl { Background = PanelBrush, Foreground = InkBrush };
        root.Children.Add(tabs);
        StackPanel author = AddTab(tabs, "本文と選択");
        CheckBox enabled = AddCheck(author, "Aibos指示言語を使う", Program.Enabled, v => Program.Enabled = v);
        AddText(author, "青い［部分］は手動ON/OFF、［候補 / 候補］は手動選択。紫の｛候補 / 候補｝は画像AI選択。色付き部分をクリックして設定できます。H3の完成文は元画面の欄に出ます。");
        _sourceMode = AddCombo(author, "今回の画像の扱い（画像には記憶しません）",
            ["自動", "アニメとして扱う", "実写として扱う"], SourceOverride == "anime" ? 1 : SourceOverride == "photoreal" ? 2 : 0);
        _sourceMode.SelectionChanged += (_, _) =>
        {
            SourceOverride = _sourceMode.SelectedIndex switch { 1 => "anime", 2 => "photoreal", _ => "auto" };
            RefreshPreview();
        };
        _variant = AddCombo(author, "編集する本文", ["Original / アニメ用", "実写用（空欄ならOriginal用を共有）"], 0);
        _template = AddEditor(author, "Original / アニメ用の指示", Program.Template, 8000, 120);
        _photoreal = AddEditor(author, "実写用の指示", Program.PhotorealTemplate, 8000, 120);
        _template.TextChanged += (_, _) => { Program.Template = _template.Text; RefreshPreview(); };
        _photoreal.TextChanged += (_, _) => { Program.PhotorealTemplate = _photoreal.Text; RefreshPreview(); };
        _variant.SelectionChanged += (_, _) => RefreshPreview();
        var palette = new WrapPanel(); author.Children.Add(palette);
        AddButton(palette, "視線を挿入", () => InsertSnippet("[こちらを見ながら / 視線を外しながら / 自然に瞬きしながら]"));
        AddButton(palette, "表情を挿入", () => InsertSnippet("[穏やかに微笑みながら / 明るく笑いながら / 落ち着いた表情で]"));
        AddButton(palette, "動作を挿入", () => InsertSnippet("[ゆっくり近づく / 振り向く / 手を振る / その場で軽く動く]"));
        AddButton(palette, "カメラを挿入", () => InsertSnippet("[固定カメラ / カメラがゆっくり寄る / カメラがゆっくり引く / カメラが左へ回り込む / カメラが右へ回り込む / カメラが被写体を追従する / カメラが左へパンする / カメラが右へパンする / 一人称視点で控えめな頭の揺れ / 控えめな手持ちカメラの揺れ]"));
        AddButton(palette, "AI候補を挿入", () => InsertSnippet("{歩く / 走る / 跳ぶ / その場で動かない}"));
        AddText(author, "クリックして切り替えるプレビュー", true);
        author.Children.Add(new Border { Padding = new Thickness(12), Margin = new Thickness(0, 5, 0, 0),
            BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Child = _preview });

        StackPanel rules = AddTab(tabs, "自動化と動作");
        AddCheck(rules, "元画像プロンプトによる条件判定", Program.SourceRules, v => { Program.SourceRules = v; RefreshPreview(); });
        AddText(rules, "［部分］の設定で「条件で自動」を選ぶと、含む／含まない／プロンプトあり／なしで決まります。OFF時とメタデータ未取得時は、その部分の既定値を使います。手動ON/OFFを条件やAIが変更することはありません。");
        AddCheck(rules, "画像AIで｛候補｝を選ぶ", Program.ImageChoices, v => { Program.ImageChoices = v; RefreshPreview(); });
        AddText(rules, "ONでも手動指定した候補を優先します。OFF時は各部分の手動候補を使います。画像AIは「H3候補を作成」を押したときだけ動きます。");
        AddCheck(rules, "画像AIで秒数付きの動作プランを作る", Program.ActionPlot, v => Program.ActionPlot = v);
        TextBox samples = AddEditor(rules, "このスタイルのアクション例（生成指示とは別に保存）", Program.ActionSamples, 4000, 130);
        samples.TextChanged += (_, _) => Program.ActionSamples = samples.Text;
        AddCheck(rules, "重力・支持・接触と動きの連続性を指示する", Program.PhysicalContinuity, v => Program.PhysicalContinuity = v);
        AddText(rules, "支持中は保持。指示された接触解放の後や、すでに支持がなく一時的な変位が明確な場合は重力・慣性・減衰を指示します。曖昧なら動作や速度を推測しません。物理シミュレーションや結果の保証ではありません。");
        ComboBox original = AddCombo(rules, "Original画像の既定の扱い（スタイルに保存可能）", ["アニメ", "実写"], Program.OriginalDefault == "photoreal" ? 1 : 0);
        original.SelectionChanged += (_, _) => { Program.OriginalDefault = original.SelectedIndex == 1 ? "photoreal" : "anime"; RefreshPreview(); };
        AddText(rules, "実写化出力は自動で実写扱いです。今回の手動指定を優先し、画像ファイルへ分類を書き込みません。");

        StackPanel description = AddTab(tabs, "日本語説明とLoRA");
        AddText(description, "日本語の説明・訳はこの欄へ。動画生成にもAI整形にも送信しません。スタイル保存で本文と一緒に残せます。");
        TextBox notes = AddEditor(description, "日本語の説明・訳（編集可能）", Program.Description, 8000, 220);
        notes.TextChanged += (_, _) => Program.Description = notes.Text;
        if (Program.UseSourceVariants)
        {
            TextBox photoNotes = AddEditor(description, "実写用の日本語の説明・訳", Program.PhotorealDescription, 8000, 160);
            photoNotes.TextChanged += (_, _) => Program.PhotorealDescription = photoNotes.Text;
        }
        TextBox lora = AddEditor(description, "専用LoRAの候補ID／メモ", Program.PreferredLoraId, 200, 48);
        lora.TextChanged += (_, _) => Program.PreferredLoraId = lora.Text;
        AddText(description, "現在のH3生成経路はLoRA指定に未対応です。この欄は候補の記録専用で、適用・ダウンロードはしません。自動翻訳も現在は未接続です。");
        AddText(description, "「この設定を使う」で編集中の設定へ反映します。再起動後も残すには、元画面でStyle名を指定して保存してください。");
        StackPanel imports = AddTab(tabs, "既存Styleを組み合わせる");
        AddText(imports, "既存のアニメ用・実写用H3文を、それぞれ土台として取り込めます。追加の［選択部品］は「本文と選択」に書きます。土台の [Shot 1] などはオプションとして解釈しません。");
        TextBox baseOriginal = AddEditor(imports, "Original / アニメ用のH3土台", Program.BaseH3Template, 8000, 130);
        TextBox basePhoto = AddEditor(imports, "実写用のH3土台（空欄ならOriginal用を共有）", Program.PhotorealBaseH3Template, 8000, 130);
        if (Program.AnnotatedH3)
        {
            baseOriginal.IsReadOnly = basePhoto.IsReadOnly = true;
            AddText(imports, "選択式にしたスタイルの元の本文です。既定状態との照合用に保持しています。文章や候補の編集は「本文と選択」で行えます。");
        }
        baseOriginal.TextChanged += (_, _) => Program.BaseH3Template = baseOriginal.Text;
        basePhoto.TextChanged += (_, _) => Program.PhotorealBaseH3Template = basePhoto.Text;
        if (!Program.AnnotatedH3 && existingStyles is { Count: > 0 })
        {
            string[] names = existingStyles.Keys.ToArray();
            ComboBox sourceStyle = AddCombo(imports, "取り込む既存Style", names, 0);
            var importButtons = new WrapPanel(); imports.Children.Add(importButtons);
            AddButton(importButtons, "Original用に取り込む", () => baseOriginal.Text = existingStyles[names[sourceStyle.SelectedIndex]]);
            AddButton(importButtons, "実写用に取り込む", () => basePhoto.Text = existingStyles[names[sourceStyle.SelectedIndex]]);
        }
        AddText(imports, "取込は編集用のコピーです。確認後、ひとつのStyleとして保存すると入力画像に応じて土台が切り替わります。説明文は「日本語説明とLoRA」へ移して保存できます。");
        _ready = true;
        RefreshPreview();
    }

    private void Accept(bool create)
    {
        if (!Program.Validate(out string error)) { _status.Text = error; return; }
        if (create && !Program.Enabled) { _status.Text = "Aibos指示言語をONにしてください。"; return; }
        if (create && !VideoPromptLanguage.TryParse(Program.TemplateFor(EffectiveKind()), out _, out error))
        { _status.Text = error; return; }
        CreateCandidate = create;
        DialogResult = true;
    }

    private string EffectiveKind() => SourceOverride != "auto" ? SourceOverride
        : _automaticKind == "photoreal" ? "photoreal" : Program.OriginalDefault;

    private void InsertSnippet(string snippet)
    {
        TextBox editor = _variant.SelectedIndex == 1 ? _photoreal : _template;
        if (editor.Text.Length - editor.SelectionLength + snippet.Length > 8000)
        { _status.Text = "挿入すると8000文字を超えます。"; return; }
        int start = editor.SelectionStart;
        editor.SelectedText = snippet;
        editor.CaretIndex = start + snippet.Length;
        editor.Focus();
    }

    private void RefreshPreview()
    {
        if (!_ready) return;
        bool photo = _variant.SelectedIndex == 1;
        _template.Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        _photoreal.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
        ((UIElement)_template.Tag).Visibility = _template.Visibility;
        ((UIElement)_photoreal.Tag).Visibility = _photoreal.Visibility;
        _preview.Inlines.Clear();
        string text = photo ? Program.PhotorealTemplate : Program.Template;
        if (!VideoPromptLanguage.TryParse(text, out var tokens, out string error)) { _status.Text = error; return; }
        foreach (VideoPromptToken token in tokens)
        {
            if (token.Kind == 't') { _preview.Inlines.Add(new Run(token.Text)); continue; }
            VideoPromptOption option = Program.OptionFor(token);
            bool on = token.Kind == '[' ? Program.IsOn(option, _sourcePrompt) : option.Mode != "off";
            string state = token.Kind == '[' ? (on ? "ON" : "OFF")
                : Program.ImageChoices && option.Mode == "auto" ? "画像AI"
                : option.Mode == "off" ? "OFF"
                : token.Choices.ElementAtOrDefault(option.ChoiceIndex) ?? "要選択";
            string shown = token.Kind == '[' && token.Choices.Count > 0
                ? token.Choices.ElementAtOrDefault(option.ChoiceIndex) ?? token.Text : token.Text;
            var link = new Hyperlink(new Run($"{(token.Kind == '[' ? '［' : '｛')}{shown} · {state}{(token.Kind == '[' ? '］' : '｝')}"))
            {
                Foreground = !on ? Brushes.LightSteelBlue : token.Kind == '[' ? Brushes.DeepSkyBlue : Brushes.Violet,
                TextDecorations = on ? null : System.Windows.TextDecorations.Strikethrough,
                ToolTip = "クリックして選択と条件を編集",
            };
            AutomationProperties.SetName(link, token.Text + " " + state);
            link.Click += (_, _) => EditOption(token);
            _preview.Inlines.Add(link);
        }
        _status.Text = $"今回の自動振り分け: {(EffectiveKind() == "photoreal" ? "実写" : "アニメ")}。"
            + (_sourcePrompt is null ? "元画像プロンプト未取得。条件は既定値を使用。" : "元画像プロンプトを参照できます。");
    }

    private void EditOption(VideoPromptToken token)
    {
        var option = Program.OptionFor(token);
        var dialog = new Window { Title = token.Text, Owner = this, Width = 540, Height = 460,
            MinWidth = 440, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = PanelBrush, Foreground = InkBrush, ShowActivated = ShowActivated, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = new ScrollViewer { Content = panel, Background = PanelBrush, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AddText(panel, token.Text, true);
        ComboBox mode = AddCombo(panel, "選び方", token.Kind == '['
            ? ["ON", "OFF", "条件で自動"] : ["手動候補", "OFF", "画像AI"], option.Mode == "off" ? 1 : option.Mode == "auto" ? 2 : 0);
        ComboBox? choices = token.Choices.Count > 0 ? AddCombo(panel, "手動候補（画像AIがOFFのときも使用）",
            token.Choices.Select((text, index) => option.ChoiceLabels.ElementAtOrDefault(index) ?? text).ToArray(), option.ChoiceIndex) : null;
        if (option.Group.Length > 0) AddText(panel, "これを使うと、同じ組の他の候補は外れます。文章は取り消し線で残ります。");
        ComboBox? condition = token.Kind == '[' ? AddCombo(panel, "元画像プロンプトの条件",
            ["指定文字列を含む", "指定文字列を含まない", "プロンプトがある", "プロンプトがない"],
            option.Condition switch { "absent" => 1, "has-prompt" => 2, "no-prompt" => 3, _ => 0 }) : null;
        TextBox? keyword = token.Kind == '[' ? AddEditor(panel, "指定文字列（大文字・小文字を区別しません）", option.Keyword, 200, 40) : null;
        CheckBox fallback = AddCheck(panel, "条件判定OFF／元プロンプト未取得時の既定値をON", option.DefaultOn, _ => { });
        if (token.Kind == '{') fallback.Visibility = Visibility.Collapsed;
        AddButton(panel, "反映", () =>
        {
            if (!Program.Options.ContainsKey(token.Key) && Program.Options.Count >= 128)
            { _status.Text = "保存できるオプション設定は128個までです。"; dialog.DialogResult = false; return; }
            option.Mode = mode.SelectedIndex switch { 1 => "off", 2 => "auto", _ => "on" };
            option.DefaultOn = fallback.IsChecked == true;
            if (choices is not null) option.ChoiceIndex = Math.Max(0, choices.SelectedIndex);
            if (condition is not null) option.Condition = condition.SelectedIndex switch { 1 => "absent", 2 => "has-prompt", 3 => "no-prompt", _ => "contains" };
            if (keyword is not null) option.Keyword = keyword.Text;
            Program.SetManualOption(token, option, _variant.SelectedIndex == 1 ? "photoreal" : "anime");
            dialog.DialogResult = true;
        });
        dialog.ShowDialog();
        // The option is mutated only by the explicit Apply button. Refresh
        // after the nested window closes, including window-manager close.
        RefreshPreview();
    }

    public bool ExerciseManualOptionClickForSmoke(Action<FrameworkElement>? captureDialog = null)
    {
        Hyperlink? link = _preview.Inlines.OfType<Hyperlink>().FirstOrDefault();
        if (link is null) return false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Window? dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Owner == this);
            if (dialog?.Content is not ScrollViewer { Content: StackPanel panel }) return;
            dialog.UpdateLayout();
            captureDialog?.Invoke((FrameworkElement)dialog.Content);
            ComboBox? mode = panel.Children.OfType<ComboBox>().FirstOrDefault();
            if (mode is not null) mode.SelectedIndex = 1;
            panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        UpdateLayout();
        string displayed = string.Concat(_preview.Inlines.OfType<Hyperlink>().SelectMany(h => h.Inlines.OfType<Run>()).Select(r => r.Text));
        return Program.Options.Values.Any(o => o.Mode == "off")
            && displayed.Contains("OFF", StringComparison.Ordinal);
    }

    public void CaptureTabsForSmoke(Action<int, FrameworkElement> capture)
    {
        var tabs = ((DockPanel)Content).Children.OfType<TabControl>().Single();
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            UpdateLayout();
            capture(i, (FrameworkElement)Content);
        }
    }

    private static StackPanel AddTab(TabControl tabs, string title)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        tabs.Items.Add(new TabItem { Header = new TextBlock { Text = title, Foreground = Brushes.Black }, Foreground = Brushes.Black,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return panel;
    }
    private static void AddText(Panel panel, string text, bool heading = false)
        => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6),
            Foreground = InkBrush, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal });
    private static TextBox AddEditor(Panel panel, string label, string text, int limit, double height)
    {
        AddText(panel, label, true);
        UIElement heading = panel.Children[panel.Children.Count - 1];
        var editor = new TextBox { Text = text, MaxLength = limit + 1, MinHeight = height, MaxHeight = 260,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 30)), Foreground = InkBrush, CaretBrush = InkBrush,
            BorderBrush = Brushes.SlateGray, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
        editor.Tag = heading;
        AutomationProperties.SetName(editor, label); panel.Children.Add(editor); return editor;
    }
    private static ComboBox AddCombo(Panel panel, string label, string[] values, int selected)
    {
        AddText(panel, label);
        var combo = new ComboBox { ItemsSource = values.Select(value => new ComboBoxItem { Content = new TextBlock { Text = value, Foreground = Brushes.Black } }).ToArray(), SelectedIndex = selected, MinHeight = 30, Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.Black, Background = Brushes.White };
        AutomationProperties.SetName(combo, label); panel.Children.Add(combo); return combo;
    }
    private static CheckBox AddCheck(Panel panel, string label, bool value, Action<bool> changed)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Foreground = InkBrush, Margin = new Thickness(0, 12, 0, 8) };
        check.Checked += (_, _) => changed(true); check.Unchecked += (_, _) => changed(false);
        AutomationProperties.SetName(check, label); panel.Children.Add(check); return check;
    }
    private static void AddButton(Panel panel, string label, Action clicked)
    {
        var button = new Button { Content = new TextBlock { Text = label, Foreground = Brushes.Black }, Foreground = Brushes.Black, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(4), MinHeight = 32 };
        button.Click += (_, _) => clicked(); AutomationProperties.SetName(button, label); panel.Children.Add(button);
    }
}
