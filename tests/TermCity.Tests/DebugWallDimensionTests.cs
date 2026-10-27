using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DebugWallDimensionTests
{
    [Fact]
    public void SiegeDefaultsToExactlyTwentyFourByTwentyFour()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        Assert.Equal(24, game.Settlement!.Bounds.Width);
        Assert.Equal(24, game.Settlement.Bounds.Height);
        Assert.Equal(100, game.Stats.Population);
        Assert.Equal(24, game.Settlement.Goblins.Count);
    }

    [Theory]
    [InlineData(48, 24)]
    [InlineData(24, 48)]
    [InlineData(12, 12)]
    public void RequestedRectanglesUseTheirExactDimensions(int width, int height)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 100, MapHeight = 80, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        var focus = SettlementDebugSetup.Apply(game, siege: true, wallWidth: width, wallHeight: height);
        Assert.Equal(width, game.Settlement!.Bounds.Width);
        Assert.Equal(height, game.Settlement.Bounds.Height);
        Assert.Equal(2 * width + 2 * height - 4, game.Settlement.Walls.Count);
        Assert.True(game.Settlement.Bounds.Contains(focus));
        Assert.Equal(100, game.Stats.Population);
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Theory]
    [InlineData(11, 24)]
    [InlineData(24, 11)]
    [InlineData(161, 24)]
    [InlineData(24, 161)]
    [InlineData(160, 24)]
    [InlineData(24, 96)]
    public void InvalidDimensionsAreAtomicAndDoNotOfferAProgressDiscard(int width, int height)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        var session = new GameSession(game, debugSiegeMode: true);
        string saved = SaveGameStore.Serialize(game);
        session.RequestDebugSiegeRestart(width, height);
        Assert.Null(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void RequestedSizeNeverSilentlyFallsBackToASmallerBailey()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42, StartingMoney = 1 });
        string saved = SaveGameStore.Serialize(game);
        var error = Assert.Throws<InvalidOperationException>(() =>
            SettlementDebugSetup.Apply(game, siege: true, wallWidth: 24, wallHeight: 20));
        Assert.Contains("24x20", error.Message);
        Assert.Null(game.Settlement);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void ResizingIsGuardedAndTheSelectedRectangleSurvivesLaterRestarts()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        var session = new GameSession(game, debugSiegeMode: true);
        var original = game.Settlement!.Bounds;
        session.RequestDebugSiegeRestart(24, 20);
        Assert.Equal(original, game.Settlement.Bounds);
        session.SelectPrompt(2);
        Assert.Same(game, session.Game);
        session.RequestDebugSiegeRestart(24, 20);
        session.SelectPrompt(1);
        Assert.Equal(24, session.Game.Settlement!.Bounds.Width);
        Assert.Equal(20, session.Game.Settlement.Bounds.Height);
        Assert.Equal(100, session.Game.Stats.Population);
        Assert.Equal(1, session.Game.Settlement.WaveNumber);
        session.RequestDebugSiegeRestart();
        session.SelectPrompt(1);
        Assert.Equal(24, session.Game.Settlement!.Bounds.Width);
        Assert.Equal(20, session.Game.Settlement.Bounds.Height);
        session.RequestDebugSiegeRestart(wallWidth: 24);
        Assert.Null(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
    }
}
