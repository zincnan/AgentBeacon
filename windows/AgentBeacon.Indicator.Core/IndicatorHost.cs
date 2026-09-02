using System.Diagnostics;
using System.IO;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Pure-C# orchestrator: connects PipeClient → SessionViewModelStore, and
/// surfaces card events / pipe errors to the WPF layer via callbacks. Keeps
/// WPF-dependent code minimal and unit-testable logic separate.
///
/// Snapshot callbacks are delivered before any CardEvents produced by
/// that same snapshot, so WPF can build modules before cards anchor to
/// them. WPF callbacks marshal themselves via Dispatcher because
/// PipeClient runs its own loop on the threadpool.
/// </summary>
public sealed class IndicatorHost : IDisposable
{
    private readonly PipeClient? _pipe;
    private readonly SessionViewModelStore _store;
    private readonly Action<IReadOnlyList<SessionViewModel>> _onSnapshotReceived;
    private readonly Action<CardEvent> _onCardEvent;
    private readonly Action<string> _onPipeError;
    private readonly object _storeGate = new();
    private List<CardEvent>? _capturedEvents;

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
        _store.CardEvent += DispatchOrCaptureCardEvent;

        _pipe = new PipeClient(pipeName);
        _pipe.OnSnapshot += env => ProcessSnapshot(env.Sessions, DateTimeOffset.UtcNow);
        _pipe.OnError += ex => ReportPipeError(ex);
    }

    public IndicatorHost(
        Action<IReadOnlyList<SessionViewModel>> onSnapshotReceived,
        Action<CardEvent> onCardEvent,
        Action<string> onPipeError)
    {
        _onSnapshotReceived = onSnapshotReceived;
        _onCardEvent = onCardEvent;
        _onPipeError = onPipeError;

        _store = new SessionViewModelStore();
        _store.CardEvent += DispatchOrCaptureCardEvent;
    }

    public void ProcessSnapshot(IReadOnlyList<SessionSnapshot> sessions, DateTimeOffset now)
    {
        lock (_storeGate)
        {
            _capturedEvents = new List<CardEvent>();
            try
            {
                _store.ApplySnapshot(sessions, now);
                InvokeSnapshotReceived(_store.Sessions);
                foreach (var ev in _capturedEvents)
                {
                    InvokeCardEvent(ev);
                }
            }
            finally
            {
                _capturedEvents = null;
            }
        }
    }

    /// <summary>Drives the 5-minute completed-session cleanup. Call from WPF timer tick.</summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_storeGate)
        {
            _store.Tick(now);
        }
    }

    public IReadOnlyList<SessionViewModel> Sessions => _store.Sessions;

    public SessionViewModelStore Store => _store;

    public void Dispose()
    {
        _pipe?.Dispose();
    }

    private void DispatchOrCaptureCardEvent(CardEvent ev)
    {
        if (_capturedEvents is not null)
        {
            _capturedEvents.Add(ev);
            return;
        }

        InvokeCardEvent(ev);
    }

    private void InvokeSnapshotReceived(IReadOnlyList<SessionViewModel> sessions)
    {
        try
        {
            _onSnapshotReceived(sessions);
        }
        catch (Exception ex)
        {
            ReportCallbackError("snapshot", ex);
        }
    }

    private void InvokeCardEvent(CardEvent ev)
    {
        try
        {
            _onCardEvent(ev);
        }
        catch (Exception ex)
        {
            ReportCallbackError("card", ex);
        }
    }

    private void ReportCallbackError(string callbackName, Exception ex)
    {
        var message = $"indicator callback error ({callbackName}): {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine($"[AgentBeacon] {message}");
        WriteLogEntry($"callback={callbackName}", ex.ToString());
        try { _onPipeError(message); }
        catch (Exception reportEx)
        {
            Debug.WriteLine(
                $"[AgentBeacon] indicator error callback failed: {reportEx.GetType().Name}: {reportEx.Message}");
            WriteLogEntry("callback=pipe-error-report", reportEx.ToString());
        }
    }

    private void ReportPipeError(Exception ex)
    {
        var message = $"pipe error: {ex.Message}";
        WriteLogEntry("callback=pipe-error", ex.ToString());
        try { _onPipeError(message); }
        catch (Exception reportEx)
        {
            Debug.WriteLine(
                $"[AgentBeacon] pipe error callback failed: {reportEx.GetType().Name}: {reportEx.Message}");
            WriteLogEntry("callback=pipe-error-report", reportEx.ToString());
        }
    }

    private static void WriteLogEntry(string header, string detail)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "AgentBeacon-indicator.log");
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            File.AppendAllText(path, $"[{timestamp}] {header}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }
}
