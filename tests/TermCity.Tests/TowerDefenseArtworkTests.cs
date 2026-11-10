using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class TowerDefenseArtworkTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void GeneratedTowersAddOneOuterGapStationOnEachAdjacentFace(int facing)
    {
        var tower = WallDamageLayout.PreviewTower(new(20.5f, 20.5f), WallMaterial.Wood);
        var stations = TowerDefenseArtwork.Stations(tower, facing);
        var forward = new Vector2(-tower.Tangent.Y, tower.Tangent.X) * facing;
        var right = new Vector2(forward.Y, -forward.X);
        Assert.Equal(4, stations.Count);
        Assert.All(stations.Take(2), station => Assert.Equal(forward, station.Forward));
        Assert.Contains(stations.Skip(2), station => station.Forward == right);
        Assert.Contains(stations.Skip(2), station => station.Forward == -right);
        Assert.All(stations.Skip(2), station => Assert.True(Vector2.Dot(station.Position, forward) > .7f));
        foreach (var kind in new[] { WallDefenseKind.OilCauldron, WallDefenseKind.Rocks })
        {
            var footprints = DropAttackLayout.Footprints(tower, kind, facing);
            Assert.Equal(stations.Count, footprints.Count);
            for (int i = 0; i < stations.Count; i++)
                Assert.Equal(stations[i].Forward, footprints[i].Forward);
        }
        Assert.True(stations.Count <= TowerDefenseArtwork.MaximumStations);
    }

    [Fact]
    public void StationsUseExteriorMerlonGapsAndHaveConnectedAccessSteps()
    {
        var siege = new SettlementState(new CellRect(10, 10, 48, 48));
        foreach (var wall in siege.Walls.Where(w => w.IsTower))
        {
            var stations = TowerDefenseArtwork.Stations(wall);
            bool corner = (wall.Cell.X == siege.Bounds.X || wall.Cell.X == siege.Bounds.Right) &&
                (wall.Cell.Y == siege.Bounds.Y || wall.Cell.Y == siege.Bounds.Bottom);
            Assert.Equal(corner ? 5 : 4, stations.Count);
            foreach (var station in stations)
            {
                var right = new Vector2(station.Forward.Y, -station.Forward.X);
                Assert.InRange(Math.Abs(Vector2.Dot(station.Position, right)), .7749f, .7751f);
                foreach (float merlon in new[] { -1.55f, 0, 1.55f })
                    Assert.True(Math.Abs(Vector2.Dot(station.Position, right) - merlon) > .25f + .37f);
                var lip = TowerDefenseArtwork.Lip(station, WorldScale.Feet(3), 1.05f);
                Assert.True(Vector2.Dot(new(lip.X, lip.Z), station.Forward) > WorldScale.TowerWidth / 2);
            }
            foreach (var kind in new[] { WallDefenseKind.OilCauldron, WallDefenseKind.Rocks })
            {
                var parts = TowerDefenseArtwork.Parts(wall, kind, WorldScale.Feet(3));
                var platforms = parts.Where(part => part.Color == 0x79634b && Math.Abs(part.Size.Y - .08f) < .00001f)
                    .Select(part => (part.Center, Size: Vector3.Abs(Vector3.Transform(Vector3.UnitX * part.Size.X, part.Rotation)) +
                        Vector3.Abs(Vector3.Transform(Vector3.UnitY * part.Size.Y, part.Rotation)) +
                        Vector3.Abs(Vector3.Transform(Vector3.UnitZ * part.Size.Z, part.Rotation)))).ToArray();
                for (int a = 0; a < platforms.Length; a++)
                    for (int b = a + 1; b < platforms.Length; b++)
                        Assert.False(Math.Abs(platforms[a].Center.X - platforms[b].Center.X) <
                                (platforms[a].Size.X + platforms[b].Size.X) / 2 - .00001f &&
                            Math.Abs(platforms[a].Center.Z - platforms[b].Center.Z) <
                                (platforms[a].Size.Z + platforms[b].Size.Z) / 2 - .00001f);
                var steps = parts.Where(part => part.Color == 0xa69666).OrderBy(part => part.Size.Y).ToArray();
                Assert.Equal(TowerDefenseArtwork.StairCount, steps.Length);
                Assert.Equal(WorldScale.Feet(3), steps[^1].Center.Y + steps[^1].Size.Y / 2, 5);
                Assert.All(parts, part => Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0));
                for (int step = 0; step < steps.Length; step++)
                    Assert.Equal(WorldScale.Feet(3) * (step + 1) / steps.Length,
                        steps[step].Center.Y + steps[step].Size.Y / 2, 5);
            }
        }
    }

    [Fact]
    public void CauldronsRotateAroundTheirFrontHingeAndReturnUpright()
    {
        var part = TowerDefenseArtwork.Parts(null, WallDefenseKind.OilCauldron, WorldScale.Feet(3))
            .First(part => part.Moving);
        float angle = TowerDefenseArtwork.TipAngle(.4);
        Assert.True(angle > 1);
        var tipped = TowerDefenseArtwork.Pose(part, angle);
        Assert.NotEqual(part.Center, tipped.Center);
        Assert.Equal(Vector3.Distance(part.Center, part.Pivot), Vector3.Distance(tipped.Center, part.Pivot), 5);
        Assert.Equal(0, TowerDefenseArtwork.TipAngle(0));
        Assert.Equal(0, TowerDefenseArtwork.TipAngle(
            TowerDefenseArtwork.WindupSeconds + TowerDefenseArtwork.PourSeconds + .4));
        Assert.Equal(part, TowerDefenseArtwork.Pose(part, 0));
    }

    [Fact]
    public void OilFormsAConnectedFallingSheetOutsideTheWallAndReachesTheSplashCenter()
    {
        var station = TowerDefenseArtwork.Stations(null)[0];
        var start = TowerDefenseArtwork.Lip(station, WorldScale.Feet(3), 1.05f) + Vector3.UnitY * 6;
        var end = new Vector3(start.X, .03f, -2.4f);
        float fall = TowerDefenseArtwork.FallSeconds(start.Y - end.Y);
        Assert.Empty(TowerDefenseArtwork.OilFlow(start, end, .1, fall));
        var early = TowerDefenseArtwork.OilFlow(start, end, TowerDefenseArtwork.WindupSeconds + .1, fall);
        Assert.Equal(start, early[0].Start);
        var landing = TowerDefenseArtwork.OilFlow(start, end, TowerDefenseArtwork.WindupSeconds + fall + .1, fall);
        Assert.Equal(end, landing[^1].End);
        for (int i = 0; i < landing.Count; i++)
        {
            Assert.True(landing[i].End.Y <= landing[i].Start.Y);
            Assert.True(landing[i].Start.Z < -WorldScale.TowerWidth / 2);
            Assert.True(landing[i].End.Z < -WorldScale.TowerWidth / 2);
            if (i > 0) Assert.Equal(landing[i - 1].End, landing[i].Start);
        }
        Assert.Empty(TowerDefenseArtwork.OilFlow(start, end,
            TowerDefenseArtwork.WindupSeconds + fall + TowerDefenseArtwork.PourSeconds + .01, fall));
        Assert.True(TowerDefenseArtwork.DropPoint(start, end, .6f).Y -
            TowerDefenseArtwork.DropPoint(start, end, .8f).Y >
            TowerDefenseArtwork.DropPoint(start, end, .2f).Y -
            TowerDefenseArtwork.DropPoint(start, end, .4f).Y);
    }

    [Fact]
    public void OilSheetHasAChangingBroadSilhouetteInsteadOfAStraightRod()
    {
        var start = new Vector3(0, 7, -2);
        var end = new Vector3(0, .03f, -2.6f);
        float fall = TowerDefenseArtwork.FallSeconds(start.Y - end.Y);
        double elapsed = TowerDefenseArtwork.WindupSeconds + fall + .05;
        var sheet = TowerDefenseArtwork.OilSheet(start, end, elapsed, fall);
        var later = TowerDefenseArtwork.OilSheet(start, end, elapsed + .13, fall);
        Assert.Equal(17, sheet.Count);
        Assert.Equal(start, sheet[0].Ridge);
        Assert.Equal(end, sheet[^1].Ridge);
        var widths = sheet.Select(s => Vector3.Distance(s.Left, s.Right)).ToArray();
        Assert.True(widths.Min() >= .25f);
        Assert.True(widths.Max() - widths.Min() > .15f);
        Assert.True(sheet[^1].Thickness < sheet[0].Thickness);
        Assert.Contains(sheet.Skip(1).SkipLast(1), section => Math.Abs(section.Ridge.X) > .01f);
        Assert.False(sheet.SequenceEqual(later));
        Assert.All(sheet, section =>
        {
            Assert.True(float.IsFinite(section.Left.X) && float.IsFinite(section.Ridge.Y) && float.IsFinite(section.Right.Z));
            Assert.True(section.Thickness > 0);
        });
        Assert.Empty(TowerDefenseArtwork.OilSheet(start, end, .1, fall));
        var fallingHead = TowerDefenseArtwork.OilSheet(start, end, .8, fall);
        Assert.True(Vector3.Distance(fallingHead[^1].Left, fallingHead[^1].Right) <
            Vector3.Distance(fallingHead[^2].Left, fallingHead[^2].Right));
    }

    [Fact]
    public void OilPoolSpreadsWithinItsFootprintWithConnectedNonoverlappingBands()
    {
        const float size = 1.5f;
        Assert.Empty(TowerDefenseArtwork.OilPool(size, -.01));
        Assert.Empty(TowerDefenseArtwork.OilPool(size, 0));
        var early = TowerDefenseArtwork.OilPool(size, .04);
        var full = TowerDefenseArtwork.OilPool(size, .18);
        Assert.True(early.Sum(p => p.Size.X * p.Size.Y) < full.Sum(p => p.Size.X * p.Size.Y));
        Assert.Equal(5, full.Count);
        Assert.True(full[0].Size.X < full[2].Size.X);
        Assert.Equal(size, full.Max(p => p.Size.X), 5);
        foreach (var patch in full)
        {
            Assert.True(Math.Abs(patch.Center.X) + patch.Size.X / 2 <= size / 2 + .00001f);
            Assert.True(Math.Abs(patch.Center.Y) + patch.Size.Y / 2 <= size / 2 + .00001f);
        }
        for (int i = 1; i < full.Count; i++)
            Assert.Equal(full[i - 1].Center.Y + full[i - 1].Size.Y / 2,
                full[i].Center.Y - full[i].Size.Y / 2, 5);
        Assert.Equal(full.ToArray(), TowerDefenseArtwork.OilPool(size, .18, false).ToArray());
        Assert.Equal(full.ToArray(), TowerDefenseArtwork.OilPool(size, 0, false).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => TowerDefenseArtwork.OilPool(float.NaN, 0));
    }

    [Fact]
    public void DroppedTracesHavePhysicalFallTimeWithoutChangingAttackDefinitions()
    {
        var oil = new SiegeProjectile { Kind = WallDefenseKind.OilCauldron, StartHeight = 7, DropHeight = 7 };
        var rocks = new SiegeProjectile { Kind = WallDefenseKind.Rocks, StartHeight = 7, DropHeight = 7 };
        Assert.True(oil.FlightSeconds > 2);
        Assert.True(rocks.FlightSeconds > 1.5);
        Assert.Equal(TowerDefenseArtwork.PourSeconds, oil.FlightSeconds - rocks.FlightSeconds, 5);
        Assert.Equal(180, WallDefenses.Get(WallDefenseKind.OilCauldron).Damage);
        Assert.Equal(10, WallDefenses.Get(WallDefenseKind.OilCauldron).ReloadSeconds);
        Assert.Equal(1.5f, DropAttackLayout.Size(WallDefenseKind.OilCauldron));
        Assert.Equal(140, WallDefenses.Get(WallDefenseKind.Rocks).Damage);
        Assert.Equal(7, WallDefenses.Get(WallDefenseKind.Rocks).ReloadSeconds);
        Assert.Equal(3.5, WallDefenses.Get(WallDefenseKind.Rocks).Range);
        Assert.Equal(1f, DropAttackLayout.Size(WallDefenseKind.Rocks));
    }
}
