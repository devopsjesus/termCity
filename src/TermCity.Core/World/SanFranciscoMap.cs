using TermCity.Core.Features;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using static TermCity.Core.World.CityMapGeometry;

namespace TermCity.Core.World;

/// <summary>A north-up, hand-shaped Bay Area scenario; coordinates are percentages of the large map.</summary>
public static class SanFranciscoMap
{
    private static readonly (double X, double Y)[] Peninsula =
    [
        (31, 40), (38, 42), (42, 44), (50, 43), (54, 45), (59, 52), (60, 57),
        (58, 60), (60, 65), (63, 69), (63, 73), (61, 76), (64, 82), (62, 86),
        (63, 93), (62, 101), (22, 101), (22, 80), (23, 70), (24, 60), (26, 52), (29, 47),
    ];
    private static readonly (double X, double Y)[] Marin =
    [
        (-1, -1), (59, -1), (56, 8), (59, 13), (58, 18), (54, 23), (49, 19),
        (44, 23), (40, 28), (33, 27), (31, 22), (24, 20), (17, 14), (-1, 10),
    ];
    private static readonly (double X, double Y)[] EastBay =
    [
        (94, -1), (101, -1), (101, 101), (83, 101), (85, 88), (84, 77),
        (87, 65), (84, 59), (87, 49), (86, 39), (90, 23), (89, 14),
    ];
    private static readonly (double X, double Y)[] Alameda =
    [
        (79, 67), (82, 68), (83, 71), (83, 77), (80, 80), (78, 79), (78, 74), (77, 71),
    ];
    private static readonly (double X, double Y, double Rx, double Ry)[] Hills =
    [
        (30, 10, 16, 12), (46, 11, 9, 8), (59, 29, 2, 2), // Marin and Angel Island
        (35, 47, 8, 6), (49, 48, 2.5, 3), (50, 54, 2.5, 3), (55, 48, 2, 2), // Presidio, Russian/Nob/Telegraph
        (40, 57, 5, 4), (38, 72, 5, 5), (43, 77, 3, 4), (42, 86, 3, 3), // Pacific Heights, Sunset, Twin Peaks, Davidson
        (49, 82, 3, 3), (55, 76, 2, 3), (71, 53, 1.5, 1.5), (96, 45, 8, 34),
    ];

    public static GameMap Generate(GameConfig config, GameContent content)
    {
        if (config.MapWidth != 640 || config.MapHeight != 384)
            throw new ArgumentException("The SF scenario requires the large 640x384 map dimensions.", nameof(config));
        var map = new GameMap(config.MapWidth, config.MapHeight, content);
        var grass = content.Terrains.Get(DefaultTerrains.GrassName);
        var hill = content.Terrains.Get(DefaultTerrains.HillName);
        var water = content.Terrains.Get(DefaultTerrains.WaterName);
        var tree = content.Features.Get(DefaultFeatures.TreeName);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
                bool land = Contains(Peninsula, u, v) || Contains(Marin, u, v) || Contains(EastBay, u, v) ||
                    Contains(Alameda, u, v) ||
                    Ellipse(u, v, 52, 36, 1.1, 0.8) || // Alcatraz
                    Ellipse(u, v, 59, 29, 3, 3) || // Angel Island
                    Ellipse(u, v, 72, 50, 2.5, 2) || Ellipse(u, v, 71, 53, 1.5, 1.5);
                bool lake = Ellipse(u, v, 28, 90, 2, 3); // Lake Merced
                bool elevated = land && Hills.Any(h => Ellipse(u, v, h.X, h.Y, h.Rx, h.Ry));
                map.SetTerrain(x, y, !land || lake ? water : elevated ? hill : grass);
                if (land && !lake && IsPark(u, v) && CellHash.Pick(x, y, 4) != 0)
                    map.SetFeature(x, y, tree);
            }
        }

        var street = content.Roads.Get(DefaultRoads.TrackName);
        var avenue = content.Roads.Get(DefaultRoads.CobbledName);
        var highway = content.Roads.Get(DefaultRoads.KingsRoadName);
        // Golden Gate / US 101, Bay Bridge via Yerba Buena, and Market Street.
        Road(map, highway, (32, 0), (32, 25), (32, 43), (37, 56), (50, 70), (54, 100));
        Road(map, highway, (55, 60), (59, 57), (71, 53), (72, 50), (87, 57), (100, 57));
        Road(map, avenue, (58, 54), (54, 60), (49, 69), (44, 79));
        Road(map, avenue, (24, 68), (47, 68), (58, 68));
        Road(map, avenue, (32, 47), (56, 47));
        Road(map, highway, (92, 0), (91, 32), (90, 52), (89, 65), (88, 85), (87, 100));
        Road(map, avenue, (32, 17), (45, 17), (51, 16), (56, 18)); // Marin / Tiburon
        Road(map, avenue, (45, 17), (43, 24)); // Sausalito
        Road(map, avenue, (80, 74), (84, 74), (89, 70)); // Alameda bridge to Oakland
        Grid(map, (x, y) =>
        {
            double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
            return DistrictAt(u, v) != ZoneType.None && map.TerrainAt(x, y).Buildable;
        }, street, avenue);
        RoadSeparation.RemoveFragments(map, 16);

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                double u = x * 100.0 / (map.Width - 1), v = y * 100.0 / (map.Height - 1);
                ZoneType zone = DistrictAt(u, v);
                if (zone == ZoneType.None || !map.TerrainAt(x, y).Buildable || map.HasRoad(x, y)) continue;
                map.SetZone(x, y, zone);
            }
        }
        return map;
    }

    private static bool IsPark(double x, double y) =>
        x is >= 25 and <= 47 && y is >= 64 and <= 68 || // Golden Gate Park
        Ellipse(x, y, 34, 47, 7, 5) || // Presidio
        Ellipse(x, y, 43, 77, 1.5, 2) || Ellipse(x, y, 42, 86, 1.5, 1.5);

    private static ZoneType DistrictAt(double x, double y)
    {
        if (IsPark(x, y)) return ZoneType.None;
        if (y >= 45 && Contains(Peninsula, x, y))
            return x >= 57 && y is >= 75 and <= 94 ? ZoneType.Industrial
                : x >= 51 && y <= 73 || x is >= 47 and <= 54 && y is >= 55 and <= 63
                    ? ZoneType.Commercial : ZoneType.Residential;
        if (Contains(Marin, x, y) && x is >= 40 and <= 58 && y is >= 3 and <= 25)
            return x is >= 42 and <= 46 && y >= 19 || x is >= 52 and <= 56 && y >= 17
                ? ZoneType.Commercial : ZoneType.Residential;
        if (Contains(EastBay, x, y) && x <= 96 && y is >= 12 and <= 94)
            return x <= 89 && y is >= 56 and <= 76 ? ZoneType.Industrial // Port of Oakland
                : x is >= 90 and <= 93 && y is >= 52 and <= 68 || // Downtown Oakland
                    x <= 93 && y is >= 29 and <= 34 || // Berkeley
                    x <= 91 && y is >= 46 and <= 52 // Emeryville
                    ? ZoneType.Commercial : ZoneType.Residential;
        if (Contains(Alameda, x, y))
            return y is >= 71 and <= 73 ? ZoneType.Commercial : ZoneType.Residential;
        return ZoneType.None;
    }

}
