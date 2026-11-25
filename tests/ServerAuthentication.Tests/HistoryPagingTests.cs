using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static Task RecorderHistoryPreferences()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.HistoryFilters-");
        try
        {
            var path = new HistoryDatabase(Path.Combine(directory.FullName, "scanner.db"));
            var connections = new ScannerConnectionStore(path);
            var first = connections.Save("https://one.example/archive", "One", null, null);
            var second = connections.Save("https://two.example", "Two", null, null);
            var store = new HistorySearchPreferences(path);
            var day = DateOnly.FromDateTime(Now.LocalDateTime);
            var dates = new HistoryDateRange(day.AddDays(-2), day, TimeSpan.FromHours(3));

            Check(HistorySearchPreferences.ChooseRecorder([first, second], null, second.Id, first.Endpoint)?.Id == second.Id,
                "A fresh History selection should prefer the active Live recorder.");
            store.LastRecorder = first.Id;
            store.SaveFilters(first.Id, new(dates, Talkgroups: [new("101-SYS", "Dispatch"), new("202-SYS", "Fire")]));
            store.SaveFilters(second.Id, new(dates with { StartTime = TimeSpan.FromHours(9) }, Site: new("3-SYS", "East")));

            var edited = connections.Save("https://one.example:8443/new", "Renamed One", null, first.Id);
            var reopened = new HistorySearchPreferences(path);
            var savedFirst = reopened.ReadFilters(edited.Id)!;
            var savedSecond = reopened.ReadFilters(second.Id)!;
            Check(edited.Id == first.Id &&
                HistorySearchPreferences.ChooseRecorder([second, edited], reopened.LastRecorder, second.Id, second.Endpoint) == edited &&
                savedFirst.Dates == dates && savedFirst.Site == null && savedFirst.Talkgroups?.Length == 2 &&
                savedFirst.ForRecorder(first.Id).Talkgroups.All(item => item.RecorderId == first.Id) &&
                savedSecond.Dates.StartTime == TimeSpan.FromHours(9) && savedSecond.Talkgroups == null &&
                savedSecond.ForRecorder(second.Id).Site is { Value: "3-SYS" } site && site.RecorderId == second.Id,
                "Recorder filters, date and time, and the last History recorder must survive reopen and address editing.");
            Check(HistorySearchPreferences.ChooseRecorder([second], first.Id, null, null) == second,
                "A deleted recorder cannot remain the selected History recorder.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
        return Task.CompletedTask;
    }

    internal static async Task ScopedHistoryAndNativeTalkgroupFilter()
    {
        using var fixture = new Fixture();
        var first = new HistoryRecorder(Guid.NewGuid(), "https://one.example/archive");
        var second = new HistoryRecorder(Guid.NewGuid(), "https://two.example/archive");
        fixture.Settings.ServerUrl = second.Endpoint;
        fixture.Settings.RecorderCredentials[(first.Id, first.Endpoint)] = ("first-test", "one");
        fixture.Settings.RecorderCredentials[(second.Id, second.Endpoint)] = ("second-test", "two");
        var firstGets = 0;
        var secondGets = 0;
        var searches = new List<(string Host, HistoryRequest Filter)>();
        using var http = fixture.CreateClient(new AsyncStubHandler(async (request, token) =>
        {
            var onFirst = request.RequestUri!.Host == "one.example";
            var recorder = onFirst ? first : second;
            Check(request.Options.TryGetValue(ServerAuthenticationHandler.Recorder, out var scope) &&
                scope.ServerId == recorder.Id && request.RequestUri.AbsolutePath.StartsWith("/archive/") &&
                HistoryBasic(onFirst ? "first-test:one" : "second-test:two").Equals(request.Headers.Authorization),
                "The History request escaped its recorder path or used the Live recorder's credentials.");
            if (request.Method == HttpMethod.Get)
            {
                if (onFirst) firstGets++; else secondGets++;
                var entries = request.RequestUri.AbsolutePath.EndsWith("talkgroupsjson")
                    ? new[] { new { id = "101-SYS", text = onFirst ? "Dispatch A" : "Dispatch B" },
                        new { id = "202-SYS", text = "Fire" } }
                    : [new { id = "3-SYS", text = "East" }];
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { results = entries }) };
            }

            var filter = await ReadHistoryRequest(request, token);
            searches.Add((request.RequestUri.Host, filter));
            return HistoryReply(filter, 1, [Row("same-recording", Now.AddMinutes(-1), "same.mp3")]);
        }));
        var subscription = new ScriptSubscription(false, 0, (ISettingsService)fixture.Settings);
        var history = new CallHistoryService((ISettingsService)fixture.Settings, new RecordedCalls(), new ScopedHistoryLookups(), subscription, http,
            new FixedClock(Now));

        var one = await history.GetRecorderLookupsAsync(first, refresh: false);
        var two = await history.GetRecorderLookupsAsync(second, refresh: false);
        _ = await history.GetRecorderLookupsAsync(first, refresh: false);
        Check(firstGets == 3 && secondGets == 3 && searches.Count == 0, "Selecting a recorder should fetch its own lookups without searching.");
        Check(one.Talkgroups.Any(item => item.Label == "Dispatch A") &&
            two.Talkgroups.Any(item => item.Label == "Dispatch B") && one.Talkgroups.All(item => item.RecorderId == first.Id) &&
            two.Talkgroups.All(item => item.RecorderId == second.Id), "The cached lookup catalogs were mixed.");

        var dates = ArchiveDates();
        await HistoryThrows<ArgumentException>(() => history.SearchAsync(first, new(), dates));
        await HistoryThrows<ArgumentException>(() => history.SearchAsync(first, new() { Site = one.Sites.First() }, dates));
        await HistoryThrows<ArgumentException>(() => history.SearchAsync(first,
            new() { Site = one.Sites.Last(), Talkgroups = [two.Talkgroups.Last()] }, dates));
        Check(searches.Count == 0, "Empty, All, or cross-recorder filters must fail before POST /calljson.");

        var a = await history.SearchAsync(first, new() { Talkgroups = one.Talkgroups.Where(item => item.Value.Length != 0).ToArray() }, dates);
        var b = await history.SearchAsync(second, new() { Site = two.Sites.Last() }, dates);
        Check(searches.Count == 2 && searches[0] is ("one.example", { Target: "101-SYS,202-SYS", Regex: false }) &&
            searches[1] is ("two.example", { Target: "", Site: "3-SYS" }) && subscription.Checks == 0,
            "Talkgroups need one native CSV request, and independent recorders must use only their own authorization.");
        Check(a.Calls.Single() is { BackendId: "same-recording" } firstCall && firstCall.ServerId == first.Id &&
            firstCall.RecordingServer == first.Endpoint &&
            b.Calls.Single() is { BackendId: "same-recording" } secondCall && secondCall.ServerId == second.Id &&
            secondCall.RecordingServer == second.Endpoint, "Equal recording IDs must remain separate across recorder scopes.");
    }

    internal static async Task HistorySnapshotPaging()
    {
        using var fixture = new Fixture();
        var clock = new FixedClock(Now);
        var recorder = new HistoryRecorder(Guid.NewGuid(), "https://archive.example");
        var archive = Enumerable.Range(1, 357).Reverse()
            .Select(index => ($"row_{index}", Now.AddSeconds(index / 3 - 500))).ToList();
        var expected = archive.Select(row => row.Item1).ToArray();
        archive.Insert(197, archive[196]);
        var requests = new List<HistoryRequest>();
        using var http = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var page = await ReadHistoryRequest(request, token);
            requests.Add(page);
            var eligible = archive.Where(row => row.Item2 >= page.From && row.Item2 < page.Until).ToArray();
            return HistoryReply(page, eligible.Length, eligible.Skip(page.Offset).Take(Math.Min(page.Length, 200))
                .Select(row => Row(row.Item1, row.Item2, "audio.mp3")).ToArray());
        }));
        var history = new CallHistoryService((ISettingsService)fixture.Settings, new RecordedCalls(), new NoHistoryLookups(),
            new ScriptSubscription(false, null), http, clock);

        var initial = await history.SearchAsync(recorder, SiteFilter(recorder), ArchiveDates());
        Check(initial.HasMore && initial.Calls.Select(call => call.BackendId).SequenceEqual(expected.Take(250)) &&
            requests.Take(2).Select(page => (page.Offset, page.Length)).SequenceEqual([(0, 200), (199, 52)]),
            "The first 250 distinct calls must fill across the native 200-record limit and a duplicate.");
        archive.Insert(0, ("incoming", Now));
        clock.UtcNow = Now.AddMinutes(5);
        var more = await history.LoadMoreAsync(initial);
        var last = await history.LoadMoreAsync(more);
        Check(more.HasMore && more.Calls.Select(call => call.BackendId).SequenceEqual(expected.Skip(250).Take(100)) &&
            !last.HasMore && last.Calls.Select(call => call.BackendId).SequenceEqual(expected.Skip(350)) &&
            requests.All(page => page.Length <= 200 && page.Until == Now && page.From == requests[0].From),
            "Loading 100 older calls and the final partial page must reuse the snapshot without gaps or duplicates.");

        var visible = new ObservableCollection<CallItem>(initial.Calls);
        var firstVisible = visible[83];
        var selected = visible[102];
        var actions = new List<NotifyCollectionChangedAction>();
        visible.CollectionChanged += (_, change) => actions.Add(change.Action);
        HistoryResultOrder.Add(visible, more.Calls);
        Check(visible.Count == 350 && ReferenceEquals(visible[83], firstVisible) &&
            ReferenceEquals(visible[102], selected) && actions.Count == 100 &&
            actions.All(action => action == NotifyCollectionChangedAction.Add),
            "Older pages must append without resetting the visible card objects or the current selection.");
    }

    internal static async Task HistoryPageRejectsArchiveDrift()
    {
        using var fixture = new Fixture();
        var recorder = new HistoryRecorder(Guid.NewGuid(), "https://archive.example");
        var archive = Enumerable.Range(1, 260).Reverse().ToList();
        using var http = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var page = await ReadHistoryRequest(request, token);
            return HistoryReply(page, archive.Count, archive.Skip(page.Offset).Take(page.Length)
                .Select(id => Row($"row_{id}", Now.AddSeconds(id - 300), "audio.mp3")).ToArray());
        }));
        var history = new CallHistoryService((ISettingsService)fixture.Settings, new RecordedCalls(), new NoHistoryLookups(),
            new ScriptSubscription(false, null), http, new FixedClock(Now));
        var initial = await history.SearchAsync(recorder, SiteFilter(recorder), ArchiveDates());

        // Simulate a recording inserted before the cursor and one purged later
        // in the archive, keeping recordsFiltered identical.
        archive.Insert(0, 261);
        archive.RemoveAt(archive.Count - 1);
        await HistoryThrows<HistoryResultsChangedException>(() => history.LoadMoreAsync(initial));
        Check(initial.Calls.Count == 250 && initial.Calls[0].BackendId == "row_260",
            "Archive movement must preserve the displayed snapshot and require a fresh search.");
    }

    internal static async Task LateHistorySearchCancellation()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new HistoryRecorder(Guid.NewGuid(), "https://one.example");
        var second = new HistoryRecorder(Guid.NewGuid(), "https://two.example");
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var page = await ReadHistoryRequest(request, token);
            var old = request.RequestUri!.Host == "one.example";
            if (old)
            {
                oldStarted.SetResult();
                await oldRelease.Task.WaitAsync(deadline.Token);
            }
            return HistoryReply(page, 1, [Row(old ? "old" : "new", Now.AddSeconds(-5), "audio.mp3")]);
        }));
        var cache = new RecordedCalls();
        var history = new CallHistoryService((ISettingsService)fixture.Settings, cache, new NoHistoryLookups(),
            new ScriptSubscription(false, null), http, new FixedClock(Now));
        var requests = new LivePlaybackRequests();
        using var canceled = requests.Begin(false, deadline.Token);
        var retired = history.SearchAsync(first, SiteFilter(first), ArchiveDates(), canceled.Token);
        await oldStarted.Task.WaitAsync(deadline.Token);
        using var active = requests.Begin(false, deadline.Token);
        var current = await history.SearchAsync(second, SiteFilter(second), ArchiveDates(), active.Token);
        oldRelease.SetResult();
        await HistoryThrows<OperationCanceledException>(async () => await retired.WaitAsync(deadline.Token));
        Check(!canceled.CanContinue && active.CanContinue && current.Calls.Single().BackendId == "new" &&
            !cache.Ids.Contains("old"), "A transport returning after cancellation cannot publish or persist retired results.");
    }

    private static HistoryDateRange ArchiveDates()
    {
        var day = DateOnly.FromDateTime(Now.LocalDateTime);
        return new(day.AddDays(-1), day, TimeSpan.Zero);
    }

    private static HistorySearchFilters SiteFilter(HistoryRecorder recorder) =>
        new() { Site = new HistoryLookupItem { Value = "3-SYS", RecorderId = recorder.Id } };

    private static async Task<T> HistoryThrows<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task<HistoryRequest> ReadHistoryRequest(HttpRequestMessage request, CancellationToken token)
    {
        Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/calljson") &&
            request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded", "Wrong native History request.");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var body = json.RootElement;
        var columns = body.GetProperty("columns").EnumerateArray().ToDictionary(col => col.GetProperty("name").GetString()!,
            col => col.GetProperty("search"));
        string Filter(string name) => columns[name].GetProperty("value").GetString()!;
        var bounds = Filter("Date").Split('|').Select(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new(body.GetProperty("draw").GetInt32(), body.GetProperty("start").GetInt32(), body.GetProperty("length").GetInt32(),
            bounds[0], bounds[1], Filter("Target"), columns["Target"].GetProperty("regex").GetBoolean(), Filter("Site"));
    }

    private static AuthenticationHeaderValue HistoryBasic(string pair) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));

    private static HttpResponseMessage HistoryReply(HistoryRequest request, int total, object[] rows) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { draw = request.Draw, recordsFiltered = total, data = rows }) };

    private sealed record HistoryRequest(int Draw, int Offset, int Length, DateTimeOffset From, DateTimeOffset Until, string Target,
        bool Regex, string Site);
    private sealed record HistoryDatabase(string DbPath) : IDatabasePathProvider;
    private sealed class ScopedHistoryLookups : IHistoryLookupsRepository
    {
        private readonly Dictionary<string, (HistoryLookupData? Data, DateTime? FetchedUtc)> _entries = [];
        public Task<(HistoryLookupData?, DateTime?)> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));
        public Task UpsertAsync(string key, HistoryLookupData data, DateTime fetchedUtc, CancellationToken token = default)
        {
            _entries[key] = (data, fetchedUtc);
            return Task.CompletedTask;
        }
    }
}
