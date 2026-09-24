using System.Windows;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    internal bool VerifyModalVideoSeekLifecycleForSmoke()
    {
        if (!_modalShowingVideo || _modalVideoDurationSeconds <= 0)
            return false;

        double duration = _modalVideoDurationSeconds;
        double first = duration / 4;
        double second = duration / 2;
        double third = duration * 3 / 4;
        void BeginDrag() => ModalVideoSeekSlider.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        bool At(double seconds) => Math.Abs(ModalVideoSeekSlider.Value - seconds) < 0.001;

        try
        {
            ResetModalVideoTimeline(duration, show: true);
            UpdateModalVideoTimelineFromPlayback(TimeSpan.FromSeconds(first));
            BeginDrag();
            UpdateModalVideoTimelineFromPlayback(TimeSpan.FromSeconds(second));
            bool heldDuringDrag = At(first);

            // Routed events exercise the XAML handlers without capturing the
            // user's mouse. Playback uses the same update gate as the timer.
            ModalVideoSeekSlider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.LostMouseCaptureEvent,
            });
            UpdateModalVideoTimelineFromPlayback(TimeSpan.FromSeconds(second));
            bool resumedAfterCaptureLoss = At(second);

            BeginDrag();
            ResetModalVideoTimeline(duration, show: false);
            ResetModalVideoTimeline(duration, show: true);
            UpdateModalVideoTimelineFromPlayback(TimeSpan.FromSeconds(third));
            bool resumedAfterNavigation = At(third);

            BeginDrag();
            ModalVideoSeekSlider.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            });
            UpdateModalVideoTimelineFromPlayback(TimeSpan.FromSeconds(first));
            return heldDuringDrag && resumedAfterCaptureLoss
                && resumedAfterNavigation && At(first);
        }
        finally
        {
            ResetModalVideoTimeline(duration, show: true);
            SeekModalVideoToSeconds(0);
        }
    }
}
