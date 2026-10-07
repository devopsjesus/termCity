namespace TermCity.Core.Simulation;

public enum EventKind
{
    Fire,
    Outbreak,
    Flood,
    Earthquake,
    Abandonment,
    Closure,
    Milestone,
    Finance,
    Upgrade,
}

/// <summary>Something that happened to the city. <see cref="Cells"/> are the map cells it touched (for a UI to point at).</summary>
public sealed record CityEvent(int Week, EventKind Kind, string Message, IReadOnlyList<int> Cells, bool Bad = true);
