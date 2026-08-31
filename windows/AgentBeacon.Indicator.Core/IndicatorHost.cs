using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Pure-C# orchestrator: connects PipeClient → SessionViewModelStore, and
/// surfaces card events / pipe errors to the WPF layer via callbacks. Keeps
/// WPF-dependent code minimal and unit-testable logic separate.
///
/// All callbacks are invoked on the WPF thread by the caller (MainWindow
/// marshals via Dispatcher). PipeClient runs its own loop on the threadpool.
/// </summary>
public sealed class IndicatorHost : IDisposable
{
    private readonly PipeClient _pipe;
    private readonly SessionViewModelStore _store;
    private readonly Action<IReadOnlyList<SessionViewModel>> _onSnapshotReceived;
    private readonly Action<CardEvent> _onCardEvent;
    private readonly Action<string> _onPipeError;

    public IndicatorHost(
        string pipeName,
        Action<IReadOnlyList<SessionViewModel>> onSnapshotReceived,
        Action<CardEvent> onCardEvent,
        Action<string> onPipeError)
    {
        _onSnapshotReceived = onSnapshotReceived;
        _onCardEvent = onCardEvent;
        _onPipeError = onPipeError;

        _store = new SessionViewModelStore();
        _store.CardEvent += ev =>
        {
            try { _onCardEvent(ev); } catch { /* WPF marshalling errors are reported elsewhere */ }
        };

        _pipe = new PipeClient(pipeName);
        _pipe.OnSnapshot += env =>
        {
            // The pipe's snapshot is the source of truth for "now" too, but
            // we still use UtcNow here — drift is irrelevant for the dedup
            // rules which are based on UpdatedAt deltas.
            _store.ApplySnapshot(env.Sessions, DateTimeOffset.UtcNow);
            try { _onSnapshotReceived(_store.Sessions); } catch { }
        };
        _pipe.OnError += ex => _onPipeError($"pipe error: {ex.Message}");
    }

    /// <summary>Drives the 5-minute completed-session cleanup. Call from WPF timer tick.</summary>
    public void Tick(DateTimeOffset now) => _store.Tick(now);

    public IReadOnlyList<SessionViewModel> Sessions => _store.Sessions;

    public SessionViewModelStore Store => _store;

    public void Dispose()
    {
        _pipe.Dispose();
    }
}
