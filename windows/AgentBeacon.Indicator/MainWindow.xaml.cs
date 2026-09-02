using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator;

public partial class MainWindow : Window
{
    /// <summary>
    /// Ordered list of currently-known sessions, in stable display order
    /// (by UpdatedAt then SessionId). Each entry maps to one
    /// <see cref="LampModuleView"/> in <see cref="ModuleStack"/>.
    /// </summary>
    private readonly ObservableCollection<SessionViewModel> _sessions = new();

    /// <summary>
    /// LampModuleView per session_id. Maintained in parallel with
    /// <see cref="_sessions"/>; rebuilt on each snapshot.
    /// </summary>
    private readonly Dictionary<string, LampModuleView> _modules = new(StringComparer.Ordinal);

    /// <summary>
    /// Active CardWindow per session_id (or null if no card is currently
    /// being shown). At most ONE CardWindow per session.
    /// </summary>
    private readonly Dictionary<string, CardWindow> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cardShowOrdinal = new(StringComparer.Ordinal);
    private long _nextCardShowOrdinal;

    /// <summary>
    /// Per-session auto-hide timer. At most ONE timer per session. The
    /// previous timer is cancelled and replaced on each new Show.
    /// </summary>
    private readonly Dictionary<string, DispatcherTimer> _cardTimers = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-session screen-Y of the LampModule's vertical center, cached
    /// each time the layout is rebuilt so the per-card anchored layout
    /// helper does not have to walk the visual tree. Keyed by session_id.
    /// </summary>
    private readonly Dictionary<string, double> _moduleCenterYScreen = new(StringComparer.Ordinal);

    /// <summary>
    /// Right-click dismissal watermarks: session_id → the updated_at of
    /// the last event the user chose to hide. The lamp is rebuilt only
    /// when the session produces a strictly newer event
    /// (<see cref="LampDismissal.ShouldShow"/>).
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _dismissedAt = new(StringComparer.Ordinal);

    /// <summary>Last updated_at per session, refreshed on every snapshot.</summary>
    private readonly Dictionary<string, DateTimeOffset> _sessionUpdatedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// True once the user has dragged the indicator column somewhere:
    /// stops the automatic snap-back to the right edge. Resets when the
    /// process exits (position is not persisted to disk).
    /// </summary>
    private bool _userMoved;

    private readonly DispatcherTimer _tickTimer;
    private bool _layoutRefreshQueued;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => NoActivateHelper.EnsureNoActivate(this);

