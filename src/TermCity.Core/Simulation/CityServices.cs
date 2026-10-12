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
    private readonly byte[][] _coverage = new byte[ServiceKinds.Count][];
    private readonly byte[] _pollution;
    private readonly bool _utilitiesRequired;
    private readonly int _powerThreshold;
    private readonly int _waterThreshold;
    private readonly Dictionary<ServiceKind, TravelRoutes> _routes = [];
    private readonly Dictionary<int, double> _workloads = [];
    private readonly Dictionary<int, double> _ages = [];
    private readonly double[] _peakWorkload = new double[ServiceKinds.Count];

    public double ResponseDays(ServiceKind kind, int index) =>
        _routes.TryGetValue(kind, out var routes) ? routes.Cost[index] / TravelRoutes.UnitsPerDay *
            Math.Max(1, WorkloadAt(kind, index)) : double.PositiveInfinity;

    public bool HasArrived(ServiceKind kind, int index) =>
        _routes.TryGetValue(kind, out var routes) && routes.Source[index] >= 0 &&
        _ages[routes.Source[index]] >= ResponseDays(kind, index);

    public double Workload(ServiceKind kind) => _peakWorkload[(int)kind];

    public double WorkloadAt(ServiceKind kind, int index) =>
        kind.IsUtility() ? Workload(kind) :
        _routes.TryGetValue(kind, out var routes) && routes.Source[index] >= 0
            ? _workloads.GetValueOrDefault(routes.Source[index]) : 0;

    private CityServices(
        byte[][] coverage, byte[] pollution, bool utilitiesRequired, ServiceSupply power, ServiceSupply water,
        int[] buildingCounts, int[] activeCounts, int upkeep, int seatRank, int granaryCapacity)
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
        SeatRank = seatRank;
        GranaryCapacity = granaryCapacity;
        _powerThreshold = (int)Math.Round(power.Ratio * 1000);
        _waterThreshold = (int)Math.Round(water.Ratio * 1000);
    }

    public ServiceSupply Power { get; }

    public ServiceSupply Water { get; }

    /// <summary>Civic buildings of each kind that exist, indexed by <see cref="ServiceKind"/>.</summary>
    public int[] BuildingCounts { get; }

    /// <summary>Civic buildings of each kind on walkable land; road access is optional.</summary>
    public int[] ActiveCounts { get; }

    public int WeeklyUpkeepAtFullFunding { get; }

    /// <summary>
    /// The highest rank of working castle in the town: 0 none, 1 motte and bailey, 2 stone keep, 3 castle. The seat of
    /// the lord draws settlers, lets buildings grow taller and lifts the tithe.
    /// </summary>
    public int SeatRank { get; }

    /// <summary>Measures of grain the working granaries can hold.</summary>
    public int GranaryCapacity { get; }

    /// <summary>How well an area service covers a cell, 0-100. Utilities return 0 here; use <see cref="IsPowered"/>.</summary>
    public int Coverage(ServiceKind kind, int index) => _coverage[(int)kind]?[index] ?? 0;

    public int Pollution(int index) => _pollution[index];

    public bool IsPowered(GameMap map, int index) =>
        (!_utilitiesRequired || HasArrived(ServiceKind.Power, index)) && HasUtility(map, index, _powerThreshold, salt: 0);

    public bool IsWatered(GameMap map, int index) =>
        (!_utilitiesRequired || HasArrived(ServiceKind.Water, index)) && HasUtility(map, index, _waterThreshold, salt: 7);

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
        double powerDemandFactor = 1, double elapsedDays = double.PositiveInfinity)
    {
        int count = map.Width * map.Height;
        var coverage = new byte[ServiceKinds.Count][];
        foreach (var kind in ServiceKinds.Area)
        {
            coverage[(int)kind] = new byte[count];
        }

        var pollution = new byte[count];
        var cleaners = new List<(int X, int Y, int Amount)>();
        var buildings = new int[ServiceKinds.Count];
        var active = new int[ServiceKinds.Count];
        int powerSupply = 0, waterSupply = 0, upkeep = 0, seatRank = 0, granary = 0;

        foreach (int index in map.ServiceCells.Order())
        {
            var type = map.Content.Buildings[map.BuildingLayer[index]];
            var kind = type.Service;
            buildings[(int)kind]++;
            upkeep += type.WeeklyUpkeep;
            bool working = map.BuildingIsServed(index, network.IsServed);
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
            else if (kind == ServiceKind.Granary)
            {
                granary += type.Capacity;
            }

            seatRank = Math.Max(seatRank, type.SeatRank);
            if (type.Pollution != 0)
            {
                if (type.Pollution < 0) cleaners.Add((x, y, type.Pollution));
                else EmitPollution(pollution, map, x, y, type.Pollution);
            }
        }

        // Apply all sources before cleanup so map insertion order does not change air quality.
        int powerDemand = 0, waterDemand = 0;
        foreach (int index in map.ZoneCells(ZoneType.Industrial))
        {
            if (map.BuildingLayer[index] == 0 || !map.IsBuildingAnchor(index))
            {
                continue;
            }

            var type = map.Content.Buildings[map.BuildingLayer[index]];
            EmitPollution(pollution, map, index % map.Width, index / map.Width, type.Pollution);
        }
        foreach (var cleaner in cleaners)
            EmitPollution(pollution, map, cleaner.X, cleaner.Y, cleaner.Amount);

        foreach (var zone in Zones.Placeable)
        {
            foreach (int index in map.ZoneCells(zone))
            {
                if (map.BuildingLayer[index] == 0 || !map.IsBuildingAnchor(index) || !network.IsServed(index))
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
        var result = new CityServices(coverage, pollution, config.FullRules, power, water, buildings, active, upkeep, seatRank, granary);
        result._peakWorkload[(int)ServiceKind.Power] = power.Demand / (double)Math.Max(1, power.Supply);
        result._peakWorkload[(int)ServiceKind.Water] = water.Demand / (double)Math.Max(1, water.Supply);
        foreach (var kind in ServiceKinds.All)
        {
            if (kind == ServiceKind.Granary) continue;
            var sources = map.ServiceCells.Where(i => map.Content.Buildings[map.BuildingLayer[i]].Service == kind &&
                network.IsServed(i) && (kind.IsUtility() || funding(kind) > 0)).ToArray();
            if (sources.Length == 0) continue;
            var routes = TravelRoutes.Compute(map, sources);
            result._routes.Add(kind, routes);
            foreach (int source in sources)
                result._ages[source] = map.BuildingEstablishedDays.TryGetValue(source, out double established)
                    ? Math.Max(0, elapsedDays - established) : double.PositiveInfinity;
            var load = new Dictionary<int, int>();
            foreach (int home in map.ZoneCells(ZoneType.Residential))
            {
                int source = routes.Source[home];
                if (source >= 0 && map.IsBuildingAnchor(home))
                    load[source] = load.GetValueOrDefault(source) + map.HouseholdLayer[home].Total;
            }
            foreach (int source in sources)
            {
                var type = map.Content.Buildings[map.BuildingLayer[source]];
                double capacity = kind.IsUtility() ? Math.Max(1, type.Capacity) :
                    type.StaffingCapacity * funding(kind);
                result._workloads[source] = load.GetValueOrDefault(source) / capacity;
                if (!kind.IsUtility())
                    result._peakWorkload[(int)kind] = Math.Max(result._peakWorkload[(int)kind], result._workloads[source]);
            }
            if (kind.IsUtility()) continue;
            for (int i = 0; i < count; i++)
            {
                int source = routes.Source[i];
                if (source < 0 || !result.HasArrived(kind, i)) continue;
                var type = map.Content.Buildings[map.BuildingLayer[source]];
                double timely = Math.Min(1, type.ResponseTargetDays /
                    Math.Max(1 / TravelRoutes.UnitsPerDay, routes.Cost[i] / TravelRoutes.UnitsPerDay));
                double capacityFactor = 1 / Math.Max(1, result._workloads[source]);
                coverage[(int)kind][i] = (byte)Math.Clamp(
                    (int)Math.Round(type.Strength * funding(kind) * timely * capacityFactor), 1, 100);
            }
        }
        return result;
    }

    private static void EmitPollution(byte[] field, GameMap map, int cx, int cy, int amount)
    {
        if (amount == 0)
        {
            return;
        }

        int radius = PollutionRadius(amount);
        int extent = (int)Math.Ceiling(radius * AreaFalloff.Extent);
        int verticalExtent = (int)Math.Ceiling(extent / AreaFalloff.CellAspect);
        int strength = Math.Abs(amount) * 9 + 14;
        bool clean = amount < 0;
        for (int dy = -verticalExtent; dy <= verticalExtent; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= map.Height)
            {
                continue;
            }

            for (int dx = -extent; dx <= extent; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= map.Width)
                {
                    continue;
                }

                int add = (int)Math.Round(strength * AreaFalloff.Strength(AreaFalloff.Distance(dx, dy) / radius));
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

    public static int PollutionRadius(int amount) => amount == 0 ? 0 : 3 + 2 * Math.Abs(amount);
}

/// <summary>Supply against demand for a utility. A ratio of 1 or more means everyone is served.</summary>
public readonly record struct ServiceSupply(int Supply, int Demand)
{
    public double Ratio => Demand <= 0 ? 1 : Math.Clamp(Supply / (double)Demand, 0, 1);

    public int Surplus => Supply - Demand;
}
