using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DebugSiegeControlsTests
{
    private static GameSession Siege(bool enabled = true)
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        Pos target = SettlementDebugSetup.Apply(game, siege: true, gateSide: BaileyGateSide.Bottom);
        return new(game, initialCameraTarget: target, debugSiegeMode: enabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(120)]
    public void ForcedWavesReplaceAttackersWithExactSizesWithoutChangingTheCity(int size)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        var game = session.Game;
        var state = game.Settlement!;
        int wave = state.WaveNumber, money = game.Money, mapVersion = game.MapVersion, population = game.Stats.Population;
        ulong random = game.Rng.State;
        int firstId = state.Goblins.Max(g => g.Id);
        Assert.True(session.DebugStartWave(size).Success);
        Assert.Equal(size, state.Goblins.Count);
        Assert.Equal(wave + 1, state.WaveNumber);
        Assert.Equal(1, state.NextWaveFraction);
        Assert.Equal(money, game.Money);
        Assert.Equal(population, game.Stats.Population);
        Assert.Equal(mapVersion, game.MapVersion);
        Assert.Equal(random, game.Rng.State);
        Assert.All(state.Goblins, goblin =>
        {
            Assert.True(goblin.Id > firstId);
            Assert.True(game.Map.TerrainAt((int)goblin.X, (int)goblin.Y).Buildable);
        });
        Assert.Empty(state.Projectiles);
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    public void InvalidSizesFailWithoutAnyStateChanges(int size)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        string saved = SaveGameStore.Serialize(session.Game);
        Assert.False(session.DebugStartWave(size).Success);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Equal(saved, SaveGameStore.Serialize(session.Game));
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void ForcedWaveWithoutAnExteriorRouteFailsAtomically()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        var game = session.Game;
        Assert.True(session.DebugClearGoblins().Success);
        var bounds = game.Settlement!.Bounds;
        var water = game.Map.Content.Terrains.Get("Water");
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
                if (!bounds.Contains(new(x, y))) game.Map.SetTerrain(x, y, water);
        game.Touch();
        string saved = SaveGameStore.Serialize(game);
        Assert.False(session.DebugStartWave(30).Success);
        Assert.Contains("dry land", session.Message);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void ClearingAttackersAndFreeRepairsPreserveBuildingsMoneyAndWaveNumber()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        var game = session.Game;
        var state = game.Settlement!;
        game.Update(0.5);
        int money = game.Money, wave = state.WaveNumber, mapVersion = game.MapVersion;
        var buildings = game.Map.BuildingFootprints.ToArray();
        foreach (var wall in state.Walls) wall.Health = 0;
        Assert.True(session.DebugRepairBuildings().Success);
        Assert.All(state.Walls, wall => Assert.Equal(wall.MaxHealth, wall.Health));
        Assert.Equal(money, game.Money);
        Assert.Equal(buildings, game.Map.BuildingFootprints.ToArray());
        Assert.True(session.DebugClearGoblins().Success);
        Assert.Empty(state.Goblins);
        Assert.Empty(state.Projectiles);
        Assert.Equal(wave, state.WaveNumber);
        Assert.Equal(1, state.NextWaveFraction);
        Assert.Equal(mapVersion, game.MapVersion);
        Assert.Equal(money, game.Money);
        Assert.True(session.DebugClearGoblins().Success);
    }

    [Fact]
    public void NormalSessionsAndReleaseBuildsCannotUseDebugActions()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        var session = new GameSession(game);
        string saved = SaveGameStore.Serialize(game);
        Assert.False(session.DebugStartWave(10).Success);
        Assert.False(session.DebugClearGoblins().Success);
        Assert.False(session.DebugRepairBuildings().Success);
        session.RequestDebugSiegeRestart();
        Assert.Null(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
        if (!CityGame.DebugToolsAvailable)
        {
            Assert.Throws<InvalidOperationException>(() => new GameSession(game, debugSiegeMode: true));
            Assert.False(game.DebugStartSiegeWave(10).Success);
            Assert.False(game.DebugRemoveGoblins().Success);
            Assert.False(game.DebugRepairAllBuildings().Success);
        }
        else
        {
            var ordinary = Siege(enabled: false);
            saved = SaveGameStore.Serialize(ordinary.Game);
            Assert.False(ordinary.DebugStartWave(10).Success);
            Assert.False(ordinary.DebugClearGoblins().Success);
            Assert.False(ordinary.DebugRepairBuildings().Success);
            ordinary.RequestDebugSiegeRestart();
            Assert.Null(ordinary.Prompt);
            Assert.Equal(saved, SaveGameStore.Serialize(ordinary.Game));
        }
    }

    [Fact]
    public void FailedPresetRestartReportsTheErrorAndPreservesTheCurrentGame()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42, StartingMoney = 1 });
        var session = new GameSession(game, debugSiegeMode: true);
        string saved = SaveGameStore.Serialize(game);
        session.RequestDebugSiegeRestart();
        session.SelectPrompt(1);
        Assert.Same(game, session.Game);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Contains("restart failed", session.Message);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void DebugActionsUseTheExistingPausedUndoSnapshots()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        session.Game.Paused = true;
        Assert.True(session.DebugClearGoblins().Success);
        Assert.Empty(session.Game.Settlement!.Goblins);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.True(session.DebugSiegeMode);
        Assert.Equal(24, session.Game.Settlement!.Goblins.Count);
        Assert.Equal(1, session.Game.Settlement.WaveNumber);
        Assert.True(session.Game.Paused);
    }

    [Fact]
    public void RestartIsGuardedAndRestoresTheSameSeedSiegePresetWithoutUndoOrCameraHighlights()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var session = Siege();
        var original = session.Game;
        string fresh = SaveGameStore.Serialize(original);
        Assert.True(session.DebugStartWave(70).Success);
        original.Settlement!.Walls[0].Health = 1;
        session.RequestDebugSiegeRestart();
        Assert.Equal("Restart debug siege", session.Prompt!.Title);
        session.SelectPrompt(2);
        Assert.Same(original, session.Game);
        session.RequestNewCity(restart: true);
        session.SelectPrompt(1);
        Assert.NotSame(original, session.Game);
        Assert.True(session.DebugSiegeMode);
        Assert.Equal(fresh, SaveGameStore.Serialize(session.Game));
        Assert.Equal(0, session.UndoCount);
        Assert.Null(session.Prompt);
        Assert.Null(session.Preview);
        Assert.False(session.HasCursor);
        Assert.False(session.GuideVisible);
        Assert.False(session.ClockSuspended);
        Assert.Equal(-1, session.ZoomLevel);
    }
}
