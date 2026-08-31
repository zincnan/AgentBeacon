namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Pure stack-layout decision used by the WPF Indicator when arranging
/// notification cards from the bottom-right corner upward. Zero WPF /
/// zero I/O so it can be unit-tested without a Dispatcher or a Window.
///
/// Contract:
///   - Given card heights in insertion order (oldest first) and a
///     safe vertical band [safeTop, safeBottom], compute each card's
///     natural top assuming a clean bottom-up stack with `gap` pixels
///     between cards.
///   - Cards whose natural top is &lt; safeTop do not fit cleanly into
///     the safe area and should be hidden by the caller.
///   - The bottommost card's natural bottom is always == safeBottom
///     (the stack grows upward from there).
///   - No two "fits" cards share the same top.
/// </summary>
public static class CardStackLayout
{
    public readonly struct CardPlacement
    {
        public double Top { get; init; }
        public bool Fits { get; init; }
    }

    public static CardPlacement[] Compute(
        IReadOnlyList<double> heights,
        double safeTop,
        double safeBottom,
        double gap)
    {
        int n = heights.Count;
        var result = new CardPlacement[n];
        if (n == 0) return result;

        double cursor = safeBottom;
        for (int i = n - 1; i >= 0; i--)
        {
            cursor -= heights[i];
            result[i] = new CardPlacement
            {
                Top = cursor,
                Fits = cursor >= safeTop,
            };
            cursor -= gap;
        }
        return result;
    }
}
