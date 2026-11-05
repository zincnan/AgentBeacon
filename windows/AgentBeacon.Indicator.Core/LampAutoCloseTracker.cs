namespace AgentBeacon.Indicator.Core;

/// <summary>Tracks continuous terminal-state time for locally displayed lamps.</summary>
public sealed class LampAutoCloseTracker
{
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, (string Status, DateTimeOffset Since)> _entries =
        new(StringComparer.Ordinal);

    public LampAutoCloseTracker(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public void Observe(string sessionId, string status, DateTimeOffset now)
    {
        IndicatorStatus.Validate(status);
        if (status is not (IndicatorStatus.Completed or IndicatorStatus.Failed))
        {
            Remove(sessionId);
            return;
        }

        // Replayed snapshots and same-color updates do not extend the timer.
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.Status != status)
            _entries[sessionId] = (status, now);
    }

    public IReadOnlyList<string> GetExpiredSessionIds(DateTimeOffset now)
        => _entries.Where(pair => now - pair.Value.Since >= _timeout)
            .Select(pair => pair.Key).ToArray();

    public void Remove(string sessionId) => _entries.Remove(sessionId);

    public void Clear() => _entries.Clear();
}
