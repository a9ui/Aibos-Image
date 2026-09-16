using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    public void VerifyVideoMenuScrollingForSmoke(Dictionary<string, bool> checks, Action<string, FrameworkElement> capture)
    {
        double width = Width, height = Height, minimumWidth = MinWidth, minimumHeight = MinHeight;
        var editor = (VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content;
        var outer = ModalVideoGenerationScrollViewer;
        void Layout() { UpdateLayout(); UpdateVideoGenerationBoardLayout(ModalVideoGenerationPopup.ActualWidth, ModalVideoGenerationPopup.ActualHeight); UpdateLayout(); }
        void Wheel(UIElement source, int delta)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            source.RaiseEvent(args);
            if (!args.Handled) source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent });
            UpdateLayout();
        }
        bool Fits()
        {
            Point origin = ModalVideoGenerationBoardBorder.TranslatePoint(new Point(), ModalVideoGenerationPopup);
            return origin.X >= 0 && origin.Y >= 0
                && origin.X + ModalVideoGenerationBoardBorder.ActualWidth <= ModalVideoGenerationPopup.ActualWidth + 1
                && origin.Y + ModalVideoGenerationBoardBorder.ActualHeight <= ModalVideoGenerationPopup.ActualHeight + 1;
        }
        try
        {
            Width = 1280; Height = 1020;
            string longPrompt = MiniMaxH3I2vaPromptConformance.Opening + MiniMaxH3I2vaPromptConformance.IntegratedPrefix
                + string.Join("\n\n", Enumerable.Repeat("The camera follows the subject walking through the garden. The subject waves and looks toward the camera.", 35))
                + MiniMaxH3I2vaPromptConformance.SoundscapePrefix + "Footsteps and birdsong."
                + MiniMaxH3I2vaPromptConformance.MusicPrefix + "N/A";
            editor.Load(new(), longPrompt, "auto", "anime", null);
            FrameworkElement reading = editor.ReadingSurfaceForSmoke;
            Layout(); outer.ScrollToTop(); UpdateLayout();
            checks["videoMenuUsesAvailableWidthAndHeight"] = ModalVideoGenerationBoardBorder.ActualWidth > 800
                && ModalVideoGenerationBoardBorder.ActualHeight > 680 && Fits();
            checks["videoMenuExtendsToWindowBottom"] = Math.Abs(ModalVideoGenerationBoardBorder.ActualHeight
                + ModalVideoGenerationBoardBorder.Margin.Top + ModalVideoGenerationBoardBorder.Margin.Bottom
                - ModalVideoGenerationPopup.ActualHeight) <= 1;
            checks["longPromptHasOneReadingScrollSurface"] = reading.ActualHeight > 360 && outer.ScrollableHeight > 0;
            Wheel(reading, -120);
            checks["wheelOverReadingScrollsMenu"] = outer.VerticalOffset > 0;
            capture("video-menu-wide", ModalVideoGenerationBoardBorder);
            int styleIndex = ModalVideoStyleComboBox.SelectedIndex;
            double before = outer.VerticalOffset;
            Wheel(ModalVideoStyleComboBox, -120);
            checks["wheelDoesNotChangeClosedStyleSelector"] = styleIndex == ModalVideoStyleComboBox.SelectedIndex && outer.VerticalOffset > before;

            MinWidth = 640; MinHeight = 480; Width = 680; Height = 600;
            Layout();
            checks["videoMenuFitsSmallWindow"] = ModalVideoGenerationBoardBorder.ActualWidth < 680 && Fits();
            checks["longPromptFooterStaysVisible"] = VideoSubmissionFooterFixedForSmoke();
            capture("video-menu-small", ModalVideoGenerationBoardBorder);
            ScrollViewer inner = editor.EditingScrollForSmoke;
            Layout(); outer.ScrollToVerticalOffset(150); inner.ScrollToVerticalOffset(100); UpdateLayout();
            before = outer.VerticalOffset;
            double innerBefore = inner.VerticalOffset;
            Wheel(inner, -120);
            checks["wheelInsideEditorScrollsEditorFirst"] = inner.VerticalOffset > innerBefore && Math.Abs(outer.VerticalOffset - before) < 1;
            inner.ScrollToBottom(); outer.ScrollToTop(); UpdateLayout();
            Wheel(inner, -120);
            checks["editorBottomHandsWheelToMenu"] = outer.VerticalOffset > 0;
            inner.ScrollToTop(); outer.ScrollToVerticalOffset(150); UpdateLayout();
            before = outer.VerticalOffset;
            Wheel(inner, 120);
            checks["editorTopHandsWheelToMenu"] = outer.VerticalOffset < before;
            outer.ScrollToBottom(); inner.ScrollToBottom(); UpdateLayout();
            var boundary = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            inner.RaiseEvent(boundary);
            checks["menuBoundaryKeepsWheelOutOfViewer"] = boundary.Handled;
        }
        finally
        {
            _ = editor.ReadingSurfaceForSmoke;
            Width = width; Height = height; MinWidth = minimumWidth; MinHeight = minimumHeight;
            SyncVideoGenerationSettingsForSmoke(); Layout(); outer.ScrollToTop(); UpdateLayout();
        }
    }
}
