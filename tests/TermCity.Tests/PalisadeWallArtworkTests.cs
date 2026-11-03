using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;

namespace TermCity.Tests;

public class PalisadeWallArtworkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TimberDefensesUseDeterministicLogsWithinTheirDamageFragments(bool tower, bool gate)
    {
        var wall = tower ? WallDamageLayout.PreviewTower(new(20, 20), WallMaterial.Wood)
            : WallDamageLayout.Preview(new(18, 20), new(22, 20), WallMaterial.Wood, gate);
        foreach (double health in new[] { 1, .9, .75, .5, .32, .1, 0 })
        {
            wall.Health = wall.MaxHealth * health;
            var source = WallDamageLayout.Fragments(wall);
            var parts = PalisadeArtwork.WallParts(wall, source);
            Assert.Equal(parts, PalisadeArtwork.WallParts(wall, source));
            Assert.NotEmpty(parts);
            foreach (var part in parts)
            {
                Assert.True(part.Fragment.Size.X > 0 && part.Fragment.Size.Y > 0 && part.Fragment.Size.Z > 0);
                Assert.Contains(source, parent => parent.Kind == part.Fragment.Kind && Contains(parent, part.Fragment));
            }
            foreach (var deck in source.Where(part => part.Kind == WallFragmentKind.Trim))
                Assert.Contains(parts, part => part.Fragment == deck);
            foreach (var fitting in source.Where(part => part.Kind is WallFragmentKind.DoorBrace or WallFragmentKind.DoorStud))
                Assert.Contains(parts, part => part.Fragment == fitting);
            if (health > .85)
            {
                Assert.True(parts.Select(part => part.Color).Where(PalisadeArtwork.IsBarkColor).Distinct().Count() >= 4);
                Assert.Contains(parts, part => part.Color == PalisadeArtwork.CutShadeColor);
                Assert.DoesNotContain(parts, part => part.Color is 0x79634b or 0xa69666);
            }
        }
    }

    [Fact]
    public void TowerCladdingDoesNotCreateSolidOrNestedInteriorSlabs()
    {
        var tower = WallDamageLayout.PreviewTower(new(20, 20), WallMaterial.Wood);
        float inner = WallDamageLayout.Width(tower) / 2 - PalisadeArtwork.TowerLogDepth - .01f;
        foreach (double fraction in new[] { 1d, .75, .5 })
        {
            tower.Health = tower.MaxHealth * fraction;
            var parts = PalisadeArtwork.WallParts(tower, WallDamageLayout.Fragments(tower));
            var trunks = parts.Where(part => part.Fragment.Kind == WallFragmentKind.Body &&
                part.Fragment.Size.Y > 1).ToArray();
            Assert.NotEmpty(trunks);
            Assert.All(trunks, part =>
                Assert.True(Math.Abs(part.Fragment.Center.X) >= inner ||
                    Math.Abs(part.Fragment.Center.Z) >= inner));
        }
    }

    [Fact]
    public void GratesAndWaterOpeningsAreNotFilledWithTimber()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int y in new[] { 19, 20 })
            foreach (int x in new[] { 20, 21 })
                map.SetTerrain(x, y, map.Content.Terrains.Get(DefaultTerrains.WaterName));
        var wall = WallDamageLayout.Preview(new(18, 20), new(23, 20), WallMaterial.Wood);
        var source = WallDamageLayout.Fragments(wall, map);
        var parts = PalisadeArtwork.WallParts(wall, source);
        var grates = source.Where(part => part.Kind == WallFragmentKind.Grate).ToArray();
        Assert.NotEmpty(grates);
        foreach (var grate in grates)
            Assert.Contains(parts, part => part.Fragment == grate && part.Color == 0x343b3e);
        foreach (var part in parts.Where(part => part.Fragment.Kind == WallFragmentKind.Body))
            Assert.Contains(source, parent => parent.Kind == WallFragmentKind.Body && Contains(parent, part.Fragment));
    }

    [Fact]
    public void FlamesUseExposedLogSurfacesAndRepairRestoresTheOriginalProfile()
    {
        var wall = WallDamageLayout.PreviewTower(new(20, 20), WallMaterial.Wood);
        var original = PalisadeArtwork.WallParts(wall, WallDamageLayout.Fragments(wall));
        wall.Health = wall.MaxHealth * .2;
        var burned = PalisadeArtwork.WallParts(wall, WallDamageLayout.Fragments(wall));
        Assert.Contains(burned, part => part.Color == PalisadeArtwork.CharredColor);
        var fragments = burned.Select(part => part.Fragment).ToArray();
        var flames = WallDamageLayout.FlameSurfaces(wall, fragments);
        Assert.NotEmpty(flames);
        foreach (var flame in flames)
            Assert.Contains(fragments, part =>
                Math.Abs(part.Center.Y + part.Size.Y / 2 - flame.Position.Y) < .00001f &&
                Math.Abs(part.Center.X - flame.Position.X) < part.Size.X / 2 &&
                Math.Abs(part.Center.Z - flame.Position.Z) < part.Size.Z / 2);
        wall.Health = wall.MaxHealth;
        Assert.Equal(original, PalisadeArtwork.WallParts(wall, WallDamageLayout.Fragments(wall)));
        var stone = WallDamageLayout.PreviewTower(new(20, 20), WallMaterial.Stone);
        Assert.Throws<ArgumentException>(() => PalisadeArtwork.WallParts(stone, WallDamageLayout.Fragments(stone)));
    }

    [Fact]
    public void SharedPanelsPreserveFlatSupportingTopsAndHaveRoughExposedCrowns()
    {
        var flat = PalisadeArtwork.Panel(3, .675f, 4, 42, roughTop: false);
        var rough = PalisadeArtwork.Panel(3, .675f, 4, 42, roughTop: true);
        Assert.Equal(flat, PalisadeArtwork.Panel(3, .675f, 4, 42, roughTop: false));
        Assert.All(flat.Where(part => part.Color is PalisadeArtwork.CutColor or PalisadeArtwork.CutShadeColor),
            part => Assert.Equal(4f, part.Center.Y + part.Size.Y / 2, precision: 4));
        Assert.True(rough.Where(part => part.Color is PalisadeArtwork.CutColor or PalisadeArtwork.CutShadeColor)
            .Select(part => part.Center.Y).Distinct().Count() >= 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => PalisadeArtwork.Panel(0, 1, 1, 0, true));
    }

    private static bool Contains(WallFragment outer, WallFragment inner)
    {
        var clearance = outer.Size / 2 - Vector3.Abs(inner.Center - outer.Center) - inner.Size / 2;
        return clearance.X >= -.00001f && clearance.Y >= -.00001f && clearance.Z >= -.00001f;
    }
}
