using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class TreeAppearanceTests
{
    [Fact]
    public void AdjacentCrownsDoNotRepeatCoplanarOverlappingTops()
    {
        int CountCollisions(bool quantized)
        {
            var trees = Enumerable.Range(0, 24).SelectMany(x => Enumerable.Range(0, 24).Select(y =>
            {
                int sx = x * 5 + 2, sy = y * 5 + 2;
                var look = TreeAppearance.At(sx, sy);
                if (quantized) look = look with
                {
                    WidthScale = .9f + CellHash.Pick(sx + 71, sy - 23, 5) * .05f,
                    HeightScale = .9f + CellHash.Pick(sx - 37, sy + 89, 5) * .05f,
                };
                return (X: x, Z: y, Parts: look.Parts().Where(part => TreeAppearance.IsFoliageColor(part.Color)).ToArray());
            })).ToArray();
            int collisions = 0;
            for (int a = 0; a < trees.Length; a++)
                for (int b = a + 1; b < trees.Length; b++)
                {
                    if (Math.Abs(trees[a].X - trees[b].X) > 3 || Math.Abs(trees[a].Z - trees[b].Z) > 3) continue;
                    foreach (var first in trees[a].Parts)
                        foreach (var second in trees[b].Parts)
                            if (Math.Abs(first.Center.Y + first.Size.Y / 2 - second.Center.Y - second.Size.Y / 2) < 0.00001f &&
                                Math.Abs(trees[a].X + first.Center.X - trees[b].X - second.Center.X) <
                                    (first.Size.X + second.Size.X) / 2 - 0.0001f &&
                                Math.Abs(trees[a].Z + first.Center.Z - trees[b].Z - second.Center.Z) <
                                    (first.Size.Z + second.Size.Z) / 2 - 0.0001f)
                                collisions++;
                }
            return collisions;
        }
        Assert.True(CountCollisions(quantized: true) > 0);
        Assert.Equal(0, CountCollisions(quantized: false));
    }

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
        var foliage = new TreeAppearance(shape, 1, 1, 0).Parts().Where(part => TreeAppearance.IsFoliageColor(part.Color)).ToArray();
        var crown = Enumerable.Range(0, tiers).Select(tier =>
            foliage.Single(part => Math.Abs(part.Center.Y - (.48f + tier * .19f) * 5) < .00001f)).ToArray();
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
    public void MatureTreesHavePhysicalCanopiesAndBroadFoliageTextureClusters()
    {
        foreach (var shape in Enum.GetValues<TreeShape>())
            for (int palette = 0; palette < 3; palette++)
            {
                var parts = new TreeAppearance(shape, 1.1f, 1.1f, palette).Parts();
                Assert.Equal(TreeAppearance.TrunkColor, parts[0].Color);
                foreach (var part in parts)
                {
                    Assert.True(part.Size.X >= 0.1f && part.Size.Y >= 0.1f && part.Size.Z >= 0.1f);
                    Assert.True(Math.Abs(part.Center.X) + part.Size.X / 2 <= 1.56f);
                    Assert.True(Math.Abs(part.Center.Z) + part.Size.Z / 2 <= 1.56f);
                    Assert.InRange(part.Center.Y - part.Size.Y / 2, 0, 7.5f);
                    Assert.InRange(part.Center.Y + part.Size.Y / 2, 0, 8f);
                }
                foreach (int color in parts.Where(part => TreeAppearance.IsFoliageColor(part.Color)).Select(part => part.Color).Distinct())
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
