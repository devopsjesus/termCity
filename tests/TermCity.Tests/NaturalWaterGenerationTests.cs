using TermCity.Core.Rendering;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Tests;

public class NaturalWaterGenerationTests
{
    [Fact]
    public void MostInlandInletsApproachAcrossTheShortAxisOfTheirLake()
    {
        int preferred = 0, total = 0;
        for (int seed = 1; seed <= 50; seed++)
        {
            var map = new GameMap(160, 96, new GameContent());
            var plan = WaterGenerator.Build(map, map.Content.Terrains.Get("Water"), seed);
            foreach (var river in plan.Rivers.Where(r => r.Sources == 1))
            {
                var body = plan.Bodies.Single(b => b.Id == river.TargetBodyId);
                if (body.Kind != WaterKind.InlandLake) continue;
                bool northSouth = Enumerable.Range(0, map.Width).Any(x =>
                    plan.IdAt(x, 0) == river.Id || plan.IdAt(x, map.Height - 1) == river.Id);
                preferred += northSouth ? 1 : 0;
                total++;
            }
        }
        Assert.True(total >= 20);
        Assert.True(preferred >= total * 0.65, $"Only {preferred}/{total} lake inlets favored lateral spreading.");
    }

    [Fact]
    public void BroadHillBanksFormSteepCanyonsWithoutRaisingRiverWater()
    {
        var map = new GameMap(40, 40, new GameContent());
        for (int y = 4; y < 36; y++)
            for (int x = 4; x < 36; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        for (int y = 0; y < 40; y++)
            for (int x = 18; x <= 21; x++)
            {
                map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
                map.SetRiverWater(x, y, true);
            }
        Assert.True(TerrainSprite.Elevation(map, 17, 20) >= 1);
        Assert.True(TerrainSprite.Elevation(map, 22, 20) >= 1);
        var flow = TerrainSprite.Flow(map);
        Assert.True(flow.At(19, 20).Level < 0.1f);
        Assert.InRange(flow.At(19, 20).Level - flow.At(19, 21).Level, 0, 0.0011f);
    }

    [Fact]
    public void RandomMapsIncludeCanyonVarietyRatherThanCanyonsEverywhere()
    {
        int canyons = 0;
        for (int seed = 1; seed <= 30; seed++)
        {
            var map = MapGenerator.Generate(160, 96, seed, new GameContent());
            bool steep = false;
            for (int y = 8; y < map.Height - 8 && !steep; y++)
                for (int x = 8; x < map.Width - 8 && !steep; x++)
                    if (map.IsRiver(x, y))
                        steep = new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }.Any(d =>
                            map.TerrainAt(x + d.Item1, y + d.Item2).Buildable &&
                            TerrainSprite.Elevation(map, x + d.Item1, y + d.Item2) >= 1);
            if (steep) canyons++;
        }
        Assert.InRange(canyons, 3, 25);
    }
}
