using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views.Controls;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;

namespace JoesScanner.Views
{
    public partial class HistoryPage : ContentPage
    {
        private readonly HistoryViewModel _viewModel;
        private int _ignoreNextScrollEvents;
        private bool _isDropdownOpen;

        private bool _addressAlertHandlersAttached;
public HistoryPage(HistoryViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            AttachAddressAlertHandlers();

            _viewModel.ScrollRequested -= OnScrollRequested;
            _viewModel.ScrollRequested += OnScrollRequested;

            try
            {
                await _viewModel.OnPageOpenedAsync();
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"HistoryPage: OnAppearing failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }

        private void AttachAddressAlertHandlers()
        {
            if (_addressAlertHandlersAttached)
                return;

            _addressAlertHandlersAttached = true;

            try
            {
                AppLog.Add(() => $"AddrAlert(UI): HistoryPage appearing. vm={_viewModel?.GetType().Name ?? "(null)"} visibleCount={_viewModel?.AddressAlerts?.Count ?? -1} hasVisibleFlag={_viewModel?.HasAddressAlerts}");
            }
            catch
            {
            }

            try
            {
                if (_viewModel?.AddressAlerts != null)
                {
                    _viewModel.AddressAlerts.CollectionChanged += (_, __) =>
                    {
                        try
                        {
                            AppLog.Add(() => $"AddrAlert(UI): HistoryPage AddressAlerts changed. visibleCount={_viewModel.AddressAlerts.Count} hasVisibleFlag={_viewModel.HasAddressAlerts}");
                        }
                        catch
                        {
                        }
                    };
                }
            }
            catch
            {
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            _viewModel.ScrollRequested -= OnScrollRequested;

            try
            {
                _ = _viewModel.OnPageClosedAsync();
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"HistoryPage: OnDisappearing failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }


        private void OnScrollRequested(CallItem call, ScrollToPosition position)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    _ignoreNextScrollEvents = 3;
                    CallsList.ScrollTo(call, position: position, animate: false);
                }
                catch
                {
                }
            });
        }

        private void OnCallSelected(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selected = e.CurrentSelection?.FirstOrDefault() as CallItem;

                // Clear selection immediately so the same call can be tapped again.
                if (sender is CollectionView cv)
                    cv.SelectedItem = null;

                if (selected == null)
                    return;

                if (BindingContext is HistoryViewModel vm)
                {
                    var cmd = vm.PlayFromCallCommand;
                    if (cmd != null && cmd.CanExecute(selected))
                        cmd.Execute(selected);
                }
            }
            catch
            {
            }
        }

        private async void OnCallPrimaryTapped(object sender, TappedEventArgs e)
        {
            try
            {
                var selected = e.Parameter as CallItem;
                if (selected == null)
                    return;
                await ShowCallOptionsAsync(selected);
            }
            catch
            {
            }
        }

        private async Task ShowCallOptionsAsync(CallItem call)
        {
            try
            {
                if (call == null)
                    return;

                var choice = await DisplayActionSheet(
                    title: "Call",
                    cancel: "Cancel",
                    destruction: null,
                    buttons: new[]
                    {
                        "Play",
                        "Download this call",
                        "Download a range",
                        "Download a range (this talkgroup only)",
                    });

                if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, "Cancel", StringComparison.Ordinal))
                    return;

                if (BindingContext is not HistoryViewModel vm)
                    return;

                if (string.Equals(choice, "Play", StringComparison.Ordinal))
                {
                    var cmd = vm.PlayFromCallCommand;
                    if (cmd != null && cmd.CanExecute(call))
                        cmd.Execute(call);
                    return;
                }

                if (string.Equals(choice, "Download this call", StringComparison.Ordinal))
                {
                    await DownloadSingleAsync(call);
                    return;
                }

