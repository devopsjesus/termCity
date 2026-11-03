using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class MapArtworkTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RockFacetsAreDeterministicGroundedAndInsideExistingFeatureBounds(int variant)
    {
        var parts = RockArtwork.Parts(variant);
        Assert.Equal(parts, RockArtwork.Parts(variant));
        Assert.All(parts, part =>
        {
            Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
            Assert.True(Math.Abs(part.Center.X) + part.Size.X / 2 <= .45f);
            Assert.True(Math.Abs(part.Center.Z) + part.Size.Z / 2 <= .45f);
            Assert.InRange(part.Center.Y - part.Size.Y / 2, -.00001f, .7f);
            Assert.InRange(part.Center.Y + part.Size.Y / 2, 0, .7f);
            Assert.True(MapMaterialArtwork.IsStoneColor(part.Color));
        });
    }

    [Theory]
    [InlineData(0x79634b)]
    [InlineData(0x967a56)]
    [InlineData(0xa4a599)]
    [InlineData(0x89877b)]
    public void MapMaterialsUseBroadOpaqueColorBlocksWithoutFineFlecks(int color)
    {
        var texture = MapMaterialArtwork.Texture(color);
        Assert.Equal(texture, MapMaterialArtwork.Texture(color));
        Assert.Equal(16 * 16 * 4, texture.Length);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4, patch = (y / 4 * 4 * 16 + x / 4 * 4) * 4;
                Assert.Equal(texture[patch..(patch + 4)], texture[i..(i + 4)]);
                Assert.Equal(255, texture[i + 3]);
            }
        Assert.Throws<ArgumentOutOfRangeException>(() => MapMaterialArtwork.Texture(0xff00ff));
    }

    [Theory]
    [InlineData(.45f, .45f, .65f, 1)]
    [InlineData(2.5f, 1.8f, .9f, 2)]
    [InlineData(.56f, .56f, 1.3f, 3)]
    public void SharedFacadeBodiesKeepActualRecessesWithoutGeneratingUnusedRoofs(float width, float depth, float height, int stories)
    {
        var look = new HouseAppearance(width, depth, height, .32f, 0xc6bf9c, 0x956b50,
            HouseStyle.TimberGable, false, false, false);
        var block = new HouseBlock(0, 0, width, depth, height, .32f, stories, false);
        var parts = DepthStudyArtwork.HouseParts(look, block, true, includeRoof: false);
        Assert.DoesNotContain(parts, part => part.Roof);
        Assert.Contains(parts, part => part.Color == 0x514637);
        foreach (var pane in parts.Where(part => part.Color is 0x514637 or 0x35494b))
            Assert.DoesNotContain(parts, part =>
            {
                if (part.Color != look.PlasterColor) return false;
                var overlap = (part.Size + pane.Size) / 2 - Vector3.Abs(part.Center - pane.Center);
                return overlap.X > .00001f && overlap.Y > .00001f && overlap.Z > .00001f;
            });
    }
}
