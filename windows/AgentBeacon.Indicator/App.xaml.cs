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

        _mainWindow = new MainWindow();
        _host = new IndicatorHost(pipeName,
            onSnapshotReceived: _mainWindow.OnSnapshotReceived,
            onCardEvent: _mainWindow.OnCardEvent,
            onPipeError: msg => _mainWindow.SetStatusText(msg));
        _mainWindow.AttachHost(_host);
        _mainWindow.Show();
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