                if (string.Equals(choice, "Download a range", StringComparison.Ordinal) ||
                    string.Equals(choice, "Download a range (this talkgroup only)", StringComparison.Ordinal))
                {
                    var talkgroupOnly = string.Equals(choice, "Download a range (this talkgroup only)", StringComparison.Ordinal);

                    var eachSide = await PromptRangeCountAsync();
                    if (eachSide == null)
                        return;

                    var calls = BuildRangeCalls(vm.Calls, call, eachSide.Value, talkgroupOnly);
                    if (calls.Count == 0)
                        return;

                    await DownloadRangeAsZipAsync(calls);
                }
            }
            catch
            {
            }
        }








        private static int FindCallIndex(System.Collections.Generic.IReadOnlyList<CallItem> list, CallItem target)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var cur = list[i];
                if (ReferenceEquals(cur, target))
                    return i;

                if (!string.IsNullOrWhiteSpace(target.BackendId) &&
                    string.Equals(cur.BackendId, target.BackendId, StringComparison.Ordinal))
                    return i;

                if (cur.Timestamp == target.Timestamp &&
                    string.Equals(cur.Talkgroup, target.Talkgroup, StringComparison.Ordinal) &&
                    string.Equals(cur.Site, target.Site, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        
        private async Task<int?> PromptRangeCountAsync()
        {
            const int min = 1;
            const int max = 25;
            const int def = 5;

            try
            {
                var input = await UiDialogs.PromptAsync(
                    title: "Download range",
                    message: $"How many calls on each side? ({min} to {max})",
                    accept: "OK",
                    cancel: "Cancel",
                    initialValue: def.ToString(),
                    maxLength: 2,
                    keyboard: Keyboard.Numeric);

                if (string.IsNullOrWhiteSpace(input))
                    return null;

                if (!int.TryParse(input.Trim(), out var n))
                    return null;

                if (n < min) n = min;
                if (n > max) n = max;
                return n;
            }
            catch
            {
                return null;
            }
        }

        private static List<CallItem> BuildRangeCalls(IReadOnlyList<CallItem> source, CallItem center, int eachSide, bool talkgroupOnly)
        {
            var list = new List<CallItem>();

            if (source == null || source.Count == 0 || center == null)
                return list;

            var idx = -1;
            for (int i = 0; i < source.Count; i++)
            {
                if (ReferenceEquals(source[i], center))
                {
                    idx = i;
                    break;
                }

                // Fallback to ID match if we got a different instance
                if (!string.IsNullOrWhiteSpace(source[i]?.BackendId) && string.Equals(source[i].BackendId, center.BackendId, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
                return list;

            var start = Math.Max(0, idx - eachSide);
            var end = Math.Min(source.Count - 1, idx + eachSide);

            var tg = center.Talkgroup ?? string.Empty;

            for (int i = start; i <= end; i++)
            {
                var c = source[i];
                if (c == null)
                    continue;

                if (talkgroupOnly)
                {
                    var cTg = c.Talkgroup ?? string.Empty;
                    if (!string.Equals(cTg, tg, StringComparison.Ordinal))
                        continue;
                }

                list.Add(c);
            }

            return list;
        }

        private async Task DownloadSingleAsync(CallItem call)
        {
            try
            {
                var downloader = this.Handler?.MauiContext?.Services?.GetService<ICallDownloadService>();
                if (downloader == null)
                    return;

                await downloader.DownloadSingleAsync(call);
            }
            catch
            {
            }
        }

        private async Task DownloadRangeAsZipAsync(System.Collections.Generic.IReadOnlyList<CallItem> calls)
        {
            try
            {
                var downloader = this.Handler?.MauiContext?.Services?.GetService<ICallDownloadService>();
                if (downloader == null)
                    return;

                await downloader.DownloadRangeZipAsync(calls);
            }
            catch
            {
            }
        }






private void OnAlertTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e?.Parameter is not CallItem call)
            return;

        if (BindingContext is HistoryViewModel vm)
        {
            var resolved = vm.ResolveCallForAddressAlert(call);
            if (resolved == null)
            {
                vm.DismissAddressAlert(call);
                return;
            }

            try { CallsList?.ScrollTo(resolved, position: ScrollToPosition.Center, animate: true); } catch { }

            var cmd = vm.PlayFromCallCommand;
            if (cmd != null && cmd.CanExecute(resolved))
                cmd.Execute(resolved);
        }
    }
    catch
    {
    }
}

private void OnAlertDismissTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e?.Parameter is not CallItem call)
            return;

        if (BindingContext is HistoryViewModel vm)
            vm.DismissAddressAlert(call);
    }
    catch
    {
    }
}

