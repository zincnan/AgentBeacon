namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Per-card input to <see cref="AnchoredCardLayout.Compute"/>: where
/// the card would naturally sit if there were no other cards, and how
/// tall it actually is after WPF has measured it.
/// </summary>
public readonly struct AnchoredCardInput
{
    /// <summary>The session this card belongs to (used as the dictionary key).</summary>
    public required string SessionId { get; init; }

    /// <summary>The vertical center of the owning LampModule, in screen Y.</summary>
    public required double ModuleCenterYScreen { get; init; }

    /// <summary>Card's measured height (post-WPF-measure).</summary>
    public required double Height { get; init; }
}

/// <summary>
/// Per-card output of <see cref="AnchoredCardLayout.Compute"/>: where
/// to actually place the card on screen after collision adjustment.
/// </summary>
public readonly struct AnchoredCardPlacement
{
    /// <summary>True when the card fits inside the safe area and should be shown.</summary>
    public bool Visible { get; init; }

    /// <summary>Final screen-Y of the card's top edge.</summary>
    public double Top { get; init; }
}

/// <summary>
/// Pure collision-aware layout decision for notification cards anchored
/// to their owning LampModule.
///
/// The product rule: each Card sits to the LEFT of its own Agent
/// traffic-light module, vertically centered on the module, with a
/// small gap between the card's right edge and the module's left edge.
/// Multiple cards must NOT overlap each other.
///
/// Newer-wins policy: when there isn't enough vertical room for every
/// card, the NEWER card (later in the input list = later in insertion
/// order = more recently Show'd) stays at its preferred position, and
/// OLDER cards are pushed UP to make room. If an older card would have
/// to climb above <c>safeTop</c>, it is marked not visible.
///
/// Algorithm (MVP, deterministic):
///
///   1. Inputs come in insertion order: oldest first, newest last.
///   2. We process the list from newest to oldest (reverse iteration).
///      The newest card sits at its natural top first.
///   3. For each subsequent (older) card:
///        a. naturalTop = moduleCenter - height/2 (clamped to
///           [safeTop, safeBottom - height]).
///        b. maxTop = cursor - gap - height  (must clear the next-newer
///           card already placed above us).
///        c. top = min(naturalTop, maxTop) clamped to safe band.
///        d. If top &lt; safeTop: HIDDEN.
///        e. Else: VISIBLE at top. cursor = top.
///
/// No fifth state, no notification center history, no z-order tricks.
/// </summary>
public static class AnchoredCardLayout
{
    public static IReadOnlyDictionary<string, AnchoredCardPlacement> Compute(
        IReadOnlyList<AnchoredCardInput> inputs,
        double safeTop,
        double safeBottom,
        double gap)
    {
        var result = new Dictionary<string, AnchoredCardPlacement>(StringComparer.Ordinal);
        if (inputs.Count == 0) return result;

        // cursor = top of the next-newer card already placed. Until
        // we've placed any card, there's no constraint above us, so
        // we leave cursor at a sentinel that disables the maxTop
        // check.
        bool anyPlaced = false;
        double cursor = 0;

        for (int i = inputs.Count - 1; i >= 0; i--)
        {
            var inp = inputs[i];

            double natural = inp.ModuleCenterYScreen - inp.Height / 2.0;
            // Pre-clamp natural into the safe band.
            if (natural < safeTop) natural = safeTop;
            if (natural + inp.Height > safeBottom) natural = safeBottom - inp.Height;

            double top = natural;
            if (anyPlaced)
            {
                // Don't overlap the next-newer card above us.
                double maxTop = cursor - gap - inp.Height;
                if (top > maxTop) top = maxTop;
            }

            if (top < safeTop)
            {
                result[inp.SessionId] = new AnchoredCardPlacement
                {
                    Visible = false,
                    Top = natural, // reported for caller diagnostics
                };
                // A hidden card does not reserve space.
                continue;
            }

            result[inp.SessionId] = new AnchoredCardPlacement { Visible = true, Top = top };
            cursor = top;
            anyPlaced = true;
        }

        return result;
    }
}
