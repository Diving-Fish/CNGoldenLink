namespace CNGoldenLink;

// Local diagnostic format, deliberately distinct from the website protocol.
public sealed record Observation(
    string? Sid, string? Side, string? Room, string Scene,
    bool? Paused, bool? Transitioning, bool? Completed,
    bool? PlayerPresent, bool? Dead, bool? HoldingGolden, bool Focused);

public sealed record DiagnosticEntry(
    long Sequence, DateTimeOffset ObservedAt, long MonotonicMs,
    string Kind, Observation? Observation, long Dropped,
    string? Detail = null, AreaStatistics? AreaStats = null)
{
    public string Schema => "cn-golden-link/diagnostic-1";
    public string Quality => "snapshot";
    public string? AttemptId => null;
    public long? ActiveMs => null;
    public object Capabilities => new {
        attemptEvents = false, practiceDetection = "none",
        cctState = false, routeNodes = false, activityTiming = false
    };
}
