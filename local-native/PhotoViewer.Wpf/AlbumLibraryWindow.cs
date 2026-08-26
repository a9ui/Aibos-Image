using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace PhotoViewer.Wpf;

internal sealed class AlbumLibraryWindow : Window
{
    private static readonly HashSet<string> AlbumImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".avif", ".gif",
    };

    private readonly string _storePath = AlbumStore.ResolvePath();
    private readonly IReadOnlyList<string> _selectedPaths;
    private readonly HashSet<string> _catalogPaths;
    private readonly Func<AlbumEntry?, Task> _activateAlbum;
    private readonly Func<Task> _libraryChanged;
    private readonly SemaphoreSlim _memberAvailabilityGate = new(1, 1);
    private readonly List<Button> _actionButtons = [];
    private readonly ListBox _albumList = new() { MinHeight = 220 };
    private readonly ListBox _memberList = new() { MinHeight = 220, SelectionMode = SelectionMode.Extended };
    private readonly TextBox _name = new() { MinWidth = 220, MaxLength = AlbumStore.MaxNameLength };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _albumSummary = new();
    private readonly TextBlock _headerSubtitle = new();
    private readonly TextBlock _statusLabel = new() { Text = "ALBUM STORE" };
    private readonly TextBlock _memberHeading = new() { Text = "Members" };
    private readonly TextBlock _memberSummary = new() { Text = "Albumを選ぶと画像を確認できます。" };
    private readonly TextBlock _albumEmpty = new() { Text = "Albumはまだありません。\n名前を入力して作成してください。" };
    private readonly TextBlock _memberEmpty = new() { Text = "Albumを選ぶと画像を確認できます。" };
    private AlbumDocumentSnapshot? _document;
    private bool _isBusy;
    private bool _closed;
    private int _operationGeneration;
    private int _reloadGeneration;
    private int _memberGeneration;
    private CancellationTokenSource? _memberAvailabilityCts;
    private int _memberAvailabilityPendingCount;
    private int _memberAvailabilityActiveCount;
    private int _memberAvailabilityMaxConcurrentCount;
    private int _memberAvailabilityCanceledCount;
    private int _memberAvailabilityApplyCount;
    private int _memberAvailabilityStartedCount;
    private int _memberAvailabilityDelayForSmokeMs;
    private int _mutationInvocationCount;
    private bool _suppressMemberRefresh;
    private bool _lastReadOffDispatcher;
    private bool _lastMutationOffDispatcher;
    private bool _lastAvailabilityOffDispatcher;
    private AlbumMutationStatus? _lastMutationStatus;

    internal AlbumLibraryWindow(
        Window owner,
        IReadOnlyList<string> selectedPaths,
        IEnumerable<string> catalogPaths,
        Func<AlbumEntry?, Task> activateAlbum,
        Func<Task> libraryChanged)
    {
        Owner = owner;
        _selectedPaths = selectedPaths.ToArray();
        _catalogPaths = catalogPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _activateAlbum = activateAlbum;
        _libraryChanged = libraryChanged;
        Title = "Albums · Aibos";
        Width = 980;
        Height = 700;
        MinWidth = 780;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "BgPrimary");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(6),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(7),
            UseAeroCaptionButtons = false,
        });
        Content = BuildContent();
        _albumList.SelectionChanged += async (_, _) =>
        {
            if (!_suppressMemberRefresh)
                await RefreshMembersAsync();
        };
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            Interlocked.Increment(ref _operationGeneration);
            Interlocked.Increment(ref _reloadGeneration);
            Interlocked.Increment(ref _memberGeneration);
            Interlocked.Exchange(ref _memberAvailabilityCts, null)?.Cancel();
        };
    }

    private UIElement BuildContent()
    {
        ConfigureList(_albumList, BuildAlbumItemTemplate());
        ConfigureList(_memberList, BuildMemberItemTemplate());
        AutomationProperties.SetName(_albumList, "Albums");
        AutomationProperties.SetHelpText(_albumList, "ピン留めと最近使った順に並ぶAlbum一覧");
        AutomationProperties.SetName(_memberList, "Albumの画像");
        AutomationProperties.SetHelpText(_memberList, "選択中Albumの画像と現在の利用状態");
        ConfigureEmptyState(_albumEmpty);
        ConfigureEmptyState(_memberEmpty);
        _albumEmpty.Visibility = Visibility.Collapsed;

        var frameGrid = new Grid();
        frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frameGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border header = BuildHeader();
        Grid.SetRow(header, 0);
        frameGrid.Children.Add(header);

        var body = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(BuildCreateCard());

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.44, GridUnitType.Star), MinWidth = 300 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.56, GridUnitType.Star), MinWidth = 330 });
        Grid.SetRow(columns, 2);
        body.Children.Add(columns);

        Border albums = BuildAlbumPanel();
        Grid.SetColumn(albums, 0);
        columns.Children.Add(albums);

        Border members = BuildMemberPanel();
        Grid.SetColumn(members, 2);
        columns.Children.Add(members);

        Grid.SetRow(body, 1);
        frameGrid.Children.Add(body);

        Border status = BuildStatusSurface();
        Grid.SetRow(status, 2);
        frameGrid.Children.Add(status);

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = frameGrid,
            ClipToBounds = true,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "BgPrimary");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassBorderHover");
        return frame;
    }

    private Border BuildHeader()
    {
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "Albums", FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _headerSubtitle.Text = "画像をまとめ、Galleryへすぐ呼び出せます";
        _headerSubtitle.FontSize = 11;
        _headerSubtitle.Margin = new Thickness(0, 2, 0, 0);
        _headerSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        titleStack.Children.Add(title);
        titleStack.Children.Add(_headerSubtitle);
        titleStack.MouseLeftButtonDown += DragHeader_MouseLeftButtonDown;
        Grid.SetColumn(titleStack, 0);
        headerGrid.Children.Add(titleStack);

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerActions.Children.Add(BuildHeaderChip("SHARED STATE"));
        headerActions.Children.Add(BuildHeaderChip($"{_selectedPaths.Count:N0} SELECTED"));
        var closeGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,2 L12,12 M12,2 L2,12"),
            StrokeThickness = 1.4,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
        };
        closeGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
        var close = new Button
        {
            Content = closeGlyph,
            ToolTip = "閉じる",
            Margin = new Thickness(8, 0, 0, 0),
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "CloseButton");
        AutomationProperties.SetName(close, "Albumsを閉じる");
        AutomationProperties.SetHelpText(close, "共有状態を変更せずAlbumsを閉じます");
        close.Click += (_, _) => Close();
        headerActions.Children.Add(close);
        Grid.SetColumn(headerActions, 1);
        headerGrid.Children.Add(headerActions);

        var header = new Border
        {
            Padding = new Thickness(14, 10, 10, 10),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };
        header.SetResourceReference(Border.BackgroundProperty, "HeaderBg");
        header.SetResourceReference(Border.BorderBrushProperty, "GlassBorder");
        return header;
    }

    private Border BuildHeaderChip(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 7, 0),
            BorderThickness = new Thickness(1),
            Child = label,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        chip.SetResourceReference(Border.BorderBrushProperty, "AccentGlass");
        return chip;
    }

    private Border BuildCreateCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var label = SectionLabel("ALBUM NAME", "新しいAlbumの作成、または選択中Albumの名前変更");
        inputStack.Children.Add(label);
        _name.Height = 32;
        _name.Padding = new Thickness(10, 0, 10, 0);
        _name.BorderThickness = new Thickness(0);
        _name.Background = Brushes.Transparent;
        _name.VerticalContentAlignment = VerticalAlignment.Center;
        _name.FontSize = 13;
        _name.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _name.SetResourceReference(TextBox.CaretBrushProperty, "AccentLight");
        AutomationProperties.SetName(_name, "Album名");
        AutomationProperties.SetHelpText(_name, "新しいAlbum名、または選択中Albumの変更後の名前を入力します");
        var inputBorder = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = _name,
            Style = BuildInputBorderStyle(),
        };
        inputStack.Children.Add(inputBorder);
        grid.Children.Add(inputStack);

        Button create = ActionButton("作成", "入力した名前でAlbumを作成します", async (_, _) => await CreateAlbumAsync(), "PrimaryButton");
        create.Height = 32;
        create.MinWidth = 90;
        create.Margin = new Thickness(0, 20, 8, 0);
        Grid.SetColumn(create, 1);
        grid.Children.Add(create);

        Button refresh = ActionButton("更新", "Albumsの最新状態を読み込みます", async (_, _) => await ReloadAsync());
        refresh.Height = 32;
        refresh.MinWidth = 82;
        refresh.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetColumn(refresh, 2);
        grid.Children.Add(refresh);

        var card = PanelCard(grid, new Thickness(16, 14, 16, 14));
        card.SetResourceReference(Border.BackgroundProperty, "BgSecondary");
        return card;
    }

    private Border BuildAlbumPanel()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _albumSummary.Text = "Albumsを読み込み中…";
        var sectionHeader = SectionHeading("Library", _albumSummary);
        layout.Children.Add(sectionHeader);

        var listSurface = ListSurface(_albumList, _albumEmpty);
        Grid.SetRow(listSurface, 2);
        layout.Children.Add(listSurface);

        var actions = new StackPanel { Margin = new Thickness(0, 13, 0, 0) };
        var primary = new WrapPanel();
        Button open = ActionButton("Galleryで開く", "選択中のAlbumをGalleryで開きます", async (_, _) => await OpenAlbumAsync(), "PrimaryButton");
        open.Height = 32;
        open.MinWidth = 125;
        primary.Children.Add(open);
        primary.Children.Add(ActionButton("選択画像を追加", "Galleryで選択中の対応画像をこのAlbumへ追加します", async (_, _) => await AddSelectionAsync()));
        actions.Children.Add(primary);

        var management = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        management.Children.Add(ActionButton("名前を変更", "選択中Albumの名前を変更します", async (_, _) => await RenameAlbumAsync()));
        management.Children.Add(ActionButton("ピン留め / 解除", "選択中Albumのピン留めを切り替えます", async (_, _) => await TogglePinAsync()));
        management.Children.Add(ActionButton("Catalogへ戻る", "Galleryを現在のCatalogへ戻します", async (_, _) =>
        {
            await _activateAlbum(null);
            _status.Text = "Catalogへ戻りました。";
        }));
        management.Children.Add(ActionButton("Albumを削除", "Albumだけを削除します。元画像は削除されません。", async (_, _) => await DeleteAlbumAsync(), "DangerButton"));
        actions.Children.Add(management);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);
        return PanelCard(layout, new Thickness(16));
    }

    private Border BuildMemberPanel()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(SectionHeading(_memberHeading, _memberSummary));

        var listSurface = ListSurface(_memberList, _memberEmpty);
        Grid.SetRow(listSurface, 2);
        layout.Children.Add(listSurface);

        var actions = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
        actions.Children.Add(ActionButton("Coverに設定", "選択した画像をAlbumのCoverに設定します", async (_, _) => await SetCoverAsync()));
        actions.Children.Add(ActionButton("Albumから外す", "選択した画像をAlbumから外します。元画像は削除されません。", async (_, _) => await RemoveMembersAsync(), "DangerButton"));
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);
        return PanelCard(layout, new Thickness(16));
    }

    private Border BuildStatusSurface()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _status.FontSize = 11.5;
        _status.VerticalAlignment = VerticalAlignment.Center;
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(999), Margin = new Thickness(0, 0, 8, 0) };
        dot.SetResourceReference(Border.BackgroundProperty, "AccentLight");
        statusGrid.Children.Add(dot);
        _statusLabel.FontSize = 9.5;
        _statusLabel.FontWeight = FontWeights.SemiBold;
        _statusLabel.Margin = new Thickness(0, 0, 12, 0);
        _statusLabel.VerticalAlignment = VerticalAlignment.Center;
        _statusLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        Grid.SetColumn(_statusLabel, 1);
        statusGrid.Children.Add(_statusLabel);
        Grid.SetColumn(_status, 2);
        statusGrid.Children.Add(_status);

        var surface = new Border
        {
            Padding = new Thickness(22, 11, 22, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = statusGrid,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "HeaderBg");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassBorder");
        return surface;
    }

    private static Border PanelCard(UIElement child, Thickness padding)
    {
        var card = new Border
        {
            Padding = padding,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = child,
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgSecondary");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassBorder");
        return card;
    }

    private static Border ListSurface(ListBox list, TextBlock empty)
    {
        var grid = new Grid();
        grid.Children.Add(list);
        grid.Children.Add(empty);
        var surface = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = grid,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "BgPrimary");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassBorder");
        return surface;
    }

    private static StackPanel SectionHeading(string title, TextBlock summary)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return SectionHeading(heading, summary);
    }

    private static StackPanel SectionHeading(TextBlock heading, TextBlock summary)
    {
        heading.FontSize = 16;
        heading.FontWeight = FontWeights.SemiBold;
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        summary.FontSize = 10.5;
        summary.Margin = new Thickness(0, 3, 0, 0);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.Add(summary);
        return stack;
    }

    private static StackPanel SectionLabel(string title, string subtitle)
    {
        var titleText = new TextBlock { Text = title, FontSize = 9.5, FontWeight = FontWeights.SemiBold };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        var subtitleText = new TextBlock { Text = subtitle, FontSize = 10.5, Margin = new Thickness(8, 0, 0, 0) };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 0, 0, 7) };
        row.Children.Add(titleText);
        row.Children.Add(subtitleText);
        return row;
    }

    private static void ConfigureEmptyState(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        text.TextWrapping = TextWrapping.Wrap;
        text.LineHeight = 19;
        text.FontSize = 11.5;
        text.Margin = new Thickness(20);
        text.IsHitTestVisible = false;
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
    }

    private static void ConfigureList(ListBox list, DataTemplate template)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        list.Padding = new Thickness(0);
        list.ItemTemplate = template;
        list.ItemContainerStyle = BuildListItemStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
    }

    private static Style BuildListItemStyle()
    {
        var card = new FrameworkElementFactory(typeof(Border), "Card");
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        card.SetValue(Border.PaddingProperty, new Thickness(10));
        card.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 7));
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.BackgroundProperty, ResourceBrush("BgTertiary"));
        card.SetValue(Border.BorderBrushProperty, ResourceBrush("GlassBorder"));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
        });
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding(nameof(ContentControl.ContentTemplate))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
        });
        card.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = card };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("BgElevated"), "Card"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("GlassBorderHover"), "Card"));
        template.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("AccentSoft"), "Card"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("AccentLight"), "Card"));
        selected.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "Card"));
        template.Triggers.Add(selected);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush("TextPrimary")));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        if (Application.Current?.TryFindResource("CommonButtonFocusVisual") is Style focusStyle)
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, focusStyle));
        return style;
    }

    private static Style BuildInputBorderStyle()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("BgTertiary")));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("GlassBorder")));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("BgElevated")));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("GlassBorderHover")));
        style.Triggers.Add(hover);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("BgElevated")));
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("AccentLight")));
        style.Triggers.Add(focused);
        return style;
    }

    private static DataTemplate BuildAlbumItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(ToolTipService.ToolTipProperty, new Binding(nameof(AlbumListItem.ToolTip)));

        var pin = new FrameworkElementFactory(typeof(Border));
        pin.SetValue(DockPanel.DockProperty, Dock.Right);
        pin.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        pin.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        pin.SetValue(Border.MarginProperty, new Thickness(8, 0, 0, 0));
        pin.SetValue(Border.BackgroundProperty, ResourceBrush("AccentSoft"));
        pin.SetValue(Border.BorderBrushProperty, ResourceBrush("AccentGlass"));
        pin.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        pin.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(AlbumListItem.PinnedVisibility)));
        var pinText = new FrameworkElementFactory(typeof(TextBlock));
        pinText.SetValue(TextBlock.TextProperty, "PINNED");
        pinText.SetValue(TextBlock.FontSizeProperty, 8.5);
        pinText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        pinText.SetValue(TextBlock.ForegroundProperty, ResourceBrush("AccentLight"));
        pin.AppendChild(pinText);
        root.AppendChild(pin);

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(DockPanel.DockProperty, Dock.Left);
        badge.SetValue(FrameworkElement.WidthProperty, 40.0);
        badge.SetValue(FrameworkElement.HeightProperty, 40.0);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        badge.SetValue(Border.MarginProperty, new Thickness(0, 0, 11, 0));
        badge.SetValue(Border.BackgroundProperty, ResourceBrush("AccentGlass"));
        badge.SetValue(Border.BorderBrushProperty, ResourceBrush("AccentLight"));
        badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var badgeText = new FrameworkElementFactory(typeof(TextBlock));
        badgeText.SetValue(TextBlock.TextProperty, "AL");
        badgeText.SetValue(TextBlock.FontSizeProperty, 10.0);
        badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        badgeText.SetValue(TextBlock.ForegroundProperty, ResourceBrush("AccentLight"));
        badgeText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        badgeText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.AppendChild(badgeText);
        root.AppendChild(badge);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumListItem.Name)));
        name.SetValue(TextBlock.FontSizeProperty, 13.0);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(TextBlock.ForegroundProperty, ResourceBrush("TextPrimary"));
        text.AppendChild(name);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumListItem.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, 10.0);
        detail.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        detail.SetValue(TextBlock.ForegroundProperty, ResourceBrush("TextSecondary"));
        text.AppendChild(detail);
        root.AppendChild(text);
        return new DataTemplate(typeof(AlbumListItem)) { VisualTree = root };
    }

    private static DataTemplate BuildMemberItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(ToolTipService.ToolTipProperty, new Binding(nameof(AlbumMemberListItem.Path)));

        var status = new FrameworkElementFactory(typeof(Border), "StatusBadge");
        status.SetValue(DockPanel.DockProperty, Dock.Right);
        status.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        status.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        status.SetValue(Border.MarginProperty, new Thickness(8, 0, 0, 0));
        status.SetValue(Border.BackgroundProperty, ResourceBrush("AccentSoft"));
        var statusText = new FrameworkElementFactory(typeof(TextBlock), "StatusText");
        statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumMemberListItem.AvailabilityDisplay)));
        statusText.SetValue(TextBlock.FontSizeProperty, 8.5);
        statusText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        statusText.SetValue(TextBlock.ForegroundProperty, ResourceBrush("AccentLight"));
        status.AppendChild(statusText);
        root.AppendChild(status);

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(DockPanel.DockProperty, Dock.Left);
        badge.SetValue(FrameworkElement.WidthProperty, 38.0);
        badge.SetValue(FrameworkElement.HeightProperty, 38.0);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        badge.SetValue(Border.MarginProperty, new Thickness(0, 0, 11, 0));
        badge.SetValue(Border.BackgroundProperty, ResourceBrush("SoftFill"));
        badge.SetValue(Border.BorderBrushProperty, ResourceBrush("GlassBorder"));
        badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var extension = new FrameworkElementFactory(typeof(TextBlock));
        extension.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumMemberListItem.ExtensionLabel)));
        extension.SetValue(TextBlock.FontSizeProperty, 8.5);
        extension.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        extension.SetValue(TextBlock.ForegroundProperty, ResourceBrush("TextSecondary"));
        extension.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        extension.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.AppendChild(extension);
        root.AppendChild(badge);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumMemberListItem.FileName)));
        name.SetValue(TextBlock.FontSizeProperty, 12.0);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(TextBlock.ForegroundProperty, ResourceBrush("TextPrimary"));
        text.AppendChild(name);

        var detailRow = new FrameworkElementFactory(typeof(StackPanel));
        detailRow.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        detailRow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        var cover = new FrameworkElementFactory(typeof(TextBlock));
        cover.SetValue(TextBlock.TextProperty, "COVER  ·  ");
        cover.SetValue(TextBlock.FontSizeProperty, 9.0);
        cover.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        cover.SetValue(TextBlock.ForegroundProperty, ResourceBrush("AccentLight"));
        cover.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(AlbumMemberListItem.CoverVisibility)));
        detailRow.AppendChild(cover);
        var path = new FrameworkElementFactory(typeof(TextBlock));
        path.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlbumMemberListItem.Path)));
        path.SetValue(TextBlock.FontSizeProperty, 9.0);
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        path.SetValue(TextBlock.ForegroundProperty, ResourceBrush("TextTertiary"));
        if (Application.Current?.TryFindResource("MonoFont") is FontFamily mono)
            path.SetValue(TextBlock.FontFamilyProperty, mono);
        detailRow.AppendChild(path);
        text.AppendChild(detailRow);
        root.AppendChild(text);

        var template = new DataTemplate(typeof(AlbumMemberListItem)) { VisualTree = root };
        var current = new DataTrigger { Binding = new Binding(nameof(AlbumMemberListItem.Availability)), Value = "current" };
        current.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("SuccessSoft"), "StatusBadge"));
        current.Setters.Add(new Setter(TextBlock.ForegroundProperty, ResourceBrush("Success"), "StatusText"));
        template.Triggers.Add(current);
        var missing = new DataTrigger { Binding = new Binding(nameof(AlbumMemberListItem.Availability)), Value = "missing" };
        missing.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("FavoriteSoft"), "StatusBadge"));
        missing.Setters.Add(new Setter(TextBlock.ForegroundProperty, ResourceBrush("DangerText"), "StatusText"));
        template.Triggers.Add(missing);
        return template;
    }

    private static Brush ResourceBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private Button ActionButton(string text, string toolTip, RoutedEventHandler click, string styleKey = "GhostButton")
    {
        object content = text;
        if (string.Equals(styleKey, "PrimaryButton", StringComparison.Ordinal))
        {
            var label = new TextBlock { Text = text };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SelectionText");
            content = label;
        }
        var button = new Button
        {
            Content = content,
            ToolTip = toolTip,
            Margin = new Thickness(0, 0, 7, 0),
            Padding = new Thickness(10, 5, 10, 5),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, toolTip);
        button.Click += click;
        _actionButtons.Add(button);
        return button;
    }

    private AlbumEntry? SelectedAlbum => (_albumList.SelectedItem as AlbumListItem)?.Album;

    private bool TryBeginBusy(string message)
    {
        if (_closed || _isBusy)
        {
            if (!_closed)
                _status.Text = "別のAlbum操作を処理中です。";
            return false;
        }

        _isBusy = true;
        Interlocked.Increment(ref _operationGeneration);
        SetInteractionEnabled(false);
        _status.Text = message;
        return true;
    }

    private void EndBusy(int operationGeneration)
    {
        if (_closed || operationGeneration != Volatile.Read(ref _operationGeneration))
            return;
        _isBusy = false;
        SetInteractionEnabled(true);
    }

    private void SetInteractionEnabled(bool enabled)
    {
        _name.IsEnabled = enabled;
        _albumList.IsEnabled = enabled;
        _memberList.IsEnabled = enabled;
        foreach (Button button in _actionButtons)
            button.IsEnabled = enabled;
    }

    private async Task<bool> ReloadAsync(string? selectAlbumId = null, string? message = null)
    {
        if (!TryBeginBusy("Albumsを読み込み中…"))
            return false;
        int operationGeneration = Volatile.Read(ref _operationGeneration);
        try
        {
            return await ReloadCoreAsync(selectAlbumId, message);
        }
        catch (Exception ex)
        {
            if (!_closed)
                _status.Text = $"共有状態を変更せず停止しました：{ex.Message}";
            return false;
        }
        finally
        {
            EndBusy(operationGeneration);
        }
    }

    private async Task<bool> ReloadCoreAsync(string? selectAlbumId = null, string? message = null, int workerDelayForSmokeMs = 0)
    {
        int reloadGeneration = Interlocked.Increment(ref _reloadGeneration);
        string? selectedId = selectAlbumId ?? SelectedAlbum?.Id;
        AlbumReadResult read = await Task.Run(() =>
        {
            _lastReadOffDispatcher = !Dispatcher.CheckAccess();
            if (workerDelayForSmokeMs > 0)
                Thread.Sleep(workerDelayForSmokeMs);
            return AlbumStore.Read(_storePath);
        });

        if (_closed || reloadGeneration != Volatile.Read(ref _reloadGeneration))
            return false;

        if (!read.Supported || read.Document is null)
        {
            _document = null;
            _albumList.ItemsSource = null;
            _memberList.ItemsSource = null;
            _albumSummary.Text = "保護された共有状態";
            _albumEmpty.Text = "Albumデータは保護されています。\n共有状態は変更していません。";
            _albumEmpty.Visibility = Visibility.Visible;
            _memberHeading.Text = "Members";
            _memberSummary.Text = "共有状態の保護中は画像を表示できません。";
            _memberEmpty.Text = "画像を表示できません。";
            _memberEmpty.Visibility = Visibility.Visible;
            _status.Text = $"Albumデータを保護しました。変更はありません。{read.Error}";
            return true;
        }

        _document = read.Document;
        var recentOrder = read.Document.RecentAlbumIds
            .Select((id, index) => (id, index))
            .ToDictionary(static item => item.id, static item => item.index, StringComparer.Ordinal);
        var items = read.Document.Albums
            .OrderByDescending(static album => album.Pinned)
            .ThenBy(album => recentOrder.TryGetValue(album.Id, out int index) ? index : int.MaxValue)
            .ThenBy(static album => album.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static album => new AlbumListItem(album))
            .ToList();
        _albumList.ItemsSource = items;
        _albumSummary.Text = $"{items.Count:N0} Albums  ·  Rev. {read.Document.Revision:N0}";
        _albumEmpty.Text = "Albumはまだありません。\n名前を入力して作成してください。";
        _albumEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppressMemberRefresh = true;
        try
        {
            _albumList.SelectedItem = items.FirstOrDefault(item => item.Album.Id == selectedId) ?? items.FirstOrDefault();
        }
        finally
        {
            _suppressMemberRefresh = false;
        }
        _status.Text = message ?? $"Shared Rev. {read.Document.Revision:N0} · 選択中 {_selectedPaths.Count:N0}枚を追加できます";
        await RefreshMembersAsync();
        return true;
    }

    private async Task<bool> RefreshMembersAsync()
    {
        int memberGeneration = Interlocked.Increment(ref _memberGeneration);
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _memberAvailabilityCts, cancellation);
        previous?.Cancel();
        AlbumEntry? album = SelectedAlbum;
        if (album is null)
        {
            try
            {
                _memberList.ItemsSource = null;
                _memberHeading.Text = "Members";
                _memberSummary.Text = "Albumを選ぶと画像を確認できます。";
                _memberEmpty.Text = "Albumを選ぶと画像を確認できます。";
                _memberEmpty.Visibility = Visibility.Visible;
                return true;
            }
            finally
            {
                CompleteMemberAvailabilityRequest(cancellation);
            }
        }

        _name.Text = album.Name;
        IReadOnlyList<AlbumMemberEntry> members = album.Members.ToArray();
        Interlocked.Increment(ref _memberAvailabilityPendingCount);
        try
        {
            await _memberAvailabilityGate.WaitAsync(cancellation.Token);
            int active = Interlocked.Increment(ref _memberAvailabilityActiveCount);
            UpdateMaximum(ref _memberAvailabilityMaxConcurrentCount, active);
            Interlocked.Increment(ref _memberAvailabilityStartedCount);
            List<AlbumMemberListItem> items;
            try
            {
                items = await Task.Run(() =>
                {
                    _lastAvailabilityOffDispatcher = !Dispatcher.CheckAccess();
                    var scanned = new List<AlbumMemberListItem>(members.Count);
                    foreach (AlbumMemberEntry member in members)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        int delay = Volatile.Read(ref _memberAvailabilityDelayForSmokeMs);
                        if (delay > 0 && cancellation.Token.WaitHandle.WaitOne(delay))
                            cancellation.Token.ThrowIfCancellationRequested();
                        scanned.Add(new AlbumMemberListItem(
                            member,
                            Availability(member.ImagePath),
                            string.Equals(album.CoverMemberId, member.Id, StringComparison.Ordinal)));
                    }
                    return scanned;
                }, cancellation.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _memberAvailabilityActiveCount);
                _memberAvailabilityGate.Release();
            }

            if (_closed || cancellation.IsCancellationRequested || memberGeneration != Volatile.Read(ref _memberGeneration))
                return false;
            _memberList.ItemsSource = items;
            _memberHeading.Text = album.Name;
            int current = items.Count(static item => item.Availability == "current");
            int missing = items.Count(static item => item.Availability == "missing");
            int outside = items.Count - current - missing;
            _memberSummary.Text = $"{items.Count:N0}枚  ·  利用可 {current:N0}  ·  範囲外 {outside:N0}  ·  見つからない {missing:N0}";
            _memberEmpty.Text = "このAlbumに画像はありません。\nGalleryで画像を選び、［選択画像を追加］を使ってください。";
            _memberEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Interlocked.Increment(ref _memberAvailabilityApplyCount);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Interlocked.Increment(ref _memberAvailabilityCanceledCount);
            return false;
        }
        catch (Exception ex)
        {
            if (!_closed && memberGeneration == Volatile.Read(ref _memberGeneration))
                _status.Text = $"Album内の画像状態を更新できません：{ex.Message}";
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _memberAvailabilityPendingCount);
            CompleteMemberAvailabilityRequest(cancellation);
        }
    }

    private void CompleteMemberAvailabilityRequest(CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref _memberAvailabilityCts, null, cancellation);
        cancellation.Dispose();
    }

    private static void UpdateMaximum(ref int location, int value)
    {
        int observed = Volatile.Read(ref location);
        while (value > observed)
        {
            int prior = Interlocked.CompareExchange(ref location, value, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    private string Availability(string imagePath)
        => !File.Exists(imagePath) ? "missing" : _catalogPaths.Contains(imagePath) ? "current" : "outside catalog - unavailable in this WPF session";

    private async Task<bool> RunMutationAsync(
        Func<AlbumMutationResult> mutation,
        string? albumId,
        Func<AlbumMutationResult, string> successMessage,
        Func<AlbumMutationResult, Task>? onSuccess = null,
        bool reconcileLibrary = true)
    {
        if (!TryBeginBusy("Albumを更新中…"))
            return false;
        int operationGeneration = Volatile.Read(ref _operationGeneration);
        bool mutationCommitted = false;
        try
        {
            Interlocked.Increment(ref _mutationInvocationCount);
            AlbumMutationResult result = await Task.Run(() =>
            {
                _lastMutationOffDispatcher = !Dispatcher.CheckAccess();
                return mutation();
            });
            _lastMutationStatus = result.Status;
            if (!result.Ok)
            {
                if (_closed || operationGeneration != Volatile.Read(ref _operationGeneration))
                    return true;
                string failure = result.Status == AlbumMutationStatus.Conflict
                    ? "Albumsが別の場所で変更されました。最新状態を読み込みました。もう一度実行してください。"
                    : $"共有状態を上書きせず停止しました：{result.Error ?? result.Status.ToString()}";
                await ReloadCoreAsync(albumId, failure);
                return true;
            }

            mutationCommitted = true;
            if (onSuccess is not null)
                await onSuccess(result);
            if (reconcileLibrary)
                await _libraryChanged();
            if (_closed || operationGeneration != Volatile.Read(ref _operationGeneration))
                return true;
            await ReloadCoreAsync(albumId, successMessage(result));
            return true;
        }
        catch (Exception ex)
        {
            if (!_closed)
                _status.Text = mutationCommitted
                    ? $"Albumの変更は完了しましたが、画面を更新できません：{ex.Message}"
                    : $"共有状態を上書きせず停止しました：{ex.Message}";
            return true;
        }
        finally
        {
            EndBusy(operationGeneration);
        }
    }

    private async Task<bool> CreateAlbumAsync()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { _status.Text = "Album名を入力してください。"; return false; }
        long? revision = _document?.Revision;
        string albumId = Guid.NewGuid().ToString("D");
        return await RunMutationAsync(
            () => AlbumStore.Create(_storePath, name, revision, albumId),
            albumId,
            _ => $"「{name}」を作成しました。");
    }

    private async Task<bool> RenameAlbumAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        if (album is null) return false;
        string name = _name.Text;
        long? revision = _document?.Revision;
        return await RunMutationAsync(() => AlbumStore.Update(_storePath, album.Id, revision, name: name), album.Id, _ => "Album名を変更しました。");
    }

    private async Task<bool> TogglePinAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        if (album is null) return false;
        long? revision = _document?.Revision;
        return await RunMutationAsync(
            () => AlbumStore.Update(_storePath, album.Id, revision, pinned: !album.Pinned),
            album.Id,
            _ => album.Pinned ? "ピン留めを解除しました。" : "Albumをピン留めしました。");
    }

    private async Task<bool> AddSelectionAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        if (album is null || _selectedPaths.Count == 0)
        {
            _status.Text = "Galleryで画像を選んでからAlbumsを開いてください。";
            return false;
        }

        SelectionAddPlan plan = BuildSelectionAddPlan(album, _selectedPaths);
        if (plan.Paths.Count == 0)
        {
            _status.Text = $"追加 0件 · スキップ {plan.SkippedCount:N0}件";
            return false;
        }

        long? revision = _document?.Revision;
        return await RunMutationAsync(
            () => AlbumStore.AddMembers(_storePath, album.Id, plan.Paths, revision),
            album.Id,
            _ => $"追加 {plan.Paths.Count:N0}件 · スキップ {plan.SkippedCount:N0}件");
    }

    private async Task<bool> RemoveMembersAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        var selected = _memberList.SelectedItems.OfType<AlbumMemberListItem>().ToList();
        if (album is null || selected.Count == 0)
        {
            _status.Text = "Albumから外す画像を選んでください。";
            return false;
        }
        long? revision = _document?.Revision;
        IReadOnlyList<string> memberIds = selected.Select(static item => item.Member.Id).ToList();
        return await RunMutationAsync(
            () => AlbumStore.RemoveMembers(_storePath, album.Id, memberIds, null, revision),
            album.Id,
            _ => $"Albumから{selected.Count:N0}件外しました。");
    }

    private async Task<bool> SetCoverAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        AlbumMemberListItem? member = _memberList.SelectedItems.OfType<AlbumMemberListItem>().FirstOrDefault();
        if (album is null || member is null) { _status.Text = "Coverにする画像を1件選んでください。"; return false; }
        long? revision = _document?.Revision;
        return await RunMutationAsync(
            () => AlbumStore.Update(_storePath, album.Id, revision, coverMemberId: member.Member.Id, updateCover: true),
            album.Id,
            _ => "AlbumのCoverを更新しました。");
    }

    private async Task<bool> DeleteAlbumAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        if (album is null) return false;
        if (MessageBox.Show(this, $"「{album.Name}」を削除しますか？\n元画像は削除されません。", "Albumを削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;
        long? revision = _document?.Revision;
        return await RunMutationAsync(() => AlbumStore.Delete(_storePath, album.Id, revision), null, _ => "Albumを削除しました。");
    }

    private async Task<bool> OpenAlbumAsync()
    {
        AlbumEntry? album = SelectedAlbum;
        if (album is null) return false;
        return await RunMutationAsync(
            () => AlbumStore.MarkRecent(_storePath, album.Id, expectedRevision: null),
            album.Id,
            result =>
            {
                AlbumEntry active = result.Document?.Albums.FirstOrDefault(candidate => candidate.Id == album.Id) ?? album;
                return $"「{active.Name}」をGalleryで開きました。Catalog外の画像はAlbumに残ります。";
            },
            async result =>
            {
                AlbumEntry active = result.Document?.Albums.FirstOrDefault(candidate => candidate.Id == album.Id) ?? album;
                await _activateAlbum(active);
            },
            reconcileLibrary: false);
    }

    private static SelectionAddPlan BuildSelectionAddPlan(AlbumEntry album, IReadOnlyList<string> selectedPaths)
    {
        var existing = album.Members
            .Select(static member => Path.GetFullPath(member.ImagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in selectedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !AlbumImageExtensions.Contains(Path.GetExtension(path)))
                continue;
            candidates.Add(Path.GetFullPath(path));
        }
        List<string> paths = candidates.Where(path => !existing.Contains(path)).ToList();
        return new SelectionAddPlan(paths, Math.Max(0, selectedPaths.Count - paths.Count));
    }

    internal async Task WaitForIdleForSmokeAsync()
    {
        while (_isBusy
            || Volatile.Read(ref _memberAvailabilityPendingCount) > 0
            || Volatile.Read(ref _memberAvailabilityActiveCount) > 0)
            await Task.Delay(20);
    }

    internal bool SelectAlbumForSmoke(string albumId)
    {
        AlbumListItem? item = (_albumList.ItemsSource as IEnumerable<AlbumListItem>)?.FirstOrDefault(candidate => candidate.Album.Id == albumId);
        if (item is null)
            return false;
        _albumList.SelectedItem = item;
        return true;
    }

    internal Task<bool> AddSelectionForSmokeAsync() => AddSelectionAsync();

    internal Task<bool> OpenSelectedAlbumForSmokeAsync() => OpenAlbumAsync();

    internal Task<bool> RunMutationForSmokeAsync(Func<AlbumMutationResult> mutation, string? albumId = null)
        => RunMutationAsync(mutation, albumId, _ => "Smoke mutation committed.");

    internal async Task<bool> VerifyReloadGenerationGuardForSmokeAsync()
    {
        Task<bool> stale = ReloadCoreAsync(SelectedAlbum?.Id, "stale", workerDelayForSmokeMs: 120);
        await Task.Delay(15);
        Task<bool> latest = ReloadCoreAsync(SelectedAlbum?.Id, "latest");
        bool latestApplied = await latest;
        bool staleApplied = await stale;
        return latestApplied && !staleApplied && string.Equals(_status.Text, "latest", StringComparison.Ordinal);
    }

    internal bool ReadRanOffDispatcherForSmoke => _lastReadOffDispatcher;
    internal bool MutationRanOffDispatcherForSmoke => _lastMutationOffDispatcher;
    internal bool AvailabilityRanOffDispatcherForSmoke => _lastAvailabilityOffDispatcher;
    internal bool IsBusyForSmoke => _isBusy;
    internal int MutationInvocationCountForSmoke => Volatile.Read(ref _mutationInvocationCount);
    internal AlbumMutationStatus? LastMutationStatusForSmoke => _lastMutationStatus;
    internal string StatusForSmoke => _status.Text;
    internal bool ActionButtonsHaveToolTipsForSmoke
        => _actionButtons.Count > 0 && _actionButtons.All(button => !string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
    internal bool ActionButtonsHaveAutomationHelpTextForSmoke
        => _actionButtons.Count > 0 && _actionButtons.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(button)));
    internal bool ActionButtonsHaveFocusVisualForSmoke
        => _actionButtons.Count > 0 && _actionButtons.All(button => button.FocusVisualStyle is not null);
    internal string HeaderSubtitleForSmoke => _headerSubtitle.Text;
    internal string StatusLabelForSmoke => _statusLabel.Text;
    internal IReadOnlyList<string> ActionLabelsForSmoke
        => _actionButtons.Select(ActionButtonLabel).ToArray();
    internal IReadOnlyDictionary<string, string> ActionHelpTextByLabelForSmoke
        => _actionButtons.ToDictionary(
            ActionButtonLabel,
            AutomationProperties.GetHelpText,
            StringComparer.Ordinal);
    internal int MemberAvailabilityStartedCountForSmoke => Volatile.Read(ref _memberAvailabilityStartedCount);
    internal int MemberAvailabilityCanceledCountForSmoke => Volatile.Read(ref _memberAvailabilityCanceledCount);
    internal int MemberAvailabilityMaxConcurrentCountForSmoke => Volatile.Read(ref _memberAvailabilityMaxConcurrentCount);
    internal int MemberAvailabilityApplyCountForSmoke => Volatile.Read(ref _memberAvailabilityApplyCount);
    internal int MemberAvailabilityActiveCountForSmoke => Volatile.Read(ref _memberAvailabilityActiveCount);
    internal int MemberAvailabilityPendingCountForSmoke => Volatile.Read(ref _memberAvailabilityPendingCount);
    internal string? SelectedAlbumIdForSmoke => SelectedAlbum?.Id;
    internal string MemberHeadingForSmoke => _memberHeading.Text;
    internal int MemberItemCountForSmoke => (_memberList.ItemsSource as IEnumerable<AlbumMemberListItem>)?.Count() ?? 0;

    private static string ActionButtonLabel(Button button)
        => button.Content is TextBlock label
            ? label.Text
            : Convert.ToString(button.Content) ?? "";

    internal void ConfigureMemberAvailabilityDelayForSmoke(int milliseconds)
        => Volatile.Write(ref _memberAvailabilityDelayForSmokeMs, Math.Max(0, milliseconds));

    internal async Task WaitForMemberAvailabilityStartedForSmokeAsync(int startedAfter, int timeoutMilliseconds = 5_000)
    {
        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref _memberAvailabilityStartedCount) <= startedAfter)
        {
            if (timeout.ElapsedMilliseconds >= timeoutMilliseconds)
                throw new TimeoutException("Album member availability scan did not start.");
            await Task.Delay(10);
        }
    }

    internal async Task WaitForMemberAvailabilityIdleForSmokeAsync(int timeoutMilliseconds = 5_000)
    {
        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref _memberAvailabilityPendingCount) > 0
            || Volatile.Read(ref _memberAvailabilityActiveCount) > 0)
        {
            if (timeout.ElapsedMilliseconds >= timeoutMilliseconds)
                throw new TimeoutException("Album member availability scans did not stop.");
            await Task.Delay(10);
        }
    }

    private sealed record SelectionAddPlan(IReadOnlyList<string> Paths, int SkippedCount);

    private sealed record AlbumListItem(AlbumEntry Album)
    {
        public string Name => Album.Name;
        public string Detail => $"{Album.Members.Count:N0} images  ·  Rev. {Album.Revision:N0}";
        public string ToolTip => $"{Album.Name} · {Album.Members.Count:N0} images";
        public Visibility PinnedVisibility => Album.Pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record AlbumMemberListItem(AlbumMemberEntry Member, string Availability, bool IsCover)
    {
        public string FileName => System.IO.Path.GetFileName(Member.ImagePath);
        public string Path => Member.ImagePath;
        public string ExtensionLabel
        {
            get
            {
                string extension = System.IO.Path.GetExtension(Member.ImagePath).TrimStart('.');
                return string.IsNullOrWhiteSpace(extension) ? "FILE" : extension.ToUpperInvariant();
            }
        }
        public string AvailabilityDisplay => Availability switch
        {
            "current" => "CURRENT",
            "missing" => "MISSING",
            _ => "OUTSIDE",
        };
        public Visibility CoverVisibility => IsCover ? Visibility.Visible : Visibility.Collapsed;
    }
}
