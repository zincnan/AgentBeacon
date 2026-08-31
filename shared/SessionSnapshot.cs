using System.Text.Json.Serialization;

namespace AgentBeacon.Shared;

/// <summary>
/// Single session's current state, used both inside the Receiver and across
/// the Named Pipe to the Indicator.
/// </summary>
public sealed class SessionSnapshot
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("updated_at")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Envelope for every message sent over the Named Pipe IPC.
/// v1 only defines "snapshot" — full state, sent on connect and on every change.
/// </summary>
public sealed class SnapshotEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("sessions")]
    public required IReadOnlyList<SessionSnapshot> Sessions { get; init; }
}
