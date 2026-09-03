using System.Windows;
using System.Windows.Media.Animation;
using AgentBeacon.Indicator.Core;

namespace AgentBeacon.Indicator;

public partial class CardWindow : Window
{
    /// <summary>
    /// Lifecycle state of a CardWindow. Drives whether the card is
    /// visible, whether a retract animation is in flight, and whether a
    /// new Show event can re-trigger slide-out.
    /// </summary>
    public enum CardPhase
    {
        /// <summary>Card has never been shown or has been fully torn down.</summary>
        Hidden,
        /// <summary>Slide-out animation is in progress (from retracted seat to anchor).</summary>
        Showing,
        /// <summary>Slide-out completed; the card is parked at the anchor; stay timer ticking.</summary>
        Visible,
        /// <summary>Slide-back animation is in progress (from anchor back to retracted seat).</summary>
        Retracting,
    }

    public string SessionId { get; private set; } = "";

    /// <summary>
    /// Anchor position the card is currently sitting at / sliding to.
    /// Maintained by <see cref="MainWindow.AnchorCardOnModule"/> so the
    /// animation system can use it as the "rest" state.
    /// </summary>
    public double TargetLeft { get; set; }
    public double TargetTop { get; set; }

    /// <summary>
    /// "Hidden" seat — just past the right edge of the lamp module.
    /// The card sits here when not shown. Slide-out moves LEFT to
    /// <see cref="TargetLeft"/>; slide-back moves RIGHT back here.
    /// </summary>
    public double RetractLeft { get; private set; }

    private CardPhase _phase = CardPhase.Hidden;
    public CardPhase Phase => _phase;

    /// <summary>
    /// Fires when a slide-back animation completes (or is short-circuited
    /// via <see cref="RetractImmediate"/>). The MainWindow subscribes
    /// per-card and is responsible for removing this card from its
    /// <c>_cards</c> dictionary. We do NOT remove ourselves here because
    /// we want a single, instance-safe ownership check at the consumer
    /// (a stale card must not be allowed to evict a freshly-created
    /// replacement for the same session).
    /// </summary>
    public event EventHandler? RetractCompleted;

    public CardWindow()
    {
        InitializeComponent();
    }

