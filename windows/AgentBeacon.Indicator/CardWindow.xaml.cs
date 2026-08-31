using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentBeacon.Indicator.Core;

namespace AgentBeacon.Indicator;

public partial class CardWindow : Window
{
    public string SessionId { get; private set; } = "";

    public CardWindow()
    {
        InitializeComponent();
    }

    public void UpdateFrom(SessionViewModel vm)
    {
        SessionId = vm.SessionId;
        AgentText.Text = vm.Agent;
        HostText.Text = vm.Host ?? "";
        HostText.Visibility = string.IsNullOrEmpty(vm.Host) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = vm.Status.ToUpperInvariant();
        MessageText.Text = vm.Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(vm.Message) ? Visibility.Collapsed : Visibility.Visible;
        SessionIdText.Text = vm.SessionId;
        StatusDot.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(vm.LampColor));
    }

    /// <summary>
    /// Slide the card in from off-screen right. The animation is a one-shot
    /// tween from "off-screen-right" to <paramref name="targetLeft"/>, with
    /// the window's base Left pre-set to <paramref name="targetLeft"/> so
    /// that once the animation finishes the window rests exactly at the
    /// target position.
    ///
    /// On the animation's Completed event we detach it via
    /// BeginAnimation(LeftProperty, null) and pin <c>Left = targetLeft</c>.
    /// We do NOT touch <c>Top</c> in Completed: LayoutCards
    /// (<see cref="MainWindow.LayoutCards"/>) is the single source of
    /// truth for vertical position, and it may legitimately have moved
    /// <c>Top</c> during the 220 ms animation window if another Show /
    /// Hide / SizeChanged arrived. Re-pinning <c>Top</c> here with a value
    /// captured 220 ms earlier would clobber the latest layout.
    /// </summary>
    public void SlideInFromRight(double targetLeft, double targetTop)
    {
        Top = targetTop;
        // Base value = final target. Animation From = off-screen-right.
        // After FillBehavior.Stop the property falls back to the base,
        // which is now the intended final position.
        Left = targetLeft;
        // Make sure no stale animation is still attached (defensive — only
        // first Show runs this, but the card object can be reused after
        // Hide via ShowOrUpdateCard paths).
        BeginAnimation(LeftProperty, null);
        ShowNoActivate();

        var anim = new DoubleAnimation
        {
            From = targetLeft + ActualWidth,
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(IndicatorUiConstants.SlideInDurationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            // Detach the animation so future LayoutCards writes to Left
            // take effect immediately (FillBehavior.Stop would otherwise
            // keep the animation "applied" and intercept subsequent
            // writes), and pin Left = targetLeft so the final on-screen
            // position matches the seat LayoutCards computed at slide
            // start. We deliberately do not re-pin Top here: see the
            // xmldoc above.
            BeginAnimation(LeftProperty, null);
            Left = targetLeft;
        };
        BeginAnimation(LeftProperty, anim);
    }

    /// <summary>
    /// Show without stealing focus from the foreground app. We use
    /// ShowActivated=False (set in XAML) plus a no-activate Win32 style on
    /// the HWND for robustness against apps like Terminal / VS Code.
    /// </summary>
    public void ShowNoActivate()
    {
        Show();
        NoActivateHelper.EnsureNoActivate(this);
    }

    public new void Hide()
    {
        base.Hide();
    }
}
