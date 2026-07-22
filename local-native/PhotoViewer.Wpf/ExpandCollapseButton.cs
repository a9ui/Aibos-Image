using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public sealed class ExpandCollapseButton : Button
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(ExpandCollapseButton),
        new FrameworkPropertyMetadata(true, OnIsExpandedChanged));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new ExpandCollapseButtonAutomationPeer(this);

    internal void SetExpandedFromAutomation(bool expanded)
    {
        if (!IsEnabled)
            throw new ElementNotEnabledException();

        void ApplyRequestedState()
        {
            if (IsExpanded != expanded)
                OnClick();
        }

        if (Dispatcher.CheckAccess())
            ApplyRequestedState();
        else
            Dispatcher.Invoke(ApplyRequestedState);
    }

    private static void OnIsExpandedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ExpandCollapseButton button
            || args.OldValue is not bool oldValue
            || args.NewValue is not bool newValue
            || UIElementAutomationPeer.FromElement(button) is not ExpandCollapseButtonAutomationPeer peer)
        {
            return;
        }

        peer.RaiseExpandCollapseStateChanged(oldValue, newValue);
    }
}

public sealed class ExpandCollapseButtonAutomationPeer(ExpandCollapseButton owner)
    : ButtonAutomationPeer(owner), IExpandCollapseProvider
{
    private ExpandCollapseButton ExpandCollapseOwner => (ExpandCollapseButton)Owner;

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

    public ExpandCollapseState ExpandCollapseState
        => ExpandCollapseOwner.IsExpanded
            ? ExpandCollapseState.Expanded
            : ExpandCollapseState.Collapsed;

    public void Collapse() => ExpandCollapseOwner.SetExpandedFromAutomation(expanded: false);

    public void Expand() => ExpandCollapseOwner.SetExpandedFromAutomation(expanded: true);

    internal void RaiseExpandCollapseStateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue)
            return;

        RaisePropertyChangedEvent(
            ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            oldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
            newValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
}
