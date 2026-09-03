using System.Collections.Concurrent;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Pure-C# store that ingests SnapshotEnvelopes and produces both:
///   1. The lamp column state (visible Sessions, in stable order).
///   2. Card events (Show/Update/Hide) for the WPF layer to render.
///
/// All rules from docs/ui-policy.md are encoded here so they can be tested
/// without spinning up WPF:
///
///   running   -> no card. Lamp entry only (blue).
///   approval  -> persistent card until status changes (yellow lamp).
///   completed -> card for 5s, lamp entry hidden 5min after updated_at (green).
///   failed    -> card for 10s, lamp entry long-lived until status changes (red).
///
/// Dedup: a card for (session_id, updated_at) is shown at most once; if the
/// same updated_at arrives again, no event is emitted. A newer updated_at
/// replaces and re-emits.
///
/// Completed-suppression (Round 2 close-out):
///   When Tick() removes a completed lamp because its updated_at is older
///   than CompletedLampDuration, the (session_id, updated_at) pair is
///   recorded in a hidden-completed tombstone. A subsequent snapshot that
///   re-reports the SAME completed event (same updated_at) will neither
///   re-show the lamp nor re-fire a card — this matches the user's
///   expectation that completed lamps don't reanimate when an unrelated
///   full-snapshot re-broadcast happens to include them.
///
///   A newer completed event (updated_at strictly greater than the
///   tombstone) clears the tombstone and is treated as a fresh completion.
///   A non-completed event for the same session always clears the
///   tombstone (the lamp transitions to a live state).
/// </summary>
public sealed class SessionViewModelStore
{
    /// <summary>Card visibility duration for "approval".</summary>
    public static readonly TimeSpan ApprovalCardDuration = TimeSpan.FromSeconds(30);

    /// <summary>Card visibility duration for "completed".</summary>
    public static readonly TimeSpan CompletedCardDuration = TimeSpan.FromSeconds(30);

    /// <summary>Card visibility duration for "failed".</summary>
    public static readonly TimeSpan FailedCardDuration = TimeSpan.FromSeconds(30);

    /// <summary>Lamp visibility duration for "completed" sessions, measured from updated_at.</summary>
    public static readonly TimeSpan CompletedLampDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SessionViewModel> _byId =
        new(StringComparer.Ordinal);

    /// <summary>Last UpdatedAt at which we emitted a Show event for a session, per session.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCardShownUpdatedAt =
        new(StringComparer.Ordinal);

    /// <summary>Status at the time we last emitted a card event for a session.</summary>
    private readonly ConcurrentDictionary<string, string> _lastCardShownStatus =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Hidden-completed tombstones: session_id -> the updated_at of the
    /// completed event whose lamp has been aged out and must not
    /// re-animate on re-broadcast. Cleared on (a) a strictly-newer
    /// completed event for the same session, or (b) any non-completed
    /// event for the same session.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hiddenCompleted =
        new(StringComparer.Ordinal);

    private readonly List<SessionViewModel> _ordered = new();
    private readonly object _orderLock = new();

    /// <summary>Subscribe to card lifecycle events.</summary>
    public event Action<CardEvent>? CardEvent;

    /// <summary>Snapshot of currently visible sessions, ordered by UpdatedAt then SessionId.</summary>
    public IReadOnlyList<SessionViewModel> Sessions
    {
        get
        {
            lock (_orderLock)
            {
                return _ordered.ToList();
            }
        }
    }

