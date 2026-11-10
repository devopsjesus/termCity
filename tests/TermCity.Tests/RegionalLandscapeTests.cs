using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RegionalLandscapeTests
{
    [Theory]
    [InlineData(80, 48, 1)]
    [InlineData(160, 96, 7)]
    [InlineData(320, 320, 42)]
    [InlineData(320, 192, 1234)]
    public void DefaultRandomMapsAreOpenWithCoherentWoodedAndHillAreas(int width, int height, int seed)
    {
        var map = MapGenerator.Generate(width, height, seed, new GameContent());
        var land = Enumerable.Range(0, width * height).Where(cell => map.TerrainAt(cell % width, cell / width).Buildable).ToArray();
        var forests = LandscapeRegions.Create(new(map, seed), "forest:Tree", .125, .07);
        var hills = land.Where(cell => map.TerrainAt(cell % width, cell / width).Name == DefaultTerrains.HillName).ToArray();
        var trees = land.Where(cell => map.FeatureAt(cell % width, cell / width)?.Name == "Tree").ToArray();
        Assert.InRange(hills.Length / (double)land.Length, .10, .15);
        Assert.InRange(forests.Mask.Count(value => value) / (double)land.Length, .10, .15);
        Assert.NotEmpty(trees);
        Assert.True(trees.Count(cell => forests.Mask[cell]) >= trees.Length * .75,
            $"Most trees must belong to forest regions: inside={trees.Count(cell => forests.Mask[cell])}, total={trees.Length}.");
        int exterior = trees.Count(cell => !forests.Mask[cell]);
        Assert.True(exterior <= land.Length * .003, $"Exterior trees must remain sparse: {exterior}/{land.Length}.");
        var components = Components(map, forests.Mask);
        Assert.True(components.Count >= 2, "Forest regions must be distributed rather than one giant patch.");
        Assert.True(components.Where(size => size >= 16).Sum() >= forests.TargetCells * .9,
            "Forest coverage must form coherent patches, not scattered pixels.");
        var hillMask = new bool[width * height];
        foreach (int cell in hills) hillMask[cell] = true;
        var hillComponents = Components(map, hillMask);
        Assert.True(hillComponents.Count >= 2, "Hill areas must be distributed around the map.");
        Assert.True(hillComponents.Where(size => size >= 16).Sum() >= hills.Length * .9,
            "Hill terrain must form substantial groups, not isolated single-cell peaks.");
    }

    [Fact]
    public void WaterLayersAreIndependentOfTheNewHillDistribution()
    {
        var content = new GameContent();
        var expected = new GameMap(160, 96, content);
        content.Terrains.Get("Water").Generator!.Generate(new(expected, 42), content.Terrains.Get("Water"));
        var actual = MapGenerator.Generate(160, 96, 42, content);
        Assert.Equal(expected.SeaLayer, actual.SeaLayer);
        Assert.Equal(expected.RiverLayer, actual.RiverLayer);
        Assert.Equal(expected.TerrainLayer.Select(id => id == content.Terrains.Get("Water").Id),
            actual.TerrainLayer.Select(id => id == content.Terrains.Get("Water").Id));
    }

    [Fact]
    public void RegionMasksAreDeterministicAndHandleTinyOrWaterOnlyMaps()
    {
        var map = new GameMap(8, 8, new GameContent());
        var first = LandscapeRegions.Create(new(map, 42), "forest:Tree", .125);
        Assert.Equal(first.Mask, LandscapeRegions.Create(new(map, 42), "forest:Tree", .125).Mask);
        Assert.Equal(8, first.Mask.Count(value => value));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        Assert.DoesNotContain(true, LandscapeRegions.Create(new(map, 42), "forest:Tree", .125).Mask);
    }

    [Fact]
    public void ExplicitCustomHillGeneratorsRetainTheirOriginalNoiseAndOrder()
    {
        var generator = new HillGenerator { Coverage = .09, Frequency = .032 };
        var map = new GameMap(80, 48, new GameContent());
        var context = new GenerationContext(map, 42);
        var hill = map.Content.Terrains.Get(DefaultTerrains.HillName);
        generator.Generate(context, hill);
        var noise = new PerlinNoise(context.CreateRandom("hills"));
        var values = Enumerable.Range(0, map.Width * map.Height).Select(cell =>
            noise.Fractal(cell % map.Width * generator.Frequency * .5,
                cell / map.Width * generator.Frequency, 3)).ToArray();
        double threshold = PerlinNoise.ThresholdForCoverage(values, generator.Coverage);
        Assert.False(generator.GeographicRegions);
        Assert.Equal(10, generator.Order);
        Assert.Equal(values.Select(value => value >= threshold), map.TerrainLayer.Select(id => id == hill.Id));
    }

    private static List<int> Components(GameMap map, bool[] mask)
    {
        var seen = new bool[mask.Length];
        var sizes = new List<int>();
        var queue = new Queue<int>();
        for (int cell = 0; cell < mask.Length; cell++)
        {
            if (!mask[cell] || seen[cell]) continue;
            seen[cell] = true;
            queue.Enqueue(cell);
            int size = 0;
            while (queue.TryDequeue(out int current))
            {
                size++;
                int x = current % map.Width, y = current / map.Width;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (map.InBounds(x + dx, y + dy))
                        {
                            int next = map.Index(x + dx, y + dy);
                            if (!mask[next] || seen[next]) continue;
                            seen[next] = true;
                            queue.Enqueue(next);
                        }
            }
            sizes.Add(size);
        }
        return sizes;
    }
}
