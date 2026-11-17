using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AgentBeacon.Indicator;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;
using WinForms = System.Windows.Forms;

namespace AgentBeacon.Indicator.WindowsTests;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int z, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out NativeRect rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }

    static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    static CardWindow Card(MainWindow main) => ((Dictionary<string, CardWindow>)Field(main, "_cards")).Values.Single();
    static object Timer(MainWindow main) => ((IDictionary)Field(main, "_cardTimers"))["native-card"]!;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    [STAThread]
    public static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? main = null;
            try
            {
                main = new MainWindow();
                main.GetType().GetField("_userMoved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
                void Snapshot()
                {
                    var sessions = new[] { ("native-top", "running"), ("native-card", "completed"), ("native-bottom", "running") }
                        .Select(pair => SessionViewModel.FromSnapshot(new SessionSnapshot
                        {
                            SessionId = pair.Item1, Status = pair.Item2, Agent = "Multimon test", UpdatedAt = DateTimeOffset.UtcNow,
                            Message = pair.Item1 == "native-card" ? "Multi-monitor placement validation" : null,
                        }, DateTimeOffset.UtcNow)).ToArray();
                    main.OnSnapshotReceived(sessions);
                    main.OnCardEvent(new CardEvent { Kind = CardEventKind.Show, Session = sessions[1], AutoHideAfterMs = 30000 });
                }

                Snapshot();
                await Task.Delay(400);
                var screens = WinForms.Screen.AllScreens;
                Require(screens.Length >= 2, "two monitors required for this validation");
                foreach (var screen in screens) Console.WriteLine($"MONITOR {screen.DeviceName} work={screen.WorkingArea}");

                foreach (var screen in screens.OrderByDescending(s => s.Primary))
                {
                    foreach (var nearLeft in new[] { false, true })
                    {
                        var card = Card(main);
                        var timer = Timer(main);
                        var work = screen.WorkingArea;
                        int x = nearLeft ? work.Left + 20 : work.Right - 260;
                        var before = GetForegroundWindow();
                        SetWindowPos(new WindowInteropHelper(main).Handle, IntPtr.Zero,
                            x, work.Top + 150, 0, 0, 0x0001 | 0x0004 | 0x0010);
                        await Task.Delay(500);
                        Require(WinForms.Screen.FromHandle(new WindowInteropHelper(main).Handle).DeviceName == screen.DeviceName,
                            "lamp must be on requested monitor");
                        Require(ReferenceEquals(timer, Timer(main)), "cross-monitor move must not replace stay timer");
                        Check(main, card, screen, $"move nearLeft={nearLeft}");
                        Require(before == GetForegroundWindow(), "window positioning stole foreground focus");
                    }

                    // Exercise initial measurement on each monitor, not only reanchoring.
                    var retired = Card(main);
                    main.OnCardEvent(new CardEvent { Kind = CardEventKind.Hide,
                        Session = new SessionViewModel { SessionId = "native-card" } });
                    await Task.Delay(300);
                    retired.Close();
                    Snapshot();
                    await Task.Delay(400);
                    Check(main, Card(main), screen, "new card");
                }

                // Cross monitors while an existing card is retracting.
                var finalCard = Card(main);
                main.OnCardEvent(new CardEvent { Kind = CardEventKind.Hide,
                    Session = new SessionViewModel { SessionId = "native-card" } });
                var target = screens.First(s => s.Primary).WorkingArea;
                SetWindowPos(new WindowInteropHelper(main).Handle, IntPtr.Zero,
                    target.Right - 260, target.Top + 150, 0, 0, 0x0001 | 0x0004 | 0x0010);
                await Task.Delay(500);
                Require(!finalCard.IsVisible && finalCard.Phase == CardWindow.CardPhase.Hidden,
                    "retraction must finish after crossing monitors");
                finalCard.Close();
                Console.WriteLine("PASS retract across monitors");

                Snapshot();
                await Task.Delay(400);
                var reused = Card(main);
                main.OnCardEvent(new CardEvent { Kind = CardEventKind.Hide,
                    Session = new SessionViewModel { SessionId = "native-card" } });
                await Task.Delay(40);
                var failure = new SessionViewModel
                {
                    SessionId = "native-card", Agent = "Multimon test", Status = "failed",
                    UpdatedAt = DateTimeOffset.UtcNow, Host = "host visibility test",
                    Message = string.Join("\n", Enumerable.Repeat("Long multiline failure message", 8)),
                };
                main.OnCardEvent(new CardEvent { Kind = CardEventKind.Show, Session = failure, AutoHideAfterMs = 30000 });
                await Task.Delay(400);
                Require(ReferenceEquals(reused, Card(main)), "Show during retract must reuse the tracked instance");
                Check(main, reused, WinForms.Screen.FromHandle(new WindowInteropHelper(main).Handle), "Show during retract with remeasure");

                var bottom = new SessionViewModel { SessionId = "native-bottom", Agent = "Multimon test",
                    Status = "completed", UpdatedAt = DateTimeOffset.UtcNow, Message = "Newest card" };
                main.OnSnapshotReceived(new[]
                {
                    new SessionViewModel { SessionId = "native-top", Agent = "Multimon test", Status = "running" },
                    failure, bottom,
                });
                main.OnCardEvent(new CardEvent { Kind = CardEventKind.Show, Session = bottom, AutoHideAfterMs = 30000 });
                await Task.Delay(500);
                var newest = ((Dictionary<string, CardWindow>)Field(main, "_cards"))["native-bottom"];
                Require(!reused.IsVisible && newest.IsVisible, "newest-wins collision policy must hide the older tall card");
                Check(main, newest, WinForms.Screen.FromHandle(new WindowInteropHelper(main).Handle), "multi-card collision", "native-bottom");
                reused.Close();
                Console.WriteLine("PASS all native multi-monitor checks");
                exit = 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); }
            finally
            {
                if (main is not null)
                {
                    foreach (var card in ((Dictionary<string, CardWindow>)Field(main, "_cards")).Values.ToArray())
                    { card.RetractImmediate(); card.Close(); }
                    main.Close();
                }
                app.Shutdown();
            }
        };
        app.Run();
        return exit;
    }

    static void Check(MainWindow main, CardWindow card, WinForms.Screen screen, string scenario, string moduleId = "native-card")
    {
        var work = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(main);
        var module = ((Dictionary<string, LampModuleView>)Field(main, "_modules"))[moduleId];
        var corner = module.PointToScreen(new Point());
        var center = module.PointToScreen(new Point(0, module.ActualHeight / 2));
        var width = card.ActualWidth * dpi.DpiScaleX;
        var height = card.ActualHeight * dpi.DpiScaleY;
        var expectedLeft = Math.Max(work.Left, corner.X - 12 * dpi.DpiScaleX - width);
        var expectedTop = Math.Min(Math.Max(work.Top + 64 * dpi.DpiScaleY, center.Y - height / 2), work.Bottom - height);
        GetWindowRect(new WindowInteropHelper(card).Handle, out var rect);
        Console.WriteLine($"CHECK {screen.DeviceName} {scenario} DPI={dpi.DpiScaleX} lamp={corner} card=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) expected=({expectedLeft},{expectedTop}) phase={card.Phase}");
        Require(card.IsVisible && card.Opacity == 1, "card must be visible");
        Require(Math.Abs(rect.Left - expectedLeft) <= 3, "physical left coordinate mismatch");
        Require(Math.Abs(rect.Top - expectedTop) <= 3, "physical top coordinate mismatch");
        Require(Math.Abs((rect.Right - rect.Left) - width) <= 3, "card DPI/physical width mismatch");
        Require(rect.Left >= work.Left - 1 && rect.Right <= work.Right + 1,
            "card must stay in target monitor horizontally");
        Require(rect.Top >= work.Top && rect.Bottom <= work.Bottom + 1,
            "card must stay inside target work area vertically");
        Console.WriteLine("PASS " + scenario);
    }
}
