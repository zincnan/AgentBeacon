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
    public const double CardRightGutter = 8;

    /// <summary>Vertical gap (px) between stacked cards.</summary>
    public const double CardStackGap = 8;

    /// <summary>Right-edge gutter (px) between the lamp column and monitor edge.</summary>
    public const double LampRightGutter = 4;

    /// <summary>Top gutter (px) for the lamp column.</summary>
    public const double LampTopGutter = 8;
}
