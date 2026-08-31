using System.Windows;
using AgentBeacon.Indicator.Core;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator;

public partial class App : Application
{
    private IndicatorHost? _host;
    private MainWindow? _mainWindow;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var pipeName = ParsePipeArg(e.Args);
        if (string.IsNullOrEmpty(pipeName))
        {
            pipeName = IpcConstants.DefaultPipeName;
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
    }
}
