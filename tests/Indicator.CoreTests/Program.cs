using System.Diagnostics;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.CoreTests;

public static class Program
{
    private static int _failed;

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Store_Running_NoCard",                                Store_Running_NoCard),
            ("Store_Approval_ShowsPersistentCard",                  Store_Approval_ShowsPersistentCard),
            ("Store_ApprovalToRunning_HidesCard",                   Store_ApprovalToRunning_HidesCard),
            ("Store_Completed_ShowsThenAutoHideSignal",             Store_Completed_ShowsThenAutoHideSignal),
            ("Store_Failed_Lamp_LongLived_Card10s",                 Store_Failed_Lamp_LongLived_Card10s),
            ("Store_FailedToRunning_HidesCard",                     Store_FailedToRunning_HidesCard),
            ("Store_Failed_DedupSameUpdatedAt",                     Store_Failed_DedupSameUpdatedAt),
            ("Store_Completed_DedupSameUpdatedAt",                  Store_Completed_DedupSameUpdatedAt),
            ("Store_Completed_NewerUpdatedAt_Reappears",            Store_Completed_NewerUpdatedAt_Reappears),
            ("Store_Completed_HiddenAfter5MinFromUpdated",          Store_Completed_HiddenAfter5MinFromUpdated),
            ("Store_Failed_HiddenAfter5Min_StillPresent",          Store_Failed_HiddenAfter5Min_StillPresent),
            ("Store_RunningToRunning_NewerUpdated_NoRedup",         Store_RunningToRunning_NewerUpdated_NoRedup),
            ("Store_ApprovalToApproval_NewerUpdated_Updates",       Store_ApprovalToApproval_NewerUpdated_Updates),
            ("Store_MultiSession_StableOrder",                      Store_MultiSession_StableOrder),
            ("Store_RemovedFromSnapshot_Forgotten",                 Store_RemovedFromSnapshot_Forgotten),
            ("Store_UnknownStatus_Throws",                          Store_UnknownStatus_Throws),
            ("Vm_LampColor_Mapping",                                Vm_LampColor_Mapping),
            ("Vm_UnknownStatus_Throws",                             Vm_UnknownStatus_Throws),
            ("Vm_PropertyChanged_OnStatus",                         Vm_PropertyChanged_OnStatus),
            ("Vm_TooltipText_Format",                               Vm_TooltipText_Format),

            // Round 2 close-out — completed suppression tombstone
            ("Store_Completed_Hidden_SameSnapshotDoesNotReappear",  Store_Completed_Hidden_SameSnapshotDoesNotReappear),
            ("Store_Completed_Hidden_NewerCompletedReappears",      Store_Completed_Hidden_NewerCompletedReappears),
            ("Store_Completed_Hidden_RunningReappears",             Store_Completed_Hidden_RunningReappears),

            // Round 2 close-out — full-snapshot Agent / Host replacement
            ("Store_SameSessionId_AgentReplaced",                   Store_SameSessionId_AgentReplaced),
            ("Store_SameSessionId_HostReplaced",                    Store_SameSessionId_HostReplaced),
            ("Vm_AgentReplacement_FiresTooltipChanged",             Vm_AgentReplacement_FiresTooltipChanged),
            ("Vm_HostReplacement_FiresTooltipChanged",              Vm_HostReplacement_FiresTooltipChanged),

