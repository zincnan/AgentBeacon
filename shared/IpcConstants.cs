namespace AgentBeacon.Shared;

/// <summary>
/// Constants for the local Named Pipe IPC between Receiver and Indicator.
/// This IPC is a Windows-internal implementation detail; it is NOT part of
/// Protocol v1 (the Agent -> Receiver HTTP contract documented in
/// docs/protocol.md).
/// </summary>
public static class IpcConstants
{
    /// <summary>
    /// Default pipe name. Both Receiver and Indicator fall back to this
    /// when no --pipe override is supplied. The Receiver exposes it via
    /// --pipe; the Indicator passes the same value when launching.
    /// </summary>
    public const string DefaultPipeName = "AgentBeacon.Status";

    /// <summary>
    /// Snapshot envelope type discriminator. v1 only emits "snapshot".
    /// </summary>
    public const string SnapshotType = "snapshot";
}
