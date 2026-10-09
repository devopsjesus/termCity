using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>
/// Gives a pre-built scenario city the power plants, waterworks and civic buildings a real city of its size would already
/// have, so the player inherits a working, if imperfect, place instead of one that collapses on the first day.
/// </summary>
internal static class ScenarioSeeder
{
    // Lattice spacing in cells for one station of each kind at "normal" provision; larger spacing is thinner cover.
    private static readonly (string Building, int Spacing)[] Network =
    [
        ("Fire Watch", 24), ("Sheriff's Hall", 28), ("Apothecary", 22), ("Infirmary", 64),
        ("Chantry School", 22), ("Monastery", 84), ("Village Green", 15),
        ("Chapel", 17), ("Market Cross", 20), ("Motte and Bailey", 26),
    ];

    /// <summary>How well each scenario's real-world counterpart is provided for: above 1 is generous, below 1 is thin.</summary>
    private static double Provision(CityScenario scenario) => scenario switch
    {
        CityScenario.SanFrancisco => 1.2,
        CityScenario.Chicago => 1.0,
        CityScenario.SanDiego => 0.95,
        CityScenario.LosAngeles => 0.8,
        CityScenario.StLouis => 0.65,
        _ => 1.0,
    };

    public static void Seed(CityGame game)
    {
        game.Touch();
        PlaceUtility(game, ServiceKind.Power, PlannedDemand(game, ServiceKind.Power));
        PlaceUtility(game, ServiceKind.Water, PlannedDemand(game, ServiceKind.Water));

        double provision = Provision(game.Config.Scenario);
        int offset = 0;
        foreach (var (name, spacing) in Network)
        {
            var type = game.Map.Content.Buildings.Get(name);
            PlaceLattice(game, type, Math.Max(8, (int)Math.Round(spacing / provision)), offset += 7);
        }

        // The lord's seat and the town's grain store stand near the middle of the settlement.
        PlaceCentral(game, "Stone Keep", Centroid(game.Map, ZoneType.Residential));
        PlaceCentral(game, "Granary", Centroid(game.Map, ZoneType.Commercial));

        CityScenarioMap.Populate(game.Map, game.Config);
        game.Touch();
        game.Money = Math.Max(game.Money, 8 * game.Finance.Expenses);
    }

    private static int PlannedDemand(CityGame game, ServiceKind kind)
    {
        int demand = 0;
        foreach (var zone in Zones.Placeable)
        {
            var type = game.Map.Content.Buildings.ForZone(zone)
                ?? throw new InvalidOperationException($"City scenarios require a growth building for {zone}.");
            foreach (int index in game.Map.ZoneCells(zone))
                if (game.Network.IsServed(index) && CellHash.Pick(index % game.Map.Width, index / game.Map.Width, 10) < 8)
                    demand += kind == ServiceKind.Power ? type.PowerUse : type.WaterUse;
        }
        return kind == ServiceKind.Power ? (int)Math.Round(demand * game.Profile.PowerDemand) : demand;
    }

    private static void PlaceUtility(CityGame game, ServiceKind kind, int demand)
    {
        var map = game.Map;
        var supply = kind == ServiceKind.Power ? game.Services.Power : game.Services.Water;
        var buildings = map.Content.Buildings;
        BuildingType[] options = kind == ServiceKind.Power
            ? [buildings.Get("Charcoal Burners")]
            : [buildings.Get("Aqueduct"), buildings.Get("Town Well")];

        // Power goes in the industrial district; water at the shore, then anywhere vacant once the shore is used up.
        var centre = Centroid(map, kind == ServiceKind.Power ? ZoneType.Industrial : ZoneType.Residential);
        var candidates = new List<int>();
        for (int i = 0; i < map.Width * map.Height; i++)
        {
            if (map.ZoneAt(i % map.Width, i / map.Width) == ZoneType.None || map.BuildingLayer[i] != 0 || !game.Network.IsServed(i))
            {
                continue;
            }

            if (kind == ServiceKind.Power && map.ZoneAt(i % map.Width, i / map.Width) != ZoneType.Industrial)
            {
                continue;
            }

            candidates.Add(i);
        }

        candidates.Sort((a, b) => Distance2(map, a, centre).CompareTo(Distance2(map, b, centre)) is var c && c != 0 ? c : a.CompareTo(b));
        double needed = demand * 1.15 - supply.Supply;
        foreach (var option in options)
        {
            if (needed <= 0) break;
            foreach (int i in candidates)
            {
                if (needed <= 0)
                {
                    break;
                }

                int x = i % map.Width, y = i / map.Width;
                if (!CanSeed(game, option, x, y) || UtilityCrowdsDistrict(map, option, x, y))
                {
                    continue;
                }

                Place(game.Map, option, x, y);
                needed -= option.Capacity * (kind == ServiceKind.Water ? game.Profile.WaterSupply : 1);
            }
        }

        game.Touch();
    }

