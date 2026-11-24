using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    internal FavoritesService Favorites => _mainViewModel.Favorites;

    public bool LocalOnlyMode
    {
        get => _settings.LocalOnlyMode;
        set => _settings.LocalOnlyMode = value;
    }

    public string FavoritesStatus => Favorites.StatusText;
    public bool FavoritesCanSignIn => !LocalOnlyMode && !Favorites.IsSignedIn;
    public bool FavoritesCanSignOut => Favorites.IsSignedIn;
    public bool FavoritesHasConflicts => Favorites.Collection.Conflicts.Length != 0;

    private void OnFavoritesSettingsChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(LocalOnlyMode));
            OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
            OnPropertyChanged(nameof(FavoritesStatus));
            OnPropertyChanged(nameof(FavoritesCanSignIn));
            OnPropertyChanged(nameof(FavoritesCanSignOut));
            OnPropertyChanged(nameof(FavoritesHasConflicts));
        });
    }

    internal Task SignInForFavoritesAsync(string username, string password) =>
        SignInForFavoritesAsync(username.Trim(), password, CancellationToken.None);

    private async Task SignInForFavoritesAsync(string username, string password, CancellationToken token)
    {
        if (username.Length == 0 || username.Contains(':') || string.IsNullOrEmpty(password))
            throw new ArgumentException("Enter your Joe's Scanner username or email and password.");

        var verified = await Favorites.VerifyLoginAsync(username, password, token);
        await _mainViewModel.ApplyHostedAccountAsync(username, false, () => Favorites.CanCompleteLogin(verified),
            () => Favorites.CompleteLogin(verified, username, password));
    }

    internal Task SignOutOfFavoritesAsync()
    {
        var owner = Favorites.OwnerKey;
        return _mainViewModel.ApplyHostedAccountAsync(string.Empty, false, () => owner == Favorites.OwnerKey, Favorites.SignOut);
    }

    internal bool HasFavoritesForServer(ScannerConnectionRow row) => Favorites.HasServerFavorites(row.Profile);
}
