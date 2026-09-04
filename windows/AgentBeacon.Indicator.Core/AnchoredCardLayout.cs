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
    /// <summary>True when the card fits inside the safe area without overlapping another card, and should be shown.</summary>
    public bool Visible { get; init; }

    /// <summary>Final screen-Y of the card's top edge.</summary>
    public double Top { get; init; }
}

/// <summary>
/// Pure collision-aware layout decision for notification cards anchored
/// to their owning LampModule.
///
/// The product rule: each Card sits to the LEFT of its own Agent
/// traffic-light module, vertically centered on the module. A card that
/// cannot sit at its own module's side without overlapping another card
/// is NOT shown displaced — it is hidden. A displaced card would visually
/// attach to the WRONG lamp, which is worse than seeing fewer cards.
///
/// Newest-wins: inputs arrive oldest first; the newest card claims its
/// natural seat first, and any OLDER card whose natural seat would
/// overlap an already-placed card is hidden. A hidden card comes back on
/// its next Show, or as soon as the newer card retracts and frees the
/// space (the caller re-runs this layout on retract).
///
/// Algorithm (per card, newest → oldest):
///   1. natural = clamp(moduleCenter - height/2, safeTop, safeBottom - height).
///   2. If [natural, natural+height) overlaps any placed card's band
///      (expanded by gap on each side) → HIDDEN.
///   3. Else VISIBLE at natural; record the band.
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

        var placed = new List<(double Top, double Bottom)>();

        for (int i = inputs.Count - 1; i >= 0; i--) // newest → oldest
        {
            var inp = inputs[i];

            double natural = inp.ModuleCenterYScreen - inp.Height / 2.0;
            if (natural < safeTop) natural = safeTop;
            if (natural + inp.Height > safeBottom) natural = safeBottom - inp.Height;
            double bottom = natural + inp.Height;

            bool overlaps = placed.Any(band =>
                natural < band.Bottom + gap && bottom > band.Top - gap);

            if (overlaps)
            {
                result[inp.SessionId] = new AnchoredCardPlacement
                {
                    Visible = false,
                    Top = natural, // reported for diagnostics; card will be hidden
                };
                continue;
            }

            result[inp.SessionId] = new AnchoredCardPlacement { Visible = true, Top = natural };
            placed.Add((natural, bottom));
        }

        return result;
    }
}
