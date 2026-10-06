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
    int WeeklyIncome)
{
    public ZoneCount For(ZoneType zone) => zone switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => default,
    };
}

/// <summary>How much residents want more of each zone type, from 0 (none) to 1 (strong).</summary>
public readonly record struct Demand(double Residential, double Commercial, double Industrial)
{
    public double For(ZoneType zone) => zone switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => 0,
    };

    public static Demand Compute(CityStats stats, GameConfig config)
    {
        int zonedR = stats.Residential.Zoned;
        int filledR = stats.Residential.Occupied;

        // Housing is needed to support the commercial and industrial areas that have been designated.
        int neededForCommercial = stats.Commercial.Zoned == 0 ? 0 : config.ResidentialPerCommercial * (stats.Commercial.Zoned - 1) + 1;
        int neededForIndustrial = stats.Industrial.Zoned == 0 ? 0 : config.ResidentialPerIndustrial * (stats.Industrial.Zoned - 1) + 1;
        int neededR = Math.Max(config.MinResidentialCells, Math.Max(neededForCommercial, neededForIndustrial));

        double residential = Math.Clamp((neededR - zonedR) / (double)neededR, 0, 1);
        if (zonedR > 0 && stats.Residential.Filled >= zonedR)
        {
            // Every home is taken: people want somewhere else to live.
            residential = Math.Max(residential, 0.6);
        }

        double commercial = 0, industrial = 0;
        if (filledR >= config.MinResidentialCells)
        {
            commercial = ShortfallFor(filledR, config.ResidentialPerCommercial, stats.Commercial.Zoned);
            industrial = ShortfallFor(filledR, config.ResidentialPerIndustrial, stats.Industrial.Zoned);
        }

        return new Demand(residential, commercial, industrial);
    }

    private static double ShortfallFor(int filledResidential, int ratio, int zoned)
    {
        int allowed = (int)Math.Ceiling(filledResidential / (double)ratio);
        return Math.Clamp((allowed - zoned) / (double)allowed, 0, 1);
    }
}
