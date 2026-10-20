using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WoodlotArtworkTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void FirewoodYardFitsLegacyAndCurrentPlotsWithNoTreeGeometry(int width, int depth)
    {
        var parts = WoodlotArtwork.Parts(width, depth);
        Assert.Equal(parts, WoodlotArtwork.Parts(width, depth));
        Assert.Contains(parts, part => part.Color == WoodlotArtwork.EarthColor);
        Assert.Contains(parts, part => part.Color == WoodlotArtwork.FenceColor);
        Assert.Contains(parts, part => part.Color == WoodlotArtwork.BarkColor);
        Assert.Contains(parts, part => part.Color == WoodlotArtwork.CutEndColor);
        int stacks = (width >= 2 ? 2 : 1) * (depth >= 2 ? 2 : 1);
        Assert.Equal(stacks * 9, parts.Count(part => part.Color is WoodlotArtwork.BarkColor or WoodlotArtwork.BarkShadeColor));
        Assert.Equal(stacks * 18, parts.Count(part =>
            part.Color is WoodlotArtwork.CutEndColor or WoodlotArtwork.CutEndShadeColor));
        Assert.All(parts, part =>
        {
            Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
            Assert.InRange(Math.Abs(part.Center.X) + part.Size.X / 2, 0, width / 2f);
            Assert.InRange(Math.Abs(part.Center.Z) + part.Size.Z / 2, 0, depth / 2f);
            Assert.InRange(part.Center.Y - part.Size.Y / 2, 0, 0.5f);
            Assert.InRange(part.Center.Y + part.Size.Y / 2, 0, 0.5f);
            Assert.False(TreeAppearance.IsFoliageColor(part.Color));
        });
    }

    [Fact]
    public void FirewoodSpriteContainsFencedStacksInsteadOfGreenTreeCanopies()
    {
        var content = new GameContent();
        var woodlot = content.Buildings.Get("Woodlot");
        var sprite = BuildingSprite.Render(woodlot, woodlot.Width, woodlot.Height);
        var colors = Enumerable.Range(0, sprite.Width * sprite.Height)
            .Where(pixel => sprite.Pixels[pixel * 4 + 3] == 255)
            .Select(pixel => sprite.Pixels[pixel * 4] << 16 |
                sprite.Pixels[pixel * 4 + 1] << 8 | sprite.Pixels[pixel * 4 + 2]).ToHashSet();
        Assert.Contains(WoodlotArtwork.CutEndColor, colors);
        Assert.Contains(WoodlotArtwork.BarkColor, colors);
        Assert.DoesNotContain(colors, TreeAppearance.IsFoliageColor);
        Assert.DoesNotContain(0x53694a, colors);
        Assert.Equal(2, woodlot.Width);
        Assert.Equal(2, woodlot.Height);
        Assert.Equal(300, woodlot.Capacity);
        Assert.Contains("fenced yard", woodlot.Description);
    }

    [Fact]
    public void FirewoodArtworkRejectsInvalidFootprints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodlotArtwork.Parts(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => WoodlotArtwork.Parts(2, -1));
    }
}
