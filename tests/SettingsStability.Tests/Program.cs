using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

var filters = FilterService.Instance;
RepeatedCallsDoNotInvalidateTheCatalog();
HierarchyAndExplicitChangesStayEffective();
await DiscoveryContinuesWhileStorageIsBlocked();
await FirstOpenReusesOnlyMatchingSuccessfulPreparation();
await FilterProfileSchemaTests.RunAsync();

static async Task FirstOpenReusesOnlyMatchingSuccessfulPreparation()
{
    var preparation = new SettingsStartupPreparation<int>();
    var readCount = 0;
    var pending = new TaskCompletionSource<int>();
    Task<int> StartRead(string _)
    {
        readCount++;
        return pending.Task;
    }

    var warmup = preparation.GetOrStart("https://first.test", StartRead);
    var firstOpen = preparation.GetOrStart("https://FIRST.test", StartRead);
    Check(ReferenceEquals(warmup, firstOpen) && readCount == 1, "Opening Settings started a duplicate local read.");
    pending.SetResult(1);
    Check(await firstOpen == 1 && ReferenceEquals(warmup, preparation.GetOrStart("https://first.test", StartRead)),
        "The completed preparation was not reused.");

    pending = new TaskCompletionSource<int>();
    var otherServer = preparation.GetOrStart("https://second.test", StartRead);
    preparation.Release(warmup);
    Check(ReferenceEquals(otherServer, preparation.GetOrStart("https://second.test", StartRead)),
        "Releasing old data removed the newer preparation.");
    pending.SetCanceled();
    Check(otherServer.IsCanceled && readCount == 2, "The changed server incorrectly reused prepared data.");
    var retry = preparation.GetOrStart("https://second.test", _ => Task.FromResult(2));
    Check(await retry == 2 && !ReferenceEquals(otherServer, retry), "A canceled preparation prevented a retry.");
    preparation.Release(retry);
    Check(await preparation.GetOrStart("https://second.test", _ => Task.FromResult(3)) == 3,
        "Consumed startup data was kept past its first use.");
    Console.WriteLine("PASS First Settings open shares preparation, isolates server changes and retries cancellation");
}

void RepeatedCallsDoNotInvalidateTheCatalog()
{
    filters.SetServerUrl("https://metadata.test");
    var catalogChanges = 0;
    var ruleChanges = 0;
    EventHandler catalogChanged = (_, _) => catalogChanges++;
    EventHandler rulesChanged = (_, _) => ruleChanges++;
    filters.CatalogChanged += catalogChanged;
    filters.RulesChanged += rulesChanged;
    try
    {
        var call = new CallItem("Receiver", "Site", "Talkgroup");
        filters.EnsureRulesForCall(call);
        for (var i = 0; i < 10_000; i++)
            filters.EnsureRulesForCall(call);

        Check(catalogChanges == 1, "Repeated calls triggered another catalog publication.");
        Check(ruleChanges == 0, "Discovery without configured filters caused live queue invalidation.");
        Check(filters.GetCatalogSnapshot().Count == 3, "Expected one row for each hierarchy level.");
        Console.WriteLine("PASS Repeated live calls only update discovery metadata");
    }
    finally
    {
        filters.CatalogChanged -= catalogChanged;
        filters.RulesChanged -= rulesChanged;
    }
}

void HierarchyAndExplicitChangesStayEffective()
{
    filters.SetServerUrl("https://hierarchy.test");
    var call = new CallItem("Receiver", "Site", "Talkgroup");
    filters.EnsureRulesForCall(call);
    var receiver = new FilterRuleStateRecord { Level = FilterLevel.Receiver, Receiver = "Receiver", IsDisabled = true };
    var talkgroup = new FilterRuleStateRecord
    {
        Level = FilterLevel.Talkgroup, Receiver = "Receiver", Site = "Site", Talkgroup = "Talkgroup", IsMuted = true
    };
    filters.ApplyStateRecords([receiver], resetOthers: true);
    Check(filters.ShouldHide(call) && filters.ShouldMute(call), "Receiver disable did not propagate.");
    filters.ApplyStateRecords([talkgroup], resetOthers: false);
    Check(!filters.ShouldHide(call) && filters.ShouldMute(call), "Talkgroup state did not override inherited flags.");
    Check(filters.GetCatalogSnapshot().Last().IsMuted, "Catalog and playback filter decisions disagree.");

    // The row can still be on screen while the asynchronous catalog refresh runs.
    var row = new FilterRule { Level = FilterLevel.Talkgroup, Receiver = "Receiver", Site = "Site", Talkgroup = "Talkgroup" };
    filters.ToggleMute(row);
    filters.ToggleMute(row);
    Check(!filters.ShouldHide(call) && filters.ShouldMute(call), "Rapid toggles used stale display state.");
    filters.ClearRule(row);
    Check(!filters.ShouldHide(call) && !filters.ShouldMute(call), "Clear failed to remove inherited state.");
    Console.WriteLine("PASS Filter inheritance, overrides, rapid edits and clear");
}

async Task DiscoveryContinuesWhileStorageIsBlocked()
{
    const string origin = "https://blocked-storage.test";
    const string discoveryKey = "filter_discovery_v2::" + origin;
    filters.SetServerUrl(origin);
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    AppStateStore.BeforeWrite = key =>
    {
        if (key != discoveryKey)
            return;

        entered.TrySetResult();
        release.Wait();
    };

    try
    {
        filters.EnsureRulesForCall(new CallItem("Receiver", "Site", "Talkgroup 1"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(() =>
        {
            for (var i = 2; i <= 1_000; i++)
                filters.EnsureRulesForCall(new CallItem("Receiver", "Site", $"Talkgroup {i}"));

            filters.SetServerUrl("https://other.test");
            Check(filters.GetCatalogSnapshot().Count == 0, "Discovery leaked to another server.");
            filters.SeedFromObservedTriples(
                [new ObservedTriple { ReceiverLabel = "Old", SiteLabel = "Site", TalkgroupLabel = "Talkgroup" }], origin);
            Check(filters.GetCatalogSnapshot().Count == 0, "A late seed was applied after changing servers.");
            filters.SetServerUrl(origin);
            Check(filters.GetCatalogSnapshot().Count == 1_002, "Switching back lost pending discovery.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally
    {
        AppStateStore.BeforeWrite = null;
        release.Set();
    }

    Check(SpinWait.SpinUntil(() =>
    {
        var json = AppStateStore.GetString(discoveryKey, "[]");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetArrayLength() == 1_000;
    }, TimeSpan.FromSeconds(5)), "Latest discovery snapshot did not persist after storage resumed.");
    filters.SetServerUrl("https://other.test");
    filters.SetServerUrl(origin);
    Check(filters.GetCatalogSnapshot().Count == 1_002, "Persisted discovery could not be reopened.");
    Console.WriteLine("PASS Discovery and server isolation while storage is blocked");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