    private static void PlaceLattice(CityGame game, BuildingType type, int spacing, int offset)
    {
        var map = game.Map;
        int half = spacing / 2;
        for (int gy = offset % spacing; gy < map.Height; gy += spacing)
        {
            for (int gx = offset * 3 % spacing; gx < map.Width; gx += spacing)
            {
                int best = -1, bestD = int.MaxValue, built = 0;
                for (int y = Math.Max(0, gy - half); y < Math.Min(map.Height, gy + half); y++)
                {
                    for (int x = Math.Max(0, gx - half); x < Math.Min(map.Width, gx + half); x++)
                    {
                        int i = map.Index(x, y);
                        if (map.ZoneAt(x, y) == ZoneType.None)
                        {
                            continue;
                        }

                        if (CellHash.Pick(x, y, 10) < 8) built++;
                        if (game.Network.IsServed(i) && CanSeed(game, type, x, y))
                        {
                            int d = (x - gx) * (x - gx) + (y - gy) * (y - gy);
                            if (d < bestD)
                            {
                                best = i;
                                bestD = d;
                            }
                        }
                    }
                }

                if (best >= 0 && built >= spacing * spacing / 6)
                {
                    int x = best % map.Width, y = best / map.Width;
                    Place(map, type, x, y);
                }
            }
        }
    }

    /// <summary>Puts one building on the served, vacant footprint nearest <paramref name="centre"/>.</summary>
    private static void PlaceCentral(CityGame game, string name, (int X, int Y) centre)
    {
        var map = game.Map;
        var type = map.Content.Buildings.Get(name);
        int best = -1;
        long bestD = long.MaxValue;
        for (int i = 0; i < map.Width * map.Height; i++)
        {
            if (map.ZoneAt(i % map.Width, i / map.Width) == ZoneType.None || !game.Network.IsServed(i) ||
                !CanSeed(game, type, i % map.Width, i / map.Width))
            {
                continue;
            }

            long d = Distance2(map, i, centre);
            if (d < bestD)
            {
                best = i;
                bestD = d;
            }
        }

        if (best >= 0)
        {
            Place(map, type, best % map.Width, best / map.Width);
        }
    }

    private static bool CanSeed(CityGame game, BuildingType type, int x, int y)
    {
        var map = game.Map;
        var area = new CellRect(x, y, type.Width, type.Height);
        foreach (var p in area.Cells())
            if (!map.InBounds(p) || !map.TerrainAt(p.X, p.Y).Buildable || map.HasRoad(p.X, p.Y) ||
                map.BuildingAt(p.X, p.Y) is not null || game.Network.RoadOverlapsCell(map.Index(p.X, p.Y)))
                return false;
        return !type.RequiresWaterNearby || area.Cells().Any(p => map.NearWater(p.X, p.Y, 1));
    }

    private static void Place(GameMap map, BuildingType type, int x, int y)
    {
        var area = new CellRect(x, y, type.Width, type.Height);
        foreach (var p in area.Cells()) map.ClearCell(p.X, p.Y);
        map.SetBuildingFootprint(type, area);
    }

    private static bool UtilityCrowdsDistrict(GameMap map, BuildingType type, int x, int y)
    {
        // Keep a strip of district plots between utility reservations, rather than consuming a whole neighbourhood.
        for (int cy = y - 1; cy <= y + type.Height; cy++)
            for (int cx = x - 1; cx <= x + type.Width; cx++)
                if (map.InBounds(cx, cy) && map.BuildingAt(cx, cy) is not null) return true;
        return false;
    }

    private static (int X, int Y) Centroid(GameMap map, ZoneType zone)
    {
        long sx = 0, sy = 0;
        int n = 0;
        foreach (int i in map.ZoneCells(zone))
        {
            sx += i % map.Width;
            sy += i / map.Width;
            n++;
        }

        return n == 0 ? (map.Width / 2, map.Height / 2) : ((int)(sx / n), (int)(sy / n));
    }

    private static long Distance2(GameMap map, int index, (int X, int Y) c)
    {
        long dx = index % map.Width - c.X, dy = index / map.Width - c.Y;
        return dx * dx + dy * dy;
    }
}
