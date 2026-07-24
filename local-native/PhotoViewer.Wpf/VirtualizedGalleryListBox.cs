using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public sealed class VirtualizedGalleryListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer()
        => new VirtualizedGalleryListBoxAutomationPeer(this);
}

public sealed class VirtualizedGalleryListBoxAutomationPeer(VirtualizedGalleryListBox owner)
    : ListBoxAutomationPeer(owner)
{
    protected override ItemAutomationPeer CreateItemAutomationPeer(object item)
        => new VirtualizedGalleryListBoxItemAutomationPeer(item, this);

    internal ItemAutomationPeer GetOrCreateItemPeer(object item)
        => FindOrCreateItemAutomationPeer(item);
}

public sealed class VirtualizedGalleryListBoxItemAutomationPeer(
    object item,
    SelectorAutomationPeer selectorAutomationPeer)
    : ListBoxItemAutomationPeer(item, selectorAutomationPeer)
{
    protected override string GetNameCore()
        => Item is Tile tile && !string.IsNullOrWhiteSpace(tile.FileName)
            ? tile.FileName
            : base.GetNameCore();

    protected override string GetHelpTextCore()
        => Item is Tile tile
            ? tile.AutomationHelpText
            : base.GetHelpTextCore();
}
