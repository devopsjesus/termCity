using TermCity.Core.Effects;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class BuildingArtTests
{
    [Fact]
    public void EveryLargerDefaultBuildingHasAsciiArtCoveringEveryCell()
    {
        var content = new GameContent();
        foreach (var type in content.Buildings.Where(type => type.Width * type.Height > 1))
        {
            Assert.Equal(type.Height * 2, type.FootprintArt.Count);
            Assert.All(type.FootprintArt, row =>
            {
                Assert.Equal(type.Width * 2, row.Length);
                Assert.All(row, glyph => Assert.InRange((int)glyph, 32, 126));
            });
            var footprint = new CellRect(10, 10, type.Width, type.Height);
            foreach (var p in footprint.Cells())
            {
                string tile = type.FootprintGlyphAt(footprint, p.X, p.Y);
                Assert.Equal(2, tile.Split('\n').Length);
                Assert.All(tile.Split('\n'), row => Assert.Equal(2, row.Length));
                Assert.Contains(tile, glyph => !char.IsWhiteSpace(glyph));
            }
        }
    }

    [Theory]
    [InlineData("Sheriff's Hall", "S")]
    [InlineData("Parish Church", "+")]
    [InlineData("Castle", "[]")]
    [InlineData("Aqueduct", "()")]
    [InlineData("Village Green", "T")]
    public void ArtworkHasBuildingSpecificFeatures(string name, string feature)
    {
        var type = new GameContent().Buildings.Get(name);
        Assert.Contains(feature, string.Join("\n", type.FootprintArt));
    }

    [Fact]
    public void ArtSurvivesSavingAndUsesCanonicalIconsWhenZoomedOut()
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get("Parish Church");
        var footprint = new CellRect(10, 17, 2, 2);
        game.Map.SetBuildingFootprint(type, footprint);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        foreach (var p in footprint.Cells())
        {
            Assert.Equal(type.FootprintGlyphAt(footprint, p.X, p.Y), CellRenderer.Render(loaded, p.X, p.Y).Glyph);
            Assert.Equal(type.Glyphs[0], CellRenderer.Render(loaded, p.X, p.Y, buildingArt: false).Glyph);
        }
        Assert.Equal(type.Glyphs[0], new BlockSampler(loaded).Sample(11, 18, 2).Glyph);
    }

    [Fact]
    public void DemolitionGhostsRetainEveryTileOfTheAsciiArt()
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get("Parish Church");
        var footprint = new CellRect(10, 17, 2, 2);
        game.Map.SetBuildingFootprint(type, footprint);
        var snapshot = new MapSnapshot();
        snapshot.Capture(game.Map);
        Assert.True(game.Demolish(new CellRect(11, 18, 1, 1)).Success);
        foreach (var p in footprint.Cells())
            Assert.Equal(type.FootprintGlyphAt(footprint, p.X, p.Y), snapshot.Describe(game.Map, p.X, p.Y).Glyph);
    }
}
