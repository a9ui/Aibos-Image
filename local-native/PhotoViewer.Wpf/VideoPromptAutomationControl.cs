using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

// Edits only the current program's settings. It never starts an AI operation.
public sealed class VideoPromptAutomationControl : StackPanel
{
    private bool _loading;
    private readonly CheckBox _rules = Check("元画像のプロンプトで条件を判定");
    private readonly CheckBox _choices = Check("画像から｛候補｝を選ぶ");
    private readonly CheckBox _plot = Check("動作例を補完の参考にする");
    private readonly CheckBox _physics = Check("重力・接触・動きのつながりを補う");
    private readonly TextBlock _help = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _samples = new() { MaxLength = 4000, MinHeight = 80, MaxHeight = 180,
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(16, 24, 39)), Foreground = Ink, CaretBrush = Ink, Padding = new Thickness(8) };
    private readonly ComboBox _original = new() { ItemsSource = new[] { "アニメ", "実写" }, MinHeight = 30 };
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(213, 225, 245));
    public event Action<Action<VideoPromptProgram>>? Changed;

    public VideoPromptAutomationControl()
    {
        Children.Add(_rules);
        Children.Add(_help);
        foreach (CheckBox check in new[] { _choices, _plot, _physics }) Children.Add(check);
        Bind(_rules, (p, v) => p.SourceRules = v);
        Bind(_choices, (p, v) => p.ImageChoices = v);
        Bind(_plot, (p, v) => p.ActionPlot = v);
        Bind(_physics, (p, v) => p.PhysicalContinuity = v);
        Children.Add(new Expander { Header = "動作のサンプル", Foreground = Ink, Margin = new Thickness(0, 8, 0, 10), Content = _samples });
        Children.Add(new TextBlock { Text = "Original画像の既定の扱い", Foreground = Ink, Margin = new Thickness(0, 0, 0, 5) });
        _original.SetResourceReference(StyleProperty, "PhotorealSettingsComboBox");
        Children.Add(_original);
        _samples.TextChanged += (_, _) => { if (!_loading) Changed?.Invoke(p => p.ActionSamples = _samples.Text); };
        _original.SelectionChanged += (_, _) => { if (!_loading) Changed?.Invoke(p => p.OriginalDefault = _original.SelectedIndex == 1 ? "photoreal" : "anime"); };
    }

    private static CheckBox Check(string label) => new() { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 0, 9) };
    private void Bind(CheckBox check, Action<VideoPromptProgram, bool> apply)
    {
        void Update(object sender, RoutedEventArgs e) { if (!_loading) Changed?.Invoke(p => apply(p, check.IsChecked == true)); }
        check.Checked += Update; check.Unchecked += Update;
    }
    public void Load(VideoPromptProgram program, bool enhance)
    {
        _loading = true;
        try
        {
            _rules.IsChecked = program.SourceRules; _choices.IsChecked = program.ImageChoices;
            _plot.IsChecked = program.ActionPlot; _physics.IsChecked = program.PhysicalContinuity;
            if (_samples.Text != program.ActionSamples) _samples.Text = program.ActionSamples;
            _original.SelectedIndex = program.OriginalDefault == "photoreal" ? 1 : 0;
            foreach (var check in new[] { _rules, _choices }) check.IsEnabled = program.Enabled;
            _samples.IsEnabled = true;
            _help.Text = !program.Enabled ? "条件判定と｛候補｝の選択は、本文を選択式にすると使えます。動作例・物理的なつながりの補完は、通常の本文でも使えます。"
                : enhance ? "条件は登録時に判定します。画像による候補選択と細部の補完は、キューの順番が来てから実行します。手動選択を優先します。"
                : "AIの補助は、下部の「AIで動きの細部を補完」を選ぶと使います。";
        }
        finally { _loading = false; }
    }
}
