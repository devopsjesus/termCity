using TermCity.Core.Buildings;
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
    ];

    /// <summary>How well each scenario's real-world counterpart is provided for: above 1 is generous, below 1 is thin.</summary>
    private static double Provision(CityScenario scenario) => scenario switch
    {
        CityScenario.Constantinople => 1.2,
        CityScenario.Lubeck => 1.0,
        CityScenario.Genoa => 0.95,
        CityScenario.Naples => 0.8,
        CityScenario.York => 0.65,
        _ => 1.0,
    };

    public static void Seed(CityGame game)
    {
        game.Touch();
        for (int pass = 0; pass < 4 && !Supplied(game.Services.Power); pass++)
        {
            PlaceUtility(game, ServiceKind.Power);
        }

        for (int pass = 0; pass < 4 && !Supplied(game.Services.Water); pass++)
        {
            PlaceUtility(game, ServiceKind.Water);
        }

        double provision = Provision(game.Config.Scenario);
        int offset = 0;
        foreach (var (name, spacing) in Network)
        {
            var type = game.Map.Content.Buildings.Get(name);
            PlaceLattice(game, type, Math.Max(8, (int)Math.Round(spacing / provision)), offset += 7);
        }

        game.Touch();
        game.Money = Math.Max(game.Money, 8 * game.Finance.Expenses);
    }

    private static bool Supplied(ServiceSupply supply) => supply.Supply >= supply.Demand * 1.05;

    private static void PlaceUtility(CityGame game, ServiceKind kind)
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
        double needed = supply.Demand * 1.15 - supply.Supply;
        foreach (var option in options)
        {
            foreach (int i in candidates)
            {
                if (needed <= 0)
                {
                    return;
                }

                int x = i % map.Width, y = i / map.Width;
                if (map.BuildingLayer[i] != 0 || (option.RequiresWaterNearby && !map.NearWater(x, y, 1)))
                {
                    continue;
                }

                map.ClearCell(x, y);
                map.SetBuilding(x, y, option);
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

                        if (map.BuildingLayer[i] != 0)
                        {
                            built++;
                        }
                        else if (game.Network.IsServed(i))
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
                    map.ClearCell(x, y);
                    map.SetBuilding(x, y, type);
                }
            }
        }
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
