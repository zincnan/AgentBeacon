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

        ToolTip = TooltipText;
    }

    private static void SetLight(Ellipse e, bool active, string activeHex, string inactiveHex)
    {
        e.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(active ? activeHex : inactiveHex));
        e.Effect = active
            ? new DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.26,
                Color = (Color)ColorConverter.ConvertFromString(activeHex),
            }
            : null;
    }
}
