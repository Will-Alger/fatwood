namespace ResearchDiscovery.Api.Hosting;

/// <summary>
/// Whether an analysis job is currently executing. Per-paper progress comes
/// from the database (results persist one by one); this flag lets the UI tell
/// "still working" apart from "finished with some papers declined/failed",
/// which would otherwise look identical mid-poll.
/// </summary>
public class AnalysisProgressTracker
{
    private volatile bool _active;

    public bool Active => _active;

    public void Begin() => _active = true;

    public void End() => _active = false;
}
