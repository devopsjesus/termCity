using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>One reason residents are unhappy, with the points of happiness it costs on average.</summary>
public sealed record Complaint(string Reason, double Points);

/// <summary>
/// A health check of the whole city, recomputed whenever the city changes. Every number here comes from the map itself
/// (what is built where, which roads reach it, what the budget pays for) and feeds back into who moves in, who leaves,
/// who is born and who dies, which businesses stay and which buildings grow.
/// </summary>
public sealed record CityIndicators
{
    public int Jobs { get; init; }

    public int Workers { get; init; }

    /// <summary>Share of workers without a job, 0-1.</summary>
    public double Unemployment { get; init; }

    /// <summary>Share of jobs that have a worker, 0-1. Below about 0.7 businesses struggle to staff up.</summary>
    public double JobsFilled { get; init; } = 1;

    /// <summary>Average resident happiness, 0-100.</summary>
    public double Happiness { get; init; } = 60;

    /// <summary>Crime felt by an average resident, 0-100.</summary>
    public double Crime { get; init; }

    public double Pollution { get; init; }

    public double Education { get; init; }

    /// <summary>Roads against trips: 0 is empty, 100 is gridlock.</summary>
    public double Congestion { get; init; }

    public double LandValue { get; init; } = 40;

    /// <summary>How well each service covers the average resident (0-100), indexed by <see cref="ServiceKind"/>.</summary>
    public IReadOnlyList<double> Coverage { get; init; } = new double[ServiceKinds.Count];

    public double PowerSupplied { get; init; } = 1;

    public double WaterSupplied { get; init; } = 1;

    /// <summary>Multiplier on how many families move in each week: 0 nobody, 1 normal, 2 a boom.</summary>
    public double Attraction { get; init; } = 1;

    /// <summary>Multiplier on how many businesses open: below about 0.4 they start to close.</summary>
    public double BusinessClimate { get; init; } = 1;

    public IReadOnlyList<Complaint> Complaints { get; init; } = [];

    internal double CrimeBase { get; init; }

    internal double ResidentialTaxPenalty { get; init; }

    public static CityIndicators Neutral { get; } = new();

    public string Mood => Happiness switch
    {
        >= 80 => "Delighted",
        >= 65 => "Content",
        >= 50 => "Getting by",
        >= 35 => "Unhappy",
        _ => "Miserable",
    };

    public double CoverageOf(ServiceKind kind) => Coverage[(int)kind];
}

public static class CityAnalysis
{
    // How many souls a town needs before it starts to want each service. A hamlet has no use for a sheriff; the same
    // want grows linearly until the town is as big as the threshold and then stays at full strength. Index is the
    // ServiceKind: none, fuel, water, fire, sheriff, physic, learning, commons, lord's garrison, faith, trade, granary.
    internal static readonly int[] NeedPopulation = [0, 0, 0, 200, 400, 500, 900, 600, 150, 250, 350, 0];

    public static double Need(ServiceKind kind, int population) =>
        NeedPopulation[(int)kind] == 0 ? 0 : Math.Clamp(population / (double)NeedPopulation[(int)kind], 0, 1);

