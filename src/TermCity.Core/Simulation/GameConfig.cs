namespace TermCity.Core.Simulation;

public enum GameSpeed
{
    Slow,
    Medium,
    Fast,
}

/// <summary>All tunable rules and starting values. Serialized into save files so a loaded game keeps its rules.</summary>
public sealed record GameConfig
{
    /// <summary>The default map is 160x96 logical cells.</summary>
    public int MapWidth { get; init; } = 160;

    public int MapHeight { get; init; } = 96;

    public int Seed { get; init; } = 1;
    public CityScenario Scenario { get; init; }
    // Retains compatibility with saves created before the other city presets existed.
    public bool SanFrancisco
    {
        get => Scenario == CityScenario.SanFrancisco;
        init
        {
            if (value) Scenario = CityScenario.SanFrancisco;
            else if (Scenario == CityScenario.SanFrancisco) Scenario = CityScenario.Random;
        }
    }

    public int? StartingYear { get; init; }

    /// <summary>Enough for about 100 street cells: an access road plus a small neighbourhood grid before any tax comes in.</summary>
    public int StartingMoney { get; init; } = 50_000;

    public int RoadCostPerCell { get; init; } = 500;

    /// <summary>Residential cells that must be filled before any commercial or industrial cell can fill.</summary>
    public int MinResidentialCells { get; init; } = 10;

    /// <summary>One filled commercial cell for every this-many filled residential cells (rounded up once the minimum is met).</summary>
    public int ResidentialPerCommercial { get; init; } = 20;

    public int ResidentialPerIndustrial { get; init; } = 10;

    /// <summary>Tax collected each week is this fraction of the weekly value of every filled cell.</summary>
    public double DefaultTaxRate { get; init; } = 0.05;

    /// <summary>How far (in cells, through open ground) a connected road serves surrounding zones.</summary>
    public int RoadServiceReach { get; init; } = 2;

    // How many cells can fill in a week. These are the caps for a brand new city; as the city grows the cap grows with
    // it (see GrowthRatePerWeek), so a large city does not take centuries to fill.
    public int MaxNewResidentialPerWeek { get; init; } = 3;

    public int MaxNewCommercialPerWeek { get; init; } = 1;

    public int MaxNewIndustrialPerWeek { get; init; } = 1;

    /// <summary>
    /// The weekly cap grows by this fraction of the cells already filled (of the commercial and industrial cells that
    /// the residents allow, for those zones), so growth compounds: 0.02 is two percent a week.
    /// </summary>
    public double GrowthRatePerWeek { get; init; } = 0.02;

    public int WeeksPerYear { get; init; } = 52;

    /// <summary>A week is made of this many days; things happen a little on each day and taxes arrive at the end of the week.</summary>
    public int DaysPerWeek { get; init; } = 7;

    // Real seconds per game week (1.2, 0.6 or 0.2 seconds per day).
    public double SlowSecondsPerWeek { get; init; } = 8.4;

    public double MediumSecondsPerWeek { get; init; } = 4.2;

    public double FastSecondsPerWeek { get; init; } = 1.4;

    public double SecondsPerWeek(GameSpeed speed) => speed switch
    {
        GameSpeed.Slow => SlowSecondsPerWeek,
        GameSpeed.Fast => FastSecondsPerWeek,
        _ => MediumSecondsPerWeek,
    };
}
