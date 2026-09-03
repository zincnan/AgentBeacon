namespace AgentBeacon.Indicator;

/// <summary>
/// UI-only constants. These are NOT part of HTTP Protocol v1 and can be
/// tuned freely without affecting the Agent → Receiver contract.
/// </summary>
public static class IndicatorUiConstants
{
    // --- Lamp module visual dimensions ---

    /// <summary>Total width of one Agent lamp module (housing + 3 lights + label).</summary>
    public const double ModuleWidth = 84;

    /// <summary>Total height of one Agent lamp module (label + housing + 3 lights).</summary>
    public const double ModuleHeight = 108;

    /// <summary>Vertical gap between adjacent lamp modules.</summary>
    public const double ModuleGap = 12;

    /// <summary>Right-edge margin (px) between lamp column and primary monitor's working-area right edge.</summary>
    public const double LampRightMargin = 12;

    /// <summary>
    /// Top safe margin (px) added to the primary monitor's working-area
    /// top before placing the first module. Chosen to sit clearly BELOW
    /// the standard Windows 10 / 11 caption-button row.
    /// </summary>
    public const double LampTopSafeMargin = 64;

    // --- Lamp light sizes ---

    /// <summary>Diameter of one physical light dot inside the module.</summary>
    public const double LightDiameter = 17;

    /// <summary>Vertical gap between adjacent light dots inside the module.</summary>
    public const double LightGap = 6;

    /// <summary>
    /// Housing corner radius. NOTE: this is a System.Windows.CornerRadius
    /// (not a bare double) because LampModuleView.xaml binds it via
    /// {x:Static}. x:Static performs NO type conversion — feeding a
    /// double const into the CornerRadius-typed property throws
    /// XamlParseException ("'10' is not a valid value for CornerRadius")
    /// at runtime, which is exactly the bug that made the whole UI
    /// silently invisible (the exception was swallowed per-snapshot).
    /// </summary>
    public static readonly System.Windows.CornerRadius ModuleCornerRadius = new(10);

    /// <summary>
    /// Padding inside the housing (around the lights). Same rule as
    /// ModuleCornerRadius: must be a Thickness for {x:Static} binding.
    /// </summary>
    public static readonly System.Windows.Thickness ModuleHousingPadding = new(7);

    // --- Card animation ---

    /// <summary>Duration of the card slide-out (left) animation.</summary>
    public const int CardSlideOutDurationMs = 220;

    /// <summary>Duration of the card slide-back (right) / retract animation.</summary>
    public const int CardSlideBackDurationMs = 200;

    // --- Lamp blink (status-change attention flash) ---

    /// <summary>
    /// How long a newly-changed non-running lamp blinks. Real traffic
    /// lights flash on/off; the blink draws the eye to the yellow /
    /// green / red transition. Running (blue) never blinks.
    /// </summary>
    public const int BlinkDurationMs = 4600;

    /// <summary>One half-cycle of the blink (lit → dark or dark → lit).</summary>
    public const int BlinkHalfCycleMs = 320;

    /// <summary>
    /// Gap (px) between the right edge of the card and the left edge of
    /// the lamp module it visually belongs to.
    /// </summary>
    public const double CardToModuleGap = 12;

    /// <summary>Vertical gap (px) between collision-adjusted notification cards.</summary>
    public const double CardVerticalGap = 12;

    // --- Card stay durations (UI policy, not Protocol v1) ---
    //
    // Public so it can be referenced from LampStateMapper.CardStayPolicy
    // and from tests if. / Reference via IndicatorUiConstants directly
    // from the WPF layer; the Core layer uses LampStateMapper.CardStayPolicy
    // for pure mappings so Core tests stay free of WPF deps.
    // (Card stays are mirrored in LampStateMapper.CardStayPolicy.)
}
