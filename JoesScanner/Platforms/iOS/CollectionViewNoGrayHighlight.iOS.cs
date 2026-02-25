#nullable enable

#if IOS
using UIKit;

namespace JoesScanner.Platforms.iOS;

// iOS: globally suppress the default gray highlight/selection background on UICollectionViewCell.
// This is safe for our app because playback is driven by explicit tap gestures rather than
// CollectionView selection state.
public static class CollectionViewNoGrayHighlight
{
    public static void Apply()
    {
        // NOTE:
        // Some iOS target frameworks do not expose UIAppearance hooks for SelectedBackgroundView.
        // We keep this method as a harmless no-op so the app compiles cleanly.
        // The actual gray-highlight suppression is handled in MainPage.xaml.cs via
        // ClearIosCallCellHighlight(), which operates on the live UICollectionView cells.
    }
}
#endif
