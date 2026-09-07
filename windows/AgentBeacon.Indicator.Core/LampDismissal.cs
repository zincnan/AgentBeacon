namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Pure rule for the "dismissed lamp" feature.
///
/// A user may right-click a lamp module and dismiss it: the module is
/// torn down entirely (no hidden windows kept around). The dismissal
/// records the updated_at of the LAST event the user chose to hide.
///
/// The lamp comes back only when the session produces a NEWER event
/// (updated_at strictly greater than the dismissal watermark):
///   - full-snapshot re-broadcasts of the same (session_id, updated_at)
///     do NOT resurrect a dismissed lamp;
///   - any new status / message / updated_at DOES — the session is
///     alive again and the user should see it.
/// </summary>
public static class LampDismissal
{
    /// <summary>
    /// True when the lamp should be (re)shown for an incoming event.
    /// </summary>
    /// <param name="dismissedAt">
    /// The updated_at recorded when the user dismissed the lamp, or
    /// null when the lamp was never dismissed.
    /// </param>
    public static bool ShouldShow(DateTimeOffset? dismissedAt, DateTimeOffset incomingUpdatedAt)
        => dismissedAt is null || incomingUpdatedAt > dismissedAt.Value;
}
