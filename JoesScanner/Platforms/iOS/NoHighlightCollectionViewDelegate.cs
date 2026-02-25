#nullable enable

using UIKit;

namespace JoesScanner.Platforms.iOS;

// NOTE:
// This file exists only to preserve compatibility with earlier patches that referenced
// NoHighlightCollectionViewDelegate.Apply(...).
//
// In this codebase/target, the UIKit WillDisplayCell event args type used in the earlier
// implementation is not available, which caused build failures.
//
// The current no-gray-highlight behavior is handled elsewhere (page-level tap handling and
// iOS cell reset logic). This helper is intentionally a safe no-op aside from disabling
// UICollectionView selection.
public static class NoHighlightCollectionViewDelegate
{
    public static void Apply(UICollectionView? collectionView)
    {
        if (collectionView == null)
        {
            return;
        }

        // Prevent iOS from keeping a selected state that can paint a gray background.
        collectionView.AllowsSelection = false;
        collectionView.AllowsMultipleSelection = false;
    }
}
