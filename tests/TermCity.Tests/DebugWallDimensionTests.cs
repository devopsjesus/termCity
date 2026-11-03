using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DebugWallDimensionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopDebugDefaultsUseExactlySixtyFourCellsWithoutShrinking(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 128, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        Assert.Equal(64, SettlementDebugSetup.DefaultWallSize);
        SettlementDebugSetup.Apply(game, siege,
            wallWidth: SettlementDebugSetup.DefaultWallSize, wallHeight: SettlementDebugSetup.DefaultWallSize);
        Assert.Equal(64, game.Settlement!.Bounds.Width);
        Assert.Equal(64, game.Settlement.Bounds.Height);
        Assert.Equal(siege ? 100 : 0, game.Stats.Population);
    }

    [Fact]
    public void SiegeDefaultsToACapacityCheckedRealisticSquare()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        Assert.InRange(game.Settlement!.Bounds.Width, 36, 64);
        Assert.Equal(game.Settlement.Bounds.Width, game.Settlement.Bounds.Height);
        Assert.Equal(100, game.Stats.Population);
        Assert.Equal(24, game.Settlement.Enemies.Count);
    }

    [Theory]
    [InlineData(64, 48)]
    [InlineData(48, 64)]
    [InlineData(48, 48)]
    public void RequestedRectanglesUseTheirExactDimensions(int width, int height)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 128, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        var focus = SettlementDebugSetup.Apply(game, siege: true, wallWidth: width, wallHeight: height);
        Assert.Equal(width, game.Settlement!.Bounds.Width);
        Assert.Equal(height, game.Settlement.Bounds.Height);
        Assert.Equal(2 * width + 2 * height - 4, game.Settlement.Walls.Count);
        Assert.True(game.Settlement.Bounds.Contains(focus));
        Assert.Equal(100, game.Stats.Population);
        foreach (string name in new[] { "Guard Post", "Archer Barracks" })
            Assert.Single(game.Map.BuildingFootprints.Values,
                area => game.Map.BuildingAt(area.X, area.Y)?.Name == name);
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTheSiegePresetPlacesEstablishedGarrisonsThatRecruitNormally(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 128, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        SettlementDebugSetup.Apply(game, siege);
        foreach (string name in new[] { "Guard Post", "Archer Barracks" })
        {
            var buildings = game.Map.BuildingFootprints.Values
                .Where(area => game.Map.BuildingAt(area.X, area.Y)?.Name == name).ToArray();
            if (!siege)
            {
                Assert.Empty(buildings);
                continue;
            }
            var footprint = Assert.Single(buildings);
            Assert.All(footprint.Cells(), cell => Assert.True(game.InsideSettlement(cell)));
            Assert.False(game.Map.BuildingEstablishedDays.ContainsKey(game.Map.Index(footprint.X, footprint.Y)));
        }
        if (!siege) return;
        Assert.True(game.DebugRemoveGoblins().Success);
        for (int n = 0; n < 80; n++) game.Settlement!.AdvanceDefenders(game, .1);
        Assert.Equal(4, game.Settlement!.Defenders.Count(unit => unit.Kind == DefenderKind.Soldier));
        Assert.Equal(4, game.Settlement.Defenders.Count(unit => unit.Kind == DefenderKind.Archer));
    }

    [Theory]
    [InlineData(11, 24)]
    [InlineData(24, 11)]
    [InlineData(161, 24)]
    [InlineData(24, 161)]
    [InlineData(160, 24)]
    [InlineData(24, 96)]
    [InlineData(158, 24)]
    [InlineData(24, 94)]
    public void InvalidDimensionsAreAtomicAndDoNotOfferAProgressDiscard(int width, int height)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42, MapWidth = 160, MapHeight = 96 });
        SettlementDebugSetup.Apply(game, siege: true);
        var session = new GameSession(game, debugSiegeMode: true);
        string saved = SaveGameStore.Serialize(game);
        session.RequestDebugSiegeRestart(width, height);
        Assert.Null(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void UndersizedSiegeEnclosuresDoNotOmitGarrisonsOrReduceThePopulation()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 128, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        var error = Assert.Throws<DebugSettlementSiteUnavailableException>(() =>
            SettlementDebugSetup.Apply(game, siege: true, wallWidth: 36, wallHeight: 36));
        Assert.Contains("100 residents with both garrisons", error.Message);
        Assert.Contains("larger walls", error.Message);
    }

    [Fact]
    public void RequestedSizeNeverSilentlyFallsBackToASmallerBailey()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42, MapWidth = 320, MapHeight = 192, StartingMoney = 1 });
        string saved = SaveGameStore.Serialize(game);
        var error = Assert.Throws<DebugSettlementSiteUnavailableException>(() =>
            SettlementDebugSetup.Apply(game, siege: true, wallWidth: 48, wallHeight: 48));
        Assert.Contains("48x48", error.Message);
        Assert.Null(game.Settlement);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void ResizingIsGuardedAndTheSelectedRectangleSurvivesLaterRestarts()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42, MapWidth = 320, MapHeight = 192 });
        SettlementDebugSetup.Apply(game, siege: true);
        var session = new GameSession(game, debugSiegeMode: true);
        var original = game.Settlement!.Bounds;
        session.RequestDebugSiegeRestart(48, 48);
        Assert.Equal(original, game.Settlement.Bounds);
        session.SelectPrompt(2);
        Assert.Same(game, session.Game);
        session.RequestDebugSiegeRestart(48, 48);
        session.SelectPrompt(1);
        Assert.Equal(48, session.Game.Settlement!.Bounds.Width);
        Assert.Equal(48, session.Game.Settlement.Bounds.Height);
        Assert.Equal(100, session.Game.Stats.Population);
        Assert.Equal(1, session.Game.Settlement.WaveNumber);
        session.RequestDebugSiegeRestart();
        session.SelectPrompt(1);
        Assert.Equal(48, session.Game.Settlement!.Bounds.Width);
        Assert.Equal(48, session.Game.Settlement.Bounds.Height);
        session.RequestDebugSiegeRestart(wallWidth: 24);
        Assert.Null(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
    }
}
