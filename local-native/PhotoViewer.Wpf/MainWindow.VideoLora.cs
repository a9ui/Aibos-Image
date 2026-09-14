using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private string _videoLoraDirectory = "";
    private long _videoLoraRevision, _videoLoraScanRevision;
    private readonly List<VideoLoraDraft> _videoLoras = [EmptyVideoLora(), EmptyVideoLora(), EmptyVideoLora()];
    private VideoLoraChoice[] _videoLoraChoices = [];
    private static VideoLoraDraft EmptyVideoLora() => new(new(1, "", "", "", 0, 1)) { Enabled = false };
    private sealed class VideoLoraDraft(VideoLoraSelection selection)
    {
        public VideoLoraSelection Selection = selection;
        public bool Enabled = true;
    }
    private sealed record VideoLoraChoice(string Directory, string Name, long Bytes) { public override string ToString() => Name.Length == 0 ? "LoRAを選択" : Name; }

    private VideoLoraSelection[]? CurrentVideoLoras()
    {
        var selected = _videoLoras.Where(x => x.Enabled && x.Selection.FileName.Length > 0 && x.Selection.Strength != 0).Select(x => x.Selection).ToArray();
        return selected.Length == 0 ? null : selected;
    }

    private void VideoLorasChanged()
    {
        _videoLoraRevision++;
        VideoH3PromptRewriteContextChanged();
    }

    private async void ChooseVideoLoraFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "動画LoRAの保存フォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _videoLoraDirectory = dialog.FolderName;
        SaveState();
        await RefreshVideoLorasAsync();
    }

    private async void RefreshVideoLoras_Click(object sender, RoutedEventArgs e) => await RefreshVideoLorasAsync();

    private async Task RefreshVideoLorasAsync()
    {
        if (VideoLoraRows is null) return;
        long scan = ++_videoLoraScanRevision;
        string directory = _videoLoraDirectory;
        _videoLoraChoices = []; RenderVideoLoraRows();
        VideoLoraFolderText.Text = directory.Length == 0 ? "保存フォルダを選んでください" : directory;
        VideoLoraStatus.Text = directory.Length == 0 ? "フォルダ内から追加できます。何も追加しなければLoRAを使いません。" : "フォルダを確認しています…";
        try
        {
            VideoLoraChoice[] files = directory.Length == 0 ? [] : await Task.Run(() =>
            {
                if (!new VideoLoraSelection(1, directory, "probe.safetensors", "", 0, 1).ValidNames())
                    throw new InvalidDataException("ローカルドライブの保存フォルダを選んでください。");
                VideoLoraSelection.RequireNormalPath(directory);
                var found = new List<VideoLoraChoice>();
                int inspected = 0;
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (++inspected > 20_000) throw new InvalidDataException("ファイル数が多いため一覧を読み込めません。LoRA専用のフォルダを選んでください。");
                    if (!file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0) found.Add(new(directory, info.Name, info.Length));
                }
                return found.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (scan != _videoLoraScanRevision) return;
            _videoLoraChoices = files;
            if (directory.Length > 0) VideoLoraStatus.Text = files.Length == 0 ? "このフォルダにはsafetensorsファイルがありません。" : $"{files.Length}個のファイル · ファイルの追加後は一覧を更新してください。";
            RenderVideoLoraRows();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (scan == _videoLoraScanRevision) VideoLoraStatus.Text = $"LoRAフォルダを読み込めません: {e.Message}";
        }
    }

    private void AddVideoLora_Click(object sender, RoutedEventArgs e)
    {
        if (_videoLoras.Count >= 8) { VideoLoraStatus.Text = "LoRAは8行まで追加できます。不要な行を外してください。"; return; }
        _videoLoras.Add(EmptyVideoLora());
        VideoLorasChanged(); RenderVideoLoraRows();
    }

    private void RenderVideoLoraRows()
    {
        if (VideoLoraRows is null) return;
        while (_videoLoras.Count < 3) _videoLoras.Add(EmptyVideoLora());
        VideoLoraAddButton.IsEnabled = _videoLoras.Count < 8;
        VideoLoraRows.Children.Clear();
        foreach (var draft in _videoLoras)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var head = new DockPanel();
            var remove = new Button { Content = "外す", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 3, 8, 3) };
            remove.Click += (_, _) => { _videoLoras.Remove(draft); VideoLorasChanged(); RenderVideoLoraRows(); };
            DockPanel.SetDock(remove, Dock.Right); head.Children.Add(remove);
            foreach (int direction in new[] { 1, -1 })
            {
                var move = new Button { Content = direction < 0 ? "↑" : "↓", ToolTip = direction < 0 ? "先に適用" : "後に適用", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(7, 3, 7, 3) };
                move.Click += (_, _) => { int index = _videoLoras.IndexOf(draft), next = index + direction; if (next < 0 || next >= _videoLoras.Count) return; _videoLoras.RemoveAt(index); _videoLoras.Insert(next, draft); VideoLorasChanged(); RenderVideoLoraRows(); };
                DockPanel.SetDock(move, Dock.Right); head.Children.Add(move);
            }
            var enabled = new CheckBox { IsChecked = draft.Enabled, IsEnabled = draft.Selection.FileName.Length > 0,
                Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), ToolTip = "このLoRAを使用する" };
            DockPanel.SetDock(enabled, Dock.Left); head.Children.Add(enabled);
            enabled.Click += (_, _) => { draft.Enabled = enabled.IsChecked == true; VideoLorasChanged(); };
            var number = new TextBlock { Text = $"{_videoLoras.IndexOf(draft) + 1}.", Width = 22,
                Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(number, Dock.Left); head.Children.Add(number);
            var choices = new List<VideoLoraChoice> { new("", "", 0) };
            choices.AddRange(_videoLoraChoices);
            var selected = choices.FirstOrDefault(x => string.Equals(x.Directory, draft.Selection.Directory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Name, draft.Selection.FileName, StringComparison.OrdinalIgnoreCase));
            if (selected is null && draft.Selection.FileName.Length > 0)
            { selected = new(draft.Selection.Directory, draft.Selection.FileName, draft.Selection.Bytes); choices.Add(selected); }
            var select = new ComboBox { ItemsSource = choices, SelectedItem = selected ?? choices[0], MinWidth = 120,
                Style = (Style)FindResource("PhotorealSettingsComboBox"),
                ToolTip = draft.Selection.FileName.Length == 0 ? "保存フォルダ内のLoRAを選択" : Path.Combine(draft.Selection.Directory, draft.Selection.FileName) };
            select.SelectionChanged += (_, _) =>
            {
                if (select.SelectedItem is not VideoLoraChoice choice) return;
                draft.Selection = new(1, choice.Directory, choice.Name, "", choice.Bytes, draft.Selection.Strength);
                draft.Enabled = choice.Name.Length > 0;
                enabled.IsEnabled = draft.Enabled; enabled.IsChecked = draft.Enabled;
                select.ToolTip = choice.Name.Length == 0 ? "LoRAを選択" : Path.Combine(choice.Directory, choice.Name);
                VideoLorasChanged();
            };
            head.Children.Add(select); row.Children.Add(head);
            var amount = new DockPanel { Margin = new Thickness(22, 4, 0, 0) };
            var value = new TextBlock { Text = draft.Selection.Strength.ToString("0.00", CultureInfo.InvariantCulture), Width = 45,
                Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(value, Dock.Right); amount.Children.Add(value);
            var label = new TextBlock { Text = "強度", Width = 38, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left); amount.Children.Add(label);
            var slider = new Slider { Minimum = -2, Maximum = 2, Value = draft.Selection.Strength, TickFrequency = 0.05,
                IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, args) => { draft.Selection = draft.Selection with { Strength = Math.Round(args.NewValue, 2) }; value.Text = draft.Selection.Strength.ToString("0.00", CultureInfo.InvariantCulture); VideoLorasChanged(); };
            amount.Children.Add(slider); row.Children.Add(amount); VideoLoraRows.Children.Add(row);
        }
    }

    private static string? ValidateVideoLoraCapability(JsonElement health)
    {
        if (!health.TryGetProperty("capabilities", out JsonElement capabilities)
            || !HasSingleProperty(capabilities, "videoLoraV1")
            || !capabilities.TryGetProperty("videoLoraV1", out JsonElement lora)
            || !HasExactProperties(lora, "contractId", "protocol", "maximumCount", "maximumBytes", "minimumStrength", "maximumStrength", "workflowRevision")
            || !TryGetExactStringProperty(lora, "contractId", "PV-ENHANCE-VIDEO-LORA-001")
            || !TryGetExactStringProperty(lora, "protocol", "aibos.enhancement-video-lora/v1")
            || !TryGetExactStringProperty(lora, "workflowRevision", VideoLoraSelection.Workflow)
            || !HasExactInt32(lora, "maximumCount", 8)
            || !lora.GetProperty("maximumBytes").TryGetInt64(out long bytes) || bytes != VideoLoraSelection.MaximumBytes
            || !HasExactInt32(lora, "minimumStrength", -2) || !HasExactInt32(lora, "maximumStrength", 2))
            return "動画LoRAに対応したCompanionが必要です。更新後に再起動してください。キューには追加していません。";
        return null;
    }

    public void ConfigureVideoLoraRowForSmoke(int index, bool enabled, double strength, bool moveUp = false)
    {
        var row = (StackPanel)VideoLoraRows.Children[index];
        var head = (DockPanel)row.Children[0];
        var check = head.Children.OfType<CheckBox>().Single();
        check.IsChecked = enabled; check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        ((DockPanel)row.Children[1]).Children.OfType<Slider>().Single().Value = strength;
        if (moveUp) head.Children.OfType<Button>().Single(x => Equals(x.Content, "↑")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    public void ExpandVideoLorasForSmoke() { VideoLoraExpander.IsExpanded = true; VideoLoraExpander.BringIntoView(); UpdateLayout(); }
    public async Task SetVideoLorasForSmoke(string directory, params string[] fileNames)
    {
        _videoLoraDirectory = directory; _videoLoras.Clear();
        foreach (string name in fileNames) _videoLoras.Add(new(new(1, directory, name, "", 0, 1)));
        await RefreshVideoLorasAsync(); VideoLorasChanged();
    }
}
