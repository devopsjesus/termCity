using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class TopScenarioTests
{
    [Fact]
    public void TopPresetAndPerformanceExperimentsRemainExplicit()
    {
        Assert.True(MapSize.TryParse("top", out var preset, out _));
        Assert.Equal(new MapSize(640, 640) { Scenario = CityScenario.Top }, preset);
        foreach (string experiment in new[] { "Tuned", "LargerBudgets", "RecentRegion", "RecentRegionLod", "RecentRegionPagedLod", "RecentRegionPagedLodLarge" })
            Assert.True(GodotOptions.Parse(["--size", "TOP", "--recent-region-test",
                "--cache-experiment", experiment]).RecentRegionTest);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--recent-region-test", "--minimap-traversal-test"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--cache-experiment", "RecentRegion"]));
        Assert.True(GodotOptions.Parse(["--size", "TOP", "--footpath-art-smoke-test"]).FootpathArtworkSmokeTest);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--size", "SF", "--footpath-art-smoke-test"]));
        Assert.Equal(CityScenario.Random, GodotOptions.Parse(["--size", "TOP", "--size", "large"]).Config.Scenario);
    }

    [Fact]
    public void TopIsAFullScale100KSettlementWithIntactDefensesAndRoundTrips()
    {
        var config = GodotOptions.Parse(["--size", "TOP", "--seed", "42"]).Config;
        Assert.Equal(CityScenario.Top, config.Scenario);
        var game = CityGame.New(config);
        Assert.Equal(640, game.Map.Width);
        Assert.Equal(640, game.Map.Height);
        Assert.Equal(100_000, game.Stats.Population);
        Assert.NotNull(game.Settlement);
        Assert.All(game.Settlement!.Walls, wall => Assert.Equal(wall.MaxHealth, wall.Health));
        Assert.Contains(game.Settlement.Walls, wall => wall.Defense != WallDefenseKind.None);
        Assert.Contains(game.Settlement.Walls, wall => wall.IsGate);
        Assert.Contains(game.Settlement.Walls, wall => wall.IsTower);
        Assert.Equal(64, game.Settlement.Defenders.Count);
        Assert.True(game.Settlement.WallNetwork.Contains(new System.Numerics.Vector2(320, 320)));
        Assert.True(game.Settlement.WallNetwork.Contains(new System.Numerics.Vector2(45, 45)));
        Assert.False(game.Settlement.WallNetwork.Contains(new System.Numerics.Vector2(4, 4)));
        Assert.All(game.Map.BuildingFootprints.Values, area =>
            Assert.True(TopSettlementScenario.TownBounds.Contains(new(area.X, area.Y))));
        Assert.True(game.Indicators.Happiness >= 65, $"TOP happiness is {game.Indicators.Happiness:0.0}, not content. " +
            $"Complaints: {string.Join(", ", game.Indicators.Complaints.Select(reason => $"{reason.Reason}={reason.Points:0.0}"))} " +
            $"Power={game.Services.Power.Ratio}, water={game.Services.Water.Ratio}, jobs={game.Indicators.Jobs}, workers={game.Indicators.Workers}");
        Assert.All(game.Map.Content.Buildings, building => Assert.Equal(5, building.ModelScale));
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(CityScenario.Top, loaded.Config.Scenario);
        Assert.Equal(game.Stats.Population, loaded.Stats.Population);
        Assert.Equal(game.Stats.Residential, loaded.Stats.Residential);
        Assert.Equal(game.Stats.Commercial, loaded.Stats.Commercial);
        Assert.Equal(game.Stats.Industrial, loaded.Stats.Industrial);
        Assert.Equal(game.Stats.WeeklyIncome, loaded.Stats.WeeklyIncome);
        Assert.Equal(game.Indicators.Happiness, loaded.Indicators.Happiness, 6);
        Assert.Equal(game.Settlement.Walls.Count, loaded.Settlement!.Walls.Count);
        Assert.Equal(game.Settlement.Defenders.Count, loaded.Settlement.Defenders.Count);
    }
}
