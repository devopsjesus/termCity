using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public readonly record struct ZoneCount(int Zoned, int Filled, int Served, int AwaitingRemoval = 0)
{
    public int Occupied => Filled + AwaitingRemoval;
}

public sealed record CityStats(
    int Population,
    int Adults,
    int Children,
    int Seniors,
    int Households,
    ZoneCount Residential,
    ZoneCount Commercial,
    ZoneCount Industrial,
    int RoadCells,
    int ConnectedRoadCells,
    int WeeklyIncome,
    int VacantHomes = 0,
    double ResidentialIncome = 0,
    double CommercialIncome = 0,
    double IndustrialIncome = 0)
{
    public double IncomeFor(ZoneType zone) => zone switch
    {
        ZoneType.Residential => ResidentialIncome,
        ZoneType.Commercial => CommercialIncome,
        ZoneType.Industrial => IndustrialIncome,
        _ => 0,
    };

    public ZoneCount For(ZoneType zone) => zone switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => default,
    };
}

/// <summary>Growth pressure from prospective arrivals and unfilled business capacity, from 0 to 1.</summary>
public readonly record struct Demand(double Residential, double Commercial, double Industrial)
{
    public static int SupportingHomes(CityStats stats, GameConfig config) => config.FullRules
        ? Math.Max(stats.Households + stats.Residential.AwaitingRemoval, stats.Population / 5)
        : stats.Residential.Occupied;

    public double For(ZoneType zone) => zone switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => 0,
    };

    public static int SupportedBuildings(CityStats stats, GameConfig config, ZoneType zone)
    {
        if (zone == ZoneType.Residential) return int.MaxValue;
        int homes = SupportingHomes(stats, config);
        if (homes < config.MinResidentialCells) return 0;
        int ratio = zone == ZoneType.Commercial ? config.ResidentialPerCommercial : config.ResidentialPerIndustrial;
        return (int)Math.Ceiling(homes / (double)ratio);
    }

    public static double ExpectedArrivals(CityStats stats, GameConfig config, ZoneType zone,
        double attraction = 1, double businessClimate = 1, double travel = 1)
    {
        if (zone == ZoneType.Residential && config.FullRules)
        {
            int effective = Math.Max(stats.Residential.Filled, stats.Population / 5);
            return (config.MaxNewResidentialPerWeek + effective * config.MigrationRatePerWeek) * attraction * travel;
        }
        int existing = zone == ZoneType.Residential ? stats.Residential.Occupied : SupportedBuildings(stats, config, zone);
        int baseCap = zone switch
        {
            ZoneType.Residential => config.MaxNewResidentialPerWeek,
            ZoneType.Commercial => config.MaxNewCommercialPerWeek,
            ZoneType.Industrial => config.MaxNewIndustrialPerWeek,
            _ => 0,
        };
        double rate = baseCap + Math.Ceiling(Math.Max(0, existing) * Math.Max(0, config.GrowthRatePerWeek));
        return rate * (config.FullRules && zone != ZoneType.Residential ? businessClimate : 1);
    }

    public static Demand Compute(CityStats stats, GameConfig config,
        double attraction = 1, double businessClimate = 1, double travel = 1)
    {
        double Pressure(ZoneType zone)
        {
            double arrivals = ExpectedArrivals(stats, config, zone, attraction, businessClimate, travel);
            if (zone == ZoneType.Residential)
                return Math.Clamp(arrivals / Math.Max(1, config.MaxNewResidentialPerWeek), 0, 1);
            int supported = SupportedBuildings(stats, config, zone);
            int remaining = Math.Max(0, supported - stats.For(zone).Occupied);
            return supported == 0 ? 0 : Math.Clamp(Math.Min(remaining, arrivals) / supported, 0, 1);
        }
        return new(Pressure(ZoneType.Residential), Pressure(ZoneType.Commercial), Pressure(ZoneType.Industrial));
    }
}