private void OnAlertDismissInvoked(object? sender, EventArgs e)
{
    try
    {
        if (sender is SwipeItem swipe && swipe.CommandParameter is CallItem call)
        {
            if (BindingContext is HistoryViewModel vm)
                vm.DismissAddressAlert(call);
        }
    }
    catch
    {
    }
}

        private void OnCallsScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            try
            {
                if (BindingContext is not HistoryViewModel vm)
                    return;

                // While playback is running we do not trigger any paging or list maintenance.
                // Users can scroll freely, but the dataset is frozen for the active search.
                if (vm.IsHistoryPlaybackRunning)
                    return;

                if (_ignoreNextScrollEvents > 0)
                {
                    _ignoreNextScrollEvents--;
                    return;
                }

                // Do not block UI thread. VM throttles and serializes paging internally.
                _ = vm.OnCallsListScrolledAsync(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
            }
            catch
            {
            }
        }

        private async void OnReceiverTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Receiver",
                getItems: () => _viewModel.Receivers,
                getCurrent: () => _viewModel.SelectedReceiver,
                setSelected: item => _viewModel.SelectedReceiver = item);
        }

        private async void OnSiteTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Site",
                getItems: () => _viewModel.Sites,
                getCurrent: () => _viewModel.SelectedSite,
                setSelected: item => _viewModel.SelectedSite = item);
        }

        private async void OnTalkgroupTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Talkgroup",
                getItems: () => _viewModel.Talkgroups,
                getCurrent: () => _viewModel.SelectedTalkgroup,
                setSelected: item => _viewModel.SelectedTalkgroup = item);
        }

        private async Task ShowLookupAsync(
            string title,
            Func<System.Collections.Generic.IEnumerable<HistoryLookupItem>> getItems,
            Func<HistoryLookupItem?> getCurrent,
            Action<HistoryLookupItem?> setSelected)
        {
            try
            {
                if (_isDropdownOpen)
                    return;

                var items = getItems()?.ToList();
                if (HistoryViewModel.LookupHasOnlyDefault(items))
                {
                    AppLog.Add(() => $"History: dropdown '{title}' had only default/empty items. Triggering lookup load. count={items?.Count ?? 0}");
                    await _viewModel.EnsureLookupsLoadedAsync(forceReload: true);
                    items = getItems()?.ToList();
                    if (HistoryViewModel.LookupHasOnlyDefault(items))
                    {
                        AppLog.Add(() => $"History: dropdown '{title}' still default-only/empty after reload. count={items?.Count ?? 0}");
                        return;
                    }
                }

                _isDropdownOpen = true;

                const string cancel = "Cancel";
                var labels = items.Select(i => i.Label).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                if (labels.Length == 0)
                    return;

                string? choice;
                var useModalLookup = DeviceInfo.Current.Platform == DevicePlatform.iOS || labels.Length > 40;
                if (useModalLookup)
                {
                    var modal = new LookupModalPage(title, cancel, labels);
                    await Navigation.PushModalAsync(modal, animated: true);
                    choice = await modal.Result;
                }
                else
                {
                    choice = await UiDialogs.ActionSheetAsync(title, cancel, null, labels);
                    if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, cancel, StringComparison.Ordinal))
                        return;
                }

                var selected = items.FirstOrDefault(i => string.Equals(i.Label, choice, StringComparison.Ordinal));
                if (selected == null)
                    return;

                var current = getCurrent();
                if (ReferenceEquals(current, selected))
                    return;

                setSelected(selected);
            }
            catch
            {
            }
            finally
            {
                _isDropdownOpen = false;
            }
        }

        // iOS: the near-transparent native pickers can be hard to reliably tap.
        // We explicitly focus them when the styled border is tapped.
        private void OnTimeBorderTapped(object sender, TappedEventArgs e)
        {
            try { HistoryTimePicker?.Focus(); } catch { }
        }

        private void OnDateFromBorderTapped(object sender, TappedEventArgs e)
        {
            if (_viewModel?.CanPickDateRange != true)
                return;

            try { HistoryDateFromPicker?.Focus(); } catch { }
        }

        private void OnDateToBorderTapped(object sender, TappedEventArgs e)
        {
            if (_viewModel?.CanPickDateRange != true)
                return;

            try { HistoryDateToPicker?.Focus(); } catch { }
        }

        private async void OnAddressLinkTapped(object? sender, TappedEventArgs e)
        {
            try
            {
                if (e.Parameter is not CallItem call)
                    return;

                if (string.IsNullOrWhiteSpace(call.DetectedAddress))
                    return;

                var settings = this.Handler?.MauiContext?.Services?.GetService<ISettingsService>();
                if (settings == null)
                    return;

                if (!settings.AddressDetectionEnabled || !settings.AddressDetectionOpenMapsOnTap)
                    return;

                try
                {
                    var key = string.IsNullOrWhiteSpace(call.BackendId) ? call.Timestamp.ToString("O") : call.BackendId;
                    AppLog.Add(() => $"AddrDetect(Hist): open maps call={key} addr='{call.DetectedAddress}'");
                }
                catch { }

                var mapAnchor = string.Empty;
                try
                {
                    settings.TryGetMapAnchorForServerUrl(settings.ServerUrl, out mapAnchor);
                }
                catch
                {
                }

                var uri = BuildMapsSearchUri(call.DetectedAddress, mapAnchor);
                await Launcher.Default.OpenAsync(uri);
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AddrDetect(Hist): open maps failed. {ex.Message}"); } catch { }
            }
        }



