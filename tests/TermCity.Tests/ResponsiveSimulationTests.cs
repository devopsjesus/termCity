using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class ResponsiveSimulationTests
{
    [Fact]
    public async Task QuittingDiscardsUnpublishedTimeWithoutAdvancingThePublishedCity()
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.Speed = GameSpeed.Fast;
        var session = new GameSession(game);
        for (int step = 0; step < 100; step++) session.UpdateResponsive(.02);
        game.Paused = true;
        string published = SaveGameStore.Serialize(game);
        Assert.True(session.PendingSimulationSeconds > 0);
        await session.StopForQuitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.BackgroundSimulationPending);
        Assert.Equal(0, session.PendingSimulationSeconds);
        Assert.Equal(published, SaveGameStore.Serialize(game));
    }

    [Fact]
    public async Task QuitStopsAWorkerBetweenAtomicSlicesAndSkipsTheRestOfItsBatch()
    {
        var state = TestCity.Flat();
        state.Day = state.Config.DaysPerWeek - 1;
        state.DayProgressSeconds = state.Config.SecondsPerWeek(GameSpeed.Fast) / state.Config.DaysPerWeek - .01;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        state.Changed += () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Quit fixture did not release its worker.");
        };
        await using var worker = new SimulationWorker();
        var work = worker.Advance(state, Enumerable.Repeat(new SimulationSlice(.02, GameSpeed.Fast), 16).ToArray(), null);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            worker.StopAfterCurrentSlice();
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.Processed);
        Assert.Null(result.Presentation);
    }

    [Fact]
    public async Task UnchangedCombatDoesNotConstructPathPresentation()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        await using var session = new GameSession(game);
        var roads = new RoadMeshLayout(game.Map, RoadCurves.Extract(game.Map, (_, _) => false));
        var request = new PathSceneRequest(new(0, 0, game.Map.Width, game.Map.Height), roads);
        for (int step = 0; step < 8; step++)
        {
            session.UpdateResponsive(.02, request);
            await Drain(session);
        }
        Assert.Equal(0, session.SimulationPathPreparations);
        Assert.Equal(1, session.SimulationStateCaptures);
    }

    [Fact]
    public async Task PersistentWorkerRetainsItsWorldAcrossCombatAndDayBoundaries()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        var serial = game.CreateSimulationSnapshot();
        var session = new GameSession(game);
        for (int step = 0; step < 100; step++)
        {
            serial.Update(.02);
            session.UpdateResponsive(.02);
            await Drain(session);
        }
        Assert.Equal(1, session.SimulationStateCaptures);
        Assert.True(session.CombatOnlyPublications > 80);
        Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(game));
        await session.StopSimulationAsync();
    }

    [Theory]
    [InlineData(GameSpeed.Slow)]
    [InlineData(GameSpeed.Medium)]
    [InlineData(GameSpeed.Fast)]
    public async Task SiegeTicksBeforeADayBoundaryUseTheWorkerAndRetryEdits(GameSpeed speed)
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        game.Speed = speed;
        game.DayProgressSeconds = 0;
        double elapsed = .1 * game.Config.SecondsPerWeek(speed) / game.Config.MediumSecondsPerWeek;
        var serial = game.CreateSimulationSnapshot();
        var session = new GameSession(game);
        Assert.False(game.DayUpdateDue(elapsed));
        Assert.True(game.SiegeUpdateDue(elapsed));
        session.UpdateResponsive(elapsed);
        Assert.True(session.BackgroundSimulationPending);
        Assert.True(game.DebugRepairAllBuildings().Success);
        Assert.True(serial.DebugRepairAllBuildings().Success);
        serial.Update(elapsed);
        await Drain(session);
        Assert.True(session.RetriedSimulationUpdates > 0);
        Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(game));
    }

    [Fact]
    public void SiegeSchedulingMatchesTheAccumulatorBoundaryAndPause()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        game.Settlement!.Accumulator = .09;
        Assert.False(game.SiegeUpdateDue(.009));
        Assert.True(game.SiegeUpdateDue(.01));
        Assert.False(game.SiegeUpdateDue(0));
        game.Paused = true;
        Assert.False(game.SiegeUpdateDue(.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.SiegeUpdateDue(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.SiegeUpdateDue(-1));
    }

    [Fact]
    public async Task SpeedChangesKeepTheirOwnElapsedSlicesWhileAnEarlierDayIsPending()
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        game.DayProgressSeconds = game.Config.SecondsPerWeek(GameSpeed.Medium) / game.Config.DaysPerWeek - .01;
        var serial = new GameSession(game.CreateSimulationSnapshot());
        var session = new GameSession(game);
        serial.Update(.02);
        session.UpdateResponsive(.02);
        serial.Game.Speed = game.Speed = GameSpeed.Fast;
        serial.Update(.1);
        session.UpdateResponsive(.1);
        serial.Game.Speed = game.Speed = GameSpeed.Slow;
        serial.Update(.15);
        session.UpdateResponsive(.15);
        await Drain(session);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        Assert.Equal(SaveGameStore.Serialize(serial.Game), SaveGameStore.Serialize(game));
    }

    [Fact]
    public async Task NewGameDoesNotInheritAnOldWorldsPendingStep()
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.DayProgressSeconds = game.Config.SecondsPerWeek(game.Speed) / game.Config.DaysPerWeek - .01;
        var session = new GameSession(game);
        session.UpdateResponsive(.02);
        Assert.True(session.BackgroundSimulationPending);
        session.NewGame(game.Config with { Seed = 99 });
        var replacement = session.Game;
        string before = SaveGameStore.Serialize(replacement);
        await Drain(session);
        Assert.Same(replacement, session.Game);
        Assert.Equal(before, SaveGameStore.Serialize(replacement));
    }

    [Fact]
    public async Task ABackgroundWavePublishesItsHornOnceAndSlowsRetainedElapsedSlices()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Settlement!.EnemyList.Clear();
        game.Settlement.WaveNumber = 0;
        game.Settlement.NextWaveSeconds = 120;
        game.Paused = false;
        game.Speed = GameSpeed.Fast;
        game.DayProgressSeconds = game.Config.SecondsPerWeek(GameSpeed.Fast) / game.Config.DaysPerWeek - .01;
        var serial = new GameSession(game.CreateSimulationSnapshot());
        var session = new GameSession(game);
        int horns = 0;
        session.SiegeWaveStarted += () => horns++;
        serial.Update(.02);
        session.UpdateResponsive(.02);
        for (int step = 0; step < 3; step++)
        {
            serial.Update(.05);
            session.UpdateResponsive(.05);
        }
        await Drain(session);
        Assert.Equal(1, horns);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        Assert.Equal(SaveGameStore.Serialize(serial.Game), SaveGameStore.Serialize(game));
    }

    [Theory]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Right)]
    public async Task FortifiedUpdatesPreserveSerialCombatPatrolAndWallIdentity(BaileyGateSide gate)
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, gate).Game;
        game.Paused = false;
        var serial = new GameSession(game.CreateSimulationSnapshot());
        var session = new GameSession(game);
        var state = game.Settlement;
        var wall = state!.Walls[0];
        for (int step = 0; step < 240; step++)
        {
            serial.Update(.05);
            session.UpdateResponsive(.05);
            if (session.BackgroundSimulationPending) await Drain(session);
        }
        await Drain(session);
        Assert.Same(state, game.Settlement);
        Assert.Same(wall, game.Settlement!.Walls[0]);
        Assert.Equal(SaveGameStore.Serialize(serial.Game), SaveGameStore.Serialize(game));
        Assert.Equal(serial.Game.Settlement!.Deaths, game.Settlement.Deaths);
        Assert.Equal(serial.Game.Settlement.Enemies.Select(enemy => (enemy.PreviousX, enemy.PreviousY, enemy.WalkDistance)),
            game.Settlement.Enemies.Select(enemy => (enemy.PreviousX, enemy.PreviousY, enemy.WalkDistance)));
        Assert.Equal(serial.Game.Settlement.Defenders.Select(unit => (unit.PreviousX, unit.PreviousY, unit.PathRetrySeconds)),
            game.Settlement.Defenders.Select(unit => (unit.PreviousX, unit.PreviousY, unit.PathRetrySeconds)));
    }

    [Theory]
    [InlineData(GameSpeed.Slow)]
    [InlineData(GameSpeed.Medium)]
    [InlineData(GameSpeed.Fast)]
    public async Task ResponsiveUpdatesRetainEveryElapsedSliceAndMatchSerialState(GameSpeed speed)
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.Speed = speed;
        var serial = new GameSession(game.CreateSimulationSnapshot());
        var responsive = new GameSession(game);
        for (int step = 0; step < 100; step++)
        {
            serial.Update(.05);
            responsive.UpdateResponsive(.05);
        }
        await Drain(responsive);
        Assert.Same(game, responsive.Game);
        Assert.Equal(SaveGameStore.Serialize(serial.Game), SaveGameStore.Serialize(responsive.Game));
        Assert.Equal(0, responsive.PendingSimulationSeconds);
    }

    [Fact]
    public async Task EditingDuringAnUpdateRetriesWithoutOverwritingTheActionOrDroppingTime()
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.DayProgressSeconds = game.Config.SecondsPerWeek(game.Speed) / game.Config.DaysPerWeek - .01;
        var serial = game.CreateSimulationSnapshot();
        var session = new GameSession(game);
        session.UpdateResponsive(.02);
        Assert.True(session.BackgroundSimulationPending);
        var area = CellRect.Single(new Pos(10, 10));
        Assert.True(game.BuildRoad(area).Success);
        Assert.True(serial.BuildRoad(area).Success);
        serial.Update(.02);
        await Drain(session);
        Assert.True(session.RetriedSimulationUpdates > 0);
        Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(game));
    }

    [Fact]
    public async Task PauseAndCameraChangesSurvivePublicationWithoutCancellingAnAlreadyStartedStep()
    {
        var game = TestCity.Flat();
        game.Paused = false;
        game.DayProgressSeconds = game.Config.SecondsPerWeek(game.Speed) / game.Config.DaysPerWeek - .01;
        var serial = game.CreateSimulationSnapshot();
        var session = new GameSession(game);
        session.UpdateResponsive(.02);
        Assert.True(session.BackgroundSimulationPending);
        game.Paused = true;
        session.UpdateResponsive(.5);
        session.ScrollCamera(3, 2);
        int x = session.CameraX, y = session.CameraY;
        serial.Update(.02);
        serial.Paused = true;
        serial.Update(.5);
        await Drain(session);
        Assert.Equal((x, y), (session.CameraX, session.CameraY));
        Assert.True(game.Paused);
        Assert.Equal(0, session.RetriedSimulationUpdates);
        Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(game));
    }

    private static async Task Drain(GameSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Responsive simulation did not publish its queued work.");
            await Task.Delay(1);
            session.UpdateResponsive(0);
        }
    }
}
