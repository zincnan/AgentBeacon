using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Net.Http.Headers;

namespace AgentBeacon.Receiver;

public sealed class SessionState
{
    public required string Status { get; init; }
    public required string Agent { get; init; }
    public string? Host { get; init; }
    public string? Message { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CliOptions(string Bind, int Port, string? Token, bool Debug)
{
    public static (CliOptions Opts, int? ExitCode) Parse(string[] args)
    {
        string bind = Environment.GetEnvironmentVariable("AGENTBEACON_BIND") ?? "0.0.0.0";
        int port = 8765;
        var portEnv = Environment.GetEnvironmentVariable("AGENTBEACON_PORT");
        if (portEnv is not null)
        {
            if (!TryParsePort(portEnv, out var parsedFromEnv))
            {
                Console.Error.WriteLine(
                    $"agentbeacon-receiver: AGENTBEACON_PORT must be an integer in [1, 65535], got '{portEnv}'");
                return (new CliOptions(bind, port, Token: null, Debug: false), 4);
            }
            port = parsedFromEnv;
        }
        string? token = Environment.GetEnvironmentVariable("AGENTBEACON_TOKEN");
        bool debug = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bind":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --bind requires a value");
                        return (new CliOptions(bind, port, token, debug), 4);
                    }
                    bind = args[++i];
                    break;
                case "--port":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --port requires a value");
                        return (new CliOptions(bind, port, token, debug), 4);
                    }
                    var portArg = args[++i];
                    if (!TryParsePort(portArg, out var parsed))
                    {
                        Console.Error.WriteLine(
                            $"agentbeacon-receiver: --port must be an integer in [1, 65535], got '{portArg}'");
                        return (new CliOptions(bind, port, token, debug), 4);
                    }
                    port = parsed;
                    break;
                case "--token":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("agentbeacon-receiver: --token requires a value");
                        return (new CliOptions(bind, port, token, debug), 4);
                    }
                    token = args[++i];
                    break;
                case "--debug":
                    debug = true;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    Console.Error.WriteLine($"agentbeacon-receiver: unknown argument: {args[i]}");
                    return (new CliOptions(bind, port, token, debug), 4);
            }
        }

        if (help)
        {
            PrintHelp();
            return (new CliOptions(bind, port, token, debug), 0);
        }

        return (new CliOptions(bind, port, token, debug), null);
    }

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
        Console.WriteLine("  --bind <addr>    bind address (default 0.0.0.0; env: AGENTBEACON_BIND)");
        Console.WriteLine("  --port <port>    TCP port 1..65535 (default 8765; env: AGENTBEACON_PORT)");
        Console.WriteLine("  --token <token>  bearer token (default $AGENTBEACON_TOKEN)");
        Console.WriteLine("  --debug          enable /debug/sessions endpoint (off by default)");
        Console.WriteLine("  -h, --help       show this help");
    }
}

public static class Program
{
    private const int MaxBodyBytes = 8 * 1024;
    private const int MaxSessionIdChars = 256;
    private const int MaxMessageChars = 512;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "running", "approval", "completed", "failed"
    };

    public static int Main(string[] args)
    {
        var (opts, exit) = CliOptions.Parse(args);
        if (exit.HasValue) return exit.Value;
        if (string.IsNullOrEmpty(opts.Token))
        {
            Console.Error.WriteLine(
                "agentbeacon-receiver: token is required (use --token or set AGENTBEACON_TOKEN)");
            return 4;
        }

        var state = new ConcurrentDictionary<string, SessionState>(StringComparer.Ordinal);
        var bearer = opts.Token!;

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
            // 1. Auth.
            if (!CheckAuth(ctx, bearer))
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
                var prev = state.TryGetValue(sessionId, out var oldState) ? oldState.Status : null;
                state[sessionId] = new SessionState
                {
                    Status = status,
                    Agent = agent,
                    Host = host,
                    Message = message,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Status");
                logger.LogInformation(
                    "session={SessionId} agent={Agent} status {Prev} -> {New}",
                    sessionId, agent, prev ?? "(none)", status);

                return Results.Json(new { session_id = sessionId, status });
            }
        });

        // GET /debug/sessions — dev-only, requires Bearer, not part of Protocol v1.
        if (opts.Debug)
        {
            app.MapGet("/debug/sessions", (HttpContext ctx) =>
            {
                if (!CheckAuth(ctx, bearer))
                {
                    return WriteError(ctx, 401, "unauthorized", "missing or invalid bearer token");
                }
                var snapshot = state
                    .Select(kv => new
                    {
                        session_id = kv.Key,
                        status = kv.Value.Status,
                        agent = kv.Value.Agent,
                        host = kv.Value.Host,
                        message = kv.Value.Message,
                        updated_at = kv.Value.UpdatedAt,
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
            app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"agentbeacon-receiver: fatal: {ex.Message}");
            return 1;
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