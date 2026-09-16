using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public sealed partial class VideoPromptAuthoringControl
{
    private int _durationMs = 15083;
    private readonly ComboBox _captureMode = new() { MinHeight = 32, MaxDropDownHeight = 340 };
    private readonly List<(Border Card, TextBlock Title, TextBlock Start, TextBox End, ComboBox[] Selectors)> _phaseControls = [];
    private readonly Button _addPhase = new() { Content = "＋ 区間を追加", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 2, 0, 8) };

    private bool EnsureDirectionProgram()
    {
        if (_program.Enabled) return true;
        string original = _program.UseSourceVariants && _photo ? _program.BaseH3Template : _input.Text;
        string photo = _program.UseSourceVariants && _photo ? _input.Text : _program.PhotorealBaseH3Template;
        if (EscapeLiteral(original).Length > 8000 || EscapeLiteral(photo).Length > 8000)
        { _hint.Text = "本文が長いため演出を追加できません。元の本文は変更していません。"; return false; }
        if (_program.UseSourceVariants && _photo) _program.PhotorealBaseH3Template = _input.Text;
        else _program.BaseH3Template = _input.Text;
        _program.Template = EscapeLiteral(_program.BaseH3Template);
        _program.PhotorealTemplate = EscapeLiteral(_program.PhotorealBaseH3Template);
        _program.Enabled = _program.AnnotatedH3 = true;
        _loading = true;
        try { SetText(_input, _program.TemplateFor(_photo ? "photoreal" : "anime")); }
        finally { _loading = false; }
        return true;
    }

    private void BuildTimelineControls(Panel parent)
    {
        parent.Children.Add(Label("演出の流れ", true));
        parent.Children.Add(Label("区間を追加すると、カメラ・腕・表情・ムードを順に変えられます。最大3区間。", false));
        for (int index = 0; index < 3; index++)
        {
            int slot = index;
            var content = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var title = new TextBlock { Foreground = Ink, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            var start = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
            var end = new TextBox { Width = 65, Foreground = Ink, Background = Paper, BorderBrush = ColorBrush("#526887"), Padding = new Thickness(5, 3, 5, 3), MaxLength = 7 };
            AutomationProperties.SetName(end, $"区間{slot + 1}の終了秒");
            void CommitEnd()
            {
                if (_loading || slot >= _program.DirectionPhases.Count - 1) return;
                int lower = slot == 0 ? 0 : VideoDirectionTimeline.EndMs(_program.DirectionPhases[slot - 1], _durationMs);
                int upper = VideoDirectionTimeline.EndMs(_program.DirectionPhases[slot + 1], _durationMs);
                if (!double.TryParse(end.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out double seconds)
                    || !double.IsFinite(seconds) || seconds * 1000 < lower + 100 || seconds * 1000 > upper - 100)
                { RefreshTimelineControls(); _hint.Text = "終了時刻は前後の区切りの間で指定してください。"; return; }
                _program.DirectionPhases[slot].EndMillionths = (int)Math.Round(seconds * 1000 / _durationMs * 1_000_000);
                RefreshTimelineControls(); Publish(null); Render();
            }
            end.LostKeyboardFocus += (_, _) => CommitEnd();
            end.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitEnd(); e.Handled = true; } };
            if (slot > 0)
            {
                var remove = new Button { Content = "区間を削除", Padding = new Thickness(8, 3, 8, 3), ToolTip = "前の区間へまとめます" };
                remove.SetResourceReference(StyleProperty, "GhostButton");
                DockPanel.SetDock(remove, Dock.Right); header.Children.Add(remove);
                remove.Click += (_, _) =>
                {
                    if (slot >= _program.DirectionPhases.Count) return;
                    _program.DirectionPhases[slot - 1].EndMillionths = _program.DirectionPhases[slot].EndMillionths;
                    _program.DirectionPhases.RemoveAt(slot);
                    if (_program.DirectionPhases.Count == 1)
                    {
                        var remaining = _program.DirectionPhases[0];
                        _program.CameraMotionId = remaining.CameraId; _program.ArmMotionId = remaining.ArmsId;
                        _program.ExpressionId = remaining.ExpressionId; _program.MoodId = remaining.MoodId;
                        _program.DirectionPhases.Clear();
                    }
                    RefreshTimelineControls(); Publish(null); Render();
                };
            }
            var time = new StackPanel { Orientation = Orientation.Horizontal };
            time.Children.Add(start); time.Children.Add(end);
            time.Children.Add(new TextBlock { Text = " 秒", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(title); header.Children.Add(time); content.Children.Add(header);
            var grid = new UniformGrid { Columns = 2 };
            ComboBox[] selectors = slot == 0
                ? [new() { MinHeight = 32, MaxDropDownHeight = 340 }, _armMotion, _expression, _mood]
                : Enumerable.Range(0, 4).Select(_ => new ComboBox { MinHeight = 32, MaxDropDownHeight = 340 }).ToArray();
            string[] titles = ["カメラワーク", "腕・手の動き", "表情", "ムード"];
            string[] colors = ["#6EE7D0", "#93C5FD", "#F0ABFC", "#FDE68A"];
            for (int a = 0; a < 4; a++)
            {
                string aspect = VideoDirectionTimeline.Aspects[a];
                AddActingChoice(grid, selectors[a], titles[a], "この区間", colors[a], VideoDirectionTimeline.Choices(aspect), value =>
                {
                    if (_program.DirectionPhases.Count > 0) _program.DirectionPhases[slot].Set(aspect, value);
                    else switch (aspect)
                    { case "camera": _program.CameraMotionId = value; break; case "arms": _program.ArmMotionId = value; break;
                      case "expression": _program.ExpressionId = value; break; case "mood": _program.MoodId = value; break; }
                });
            }
            content.Children.Add(grid);
            var card = new Border { Child = content, Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 3),
                Background = Paper, BorderBrush = ColorBrush("#3F5474"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            _phaseControls.Add((card, title, start, end, selectors)); parent.Children.Add(card);
        }
        _addPhase.SetResourceReference(StyleProperty, "GhostButton");
        _addPhase.Click += (_, _) => AddTimelinePhase();
        parent.Children.Add(_addPhase);
        RefreshTimelineControls();
    }

    private void AddTimelinePhase()
    {
        if (_program.DirectionPhases.Count >= 3 || !EnsureDirectionProgram()) return;
        if (_program.DirectionPhases.Count == 0) _program.DirectionPhases.Add(VideoDirectionTimeline.FromGlobal(_program));
        int last = _program.DirectionPhases.Count - 1;
        int start = last == 0 ? 0 : _program.DirectionPhases[last - 1].EndMillionths;
        var next = _program.DirectionPhases[last].Copy();
        _program.DirectionPhases[last].EndMillionths = start + (1_000_000 - start) / 2;
        _program.DirectionPhases.Add(next);
        RefreshTimelineControls(); Publish(null); Render();
    }

    private void RefreshTimelineControls()
    {
        bool previous = _loading; _loading = true;
        try
        {
            int count = Math.Max(1, _program.DirectionPhases.Count), start = 0;
            for (int i = 0; i < _phaseControls.Count; i++)
            {
                var c = _phaseControls[i]; c.Card.Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
                if (i >= count) continue;
                var phase = _program.DirectionPhases.Count == 0 ? VideoDirectionTimeline.FromGlobal(_program) : _program.DirectionPhases[i];
                int end = VideoDirectionTimeline.EndMs(phase, _durationMs);
                c.Title.Text = count == 1 ? "動画全体" : i == 0 ? "冒頭" : i == count - 1 ? "終盤" : "中盤";
                c.Start.Text = (start / 1000d).ToString("0.##", CultureInfo.CurrentCulture) + " ～ ";
                c.End.Text = (end / 1000d).ToString("0.##", CultureInfo.CurrentCulture);
                c.End.IsReadOnly = i == count - 1;
                c.End.ToolTip = i == count - 1 ? "動画の終わりまで。長さに合わせて変わります。" : "区間の終了秒。Enterで確定します。";
                for (int a = 0; a < 4; a++) c.Selectors[a].SelectedValue = phase.For(VideoDirectionTimeline.Aspects[a]);
                start = end;
            }
            _addPhase.Visibility = count < 3 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loading = previous; }
    }

    public void AddTimelinePhaseForSmoke() => AddTimelinePhase();
    public void RevealTimelineForSmoke() => _phaseControls[0].Card.BringIntoView();
    public int TimelinePhaseCountForSmoke => Math.Max(1, _program.DirectionPhases.Count);
    public void SelectTimelineForSmoke(int phase, string aspect, string id)
        => _phaseControls[phase].Selectors[Array.IndexOf(VideoDirectionTimeline.Aspects, aspect)].SelectedValue = id;
}