    public static CityIndicators Assess(CityGame game)
    {
        var config = game.Config;
        var map = game.Map;
        if (!config.FullRules)
        {
            return CityIndicators.Neutral;
        }

        var services = game.Services;
        var network = game.Network;
        var profile = game.Profile;
        var stats = game.Stats;

        int jobs = 0;
        foreach (var zone in new[] { ZoneType.Commercial, ZoneType.Industrial })
        {
            foreach (int i in map.ZoneCells(zone))
            {
                if (map.BuildingLayer[i] != 0 && Operating(game, i))
                {
                    jobs += map.Content.Buildings[map.BuildingLayer[i]].Capacity;
                }
            }
        }

        int workers = (int)Math.Round(stats.Adults * config.AdultParticipation + stats.Seniors * config.SeniorParticipation);
        int informal = (int)Math.Round(workers * config.InformalJobShare) + config.InformalJobsBase;
        double unemployment = workers == 0 ? 0 : Math.Clamp(1 - (jobs + informal) / (double)workers, 0, 1);
        double jobsFilled = jobs + informal == 0 ? 1 : Math.Clamp(workers / (double)(jobs + informal), 0, 1);

        double congestion = Congestion(game, stats.Population);
        int pop = stats.Population;

        // Pass over the homes: everything a resident feels is a property of where they live.
        double weight = 0, happiness = 0, crimeSum = 0, pollutionSum = 0, landSum = 0, educationSum = 0;
        var coverage = new double[ServiceKinds.Count];
        var penalties = new Dictionary<string, double>();
        double crimeBase = CrimeBase(game, unemployment, stats);
        var taxHappiness = TaxPenalty(game, game.Taxes.Residential);

        foreach (int i in map.ZoneCells(ZoneType.Residential))
        {
            if (map.BuildingLayer[i] == 0)
            {
                continue;
            }

            int people = map.HouseholdLayer[i].Total;
            if (people == 0)
            {
                continue;
            }

            var cell = Feel(game, i, pop, crimeBase, unemployment, congestion, taxHappiness, penalties, people);
            weight += people;
            happiness += people * cell.Happiness;
            crimeSum += people * cell.Crime;
            pollutionSum += people * services.Pollution(i);
            landSum += people * cell.LandValue;
            educationSum += people * services.Coverage(ServiceKind.Education, i);
            foreach (var kind in ServiceKinds.Area)
            {
                coverage[(int)kind] += people * services.Coverage(kind, i);
            }
        }

        if (weight == 0)
        {
            return CityIndicators.Neutral with
            {
                Jobs = jobs, Workers = workers, Congestion = congestion * 100,
                PowerSupplied = services.Power.Ratio, WaterSupplied = services.Water.Ratio,
            };
        }

        double avgHappiness = happiness / weight;
        for (int k = 0; k < coverage.Length; k++)
        {
            coverage[k] /= weight;
        }

        var complaints = penalties
            .Select(p => new Complaint(p.Key, p.Value / weight))
            .Where(c => c.Points >= 1.5)
            .OrderByDescending(c => c.Points)
            .Take(4)
            .ToList();

        double attraction = Attraction(profile, avgHappiness, jobs + informal, workers, taxHappiness) * SeatPull(services.SeatRank);
        double climate = BusinessClimate(game, unemployment, jobsFilled, crimeSum / weight, congestion);

        return new CityIndicators
        {
            Jobs = jobs,
            Workers = workers,
            Unemployment = unemployment,
            JobsFilled = jobsFilled,
            Happiness = avgHappiness,
            Crime = crimeSum / weight,
            Pollution = pollutionSum / weight,
            Education = educationSum / weight,
            Congestion = congestion * 100,
            LandValue = landSum / weight,
            Coverage = coverage,
            PowerSupplied = services.Power.Ratio,
            WaterSupplied = services.Water.Ratio,
            Attraction = attraction,
            BusinessClimate = climate,
            Complaints = complaints,
            CrimeBase = crimeBase,
            ResidentialTaxPenalty = taxHappiness,
        };
    }

    /// <summary>How happy the people of one home are, 0-100, using the city-wide conditions of the last assessment.</summary>
    internal static double CellHappiness(CityGame game, int i)
    {
        var ind = game.Indicators;
        return Feel(game, i, game.Stats.Population, ind.CrimeBase, ind.Unemployment, ind.Congestion / 100,
            ind.ResidentialTaxPenalty, null, 0).Happiness;
    }

    /// <summary>A building works only if a road reaches it and (in the full rules) it has power and water.</summary>
    internal static bool Operating(CityGame game, int index)
    {
        if (!game.Network.IsServed(index))
        {
            return false;
        }

        var services = game.Services;
        return services.IsPowered(game.Map, index) && services.IsWatered(game.Map, index);
    }

    private static double Congestion(CityGame game, int population)
    {
        double capacity = game.Network.TrafficCapacity * Math.Max(0.15, game.Budget.Effective(ServiceKind.None, insolvent: false));
        if (capacity <= 0)
        {
            return population > 0 ? 1 : 0;
        }

        double trips = population * game.Config.TripsPerResident * game.Profile.CarDependence;
        return Math.Clamp(trips / capacity - 0.5, 0, 1);
    }

    private static double CrimeBase(CityGame game, double unemployment, CityStats stats)
    {
        double density = stats.Residential.Filled == 0 ? 0 : Math.Min(1.5, stats.Population / (stats.Residential.Filled * 5.0));
        double raw = 8 + 55 * unemployment + 12 * density;
        return Math.Clamp(raw * game.Profile.CrimeLevel * Need(ServiceKind.Police, stats.Population) + 4, 0, 100);
    }

    private static double TaxPenalty(CityGame game, double rate) => (rate - game.Profile.FairTaxRate) * 250;

    internal readonly record struct Feeling(double Happiness, double Crime, double LandValue);

