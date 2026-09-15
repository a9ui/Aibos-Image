using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

public partial class App
{
    private static bool VerifyH3PreparationPresentation(
        MainWindow window,
        string resultDirectory)
    {
        static EnhancementWorkspaceJobView Create(
            int progress = 1,
            string status = "running",
            bool cancelRequested = false,
            bool videoMutationSafe = true,
            string adapterId = "minimax-h3-local-v1") => new(
                id: "preparation-fixture",
                sourceId: "preparation-source",
                sourcePath: Path.Combine(Path.GetTempPath(), "preparation-fixture.png"),
                sourceProducerJobId: null,
                sourceVideoJobId: null,
                presetId: "minimax-h3-i2v-preview-v1",
                adapterId: adapterId,
                operation: "video",
                photorealMutationSafe: false,
                videoMutationSafe: videoMutationSafe,
                queueReorderSafe: true,
                i2iMutationSafe: false,
                i2iSchemaVersion: null,
                i2iTarget: null,
                i2iInstructionSummary: null,
                i2iV2EnvelopeClaimed: false,
                status: status,
                cancelRequested: cancelRequested,
                progress: progress,
                outputPath: null,
                errorMessage: null,
                createdAt: DateTimeOffset.UnixEpoch,
                updatedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
                startedAt: DateTimeOffset.UnixEpoch,
                finishedAt: null,
                sourceSize: null,
                sourceMtimeMs: null,
                queueOrder: 0,
                apiOrdinal: 0,
                requestDetailsText: "");

        static T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) return match;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                if (Find<T>(VisualTreeHelper.GetChild(root, index)) is { } child)
                    return child;
            return null;
        }

        var view = Create();
        var notifications = new HashSet<string>();
        view.PropertyChanged += (_, change) => notifications.Add(change.PropertyName ?? "");
        if (window.FindName("EnhancementJobsList") is not ListBox list
            || list.ItemTemplate.LoadContent() is not FrameworkElement row)
            return false;
        row.DataContext = view;
        row.Width = 760;
        row.Measure(new Size(760, double.PositiveInfinity));
        row.Arrange(new Rect(new Point(), row.DesiredSize));
        row.UpdateLayout();
        ProgressBar? bar = Find<ProgressBar>(row);
        bool preparing = view.StatusLabel == "動画生成の準備中"
            && !view.StatusLabel.Contains('%')
            && view.ShowDetailText
            && view.Progress == 1
            && bar is { IsIndeterminate: true, Visibility: Visibility.Visible };

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(row.ActualWidth),
            (int)Math.Ceiling(row.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(row), null,
                new Rect(0, 0, row.ActualWidth, row.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(resultDirectory, "h3-preparation-row.png")))
            encoder.Save(stream);

        // The lightweight health path must update the already-bound row;
        // replacing the view or reopening Jobs must not be necessary.
        view.ApplyHealthProgress(5, DateTimeOffset.UnixEpoch.AddMinutes(2));
        row.UpdateLayout();
        bool healthTransition = bar is { IsIndeterminate: false, Value: 5 }
            && view.StatusLabel == "処理中 5%"
            && !view.ShowDetailText
            && notifications.Contains(nameof(view.IsProgressIndeterminate))
            && notifications.Contains(nameof(view.ShowDetailText));
        view.RefreshFrom(Create(progress: 1));
        row.UpdateLayout();
        bool inventoryTransition = bar is { IsIndeterminate: true };
        view.RefreshFrom(Create(cancelRequested: true));
        row.UpdateLayout();
        bool cancellationPrecedence = bar is { IsIndeterminate: false }
            && view.StatusLabel.StartsWith("中止処理中", StringComparison.Ordinal);
        view.RefreshFrom(Create(progress: 100, status: "succeeded"));
        row.UpdateLayout();
        bool terminalTransition = bar?.Visibility == Visibility.Collapsed
            && view.StatusLabel == "完了";

        return preparing && healthTransition && inventoryTransition
            && cancellationPrecedence && terminalTransition
            && !Create(status: "queued").IsProgressIndeterminate
            && !Create(status: "failed").IsProgressIndeterminate
            && !Create(videoMutationSafe: false).IsProgressIndeterminate
            && !Create(adapterId: "future-video-adapter").IsProgressIndeterminate;
    }
}
