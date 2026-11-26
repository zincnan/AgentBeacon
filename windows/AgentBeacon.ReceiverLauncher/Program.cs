using System.Diagnostics;

namespace AgentBeacon.ReceiverLauncher;

internal static class Program
{
    private static int Main(string[] args)
    {
        var explicitRoot = ParseRootArg(args);
        var (receiverExe, rootDir) = ResolveTargets(explicitRoot);
        if (receiverExe is null || rootDir is null)
        {
            return 2;
        }

        var configPath = Path.Combine(rootDir, "agentbeacon.json");
        var pidFile = Path.Combine(rootDir, "receiver.pid");

        var psi = new ProcessStartInfo
        {
            FileName = receiverExe,
            WorkingDirectory = rootDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return 1;
        }

        // Wait briefly so duplicate launches (Mutex exit code 5) or immediate
        // startup failures do not overwrite an active receiver.pid.
        if (proc.WaitForExit(300))
        {
            return proc.ExitCode;
        }

        try
        {
            File.WriteAllText(pidFile, proc.Id.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 0;
    }

    private static string? ParseRootArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--root", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    internal static (string? ReceiverExe, string? RootDir) ResolveTargets(string? explicitRoot = null)
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrEmpty(explicitRoot))
        {
            var fullRoot = Path.GetFullPath(explicitRoot);
            var fromRoot = FindReceiverUnderRoot(fullRoot, exeDir);
            if (fromRoot is not null)
            {
                return (fromRoot, fullRoot);
            }
        }

        // Dist layout: <root>\receiver\agentbeacon-receiver-launcher.exe
        var sibling = Path.Combine(exeDir, "agentbeacon-receiver.exe");
        if (File.Exists(sibling))
        {
            var root = Directory.GetParent(exeDir)?.FullName ?? exeDir;
            return (sibling, root);
        }

        // Source-install layout: <root>\windows\AgentBeacon.ReceiverLauncher\bin\<Config>\net10.0\...
        var cursor = new DirectoryInfo(exeDir);
        for (var depth = 0; depth < 6 && cursor is not null; depth++)
        {
            var found = FindReceiverUnderRoot(cursor.FullName, exeDir);
            if (found is not null)
            {
                return (found, cursor.FullName);
            }
            cursor = cursor.Parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        var fromCwd = FindReceiverUnderRoot(cwd, exeDir);
        if (fromCwd is not null)
        {
            return (fromCwd, cwd);
        }

        return (null, null);
    }

    private static string? FindReceiverUnderRoot(string rootDir, string exeDir)
    {
        var preferDebug = exeDir.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

        var candidates = preferDebug
            ? new[]
            {
                Path.Combine(rootDir, "receiver", "agentbeacon-receiver.exe"),
                Path.Combine(rootDir, "receiver", "bin", "Debug", "net10.0", "agentbeacon-receiver.exe"),
                Path.Combine(rootDir, "receiver", "bin", "Release", "net10.0", "agentbeacon-receiver.exe"),
            }
            : new[]
            {
                Path.Combine(rootDir, "receiver", "agentbeacon-receiver.exe"),
                Path.Combine(rootDir, "receiver", "bin", "Release", "net10.0", "agentbeacon-receiver.exe"),
                Path.Combine(rootDir, "receiver", "bin", "Debug", "net10.0", "agentbeacon-receiver.exe"),
            };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
