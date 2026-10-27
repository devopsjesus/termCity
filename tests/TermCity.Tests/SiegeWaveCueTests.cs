using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SiegeWaveCueTests
{
    [Fact]
    public void NaturalWavesSlowAndAnnounceInOrdinarySessionsAndReleaseBuilds()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 80, MapHeight = 72, StartingMoney = 100_000,
        }, rules: CityRules.Full);
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var session = new GameSession(game);
        int cues = 0;
        session.SiegeWaveStarted += () => cues++;
        game.Speed = GameSpeed.Fast;
        var cottage = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        int remaining = 100;
        foreach (var plot in game.VacantPlots(ZoneType.Residential))
        {
            if (remaining == 0) break;
            int people = Math.Min(remaining, cottage.Capacity);
            game.Map.SetBuildingFootprint(cottage, plot);
            game.Map.SetHousehold(plot.X, plot.Y, new((byte)people, 0, 0));
            remaining -= people;
        }
        Assert.Equal(0, remaining);
        game.Touch();
        Assert.False(session.DebugSiegeMode);
        Assert.Equal(1, cues);
        Assert.Equal(GameSpeed.Slow, game.Speed);
    }

    private static CityGame NewSiege()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        return game;
    }

    [Fact]
    public void FirstWaveAndEveryForcedWaveSwitchToOneTimesBeforeAnnouncing()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        game.Speed = GameSpeed.Fast;
        int cues = 0;
        game.SiegeWaveStarted += () => { Assert.Equal(GameSpeed.Slow, game.Speed); cues++; };
        SettlementDebugSetup.Apply(game, siege: true);
        Assert.Equal(1, cues);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        game.Speed = GameSpeed.Fast;
        Assert.True(game.DebugStartSiegeWave(40).Success);
        Assert.Equal(2, cues);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        Assert.False(game.DebugStartSiegeWave(0).Success);
        Assert.Equal(2, cues);
    }

    [Fact]
    public void AutomaticWaveSlowsRemainingFrameTimeInsteadOfAdvancingNewAttackersAtFastSpeed()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = NewSiege();
        game.Settlement!.GoblinList.Clear();
        game.Settlement.NextWaveSeconds = 0.1;
        game.Speed = GameSpeed.Fast;
        int cues = 0;
        game.SiegeWaveStarted += () => cues++;
        game.Update(0.5);
        Assert.Equal(2, game.Settlement.WaveNumber);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        Assert.Equal(1, cues);
        Assert.InRange(game.Settlement.NextWaveSeconds, 109.7, 110);
    }

    [Fact]
    public void SessionRebindsWaveCuesOnRestartButDoesNotReplayThemOnUndoOrLoad()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var old = NewSiege();
        var session = new GameSession(old, debugSiegeMode: true);
        int cues = 0;
        session.SiegeWaveStarted += () => cues++;
        old.Paused = true;
        Assert.True(session.DebugStartWave(10).Success);
        Assert.Equal(1, cues);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(1, cues);
        session.RequestDebugSiegeRestart();
        session.SelectPrompt(1);
        Assert.Equal(2, cues);
        old.DebugStartSiegeWave(10);
        Assert.Equal(2, cues);
        Assert.True(session.DebugStartWave(10).Success);
        Assert.Equal(3, cues);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        int restoredCues = 0;
        restored.SiegeWaveStarted += () => restoredCues++;
        restored.Update(0.1);
        Assert.Equal(0, restoredCues);
    }
}
