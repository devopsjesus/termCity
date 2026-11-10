using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class OverviewTreeTests
{
    [Fact]
    public void OverviewTreesKeepAuthoredShapePaletteAndHeightWithFewerParts()
    {
        foreach (var shape in Enum.GetValues<TreeShape>())
        {
            var appearance = new TreeAppearance(shape, 1, 1, 0);
            var detailed = appearance.Parts();
            var overview = appearance.Parts(overview: true);
            Assert.True(overview.Count < detailed.Count);
            Assert.Equal(detailed[0], overview[0]);
            Assert.Equal(detailed.Max(part => part.Center.Y + part.Size.Y / 2),
                overview.Max(part => part.Center.Y + part.Size.Y / 2), 5);
            Assert.All(overview, part =>
            {
                Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
                Assert.True(float.IsFinite(part.Center.X) && float.IsFinite(part.Center.Y) && float.IsFinite(part.Center.Z));
                Assert.True(part.Color == TreeAppearance.TrunkColor || TreeAppearance.IsFoliageColor(part.Color));
            });
            Assert.Equal(detailed, appearance.Parts());
        }
    }
}
