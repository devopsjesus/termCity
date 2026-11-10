using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class WallLookupTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.28)]
    [InlineData(0.8)]
    public void GroundBlockingMatchesFullScanAtFractionalPositions(double radius)
    {
        var state = new SettlementState(new(16, 16, 28, 28));
        foreach (bool gates in new[] { false, true })
            for (int y = 14; y < 47; y++)
                for (int x = 14; x < 47; x++)
                    foreach (float offset in new[] { .01f, .5f, .99f })
                    {
                        var point = new Vector2(x + offset, y + offset);
                        bool expected = state.Walls.Any(wall => WallDamageLayout.BlocksGround(wall,
                            point.X, point.Y, radius, gates));
                        Assert.Equal(expected, state.GroundBlocked(point, radius, gates));
                    }
    }

    [Fact]
    public void SpatialLookupMatchesFullScanAcrossRotationsMaterialsAndChanges()
    {
        var state = new SettlementState(new(16, 16, 64, 64));
        foreach (var original in state.Walls.Where(wall => wall.IsOriginal))
            Assert.Same(original, state.WallAt(original.Cell));
        state.ClearWallNetwork();
        foreach (var material in new[] { WallMaterial.Wood, WallMaterial.Stone, WallMaterial.ReinforcedStone })
        {
            state.ChangeWalls(() =>
            {
                for (int index = 0; index < 8; index++)
                {
                    float angle = index * MathF.PI / 4;
                    var start = new Vector2(28.5f + index * 4, 32.5f + index * 3);
                    state.AddWall(new WallComponent
                    {
                        Id = state.NextWallId++, Cell = new((int)start.X, (int)start.Y),
                        Start = start, End = start + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (index % 2 == 0 ? 1 : 19),
                        IsTower = index == 0, IsGate = index == 3,
                        BuiltMaterial = material, Health = 800, MaxHealth = 800,
                    });
                }
            });
            for (int y = 12; y < 94; y++)
                for (int x = 12; x < 94; x++)
                {
                    var p = new Pos(x, y);
                    var point = new Vector2(x + .5f, y + .5f);
                    var expected = state.Walls.FirstOrDefault(wall => wall.Cell == p) ?? state.Walls.Where(wall => !wall.IsOriginal &&
                        Vector2.Distance(wall.Center, point) <= 2)
                        .OrderBy(wall => Vector2.DistanceSquared(wall.Center, point))
                        .FirstOrDefault(wall => WallDamageLayout.ContainsFootprint(wall, x + .5, y + .5));
                    Assert.Same(expected, state.WallAt(p));
                }
        }
        state.ClearWallNetwork();
        Assert.Null(state.WallAt(new(28, 32)));
    }
}
