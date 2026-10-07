using System.IO.Compression;
using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MapSizeTests
{
    [Theory]
    [InlineData("small", 160, 96)]
    [InlineData("MEDIUM", 320, 192)]
    [InlineData("large", 640, 384)]
    [InlineData("640x384", 640, 384)]
    [InlineData("200X60", 200, 60)]
    [InlineData(" 640 x 384 ", 640, 384)]
    public void ParsesPresetsAndDimensions(string text, int width, int height)
    {
        Assert.True(MapSize.TryParse(text, out var size, out _));
        Assert.Equal(new MapSize(width, height), size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("100")]
    [InlineData("100x")]
    [InlineData("-100x50")]
    [InlineData("79x24")]
    [InlineData("80x23")]
    [InlineData("641x384")]
    [InlineData("640x385")]
    [InlineData("640x192x3")]
    [InlineData("huge")]
    [InlineData("1280x384")]
    [InlineData("1x2x3")]
    public void RejectsInvalidSizesWithAMessage(string? text)
    {
        Assert.False(MapSize.TryParse(text, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void StandardSizesAndCityScenariosArePresets()
    {
        Assert.Equal(["CHI", "LA", "large", "medium", "SD", "SF", "small", "STL"],
            MapSize.Presets.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(new MapSize(MapSize.MaxWidth, MapSize.MaxHeight), MapSize.Presets["large"]);
        Assert.DoesNotContain("huge", MapSize.Describe());
    }

    [Fact]
    public void DefaultConfigMatchesTheSmallPreset()
    {
        var config = new GameConfig();
        Assert.Equal(MapSize.Presets["small"], new MapSize(config.MapWidth, config.MapHeight));
    }
}

public class LargeMapTests
{
    private static CityGame Generate(int w, int h, int seed = 3) => CityGame.New(new GameConfig { Seed = seed, MapWidth = w, MapHeight = h });

    [Fact]
    public void LargerMapsGetMoreWaterBodiesAndRoads()
    {
        var small = Generate(160, 96);
        var large = Generate(640, 384);
        Assert.True(large.Map.RoadCount > small.Map.RoadCount * 8, $"{large.Map.RoadCount} vs {small.Map.RoadCount}");

        double Bodies(int w, int h)
        {
            var content = new GameContent();
            double total = 0;
            for (int seed = 1; seed <= 20; seed++)
            {
                var map = new GameMap(w, h, content);
                total += Core.Terrain.WaterGenerator.Build(map, content.Terrains.Get("Water"), seed).Bodies.Count;
            }

            return total / 20;
        }

        Assert.True(Bodies(640, 384) > Bodies(160, 96) + 2);
    }
    [Theory]
    [InlineData(320, 192)]
    [InlineData(640, 384)]
    public void EveryExistingRoadReachesTheEdge(int w, int h)
    {
        var game = Generate(w, h);
        Assert.Equal(game.Map.RoadCount, game.Network.ConnectedRoadCount);
        Assert.True(game.Map.TerrainLayer.Count(t => t == game.Map.Content.Terrains.Get("Water").Id) > w * h / 100);
    }

    [Fact]
    public void WeeklyGrowthDoesNotRecomputeTheRoadNetwork()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(0, 21, 30, 1), ZoneType.Residential);
        var before = game.Network;
        TestCity.Advance(game, 5);
        Assert.Same(before, game.Network);

        game.Designate(new CellRect(0, 22, 5, 1), ZoneType.Commercial);
        Assert.Same(before, game.Network);

        Assert.True(game.BuildRoad(new CellRect(40, 19, 1, 1)).Success);
        Assert.NotSame(before, game.Network);

        var afterRoad = game.Network;
        Assert.True(game.Demolish(new CellRect(0, 21, 3, 1)).Success);
        Assert.Same(afterRoad, game.Network);
        Assert.True(game.Demolish(new CellRect(40, 19, 1, 1)).Success);
        Assert.NotSame(afterRoad, game.Network);
    }

    [Fact]
    public void SparseIndexesStayConsistentWithTheLayers()
    {
        var game = Generate(320, 192, seed: 8);
        var rng = new GameRandom(5);
        for (int i = 0; i < 300; i++)
        {
            var rect = new CellRect(rng.Next(300), rng.Next(80), rng.Next(1, 12), rng.Next(1, 6));
            switch (rng.Next(4))
            {
                case 0: game.Designate(rect, (ZoneType)rng.Next(1, 4)); break;
                case 1: game.BuildRoad(rect); break;
                case 2: game.Demolish(rect); break;
                default: game.AdvanceWeek(); break;
            }
        }

        void Check(GameMap map)
        {
            Assert.Equal(map.RoadLayer.Count(r => r), map.RoadCount);
            foreach (var zone in Zones.Placeable)
            {
                Assert.Equal(map.ZoneLayer.Count(z => z == zone), map.ZoneCells(zone).Count);
            }
        }

        Check(game.Map);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Check(loaded.Map);
        Assert.Equal(game.Stats, loaded.Stats);
    }

    [Fact]
    public void LargeSavesAreCompressedAndStillRoundTrip()
    {
        var game = Generate(640, 192);
        var designated = game.Designate(new CellRect(10, 10, 20, 20), ZoneType.Industrial); // some of it may be water
        string json = SaveGameStore.Serialize(game);
        Assert.True(json.Length < 300_000, $"save is {json.Length} bytes");
        var loaded = SaveGameStore.Deserialize(json);
        Assert.Equal(game.Map.TerrainLayer, loaded.Map.TerrainLayer);
        Assert.Equal(game.Map.RoadLayer, loaded.Map.RoadLayer);
        Assert.Equal(designated.Cells, loaded.Stats.Industrial.Zoned);
        Assert.True(designated.Cells > 0);
    }

    [Fact]
    public void UncompressedLegacySavesStillLoad()
    {
        var game = TestCity.Flat(seed: 4);
        game.Designate(new CellRect(0, 21, 20, 1), ZoneType.Residential);
        TestCity.Advance(game, 4);

        // Rewrite the save the way the first version stored it: raw base64, no Compression field.
        var root = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        static string Raw(string packed)
        {
            using var inflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(packed)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return Convert.ToBase64String(output.ToArray());
        }

        root.Remove("Compression");
        root.Remove("RoadTypes"); // the first version had no road types
        foreach (string layer in new[] { "Terrain", "Features", "Buildings" })
        {
            root[layer]!["Data"] = Raw(root[layer]!["Data"]!.GetValue<string>());
        }

        foreach (string key in new[] { "Roads", "Zones", "Households" })
        {
            root[key] = Raw(root[key]!.GetValue<string>());
        }

        var loaded = SaveGameStore.Deserialize(root.ToJsonString());
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(game.Map.HouseholdLayer, loaded.Map.HouseholdLayer);
    }

    [Fact]
    public void CorruptCompressedDataIsRejected()
    {
        var root = JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!.AsObject();
        root["Roads"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(root.ToJsonString()));
    }
}
