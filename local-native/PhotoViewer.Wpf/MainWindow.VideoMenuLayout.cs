using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private void VideoGenerationPopup_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateVideoGenerationBoardLayout(e.NewSize.Width, e.NewSize.Height);

    private void UpdateVideoGenerationBoardLayout(double width, double height)
    {
        if (ModalVideoGenerationBoardBorder is null || width <= 0 || height <= 0) return;
        Thickness margin = ModalVideoGenerationBoardBorder.Margin;
        ModalVideoGenerationBoardBorder.Width = Math.Min(860, Math.Max(1, width - margin.Left - margin.Right));
        ModalVideoGenerationBoardBorder.Height = Math.Max(1, height - margin.Top - margin.Bottom);
        ModalVideoGenerationBoardBorder.MaxHeight = ModalVideoGenerationBoardBorder.Height;
    }

    private void VideoGenerationBoard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        ScrollViewer? nested = null;
        for (DependencyObject? current = e.OriginalSource as DependencyObject;
             current is not null && !ReferenceEquals(current, ModalVideoGenerationBoardBorder);
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            // Open choices own their scrolling; closed selectors must not change
            // the selected style, duration, or source kind under the pointer.
            if (current is ComboBox { IsDropDownOpen: true } or System.Windows.Controls.ContextMenu) return;
            if (nested is null && current is ScrollViewer scroll && CanScrollVideoMenu(scroll, e.Delta)) nested = scroll;
        }
        ScrollViewer target = nested ?? ModalVideoGenerationScrollViewer;
        int lines = SystemParameters.WheelScrollLines;
        double distance = lines < 0 ? target.ViewportHeight : lines * 20;
        target.ScrollToVerticalOffset(target.VerticalOffset - e.Delta / 120d * distance);
        // At an editor boundary the same wheel gesture reaches the outer menu.
        // At the menu boundary it stays here instead of reaching the image viewer.
        e.Handled = true;
    }

    private static bool CanScrollVideoMenu(ScrollViewer scroll, int delta)
        => delta > 0 ? scroll.VerticalOffset > 0.5
            : scroll.VerticalOffset < scroll.ScrollableHeight - 0.5;
}