    /// <summary>How one home feels, and how much each thing costs or adds, in points of happiness (0-100).</summary>
    internal static Feeling Feel(
        CityGame game, int i, int population, double crimeBase, double unemployment, double congestion,
        double taxPenalty, Dictionary<string, double>? penalties, int weight)
    {
        var services = game.Services;
        var map = game.Map;
        var profile = game.Profile;

        double fire = services.Coverage(ServiceKind.Fire, i);
        double police = services.Coverage(ServiceKind.Police, i);
        double health = services.Coverage(ServiceKind.Health, i);
        double education = services.Coverage(ServiceKind.Education, i);
        double recreation = services.Coverage(ServiceKind.Recreation, i);
        double defence = services.Coverage(ServiceKind.Defence, i);
        double faith = services.Coverage(ServiceKind.Faith, i);
        double smog = services.Pollution(i);

        double crime = Math.Clamp(crimeBase * (1 - 0.85 * police / 100) * (1 - 0.25 * recreation / 100) *
            (1 - 0.3 * education / 100) * (1 - 0.3 * defence / 100) * (1 - 0.15 * faith / 100), 0, 100);

        double h = 66;
        void Charge(string reason, double points)
        {
            if (points == 0)
            {
                return;
            }

            h -= points;
            if (penalties is not null && points > 0)
            {
                penalties[reason] = penalties.GetValueOrDefault(reason) + points * weight;
            }
        }

        Charge("No fuel", services.IsPowered(map, i) ? 0 : 30);
        Charge("No water", services.IsWatered(map, i) ? 0 : 25);
        Charge("Smoke", 0.32 * smog * profile.SmogSensitivity);
        Charge("Lawlessness", 0.28 * crime);
        Charge("Fire watch", 12 * Need(ServiceKind.Fire, population) * (1 - fire / 100));
        Charge("Physic", 14 * Need(ServiceKind.Health, population) * (1 - health / 100));
        Charge("Learning", 9 * Need(ServiceKind.Education, population) * (1 - education / 100));
        Charge("Commons", 8 * Need(ServiceKind.Recreation, population) * (1 - recreation / 100));
        Charge("Unguarded", 10 * Need(ServiceKind.Defence, population) * (1 - defence / 100));
        Charge("Solace", 6 * Need(ServiceKind.Faith, population) * (1 - faith / 100));
        Charge("Cart traffic", 18 * congestion);
        Charge("Idleness", 45 * Math.Max(0, unemployment - 0.06));
        Charge("Tithes", Math.Clamp(taxPenalty, -4, 20));
        Charge("Road access", map.Content.Roads.Count > 0 && game.Network.AccessRank(i) == 0 ? 10 : 0);
        h += 0.05 * recreation + 0.04 * faith;

        return new Feeling(Math.Clamp(h, 0, 100), crime, LandValue(game, i, crime));
    }

    /// <summary>What a cell is worth, 0-100: good services, clean air, safe streets and good roads raise it.</summary>
    public static double LandValue(CityGame game, int i, double? crime = null)
    {
        var services = game.Services;
        double police = services.Coverage(ServiceKind.Police, i);
        double crimeNow = crime ?? 30 * (1 - 0.85 * police / 100);
        double value = 28
            + 0.10 * services.Coverage(ServiceKind.Fire, i)
            + 0.12 * services.Coverage(ServiceKind.Health, i)
            + 0.16 * services.Coverage(ServiceKind.Education, i)
            + 0.22 * services.Coverage(ServiceKind.Recreation, i)
            + 0.18 * services.Coverage(ServiceKind.Defence, i)
            + 0.05 * services.Coverage(ServiceKind.Faith, i)
            + 0.10 * services.Coverage(ServiceKind.Trade, i)
            + 6.0 * game.Network.AccessRank(i)
            - 0.35 * services.Pollution(i)
            - 0.25 * crimeNow;
        return Math.Clamp(value, 0, 100);
    }

    private static double Attraction(CityProfile profile, double happiness, int jobs, int workers, double taxPenalty)
    {
        double comfort = Math.Clamp((happiness - 32) / 34, 0, 1.35);
        double jobRatio = workers == 0 ? 1 : jobs / (double)workers;
        double work = jobRatio >= 1 ? 1 + 0.25 * Math.Min(1, jobRatio - 1) : Math.Pow(Math.Max(jobRatio, 0), 1.3);
        return Math.Clamp(comfort * work * profile.Appeal, 0, 2);
    }

    /// <summary>How much more a town draws settlers for having a lord's seat: 1 with none, up to 1.5 for a castle.</summary>
    public static double SeatPull(int seatRank) => 1 + 0.15 * Math.Clamp(seatRank, 0, 3);

    private static double BusinessClimate(CityGame game, double unemployment, double jobsFilled, double crime, double congestion)
    {
        double climate = 1.0;
        climate -= Math.Clamp(TaxPenalty(game, Math.Max(game.Taxes.Commercial, game.Taxes.Industrial)) / 25, -0.1, 0.8);
        climate -= jobsFilled < 0.7 ? (0.7 - jobsFilled) * 1.2 : 0;
        climate -= crime / 250;
        climate -= congestion * 0.25;
        if (game.Services.Power.Ratio < 1)
        {
            climate -= (1 - game.Services.Power.Ratio) * 0.8;
        }

        if (game.Services.Water.Ratio < 1)
        {
            climate -= (1 - game.Services.Water.Ratio) * 0.5;
        }

        return Math.Clamp(climate, 0, 1.3);
    }

}
