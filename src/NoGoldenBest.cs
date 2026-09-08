namespace CNGoldenLink;

// Conservative observation state; never seed this from AreaModeStats.BestDeaths.
public sealed class NoGoldenAttempt
{
    private object? session;
    private bool eligible;
    private bool consumed;
    private int lastDeaths;
    private bool awaitingSession;
    public void Start(object identity, bool fromStart, bool resumed, int deaths, bool golden)
    {
        session = identity;
        eligible = fromStart && !resumed && deaths == 0 && !golden;
        consumed = false;
        lastDeaths = deaths;
        awaitingSession = true;
    }
    public void Observe(object identity, int deaths, bool golden)
    {
        // Level.OnEnter may run while Engine.Scene is still the outgoing Level.
        // Do not let its final Update invalidate the incoming session before it is observed.
        if (!ReferenceEquals(session, identity)) { if (!awaitingSession) eligible = false; return; }
        awaitingSession = false;
        if (golden || deaths < lastDeaths || deaths < 0) eligible = false;
        lastDeaths = deaths;
    }
    public int? Complete(object identity, int deaths, bool fromStart, bool golden)
    {
        if (!ReferenceEquals(session, identity)) return null;
        Observe(identity, deaths, golden);
        if (!eligible || consumed || !fromStart) return null;
        consumed = true;
        return deaths;
    }
    public void Invalidate() => eligible = false;
}

public sealed record AreaStatistics(string DatasetId, string Sid, string Side,
    int? NoGoldenBestDeaths, string Source = "observed_no_native_golden_clear_v1",
    string PracticeDetection = "none", int? TotalDeaths = null);

public static class SavedAreaStatistics
{
    public static IEnumerable<AreaStatistics> Read(string dataset, IReadOnlyDictionary<string, int> bests,
        IReadOnlyDictionary<string, int> totals) {
        foreach (var pair in bests) {
            int split = pair.Key.LastIndexOf('|');
            if (split <= 0 || pair.Value < 0) continue;
            var side = pair.Key[(split + 1)..];
            if (side is not ("Normal" or "BSide" or "CSide")) continue;
            yield return new(dataset, pair.Key[..split], side, pair.Value,
                TotalDeaths: totals.TryGetValue(pair.Key, out int total) && total >= 0 ? total : null);
        }
    }
}
