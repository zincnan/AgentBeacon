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

    private readonly DispatcherTimer _tickTimer;

    public MainWindow()
    {
        InitializeComponent();
        LampList.ItemsSource = _lamps;

        // Apply no-activate style before Show() — see NoActivateHelper docs.
        SourceInitialized += (_, _) => NoActivateHelper.EnsureNoActivate(this);

        // Snap to right edge of primary monitor working area (above the taskbar).
        var work = SystemParameters.WorkArea;
        this.Left = work.Right - this.Width - IndicatorUiConstants.LampRightGutter;
        this.Top = work.Top + IndicatorUiConstants.LampTopGutter;

        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _tickTimer.Tick += (_, _) => _host?.Tick(DateTimeOffset.UtcNow);
        _tickTimer.Start();

        this.SizeChanged += (_, _) =>
        {
            var w = SystemParameters.WorkArea;
            this.Left = w.Right - this.ActualWidth - IndicatorUiConstants.LampRightGutter;
        };
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
        StatusText.Text = $"{sessions.Count} session(s)";
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
                break;
            case CardEventKind.Update:
                if (_cards.TryGetValue(ev.Session.SessionId, out var existing))
                {
                    existing.UpdateFrom(ev.Session);
                    // Update can change card height; re-stack without animating.
                    // Update must NOT touch the auto-hide timer — the
                    // original Show schedule still applies. (Approval
                    // cards are persistent and have no timer; the
                    // completed/failed card lifetimes are computed once
                    // per Show.)
                    PositionCard(existing, animate: false);
                }
                break;
            case CardEventKind.Hide:
                CancelCardTimer(ev.Session.SessionId);
                if (_cards.TryGetValue(ev.Session.SessionId, out var toHide))
                {
                    toHide.Hide();
                    _cards.Remove(ev.Session.SessionId);
                }
                break;
        }
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
                _cards.Remove(sessionId);
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
            // Pre-size offscreen so ActualWidth is sensible for the slide-in.
            card.WindowStartupLocation = WindowStartupLocation.Manual;
            card.UpdateFrom(ev.Session);
            // Place out of view first to measure.
            card.Left = -10000;
            card.Top = -10000;
            card.ShowNoActivate();
        }
        else
        {
            card!.UpdateFrom(ev.Session);
        }

        if (animateIn && isFresh)
        {
            var (left, top) = ComputeTargetPosition(card!);
            card!.SlideInFromRight(left, top);
        }
        else
        {
            PositionCard(card!, animate: false);
        }
    }

    private (double Left, double Top) ComputeTargetPosition(CardWindow card)
    {
        var work = SystemParameters.WorkArea;
        double top = work.Bottom - IndicatorUiConstants.CardRightGutter;
        foreach (var c in _cards.Values)
        {
            if (c == card) continue;
            if (!c.IsVisible) continue;
            top -= c.ActualHeight + IndicatorUiConstants.CardStackGap;
        }
        top -= card.ActualHeight;
        var left = work.Right - card.ActualWidth - IndicatorUiConstants.CardRightGutter;
        return (left, top);
    }

    private void PositionCard(CardWindow card, bool animate)
    {
        var (left, top) = ComputeTargetPosition(card);
        if (animate)
        {
            card.SlideInFromRight(left, top);
        }
        else
        {
            card.Left = left;
            card.Top = top;
        }
    }

    public void SetStatusText(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatusText(text));
            return;
        }
        StatusText.Text = text;
    }
}
