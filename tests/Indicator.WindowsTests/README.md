# Windows Multi-Monitor Regression

Run on an interactive Windows desktop with at least two monitors. Keep
the current foreground application unchanged while the focus checks run.
Different monitor scaling (for example 175% and 100%) exercises DPI transitions.

```powershell
dotnet run --project tests/Indicator.WindowsTests/AgentBeacon.Indicator.WindowsTests.csproj -c Release
```

The test opens its own temporary lamp/card windows and closes them on exit.
It does not connect to Receiver or replace the installed Indicator.
It checks physical HWND bounds, screen-edge clamping, fresh-card measurement,
cross-monitor moves without stay-timer replacement or focus theft, retraction,
Show during retract with multiline content, and newest-wins card collisions.

Use a Windows-local source directory, as for other native Windows builds.
