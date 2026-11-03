using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class DepthStudyTests
{
    [Fact]
    public void ComparisonIsOptInAndIsolated()
    {
        Assert.False(GodotOptions.Parse([]).DepthComparison);
        if (!CityGame.DebugToolsAvailable) return;
        var options = GodotOptions.Parse(["--depth-comparison", "--capture", "/tmp/study.png"]);
        Assert.True(options.DepthComparison);
        Assert.False(options.SmokeTest);
        Assert.False(options.DepthComparisonSmokeTest);
        Assert.True(GodotOptions.Parse(["--depth-comparison-smoke-test"]).DepthComparisonSmokeTest);
        Assert.True(GodotOptions.Parse(["--perspective-performance-test", "--depth-performance-comparison"]).DepthPerformanceComparison);
        Assert.True(GodotOptions.Parse(["--perspective-performance-test", "--depth-performance-baseline"]).DepthPerformanceBaseline);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--depth-performance-comparison"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--depth-performance-baseline"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([
            "--perspective-performance-test", "--depth-performance-baseline", "--depth-performance-comparison"]));
        foreach (string conflict in new[] { "--load", "--smoke-test", "--debug-settlement", "--dump-map" })
            Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--depth-comparison", conflict]));
    }

    [Fact]
    public void NormalRenderingUsesCombinedLightingAndRelief()
    {
        Assert.Equal(DepthTreatment.Combined, DepthStudyArtwork.DefaultTreatment);
        Assert.True(DepthStudyArtwork.UsesLighting(DepthStudyArtwork.DefaultTreatment));
        Assert.True(DepthStudyArtwork.UsesRelief(DepthStudyArtwork.DefaultTreatment));
        Assert.False(GodotOptions.Parse([]).DepthPerformanceBaseline);
    }

    [Fact]
    public void OriginalWoodTowersDoNotReceiveStoneButtresses()
    {
        var wall = new WallComponent
        {
            Id = 1, Cell = new(10, 10), IsTower = true, Health = 100, MaxHealth = 100,
        };
        Assert.Null(wall.BuiltMaterial);
        Assert.Equal(WallMaterial.Wood, wall.Material);
        Assert.Equal(WallDamageLayout.Fragments(wall).Count, DepthStudyArtwork.WallFragments(wall, true).Count());
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(3, true, true)]
    public void TreatmentsSeparateLightingFromRelief(int treatment, bool lighting, bool relief)
    {
        Assert.Equal(lighting, DepthStudyArtwork.UsesLighting((DepthTreatment)treatment));
        Assert.Equal(relief, DepthStudyArtwork.UsesRelief((DepthTreatment)treatment));
    }

    [Fact]
    public void ReliefIsDeterministicAndKeepsRealOpeningsAndBlockRoofs()
    {
        var appearance = HouseAppearance.At(24, 24, 1);
        var block = appearance.BlocksOnPlot(1, 1, 1)[0];
        var parts = DepthStudyArtwork.HouseParts(appearance, block, true);
        Assert.Equal(parts, DepthStudyArtwork.HouseParts(appearance, block, true));
        Assert.All(parts, part => Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0));
        Assert.Contains(parts, part => part.Roof);
        Assert.Contains(parts, part => part.Color == 0x514637);
        var panes = parts.Where(part => part.Color is 0x35494b or 0x514637).ToArray();
        Assert.NotEmpty(panes);
        foreach (var pane in panes)
            Assert.DoesNotContain(parts, part => part.Color == appearance.PlasterColor &&
                Overlap(pane, part));
    }

    [Fact]
    public void WallReliefPreservesGateOpeningAndBaselineFragments()
    {
        var wall = WallDamageLayout.Preview(new(0, 0), new(6, 0), WallMaterial.Stone);
        Assert.Equal(WallDamageLayout.Fragments(wall), DepthStudyArtwork.WallFragments(wall, false));
        Assert.True(DepthStudyArtwork.WallFragments(wall, true).Count() > WallDamageLayout.Fragments(wall).Count);
        var gate = WallDamageLayout.Preview(new(0, 0), new(3, 0), WallMaterial.Stone, true);
        Assert.Equal(WallDamageLayout.Fragments(gate), DepthStudyArtwork.WallFragments(gate, true));
        wall.Health /= 2;
        Assert.Equal(WallDamageLayout.Fragments(wall).Count, DepthStudyArtwork.WallFragments(wall, true).Count());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    public void LargerReliefModelsKeepUsableOpeningsAndExactRoofCourseBounds(int level, int plotSize)
    {
        for (int seed = 0; seed < 6; seed++)
        {
            var appearance = HouseAppearance.At(seed, seed + 7, level);
            foreach (var block in appearance.BlocksOnPlot(plotSize, plotSize, level))
            {
                var parts = DepthStudyArtwork.HouseParts(appearance, block, true);
                Assert.All(parts, part => Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0));
                foreach (var pane in parts.Where(part => part.Color is 0x35494b or 0x514637))
                    Assert.DoesNotContain(parts, part => part.Color == appearance.PlasterColor && Overlap(pane, part));
                for (int step = 0; step < block.RoofSteps; step++)
                {
                    var course = block.RoofCourse(step);
                    var roof = parts.Where(part => part.Roof && Math.Abs(part.Center.Y - course.Center.Y) < .00001f).ToArray();
                    Assert.NotEmpty(roof);
                    float width = roof.Max(part => part.Center.X + part.Size.X / 2) -
                        roof.Min(part => part.Center.X - part.Size.X / 2);
                    float depth = roof.Max(part => part.Center.Z + part.Size.Z / 2) -
                        roof.Min(part => part.Center.Z - part.Size.Z / 2);
                    Assert.Equal(course.Size.X * DepthStudyArtwork.RoofReliefScale, width, precision: 4);
                    Assert.Equal(course.Size.Z * DepthStudyArtwork.RoofReliefScale, depth, precision: 4);
                }
            }
        }
    }

    private static bool Overlap(HouseModelPart a, HouseModelPart b)
    {
        var overlap = (a.Size + b.Size) / 2 - Vector3.Abs(a.Center - b.Center);
        return overlap.X > .00001f && overlap.Y > .00001f && overlap.Z > .00001f;
    }
}
