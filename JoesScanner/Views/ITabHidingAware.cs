namespace JoesScanner.Views
{
    // Implement on tab pages that need an explicit callback when they are hidden inside RootPage.
    // This is used because RootPage hosts page content views directly, and platform lifecycle hooks can be inconsistent.
    public interface ITabHidingAware
    {
        void OnTabHiding();
    }
}
