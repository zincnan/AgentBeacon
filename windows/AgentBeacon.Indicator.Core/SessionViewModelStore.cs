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
///   completed -> card auto-retracts; lamp entry stays visible until
///                a newer snapshot changes/removes it or the user dismisses it.
///   failed    -> card for 10s, lamp entry long-lived until status changes (red).
///
/// Dedup: a card for (session_id, updated_at) is shown at most once; if the
/// same updated_at arrives again, no event is emitted. A newer updated_at
/// replaces and re-emits.
///
/// Completed lamps are not aged out by the Indicator. A completed/idle
/// agent remains visible as a green lamp until Receiver stops reporting
/// it, a newer status replaces it, or the user explicitly dismisses it.
/// </summary>
public sealed class SessionViewModelStore
{
    /// <summary>Card visibility duration for "approval".</summary>
    public static readonly TimeSpan ApprovalCardDuration = TimeSpan.FromSeconds(30);

    /// <summary>Card visibility duration for "completed".</summary>
    public static readonly TimeSpan CompletedCardDuration = TimeSpan.FromSeconds(30);

    /// <summary>Card visibility duration for "failed".</summary>
    public static readonly TimeSpan FailedCardDuration = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SessionViewModel> _byId =
        new(StringComparer.Ordinal);

    /// <summary>Last UpdatedAt at which we emitted a Show event for a session, per session.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCardShownUpdatedAt =
        new(StringComparer.Ordinal);

    /// <summary>Status at the time we last emitted a card event for a session.</summary>
    private readonly ConcurrentDictionary<string, string> _lastCardShownStatus =
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
        //    What this loop DOES clear for a removed live session: its
        //    own dedup state (_lastCardShownUpdatedAt /
        //    _lastCardShownStatus).
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
        }
    }

    /// <summary>
    /// Periodic maintenance hook. Completed lamps are intentionally not
    /// aged out here; they remain visible until replaced, removed by
    /// Receiver, or manually dismissed in the Indicator UI.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        _ = now;
    }

    /// <summary>Test helper: clear all state.</summary>
    public void Reset()
    {
        _byId.Clear();
        _lastCardShownStatus.Clear();
        _lastCardShownUpdatedAt.Clear();
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
