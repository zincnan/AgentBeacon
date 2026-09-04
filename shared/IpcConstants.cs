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
    /// Pipe-name prefix. The effective default pipe name is
    /// <see cref="PipeNameForPort"/>, i.e. the prefix plus the Receiver's
    /// port — so two Receivers running on different ports (or a dev
    /// instance next to an installed one) can never broadcast on the same
    /// pipe and get each other's sessions mixed into the Indicator.
    /// </summary>
    public const string DefaultPipeName = "AgentBeacon.Status";

    /// <summary>
    /// Effective default pipe name for a Receiver listening on
    /// <paramref name="port"/>: "AgentBeacon.Status.&lt;port&gt;".
    /// The Indicator derives the same name from the shared
    /// agentbeacon.json, so the pair always meets on the right pipe.
    /// </summary>
    public static string PipeNameForPort(int port) => $"{DefaultPipeName}.{port}";

    /// <summary>
    /// Snapshot envelope type discriminator. v1 only emits "snapshot".
    /// </summary>
    public const string SnapshotType = "snapshot";
}