    public void UpdateFrom(SessionViewModel vm)
    {
        SessionId = vm.SessionId;
        AgentText.Text = vm.Agent;
        StatusText.Text = vm.Status.ToUpperInvariant();

        // 状态色装饰：左侧色条 + 圆点用灯的原色 hex；状态文字用加深变体
        // 保证浅色底上的可读性（黄色原色在白底上对比度不足）。
        var (lampHex, deepHex) = StatusColors(vm.Status);
        var lampBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(lampHex));
        var deepBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(deepHex));
        AccentBar.Background = lampBrush;
        StatusDot.Fill = lampBrush;
        StatusText.Foreground = deepBrush;

        MessageText.Text = vm.Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(vm.Message) ? Visibility.Collapsed : Visibility.Visible;
        SessionIdText.Text = "session: " + vm.SessionId;
        HostText.Text = string.IsNullOrEmpty(vm.Host) ? "" : "host: " + vm.Host;
        HostText.Visibility = string.IsNullOrEmpty(vm.Host) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Per-status decoration colors: (lampHex, deepHex). lampHex matches
    /// the traffic light exactly; deepHex is a darkened variant that stays
    /// readable on the light card background. Unknown status throws
    /// (fail-fast, same rule as everywhere else).
    /// </summary>
    private static (string LampHex, string DeepHex) StatusColors(string status) => status switch
    {
        "running" => ("#2F81F7", "#0969DA"),
        "approval" => ("#D29922", "#9A6700"),
        "completed" => ("#3FB950", "#1A7F37"),
        "failed" => ("#F85149", "#CF222E"),
        _ => throw new ArgumentException(
            $"unknown AgentBeacon status '{status}'; expected running/approval/completed/failed",
            nameof(status)),
    };

    /// <summary>
    /// Show without stealing focus from the foreground app. We use
    /// ShowActivated=False (set in XAML) plus a no-activate Win32 style
    /// on the HWND for robustness against apps like Terminal / VS Code.
    /// </summary>
    public void ShowNoActivate()
    {
        Show();
        NoActivateHelper.EnsureNoActivate(this);
    }

    /// <summary>
    /// Measure the card so ActualWidth / ActualHeight are valid. Called
    /// by MainWindow right after construction; we place the card offscreen
    /// at Left = -10000 / Top = -10000, ShowNoActivate it, then UpdateLayout
    /// to force measure before the caller computes the anchor.
    /// </summary>
    public void MeasureOffscreen()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        ShowNoActivate();
        UpdateLayout();
    }

    /// <summary>
    /// Slide the card OUT from off-screen right (just past the lamp
    /// module's right edge) to its anchor position to the LEFT of the
    /// module.
    ///
    /// WPF base-value semantics: we set the WINDOW's base Left to
    /// <paramref name="targetLeft"/> (the final position), then animate
    /// From = <paramref name="retractLeft"/> (start position) To =
    /// <paramref name="targetLeft"/>. With FillBehavior.Stop the
    /// animated value reverts to the base after the animation, i.e.
    /// back to targetLeft — exactly where we want to land.
    /// </summary>
    public void SlideOutFromRightOfModule(
        double targetLeft,
        double targetTop,
        double retractLeft)
    {
        TargetLeft = targetLeft;
        TargetTop = targetTop;
        RetractLeft = retractLeft;
        // Detach any in-flight animation so the new base value is not
        // ignored.
        BeginAnimation(LeftProperty, null);

        // Base value = final target. After the animation ends with
        // FillBehavior.Stop the property reverts to this base.
        Left = targetLeft;
        Top = targetTop;

        _phase = CardPhase.Showing;
        ShowNoActivate();

        var anim = new DoubleAnimation
        {
            From = retractLeft,
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(IndicatorUiConstants.CardSlideOutDurationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            // Animation ended; revert to base. If a new Show has
            // happened during the animation (state Show-during-SlideOut),
            // the new SlideOutFromRightOfModule call already detached
            // this animation and set a fresh base; reaching here just
            // means the original animation finished and the card is now
            // at base = targetLeft. We promote phase to Visible.
            BeginAnimation(LeftProperty, null);
            if (_phase == CardPhase.Showing) _phase = CardPhase.Visible;
        };
        BeginAnimation(LeftProperty, anim);
    }

    /// <summary>
    /// Re-show this card from its authoritative retracted seat. Used when
    /// a new Show event arrives for an existing card, including the
    /// approval -> running -> failed case where a retract animation may
    /// already be in flight.
    /// </summary>
    public void RestartShow(
        double targetLeft,
        double targetTop,
        double retractLeft,
        bool animate)
    {
        TargetLeft = targetLeft;
        TargetTop = targetTop;
        RetractLeft = retractLeft;

        BeginAnimation(LeftProperty, null);

        if (animate || _phase == CardPhase.Hidden || _phase == CardPhase.Retracting || !IsVisible)
        {
            SlideOutFromRightOfModule(targetLeft, targetTop, retractLeft);
            return;
        }

        Left = targetLeft;
        Top = targetTop;
        _phase = CardPhase.Visible;
        ShowNoActivate();
    }

    /// <summary>
    /// Update this card's anchor after module layout changes. This keeps
    /// ordinary reflow separate from Show/re-show lifecycle transitions.
    /// </summary>
    public void ReanchorTo(
        double targetLeft,
        double targetTop,
        double retractLeft)
    {
        TargetLeft = targetLeft;
        TargetTop = targetTop;
        RetractLeft = retractLeft;

        if (_phase == CardPhase.Hidden || !IsVisible)
        {
            SlideOutFromRightOfModule(targetLeft, targetTop, retractLeft);
            return;
        }

        if (_phase == CardPhase.Retracting)
        {
            return;
        }

        BeginAnimation(LeftProperty, null);
        Left = targetLeft;
        Top = targetTop;
        _phase = CardPhase.Visible;
    }

    /// <summary>
    /// Slide the card BACK to its retracted seat (just past the lamp
    /// module's right edge) and Hide() on completion. Idempotent — if
    /// the card is already hidden or already retracting, this is a no-op
    /// or no-ops smoothly.
    /// </summary>
    public void SlideBackToRightOfModule()
    {
        if (!IsVisible) return;
        if (_phase == CardPhase.Retracting) return;

        double retractLeft = RetractLeft;
        double currentLeft = Left;
        // Already at or past the retract seat -> skip the animation and
        // tear down synchronously. Avoids a no-op animation when the
        // card is already at the seat (e.g. multiple consecutive
        // ForceRetractCard calls).
        if (currentLeft >= retractLeft - 1)
        {
            FinishRetract();
            return;
        }

        _phase = CardPhase.Retracting;
        BeginAnimation(LeftProperty, null);

        // Base Left = retracted seat so that any mid-animation rebase
        // (e.g. Show during retract in MainWindow) lands correctly.
        Left = retractLeft;

        var anim = new DoubleAnimation
        {
            From = currentLeft,
            To = retractLeft,
            Duration = TimeSpan.FromMilliseconds(IndicatorUiConstants.CardSlideBackDurationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            FinishRetract();
        };
        BeginAnimation(LeftProperty, anim);
    }

    /// <summary>
    /// Tear down the card without playing the slide-back animation.
    /// Used by authoritative removal where we want the module AND
    /// card gone immediately. We still fire <see cref="RetractCompleted"/>
    /// so the MainWindow cleans up its dictionary.
    /// </summary>
    public void RetractImmediate()
    {
        if (_phase == CardPhase.Hidden) return;
        BeginAnimation(LeftProperty, null);
        Hide();
        FinishRetract();
    }

    private void FinishRetract()
    {
        BeginAnimation(LeftProperty, null);
        Hide();
        _phase = CardPhase.Hidden;
        try { RetractCompleted?.Invoke(this, EventArgs.Empty); } catch { /* swallow */ }
    }
}
