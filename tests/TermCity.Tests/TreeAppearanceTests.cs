using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class TreeAppearanceTests
{
    [Fact]
    public void WoodlandContainsEveryShapeAndBothPinesDeterministically()
    {
        var trees = Enumerable.Range(0, 24).SelectMany(x => Enumerable.Range(0, 24).Select(y =>
        {
            var appearance = TreeAppearance.At(x, y);
            Assert.Equal(appearance, TreeAppearance.At(x, y));
            Assert.Equal(appearance.Parts(), TreeAppearance.At(x, y).Parts());
            return appearance;
        })).ToArray();
        Assert.Equal(Enum.GetValues<TreeShape>().Order(), trees.Select(tree => tree.Shape).Distinct().Order());
        Assert.InRange(trees.Count(tree => tree.IsPine), trees.Length / 4, trees.Length / 2);
        Assert.Equal(3, trees.Where(tree => !tree.IsPine).Select(tree => tree.Palette).Distinct().Count());
        Assert.True(trees.Select(tree => (tree.WidthScale, tree.HeightScale)).Distinct().Count() >= 20);
    }

    [Theory]
    [InlineData(TreeShape.Pine, 4)]
    [InlineData(TreeShape.TallPine, 5)]
    public void PinesHaveOverlappingTaperedTiers(TreeShape shape, int tiers)
    {
        var crown = new TreeAppearance(shape, 1, 1, 0).Parts().Skip(1).ToArray();
        Assert.Equal(tiers, crown.Length);
        for (int index = 1; index < crown.Length; index++)
        {
            Assert.True(crown[index].Size.X < crown[index - 1].Size.X);
            Assert.True(crown[index].Size.Z < crown[index - 1].Size.Z);
            Assert.True(crown[index].Center.Y > crown[index - 1].Center.Y);
            Assert.True(crown[index].Center.Y - crown[index].Size.Y / 2 <
                crown[index - 1].Center.Y + crown[index - 1].Size.Y / 2);
        }
    }

    [Fact]
    public void TreesStayWithinTheirCellAndUseBroadFoliageTextureClusters()
    {
        foreach (var shape in Enum.GetValues<TreeShape>())
            for (int palette = 0; palette < 3; palette++)
            {
                var parts = new TreeAppearance(shape, 1.1f, 1.1f, palette).Parts();
                Assert.Equal(TreeAppearance.TrunkColor, parts[0].Color);
                foreach (var part in parts)
                {
                    Assert.True(part.Size.X >= 0.1f && part.Size.Y >= 0.1f && part.Size.Z >= 0.1f);
                    Assert.True(Math.Abs(part.Center.X) + part.Size.X / 2 <= 0.52f);
                    Assert.True(Math.Abs(part.Center.Z) + part.Size.Z / 2 <= 0.52f);
                    Assert.InRange(part.Center.Y - part.Size.Y / 2, 0, 1.5f);
                    Assert.InRange(part.Center.Y + part.Size.Y / 2, 0, 1.6f);
                }
                foreach (int color in parts.Skip(1).Select(part => part.Color).Distinct())
                {
                    Assert.True(TreeAppearance.IsFoliageColor(color));
                    var texture = TerrainSprite.FoliageTexture(color);
                    for (int row = 0; row < 16; row++)
                        for (int column = 0; column < 16; column++)
                        {
                            int pixel = (row * 16 + column) * 4;
                            int cluster = (row / 4 * 4 * 16 + column / 4 * 4) * 4;
                            Assert.Equal(texture[cluster..(cluster + 4)], texture[pixel..(pixel + 4)]);
                            Assert.Equal(255, texture[pixel + 3]);
                        }
                }
            }
    }
}
