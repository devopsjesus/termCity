using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RiverRippleTests
{
    [Fact]
    public void RipplesAreSparseAndMoveDownstreamAcrossCellBoundaries()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int row = 0; row < 32; row++) map.SetTerrain(12, row, map.Content.Terrains.Get("Water"));
        var flow = new WaterFlowField(map);
        int active = Enumerable.Range(0, 24).Count(row => RiverRippleLayout.At(map, flow, 12, row).Position(0.5) is not null);
        Assert.Equal(3, active);
        var track = RiverRippleLayout.At(map, flow, 12, 7);
        Assert.Equal(24, track.Distance);
        Assert.True(track.Position(0.8)!.Value.Y > track.Position(0.4)!.Value.Y);
        var before = track.Position((1 - 0.0001) / RiverRippleLayout.Speed)!.Value + new Vector2(12, 7);
        var after = RiverRippleLayout.At(map, flow, 12, 8).Position((1 + 0.0001) / RiverRippleLayout.Speed)!.Value + new Vector2(12, 8);
        Assert.InRange(Vector2.Distance(before, after), 0, 0.001f);
        Assert.Null(RiverRippleLayout.At(map, flow, 10, 10).Position(0));
        Assert.Null(RiverRippleLayout.At(map, flow, 12, 31).Position(0));
    }

    [Fact]
    public void RiverCornerUsesContinuousRoundedTrackInsideWater()
    {
        var map = new GameMap(32, 32, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int row = 0; row <= 16; row++) map.SetTerrain(12, row, water);
        for (int column = 12; column < 32; column++) map.SetTerrain(column, 16, water);
        var flow = new WaterFlowField(map);
        var corner = RiverRippleLayout.At(map, flow, 12, 16);
        Assert.Equal(new Pos(0, 1), corner.Incoming);
        Assert.Equal(new Pos(1, 0), corner.Outgoing);
        double start = (8 - corner.Distance % 8) / (double)RiverRippleLayout.Speed;
        for (int step = 0; step < 100; step++)
        {
            var position = corner.Position(start + step / 100.0 / RiverRippleLayout.Speed)!.Value;
            Assert.InRange(position.X, 0, 1);
            Assert.InRange(position.Y, 0, 1);
        }
        var middle = corner.Position(start + 0.5 / RiverRippleLayout.Speed)!.Value;
        Assert.InRange(Vector2.Distance(new(0.625f, 0.375f), middle), 0, 0.00001f);
        var upstream = RiverRippleLayout.At(map, flow, 12, 15);
        var before = upstream.Position(start - 0.0001 / RiverRippleLayout.Speed)!.Value + new Vector2(12, 15);
        var after = corner.Position(start + 0.0001 / RiverRippleLayout.Speed)!.Value + new Vector2(12, 16);
        Assert.InRange(Vector2.Distance(before, after), 0, 0.001f);
        Assert.Equal(corner, RiverRippleLayout.At(map, new WaterFlowField(map), 12, 16));
    }
}