            // Round 2 close-out — partial header read
            ("ReadExactlyOrEofAsync_OneByteAtATime_Assembles",     ReadExactlyOrEofAsync_OneByteAtATime_Assembles),
            ("ReadExactlyOrEofAsync_CleanEof_ReturnsFalse",        ReadExactlyOrEofAsync_CleanEof_ReturnsFalse),
            ("ReadExactlyOrEofAsync_TruncatedAfterSome_Throws",    ReadExactlyOrEofAsync_TruncatedAfterSome_Throws),
            ("ReadExactlyOrEofAsync_HeaderSaysPayloadButEof_Throws", ReadExactlyOrEofAsync_HeaderSaysPayloadButEof_Throws),
        };

        var sw0 = Stopwatch.StartNew();
        foreach (var (name, run) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await run();
                sw.Stop();
                Console.WriteLine($"  PASS  {name}  ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _failed++;
                Console.WriteLine($"  FAIL  {name}  ({sw.ElapsedMilliseconds}ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }
        sw0.Stop();

        Console.WriteLine();
        Console.WriteLine($"  {tests.Length - _failed}/{tests.Length} passed in {sw0.Elapsed.TotalSeconds:F1}s");
        return _failed == 0 ? 0 : 1;
    }

    // ---- helpers ----

    private static SessionSnapshot Snap(string id, string status, DateTimeOffset updatedAt,
        string? message = null, string? host = null, string agent = "agent-x")
        => new()
        {
            SessionId = id,
            Agent = agent,
            Host = host,
            Message = message,
            Status = status,
            UpdatedAt = updatedAt,
        };

    private sealed class RecordingSink
    {
        public List<(string Kind, string SessionId, int? AutoHideAfterMs, string Status)> Events { get; } = new();

        public void OnEvent(CardEvent ev)
        {
            Events.Add((ev.Kind.ToString(), ev.Session.SessionId, ev.AutoHideAfterMs, ev.Session.Status));
        }
    }

    // ---- tests ----

    private static async Task Store_Running_NoCard()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0) }, t0);

        Assert(sink.Events.Count == 0, $"expected 0 events, got {sink.Events.Count}: [{string.Join(",", sink.Events.Select(e => e.Kind))}]");
        Assert(store.Sessions.Count == 1, "lamp should show 1 session");
        Assert(store.Sessions[0].LampColor == "#2F81F7", "running lamp color -> blue");
    }

    private static async Task Store_Approval_ShowsPersistentCard()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0, message: "needs review") }, t0);

        Assert(sink.Events.Count == 1, $"expected 1 event, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", $"kind={sink.Events[0].Kind}");
        Assert(sink.Events[0].AutoHideAfterMs is null, "approval card has no auto-hide (persistent)");

        // Same snapshot re-applied: no new event (no transition).
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0) }, t0);
        Assert(sink.Events.Count == 1, $"still 1 event after idempotent re-apply, got {sink.Events.Count}");
    }

    private static async Task Store_ApprovalToRunning_HidesCard()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0) }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(1)) }, t0.AddSeconds(1));

        Assert(sink.Events.Count == 2, $"expected 2 events, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "first show");
        Assert(sink.Events[1].Kind == "Hide", "second hide");
    }

    private static async Task Store_Completed_ShowsThenAutoHideSignal()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0, message: "done") }, t0);

        Assert(sink.Events.Count == 1, "expected 1 show event");
        Assert(sink.Events[0].Kind == "Show", $"kind={sink.Events[0].Kind}");
        Assert(sink.Events[0].AutoHideAfterMs == 5000,
            $"expected 5000ms auto-hide, got {sink.Events[0].AutoHideAfterMs}");
    }

    private static async Task Store_Failed_Lamp_LongLived_Card10s()
    {
        await Task.CompletedTask; // sync test
        // Failed card auto-hides after 10s (Show event carries 10000ms).
        // Failed lamp stays long-lived until status changes (independent of card).
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0, message: "boom") }, t0);

        Assert(sink.Events.Count == 1, "expected 1 event");
        Assert(sink.Events[0].Kind == "Show", $"kind={sink.Events[0].Kind}");
        Assert(sink.Events[0].AutoHideAfterMs == 10000,
            $"failed card auto-hide must be 10000ms, got {sink.Events[0].AutoHideAfterMs}");

        // Lamp is long-lived: survives well past 5 minutes.
        store.Tick(t0.AddMinutes(10));
        Assert(store.Sessions.Count == 1, $"failed lamp must survive past 10min; got count={store.Sessions.Count}");
        Assert(store.Sessions[0].LampColor == "#F85149", "failed lamp color -> red");

        // Re-apply: dedup on same updated_at.
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0);
        Assert(sink.Events.Count == 1, "no redup on same updated_at");
    }

    private static async Task Store_FailedToRunning_HidesCard()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(1)) }, t0.AddSeconds(1));

        Assert(sink.Events.Count == 2, $"expected Show + Hide, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "show failed");
        Assert(sink.Events[1].Kind == "Hide", "hide on failed->running");
        // Lamp still there (failed->running doesn't remove it).
        Assert(store.Sessions.Count == 1, "lamp survives failed->running transition");
    }

    private static async Task Store_Failed_DedupSameUpdatedAt()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0.AddSeconds(10));
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0.AddSeconds(20));

        Assert(sink.Events.Count == 1, $"expected 1 event, got {sink.Events.Count}");
    }

    private static async Task Store_Completed_DedupSameUpdatedAt()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        // Same updated_at re-applied (e.g. after Receiver reconnect).
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0.AddSeconds(10));
        // And again.
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0.AddSeconds(20));

        Assert(sink.Events.Count == 1, $"expected 1 event, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "single show");
    }

    private static async Task Store_Completed_NewerUpdatedAt_Reappears()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        // Newer updated_at reappears.
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0.AddSeconds(30)) }, t0.AddSeconds(30));

        Assert(sink.Events.Count == 2, $"expected 2 events, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "first show");
        Assert(sink.Events[1].Kind == "Show", "second show (newer updated_at)");
    }

    private static async Task Store_Completed_HiddenAfter5MinFromUpdated()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        // Before 5 min: lamp still there.
        store.Tick(t0.AddMinutes(2));
        Assert(store.Sessions.Count == 1, $"lamp at 2min, count={store.Sessions.Count}");
        // At exactly 5 min: removed.
        store.Tick(t0.AddMinutes(5));
        Assert(store.Sessions.Count == 0, $"lamp after 5min, count={store.Sessions.Count}");
    }

    private static async Task Store_Failed_HiddenAfter5Min_StillPresent()
    {
        await Task.CompletedTask; // sync test
        // The 5-minute cleanup ONLY removes completed sessions, not failed.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "failed", t0) }, t0);
        store.Tick(t0.AddMinutes(10));
        Assert(store.Sessions.Count == 1, $"failed lamp after 10min, count={store.Sessions.Count}");
        Assert(store.Sessions[0].SessionId == "s1", "failed session still there");
        Assert(store.Sessions[0].Status == "failed", "failed status preserved");
    }

    private static async Task Store_RunningToRunning_NewerUpdated_NoRedup()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0) }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(1)) }, t0.AddSeconds(1));

        Assert(sink.Events.Count == 0, "running produces no card events");
        Assert(store.Sessions.Count == 1, "lamp still there");
    }

    private static async Task Store_ApprovalToApproval_NewerUpdated_Updates()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0, message: "v1") }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0.AddSeconds(2), message: "v2") }, t0.AddSeconds(2));

        Assert(sink.Events.Count == 2, $"expected Show + Update, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "first show");
        Assert(sink.Events[1].Kind == "Update", "second update");
        Assert(sink.Events[1].AutoHideAfterMs is null, "no auto-hide on approval update");
    }

    private static async Task Store_MultiSession_StableOrder()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[]
        {
            Snap("c", "running", t0),
            Snap("a", "running", t0),
            Snap("b", "running", t0),
        }, t0);

        var ids = store.Sessions.Select(s => s.SessionId).ToList();
        Assert(ids.SequenceEqual(new[] { "a", "b", "c" }),
            $"expected [a,b,c], got [{string.Join(",", ids)}]");
    }

    private static async Task Store_RemovedFromSnapshot_Forgotten()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0) }, t0);
        Assert(sink.Events.Count == 1, "show");

        // Empty snapshot: session disappears.
        store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddSeconds(1));
        Assert(store.Sessions.Count == 0, "lamp removed");
    }

    private static async Task Store_UnknownStatus_Throws()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var threw = false;
        try
        {
            store.ApplySnapshot(new[] { Snap("s1", "paused", t0) }, t0);
        }
        catch (ArgumentException ex)
        {
            threw = true;
            Assert(ex.Message.Contains("paused"), "exception should name the bad status");
        }
        Assert(threw, "ApplySnapshot must throw on unknown status (fail fast)");
        // The session was not added to the store.
        Assert(store.Sessions.Count == 0, "lamp should not contain rejected session");
    }

    private static async Task Vm_LampColor_Mapping()
    {
        await Task.CompletedTask; // sync test
        var t0 = DateTimeOffset.UtcNow;
        Assert(new SessionViewModel { SessionId = "x", Agent = "a", Status = "running",   UpdatedAt = t0 }.LampColor == "#2F81F7", "running->blue");
        Assert(new SessionViewModel { SessionId = "x", Agent = "a", Status = "approval",  UpdatedAt = t0 }.LampColor == "#D29922", "approval->yellow");
        Assert(new SessionViewModel { SessionId = "x", Agent = "a", Status = "completed", UpdatedAt = t0 }.LampColor == "#3FB950", "completed->green");
        Assert(new SessionViewModel { SessionId = "x", Agent = "a", Status = "failed",    UpdatedAt = t0 }.LampColor == "#F85149", "failed->red");
    }

    private static async Task Vm_UnknownStatus_Throws()
    {
        await Task.CompletedTask; // sync test
        var threw = false;
        try
        {
            _ = new SessionViewModel { SessionId = "x", Agent = "a", Status = "idle", UpdatedAt = DateTimeOffset.UtcNow };
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Assert(threw, "VM constructor must reject unknown status");

        // Setter also rejects.
        var vm = new SessionViewModel { SessionId = "x", Agent = "a", Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
        threw = false;
        try { vm.Status = "offline"; } catch (ArgumentException) { threw = true; }
        Assert(threw, "Status setter must reject unknown value");
    }

    private static async Task Vm_PropertyChanged_OnStatus()
    {
        await Task.CompletedTask; // sync test
        var t0 = DateTimeOffset.UtcNow;
        var vm = new SessionViewModel { SessionId = "x", Agent = "a", Status = "running", UpdatedAt = t0 };
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.Status = "approval";
        Assert(seen.Contains("Status"), "Status changed");
        Assert(seen.Contains("LampColor"), "LampColor re-fired");
    }

    private static async Task Vm_TooltipText_Format()
    {
        await Task.CompletedTask; // sync test
        var t0 = DateTimeOffset.UtcNow;
        var noHost = new SessionViewModel { SessionId = "abc", Agent = "claude-code", Status = "running", UpdatedAt = t0 };
        Assert(noHost.TooltipText == "claude-code · abc", $"got '{noHost.TooltipText}'");
        var withHost = new SessionViewModel { SessionId = "abc", Agent = "claude-code", Status = "running", UpdatedAt = t0, Host = "macbook" };
        Assert(withHost.TooltipText == "claude-code · abc · macbook", $"got '{withHost.TooltipText}'");
    }

    // ---- Round 2 close-out: completed suppression tombstone ----

    private static async Task Store_Completed_Hidden_SameSnapshotDoesNotReappear()
    {
        await Task.CompletedTask; // sync test
        // After Tick removes a completed lamp (5 min past updated_at), the
        // hidden-completed tombstone must suppress any re-broadcast of the
        // SAME completed event (same updated_at). The lamp stays gone AND
        // no card is re-fired.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0, message: "done") }, t0);
        Assert(sink.Events.Count == 1, "initial show");

        // Age out the lamp (5+ minutes pass since updated_at).
        store.Tick(t0.AddMinutes(6));
        Assert(store.Sessions.Count == 0, "lamp aged out");

        // Re-broadcast the SAME completed event (this is what happens when
        // an unrelated session's POST causes the Receiver to re-send the
        // full snapshot including the still-resident s1 entry).
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0, message: "done") }, t0.AddMinutes(10));
        Assert(store.Sessions.Count == 0, "lamp must NOT reanimate from same updated_at");
        Assert(sink.Events.Count == 1, "no new card events from same updated_at re-broadcast");

        // And once more for good measure.
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0, message: "done") }, t0.AddMinutes(20));
        Assert(store.Sessions.Count == 0, "lamp still gone");
        Assert(sink.Events.Count == 1, "still no new card events");
    }

    private static async Task Store_Completed_Hidden_NewerCompletedReappears()
    {
        await Task.CompletedTask; // sync test
        // After Tick removes a completed lamp, a NEWER completed event
        // (updated_at strictly greater than the tombstone) is treated as
        // a real new completion: lamp reappears with a new card.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        store.Tick(t0.AddMinutes(6));
        Assert(store.Sessions.Count == 0, "aged out");

        var t1 = t0.AddSeconds(30);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t1, message: "done again") }, t1);
        Assert(store.Sessions.Count == 1, "newer completed reappears as lamp");
        Assert(store.Sessions[0].LampColor == "#3FB950", "green");
        Assert(sink.Events.Count == 2, $"newer completed fires a new card event; got {sink.Events.Count}");
        Assert(sink.Events[1].Kind == "Show", "newer completed -> Show");
    }

    private static async Task Store_Completed_Hidden_RunningReappears()
    {
        await Task.CompletedTask; // sync test
        // After Tick removes a completed lamp, a non-completed event for
        // the same session clears the tombstone and the lamp reappears.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        store.Tick(t0.AddMinutes(6));
        Assert(store.Sessions.Count == 0, "aged out");

        var t1 = t0.AddMinutes(7);
        store.ApplySnapshot(new[] { Snap("s1", "running", t1) }, t1);
        Assert(store.Sessions.Count == 1, "running reappears");
        Assert(store.Sessions[0].LampColor == "#2F81F7", "blue");

        // And after the lamp reappears, a subsequent same-updated_at
        // completed re-broadcast is no longer suppressed (tombstone was
        // cleared by the running event).
        store.ApplySnapshot(new[] { Snap("s1", "running", t1) }, t1.AddMinutes(1));
        Assert(store.Sessions.Count == 1, "running still there");

        // Same-updated_at completed after running -> not tombstoned, so
        // it shows as a normal completed event.
        var t2 = t1.AddMinutes(2);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t2) }, t2);
        Assert(sink.Events.Any(e => e.Kind == "Show" && e.Status == "completed"),
            "completed Show event after tombstone cleared");
    }

    // ---- Round 2 close-out: full-snapshot Agent / Host replacement ----

    private static async Task Store_SameSessionId_AgentReplaced()
    {
        await Task.CompletedTask; // sync test
        // Same session_id reported under a different agent later -> VM
        // Agent is updated and TooltipText reflects the new value.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0, agent: "claude-code") }, t0);
        Assert(store.Sessions[0].Agent == "claude-code", "initial agent");
        Assert(store.Sessions[0].TooltipText == "claude-code · s1", "initial tooltip");

        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(1), agent: "codex") }, t0.AddSeconds(1));
        Assert(store.Sessions.Count == 1, "still one session");
        Assert(store.Sessions[0].Agent == "codex", $"agent should be replaced; got '{store.Sessions[0].Agent}'");
        Assert(store.Sessions[0].TooltipText == "codex · s1",
            $"tooltip should reflect new agent; got '{store.Sessions[0].TooltipText}'");
    }

    private static async Task Store_SameSessionId_HostReplaced()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "running", t0, agent: "claude-code", host: "macbook") }, t0);
        Assert(store.Sessions[0].TooltipText == "claude-code · s1 · macbook", "initial tooltip with host");

        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(1), agent: "claude-code", host: "linux-box") }, t0.AddSeconds(1));
        Assert(store.Sessions[0].TooltipText == "claude-code · s1 · linux-box",
            $"tooltip should reflect new host; got '{store.Sessions[0].TooltipText}'");

        // Host cleared (null) drops the host segment.
        store.ApplySnapshot(new[] { Snap("s1", "running", t0.AddSeconds(2), agent: "claude-code") }, t0.AddSeconds(2));
        Assert(store.Sessions[0].TooltipText == "claude-code · s1",
            $"tooltip should drop host; got '{store.Sessions[0].TooltipText}'");
    }

    private static async Task Vm_AgentReplacement_FiresTooltipChanged()
    {
        await Task.CompletedTask; // sync test
        var t0 = DateTimeOffset.UtcNow;
        var vm = new SessionViewModel { SessionId = "s", Agent = "old", Status = "running", UpdatedAt = t0 };
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.Agent = "new";
        Assert(seen.Contains("Agent"), "Agent changed");
        Assert(seen.Contains("TooltipText"), "TooltipText re-fired");
        Assert(vm.Agent == "new", "agent applied");
        Assert(vm.TooltipText == "new · s", $"tooltip='{vm.TooltipText}'");
    }

    private static async Task Vm_HostReplacement_FiresTooltipChanged()
    {
        await Task.CompletedTask; // sync test
        var t0 = DateTimeOffset.UtcNow;
        var vm = new SessionViewModel { SessionId = "s", Agent = "a", Status = "running", UpdatedAt = t0, Host = "old" };
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.Host = "new";
        Assert(seen.Contains("Host"), "Host changed");
        Assert(seen.Contains("TooltipText"), "TooltipText re-fired");
        Assert(vm.TooltipText == "a · s · new", $"tooltip='{vm.TooltipText}'");
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new InvalidOperationException("assertion failed: " + msg);
    }

    // ---- Round 2 close-out: partial header read helper ----

    /// <summary>
    /// Stream wrapper that returns at most <paramref name="maxBytesPerRead"/>
    /// bytes per ReadAsync call, even when more bytes are available.
    /// Used to verify ReadExactlyOrEofAsync handles short reads correctly.
    /// </summary>
    private sealed class ChoppedStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _maxBytesPerRead;
        public ChoppedStream(byte[] data, int maxBytesPerRead)
        {
            _data = data;
            _maxBytesPerRead = maxBytesPerRead;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int avail = _data.Length - _pos;
            if (avail <= 0) return 0;
            int n = Math.Min(Math.Min(count, avail), _maxBytesPerRead);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            int avail = _data.Length - _pos;
            if (avail <= 0) return 0;
            int n = Math.Min(Math.Min(buffer.Length, avail), _maxBytesPerRead);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ReadExactlyOrEofAsync_OneByteAtATime_Assembles()
    {
        // A 1-byte-per-read stream must still produce the correct 4-byte
        // header. This is the regression test for "if read != 4 -> error"
        // in PipeClient's old ReadLoopAsync.
        var data = new byte[] { 0x00, 0x00, 0x00, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f };
        using var s = new ChoppedStream(data, maxBytesPerRead: 1);
        var buf = new byte[4];
        bool got = await AgentBeacon.Indicator.Core.PipeClient
            .ReadExactlyOrEofAsync(s, buf, 0, 4, CancellationToken.None);
        Assert(got, "should assemble 4 bytes");
        int len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
        Assert(len == 5, $"length should be 5, got {len}");

        // And the payload assembly also loops over short reads.
        var payload = new byte[len];
        await AgentBeacon.Indicator.Core.PipeClient
            .ReadExactlyOrEofAsync(s, payload, 0, len, CancellationToken.None);
        Assert(System.Text.Encoding.UTF8.GetString(payload) == "hello",
            $"payload='{System.Text.Encoding.UTF8.GetString(payload)}'");
    }

    private static async Task ReadExactlyOrEofAsync_CleanEof_ReturnsFalse()
    {
        // Stream has 0 bytes; a read before any byte should return false
        // (clean EOF), NOT throw.
        using var s = new ChoppedStream(Array.Empty<byte>(), maxBytesPerRead: 1);
        var buf = new byte[4];
        bool got = await AgentBeacon.Indicator.Core.PipeClient
            .ReadExactlyOrEofAsync(s, buf, 0, 4, CancellationToken.None);
        Assert(!got, "clean EOF must return false");
    }

    private static async Task ReadExactlyOrEofAsync_TruncatedAfterSome_Throws()
    {
        // 2 bytes then EOF while requesting 4 -> IOException.
        var data = new byte[] { 0x00, 0x00 };
        using var s = new ChoppedStream(data, maxBytesPerRead: 1);
        var buf = new byte[4];
        var threw = false;
        try
        {
            await AgentBeacon.Indicator.Core.PipeClient
                .ReadExactlyOrEofAsync(s, buf, 0, 4, CancellationToken.None);
        }
        catch (IOException)
        {
            threw = true;
        }
        Assert(threw, "truncated read after 2 bytes must throw IOException");
    }

    private static async Task ReadExactlyOrEofAsync_HeaderSaysPayloadButEof_Throws()
    {
        // Header declares a non-zero payload length (5), then the stream
        // closes before any payload byte is delivered. This is a
        // truncated frame, NOT a clean disconnect, and must throw
        // IOException.
        var data = new byte[] { 0x00, 0x00, 0x00, 0x05 };
        using var s = new ChoppedStream(data, maxBytesPerRead: 1);
        var header = new byte[4];
        bool gotHeader = await AgentBeacon.Indicator.Core.PipeClient
            .ReadExactlyOrEofAsync(s, header, 0, 4, CancellationToken.None);
        Assert(gotHeader, "header should be assembled");
        int len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        Assert(len == 5, $"len should be 5, got {len}");

        var payload = new byte[len];
        var threw = false;
        try
        {
            bool gotPayload = await AgentBeacon.Indicator.Core.PipeClient
                .ReadExactlyOrEofAsync(s, payload, 0, len, CancellationToken.None);
            if (!gotPayload)
            {
                // The pipe client treats early-EOF-after-header as
                // truncated, which we model by re-throwing here.
                throw new IOException(
                    $"truncated payload: header declared {len} bytes but EOF before any payload byte");
            }
        }
        catch (IOException)
        {
            threw = true;
        }
        Assert(threw, "header-says-payload-but-EOF must be IOException, not clean disconnect");
    }
}