    /// <summary>
    /// Apply an incoming snapshot. Last-received-wins per session_id.
    /// Emits zero or more CardEvents. Always non-blocking. Unknown statuses
    /// (defense in depth) throw — the Receiver already rejects them at
    /// the HTTP boundary.
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<SessionSnapshot> incoming, DateTimeOffset now)
    {
        // 1. Build the set of session IDs present in the new snapshot.
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in incoming)
        {
            IndicatorStatus.Validate(s.Status);
            present.Add(s.SessionId);

            // Hidden-completed tombstone check: if a session was aged
            // out as completed and the incoming snapshot repeats that
            // exact completed event (same updated_at), skip the lamp
            // and skip the card entirely. A strictly newer completed
            // event or any non-completed event for the same session
            // clears the tombstone.
            if (_hiddenCompleted.TryGetValue(s.SessionId, out var hiddenAt))
            {
                if (s.Status == IndicatorStatus.Completed)
                {
                    if (s.UpdatedAt <= hiddenAt)
                    {
                        // Same or older completed re-broadcast: suppressed.
                        continue;
                    }
                    // Newer completed: clear tombstone and fall through
                    // to normal processing.
                    _hiddenCompleted.TryRemove(s.SessionId, out _);
                }
                else
                {
                    // Any non-completed event supersedes the tombstone.
                    _hiddenCompleted.TryRemove(s.SessionId, out _);
                }
            }

            var existing = _byId.TryGetValue(s.SessionId, out var vm) ? vm : null;

            if (vm is null)
            {
                vm = SessionViewModel.FromSnapshot(s, now);
                _byId[s.SessionId] = vm;
                AddToOrder(vm);
            }
            else
            {
                // Whole-event replacement: copy fields. Agent/Host setters
                // fire PropertyChanged(TooltipText) on the VM so WPF
                // tooltips stay current even when the same session_id
                // later reports under a different agent / host.
                vm.Agent = s.Agent;
                vm.Status = s.Status;
                vm.UpdatedAt = s.UpdatedAt;
                vm.Message = s.Message;
                vm.Host = s.Host;
            }

            EmitCardEventForTransition(vm);
        }

        // 2. Remove sessions that are no longer in the snapshot. (The
        //    Receiver is authoritative — if it stopped reporting a session,
        //    we forget it.) For each such session we ALSO emit
        //    CardEventKind.Hide so the WPF layer can close any visible
        //    card and cancel its per-session auto-hide timer.
        //    Receiver-driven removals must be visible to the UI: a
        //    previous Show can leave a card up (e.g. a persistent
        //    approval card) that the lamp column would otherwise have
        //    lost track of. The WPF layer's Hide handler is idempotent
        //    and safe when no card is present.
        //
        //    NOTE on hidden-completed tombstones: this loop walks
        //    _byId.Keys, so it only touches sessions that are CURRENTLY
        //    live in the lamp column. Aged-out completed sessions were
        //    already removed from _byId by Tick() (5 min past their
        //    updated_at) and their tombstones in _hiddenCompleted
        //    survive on purpose: a later authoritative empty / full
        //    snapshot must NOT bring them back. Only a strictly-newer
        //    completed updated_at, or a non-completed event for the
        //    same session_id, is allowed to clear an aged-completed
        //    tombstone (see the tombstone check at the top of this
        //    method and Store_AuthoritativeEmpty_DoesNotClearAgedCompletedTombstone).
        //
        //    What this loop DOES clear for a removed live session: its
        //    own dedup state (_lastCardShownUpdatedAt /
        //    _lastCardShownStatus) and any in-flight tombstone it might
        //    have had under the same id (rare; normally a completed
        //    session that aged out has already left _byId before this
        //    loop runs, so this is mostly belt-and-braces for racing
        //    snapshots).
        var removed = new List<string>();
        foreach (var id in _byId.Keys)
        {
            if (!present.Contains(id)) removed.Add(id);
        }
        foreach (var id in removed)
        {
            if (_byId.TryRemove(id, out var vm))
            {
                RemoveFromOrder(vm);
                // Emit Hide while we still hold a reference to the VM,
                // so subscribers see a fully-populated CardEvent.Session.
                CardEvent?.Invoke(new CardEvent
                {
                    Kind = CardEventKind.Hide,
                    Session = vm,
                });
            }
            _lastCardShownUpdatedAt.TryRemove(id, out _);
            _lastCardShownStatus.TryRemove(id, out _);
            _hiddenCompleted.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Periodic tick to remove completed sessions whose lamp entry has aged
    /// out (5 minutes past updated_at). Call this every ~30s from the
    /// WPF Dispatcher. Aged-out completed sessions leave a hidden-completed
    /// tombstone so re-broadcasts of the same event don't reanimate them.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        var toRemove = new List<string>();
        foreach (var kv in _byId)
        {
            if (kv.Value.Status == IndicatorStatus.Completed
                && now - kv.Value.UpdatedAt >= CompletedLampDuration)
            {
                toRemove.Add(kv.Key);
            }
        }
        foreach (var id in toRemove)
        {
            if (_byId.TryRemove(id, out var vm))
            {
                RemoveFromOrder(vm);
                // Record the tombstone so a re-broadcast of this exact
                // completed event won't bring the lamp back.
                _hiddenCompleted[id] = vm.UpdatedAt;
            }
            _lastCardShownUpdatedAt.TryRemove(id, out _);
            _lastCardShownStatus.TryRemove(id, out _);
        }
    }

    /// <summary>Test helper: clear all state including tombstones.</summary>
    public void Reset()
    {
        _byId.Clear();
        _lastCardShownStatus.Clear();
        _lastCardShownUpdatedAt.Clear();
        _hiddenCompleted.Clear();
        lock (_orderLock) _ordered.Clear();
    }

    private void EmitCardEventForTransition(SessionViewModel vm)
    {
        var prevStatus = _lastCardShownStatus.TryGetValue(vm.SessionId, out var ps) ? ps : null;
        var prevUpdatedAt = _lastCardShownUpdatedAt.TryGetValue(vm.SessionId, out var pu) ? pu : default;

        switch (vm.Status)
        {
            case IndicatorStatus.Running:
                // No card for running. But if we previously showed a card
                // (e.g. was approval/failed/completed), the status change
                // should hide it. Emit Hide.
                if (prevStatus is IndicatorStatus.Approval
                    or IndicatorStatus.Failed
                    or IndicatorStatus.Completed)
                {
                    _lastCardShownStatus.TryRemove(vm.SessionId, out _);
                    _lastCardShownUpdatedAt.TryRemove(vm.SessionId, out _);
                    CardEvent?.Invoke(new CardEvent { Kind = CardEventKind.Hide, Session = vm });
                }
                break;

            case IndicatorStatus.Approval:
                // Card auto-retracts after 8s; lamp stays lit until
                // status leaves approval.
                //   - prev status != approval           -> fresh Show
                //   - prev approval, updated_at changed -> re-Show with new stay timer
                //   - prev approval, updated_at same    -> no event (dedup)
                if (prevStatus != IndicatorStatus.Approval)
                {
                    _lastCardShownStatus[vm.SessionId] = vm.Status;
                    _lastCardShownUpdatedAt[vm.SessionId] = vm.UpdatedAt;
                    CardEvent?.Invoke(new CardEvent
                    {
                        Kind = CardEventKind.Show,
                        Session = vm,
                        AutoHideAfterMs = (int)ApprovalCardDuration.TotalMilliseconds,
                    });
                }
                else if (prevUpdatedAt != vm.UpdatedAt)
                {
                    _lastCardShownUpdatedAt[vm.SessionId] = vm.UpdatedAt;
                    CardEvent?.Invoke(new CardEvent
                    {
                        Kind = CardEventKind.Show,
                        Session = vm,
                        AutoHideAfterMs = (int)ApprovalCardDuration.TotalMilliseconds,
                    });
                }
                break;

            case IndicatorStatus.Completed:
                // Card dedup: same updated_at -> nothing.
                if (prevStatus == IndicatorStatus.Completed && prevUpdatedAt == vm.UpdatedAt)
                {
                    break;
                }
                _lastCardShownStatus[vm.SessionId] = vm.Status;
                _lastCardShownUpdatedAt[vm.SessionId] = vm.UpdatedAt;
                CardEvent?.Invoke(new CardEvent
                {
                    Kind = CardEventKind.Show,
                    Session = vm,
                    AutoHideAfterMs = (int)CompletedCardDuration.TotalMilliseconds,
                });
                break;

            case IndicatorStatus.Failed:
                // Lamp stays until status changes (long-lived).
                // Card auto-hides after 10s; Show carries AutoHideAfterMs.
                // Dedup: same (status, updated_at) -> nothing.
                if (prevStatus == IndicatorStatus.Failed && prevUpdatedAt == vm.UpdatedAt)
                {
                    break;
                }
                _lastCardShownStatus[vm.SessionId] = vm.Status;
                _lastCardShownUpdatedAt[vm.SessionId] = vm.UpdatedAt;
                CardEvent?.Invoke(new CardEvent
                {
                    Kind = CardEventKind.Show,
                    Session = vm,
                    AutoHideAfterMs = (int)FailedCardDuration.TotalMilliseconds,
                });
                break;

            default:
                // IndicatorStatus.Validate already rejected this upstream;
                // reaching here would be a programmer error.
                throw new InvalidOperationException(
                    $"EmitCardEventForTransition called with unknown status '{vm.Status}'");
        }
    }

    private void AddToOrder(SessionViewModel vm)
    {
        lock (_orderLock)
        {
            // Insert by UpdatedAt then SessionId for stable order.
            int i = 0;
            for (; i < _ordered.Count; i++)
            {
                var cur = _ordered[i];
                if (vm.UpdatedAt < cur.UpdatedAt) break;
                if (vm.UpdatedAt == cur.UpdatedAt
                    && string.CompareOrdinal(vm.SessionId, cur.SessionId) < 0) break;
            }
            _ordered.Insert(i, vm);
        }
    }

    private void RemoveFromOrder(SessionViewModel vm)
    {
        lock (_orderLock)
        {
            _ordered.Remove(vm);
        }
    }
}
