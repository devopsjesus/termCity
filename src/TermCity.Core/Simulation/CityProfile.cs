namespace TermCity.Core.Simulation;

/// <summary>
/// The character of a place: what makes it grow, what threatens it, what it is short of. A profile only tilts the
/// shared engine (it never adds rules of its own), so every city can be understood in terms of the same few dials.
/// 1.0 is neutral for every multiplier.
/// </summary>
public sealed record CityProfile
{
    public string Summary { get; init; } = "An open stretch of land and a good road.";

    /// <summary>Pull on new residents beyond what the city itself provides: climate, reputation, the regional economy.</summary>
    public double Appeal { get; init; } = 1;

    /// <summary>Water the town can draw per well or aqueduct: low in dry country, high beside a great river or sea.</summary>
    public double WaterSupply { get; init; } = 1;

    /// <summary>Fuel each building needs: hearths, kilns and forges.</summary>
    public double PowerDemand { get; init; } = 1;

    public double CrimeLevel { get; init; } = 1;

    public double FireRisk { get; init; } = 1;

    public double OutbreakRisk { get; init; } = 1;

    public double FloodRisk { get; init; } = 1;

    /// <summary>Chance, per decade, of an earthquake that damages the city.</summary>
    public double QuakeRisk { get; init; }

    /// <summary>Smoke bothers residents more where it hangs in the air (basins, sea fog).</summary>
    public double SmogSensitivity { get; init; } = 1;

    /// <summary>How much a town leans on carts and drays: more traffic per resident.</summary>
    public double CarDependence { get; init; } = 1;

    /// <summary>Appetite for density: scales the land value needed before buildings grow taller.</summary>
    public double DensityAppetite { get; init; } = 1;

    /// <summary>The tithe rate residents consider fair; higher rates than this are felt.</summary>
    public double FairTaxRate { get; init; } = 0.09;

    public static CityProfile For(CityScenario scenario) => scenario switch
    {
        CityScenario.Constantinople => new()
        {
            Summary = "A walled peninsula at the crossing of the world: steep land, dear plots, a famous harbour and the fault underfoot.",
            Appeal = 1.25, DensityAppetite = 0.8, QuakeRisk = 0.5, FireRisk = 1.2, CarDependence = 0.7, FairTaxRate = 0.11,
        },
        CityScenario.Naples => new()
        {
            Summary = "A crowded, sun-baked bay town under a smoking mountain, loud with quarrels and thirsty for water carried from afar.",
            Appeal = 1.2, WaterSupply = 0.75, PowerDemand = 1.15, CrimeLevel = 1.1, FireRisk = 1.3, QuakeRisk = 0.4,
            SmogSensitivity = 1.5, CarDependence = 1.6, DensityAppetite = 1.3, FairTaxRate = 0.10,
        },
        CityScenario.Genoa => new()
        {
            Summary = "A proud harbour republic of galleys and tower-houses, with dry hills that burn.",
            Appeal = 1.15, WaterSupply = 0.65, FireRisk = 1.4, QuakeRisk = 0.15, CarDependence = 1.3,
            DensityAppetite = 1.15, FairTaxRate = 0.09,
        },
        CityScenario.Lubeck => new()
        {
            Summary = "A Hanseatic salt-and-timber port on a cold sea: hard winters, busy guilds, brick gables and a long memory of fire.",
            Appeal = 1.1, WaterSupply = 1.6, PowerDemand = 1.3, CrimeLevel = 1.2, FireRisk = 1.2, FloodRisk = 1.1,
            CarDependence = 0.8, DensityAppetite = 0.9, FairTaxRate = 0.10,
        },
        CityScenario.York => new()
        {
            Summary = "A river minster town past its peak: broad old lanes, fewer trades, flood-prone banks and a thin tithe.",
            Appeal = 0.8, WaterSupply = 1.3, CrimeLevel = 1.35, FireRisk = 1.1, OutbreakRisk = 1.1, FloodRisk = 1.7,
            CarDependence = 1.1, DensityAppetite = 1.1, FairTaxRate = 0.08,
        },
        _ => new(),
    };
}
