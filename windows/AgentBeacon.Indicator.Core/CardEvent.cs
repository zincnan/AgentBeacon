namespace AgentBeacon.Indicator.Core;

public enum CardEventKind
{
    /// <summary>Show a fresh card for the session.</summary>
    Show,
    /// <summary>Update the visible card's content (e.g. message changed).</summary>
    Update,
    /// <summary>Hide the visible card now.</summary>
    Hide,
}

/// <summary>
/// Decoupled notification card event. The store doesn't know about WPF
/// windows — it just emits these; the WPF layer translates them into
/// window visibility / content changes.
/// </summary>
public sealed class CardEvent
{
    public required CardEventKind Kind { get; init; }
    public required SessionViewModel Session { get; init; }

    /// <summary>When Kind == Show for a completed card, the card auto-hides after this many ms.</summary>
    public int? AutoHideAfterMs { get; init; }
}
