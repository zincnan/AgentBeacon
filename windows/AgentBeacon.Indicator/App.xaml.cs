using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;
using WinForms = System.Windows.Forms;

namespace AgentBeacon.Indicator;

public partial class App : Application
{
    private IndicatorHost? _host;
    private MainWindow? _mainWindow;

    /// <summary>
    /// Single-instance guard. Named so it also spans different terminals /
    /// scheduled tasks. Held for the process lifetime; a second launch
    /// sees AlreadyHandled and exits silently — the first instance is
    /// already showing the lamps, so there is nothing visible to do.
    /// </summary>
    private static Mutex? _singleInstanceMutex;

    // Tray icon (Round 9). The Indicator has no taskbar entry and no
    // window when idle, so the tray is the only always-visible handle:
    // tooltip identifies it, right-click 退出 is the supported way to
    // close the app. Fields are rooted to keep native handles alive.
    private WinForms::NotifyIcon? _trayIcon;
    private System.Drawing.Bitmap? _trayBitmap;
    private System.Drawing.Icon? _trayIconHandle;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            @"Global\AgentBeacon.Indicator.SingleInstance",
            out var createdNew);
        if (!createdNew)
        {
            // Another Indicator is already running. Exit quietly (the
            // first instance owns the desktop surface).
            Shutdown();
            return;
        }

        // Pipe name resolution: --pipe CLI flag > agentbeacon.json (next
        // to the exe or in the working directory) > default. The config
        // file is the same one the Receiver reads.
        var pipeName = ParsePipeArg(e.Args);
        if (string.IsNullOrEmpty(pipeName))
        {
            var cfgFile = AgentBeaconConfig.Discover();
            if (cfgFile is not null
                && AgentBeaconConfig.TryLoad(cfgFile, out var cfg, out _)
                && cfg is { PipeSpecified: true })
            {
                pipeName = cfg.Pipe; // "pipe": null = IPC disabled
            }
        }
        if (string.IsNullOrEmpty(pipeName))
        {
            // Same derivation as the Receiver: prefix + port from the
            // shared agentbeacon.json (default 8765).
            var cfgFileForPort = AgentBeaconConfig.Discover();
            int port = 8765;
            if (cfgFileForPort is not null
                && AgentBeaconConfig.TryLoad(cfgFileForPort, out var cfgPort, out _)
                && cfgPort?.Port is int p)
            {
                port = p;
            }
            pipeName = IpcConstants.PipeNameForPort(port);
        }

        // Create the window but DO NOT Show it. The window is made
        // visible only when a non-empty snapshot arrives — product
        // requirement: when no Agent Session is known, the Indicator
        // is fully invisible. We never paint a fifth state (no gray
        // lamp, no "connecting" / "0 session(s)" / "pipe error" text,
        // no empty background).
        _mainWindow = new MainWindow();
        _host = new IndicatorHost(pipeName,
            onSnapshotReceived: _mainWindow.OnSnapshotReceived,
            onCardEvent: _mainWindow.OnCardEvent,
            // Pipe errors must not produce a fifth visual state. They
            // are logged to Debug only; the UI stays at whatever it was
            // (visible with current lamps if any, otherwise hidden).
            onPipeError: msg => System.Diagnostics.Debug.WriteLine(
                $"[AgentBeacon] pipe error: {msg}"));
        _mainWindow.AttachHost(_host);

        InitTrayIcon();
    }

    /// <summary>
    /// Tray icon: a small blue dot with "AgentBeacon" context menu.
    /// 左键单击无动作（灯就是全部 UI）；右键菜单区分只退出
    /// Indicator 和停止本安装目录下的 Receiver 后退出。
    /// </summary>
    private void InitTrayIcon()
    {
        // 16x16 blue dot — same running-blue as the lamp module.
        _trayBitmap = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(_trayBitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(
                System.Drawing.ColorTranslator.FromHtml("#2F81F7"));
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        _trayIconHandle = System.Drawing.Icon.FromHandle(_trayBitmap.GetHicon());

        var menu = new WinForms::ContextMenuStrip();
        menu.Items.Add("退出指示器", null, (_, _) => ExitFromTray(stopReceiver: false));
        menu.Items.Add("停止 Receiver 并退出", null, (_, _) => ExitFromTray(stopReceiver: true));

        _trayIcon = new WinForms::NotifyIcon
        {
            Icon = _trayIconHandle,
            Text = "AgentBeacon 状态指示器",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>
    /// Supported shutdown path from the tray menu. Tears down the pipe
    /// host, hides/closes windows, removes the tray icon, then exits.
    /// </summary>
    private void ExitFromTray(bool stopReceiver)
    {
        if (stopReceiver)
        {
            StopOwnedReceiver();
        }
        _host?.Dispose();
        _host = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _mainWindow?.Close();
        Shutdown();
    }

    private static void StopOwnedReceiver()
    {
        var expectedPaths = ExpectedReceiverPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stopped = TryStopReceiverFromPidFile(expectedPaths);
        if (stopped) return;

        foreach (var proc in Process.GetProcessesByName("agentbeacon-receiver"))
        {
            using (proc)
            {
                if (!IsExpectedReceiver(proc, expectedPaths)) continue;
                TryStopProcess(proc);
                return;
            }
        }
    }

    private static bool TryStopReceiverFromPidFile(IReadOnlySet<string> expectedPaths)
    {
        foreach (var pidFile in ReceiverPidFiles())
        {
            try
            {
                if (!File.Exists(pidFile)) continue;
                var text = File.ReadAllText(pidFile).Trim();
                if (!int.TryParse(text, out var pid))
                {
                    TryDelete(pidFile);
                    continue;
                }

                using var proc = Process.GetProcessById(pid);
                if (!IsExpectedReceiver(proc, expectedPaths)) continue;
                TryStopProcess(proc);
                TryDelete(pidFile);
                return true;
            }
            catch (ArgumentException)
            {
                TryDelete(pidFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentBeacon] stop receiver failed: {ex}");
            }
        }
        return false;
    }

    private static bool IsExpectedReceiver(Process proc, IReadOnlySet<string> expectedPaths)
    {
        try
        {
            if (!string.Equals(proc.ProcessName, "agentbeacon-receiver", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var path = proc.MainModule?.FileName;
            return path is not null && expectedPaths.Contains(NormalizePath(path));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentBeacon] receiver process validation failed: {ex.Message}");
            return false;
        }
    }

    private static void TryStopProcess(Process proc)
    {
        try
        {
            if (proc.HasExited) return;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentBeacon] receiver stop failed: {ex}");
        }
    }

    private static IEnumerable<string> ExpectedReceiverPaths()
    {
        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
        };

        var relativePaths = new[]
        {
            @"receiver\agentbeacon-receiver.exe",
            @"receiver\bin\Release\net10.0\agentbeacon-receiver.exe",
            @"receiver\bin\Debug\net10.0\agentbeacon-receiver.exe",
        };

        foreach (var root in roots)
        {
            foreach (var rel in relativePaths)
            {
                yield return NormalizePath(Path.Combine(root, rel));
            }
        }
    }

    private static IEnumerable<string> ReceiverPidFiles()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "receiver.pid");
        yield return Path.Combine(AppContext.BaseDirectory, "receiver.pid");
        yield return Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "receiver.pid");
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string? ParsePipeArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _host?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconHandle?.Dispose();
        _trayBitmap?.Dispose();
    }
}
