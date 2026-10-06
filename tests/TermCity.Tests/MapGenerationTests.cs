using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MapGenerationTests
{
    private static GameMap Generate(int seed) => CityGame.New(new GameConfig { Seed = seed }).Map;

    [Fact]
    public void SameSeedProducesIdenticalMaps()
    {
        var a = Generate(42);
        var b = Generate(42);
        Assert.Equal(a.TerrainLayer, b.TerrainLayer);
        Assert.Equal(a.FeatureLayer, b.FeatureLayer);
        Assert.Equal(a.RoadLayer, b.RoadLayer);
    }

    [Fact]
    public void DifferentSeedsProduceDifferentMaps()
    {
        Assert.NotEqual(Generate(1).TerrainLayer, Generate(2).TerrainLayer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    public void MapContainsAllRequestedElements(int seed)
    {
        var map = Generate(seed);
        int water = map.TerrainLayer.Count(t => t == map.Content.Terrains.Get("Water").Id);
        int hills = map.TerrainLayer.Count(t => t == map.Content.Terrains.Get("Hill").Id);
        int trees = map.FeatureLayer.Count(f => f == map.Content.Features.Get("Tree").Id);
        int rocks = map.FeatureLayer.Count(f => f == map.Content.Features.Get("Rock").Id);
        int roads = map.RoadLayer.Count(r => r);

        Assert.InRange(water, 50, map.Width * map.Height * 45 / 100);
        Assert.InRange(hills, 300, map.Width * map.Height / 2);
        Assert.True(trees > 100, $"trees={trees}");
        Assert.True(rocks > 30, $"rocks={rocks}");
        Assert.True(roads > map.Width / 2, $"roads={roads}");
    }

    [Fact]
    public void DefaultMapIsTwoByFourScreens()
    {
        var map = Generate(1);
        Assert.Equal(160, map.Width);
        Assert.Equal(96, map.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void ExistingRoadsReachTheMapEdgeAndAreConnected(int seed)
    {
        var game = CityGame.New(new GameConfig { Seed = seed });
        var map = game.Map;
        int roads = map.RoadLayer.Count(r => r);
        Assert.True(game.Network.ConnectedRoadCount > 0);
        Assert.Equal(roads, game.Network.ConnectedRoadCount);
    }

    [Fact]
    public void NoFeaturesOnWaterOrRoads()
    {
        var map = Generate(5);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (map.FeatureAt(x, y) is not null)
                {
                    Assert.True(map.TerrainAt(x, y).AllowsFeatures);
                    Assert.False(map.HasRoad(x, y));
                }
            }
        }
    }

    [Fact]
    public void NewTerrainTypesCanBeRegisteredWithTheirOwnGenerator()
    {
        var content = new GameContent();
        var swamp = content.Terrains.Register(new TerrainType
        {
            Name = "Swamp",
            Glyphs = ["%"],
            Foreground = new Rgb(1, 2, 3),
            Background = new Rgb(4, 5, 6),
            Buildable = false,
            Generator = new StripeGenerator(),
        });

        var game = CityGame.New(new GameConfig { Seed = 3 }, content);
        Assert.Equal(swamp, game.Map.TerrainAt(0, 0));
        Assert.True(swamp.Id > content.Terrains.Get("Water").Id);
    }

    private sealed class StripeGenerator : ITerrainGenerator
    {
        public int Order => 100;

        public void Generate(GenerationContext context, TerrainType terrain) => context.Map.SetTerrain(0, 0, terrain);
    }
}
