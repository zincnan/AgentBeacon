using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using AgentBeacon.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Net.Http.Headers;

namespace AgentBeacon.Receiver;

public sealed record CliOptions(
    string Bind, int Port, string? Token, bool Debug, string? Pipe,
    bool NoAuth = false, string? ConfigFile = null)
{
    public static (CliOptions Opts, int? ExitCode) Parse(string[] args)
    {
        // ---- Layer 1: CLI flags (null = not given) ----
        string? cliBind = null, cliToken = null, cliPipe = null;
        int? cliPort = null;
        bool? cliDebug = null, cliNoAuth = null;
        bool cliPipeDisabled = false;
        string? configPath = null;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bind":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --bind requires a value");
                        return (Fail(), 4);
                    }
                    cliBind = args[++i];
                    break;
                case "--port":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --port requires a value");
                        return (Fail(), 4);
                    }
                    var portArg = args[++i];
                    if (!TryParsePort(portArg, out var parsed))
                    {
                        Console.Error.WriteLine(
                            $"agentbeacon-receiver: --port must be an integer in [1, 65535], got '{portArg}'");
                        return (Fail(), 4);
                    }
                    cliPort = parsed;
                    break;
                case "--token":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --token requires a value");
                        return (Fail(), 4);
                    }
                    cliToken = args[++i];
                    break;
                case "--no-auth":
                    cliNoAuth = true;
                    break;
                case "--debug":
                    cliDebug = true;
                    break;
                case "--pipe":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --pipe requires a value");
                        return (Fail(), 4);
                    }
                    cliPipe = args[++i];
                    if (string.IsNullOrEmpty(cliPipe))
                    {
                        cliPipe = null;
                        cliPipeDisabled = true;
                    }
                    break;
                case "--no-pipe":
                    cliPipeDisabled = true;
                    break;
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --config requires a value");
                        return (Fail(), 4);
                    }
                    configPath = args[++i];
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    Console.Error.WriteLine($"agentbeacon-receiver: unknown argument: {args[i]}");
                    return (Fail(), 4);
            }
        }

        if (help)
        {
            PrintHelp();
            return (Fail(), 0);
        }

        // ---- Layer 3: optional config file (agentbeacon.json) ----
        // Discovery: --config path → next to the exe → current directory.
        string? cfgFile = AgentBeaconConfig.Discover(configPath);
        AgentBeaconConfig? cfg = null;
        if (cfgFile is not null
            && !AgentBeaconConfig.TryLoad(cfgFile, out cfg, out var cfgError))
        {
            Console.Error.WriteLine($"agentbeacon-receiver: {cfgError}");
            return (Fail(), 4);
        }

        // ---- Layer 2: environment variables ----
        var envBind = Environment.GetEnvironmentVariable("AGENTBEACON_BIND");
        int? envPort = null;
        var portEnv = Environment.GetEnvironmentVariable("AGENTBEACON_PORT");
        if (portEnv is not null)
        {
            if (!TryParsePort(portEnv, out var parsedFromEnv))
            {
                Console.Error.WriteLine(
                    $"agentbeacon-receiver: AGENTBEACON_PORT must be an integer in [1, 65535], got '{portEnv}'");
                return (Fail(), 4);
            }
            envPort = parsedFromEnv;
        }
        var envPipe = Environment.GetEnvironmentVariable("AGENTBEACON_PIPE");
        if (string.IsNullOrEmpty(envPipe))
        {
            envPipe = null;
        }

        // ---- Merge: CLI > env > config file > default ----
        string bind = cliBind ?? envBind ?? cfg?.Bind ?? "0.0.0.0";
        int port = cliPort ?? envPort ?? cfg?.Port ?? 8765;
        bool debug = cliDebug ?? cfg?.Debug ?? false;

        string? pipe;
        if (cliPipe is not null || cliPipeDisabled)
        {
            pipe = cliPipeDisabled ? null : cliPipe;
        }
        else if (envPipe is not null)
        {
            pipe = envPipe;
        }
        else if (cfg is not null && cfg.PipeSpecified)
        {
            pipe = cfg.Pipe; // "pipe": null in the file = disable IPC
        }
        else
        {
            // Default pipe name includes the port: two Receivers on
            // different ports (or a dev instance next to an installed one)
            // must not broadcast on the same pipe.
            pipe = IpcConstants.PipeNameForPort(port);
        }

        // ---- Auth (Round 9 semantics) ----
        // Simple model: the effective token is the first non-empty value
        // of  CLI --token > env AGENTBEACON_TOKEN > file "token" > "".
        // Non-empty token → shared-bearer auth; empty token → auth is
        // OFF (with a loud startup warning). --no-auth /
        // AGENTBEACON_NO_AUTH / file "no_auth" remain as explicit
        // overrides that force auth off even when a token is present.
        // Within a single layer, giving both token and no-auth is an
        // error.
        string? token = null;
        bool noAuth = false;
        if (cliToken is not null || cliNoAuth == true)
        {
            if (cliToken is not null && cliNoAuth == true)
            {
                Console.Error.WriteLine(
                    "agentbeacon-receiver: --token and --no-auth are mutually exclusive; pick one auth mode");
                return (Fail(), 4);
            }
            token = cliToken;
            noAuth = cliNoAuth == true;
        }
        else
        {
            var envToken = Environment.GetEnvironmentVariable("AGENTBEACON_TOKEN");
            var envNoAuth = ParseNoAuthEnv(Environment.GetEnvironmentVariable("AGENTBEACON_NO_AUTH"));
            if (!string.IsNullOrEmpty(envToken) || envNoAuth)
            {
                if (!string.IsNullOrEmpty(envToken) && envNoAuth)
                {
                    Console.Error.WriteLine(
                        "agentbeacon-receiver: AGENTBEACON_TOKEN and AGENTBEACON_NO_AUTH are mutually exclusive; pick one auth mode");
                    return (Fail(), 4);
                }
                token = string.IsNullOrEmpty(envToken) ? null : envToken;
                noAuth = envNoAuth;
            }
            else
            {
                var fileToken = cfg?.Token;
                var fileNoAuth = cfg?.NoAuth == true;
                if (!string.IsNullOrEmpty(fileToken) && fileNoAuth)
                {
                    Console.Error.WriteLine(
                        $"agentbeacon-receiver: 'token' and 'no_auth' are mutually exclusive in {cfgFile}; pick one auth mode");
                    return (Fail(), 4);
                }
                // Round 9: absent OR empty token simply means auth off.
                token = string.IsNullOrEmpty(fileToken) ? null : fileToken;
                noAuth = fileNoAuth;
            }
        }

        if (cfgFile is not null)
        {
            Console.WriteLine($"agentbeacon-receiver: config file: {cfgFile}");
        }

        return (new CliOptions(bind, port, token, debug, pipe, noAuth, cfgFile), null);

        CliOptions Fail() => new("0.0.0.0", 8765, null, false, null);
    }

    private static bool ParseNoAuthEnv(string? v) => v is not null
        && (string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePort(string s, out int port)
    {
        if (!int.TryParse(s, out port)) return false;
        return port >= 1 && port <= 65535;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("agentbeacon-receiver: AgentBeacon v1 HTTP receiver (C# / ASP.NET Core / Kestrel).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --config <path>  config file (default: agentbeacon.json next to the exe or in the cwd)");
        Console.WriteLine("  --bind <addr>    bind address (default 0.0.0.0; env: AGENTBEACON_BIND)");
        Console.WriteLine("  --port <port>    TCP port 1..65535 (default 8765; env: AGENTBEACON_PORT)");
        Console.WriteLine("  --token <token>  bearer token (default $AGENTBEACON_TOKEN); enables shared-bearer auth");
        Console.WriteLine("  --no-auth        disable auth entirely (env: AGENTBEACON_NO_AUTH=1); dev / trusted LAN only");
        Console.WriteLine("  --pipe <name>    Named Pipe name for IPC (default $AGENTBEACON_PIPE or AgentBeacon.Status)");
        Console.WriteLine("  --no-pipe        disable Named Pipe IPC");
        Console.WriteLine("  --debug          enable /debug/sessions endpoint (off by default)");
        Console.WriteLine("  -h, --help       show this help");
        Console.WriteLine();
        Console.WriteLine("Values resolve as: CLI flag > environment variable > config file > default.");
        Console.WriteLine("Auth: exactly one mode must be chosen: --token (auth) or --no-auth (no auth).");
    }
}

