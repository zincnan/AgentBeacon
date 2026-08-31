using System.Collections.Concurrent;
using AgentBeacon.Shared;

namespace AgentBeacon.Receiver;

/// <summary>
/// Single source of truth for the Receiver's session state. Last-received-wins
/// semantics per session_id.
///
/// Concurrency model (Round 2 close-out):
///
/// 1. Every mutation goes through <see cref="Upsert"/>, which is serialized
///    on <c>_stateLock</c>. While one Upsert is running, no other Upsert
///    (and no <see cref="SubscribeWithInitial"/>) can run.
///
/// 2. Subscribers are invoked synchronously, INSIDE the same critical
///    section. A subscriber that does only non-blocking work
///    (Channel TryWrite) preserves the property: the order in which
///    subscribers receive a snapshot is exactly the order in which
///    Upserts acquire <c>_stateLock</c>.
///
/// 3. <see cref="SubscribeWithInitial"/> atomically subscribes the caller
///    AND runs the initial-snapshot callback, also under <c>_stateLock</c>.
///    This eliminates the connect/update race: any Upsert that has not
///    completed before <see cref="SubscribeWithInitial"/> returns will
///    fire the subscriber AFTER the initial snapshot is enqueued, so the
///    new client's channel ends up strictly monotonic in store-order time.
/// </summary>
public sealed class SessionStateStore
{
    private readonly ConcurrentDictionary<string, SessionSnapshot> _byId =
        new(StringComparer.Ordinal);

    private readonly object _stateLock = new();
    private readonly List<Action<IReadOnlyList<SessionSnapshot>>> _subscribers = new();

    /// <summary>Current full snapshot, ordered by UpdatedAt then SessionId for stability.</summary>
    public IReadOnlyList<SessionSnapshot> CurrentSnapshot()
    {
        lock (_stateLock)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>
    /// Insert or replace a session's snapshot. Whole-event replacement:
    /// no field-level merging. Fires all subscribers synchronously while
    /// holding the store lock; subscribers MUST be non-blocking
    /// (Channel TryWrite is the canonical example).
    /// </summary>
    public void Upsert(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_stateLock)
        {
            _byId[snapshot.SessionId] = snapshot;
            var snap = SnapshotLocked();
            // Copy the subscriber list under the same lock; iterate outside
            // would risk a TOCTOU between Add/Remove and the foreach, AND
            // would not serialize the enqueue order across Upserts.
            foreach (var cb in _subscribers.ToArray())
            {
                try
                {
                    cb(snap);
                }
                catch
                {
                    // A misbehaving subscriber must not affect HTTP request
                    // handling or other subscribers. The Named Pipe publisher
                    // is itself designed to swallow exceptions; this catch
                    // is a final safety net.
                }
            }
        }
    }

    /// <summary>
    /// Atomically subscribe to future changes AND run the supplied
    /// <paramref name="initialEnqueue"/> callback with the current snapshot,
    /// all under the store lock. The callback runs before this method
    /// returns, so any Upsert that observes this subscription will fire
    /// <paramref name="onChange"/> AFTER the initial enqueue — the new
    /// client's channel can never end on a snapshot older than its initial.
    ///
    /// Both callbacks are required to be non-blocking.
    /// </summary>
    public IDisposable SubscribeWithInitial(
        Action<IReadOnlyList<SessionSnapshot>> onChange,
        Action<IReadOnlyList<SessionSnapshot>> initialEnqueue)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        ArgumentNullException.ThrowIfNull(initialEnqueue);
        lock (_stateLock)
        {
            _subscribers.Add(onChange);
            initialEnqueue(SnapshotLocked());
        }
        return new Subscription(this, onChange);
    }

    private List<SessionSnapshot> SnapshotLocked()
        => _byId.Values
            .OrderBy(s => s.UpdatedAt)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();

    private void Unsubscribe(Action<IReadOnlyList<SessionSnapshot>> callback)
    {
        lock (_stateLock)
        {
            _subscribers.Remove(callback);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private SessionStateStore? _owner;
        private Action<IReadOnlyList<SessionSnapshot>>? _cb;

        public Subscription(SessionStateStore owner, Action<IReadOnlyList<SessionSnapshot>> cb)
        {
            _owner = owner;
            _cb = cb;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var cb = Interlocked.Exchange(ref _cb, null);
            if (owner is not null && cb is not null)
            {
                owner.Unsubscribe(cb);
            }
        }
    }
}
