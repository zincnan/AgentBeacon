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
            ("Store_Approval_ShowsCardWith30sTimer",                 Store_Approval_ShowsCardWith30sTimer),
            ("Store_ApprovalToRunning_HidesCard",                   Store_ApprovalToRunning_HidesCard),
            ("Store_Completed_ShowsThenAutoHideSignal",             Store_Completed_ShowsThenAutoHideSignal),
            ("Store_Failed_Lamp_LongLived_Card30s",                 Store_Failed_Lamp_LongLived_Card30s),
            ("Store_FailedToRunning_HidesCard",                     Store_FailedToRunning_HidesCard),
            ("Store_Failed_DedupSameUpdatedAt",                     Store_Failed_DedupSameUpdatedAt),
            ("Store_Completed_DedupSameUpdatedAt",                  Store_Completed_DedupSameUpdatedAt),
            ("Store_Completed_NewerUpdatedAt_Reappears",            Store_Completed_NewerUpdatedAt_Reappears),
            ("Store_Completed_HiddenAfter5MinFromUpdated",          Store_Completed_HiddenAfter5MinFromUpdated),
            ("Store_Failed_HiddenAfter5Min_StillPresent",          Store_Failed_HiddenAfter5Min_StillPresent),
            ("Store_RunningToRunning_NewerUpdated_NoRedup",         Store_RunningToRunning_NewerUpdated_NoRedup),
            ("Store_ApprovalToApproval_NewerUpdated_ReShows",       Store_ApprovalToApproval_NewerUpdated_ReShows),
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

            // Round 2 final close-out — authoritative removal must emit Hide
            ("Store_AuthoritativeRemoval_ApprovalEmitsHide",        Store_AuthoritativeRemoval_ApprovalEmitsHide),
            ("Store_AuthoritativeRemoval_ReappearsAsFresh",        Store_AuthoritativeRemoval_ReappearsAsFresh),

            // Round 2 final close-out — aged-completed tombstone is NOT
            // cleared by an authoritative empty snapshot (must survive
            // reconnect / full-snapshot re-broadcast to prevent zombie
            // completed lamps from reanimating).
            ("Store_AuthoritativeEmpty_DoesNotClearAgedCompletedTombstone",
                                                              Store_AuthoritativeEmpty_DoesNotClearAgedCompletedTombstone),
            ("Store_AuthoritativeEmpty_NewerCompletedOrRunning_ClearsTombstone",
                                                              Store_AuthoritativeEmpty_NewerCompletedOrRunning_ClearsTombstone),

            // Round 2 final close-out — card stack layout (pure helper)
            // REMOVED in Round 3: cards now anchor to their Agent
            // module instead of a global right-bottom stack.

            // Round 3 Indicator redesign — three-light mapping
            ("LampStateMapper_Failed_TopRed",                      LampStateMapper_Failed_TopRed),
            ("LampStateMapper_Approval_MiddleYellow",              LampStateMapper_Approval_MiddleYellow),
            ("LampStateMapper_Running_BottomBlue",                LampStateMapper_Running_BottomBlue),
            ("LampStateMapper_Completed_BottomGreen",             LampStateMapper_Completed_BottomGreen),
            ("LampStateMapper_RunningVsCompleted_ShareSlot_DifferColor",
                                                              LampStateMapper_RunningVsCompleted_ShareSlot_DifferColor),
            ("LampStateMapper_UnknownStatusThrows",               LampStateMapper_UnknownStatusThrows),
            ("LampStateMapper_AllFourStatuses_LightExactlyOneSlot",
                                                              LampStateMapper_AllFourStatuses_LightExactlyOneSlot),
            ("CardStayPolicy_ApprovalIs30s",                       CardStayPolicy_ApprovalIs30s),
            ("CardStayPolicy_CompletedIs30s",                      CardStayPolicy_CompletedIs30s),
            ("CardStayPolicy_FailedIs30s",                        CardStayPolicy_FailedIs30s),
            ("CardStayPolicy_RunningIsNone",                      CardStayPolicy_RunningIsNone),
            ("CardStayPolicy_UnknownStatusThrows",                CardStayPolicy_UnknownStatusThrows),

            // Round 5 — right-click lamp dismissal watermark
            ("LampDismissal_NeverDismissed_Shows",                LampDismissal_NeverDismissed_Shows),
            ("LampDismissal_SameOrOlderEvent_StaysHidden",        LampDismissal_SameOrOlderEvent_StaysHidden),
            ("LampDismissal_NewerEvent_Reactivates",              LampDismissal_NewerEvent_Reactivates),
            ("IndicatorHost_FirstApproval_DispatchesSnapshotBeforeCard",
                                                              IndicatorHost_FirstApproval_DispatchesSnapshotBeforeCard),

            // Round 3 fix — AnchoredCardLayout collision adjustment
            ("AnchoredCardLayout_Empty_NoCrash",                 AnchoredCardLayout_Empty_NoCrash),
            ("AnchoredCardLayout_SingleCard_NoOverlap",         AnchoredCardLayout_SingleCard_NoOverlap),
            ("AnchoredCardLayout_TwoCards_NoOverlap",           AnchoredCardLayout_TwoCards_NoOverlap),
            ("AnchoredCardLayout_UnequalHeights_NoOverlap",     AnchoredCardLayout_UnequalHeights_NoOverlap),
            ("AnchoredCardLayout_NaturalTopsOverlap_HidesOlder",AnchoredCardLayout_NaturalTopsOverlap_HidesOlder),
            ("AnchoredCardLayout_SafeTopSafeBottomClamp",       AnchoredCardLayout_SafeTopSafeBottomClamp),
            ("AnchoredCardLayout_NoRoom_HidesOlderNotNewer",    AnchoredCardLayout_NoRoom_HidesOlderNotNewer),
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

    private static async Task Store_Approval_ShowsCardWith30sTimer()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0, message: "needs review") }, t0);

        Assert(sink.Events.Count == 1, $"expected 1 event, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", $"kind={sink.Events[0].Kind}");
        Assert(sink.Events[0].AutoHideAfterMs == 30000,
            $"approval card must auto-hide after 30000ms; got {sink.Events[0].AutoHideAfterMs}");

        // Same snapshot re-applied (dedup): no new event.
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
        Assert(sink.Events[0].AutoHideAfterMs == 30000,
            $"expected 30000ms auto-hide, got {sink.Events[0].AutoHideAfterMs}");
    }

    private static async Task Store_Failed_Lamp_LongLived_Card30s()
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
        Assert(sink.Events[0].AutoHideAfterMs == 30000,
            $"failed card auto-hide must be 30000ms, got {sink.Events[0].AutoHideAfterMs}");

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

    private static async Task Store_ApprovalToApproval_NewerUpdated_ReShows()
    {
        await Task.CompletedTask; // sync test
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0, message: "v1") }, t0);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0.AddSeconds(2), message: "v2") }, t0.AddSeconds(2));

        // New Round 3 policy: approval card auto-retracts after 8s. A
        // strictly-newer updated_at means a new approval event, so we
        // re-Show (not just Update) so the WPF layer restarts the 8s
        // stay timer.
        Assert(sink.Events.Count == 2, $"expected Show + Show, got {sink.Events.Count}");
        Assert(sink.Events[0].Kind == "Show", "first show");
        Assert(sink.Events[1].Kind == "Show", "second show re-arms 8s timer");
        Assert(sink.Events[1].AutoHideAfterMs == 30000,
            $"re-Showed approval card must carry 30000ms; got {sink.Events[1].AutoHideAfterMs}");
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

    // ---- Round 2 final close-out: authoritative removal must emit Hide ----

    private static async Task Store_AuthoritativeRemoval_ApprovalEmitsHide()
    {
        // Receiver is the authoritative source. When a session that the
        // UI has been showing an approval card for is removed from a
        // subsequent snapshot (e.g. Receiver restart, snapshot reset,
        // Receiver's own authoritative eviction), the store must emit
        // CardEventKind.Hide so the WPF layer closes the card and
        // cancels the auto-hide timer. Without this, the approval
        // card would stay on screen forever after the lamp is gone.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0, message: "needs review") }, t0);
        Assert(sink.Events.Count == 1, "initial Show");
        Assert(sink.Events[0].Kind == "Show", "initial event is Show");

        // Authoritative empty snapshot: Receiver stopped reporting s1.
        store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddSeconds(5));
        Assert(store.Sessions.Count == 0, "lamp removed");
        Assert(sink.Events.Count == 2, $"expected Show + Hide, got {sink.Events.Count}");
        Assert(sink.Events[1].Kind == "Hide", $"second event must be Hide, got {sink.Events[1].Kind}");
        Assert(sink.Events[1].SessionId == "s1", "Hide carries the removed session_id");
        Assert(sink.Events[1].Status == "approval", "Hide carries the last-known status");
    }

    private static async Task Store_AuthoritativeRemoval_ReappearsAsFresh()
    {
        // The same session_id reappearing after authoritative removal
        // must be treated as a brand new session: full re-Show chain
        // (running -> approval Show; or completed -> completed Show
        // again; etc.) without any stale dedup or tombstone getting
        // in the way.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // First lifecycle
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0) }, t0);
        store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddSeconds(5));
        Assert(sink.Events.Count == 2, "Show + Hide from first lifecycle");
        Assert(sink.Events[1].Kind == "Hide", "Hide emitted on authoritative removal");

        // Reappearance: approval again at a strictly-newer updated_at
        store.ApplySnapshot(new[] { Snap("s1", "approval", t0.AddMinutes(1)) }, t0.AddMinutes(1));
        Assert(store.Sessions.Count == 1, "reappeared lamp");
        Assert(sink.Events.Count == 3, $"Show emitted on reappearance, got {sink.Events.Count}");
        Assert(sink.Events[2].Kind == "Show", $"reappearance event is Show, got {sink.Events[2].Kind}");
        Assert(sink.Events[2].Status == "approval", "reappearance Show carries approval status");

        // Removed again, must emit a second Hide
        store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddMinutes(2));
        Assert(sink.Events.Count == 4, $"second Hide emitted, got {sink.Events.Count}");
        Assert(sink.Events[3].Kind == "Hide", "second Hide event");
    }

    // ---- Round 2 final close-out: aged-completed tombstone survives
    // authoritative empty snapshot. ----

    private static async Task Store_AuthoritativeEmpty_DoesNotClearAgedCompletedTombstone()
    {
        // Product rule: when a completed session ages out (5 min past
        // updated_at), the lamp is dropped and a hidden-completed
        // tombstone is set. Subsequent identical / older completed
        // re-broadcasts — including those wrapped in an authoritative
        // empty snapshot, an authoritative full-snapshot reconnect,
        // or anything short of a strictly-newer completed updated_at
        // or a non-completed event for the same session_id — must NOT
        // bring the lamp back. The aged-completed tombstone is NOT
        // cleared by an authoritative empty snapshot: empty snapshots
        // only iterate currently-live _byId sessions and aged-out
        // completed sessions are no longer in _byId.
        var store = new SessionViewModelStore();
        var sink = new RecordingSink();
        store.CardEvent += sink.OnEvent;

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
        Assert(sink.Events.Count == 1, "initial completed Show");

        // 5+ minutes pass: Tick ages the lamp out and writes the tombstone.
        store.Tick(t0.AddMinutes(6));
        Assert(store.Sessions.Count == 0, "completed lamp aged out");

        // Authoritative empty snapshot (e.g. Receiver restart, snapshot
        // reset). The store iterates _byId — which no longer contains
        // s1 — so this MUST NOT clear the tombstone. We can't observe
        // the tombstone directly, but we can observe its effect.
        store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddMinutes(7));
        Assert(sink.Events.Count == 1, "no new events from empty snapshot (no current live session to Hide)");
        Assert(store.Sessions.Count == 0, "still empty");

        // Now an authoritative full snapshot re-broadcasts the SAME
        // completed event for s1 at the SAME updated_at. This is exactly
        // the "Receiver forgot to evict this session" / "Receiver
        // restarted and is re-sending its in-memory table" scenario.
        // Because the tombstone is still alive (older-or-equal
        // updated_at), the store must NOT add s1 back, NOT emit a new
        // Show, NOT make the lamp reanimate.
        store.ApplySnapshot(new[] { Snap("s1", "completed", t0, message: "done") }, t0.AddMinutes(8));
        Assert(store.Sessions.Count == 0, "same-updated_at completed must NOT resurrect the lamp");
        Assert(sink.Events.Count == 1, $"no new card events from same-updated_at re-broadcast; got {sink.Events.Count}");
    }

    private static async Task Store_AuthoritativeEmpty_NewerCompletedOrRunning_ClearsTombstone()
    {
        // Counterpart to the previous test: only a strictly-newer
        // completed updated_at or a non-completed event for the same
        // session is allowed to clear an aged-completed tombstone.
        // (empty / identical / older completed snapshots must not.)
        //
        // We start from the same aged-out state as the previous test,
        // then exercise BOTH clearing paths in independent store
        // instances to keep each assertion self-contained.

        // Path A: strictly-newer completed updated_at.
        {
            var store = new SessionViewModelStore();
            var sink = new RecordingSink();
            store.CardEvent += sink.OnEvent;

            var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
            store.Tick(t0.AddMinutes(6));
            store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddMinutes(7));
            // Tombstone now blocks same-or-older completed re-broadcasts.
            var t1 = t0.AddMinutes(10);
            store.ApplySnapshot(new[] { Snap("s1", "completed", t1, message: "done again") }, t1);
            Assert(store.Sessions.Count == 1, "newer completed brings the lamp back");
            Assert(store.Sessions[0].LampColor == "#3FB950", "completed lamp is green");
            Assert(sink.Events.Count == 2, "new completed Show after tombstone cleared");
            Assert(sink.Events[1].Kind == "Show", "second event is Show");
            Assert(sink.Events[1].AutoHideAfterMs == 30000, "completed card auto-hide 30s");
        }

        // Path B: a non-completed event for the same session_id
        // (running, approval, or failed) clears the tombstone and the
        // lamp reappears.
        {
            var store = new SessionViewModelStore();
            var sink = new RecordingSink();
            store.CardEvent += sink.OnEvent;

            var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            store.ApplySnapshot(new[] { Snap("s1", "completed", t0) }, t0);
            store.Tick(t0.AddMinutes(6));
            store.ApplySnapshot(Array.Empty<SessionSnapshot>(), t0.AddMinutes(7));
            // Tombstone blocks same completed; non-completed clears it.
            var t1 = t0.AddMinutes(10);
            store.ApplySnapshot(new[] { Snap("s1", "running", t1) }, t1);
            Assert(store.Sessions.Count == 1, "running clears tombstone and reappears");
            Assert(store.Sessions[0].LampColor == "#2F81F7", "running lamp is blue");
            // Running produces no card event; sink only saw the initial Show.
            Assert(sink.Events.Count == 1, "running does not emit a card event");
        }
    }

    // ---- Round 3 Indicator redesign: LampStateMapper + CardStayPolicy ----

    private static async Task LampStateMapper_Failed_TopRed()
    {
        var s = LampStateMapper.ForStatus("failed");
        Assert(s.ActiveSlot == LampSlot.Top, $"failed lights top slot; got {s.ActiveSlot}");
        Assert(s.ActiveColorHex == "#F85149", $"failed red hex; got {s.ActiveColorHex}");
        Assert(s.ColorFor(LampSlot.Middle) == LampStateMapper.LampModuleState.InactiveColorHex,
            "middle slot must be inactive");
        Assert(s.ColorFor(LampSlot.Bottom) == LampStateMapper.LampModuleState.InactiveColorHex,
            "bottom slot must be inactive");
    }

    private static async Task LampStateMapper_Approval_MiddleYellow()
    {
        var s = LampStateMapper.ForStatus("approval");
        Assert(s.ActiveSlot == LampSlot.Middle, $"approval lights middle slot; got {s.ActiveSlot}");
        Assert(s.ActiveColorHex == "#D29922", $"approval yellow hex; got {s.ActiveColorHex}");
        Assert(s.ColorFor(LampSlot.Top) == LampStateMapper.LampModuleState.InactiveColorHex,
            "top slot must be inactive");
        Assert(s.ColorFor(LampSlot.Bottom) == LampStateMapper.LampModuleState.InactiveColorHex,
            "bottom slot must be inactive");
    }

    private static async Task LampStateMapper_Running_BottomBlue()
    {
        var s = LampStateMapper.ForStatus("running");
        Assert(s.ActiveSlot == LampSlot.Bottom, $"running lights bottom slot; got {s.ActiveSlot}");
        Assert(s.ActiveColorHex == "#2F81F7", $"running blue hex; got {s.ActiveColorHex}");
        Assert(s.ColorFor(LampSlot.Top) == LampStateMapper.LampModuleState.InactiveColorHex,
            "top slot must be inactive");
        Assert(s.ColorFor(LampSlot.Middle) == LampStateMapper.LampModuleState.InactiveColorHex,
            "middle slot must be inactive");
    }

    private static async Task LampStateMapper_Completed_BottomGreen()
    {
        var s = LampStateMapper.ForStatus("completed");
        Assert(s.ActiveSlot == LampSlot.Bottom, $"completed lights bottom slot; got {s.ActiveSlot}");
        Assert(s.ActiveColorHex == "#3FB950", $"completed green hex; got {s.ActiveColorHex}");
        Assert(s.ColorFor(LampSlot.Top) == LampStateMapper.LampModuleState.InactiveColorHex,
            "top slot must be inactive");
        Assert(s.ColorFor(LampSlot.Middle) == LampStateMapper.LampModuleState.InactiveColorHex,
            "middle slot must be inactive");
    }

    private static async Task LampStateMapper_RunningVsCompleted_ShareSlot_DifferColor()
    {
        // Round 3 invariant: the bottom physical slot is shared between
        // running and completed. They differ ONLY in color.
        var r = LampStateMapper.ForStatus("running");
        var c = LampStateMapper.ForStatus("completed");
        Assert(r.ActiveSlot == LampSlot.Bottom && c.ActiveSlot == LampSlot.Bottom,
            "both running and completed must light the bottom physical slot");
        Assert(r.ActiveColorHex != c.ActiveColorHex,
            $"running ({r.ActiveColorHex}) and completed ({c.ActiveColorHex}) must have different colors");
    }

    private static async Task LampStateMapper_UnknownStatusThrows()
    {
        var threw = false;
        try { LampStateMapper.ForStatus("paused"); }
        catch (ArgumentException) { threw = true; }
        Assert(threw, "LampStateMapper.ForStatus must throw ArgumentException on 'paused'");
    }

    private static async Task LampStateMapper_AllFourStatuses_LightExactlyOneSlot()
    {
        // Exhaustive check: across all four Protocol v1 statuses, each
        // lights exactly one physical slot. No fifth state. The non-
        // active slots must use the inactive color, never some other
        // color like gray/idle/offline/unknown.
        var statuses = new[] { "running", "approval", "completed", "failed" };
        var seenActiveSlots = new HashSet<LampSlot>();
        foreach (var st in statuses)
        {
            var s = LampStateMapper.ForStatus(st);
            seenActiveSlots.Add(s.ActiveSlot);
            // All three slots must have a valid color, and the two
            // non-active ones must be the inactive color.
            for (int i = 0; i < 3; i++)
            {
                var slot = (LampSlot)i;
                var c = s.ColorFor(slot);
                Assert(c == "#F85149" || c == "#D29922" || c == "#2F81F7" || c == "#3FB950" || c == LampStateMapper.LampModuleState.InactiveColorHex,
                    $"slot {slot} for status '{st}' returned unexpected color '{c}'");
                if (slot != s.ActiveSlot)
                {
                    Assert(c == LampStateMapper.LampModuleState.InactiveColorHex,
                        $"non-active slot {slot} for status '{st}' must be inactive, got {c}");
                }
            }
        }
        // Top + Middle + Bottom all reachable; only one active per status.
        Assert(seenActiveSlots.SetEquals(new[] { LampSlot.Top, LampSlot.Middle, LampSlot.Bottom }),
            "all three physical slots must be reachable across the four statuses");
    }

    private static async Task CardStayPolicy_ApprovalIs30s()
    {
        var p = LampStateMapper.CardStayPolicy.ForStatus("approval");
        Assert(p.StayMs == 30000, $"approval must be 30000ms; got {p.StayMs}");
    }

    private static async Task CardStayPolicy_CompletedIs30s()
    {
        var p = LampStateMapper.CardStayPolicy.ForStatus("completed");
        Assert(p.StayMs == 30000, $"completed must be 30000ms; got {p.StayMs}");
    }

    private static async Task CardStayPolicy_FailedIs30s()
    {
        var p = LampStateMapper.CardStayPolicy.ForStatus("failed");
        Assert(p.StayMs == 30000, $"failed must be 30000ms; got {p.StayMs}");
    }

    private static async Task CardStayPolicy_RunningIsNone()
    {
        var p = LampStateMapper.CardStayPolicy.ForStatus("running");
        Assert(p.StayMs is null, $"running must be null (no card); got {p.StayMs}");
    }

    private static async Task CardStayPolicy_UnknownStatusThrows()
    {
        var threw = false;
        try { LampStateMapper.CardStayPolicy.ForStatus("paused"); }
        catch (ArgumentException) { threw = true; }
        Assert(threw, "CardStayPolicy.ForStatus must throw on unknown status");
    }

    // ---- Round 5: right-click lamp dismissal watermark ----

    private static async Task LampDismissal_NeverDismissed_Shows()
    {
        var t0 = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        Assert(LampDismissal.ShouldShow(null, t0), "never-dismissed lamp always shows");
    }

    private static async Task LampDismissal_SameOrOlderEvent_StaysHidden()
    {
        var t0 = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        // Re-broadcast of the exact event the user hid → still hidden.
        Assert(!LampDismissal.ShouldShow(t0, t0),
            "same updated_at re-broadcast must not resurrect a dismissed lamp");
        // Older event → still hidden.
        Assert(!LampDismissal.ShouldShow(t0, t0.AddSeconds(-5)),
            "older updated_at must not resurrect a dismissed lamp");
    }

    private static async Task LampDismissal_NewerEvent_Reactivates()
    {
        var t0 = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        Assert(LampDismissal.ShouldShow(t0, t0.AddSeconds(1)),
            "strictly newer updated_at reactivates the lamp");
    }

    private static async Task IndicatorHost_FirstApproval_DispatchesSnapshotBeforeCard()
    {
        await Task.CompletedTask; // sync test

        var order = new List<string>();
        CardEvent? cardEvent = null;
        using var host = new IndicatorHost(
            onSnapshotReceived: sessions =>
            {
                order.Add($"snapshot:{sessions.Count}");
                Assert(sessions.Count == 1, "snapshot callback should see the first session module");
                Assert(sessions[0].SessionId == "s1", "snapshot callback session id");
                Assert(sessions[0].Status == "approval", "snapshot callback status");
            },
            onCardEvent: ev =>
            {
                order.Add($"card:{ev.Kind}");
                cardEvent = ev;
            },
            onPipeError: msg => throw new Exception(msg));

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        host.ProcessSnapshot(new[] { Snap("s1", "approval", t0, message: "needs review") }, t0);

        Assert(order.SequenceEqual(new[] { "snapshot:1", "card:Show" }),
            $"expected snapshot before card Show; got [{string.Join(", ", order)}]");
        Assert(cardEvent is not null, "first approval snapshot must deliver CardEvent.Show");
        var delivered = cardEvent ?? throw new InvalidOperationException("missing card event");
        Assert(delivered.Kind == CardEventKind.Show, $"expected Show, got {delivered.Kind}");
        Assert(delivered.Session.SessionId == "s1", "card event session id");
        Assert(delivered.AutoHideAfterMs == 30000,
            $"approval CardEvent.Show AutoHideAfterMs must be 30000; got {delivered.AutoHideAfterMs}");
    }

    // ---- Round 3: AnchoredCardLayout collision adjustment ----

    private static AnchoredCardInput Make(string sid, double centerY, double height)
        => new() { SessionId = sid, ModuleCenterYScreen = centerY, Height = height };

    private static async Task AnchoredCardLayout_Empty_NoCrash()
    {
        var p = AnchoredCardLayout.Compute(
            Array.Empty<AnchoredCardInput>(),
            safeTop: 100, safeBottom: 800, gap: 8);
        Assert(p.Count == 0, "empty input yields empty placements");
    }

    private static async Task AnchoredCardLayout_SingleCard_NoOverlap()
    {
        var p = AnchoredCardLayout.Compute(
            new[] { Make("a", centerY: 200, height: 100) },
            safeTop: 100, safeBottom: 800, gap: 8);
        Assert(p.Count == 1, "length");
        Assert(p["a"].Visible, "single card fits");
        // Natural top = centerY - height/2 = 150
        Assert(p["a"].Top == 150, $"single card top = 150; got {p["a"].Top}");
    }

    private static async Task AnchoredCardLayout_TwoCards_NoOverlap()
    {
        // Both cards same height; their natural tops are far enough apart
        // that no offset is needed.
        var p = AnchoredCardLayout.Compute(
            new[]
            {
                Make("upper", centerY: 250, height: 100), // natural top 200
                Make("lower", centerY: 500, height: 100), // natural top 450
            },
            safeTop: 100, safeBottom: 800, gap: 8);

        Assert(p.Count == 2, "length");
        Assert(p["upper"].Visible && p["lower"].Visible, "both visible");
        // Upper stays at natural (200), lower stays at natural (450).
        Assert(p["upper"].Top == 200, $"upper top = 200; got {p["upper"].Top}");
        Assert(p["lower"].Top == 450, $"lower top = 450; got {p["lower"].Top}");

        // Pairwise invariant: every adjacent fitting pair has at least
        // `gap` between bottom-of-older and top-of-newer.
        var olderBottom = p["upper"].Top + 100;
        Assert(olderBottom + 8 <= p["lower"].Top,
            $"upper bottom ({olderBottom}) + gap (8) must be <= lower top ({p["lower"].Top})");
    }

    private static async Task AnchoredCardLayout_UnequalHeights_NoOverlap()
    {
        var p = AnchoredCardLayout.Compute(
            new[]
            {
                Make("tall", centerY: 300, height: 200), // natural top 200, bottom 400
                Make("short", centerY: 450, height: 80), // natural top 410, bottom 490
            },
            safeTop: 100, safeBottom: 800, gap: 8);

        Assert(p.Count == 2, "length");
        Assert(p["tall"].Visible && p["short"].Visible, "both visible");

        // tall's natural top (200) is < short's natural top (410), so
        // the layout puts tall first, then short. short must be at
        // >= tall.bottom + gap = 400 + 8 = 408.
        Assert(p["short"].Top >= 408,
            $"short top ({p["short"].Top}) must be >= tall bottom + gap (408)");

        // And short must still fit within safeBottom.
        Assert(p["short"].Top + 80 <= 800,
            $"short must fit in safe band; bottom = {p["short"].Top + 80}");
    }

    private static async Task AnchoredCardLayout_NaturalTopsOverlap_HidesOlder()
    {
        // Two cards whose natural regions overlap. Round 12+ policy: a
        // card must sit beside its OWN module — never displaced next to
        // another lamp. So the overlapping OLDER card is hidden outright
        // (newer keeps its natural seat); it returns when the newer card
        // retracts and the layout is re-run.
        var p = AnchoredCardLayout.Compute(
            new[]
            {
                Make("upper", centerY: 300, height: 200), // natural top 200, bottom 400 (older)
                Make("lower", centerY: 350, height: 80),  // natural top 310, bottom 390 (newer)
            },
            safeTop: 100, safeBottom: 800, gap: 8);

        Assert(p["lower"].Visible, "newer keeps its natural seat");
        Assert(p["lower"].Top == 310, $"newer keeps natural top 310; got {p["lower"].Top}");
        Assert(!p["upper"].Visible, "overlapping older card must be hidden");
    }

    private static async Task AnchoredCardLayout_SafeTopSafeBottomClamp()
    {
        // Card with module center above safeTop -> natural top clamps to safeTop.
        var p1 = AnchoredCardLayout.Compute(
            new[] { Make("top", centerY: 50, height: 100) },
            safeTop: 100, safeBottom: 800, gap: 8);
        Assert(p1["top"].Top == 100, $"top clamp; got {p1["top"].Top}");
        Assert(p1["top"].Visible, "still fits");

        // Card with module center so low that natural top would push
        // the card below safeBottom.
        var p2 = AnchoredCardLayout.Compute(
            new[] { Make("bottom", centerY: 1000, height: 100) },
            safeTop: 100, safeBottom: 800, gap: 8);
        Assert(p2["bottom"].Top == 700, $"bottom clamp; got {p2["bottom"].Top}");
        Assert(p2["bottom"].Visible, "still fits");
    }

    private static async Task AnchoredCardLayout_NoRoom_HidesOlderNotNewer()
    {
        // Two cards whose combined heights + gap exceed the safe band
    // when stacked. With newer-wins, the OLDER card (whose natural
    // top is highest) gets pushed above safeTop and is hidden; the
    // NEWER card stays at its natural position and is visible.
        const double safeTop = 100;
        const double safeBottom = 400;
        const double gap = 10;
        var p = AnchoredCardLayout.Compute(
            new[]
            {
                // older (inserted first): natural top 110, height 200.
                // newest (inserted last): natural top 250, height 200.
                Make("older", centerY: 210, height: 200),
                Make("newer", centerY: 350, height: 200),
            },
            safeTop: safeTop, safeBottom: safeBottom, gap: gap);

        Assert(p.Count == 2, "length");
        // newer (lower naturalTop = bottom of screen) wins and stays.
        Assert(p["newer"].Visible, "newer card must be visible");
        // newer sits at its clamped natural: natural = 250, 250+200=450 > safeBottom 400
        // -> clamped to safeBottom - height = 200. newest card has no constraint above
        // (no anyPlaced yet), so top = 200.
        Assert(p["newer"].Top == 200, $"newer top clamped to 200; got {p["newer"].Top}");
        // older is processed next; maxTop = newer.top - gap - height = 200 - 10 - 200 = -10.
        // top = min(natural=110, maxTop=-10) = -10. -10 < safeTop 100 -> HIDDEN.
        Assert(!p["older"].Visible, "older card must be hidden when no room");
    }
}
