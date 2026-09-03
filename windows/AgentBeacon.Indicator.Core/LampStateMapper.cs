namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Maps a Protocol v1 status to the three-physical-light LampModule state.
///
/// The LampModule is a vertical traffic-light style:
///   top    = failed          (red)
///   middle = approval        (yellow)
///   bottom = running OR completed (blue / / green) — same physical slot, different color
///
/// No fifth state: every Protocol v1 status lights exactly ONE of the three
/// physical slots. The two non-active slots stay dim (dark housing) and
/// are never re-purposed to "idle" / "offline" / "unknown" / "paused".
/// </summary>
public enum LampSlot
{
    Top,
    Middle,
    Bottom,
}

public static class LampStateMapper
{
    public readonly struct LampModuleState
    {
        /// <summary>Which physical slot is currently active for the given status.</summary>
        public LampSlot ActiveSlot { get; init; }

        /// <summary>The bottom slot's color depends on status; the other slots have a fixed color.</summary>
        public string ActiveColorHex { get; init; }

        /// <summary>
        /// The two non-active slots render as this medium gray — bright
        /// enough that the housing reads clearly at a glance (raised from
        /// the original near-black #34383D in Round 10).
        /// </summary>
        public const string InactiveColorHex = "#59616B";

        /// <summary>Returns the color for a given slot given this active state.</summary>
        public string ColorFor(LampSlot slot) => slot == ActiveSlot ? ActiveColorHex : InactiveColorHex;
    }

    public static LampModuleState ForStatus(string status)
    {
        // IndicatorStatus.Validate throws on unknown, but here we want a
        // safe fallback-free mapping: only the four protocol statuses
        // ever reach this code path (Core layer already validated).
        switch (status)
        {
            case "failed":
                return new LampModuleState
                {
                    ActiveSlot = LampSlot.Top,
                    ActiveColorHex = "#F85149", // red
                };
            case "approval":
                return new LampModuleState
                {
                    ActiveSlot = LampSlot.Middle,
                    ActiveColorHex = "#D29922", // yellow
                };
            case "running":
                return new LampModuleState
                {
                    ActiveSlot = LampSlot.Bottom,
                    ActiveColorHex = "#2F81F7", // blue
                };
            case "completed":
                return new LampModuleState
                {
                    ActiveSlot = LampSlot.Bottom,
                    ActiveColorHex = "#3FB950", // green
                };
            default:
                // Should be unreachable: SessionViewModelStore validates
                // status upstream. Fail fast.
                throw new ArgumentException(
                    $"unknown AgentBeacon status '{status}'; expected running/approval/completed/failed",
                    nameof(status));
        }
    }

    /// <summary>
    /// Card slide-out / stay / retract stay durations. All card-bearing
    /// statuses share a single 30-second stay (Round 10), while the lamp
    /// itself remains the persistent signal.
    /// These durations are UI policy and NOT part of Protocol v1.
    /// </summary>
    public readonly struct CardStayPolicy
    {
        public int? StayMs { get; init; }

        /// <summary>Approval: 30 seconds (auto-retract; yellow lamp stays).</summary>
        public static readonly CardStayPolicy Approval = new() { StayMs = 30_000 };

        /// <summary>Completed: 30 seconds (auto-retract; green lamp stays 5 min).</summary>
        public static readonly CardStayPolicy Completed = new() { StayMs = 30_000 };

        /// <summary>Failed: 30 seconds (auto-retract; red lamp stays long-lived).</summary>
        public static readonly CardStayPolicy Failed = new() { StayMs = 30_000 };

        /// <summary>Running: no card at all (lamp alone is enough).</summary>
        public static readonly CardStayPolicy None = new() { StayMs = null };

        public static CardStayPolicy ForStatus(string status) => status switch
        {
            "approval" => Approval,
            "completed" => Completed,
            "failed" => Failed,
            "running" => None,
            _ => throw new ArgumentException(
                $"unknown AgentBeacon status '{status}'; expected running/approval/completed/failed",
                nameof(status)),
        };
    }
}
