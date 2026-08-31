namespace AgentBeacon.Indicator;

/// <summary>
/// UI-only constants. These are NOT part of HTTP Protocol v1 and can be
/// tuned freely without affecting the Agent → Receiver contract.
/// </summary>
public static class IndicatorUiConstants
{
    /// <summary>Duration of the right-to-left slide-in animation when a card first appears.</summary>
    public const int SlideInDurationMs = 220;

    /// <summary>Right-edge gutter (px) between card window and monitor edge.</summary>
    public const double CardRightGutter = 12;

    /// <summary>Vertical gap (px) between stacked cards.</summary>
    public const double CardStackGap = 8;

    /// <summary>
    /// Right-edge margin (px) between the lamp column and the primary
    /// monitor's working-area right edge. Slightly larger than the
    /// old 4 px so the lamp visually "sits" off the edge rather than
    /// touching it.
    /// </summary>
    public const double LampRightMargin = 12;

    /// <summary>
    /// Top safe margin (px) added to the primary monitor's working-area
    /// top before placing the lamp column. Chosen to sit clearly BELOW
    /// the standard Windows 10 / 11 caption-button row in a maximized
    /// window (browser, VS Code, Terminal, etc.) so the lamp does not
    /// overlap the user's Close / Maximize / Minimize buttons.
    /// Same value is used to clamp the top of the stacked notification
    /// cards so a card burst cannot crawl up into the caption area
    /// either.
    /// </summary>
    public const double LampTopSafeMargin = 64;
}
