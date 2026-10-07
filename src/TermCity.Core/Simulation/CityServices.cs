using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>
/// Everything the civic buildings do to the map: how well each area service covers every cell (0-100), the smog
/// that industry and coal plants put out, and how much power and water the city makes against how much it uses.
/// Built from the sparse service-building index, so it costs in proportion to what has been built.
/// </summary>
public sealed class CityServices
{
    private readonly byte[][] _coverage = new byte[8][];
    private readonly byte[] _pollution;
    private readonly bool _utilitiesRequired;
    private readonly int _powerThreshold;
    private readonly int _waterThreshold;

    private CityServices(
        byte[][] coverage, byte[] pollution, bool utilitiesRequired, ServiceSupply power, ServiceSupply water,
        int[] buildingCounts, int[] activeCounts, int upkeep)
    {
        for (int i = 0; i < coverage.Length; i++)
        {
            _coverage[i] = coverage[i];
        }

        _pollution = pollution;
        _utilitiesRequired = utilitiesRequired;
        Power = power;
        Water = water;
        BuildingCounts = buildingCounts;
        ActiveCounts = activeCounts;
        WeeklyUpkeepAtFullFunding = upkeep;
        _powerThreshold = (int)Math.Round(power.Ratio * 1000);
        _waterThreshold = (int)Math.Round(water.Ratio * 1000);
    }

    public ServiceSupply Power { get; }

    public ServiceSupply Water { get; }

    /// <summary>Civic buildings of each kind that exist, indexed by <see cref="ServiceKind"/>.</summary>
    public int[] BuildingCounts { get; }

    /// <summary>Civic buildings of each kind that are working: they need a road connection to the outside world.</summary>
    public int[] ActiveCounts { get; }

    public int WeeklyUpkeepAtFullFunding { get; }

    /// <summary>How well an area service covers a cell, 0-100. Utilities return 0 here; use <see cref="IsPowered"/>.</summary>
    public int Coverage(ServiceKind kind, int index) => _coverage[(int)kind]?[index] ?? 0;

    public int Pollution(int index) => _pollution[index];

    public bool IsPowered(GameMap map, int index) => HasUtility(map, index, _powerThreshold, salt: 0);

    public bool IsWatered(GameMap map, int index) => HasUtility(map, index, _waterThreshold, salt: 7);

    // Shortages are spread evenly and always hit the same cells (a stable hash), so a half-supplied city browns out
    // half of its blocks rather than flickering, and every extra unit of supply restores a few more.
    private bool HasUtility(GameMap map, int index, int threshold, int salt)
    {
        if (!_utilitiesRequired || threshold >= 1000)
        {
            return true;
        }

        int x = index % map.Width, y = index / map.Width;
        return CellHash.Pick(x + salt, y + salt * 3, 1000) < threshold;
    }

    public static CityServices Compute(
        GameMap map, RoadNetwork network, GameConfig config, Func<ServiceKind, double> funding, double waterFactor = 1,
        double powerDemandFactor = 1)
    {
        int count = map.Width * map.Height;
        var coverage = new byte[8][];
        foreach (var kind in ServiceKinds.Area)
        {
            coverage[(int)kind] = new byte[count];
        }

        var pollution = new byte[count];
        var buildings = new int[8];
        var active = new int[8];
        int powerSupply = 0, waterSupply = 0, upkeep = 0;

        foreach (int index in map.ServiceCells.Order())
        {
            var type = map.Content.Buildings[map.BuildingLayer[index]];
            var kind = type.Service;
            buildings[(int)kind]++;
            upkeep += type.WeeklyUpkeep;
            bool working = network.IsServed(index);
            if (!working)
            {
                continue;
            }

            active[(int)kind]++;
            int x = index % map.Width, y = index / map.Width;
            if (kind == ServiceKind.Power)
            {
                powerSupply += type.Capacity;
            }
            else if (kind == ServiceKind.Water)
            {
                waterSupply += type.Capacity;
            }
            else if (type.Radius > 0)
            {
                Splat(coverage[(int)kind], map, x, y, type.Radius, type.Strength * funding(kind), fade: 0.6);
            }

            if (type.Pollution != 0)
            {
                EmitPollution(pollution, map, x, y, type.Pollution);
            }
        }

        // Smoke from industry. Parks (recreation) later scrub some of it away at the point of use.
        int powerDemand = 0, waterDemand = 0;
        foreach (int index in map.ZoneCells(ZoneType.Industrial))
        {
            if (map.BuildingLayer[index] == 0)
            {
                continue;
            }

            var type = map.Content.Buildings[map.BuildingLayer[index]];
            EmitPollution(pollution, map, index % map.Width, index / map.Width, type.Pollution);
        }

        foreach (var zone in Zones.Placeable)
        {
            foreach (int index in map.ZoneCells(zone))
            {
                if (map.BuildingLayer[index] == 0 || !network.IsServed(index))
                {
                    continue;
                }

                var type = map.Content.Buildings[map.BuildingLayer[index]];
                powerDemand += type.PowerUse;
                waterDemand += type.WaterUse;
            }
        }

        var power = new ServiceSupply(powerSupply, (int)Math.Round(powerDemand * powerDemandFactor));
        var water = new ServiceSupply((int)Math.Round(waterSupply * waterFactor), waterDemand);
        return new CityServices(coverage, pollution, config.FullRules, power, water, buildings, active, upkeep);
    }

    // Strength falls linearly from the full value at the building to (1 - fade) of it at the edge of the radius,
    // and overlapping buildings combine as independent chances so two stations are better than one but not twice as good.
    private static void Splat(byte[] field, GameMap map, int cx, int cy, int radius, double strength, double fade)
    {
        int s = (int)Math.Round(strength);
        if (s <= 0)
        {
            return;
        }

        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= map.Height)
            {
                continue;
            }

            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx;
                int d2 = dx * dx + dy * dy;
                if (x < 0 || x >= map.Width || d2 > r2)
                {
                    continue;
                }

                double falloff = 1 - fade * Math.Sqrt(d2) / radius;
                int add = (int)Math.Round(s * falloff);
                int i = y * map.Width + x;
                int old = field[i];
                field[i] = (byte)(old + add - old * add / 100);
            }
        }
    }

    private static void EmitPollution(byte[] field, GameMap map, int cx, int cy, int amount)
    {
        if (amount == 0)
        {
            return;
        }

        int radius = 3 + 2 * Math.Abs(amount);
        int r2 = radius * radius;
        int strength = Math.Abs(amount) * 9 + 14;
        bool clean = amount < 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= map.Height)
            {
                continue;
            }

            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx;
                int d2 = dx * dx + dy * dy;
                if (x < 0 || x >= map.Width || d2 > r2)
                {
                    continue;
                }

                int add = (int)(strength * (1 - Math.Sqrt(d2) / (radius + 1)));
                int i = y * map.Width + x;
                if (clean)
                {
                    field[i] = (byte)Math.Max(0, field[i] - add / 2);
                }
                else
                {
                    field[i] = (byte)Math.Min(100, field[i] + add);
                }
            }
        }
    }
}

/// <summary>Supply against demand for a utility. A ratio of 1 or more means everyone is served.</summary>
public readonly record struct ServiceSupply(int Supply, int Demand)
{
    public double Ratio => Demand <= 0 ? 1 : Math.Clamp(Supply / (double)Demand, 0, 1);

    public int Surplus => Supply - Demand;
}
