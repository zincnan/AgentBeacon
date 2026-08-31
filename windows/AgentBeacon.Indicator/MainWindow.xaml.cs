using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SessionViewModel> _lamps = new();
    private readonly Dictionary<string, CardWindow> _cards = new(StringComparer.Ordinal);

    /// <summary>
    /// Currently-active auto-hide timer per session. Keyed by session_id.
    /// Each session has AT MOST one live timer at a time; the previous
    /// timer is always cancelled (and removed) before a new one is
    /// installed. This prevents the bug where, e.g., a 5-second
    /// completed-card timer outlives the transition to a 10-second
    /// failed card and pre-emptively hides the failed card.
    ///
    /// Approval cards have no auto-hide, so no timer is registered for
    /// them — but if a session transitions away from approval we MUST
    /// still cancel any leftover timer from a prior completed/failed
    /// Show.
    /// </summary>
    private readonly Dictionary<string, DispatcherTimer> _cardTimers = new(StringComparer.Ordinal);

    /// <summary>
    /// Cards that LayoutCards() has decided to keep hidden because
    /// there is not enough vertical room in the safe area for them
    /// to sit without overlapping the caption-button row or the other
    /// cards. The dictionary <see cref="_cards"/> still owns them; a
    /// later LayoutCards() pass may decide to bring them back if
    /// enough room has freed up (e.g. after an auto-hide timer fires).
    /// </summary>
    private readonly HashSet<CardWindow> _layoutHiddenByOverflow = new();

    private readonly DispatcherTimer _tickTimer;

    public MainWindow()
    {
        InitializeComponent();
        LampList.ItemsSource = _lamps;

        // Apply no-activate style before Show() — see NoActivateHelper docs.
        SourceInitialized += (_, _) => NoActivateHelper.EnsureNoActivate(this);

        // Snap to right edge of primary monitor working area, but BELOW
        // the caption-button row of a maximized window. The lamp must
        // not overlap with the Close / Maximize / Minimize buttons of
        // a browser, VS Code, Terminal, etc. that the user may have in
        // the foreground. WorkingArea.Top + LampTopSafeMargin is the
        // "safe top" — see IndicatorUiConstants for the value.
        var work = SystemParameters.WorkArea;
        this.Left = work.Right - this.Width - IndicatorUiConstants.LampRightMargin;
        this.Top = work.Top + IndicatorUiConstants.LampTopSafeMargin;

        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _tickTimer.Tick += (_, _) => _host?.Tick(DateTimeOffset.UtcNow);
        _tickTimer.Start();

        this.SizeChanged += (_, _) =>
        {
            var w = SystemParameters.WorkArea;
            this.Left = w.Right - this.ActualWidth - IndicatorUiConstants.LampRightMargin;
            this.Top = w.Top + IndicatorUiConstants.LampTopSafeMargin;
            // Working-area size change invalidates card layout too.
            LayoutCards();
        };

        // Note: MainWindow is NOT shown in the constructor or by App.
        // It only becomes visible after a non-empty snapshot arrives,
        // via OnSnapshotReceived. See App.xaml.cs.
    }

    private IndicatorHost? _host;

    public void AttachHost(IndicatorHost host)
    {
        _host = host;
    }

    public void OnSnapshotReceived(IReadOnlyList<SessionViewModel> sessions)
    {
        // Marshal to UI thread (we may be on the pipe thread).
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnSnapshotReceived(sessions));
            return;
        }

        _lamps.Clear();
        foreach (var s in sessions)
        {
            _lamps.Add(s);
        }

        // Visibility policy: when there are zero known Agent Sessions,
        // the Indicator is fully invisible. We never render a fifth
        // state. We also never leave the window up showing an empty
        // lamp column, an empty background, or any "connecting"/"0
        // session(s)" text — such labels do not exist in the UI.
        // ShowActivated=False + WS_EX_NOACTIVATE are still applied
        // (in the constructor + SourceInitialized), so showing does
        // not steal focus.
        if (sessions.Count == 0)
        {
            if (IsVisible) Hide();
        }
        else
        {
            if (!IsVisible) ShowNoActivate();
        }
    }

    public void OnCardEvent(CardEvent ev)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnCardEvent(ev));
            return;
        }

        switch (ev.Kind)
        {
            case CardEventKind.Show:
                ShowOrUpdateCard(ev, animateIn: true);
                ReplaceCardTimer(ev.Session.SessionId, ev.AutoHideAfterMs);
                // Re-layout so all visible cards sit in the safe area
                // without overlap. The fresh card was positioned
                // off-screen by ShowOrUpdateCard so it could measure
                // ActualHeight; LayoutCards now finds it a real seat.
                LayoutCards();
                break;
            case CardEventKind.Update:
                if (_cards.TryGetValue(ev.Session.SessionId, out var existing))
                {
                    existing.UpdateFrom(ev.Session);
                    // Update can change card height; re-stack without
                    // animating. Update must NOT touch the auto-hide
                    // timer — the original Show schedule still applies.
                    // (Approval cards are persistent and have no timer;
                    // the completed/failed card lifetimes are computed
                    // once per Show.)
                    LayoutCards();
                }
                break;
            case CardEventKind.Hide:
                CancelCardTimer(ev.Session.SessionId);
                if (_cards.TryGetValue(ev.Session.SessionId, out var toHide))
                {
                    // Close the actual window. Idempotent — if the
                    // card was already hidden by layout overflow the
                    // second Hide() is a no-op.
                    toHide.Hide();
                    _layoutHiddenByOverflow.Remove(toHide);
                    _cards.Remove(ev.Session.SessionId);
                }
                // Re-layout: removing a card may free enough room for a
                // previously-overflowed card to come back.
                LayoutCards();
                break;
        }
    }

    /// <summary>
    /// Show without stealing focus from the foreground app. We use
    /// ShowActivated=False (set in XAML) plus a no-activate Win32 style on
    /// the HWND for robustness against apps like Terminal / VS Code.
    /// </summary>
    private void ShowNoActivate()
    {
        Show();
        NoActivateHelper.EnsureNoActivate(this);
    }

    /// <summary>
    /// Cancel any existing timer for <paramref name="sessionId"/> and
    /// install a new one if <paramref name="autoHideAfterMs"/> is set.
    /// Passing <c>null</c> for <paramref name="autoHideAfterMs"/> (the
    /// approval-card case) cancels any leftover timer from a prior
    /// completed/failed Show on the same session but installs no new
    /// timer — approval cards are persistent until status changes.
    ///
    /// The timer's Tick callback self-verifies that it is still the
    /// registered timer for the session before hiding the card. This
    /// catches the race where a replacement timer has been installed
    /// between the original Tick firing and its body running.
    /// </summary>
    private void ReplaceCardTimer(string sessionId, int? autoHideAfterMs)
    {
        // Always cancel any prior timer first. Even if we are about to
        // install a new one with the same duration, we want a fresh
        // DispatcherTimer instance so the elapsed-time clock restarts.
        CancelCardTimer(sessionId);

        if (autoHideAfterMs is not int ms)
        {
            // No new timer (approval: persistent).
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            // Self-check: am I still the registered timer for this session?
            // If a newer timer (e.g., from a subsequent Show) has
            // replaced me, do nothing — the newer timer owns the
            // auto-hide responsibility for this session now.
            if (!_cardTimers.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, timer))
            {
                return;
            }
            timer.Stop();
            _cardTimers.Remove(sessionId);
            if (_cards.TryGetValue(sessionId, out var cw))
            {
                cw.Hide();
                _layoutHiddenByOverflow.Remove(cw);
                _cards.Remove(sessionId);
                // The auto-hide timer cleared the slot — give any
                // previously-overflowed card a chance to come back.
                LayoutCards();
            }
        };
        _cardTimers[sessionId] = timer;
        timer.Start();
    }

    private void CancelCardTimer(string sessionId)
    {
        if (_cardTimers.TryGetValue(sessionId, out var existing))
        {
            existing.Stop();
            _cardTimers.Remove(sessionId);
        }
    }

    private void ShowOrUpdateCard(CardEvent ev, bool animateIn)
    {
        var isFresh = !_cards.TryGetValue(ev.Session.SessionId, out var card);
        if (isFresh)
        {
            card = new CardWindow();
            _cards[ev.Session.SessionId] = card;
            // Pre-size offscreen so ActualWidth /ActualHeight are
            // sensible for LayoutCards / SlideInFromRight to use.
            card.WindowStartupLocation = WindowStartupLocation.Manual;
            card.UpdateFrom(ev.Session);
            card.Left = -10000;
            card.Top = -10000;
            card.ShowNoActivate();
        }
        else
        {
            card.UpdateFrom(ev.Session);
        }

        if (animateIn && isFresh)
        {
            // Animate the fresh card in from off-screen right to the
            // final seat picked by LayoutCards. We compute that seat
            // here WITHOUT writing it to the card so the animation's
            // base value still lives in LayoutCards.
            var (left, top) = ComputeFreshCardSlideTarget(card);
            card.SlideInFromRight(left, top);
        }
    }

    /// <summary>
    /// Compute the seat that LayoutCards would give to a fresh card,
    /// used purely as the slide-in target. Not applied to the card
    /// directly — LayoutCards owns all card positions.
    ///
    /// Uses <see cref="CardStackLayout.Compute"/> as the SINGLE source
    /// of truth for card positions so the slide-in target is always
    /// consistent with whatever LayoutCards will eventually apply. The
    /// fresh card is the newest in insertion order, so it lives at
    /// the BOTTOM of the stack and its placement is the last element
    /// of the placements array.
    /// </summary>
    private (double Left, double Top) ComputeFreshCardSlideTarget(CardWindow card)
    {
        var work = SystemParameters.WorkArea;
        var safeTop = work.Top + IndicatorUiConstants.LampTopSafeMargin;
        var safeBottom = work.Bottom - IndicatorUiConstants.CardRightGutter;

        // Build heights in insertion order (oldest first). The fresh
        // card is the LAST element of _cards.Values.
        var heights = new double[_cards.Count];
        int idx = 0;
        foreach (var c in _cards.Values)
        {
            heights[idx++] = c.ActualHeight;
        }

        var placements = CardStackLayout.Compute(
            heights, safeTop, safeBottom, IndicatorUiConstants.CardStackGap);

        // The fresh card is the newest: last in the input -> last
        // placement.
        var fresh = placements[placements.Length - 1];

        // If the fresh card itself doesn't fit (e.g. one giant card
        // taller than the entire safe band), fall back to safeTop.
        // LayoutCards will mark it hidden via _layoutHiddenByOverflow
        // and stop trying to position it.
        var targetTop = fresh.Fits ? fresh.Top : safeTop;
        var targetLeft = work.Right - card.ActualWidth - IndicatorUiConstants.CardRightGutter;
        return (targetLeft, targetTop);
    }

    /// <summary>
    /// Recompute positions for every visible card so that:
    ///   1. No two cards overlap.
    ///   2. Every visible card's top is >= WorkingArea.Top + LampTopSafeMargin
    ///      (so they cannot crawl into the caption-button row of a
    ///      maximized foreground window).
    ///   3. Every visible card's bottom is <= WorkingArea.Bottom.
    ///
    /// When the stack overflows the safe area the OLDEST cards (in
    /// insertion order, i.e. the ones at the top of the visual stack)
    /// are kept hidden via <see cref="_layoutHiddenByOverflow"/>; the
    /// NEWEST ones that DO fit stay visible at their natural bottom-up
    /// positions. A later pass — after some auto-hide timer fires or
    /// after a status change hides a card — may bring the overflowed
    /// cards back into view if room has freed up.
    ///
    /// This method does not animate. The fresh card's slide-in is
    /// driven separately by ShowOrUpdateCard + SlideInFromRight; every
    /// other card just snaps to its new position, which is fine for
    /// completed/failed cards that have already been visible.
    /// </summary>
    private void LayoutCards()
    {
        var work = SystemParameters.WorkArea;
        var safeTop = work.Top + IndicatorUiConstants.LampTopSafeMargin;
        var safeBottom = work.Bottom - IndicatorUiConstants.CardRightGutter;

        // Snapshot the cards so we can iterate twice without worrying
        // about mutation.
        var cards = new List<CardWindow>(_cards.Values);
        int n = cards.Count;
        if (n == 0) return;

        // Build a heights array and call the pure helper from Indicator.Core.
        var heights = new double[n];
        for (int i = 0; i < n; i++) heights[i] = cards[i].ActualHeight;
        var placements = CardStackLayout.Compute(
            heights, safeTop, safeBottom, IndicatorUiConstants.CardStackGap);

        // Apply. Cards that fit get positioned and shown; cards that
        // overflow get hidden. Net effect: the newest cards that fit
        // get their natural bottom-up positions; the oldest cards that
        // would crawl past safeTop get hidden and stay hidden until a
        // later pass makes room.
        for (int i = 0; i < n; i++)
        {
            var c = cards[i];
            if (placements[i].Fits)
            {
                c.Left = work.Right - c.ActualWidth - IndicatorUiConstants.CardRightGutter;
                c.Top = placements[i].Top;
                if (!c.IsVisible)
                {
                    c.ShowNoActivate();
                }
                _layoutHiddenByOverflow.Remove(c);
            }
            else
            {
                // Overflow: hide. Only call Hide() on the transition
                // from visible → hidden, to avoid repeated focus-style
                // no-ops.
                if (_layoutHiddenByOverflow.Add(c))
                {
                    c.Hide();
                }
            }
        }
    }
}