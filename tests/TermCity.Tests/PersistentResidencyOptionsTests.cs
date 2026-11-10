using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class PersistentResidencyOptionsTests
{
    [Fact]
    public void PersistentNativePoliciesRemainOptInAndIsolated()
    {
        Assert.False(GodotOptions.Parse([]).PersistentResidency);
        Assert.False(GodotOptions.Parse([]).PersistentResidencyTest);
        Assert.Equal(CacheExperiment.Current, GodotOptions.Parse([]).CacheExperiment);
        foreach (string mode in new[] { "PersistentTerrain", "PersistentScene" })
        {
            Assert.True(GodotOptions.Parse(["--persistent-residency-test", "--cache-experiment", mode]).PersistentResidencyTest);
            Assert.True(GodotOptions.Parse(["--recent-region-test", "--cache-experiment", mode,
                "--load", "checkpoint.json"]).RecentRegionTest);
            Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--cache-experiment", mode]));
        }
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency-test", "--load", "checkpoint.json"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency-test", "--minimap-traversal-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency-test", "--cache-experiment", "OverviewLod"]));
        if (CityGame.DebugToolsAvailable)
            Assert.True(GodotOptions.Parse(["--persistent-residency", "--load", "checkpoint.json"]).PersistentResidency);
        else Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency", "--recent-region-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--persistent-residency", "--overview-lod"]));
    }
}