public static class Program
{
    /// <summary>Held for the process lifetime so the guard is not GC'd.</summary>
    private static Mutex? _singleInstanceGuard;

    private const int MaxBodyBytes = 8 * 1024;
    private const int MaxSessionIdChars = 256;
    private const int MaxMessageChars = 512;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "running", "approval", "completed", "failed"
    };

    public static async Task<int> Main(string[] args)
    {
        var (opts, exit) = CliOptions.Parse(args);
        if (exit.HasValue) return exit.Value;
        string? bearer;
        if (opts.NoAuth)
        {
            bearer = null;
            Console.WriteLine(
                "agentbeacon-receiver: auth DISABLED (--no-auth) — anyone who can reach this port can post status");
        }
        else if (string.IsNullOrEmpty(opts.Token))
        {
            bearer = null;
            Console.WriteLine(
                "agentbeacon-receiver: auth DISABLED (no token configured) — anyone who can reach this port can post status");
        }
        else
        {
            bearer = opts.Token;
        }

        var store = new SessionStateStore();

        // Single-instance guard, keyed by (pipe, bind, port): starting the
        // SAME receiver twice fails here with a clear message instead of a
        // raw Kestrel bind exception. Different ports/pipes (e.g. a dev
        // instance next to the installed one, or the test harness) are
        // legitimately distinct instances and must not block each other.
        Mutex? singleInstance;
        try
        {
            var guardName =
                $@"Global\AgentBeacon.Receiver.{opts.Pipe ?? "nopipe"}:{opts.Bind}:{opts.Port}";
            singleInstance = new Mutex(initiallyOwned: true,
                guardName,
                out var createdNew);
            if (!createdNew)
            {
                Console.Error.WriteLine(
                    "agentbeacon-receiver: another receiver instance is already running; exit it first (or just use the running one)");
                singleInstance.Dispose();
                return 5;
            }
            _singleInstanceGuard = singleInstance; // root it for the process lifetime
        }
        catch (PlatformNotSupportedException)
        {
            singleInstance = null; // unnamed fallback unnecessary; keep going
        }
        SnapshotPublisher? publisher = null;
        if (!string.IsNullOrEmpty(opts.Pipe))
        {
            // Publisher subscribes per-client inside SubscribeWithInitial
            // (under the store lock); no global Start() needed.
            publisher = new SnapshotPublisher(store, opts.Pipe!);
            Console.WriteLine($"agentbeacon-receiver: Named Pipe IPC enabled on '{opts.Pipe}'");
        }
        else
        {
            Console.WriteLine("agentbeacon-receiver: Named Pipe IPC disabled (no --pipe given)");
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{opts.Bind}:{opts.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        var app = builder.Build();

        // POST /api/v1/status — the only Protocol v1 endpoint.
        app.MapPost("/api/v1/status", async (HttpContext ctx) =>
        {
            // 1. Auth (skipped entirely in --no-auth mode).
            if (bearer is not null && !CheckAuth(ctx, bearer))
            {
                return WriteError(ctx, 401, "unauthorized", "missing or invalid bearer token");
            }

            // 2. Content-Type: parse via MediaTypeHeaderValue; only application/json accepted
            //    (with optional parameters like charset). Rejects 'application/jsonfoo' etc.
            if (!TryParseJsonMediaType(ctx.Request.ContentType, out var mediaTypeError))
            {
                return WriteError(ctx, 415, "unsupported_media_type", mediaTypeError);
            }

            // 3. Body: pre-check Content-Length, then stream-read with hard 8 KiB cap.
            //    Never call ReadToEndAsync without an upper bound.
            if (ctx.Request.ContentLength is long declared && declared > MaxBodyBytes)
            {
                return WriteError(ctx, 413, "payload_too_large",
                    $"Content-Length {declared}B exceeds {MaxBodyBytes}B");
            }

            string bodyText;
            try
            {
                bodyText = await ReadBodyCappedAsync(ctx.Request.Body, MaxBodyBytes);
            }
            catch (BodyTooLargeException)
            {
                return WriteError(ctx, 413, "payload_too_large",
                    $"body exceeds {MaxBodyBytes}B during read");
            }
            catch (IOException ex)
            {
                return WriteError(ctx, 400, "body_read_error", $"failed to read body: {ex.Message}");
            }

            // 4. Parse JSON and require an object root.
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(bodyText);
            }
            catch (JsonException ex)
            {
                return WriteError(ctx, 400, "invalid_json", $"invalid JSON: {ex.Message}");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return WriteError(ctx, 400, "invalid_json",
                        $"expected JSON object at root, got {doc.RootElement.ValueKind.ToString().ToLowerInvariant()}");
                }

                var root = doc.RootElement;

                // 5. Required: session_id.
                if (!root.TryGetProperty("session_id", out var sidEl)
                    || sidEl.ValueKind != JsonValueKind.String)
                {
                    return WriteError(ctx, 400, "missing_field",
                        "session_id is required and must be a string");
                }
                var sessionId = sidEl.GetString() ?? "";
                if (sessionId.Length == 0)
                {
                    return WriteError(ctx, 400, "missing_field", "session_id must not be empty");
                }
                if (sessionId.Length > MaxSessionIdChars)
                {
                    return WriteError(ctx, 422, "session_id_too_long",
                        $"session_id exceeds {MaxSessionIdChars} chars; identity fields are not truncated");
                }

                // 6. Required: agent.
                if (!root.TryGetProperty("agent", out var agentEl)
                    || agentEl.ValueKind != JsonValueKind.String)
                {
                    return WriteError(ctx, 400, "missing_field",
                        "agent is required and must be a string");
                }
                var agent = agentEl.GetString() ?? "";
                if (agent.Length == 0)
                {
                    return WriteError(ctx, 400, "missing_field", "agent must not be empty");
                }

                // 7. Required: status.
                if (!root.TryGetProperty("status", out var statusEl)
                    || statusEl.ValueKind != JsonValueKind.String)
                {
                    return WriteError(ctx, 400, "missing_field",
                        "status is required and must be a string");
                }
                var status = statusEl.GetString() ?? "";
                if (!AllowedStatuses.Contains(status))
                {
                    return WriteError(ctx, 400, "invalid_status",
                        "status must be one of: running, approval, completed, failed");
                }

                // 8. Optional: message (defensive truncation, never rejects).
                string? message = null;
                if (root.TryGetProperty("message", out var msgEl))
                {
                    if (msgEl.ValueKind != JsonValueKind.String && msgEl.ValueKind != JsonValueKind.Null)
                    {
                        return WriteError(ctx, 400, "invalid_field_type",
                            "message must be a string when present");
                    }
                    var raw = msgEl.GetString();
                    if (raw is { Length: > MaxMessageChars })
                    {
                        // Truncate to first MaxMessageChars characters (byte boundary here
                        // because UTF-8 JSON strings cannot contain lone surrogates).
                        raw = raw.Substring(0, MaxMessageChars);
                    }
                    message = raw;
                }

                // 9. Optional: host (display only; same type rules as message).
                string? host = null;
                if (root.TryGetProperty("host", out var hostEl))
                {
                    if (hostEl.ValueKind != JsonValueKind.String && hostEl.ValueKind != JsonValueKind.Null)
                    {
                        return WriteError(ctx, 400, "invalid_field_type",
                            "host must be a string when present");
                    }
                    host = hostEl.ValueKind == JsonValueKind.Null ? null : hostEl.GetString();
                }

                // 10. Last received wins: whole-event replacement.
                var prevStatus = store.CurrentSnapshot()
                    .Where(s => s.SessionId == sessionId)
                    .Select(s => s.Status)
                    .FirstOrDefault();

                store.Upsert(new SessionSnapshot
                {
                    SessionId = sessionId,
                    Agent = agent,
                    Host = host,
                    Message = message,
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });

                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Status");
                logger.LogInformation(
                    "session={SessionId} agent={Agent} status {Prev} -> {New}",
                    sessionId, agent, prevStatus ?? "(none)", status);

                return Results.Json(new { session_id = sessionId, status });
            }
        });

        // GET /debug/sessions — dev-only, requires Bearer, not part of Protocol v1.
        if (opts.Debug)
        {
            app.MapGet("/debug/sessions", (HttpContext ctx) =>
            {
                if (bearer is not null && !CheckAuth(ctx, bearer))
                {
                    return WriteError(ctx, 401, "unauthorized", "missing or invalid bearer token");
                }
                var snapshot = store.CurrentSnapshot()
                    .Select(s => new
                    {
                        session_id = s.SessionId,
                        status = s.Status,
                        agent = s.Agent,
                        host = s.Host,
                        message = s.Message,
                        updated_at = s.UpdatedAt,
                    })
                    .ToList();
                return Results.Json(snapshot);
            });
        }

        // GET /healthz — liveness probe, unauthenticated.
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

        Console.WriteLine(
            $"agentbeacon-receiver: listening on http://{opts.Bind}:{opts.Port} (debug={opts.Debug})");

        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"agentbeacon-receiver: fatal: {ex.Message}");
            if (publisher is not null)
            {
                await publisher.DisposeAsync();
            }
            return 1;
        }
        if (publisher is not null)
        {
            await publisher.DisposeAsync();
        }
        return 0;
    }

    private static bool TryParseJsonMediaType(string? contentType, out string error)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            error = "Content-Type is required";
            return false;
        }
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mt) || mt.MediaType.HasValue == false)
        {
            error = $"Content-Type is not a valid media type: '{contentType}'";
            return false;
        }
        if (!string.Equals(mt.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Content-Type must be application/json (got '{contentType}')";
            return false;
        }
        error = "";
        return true;
    }

    private static async Task<string> ReadBodyCappedAsync(Stream body, int maxBytes)
    {
        // Read up to maxBytes + 1 bytes to detect overflow without unbounded buffering.
        var buffer = new byte[maxBytes + 1];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await body.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead > maxBytes)
        {
            throw new BodyTooLargeException();
        }
        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private sealed class BodyTooLargeException : Exception { }

    private static bool CheckAuth(HttpContext ctx, string expected)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var h)) return false;
        var v = h.ToString();
        if (!v.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var provided = v.Substring("Bearer ".Length).Trim();
        if (provided.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private static IResult WriteError(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message });
    }
}
