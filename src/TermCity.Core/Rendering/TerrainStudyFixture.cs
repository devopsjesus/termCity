using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public enum TerrainStudyTreatment { Current, Lighting, Balanced, DoubleHills, TripleHills }

public static class TerrainStudyFixture
{
    public static IReadOnlyList<WallComponent> ReferenceWalls(int subject)
    {
        if (subject is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(subject));
        if (subject != 1) return [];
        return [
            WallDamageLayout.PreviewTower(new(19.5f, 20.5f), WallMaterial.Wood),
            WallDamageLayout.Preview(new(19.5f, 22.3f), new(19.5f, 23.4f), WallMaterial.Wood),
        ];
    }

    public static bool LitInfrastructure(TerrainStudyTreatment treatment) => treatment != TerrainStudyTreatment.Current;
    public static bool EdgeRelief(TerrainStudyTreatment treatment) => treatment >= TerrainStudyTreatment.Balanced;
    public static TerrainElevationProfile Profile(TerrainStudyTreatment treatment) => treatment switch
    {
        TerrainStudyTreatment.DoubleHills => TerrainElevationProfile.Double,
        TerrainStudyTreatment.TripleHills => TerrainElevationProfile.Triple,
        _ => TerrainElevationProfile.Current,
    };

    public static (CityGame Game, Pos Focus) Create(int subject, TerrainStudyTreatment treatment)
    {
        if (!CityGame.DebugToolsAvailable) throw new InvalidOperationException("Terrain studies require a Debug build.");
        if (subject is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(subject));
        if (!Enum.IsDefined(treatment)) throw new ArgumentOutOfRangeException(nameof(treatment));
        var game = CityGame.New(new GameConfig { MapWidth = 64, MapHeight = 64, Seed = 42 });
        game.Paused = true;
        var map = game.Map;
        var grass = map.Content.Terrains.Get(DefaultTerrains.GrassName);
        var hill = map.Content.Terrains.Get("Hill");
        var water = map.Content.Terrains.Get(DefaultTerrains.WaterName);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, grass);
                if (x >= 14 && x <= 38 && y >= 13 && y <= 39) map.SetTerrain(x, y, hill);
                if (x >= 31 && x <= 33 || y >= 53)
                {
                    map.SetTerrain(x, y, water);
                    if (y >= 53) map.SetSeaWater(x, y, true);
                    else map.SetRiverWater(x, y, true);
                }
            }
        map.SetPreviewElevationProfile(Profile(treatment));
        if (subject == 3)
        {
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, hill);
            int xNext = 3, yNext = 4, row = 0;
            foreach (string name in new[] { "Merchant House", "Blacksmith", "Chapel", "Town Well",
                "Aqueduct", "Granary", "Watch House", "Apothecary", "Gaol" })
            {
                var building = map.Content.Buildings.Get(name);
                if (xNext + building.Width > 61) { xNext = 3; yNext += row + 4; row = 0; }
                if (yNext + building.Height > 61) throw new InvalidOperationException("The map-art gallery is too small.");
                var area = building.FootprintAt(xNext, yNext);
                foreach (var p in area.Cells()) map.SetZone(p.X, p.Y, building.Zone);
                map.SetBuildingFootprint(building, area);
                xNext += building.Width + 4;
                row = Math.Max(row, building.Height);
            }
        }
        if (subject == 1)
        {
            for (int x = 6; x <= 51; x++) map.SetRoad(x, 26, game.DefaultRoad);
            for (int y = 7; y <= 45; y++) map.SetRoad(22, y, game.DefaultRoad);
            foreach (var (name, x, y) in new[] { ("Timber Home", 17, 33), ("Stone Home", 42, 34) })
            {
                var building = map.Content.Buildings.Get(name);
                var area = building.FootprintAt(x, y);
                foreach (var cell in area.Cells()) map.SetZone(cell.X, cell.Y, ZoneType.Residential);
                map.SetBuildingFootprint(building, area);
            }
            map.FootbridgeList.Add(new(new(30, 34), new(34, 34), SettlementFootbridge.ConstructionDays));
            map.SetFeature(20, 25, map.Content.Features.Get("Tree"));
        }
        if (subject == 2)
        {
            var tree = map.Content.Features.Get("Tree");
            var rock = map.Content.Features.Get("Rock");
            foreach (int y in new[] { 17, 23, 29, 35, 41 })
                foreach (int x in new[] { 17, 23, 29, 37, 43 })
                    if (map.TerrainAt(x, y).Buildable) map.SetFeature(x, y, tree);
            foreach (var p in new[] { new Pos(15, 16), new Pos(27, 21), new Pos(40, 30), new Pos(25, 40) })
                map.SetFeature(p.X, p.Y, rock);
        }
        game.Touch();
        return (game, new Pos(29, subject == 1 ? 30 : 27));
    }
}