        SnapToRightEdge();
        SizeChanged += (_, e) =>
        {
            if (_userMoved)
            {
                // Keep the right edge where the user dragged it while
                // SizeToContent grows/shrinks the window.
                this.Left += e.PreviousSize.Width - e.NewSize.Width;
            }
            else
            {
                SnapToRightEdge();
            }
            QueueLayoutRefresh();
        };
        // Cards are anchored to module SCREEN positions; when the whole
        // window moves (drag), they must follow.
        LocationChanged += (_, _) => QueueLayoutRefresh();

        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _tickTimer.Tick += (_, _) => _host?.Tick(DateTimeOffset.UtcNow);
        _tickTimer.Start();
    }

    private IndicatorHost? _host;

    public void AttachHost(IndicatorHost host)
    {
        _host = host;
    }

    private void SnapToRightEdge()
    {
        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : IndicatorUiConstants.ModuleWidth;
        this.Left = work.Right - width - IndicatorUiConstants.LampRightMargin;
        this.Top = work.Top + IndicatorUiConstants.LampTopSafeMargin;
    }

    public void OnSnapshotReceived(IReadOnlyList<SessionViewModel> sessions)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnSnapshotReceived(sessions));
            return;
        }

        if (sessions.Count == 0)
        {
            if (IsVisible) Hide();
            TeardownAll();
            return;
        }
        else
        {
            if (!IsVisible) ShowNoActivate();
        }

        RebuildModules(sessions);
    }

    /// <summary>
    /// Rebuild the LampModule stack and re-cache the module-center-Y for
    /// each visible session. Cards currently up are repositioned after
    /// collision adjustment via <see cref="ReanchorAllCards"/>.
    /// </summary>
    private void RebuildModules(IReadOnlyList<SessionViewModel> sessions)
    {
        // Step 1: tear down modules / cards whose session_id is gone.
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sessions) present.Add(s.SessionId);
        var removedIds = new List<string>();
        foreach (var id in _modules.Keys)
        {
            if (!present.Contains(id)) removedIds.Add(id);
        }
        foreach (var id in removedIds)
        {
            if (_modules.TryGetValue(id, out var m)) ModuleStack.Children.Remove(m);
            _modules.Remove(id);
            _moduleCenterYScreen.Remove(id);
            _sessionUpdatedAt.Remove(id);
            _dismissedAt.Remove(id);
            ForceRetractCard(id, immediate: true);
        }

        // Step 2: filter right-click-dismissed lamps. A dismissed lamp
        // only comes back when its session produces a strictly NEWER
        // event than the one the user hid (LampDismissal.ShouldShow);
        // a full-snapshot re-broadcast of the same event stays hidden.
        var shown = new List<SessionViewModel>(sessions.Count);
        foreach (var s in sessions)
        {
            _sessionUpdatedAt[s.SessionId] = s.UpdatedAt;
            if (_dismissedAt.TryGetValue(s.SessionId, out var dismissedAt)
                && !LampDismissal.ShouldShow(dismissedAt, s.UpdatedAt))
            {
                continue; // still dismissed
            }
            _dismissedAt.Remove(s.SessionId); // reactivated by newer event
            shown.Add(s);
        }

        // Step 3: update or insert modules in snapshot order. The
        // bottom margin on every module except the last supplies the
        // ModuleGap vertical spacing.
        for (int i = 0; i < shown.Count; i++)
        {
            var s = shown[i];
            if (!_modules.TryGetValue(s.SessionId, out var module))
            {
                module = new LampModuleView
                {
                    AgentLabelText = s.Agent,
                    TooltipText = s.TooltipText,
                    ActiveSlot = LampStateMapper.ForStatus(s.Status).ActiveSlot,
                    BottomColorHex = LampStateMapper.ForStatus(s.Status).ActiveColorHex,
                    SessionId = s.SessionId,
                };
                module.Dismissed += OnModuleDismissed;
                module.MouseLeftButtonDown += Module_MouseLeftButtonDown;
                ModuleStack.Children.Insert(i, module);
                _modules[s.SessionId] = module;
            }
            else
            {
                module.AgentLabelText = s.Agent;
                module.TooltipText = s.TooltipText;
                var st = LampStateMapper.ForStatus(s.Status);
                module.ActiveSlot = st.ActiveSlot;
                module.BottomColorHex = st.ActiveColorHex;
                int currentIdx = ModuleStack.Children.IndexOf(module);
                if (currentIdx != i && currentIdx >= 0)
                {
                    ModuleStack.Children.RemoveAt(currentIdx);
                    ModuleStack.Children.Insert(i, module);
                }
            }

            module.Margin = new Thickness(0, 0, 0,
                i == shown.Count - 1 ? 0 : IndicatorUiConstants.ModuleGap);
        }

        // Step 3: cache module center Y (screen coords) for every live
        // module. The anchor math uses window.Left/Top (screen) + the
        // module's offset relative to the window.
        ModuleStack.UpdateLayout();
        UpdateModuleCenterYCaches();

        // Step 4: reposition / hide cards via the AnchoredCardLayout
        // helper so they no longer collide.
        ReanchorAllCards();
    }

    private void UpdateModuleCenterYCaches()
    {
        _moduleCenterYScreen.Clear();
        foreach (var kv in _modules)
        {
            var module = kv.Value;
            var relative = module.TransformToAncestor(this).Transform(new Point(0, 0));
            var moduleTopOnScreen = this.Top + relative.Y;
            _moduleCenterYScreen[kv.Key] = moduleTopOnScreen + module.ActualHeight / 2.0;
        }
    }

    /// <summary>
    /// User picked 关闭此灯 on a module's context menu: tear the module
    /// down entirely (no hidden window kept), retract its card, and
    /// record the dismissal watermark. The lamp is rebuilt from scratch
    /// only when this session produces a newer event.
    /// </summary>
    private void OnModuleDismissed(object? sender, EventArgs e)
    {
        if (sender is not LampModuleView module || module.SessionId is not { } sessionId)
        {
            return;
        }
        _dismissedAt[sessionId] = _sessionUpdatedAt.TryGetValue(sessionId, out var updatedAt)
            ? updatedAt
            : DateTimeOffset.UtcNow;
        ForceRetractCard(sessionId, immediate: true);
        ModuleStack.Children.Remove(module);
        _modules.Remove(sessionId);
        _moduleCenterYScreen.Remove(sessionId);
        QueueLayoutRefresh();
    }

    /// <summary>
    /// Drag any lamp module to move the whole indicator column. The
    /// window stops auto-snapping to the right edge afterwards and the
    /// cards follow (LocationChanged → QueueLayoutRefresh).
    /// </summary>
    private void Module_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        try
        {
            _userMoved = true;
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove without a pressed button — ignore.
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
                ShowCard(ev);
                break;
            case CardEventKind.Update:
                UpdateCard(ev);
                break;
            case CardEventKind.Hide:
                ForceRetractCard(ev.Session.SessionId, immediate: false);
                break;
        }
    }

    /// <summary>
    /// Show (or replace) the card for this session. Always ONE card per
    /// session. The card slides out from the retracted seat (just past
    /// the lamp module's right edge) to its anchor left of the module.
    ///
    /// If a card for this session is already up (Showing / Visible /
    /// Retracting), we re-anchor it with the new content and re-arm the
    /// stay timer. Showing-during-Retracting is handled by detaching the
    /// in-flight retract animation before starting the new slide-out.
    /// </summary>
    private void ShowCard(CardEvent ev)
    {
        var stayMs = ev.AutoHideAfterMs;
        if (stayMs is null)
        {
            // No card for this status (e.g. running). If a card is up,
            // retract it immediately.
            ForceRetractCard(ev.Session.SessionId, immediate: false);
            return;
        }

        if (_cards.TryGetValue(ev.Session.SessionId, out var existing))
        {
            // Same session, second Show within the stay window (or
            // during retract). Refresh content, re-anchor, re-arm.
            existing.UpdateFrom(ev.Session);
            existing.UpdateLayout();
            existing.RetractCompleted -= OnCardRetractCompleted; // avoid double-subscribe
            existing.RetractCompleted += OnCardRetractCompleted;
            _cardShowOrdinal[ev.Session.SessionId] = ++_nextCardShowOrdinal;
            ReplaceCardTimer(ev.Session.SessionId, stayMs);
            // Re-anchor with the latest layout (module order may have
            // shifted).
            ReanchorAllCards(reshowSessionId: ev.Session.SessionId);
            return;
        }

        // First-time Show for this session.
        if (!_modules.TryGetValue(ev.Session.SessionId, out _))
        {
            // No module to anchor to (race with authoritative removal):
            // nothing to do.
            return;
        }

        var card = new CardWindow();
        card.WindowStartupLocation = WindowStartupLocation.Manual;
        card.UpdateFrom(ev.Session);
        card.RetractCompleted += OnCardRetractCompleted;
        _cards[ev.Session.SessionId] = card;
        _cardShowOrdinal[ev.Session.SessionId] = ++_nextCardShowOrdinal;

        // MEASURE FIRST: place the card offscreen, Show, UpdateLayout so
        // ActualWidth / ActualHeight are real values.
        card.MeasureOffscreen();

        // Re-anchor all cards (handles new + existing collision).
        ReanchorAllCards(reshowSessionId: ev.Session.SessionId);

        // Arm the stay timer.
        ReplaceCardTimer(ev.Session.SessionId, stayMs);
    }

    private void UpdateCard(CardEvent ev)
    {
        if (_cards.TryGetValue(ev.Session.SessionId, out var existing))
        {
            existing.UpdateFrom(ev.Session);
            // Content height may have changed; re-anchor via the layout
            // helper (which respects the new measured height).
            existing.UpdateLayout();
            ReanchorAllCards();
        }
    }

    /// <summary>
    /// Force-retract any visible card for this session. Safe to call
    /// when no card is present.
    /// </summary>
    /// <param name="immediate">
    /// True to skip the slide-back animation (used for authoritative
    /// snapshot removals — module is gone, no anchor target).
    /// </param>
    private void ForceRetractCard(string sessionId, bool immediate)
    {
        CancelCardTimer(sessionId);
        if (_cards.TryGetValue(sessionId, out var card))
        {
            if (immediate || !card.IsVisible)
            {
                card.RetractImmediate();
                // RetractImmediate fires RetractCompleted -> cleanup.
            }
            else
            {
                card.SlideBackToRightOfModule();
                // Cleanup happens via RetractCompleted.
            }
        }
    }

    /// <summary>
    /// Instance-safe cleanup handler subscribed per-card. The card's
    /// RetractCompleted event arrives after slide-back finishes OR
    /// after RetractImmediate. We only remove the card from _cards if
    /// it still equals the instance we are tracking (a stale event from
    /// a replaced card must not evict a fresh replacement).
    /// </summary>
    private void OnCardRetractCompleted(object? sender, EventArgs e)
    {
        if (sender is not CardWindow card) return;
        // Find the session id we currently track this card under.
        string? trackedSessionId = null;
        foreach (var kv in _cards)
        {
            if (ReferenceEquals(kv.Value, card))
            {
                trackedSessionId = kv.Key;
                break;
            }
        }
        if (trackedSessionId is null) return;
        _cards.Remove(trackedSessionId);
        _cardShowOrdinal.Remove(trackedSessionId);
    }

    /// <summary>
    /// Tear down every LampModule and force-retract every CardWindow.
    /// Used on empty snapshot.
    /// </summary>
    private void TeardownAll()
    {
        foreach (var kv in _cards.ToArray())
        {
            CancelCardTimer(kv.Key);
            kv.Value.RetractImmediate();
        }
        _cards.Clear();
        _cardShowOrdinal.Clear();
        _cardTimers.Clear();
        _modules.Clear();
        _moduleCenterYScreen.Clear();
        _sessionUpdatedAt.Clear();
        _dismissedAt.Clear();
        ModuleStack.Children.Clear();
    }

    /// <summary>
    /// Walk every visible card, compute its anchored Y via
    /// <see cref="AnchoredCardLayout"/>, then apply the result:
    ///   - Visible cards get TargetLeft / TargetTop updated and slide to
    ///     the new anchor (if not already sliding).
    ///   - Hidden cards (overflow) are retracted immediately.
    /// </summary>
    private void ReanchorAllCards(string? reshowSessionId = null)
    {
        if (_cards.Count == 0) return;

        var work = SystemParameters.WorkArea;
        var safeTop = work.Top + IndicatorUiConstants.LampTopSafeMargin;
        var safeBottom = work.Bottom;

        var cardsSnapshot = _cards
            .OrderBy(kv => _cardShowOrdinal.TryGetValue(kv.Key, out var ordinal) ? ordinal : 0)
            .ToArray();

        var inputs = new List<AnchoredCardInput>(cardsSnapshot.Length);
        foreach (var kv in cardsSnapshot)
        {
            if (!_moduleCenterYScreen.TryGetValue(kv.Key, out var centerY)) continue;
            inputs.Add(new AnchoredCardInput
            {
                SessionId = kv.Key,
                ModuleCenterYScreen = centerY,
                Height = kv.Value.ActualHeight,
            });
        }

        var placements = AnchoredCardLayout.Compute(
            inputs, safeTop, safeBottom, gap: IndicatorUiConstants.CardVerticalGap);

        foreach (var kv in cardsSnapshot)
        {
            if (!placements.TryGetValue(kv.Key, out var placement)) continue;
            var card = kv.Value;
            if (!placement.Visible)
            {
                // No room: retract.
                ForceRetractCard(kv.Key, immediate: false);
                continue;
            }
            AnchorCardOnModule(card, placement.Top, reshowSessionId == kv.Key);
        }
    }

    /// <summary>
    /// Position a CardWindow so it sits at <paramref name="targetTop"/>
    /// (already collision-adjusted) and CardToModuleGap px to the left of
    /// its LampModule. Coordinates are SCREEN-ABSOLUTE (Window.Left/Top
    /// are screen coords for a topmost window with no parent transform).
    /// </summary>
    private void AnchorCardOnModule(CardWindow card, double targetTop, bool restartShow)
    {
        var work = SystemParameters.WorkArea;
        var sessionId = card.SessionId;
        if (!_moduleCenterYScreen.TryGetValue(sessionId, out _))
        {
            return;
        }
        LampModuleView module = _modules[sessionId];
        var relative = module.TransformToAncestor(this).Transform(new Point(0, 0));
        var moduleLeftOnScreen = this.Left + relative.X;
        var moduleWidth = module.ActualWidth;
        // Retracted seat: just past the module's right edge.
        double retractLeft = moduleLeftOnScreen + moduleWidth + 16;
        // Anchor: to the LEFT of the module, with CardToModuleGap px
        // between the card's right edge and the module's left edge.
        double targetLeft = moduleLeftOnScreen - IndicatorUiConstants.CardToModuleGap - card.ActualWidth;
        if (targetLeft < work.Left) targetLeft = work.Left;

        // Stay within the safe vertical band.
        if (targetTop < work.Top + IndicatorUiConstants.LampTopSafeMargin)
            targetTop = work.Top + IndicatorUiConstants.LampTopSafeMargin;
        if (targetTop + card.ActualHeight > work.Bottom)
            targetTop = work.Bottom - card.ActualHeight;

        if (restartShow)
            card.RestartShow(targetLeft, targetTop, retractLeft, animate: true);
        else
            card.ReanchorTo(targetLeft, targetTop, retractLeft);
    }

    private void QueueLayoutRefresh()
    {
        if (_layoutRefreshQueued) return;
        _layoutRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _layoutRefreshQueued = false;
            UpdateLayout();
            ModuleStack.UpdateLayout();
            UpdateModuleCenterYCaches();
            ReanchorAllCards();
        });
    }

    private void ShowNoActivate()
    {
        Show();
        NoActivateHelper.EnsureNoActivate(this);
    }

    private void ReplaceCardTimer(string sessionId, int? autoHideAfterMs)
    {
        CancelCardTimer(sessionId);
        if (autoHideAfterMs is not int ms) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            if (!_cardTimers.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, timer))
            {
                return;
            }
            timer.Stop();
            _cardTimers.Remove(sessionId);
            if (_cards.TryGetValue(sessionId, out var card))
            {
                card.SlideBackToRightOfModule();
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
}
