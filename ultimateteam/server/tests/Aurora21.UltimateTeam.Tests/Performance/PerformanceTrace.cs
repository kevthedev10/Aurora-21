using System.Diagnostics.Tracing;

namespace Aurora21.UltimateTeam.Tests.Performance;

/// <summary>Test-only phase boundaries; no identities, SQL, or payloads.</summary>
[EventSource(Name = "Aurora21-Performance")]
internal sealed class PerformanceTrace : EventSource
{
    public static PerformanceTrace Log { get; } = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Start(string phase)
    {
        if (IsEnabled()) { WriteEvent(1, phase); }
    }

    [Event(2, Level = EventLevel.Informational)]
    public void Stop(string phase)
    {
        if (IsEnabled()) { WriteEvent(2, phase); }
    }
}
