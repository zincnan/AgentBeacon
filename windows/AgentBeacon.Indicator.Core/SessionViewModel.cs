using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Canonical AgentBeacon status values shared between Agent, Receiver, and
/// Indicator. The Receiver validates incoming status against
/// Protocol v1; this enum mirrors that set for the Indicator side.
/// Unknown values are an error — they must never produce a fifth UI state.
/// </summary>
public static class IndicatorStatus
{
    public const string Running = "running";
    public const string Approval = "approval";
    public const string Completed = "completed";
    public const string Failed = "failed";

    private static readonly HashSet<string> _valid = new(StringComparer.Ordinal)
    {
        Running, Approval, Completed, Failed,
    };

    /// <summary>Throws ArgumentException for any status outside the four valid values.</summary>
    public static void Validate(string status)
    {
        if (!_valid.Contains(status))
        {
            throw new ArgumentException(
                $"unknown AgentBeacon status '{status}'; expected one of: running, approval, completed, failed",
                nameof(status));
        }
    }

    public static bool IsValid(string status) => _valid.Contains(status);
}

/// <summary>
/// UI-facing per-session state. Pure data + INotifyPropertyChanged; no WPF
/// dependencies so the rules can be unit-tested without a Dispatcher.
/// </summary>
public sealed class SessionViewModel : INotifyPropertyChanged
{
    private string _status = IndicatorStatus.Running;
    private string _agent = "";
    private DateTimeOffset _updatedAt;
    private string? _message;
    private string? _host;

    public required string SessionId { get; init; }

    /// <summary>
    /// Agent identifier (e.g. "claude-code", "codex"). Whole-event
    /// replacement: changing this on an existing VM fires
    /// PropertyChanged(TooltipText) so the lamp tooltip refreshes.
    /// </summary>
    public string Agent
    {
        get => _agent;
        set
        {
            if (Set(ref _agent, value ?? ""))
            {
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public string? Host
    {
        get => _host;
        set
        {
            if (Set(ref _host, value))
            {
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    /// <summary>One of the four AgentBeacon statuses. Setting an unknown value throws.</summary>
    public string Status
    {
        get => _status;
        set
        {
            IndicatorStatus.Validate(value);
            if (Set(ref _status, value)) OnPropertyChanged(nameof(LampColor));
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => Set(ref _updatedAt, value);
    }

    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Tooltip text shown on hover in the lamp list. Plain string so WPF
    /// can bind it directly. Format: "agent · session_id · host" with
    /// host omitted when absent.
    /// </summary>
    public string TooltipText
    {
        get
        {
            var s = $"{_agent} · {SessionId}";
            if (!string.IsNullOrEmpty(_host))
            {
                s += $" · {_host}";
            }
            return s;
        }
    }

    /// <summary>
    /// Visual indicator color for the vertical lamp column. Pure function of
    /// Status. Unknown statuses throw — they must never reach this point
    /// because the Receiver and the SnapshotEnvelope filter upstream.
    /// </summary>
    public string LampColor => Status switch
    {
        IndicatorStatus.Running   => "#2F81F7",  // blue
        IndicatorStatus.Approval  => "#D29922",  // yellow / amber
        IndicatorStatus.Completed => "#3FB950",  // green
        IndicatorStatus.Failed    => "#F85149",  // red
        _ => throw new InvalidOperationException(
            $"LampColor called for unknown status '{Status}'"),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static SessionViewModel FromSnapshot(SessionSnapshot s, DateTimeOffset now)
    {
        IndicatorStatus.Validate(s.Status);
        return new SessionViewModel
        {
            SessionId = s.SessionId,
            // Use the property setter so future evolution (validation /
            // normalization) is centralized. The first-set semantics:
            // equality check returns false (default vs supplied), so the
            // initial PropertyChanged for TooltipText isn't fired here —
            // it isn't bound yet anyway.
            Agent = s.Agent,
            Host = s.Host,
            Message = s.Message,
            Status = s.Status,
            UpdatedAt = s.UpdatedAt,
            FirstSeenAt = now,
        };
    }
}
