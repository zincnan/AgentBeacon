using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentBeacon.Shared;

namespace AgentBeacon.Receiver.IpcTests;

/// <summary>
/// Spawns a real `agentbeacon-receiver` process for each scenario and
/// provides HTTP + pipe clients bound to it. Each harness instance owns
/// one receiver child process, one HTTP base URL, one pipe name.
/// </summary>
internal sealed class IpcTestHarness : IAsyncDisposable
{
    public string PipeName { get; }
    public string BaseUrl { get; }
    public string Token { get; }
    public int Port { get; }
    public bool DebugEnabled { get; init; }

    private readonly Process _receiver;
    private readonly HttpClient _http;
    private readonly string _stdoutLogPath;

    private IpcTestHarness(string pipeName, int port, string token, bool debug,
        Process receiver, string stdoutLogPath)
    {
        PipeName = pipeName;
        Port = port;
        Token = token;
        BaseUrl = $"http://127.0.0.1:{port}";
        DebugEnabled = debug;
        _receiver = receiver;
        _stdoutLogPath = stdoutLogPath;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    public static async Task<IpcTestHarness> StartAsync(
        string token, bool debug = true, bool withPipe = true,
        string? pipeName = null, int port = 0)
    {
        if (port == 0)
        {
            port = FindFreePort();
        }
        pipeName ??= $"AgentBeacon.Test.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var dll = FindReceiverDll();
        if (dll is null)
        {
            throw new FileNotFoundException(
                "Could not locate agentbeacon-receiver.dll. Build receiver first " +
                "(dotnet build receiver -c Release).");
        }

        var stdoutLog = Path.Combine(Path.GetTempPath(),
            $"ab-receiver-{Guid.NewGuid():N}.log");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--bind");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--token");
        psi.ArgumentList.Add(token);
        if (debug) psi.ArgumentList.Add("--debug");
        if (withPipe)
        {
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(pipeName);
        }
        else
        {
            psi.ArgumentList.Add("--no-pipe");
        }

        psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var proc = Process.Start(psi)!;
        // Tee stdout/stderr to a file so a failed assertion can dump it.
        var sw = new StreamWriter(stdoutLog) { AutoFlush = true };
        _ = Task.Run(async () =>
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line is null) break;
                try { await sw.WriteLineAsync($"OUT {line}"); } catch { }
            }
        });
        _ = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line is null) break;
                try { await sw.WriteLineAsync($"ERR {line}"); } catch { }
            }
        });

        // Wait for /healthz to confirm boot.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        using var probe = new HttpClient();
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var r = await probe.GetAsync($"http://127.0.0.1:{port}/healthz");
                if (r.IsSuccessStatusCode) break;
            }
            catch { }
            await Task.Delay(100);
        }
        if (DateTimeOffset.UtcNow >= deadline)
        {
            try { proc.Kill(true); } catch { }
            sw.Dispose();
            throw new TimeoutException(
                $"receiver did not become healthy within 15s. Log:\n{File.ReadAllText(stdoutLog)}");
        }

        // Give the pipe listener a moment after boot (it starts on ctor,
        // before the HTTP listener, but timing matters on Linux).
        await Task.Delay(200);

        return new IpcTestHarness(pipeName, port, token, debug, proc, stdoutLog);
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string? FindReceiverDll()
    {
        // Walk up from the current directory and from AppContext.BaseDirectory
        // looking for the receiver's built dll.
        var dirs = new List<string?>
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        };
        foreach (var start in dirs)
        {
            if (start is null) continue;
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "receiver", "bin", "Release", "net10.0", "agentbeacon-receiver.dll");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "receiver", "bin", "Debug", "net10.0", "agentbeacon-receiver.dll");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Public wrapper for direct CLI tests that don't use the harness.</summary>
    public static string? FindReceiverDllForCli() => FindReceiverDll();

    public async Task<HttpResponseMessage> PostStatusAsync(object body)
    {
        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/status")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req);
    }

    public async Task<JsonElement[]> DebugSessionsAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/debug/sessions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement[]>(text)!;
    }

    public NamedPipeClientStream ConnectPipe(TimeSpan? timeout = null)
    {
        var c = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);
        c.Connect((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds);
        return c;
    }

    public async Task<SnapshotEnvelope> ReadNextEnvelopeAsync(NamedPipeClientStream stream, TimeSpan? timeout = null)
    {
        var lenBuf = new byte[4];
        // ReadAsync(timeout) is not supported on PipeStream in net10.0 for
        // byte arrays in a directly-awaitable way. We use ReadAsync(memory)
        // with a CancellationTokenSource for timeout enforcement. We loop
        // on short reads so a 1-byte-at-a-time Stream cannot fool us into
        // accepting a truncated length prefix.
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        bool gotHeader;
        try
        {
            gotHeader = await AgentBeacon.Indicator.Core.PipeClient
                .ReadExactlyOrEofAsync(stream, lenBuf, 0, 4, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("timed out reading length prefix");
        }
        // The test harness never wants a clean EOF here — we asked for a
        // frame. A 0-byte return is a truncation.
        if (!gotHeader) throw new IOException("truncated length prefix (EOF before any byte)");

        int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        if (len <= 0 || len > 1024 * 1024) throw new IOException($"bad length {len}");
        var payload = new byte[len];
        try
        {
            // Same rule as PipeClient.ReadLoopAsync: the header declared
            // `len` bytes, EOF before any payload byte is truncated, not
            // clean.
            bool gotPayload = await AgentBeacon.Indicator.Core.PipeClient
                .ReadExactlyOrEofAsync(stream, payload, 0, len, cts.Token);
            if (!gotPayload)
            {
                throw new IOException(
                    $"truncated payload: header declared {len} bytes but EOF before any payload byte");
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("timed out reading payload");
        }
        return JsonSerializer.Deserialize<SnapshotEnvelope>(payload)!;
    }

    public string DumpReceiverLog()
    {
        try { return File.Exists(_stdoutLogPath) ? File.ReadAllText(_stdoutLogPath) : "(no log)"; }
        catch { return "(failed to read log)"; }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (!_receiver.HasExited)
            {
                _receiver.Kill(true);
                await _receiver.WaitForExitAsync();
            }
        }
        catch { }
        _receiver.Dispose();
        try { File.Delete(_stdoutLogPath); } catch { }
    }
}
