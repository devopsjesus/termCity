using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WaterFlowTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 9)]
    [InlineData(true, 8)]
    [InlineData(true, 9)]
    public void WideRiverRipplesTravelAlongBothBanksInsteadOfAcrossTheChannel(bool horizontal, int width)
    {
        var map = new GameMap(48, 48, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int along = 0; along < 48; along++)
            for (int across = 12; across < 12 + width; across++)
            {
                int column = horizontal ? along : across, row = horizontal ? across : along;
                map.SetTerrain(column, row, water);
                map.SetRiverWater(column, row, true);
            }
        var flow = new WaterFlowField(map);
        var expected = horizontal ? new Pos(-1, 0) : new Pos(0, 1);
        for (int along = 10; along < 34; along++)
            for (int across = 12; across < 12 + width; across++)
            {
                int column = horizontal ? along : across, row = horizontal ? across : along;
                Assert.Equal(expected, flow.At(column, row).Direction);
                var track = RiverRippleLayout.At(map, flow, column, row);
                Assert.Equal(expected, track.Incoming);
                Assert.Equal(expected, track.Outgoing);
                Assert.Equal(flow.At(column, row).Distance - 1,
                    flow.At(column + expected.X, row + expected.Y).Distance);
            }
    }

    [Fact]
    public void WideBentRiverFollowsItsCourseBeforeAndAfterTheTurn()
    {
        var map = new GameMap(48, 48, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int row = 0; row <= 28; row++)
            for (int column = 12; column <= 20; column++)
            {
                map.SetTerrain(column, row, water);
                map.SetRiverWater(column, row, true);
            }
        for (int row = 20; row <= 28; row++)
            for (int column = 12; column < 48; column++)
            {
                map.SetTerrain(column, row, water);
                map.SetRiverWater(column, row, true);
            }
        var flow = new WaterFlowField(map);
        for (int column = 12; column <= 20; column++) Assert.Equal(new Pos(0, 1), flow.At(column, 10).Direction);
        for (int row = 20; row <= 28; row++) Assert.Equal(new Pos(1, 0), flow.At(34, row).Direction);
        for (int row = 0; row < 48; row++)
            for (int column = 0; column < 48; column++)
            {
                var current = flow.At(column, row);
                if (!current.River) continue;
                var next = flow.At(column + current.Direction.X, row + current.Direction.Y);
                Assert.Equal(current.Distance - 1, next.Distance);
                Assert.InRange(current.Level - next.Level, 0, 0.00101f);
            }
    }

    [Fact]
    public void NarrowRiverFlowsToItsOutletWithoutCyclesOrTerraceDrops()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 0; y < map.Height; y++) map.SetTerrain(12, y, map.Content.Terrains.Get("Water"));
        var flow = TerrainSprite.Flow(map);
        for (int y = 0; y < map.Height - 1; y++)
        {
            var current = flow.At(12, y);
            var downstream = flow.At(12, y + 1);
            Assert.Equal(new Pos(0, 1), current.Direction);
            Assert.True(current.Distance > downstream.Distance);
            Assert.True(current.Level >= downstream.Level);
            Assert.InRange(current.Level - downstream.Level, 0, 0.00101f);
        }
        Assert.Equal(new Pos(0, 0), flow.At(12, map.Height - 1).Direction);
    }

    [Fact]
    public void RiverFlowsIntoTheBroadLakeRatherThanBackTowardItsSource()
    {
        var map = new GameMap(48, 48, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int y = 0; y < 35; y++)
            for (int x = 22; x < 25; x++) map.SetTerrain(x, y, water);
        for (int y = 30; y < 43; y++)
            for (int x = 16; x < 32; x++) map.SetTerrain(x, y, water);
        var flow = TerrainSprite.Flow(map);
        int cx = 23, cy = 1, previous = flow.At(cx, cy).Distance;
        for (int step = 0; step < 48; step++)
        {
            var cell = flow.At(cx, cy);
            if (cell.Direction == new Pos(0, 0)) break;
            cx += cell.Direction.X; cy += cell.Direction.Y;
            Assert.True(flow.At(cx, cy).Distance < previous);
            previous = flow.At(cx, cy).Distance;
        }
        Assert.True(cy >= 30);
        Assert.False(flow.At(23, 36).River);
    }

    [Fact]
    public void OpenWaterIsOneLevelAndFlowSurvivesSaveLoadDeterministically()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        var flow = TerrainSprite.Flow(map);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(flow.At(5, 5), TerrainSprite.Flow(loaded.Map).At(5, 5));
        Assert.Equal(flow.At(5, 5).Level, flow.At(20, 20).Level);
        Assert.False(flow.At(5, 5).River);
        map.SetTerrain(5, 5, map.Content.Terrains.Get("Meadow"));
        Assert.NotSame(flow, TerrainSprite.Flow(map));
    }

    [Fact]
    public void ClassifiedWideRiverFlowsDownstreamAcrossItsWholeWidthIntoTheSea()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var water = map.Content.Terrains.Get("Water");
        for (int y = 0; y < 32; y++)
            for (int x = 18; x <= 32; x++)
            {
                map.SetTerrain(x, y, water);
                if (y >= 26) map.SetSeaWater(x, y, true);
                else map.SetRiverWater(x, y, true);
            }
        var flow = TerrainSprite.Flow(map);
        for (int x = 19; x <= 31; x++)
        {
            var cell = flow.At(x, 10);
            Assert.True(cell.River);
            Assert.Equal(new Pos(0, 1), cell.Direction);
            Assert.True(cell.Distance > flow.At(x, 11).Distance);
        }
        Assert.False(flow.At(24, 28).River);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.True(restored.Map.IsRiver(24, 10));
        Assert.False(restored.Map.IsRiver(24, 28));
        Assert.Equal(flow.At(24, 10), TerrainSprite.Flow(restored.Map).At(24, 10));
    }
}
