using JoesScanner.Models;

namespace JoesScanner.Services;

// Lightweight in-app tab switch bus.
// We avoid Shell tab routing on iOS to prevent the native "More" overflow UI
// from adding a large back button and labels when more than five tabs exist.
internal sealed class TabNavigationService
{
    public static TabNavigationService Instance { get; } = new();

    public event EventHandler<AppTab>? TabRequested;

    public void Request(AppTab tab)
    {
        TabRequested?.Invoke(this, tab);
    }
}
