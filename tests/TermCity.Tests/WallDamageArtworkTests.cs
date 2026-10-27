using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class WallDamageArtworkTests
{
    private static WallComponent Wall(double fraction, bool tower = false, bool gate = false)
    {
        var wall = new WallComponent();
        Set(wall, nameof(wall.Cell), new Pos(18, 30));
        Set(wall, nameof(wall.MaxHealth), 200d);
        Set(wall, nameof(wall.Health), fraction * 200);
        Set(wall, nameof(wall.IsTower), tower);
        Set(wall, nameof(wall.IsGate), gate);
        return wall;
    }

    private static void Set(WallComponent wall, string property, object value) =>
        typeof(WallComponent).GetProperty(property)!.SetValue(wall, value);

    [Theory]
    [InlineData(1, false)]
    [InlineData(0.33, false)]
    [InlineData(0.32999, true)]
    [InlineData(0.01, true)]
    [InlineData(0, true)]
    public void FireStartsStrictlyBelowThirtyThreePercent(double health, bool burning)
    {
        var wall = Wall(health);
        Assert.Equal(burning, WallDamageLayout.IsBurning(wall));
        Set(wall, nameof(wall.Health), 66d);
        Assert.False(WallDamageLayout.IsBurning(wall));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DamageDeterministicallyRemovesStandingVolumeAndAddsRubble(bool tower, bool gate)
    {
        float volume = float.PositiveInfinity, height = float.PositiveInfinity;
        int rubble = 0, merlons = int.MaxValue;
        foreach (double health in new[] { 1, 0.875, 0.75, 0.5, 0.33, 0.32, 0.125, 0.001, 0 })
        {
            var wall = Wall(health, tower, gate);
            var parts = WallDamageLayout.Fragments(wall);
            Assert.Equal(parts, WallDamageLayout.Fragments(wall));
            Assert.All(parts, part =>
            {
                Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
                Assert.True(part.Center.Y - part.Size.Y / 2 >= -0.00001);
            });
            float next = parts.Where(p => p.Kind is WallFragmentKind.Body or WallFragmentKind.Door)
                .Sum(p => p.Size.X * p.Size.Y * p.Size.Z);
            Assert.True(next <= volume + 0.00001);
            Assert.True(WallDamageLayout.TopHeight(wall) <= height + 0.00001);
            Assert.True(parts.Count(p => p.Kind == WallFragmentKind.Rubble) >= rubble);
            Assert.True(parts.Count(p => p.Kind == WallFragmentKind.Merlon) <= merlons);
            volume = next;
            height = WallDamageLayout.TopHeight(wall);
            rubble = parts.Count(p => p.Kind == WallFragmentKind.Rubble);
            merlons = parts.Count(p => p.Kind == WallFragmentKind.Merlon);
        }
        Assert.Equal(0, volume);
        Assert.Equal(0.2f, height);
    }

    [Fact]
    public void RepairRestoresOriginalShapeAndExtinguishesFlames()
    {
        var wall = Wall(1, tower: true);
        var healthy = WallDamageLayout.Fragments(wall);
        Set(wall, nameof(wall.Health), 20d);
        Assert.True(WallDamageLayout.IsBurning(wall));
        Assert.NotEqual(healthy.Count, WallDamageLayout.Fragments(wall).Count);
        Set(wall, nameof(wall.Health), wall.MaxHealth);
        Assert.False(WallDamageLayout.IsBurning(wall));
        Assert.Equal(healthy, WallDamageLayout.Fragments(wall));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EveryFlameIsSupportedByAnExposedSurvivingSurface(bool tower, bool gate)
    {
        foreach (double health in new[] { 0.32999, 0.25, 0.125, 0.001, 0 })
            foreach (var defense in Enum.GetValues<WallDefenseKind>())
            {
                var wall = Wall(health, tower, gate);
                Set(wall, nameof(wall.Defense), defense);
                var fragments = WallDamageLayout.Fragments(wall);
                var surfaces = WallDamageLayout.FlameSurfaces(wall);
                Assert.NotEmpty(surfaces);
                Assert.Equal(surfaces, WallDamageLayout.FlameSurfaces(wall));
                foreach (var surface in surfaces)
                {
                    Assert.Contains(fragments, part =>
                        Math.Abs(part.Center.Y + part.Size.Y / 2 - surface.Position.Y) < 0.00001f &&
                        Math.Abs(part.Center.X - surface.Position.X) + surface.Width / 2 <= part.Size.X / 2 + 0.00001f &&
                        Math.Abs(part.Center.Z - surface.Position.Z) + surface.Depth / 2 <= part.Size.Z / 2 + 0.00001f);
                    Assert.DoesNotContain(fragments, part =>
                        part.Center.Y + part.Size.Y / 2 > surface.Position.Y + 0.001f &&
                        Math.Abs(part.Center.X - surface.Position.X) < (part.Size.X + surface.Width) / 2 &&
                        Math.Abs(part.Center.Z - surface.Position.Z) < (part.Size.Z + surface.Depth) / 2);
                    for (int tick = 0; tick < 60; tick++)
                    {
                        var parts = WallDamageLayout.FlameParts(surface, tick / 12d, 17);
                        var root = parts[0];
                        Assert.Equal(surface.Position.Y, root.Center.Y - root.Size.Y / 2, precision: 5);
                        Assert.Equal(surface.Position.X, root.Center.X);
                        Assert.Equal(surface.Position.Z, root.Center.Z);
                        Assert.True(root.Size.X <= surface.Width && root.Size.Z <= surface.Depth);
                        for (int i = 1; i < parts.Count; i++)
                        {
                            var lower = parts[i - 1];
                            var upper = parts[i];
                            Assert.True(upper.Center.Y - upper.Size.Y / 2 <= lower.Center.Y + lower.Size.Y / 2);
                            Assert.True(Math.Abs(upper.Center.X - lower.Center.X) < (upper.Size.X + lower.Size.X) / 2);
                            Assert.True(Math.Abs(upper.Center.Z - lower.Center.Z) < (upper.Size.Z + lower.Size.Z) / 2);
                        }
                    }
                }
            }
    }

    [Fact]
    public void FlickerChangesShapeAndColorWithoutMovingItsRoot()
    {
        var surface = WallDamageLayout.FlameSurfaces(Wall(0.2, tower: true))[0];
        var samples = Enumerable.Range(0, 60)
            .Select(tick => WallDamageLayout.FlameParts(surface, tick / 12d, 19)[0]).ToArray();
        Assert.True(samples.Select(part => part.Size.X).Distinct().Count() >= 3);
        Assert.True(samples.Select(part => part.Size.Y).Distinct().Count() >= 5);
        Assert.True(samples.Select(part => part.Color).Distinct().Count() > 1);
        Assert.Equal(WallDamageLayout.FlameParts(surface, 0, 19), WallDamageLayout.FlameParts(surface, 0.01, 19));
        Assert.Empty(WallDamageLayout.FlameSurfaces(Wall(0.33)));
    }

    [Fact]
    public void BucketCrewsPourInAlternatingCyclesAndSwitchToToolsWhenExtinguished()
    {
        var wall = Wall(0.2);
        Assert.True(RepairCrewArtwork.FightingFire(wall));
        Set(wall, nameof(wall.Health), wall.MaxHealth * WallDamageLayout.FireThreshold);
        Assert.False(RepairCrewArtwork.FightingFire(wall));
        var pouring = RepairCrewArtwork.BucketPose(0.5, 0);
        Assert.True(pouring.Lift > 0.1f && pouring.Tilt > 1);
        Assert.InRange(pouring.Pour, 0, 1);
        Assert.Equal(-1, RepairCrewArtwork.BucketPose(0.5, 1).Pour);
        Assert.Equal(-1, RepairCrewArtwork.BucketPose(0.1, 0).Pour);
        Assert.Equal(RepairCrewArtwork.BucketPose(0, 0, false), RepairCrewArtwork.BucketPose(0.7, 0, false));
        Assert.Equal(-1, RepairCrewArtwork.BucketPose(0.5, 0, false).Pour);
        Assert.True(RepairCrewArtwork.BucketWidth < BuildingDetailLayout.CottageDoorWidth);
    }

    [Fact]
    public void ThrownWaterLeavesTheBucketAsOneConnectedSheetAndReachesTheFire()
    {
        var lip = new System.Numerics.Vector3(1, 3, 2);
        var target = new System.Numerics.Vector3(2, 1, 4);
        var early = RepairCrewArtwork.BucketWater(lip, target, 0.2f);
        var middle = RepairCrewArtwork.BucketWater(lip, target, 0.4f);
        var late = RepairCrewArtwork.BucketWater(lip, target, 0.8f);
        Assert.Equal(lip, early[0].Start);
        Assert.Equal(lip, middle[0].Start);
        Assert.NotEqual(lip, late[0].Start);
        Assert.NotEqual(target, early[^1].End);
        Assert.True(middle[^1].End.Z > early[^1].End.Z);
        Assert.Equal(target, late[^1].End);
        foreach (var sheet in new[] { early, middle, late })
        {
            Assert.Equal(12, sheet.Count);
            for (int i = 0; i < sheet.Count; i++)
            {
                Assert.InRange(sheet[i].Width, 0.059f, 0.146f);
                Assert.True(sheet[i].End.Z > sheet[i].Start.Z);
                if (i > 0) Assert.Equal(sheet[i - 1].End, sheet[i].Start);
            }
        }
        Assert.Empty(RepairCrewArtwork.BucketWater(lip, target, 0));
        Assert.Empty(RepairCrewArtwork.BucketWater(lip, target, 1));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void InvalidThrowProgressIsRejected(float progress) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairCrewArtwork.BucketWater(
            System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, progress));

    [Fact]
    public void WorkersFitBuildingDoorScaleAndCarryDistinctTools()
    {
        Assert.True(RepairCrewArtwork.WorkerWidth < BuildingDetailLayout.CottageDoorWidth);
        Assert.InRange(RepairCrewArtwork.WorkerHeight, 0.3f, 0.4f);
        Assert.True(RepairCrewArtwork.BoardLength > RepairCrewArtwork.WorkerHeight);
        Assert.InRange(RepairCrewArtwork.HammerHandleLength, 0.1f, 0.2f);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(201)]
    public void InvalidHealthIsRejectedExplicitly(double health)
    {
        var wall = Wall(1);
        Set(wall, nameof(wall.Health), health);
        Assert.Throws<ArgumentException>(() => WallDamageLayout.Fragments(wall));
    }
}
