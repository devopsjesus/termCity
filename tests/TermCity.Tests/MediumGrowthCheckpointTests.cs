using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class MediumGrowthCheckpointTests
{
    [Theory]
    [InlineData("--recent-region-test")]
    [InlineData("--minimap-traversal-test")]
    public void SnapshotBenchmarksRequireExplicitPathsAndRemainIsolated(string mode)
    {
        var options = GodotOptions.Parse([mode, "--load", "checkpoint.json"]);
        Assert.True(options.Load);
        Assert.Equal("checkpoint.json", options.LoadPath);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([mode, "--load"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([mode, "--load", "checkpoint.json", "--debug-siege"]));
    }

    [Fact]
    public void GeneratorOptionsAreSeparateFromRenderingAndLoadedCities()
    {
        var options = GodotOptions.Parse(["--size", "medium", "--generate-medium-checkpoints", "fixtures"]);
        Assert.Equal("fixtures", options.MediumCheckpointsPath);
        Assert.True(options.SmokeTest);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--generate-medium-checkpoints"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--size", "small", "--generate-medium-checkpoints", "fixtures"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--size", "medium", "--generate-medium-checkpoints",
            "fixtures", "--recent-region-test"]));
        Assert.Equal("checkpoint.json", GodotOptions.Parse(["--size", "medium", "--generate-medium-checkpoints",
            "fixtures", "--load", "checkpoint.json"]).LoadPath);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--size", "medium", "--generate-medium-checkpoints",
            "fixtures", "--load"]));
    }

    [Fact]
    public void ZoomAndSpeedControlsAreExplicitDiagnosticCases()
    {
        var options = GodotOptions.Parse(["--minimap-traversal-test", "--benchmark-zoom", "overview",
            "--benchmark-speed", "Fast", "--load", "checkpoint.json"]);
        Assert.Equal(-2, options.BenchmarkZoom);
        Assert.Equal(GameSpeed.Fast, options.BenchmarkSpeed);
        Assert.Equal(0, GodotOptions.Parse(["--recent-region-test", "--benchmark-zoom", "normal"]).BenchmarkZoom);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--benchmark-zoom", "overview"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--recent-region-test", "--benchmark-speed", "Slow"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--recent-region-test", "--benchmark-zoom", "bad"]));
    }

    [Fact]
    public void BoundedGrowthIsReproducibleWithoutChangingRulesOrPopulation()
    {
        string root = Path.Combine(Path.GetTempPath(), "termcity-medium-growth-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new GameConfig { MapWidth = 480, MapHeight = 480, Seed = 42 };
            var first = MediumGrowthCheckpoints.Generate(config, Path.Combine(root, "first"), maxDays: 14);
            var second = MediumGrowthCheckpoints.Generate(config, Path.Combine(root, "second"), maxDays: 14);
            Assert.False(first.Complete);
            Assert.Equal(14, first.Days);
            Assert.Equal(first.Checkpoints, second.Checkpoints);
            var beginning = SaveGameStore.Load(Path.Combine(root, "first", "beginning.json"));
            var achieved = SaveGameStore.Load(Path.Combine(root, "first", "achieved.json"));
            Assert.Equal(0, beginning.Stats.Population);
            Assert.NotNull(beginning.Settlement);
            Assert.Equal(config.StartingMoney, beginning.Config.StartingMoney);
            Assert.Equal(config.MigrationRatePerWeek, achieved.Config.MigrationRatePerWeek);
            Assert.Equal(config.GrowthRatePerWeek, achieved.Config.GrowthRatePerWeek);
            Assert.Equal(CityRules.Full, achieved.Config.Rules);
            Assert.True(achieved.Stats.Population > 0);
            Assert.Equal(achieved.Stats.Population, first.Checkpoints.Last().Population);
            Assert.Contains("Missing", first.Limit);
            var resumed = MediumGrowthCheckpoints.Generate(config, Path.Combine(root, "resumed"), maxDays: 7,
                resumePath: Path.Combine(root, "first", "achieved.json"));
            Assert.Equal(21, resumed.Days);
            Assert.Equal(achieved.Stats.Population, resumed.Checkpoints.First().Population);
            Assert.Throws<IOException>(() => MediumGrowthCheckpoints.Generate(config, Path.Combine(root, "first"), maxDays: 1));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