private async void OnWhat3WordsLinkTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e.Parameter is not CallItem call)
            return;

        if (string.IsNullOrWhiteSpace(call.What3WordsAddress))
            return;

        var uri = BuildWhat3WordsUri(call.What3WordsAddress);
        await Launcher.Default.OpenAsync(uri);
    }
    catch (Exception ex)
    {
        try { AppLog.Add(() => $"What3Words(Hist): open map failed. {ex.Message}"); } catch { }
    }
}


private async void OnWhat3WordsCopyTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e.Parameter is not CallItem call)
            return;

        if (string.IsNullOrWhiteSpace(call.What3WordsAddress))
            return;

        await Clipboard.Default.SetTextAsync(call.What3WordsAddress.Trim());
    }
    catch (Exception ex)
    {
        try { AppLog.Add(() => $"What3Words(Hist): copy failed. {ex.Message}"); } catch { }
    }
}

private async void OnWhat3WordsCoordsCopyTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e.Parameter is not CallItem call)
            return;

        if (string.IsNullOrWhiteSpace(call.What3WordsCoordinatesText))
            return;

        await Clipboard.Default.SetTextAsync(call.What3WordsCoordinatesText.Trim());
    }
    catch (Exception ex)
    {
        try { AppLog.Add(() => $"What3Words(Hist): copy coords failed. {ex.Message}"); } catch { }
    }
}


private static Uri BuildWhat3WordsUri(string what3Words)
{
    var s = (what3Words ?? string.Empty).Trim();

    if (s.StartsWith("///", StringComparison.Ordinal))
        s = s.Substring(3);

    s = s.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '!', '?');

    var url = "https://map.what3words.com/" + Uri.EscapeDataString(s);
    return new Uri(url);
}

        private static Uri BuildMapsSearchUri(string addressQuery, string mapAnchor)
        {
            var q = (addressQuery ?? string.Empty).Trim();

            var anchor = (mapAnchor ?? string.Empty).Trim();
            if (anchor.Length > 0)
                q = q + ", " + anchor;
            if (q.Length == 0)
                return new Uri("https://www.google.com/maps/search/?api=1&query=");

            var encoded = Uri.EscapeDataString(q);

            try
            {
                if (DeviceInfo.Platform == DevicePlatform.iOS)
                    return new Uri($"http://maps.apple.com/?q={encoded}");

                if (DeviceInfo.Platform == DevicePlatform.Android)
                    return new Uri($"geo:0,0?q={encoded}");
            }
            catch
            {
            }

            return new Uri($"https://www.google.com/maps/search/?api=1&query={encoded}");
        }

    }
}
