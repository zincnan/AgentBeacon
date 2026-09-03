using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AgentBeacon.Indicator.Core;

namespace AgentBeacon.Indicator;

/// <summary>
/// One Agent traffic-light module. Renders an agent label + 3 physical
/// light positions (top red, middle yellow, bottom blue/green) in a
/// compact dark housing.
///
/// Active state is driven by:
///   AgentLabelText — display label (ellipsis if too long)
///   TooltipText    — full agent / session_id / host info on hover
///   ActiveSlot     — which physical slot is currently lit
///   BottomColorHex — color for the bottom slot (blue for running, green for completed)
///
/// All lights have a fixed geometric size; only their Fill changes between
/// active / inactive. There is no animation other than the standard WPF
/// data-binding refresh — we deliberately avoid neon-style animations on
/// the lamps.
/// </summary>
public partial class LampModuleView : UserControl
{
    public static readonly DependencyProperty AgentLabelTextProperty =
        DependencyProperty.Register(
            nameof(AgentLabelText), typeof(string), typeof(LampModuleView),
            new PropertyMetadata(string.Empty,
                (d, e) => ((LampModuleView)d).AgentLabel.Text = (string)e.NewValue));

    public static readonly DependencyProperty TooltipTextProperty =
        DependencyProperty.Register(
            nameof(TooltipText), typeof(string), typeof(LampModuleView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActiveSlotProperty =
        DependencyProperty.Register(
            nameof(ActiveSlot), typeof(LampSlot), typeof(LampModuleView),
            new PropertyMetadata(LampSlot.Bottom,
                (d, e) => ((LampModuleView)d).RefreshLights()));

    public static readonly DependencyProperty BottomColorHexProperty =
        DependencyProperty.Register(
            nameof(BottomColorHex), typeof(string), typeof(LampModuleView),
            new PropertyMetadata("#2F81F7",
                (d, e) => ((LampModuleView)d).RefreshLights()));

    public string AgentLabelText
    {
        get => (string)GetValue(AgentLabelTextProperty);
        set => SetValue(AgentLabelTextProperty, value);
    }

    public string TooltipText
    {
        get => (string)GetValue(TooltipTextProperty);
        set => SetValue(TooltipTextProperty, value);
    }

    public LampSlot ActiveSlot
    {
        get => (LampSlot)GetValue(ActiveSlotProperty);
        set => SetValue(ActiveSlotProperty, value);
    }

    public string BottomColorHex
    {
        get => (string)GetValue(BottomColorHexProperty);
        set => SetValue(BottomColorHexProperty, value);
    }

    /// <summary>
    /// Owning session id. Set by MainWindow when the module is created;
    /// used to route the dismiss (right-click → 关闭此灯) action back to
    /// the right session.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Current status applied via <see cref="ApplyStatus"/>. Empty before
    /// the first apply.
    /// </summary>
    public string Status { get; private set; } = "";

    /// <summary>
    /// Raised when the user picks "关闭此灯" in the module's context
    /// menu. MainWindow tears the module down and records a dismissal
    /// watermark; the lamp is only rebuilt when the session produces a
    /// newer event.
    /// </summary>
    public event EventHandler? Dismissed;

    /// <summary>
    /// Apply a (possibly changed) status: refresh the three lights and,
    /// when the status actually CHANGED and the new state is not running,
    /// blink the newly-lit light like a real traffic light for
    /// BlinkDurationMs. Running (blue) never blinks; a lamp's first
    /// appearance does not blink either (only transitions do).
    /// </summary>
    public void ApplyStatus(string status)
    {
        var st = LampStateMapper.ForStatus(status); // validates, fail-fast
        var changed = Status.Length > 0 && Status != status;
        StopBlink();
        Status = status;
        ActiveSlot = st.ActiveSlot;
        BottomColorHex = st.ActiveColorHex;
        if (changed && status != "running")
        {
            BlinkActiveLight();
        }
    }

    /// <summary>
    /// Flash the currently active light on/off (opacity 1 ↔ 0.35) for
    /// BlinkDurationMs, then leave it solidly lit.
    /// </summary>
    public void BlinkActiveLight()
    {
        if (TopLight == null || MiddleLight == null || BottomLight == null) return;
        var target = ActiveSlot switch
        {
            LampSlot.Top => TopLight,
            LampSlot.Middle => MiddleLight,
            _ => BottomLight,
        };
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 0.35,
            Duration = TimeSpan.FromMilliseconds(IndicatorUiConstants.BlinkHalfCycleMs),
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(
                TimeSpan.FromMilliseconds(IndicatorUiConstants.BlinkDurationMs)),
        };
        anim.Completed += (_, _) =>
        {
            target.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            target.Opacity = 1;
        };
        target.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
    }

    /// <summary>Stop any in-flight blink and leave all lights solid.</summary>
    private void StopBlink()
    {
        if (TopLight == null || MiddleLight == null || BottomLight == null) return;
        foreach (var light in new[] { TopLight, MiddleLight, BottomLight })
        {
            light.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            light.Opacity = 1;
        }
    }

    public LampModuleView()
    {
        InitializeComponent();
        ToolTip = TooltipText;
        // Bind ToolTip reactively when TooltipText changes
        DataContext = this;
        Loaded += (_, _) =>
        {
            ToolTip = TooltipText;
            RefreshLights();
        };
        DismissItem.Click += (_, _) =>
        {
            if (SessionId is not null)
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>
    /// Update the three light colors based on ActiveSlot and BottomColorHex.
    /// All four Protocol v1 statuses map to exactly one active slot; the
    /// other two are dim. No fifth visual state is ever produced.
    /// </summary>
    private void RefreshLights()
    {
        if (TopLight == null || MiddleLight == null || BottomLight == null) return;

        string activeHex = ActiveSlot switch
        {
            LampSlot.Top => "#F85149",     // failed red
            LampSlot.Middle => "#D29922",  // approval yellow
            LampSlot.Bottom => BottomColorHex, // running blue / completed green
            _ => throw new ArgumentException($"unknown lamp slot '{ActiveSlot}'", nameof(ActiveSlot)),
        };
        string inactiveHex = LampStateMapper.LampModuleState.InactiveColorHex;

        SetLight(TopLight, ActiveSlot == LampSlot.Top, activeHex, inactiveHex);
        SetLight(MiddleLight, ActiveSlot == LampSlot.Middle, activeHex, inactiveHex);
        SetLight(BottomLight, ActiveSlot == LampSlot.Bottom, activeHex, inactiveHex);
        ApplyGlow();

        ToolTip = TooltipText;
    }

    private static void SetLight(Ellipse e, bool active, string activeHex, string inactiveHex)
    {
        // Fill only — the active light's glow is applied by ApplyGlow().
        e.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(active ? activeHex : inactiveHex));
    }

    /// <summary>
    /// Perceived-brightness boost (Round 10): the ACTIVE light gets a soft
    /// same-color glow (DropShadowEffect, BlurRadius 9 / Opacity 0.85);
    /// inactive lights get no effect. Canonical lamp hexes stay untouched
    /// while the lit slot pops.
    /// </summary>
    private void ApplyGlow()
    {
        var lights = new (System.Windows.Shapes.Ellipse Light, LampSlot Slot)[]
        {
            (TopLight, LampSlot.Top),
            (MiddleLight, LampSlot.Middle),
            (BottomLight, LampSlot.Bottom),
        };
        string activeHex = ActiveSlot switch
        {
            LampSlot.Top => "#F85149",
            LampSlot.Middle => "#D29922",
            LampSlot.Bottom => BottomColorHex,
            _ => "#2F81F7",
        };
        foreach (var (light, slot) in lights)
        {
            if (slot == ActiveSlot)
            {
                light.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString(activeHex),
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.85,
                };
            }
            else if (light.Effect is not null)
            {
                light.Effect = null;
            }
        }
    }
}
