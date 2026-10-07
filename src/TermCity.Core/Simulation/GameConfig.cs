namespace TermCity.Core.Simulation;

public enum GameSpeed
{
    Slow,
    Medium,
    Fast,
}

/// <summary>
/// Classic keeps the original sandbox: families move in at a steady rate, one flat tax, nothing needs power or water, and
/// nothing goes wrong. Full is the city simulation: services, utilities, jobs, ageing, migration, density, budgets and events.
/// </summary>
public enum CityRules
{
    Classic,
    Full,
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

    /// <summary>Which rules the city plays by. See <see cref="CityRules"/>.</summary>
    public CityRules Rules { get; init; } = CityRules.Full;

    /// <summary>True when utilities, services, jobs, demographics, budgets and disasters matter.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FullRules => Rules == CityRules.Full;

    /// <summary>Residential cells that must be filled before any commercial or industrial cell can fill.</summary>
    public int MinResidentialCells { get; init; } = 10;

    /// <summary>One filled commercial cell for every this-many filled residential cells (rounded up once the minimum is met).</summary>
    public int ResidentialPerCommercial { get; init; } = 20;

    public int ResidentialPerIndustrial { get; init; } = 10;

    /// <summary>Tax collected each week is this fraction of the weekly value of every filled cell.</summary>
    public double DefaultTaxRate { get; init; } = 0.09;

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

    /// <summary>Share of adults who work (and, for seniors, still work).</summary>
    public double AdultParticipation { get; init; } = 0.8;

    public double SeniorParticipation { get; init; } = 0.2;

    /// <summary>Share of children who work (herding, gleaning, spinning, apprenticed at ten): medieval children start early.</summary>
    public double ChildLabour { get; init; } = 0.12;

    /// <summary>Jobs that exist without a building: self-employed, home businesses, odd jobs, as a share of workers.</summary>
    public double InformalJobShare { get; init; } = 0.15;

    /// <summary>Odd jobs and farm work any settlement offers, so a hamlet of a few families is not "unemployed" before its first shop opens.</summary>
    public int InformalJobsBase { get; init; } = 30;

    /// <summary>Trips each resident makes on the road network in a week, before the place's car dependence.</summary>
    public double TripsPerResident { get; init; } = 0.9;

    /// <summary>Full rules: share of the city's population that arrives or leaves in a week when it is perfectly attractive.</summary>
    public double MigrationRatePerWeek { get; init; } = 0.006;

    /// <summary>Full rules: the share of tax income a city of <see cref="AdministrationFullAt"/> people or more spends on payroll, welfare and overhead.</summary>
    public double AdministrationShare { get; init; } = 0.55;

    public int AdministrationFullAt { get; init; } = 50_000;

    /// <summary>Full rules: weekly chance, per building, that a fire starts (before fire cover and the place's fire risk).</summary>
    public double FireIgnitionPerBuildingWeek { get; init; } = 0.00009;

    public int OutbreakMinPopulation { get; init; } = 250;

    public double OutbreakChancePerWeek { get; init; } = 0.006;

    /// <summary>Share of a stricken town's people who die each week of a plague with no physic to help them.</summary>
    public double PlagueDeathPerWeek { get; init; } = 0.012;

    /// <summary>Weekly chance bandits strike a town (before the place's raid risk, hunger, the sheriff and the garrison).</summary>
    public double RaidChancePerWeek { get; init; } = 0.007;

    public int RaidMinPopulation { get; init; } = 150;

    /// <summary>Grain, in measures, a person eats in a week.</summary>
    public double GrainPerPersonWeek { get; init; } = 0.1;

    /// <summary>Weeks of grain households can keep in their own bins, before any granary.</summary>
    public double HouseholdStoreWeeks { get; init; } = 10;

    public double MaxStoreWeeks { get; init; } = 78;

    /// <summary>Gold a measure of grain costs from merchants in a shortage, in a town with no market.</summary>
    public double GrainPrice { get; init; } = 5;

    /// <summary>Gold a soul owes the crown each Michaelmas, before the relief a lord's seat and a charter win.</summary>
    public double TributePerSoul { get; init; } = 2;

    /// <summary>Towns smaller than this are beneath the crown's notice.</summary>
    public int TributeMinPopulation { get; init; } = 150;

    /// <summary>Gold each pilgrim spends at a feast, before the market multiplier.</summary>
    public double PilgrimGold { get; init; } = 3;

    public double FloodChancePerWeek { get; init; } = 0.012;

    /// <summary>Weekly chance that a filled cell is considered for a density upgrade or downgrade.</summary>
    public double DensityChangePerWeek { get; init; } = 0.006;

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
