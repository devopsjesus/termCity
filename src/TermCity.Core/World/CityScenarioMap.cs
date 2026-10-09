using TermCity.Core.Buildings;
using TermCity.Core.Features;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using static TermCity.Core.World.CityMapGeometry;

namespace TermCity.Core.World;

/// <summary>Hand-shaped, north-up regional city maps, not street-accurate GIS reconstructions.</summary>
public static class CityScenarioMap
{
    private static readonly (double X, double Y)[] LosAngelesCoast =
    [
        (-1, -1), (101, -1), (101, 101), (72, 101), (65, 91), (57, 84), (50, 81),
        (43, 73), (36, 72), (30, 65), (21, 58), (15, 50), (9, 43), (-1, 37),
    ];
    private static readonly (double X, double Y)[] SanDiegoCoast =
    [
        (34, -1), (101, -1), (101, 101), (30, 101), (31, 81), (35, 71),
        (38, 64), (37, 52), (32, 43), (34, 36), (29, 24), (31, 12),
    ];

    public static string Name(CityScenario scenario) => scenario switch
    {
        CityScenario.SanFrancisco => "San Francisco",
        CityScenario.LosAngeles => "Los Angeles",
        CityScenario.SanDiego => "San Diego",
        CityScenario.Chicago => "Chicago",
        CityScenario.StLouis => "St. Louis",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    public static GameMap Generate(GameConfig config, GameContent content)
    {
        if (config.Scenario == CityScenario.SanFrancisco) return SanFranciscoMap.Generate(config, content);
        _ = Name(config.Scenario);
        if (config.MapWidth != 640 || config.MapHeight != 384)
            throw new ArgumentException("City scenarios require large 640x384 dimensions.", nameof(config));
        var map = new GameMap(config.MapWidth, config.MapHeight, content);
        var grass = content.Terrains.Get(DefaultTerrains.GrassName);
        var hill = content.Terrains.Get(DefaultTerrains.HillName);
        var water = content.Terrains.Get(DefaultTerrains.WaterName);
        var tree = content.Features.Get(DefaultFeatures.TreeName);
        var street = content.Roads.Get(DefaultRoads.TrackName);
        var avenue = content.Roads.Get(DefaultRoads.CobbledName);
        var highway = content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
                bool wet = IsWater(config.Scenario, u, v);
                bool elevated = !wet && IsHill(config.Scenario, u, v);
                bool park = !wet && IsPark(config.Scenario, u, v);
                map.SetTerrain(x, y, wet ? water : elevated ? hill : grass);
                if (park && CellHash.Pick(x, y, 4) != 0) map.SetFeature(x, y, tree);
            }
        }
        foreach (var route in Routes(config.Scenario)) Road(map, highway, route);
        Grid(map, (x, y) =>
        {
            double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
            return !IsWater(config.Scenario, u, v) && !IsPark(config.Scenario, u, v) && District(config.Scenario, u, v) != ZoneType.None;
        }, street, avenue);
        RoadSeparation.RemoveFragments(map, 16);

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
                if (!map.TerrainAt(x, y).Buildable || map.HasRoad(x, y) || IsPark(config.Scenario, u, v)) continue;
                var zone = District(config.Scenario, u, v);
                if (zone == ZoneType.None) continue;
                map.SetZone(x, y, zone);
            }
        }
        return map;
    }

    internal static void Populate(GameMap map, GameConfig config)
    {
        var buildings = new BuildingType?[4];
        foreach (var zone in Zones.Placeable)
            buildings[(int)zone] = map.Content.Buildings.ForZone(zone)
                ?? throw new InvalidOperationException($"City scenarios require a growth building for {zone}.");
        var rng = GameRandom.ForStage(config.Seed,
            config.Scenario == CityScenario.SanFrancisco ? "sf-occupants" : "scenario-occupants");
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                var zone = map.ZoneAt(x, y);
                if (zone == ZoneType.None || CellHash.Pick(x, y, 10) >= 8) continue;
                map.SetBuilding(x, y, buildings[(int)zone]);
                if (zone == ZoneType.Residential) map.SetHousehold(x, y, Household.Random(rng));
            }
    }

    private static bool IsWater(CityScenario city, double x, double y) => city switch
    {
        CityScenario.LosAngeles => !Contains(LosAngelesCoast, x, y) ||
            y is >= 30 and <= 94 && Math.Abs(x - (62 + 8 * Math.Sin(y * 0.06))) < 0.6,
        CityScenario.SanDiego => (!Contains(SanDiegoCoast, x, y) ||
            Ellipse(x, y, 34, 42, 6, 6) || Ellipse(x, y, 39, 69, 9, 20)) &&
            !Ellipse(x, y, 34, 72, 2, 11), // Coronado inside San Diego Bay
        CityScenario.Chicago => x > 71 + 4 * Math.Sin(y * 0.045) || // Lake Michigan, east
            y > 52 && Math.Abs(x - (61 - (y - 52) * 0.32)) < 0.65 || // South Branch
            y < 52 && Math.Abs(x - (61 - (52 - y) * 0.22)) < 0.65 || // North Branch
            x is >= 61 and <= 75 && Math.Abs(y - 52) < 0.65,
        CityScenario.StLouis => Math.Abs(x - (69 + 4 * Math.Sin(y * 0.06))) < 2 || // Mississippi
            y < 30 && Math.Abs(y - (14 + x * 0.12)) < 1.3 || // Missouri confluence
            y > 65 && x < 67 && Math.Abs(y - (91 - x * 0.22)) < 0.7, // Meramec
        _ => throw new ArgumentOutOfRangeException(nameof(city)),
    };

    private static bool IsHill(CityScenario city, double x, double y) => city switch
    {
        CityScenario.LosAngeles => Ellipse(x, y, 24, 32, 22, 9) || Ellipse(x, y, 67, 14, 29, 14) ||
            Ellipse(x, y, 59, 88, 5, 6),
        CityScenario.SanDiego => Ellipse(x, y, 75, 32, 22, 22) ||
            Ellipse(x, y, 41, 27, 5, 6) || Ellipse(x, y, 72, 75, 12, 13),
        CityScenario.Chicago => false,
        CityScenario.StLouis => Ellipse(x, y, 20, 47, 13, 25) || Ellipse(x, y, 32, 85, 15, 13),
        _ => throw new ArgumentOutOfRangeException(nameof(city)),
    };

    private static bool IsPark(CityScenario city, double x, double y) => city switch
    {
        CityScenario.LosAngeles => Ellipse(x, y, 51, 34, 5, 5) || // Griffith Park
            y < 25 && IsHill(city, x, y),
        CityScenario.SanDiego => Ellipse(x, y, 53, 55, 4, 5) || // Balboa Park
            Ellipse(x, y, 75, 34, 10, 8),
        CityScenario.Chicago => x is >= 68 and <= 71 && y is >= 36 and <= 67 ||
            Ellipse(x, y, 31, 45, 3, 4),
        CityScenario.StLouis => Ellipse(x, y, 39, 51, 7, 4) || // Forest Park
            Ellipse(x, y, 46, 70, 3, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(city)),
    };

    private static ZoneType District(CityScenario city, double x, double y) => city switch
    {
        CityScenario.LosAngeles when x is >= 18 and <= 90 && y is >= 27 and <= 96 =>
            x is >= 62 and <= 79 && y >= 63 ? ZoneType.Industrial // Vernon / Long Beach port
                : x is >= 54 and <= 63 && y is >= 48 and <= 60 || // Downtown
                    x is >= 27 and <= 35 && y is >= 47 and <= 57 || // Santa Monica
                    x is >= 40 and <= 57 && y is >= 38 and <= 44 // Hollywood
                    ? ZoneType.Commercial : ZoneType.Residential,
        CityScenario.SanDiego when x is >= 32 and <= 89 && y is >= 10 and <= 97 =>
            x is >= 46 and <= 65 && y >= 75 || x is >= 43 and <= 49 && y is >= 49 and <= 54
                ? ZoneType.Industrial // National City / working waterfront
                : x is >= 47 and <= 61 && y is >= 59 and <= 69 || x is >= 38 and <= 46 && y is >= 22 and <= 29
                    ? ZoneType.Commercial : ZoneType.Residential,
        CityScenario.Chicago when x is >= 9 and <= 72 && y is >= 8 and <= 96 =>
            x is >= 30 and <= 59 && y >= 70 ? ZoneType.Industrial // South/West Side corridors
                : x is >= 61 and <= 68 && y is >= 49 and <= 59 ? ZoneType.Commercial // Loop
                    : ZoneType.Residential,
        CityScenario.StLouis when x is >= 15 and <= 95 && y is >= 28 and <= 96 =>
            x is >= 65 and <= 81 && (y < 48 || y > 64) ? ZoneType.Industrial // riverfront / Metro East
                : x is >= 55 and <= 66 && y is >= 48 and <= 59 || x is >= 45 and <= 51 && y is >= 49 and <= 55
                    ? ZoneType.Commercial : ZoneType.Residential,
        _ => ZoneType.None,
    };

    private static (int X, int Y)[][] Routes(CityScenario city) => city switch
    {
        CityScenario.LosAngeles =>
        [
            [(60, 0), (58, 53), (66, 72), (74, 100)],
            [(26, 52), (28, 54), (58, 53), (100, 53)],
            [(29, 34), (36, 58), (60, 80), (73, 95)],
        ],
        CityScenario.SanDiego =>
        [
            [(43, 0), (45, 40), (50, 66), (57, 100)],
            [(64, 0), (59, 52), (56, 100)],
            [(33, 71), (49, 64), (65, 62), (100, 62)],
        ],
        CityScenario.Chicago =>
        [
            [(47, 0), (49, 34), (57, 57), (47, 100)],
            [(0, 57), (64, 57), (67, 74), (64, 100)],
            [(23, 0), (24, 44), (36, 78), (36, 100)],
            [(67, 0), (67, 49), (67, 60), (67, 100)],
        ],
        CityScenario.StLouis =>
        [
            [(57, 0), (58, 52), (55, 79), (46, 100)],
            [(0, 54), (63, 54), (75, 53), (100, 53)],
            [(0, 80), (55, 77), (75, 74), (100, 74)],
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(city)),
    };
}
