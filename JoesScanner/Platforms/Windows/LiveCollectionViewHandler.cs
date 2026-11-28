using System.ComponentModel;
using Microsoft.Maui.Controls.Handlers.Items2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Native = Microsoft.UI.Xaml.Controls;
using MauiScrollBar = Microsoft.Maui.ScrollBarVisibility;

namespace JoesScanner.WinUI;

// Retain MAUI's item factory, selection and layout mappers. Only the Live list uses this native ItemsView.
internal sealed class LiveCollectionViewHandler : CollectionViewHandler2
{
    protected override Native.ItemsView CreatePlatformView() => new LiveItemsView();

    protected override void ConnectHandler(Native.ItemsView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.PropertyChanged += OnViewPropertyChanged;
        ApplyScrollBars();
    }

    protected override void DisconnectHandler(Native.ItemsView platformView)
    {
        VirtualView.PropertyChanged -= OnViewPropertyChanged;
        base.DisconnectHandler(platformView);
    }

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CollectionView.VerticalScrollBarVisibility) or
            nameof(CollectionView.HorizontalScrollBarVisibility))
            ApplyScrollBars();
    }

    private void ApplyScrollBars()
    {
        var native = (LiveItemsView)PlatformView;
        native.SetScrollBars(VirtualView.VerticalScrollBarVisibility, VirtualView.HorizontalScrollBarVisibility);
    }
}

public sealed partial class LiveItemsView : Native.ItemsView
{
    private readonly Native.ControlTemplate _scrollTemplate;

    private MauiScrollBar _verticalBar;
    private MauiScrollBar _horizontalBar;
    private bool _reconstructing;

    public LiveItemsView()
    {
        var resources = new LiveScrollViewResources();
        _scrollTemplate = (Native.ControlTemplate)resources["LiveScrollViewTemplate"];
        Style = (Microsoft.UI.Xaml.Style)resources["LiveItemsViewStyle"];
        // CollectionViewHandler2 reenables keyboard focus on load only for its internal ItemsView.
        Loaded += (_, _) => IsTabStop = true;

        // Match MAUI's card presentation. Interactive controls inside each card retain their own visuals.
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ItemContainerBackground", "ItemContainerPointerOverBackground",
            "ItemContainerPressedBackground", "ItemContainerBorderBrush", "ItemContainerPointerOverBorderBrush",
            "ItemContainerPressedBorderBrush" })
            Resources[key] = transparent;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // MAUI disables the per-item entrance animation for a recycling, frequently updated list.
        ItemTransitionProvider = null;
        if (ScrollView is { } scroll)
        {
            // Wheel detents and explicit Live navigation must reach the same native
            // request queue. Touch and precision touchpad input remain compositor-owned.
            scroll.IgnoredInputKinds |= Native.ScrollingInputKinds.MouseWheel;
            scroll.Template = _scrollTemplate;
            ApplyScrollBars(scroll);
        }
    }

    // Keep an incomplete suffix out of the accessibility tree until the measured bookmark is published.
    internal void SetReconstructing(bool reconstructing)
    {
        if (_reconstructing == reconstructing)
            return;
        _reconstructing = reconstructing;
        if (ScrollView?.ScrollPresenter is LiveScrollPresenter presenter)
            presenter.SetReconstructing(reconstructing);
        FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    internal void SetScrollBars(MauiScrollBar vertical, MauiScrollBar horizontal)
    {
        (_verticalBar, _horizontalBar) = (vertical, horizontal);
        if (ScrollView is { } scroll)
            ApplyScrollBars(scroll);
    }

    private void ApplyScrollBars(Native.ScrollView scroll)
    {
        scroll.HorizontalScrollMode = Native.ScrollingScrollMode.Disabled;
        scroll.VerticalScrollMode = Native.ScrollingScrollMode.Enabled;
        scroll.VerticalScrollBarVisibility = ToNative(_verticalBar);
        scroll.HorizontalScrollBarVisibility = ToNative(_horizontalBar);
    }

    private static Native.ScrollingScrollBarVisibility ToNative(MauiScrollBar visibility) => visibility switch
    {
        MauiScrollBar.Always => Native.ScrollingScrollBarVisibility.Visible,
        MauiScrollBar.Never => Native.ScrollingScrollBarVisibility.Hidden,
        _ => Native.ScrollingScrollBarVisibility.Auto
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new LiveItemsPeer(this);

    private sealed partial class LiveItemsPeer(LiveItemsView view) : ItemsViewAutomationPeer(view)
    {
        protected override string GetClassNameCore() => nameof(CollectionView);

        protected override object? GetPatternCore(PatternInterface pattern)
        {
            if (pattern == PatternInterface.Scroll && Owner is LiveItemsView { ScrollView.ScrollPresenter: { } presenter })
                return FrameworkElementAutomationPeer.CreatePeerForElement(presenter)?.GetPattern(pattern);

            return Owner is LiveItemsView { _reconstructing: true } ? null : base.GetPatternCore(pattern);
        }

        protected override IList<AutomationPeer> GetChildrenCore() =>
            Owner is LiveItemsView { _reconstructing: true } ? [] : base.GetChildrenCore();
    }
}
