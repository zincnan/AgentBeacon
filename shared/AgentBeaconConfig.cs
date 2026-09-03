using System.Text.Json;

namespace AgentBeacon.Shared;

/// <summary>
/// Optional JSON configuration file shared by the Windows-side programs
/// (Receiver + Indicator). Value layers, most specific first:
///
///     CLI flag  >  environment variable  >  this file  >  built-in default
///
/// Schema (agentbeacon.json — see agentbeacon.example.json in the repo
/// root; the live file is git-ignored):
///
/// {
///   "bind": "0.0.0.0",
///   "port": 8765,
///   "token": "shared-secret",     // shared-bearer auth
///   "no_auth": false,             // true = auth disabled (dev / trusted LAN)
///   "debug": false,
///   "pipe": "AgentBeacon.Status"  // null = disable Named Pipe IPC
/// }
///
/// "token" and "no_auth": true are mutually exclusive within one source.
/// Unknown fields are ignored so the file can carry plugin-side settings
/// too (it does not — but staying forward-compatible is free).
/// </summary>
public sealed record AgentBeaconConfig(
    string? Bind,
    int? Port,
    string? Token,
    bool? NoAuth,
    bool? Debug,
    string? Pipe,
    bool PipeSpecified)
{
    public const string FileName = "agentbeacon.json";

    /// <summary>
    /// Find the first existing config file: any explicit path (in order),
    /// then one next to the running executable, then in the current
    /// working directory. Returns null when none exists.
    /// </summary>
    public static string? Discover(params string?[] explicitPaths)
    {
        foreach (var p in explicitPaths)
        {
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                return Path.GetFullPath(p);
            }
        }
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, FileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
        candidate = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Parse a config file. Missing fields stay null (= "not configured
    /// here"); the file itself missing → (null, null); malformed JSON or
    /// wrong field types → false with a human-readable error.
    /// </summary>
    public static bool TryLoad(string path, out AgentBeaconConfig? config,
        out string? error)
    {
        config = null;
        error = null;
        if (!File.Exists(path))
        {
            return true; // absent file is not an error; config stays null
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"config root must be a JSON object: {path}";
                return false;
            }

            string? bind = null;
            if (root.TryGetProperty("bind", out var b)
                && b.ValueKind == JsonValueKind.String)
            {
                bind = b.GetString();
            }

            int? port = null;
            if (root.TryGetProperty("port", out var p))
            {
                if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var pv))
                {
                    error = $"'port' must be an integer in {path}";
                    return false;
                }
                if (pv is < 1 or > 65535)
                {
                    error = $"'port' must be in [1, 65535] in {path} (got {pv})";
                    return false;
                }
                port = pv;
            }

            string? token = null;
            if (root.TryGetProperty("token", out var t)
                && t.ValueKind == JsonValueKind.String)
            {
                token = t.GetString();
            }

            bool? noAuth = null;
            if (root.TryGetProperty("no_auth", out var na))
            {
                if (na.ValueKind != JsonValueKind.True && na.ValueKind != JsonValueKind.False)
                {
                    error = $"'no_auth' must be true or false in {path}";
                    return false;
                }
                noAuth = na.GetBoolean();
            }

            bool? dbg = null;
            if (root.TryGetProperty("debug", out var d))
            {
                if (d.ValueKind != JsonValueKind.True && d.ValueKind != JsonValueKind.False)
                {
                    error = $"'debug' must be true or false in {path}";
                    return false;
                }
                dbg = d.GetBoolean();
            }

            string? pipe = null;
            var pipeSpecified = false;
            if (root.TryGetProperty("pipe", out var pp))
            {
                pipeSpecified = true;
                if (pp.ValueKind == JsonValueKind.String)
                {
                    pipe = pp.GetString();
                }
                // pipe: null → PipeSpecified=true with Pipe=null → disable IPC.
            }

            config = new AgentBeaconConfig(bind, port, token, noAuth, dbg,
                pipe, pipeSpecified);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON in config file {path}: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = $"cannot read config file {path}: {ex.Message}";
            return false;
        }
    }
}
