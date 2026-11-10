using System.Diagnostics;
using TermCity.Core.Simulation;
using Xunit.Abstractions;

namespace TermCity.Tests;

public class CameraSimulationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Right)]
    public void LiveTownUpdatesPreserveElapsedTimeWhileProfilingDayAndPatrolCosts(BaileyGateSide gate)
    {
        var preset = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, gate);
        var game = preset.Game;
        Assert.True(game.DebugRemoveGoblins().Success);
        game.ProfileUpdates = true;
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        game.Update(.1);
        _ = game.Stats;
        double initialDays = game.ElapsedDays;
        double worst = 0;
        for (int step = 0; step < 400; step++)
        {
            double day = game.ElapsedDays;
            double services = game.ServiceComputeMilliseconds;
            var watch = Stopwatch.StartNew();
            game.Update(.05);
            worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
            if (game.LastDayUpdateMilliseconds > 20 || game.LastSiegeUpdateMilliseconds > 50)
                output.WriteLine($"day={day:F2} update={watch.Elapsed.TotalMilliseconds:F1} " +
                    $"day-work={game.LastDayUpdateMilliseconds:F1} siege={game.LastSiegeUpdateMilliseconds:F1} " +
                    $"patrol={game.LastPatrolAssignmentMilliseconds:F1} wear={game.LastPathWearMilliseconds:F1} " +
                    $"repair={game.Map.LastTravelRepairMilliseconds:F1} fields={game.Map.LastRepairedFields} " +
                    $"compacted={game.Map.LastCompactedCells} services={game.ServiceComputeMilliseconds - services:F1} " +
                    $"tree={game.Map.LastRepairTreeMilliseconds:F1} scan={game.Map.LastRepairScanMilliseconds:F1} " +
                    $"flood={game.Map.LastRepairFloodMilliseconds:F1}/{game.Map.LastRepairFloodNodes} " +
                    $"population-week={game.LastPopulationWeekMilliseconds:F1} finish={game.LastWeekFinishMilliseconds:F1} " +
                    $"population={game.Stats.Population}");
        }
        output.WriteLine($"Worst live update={worst:F1}ms; the graphical camera benchmark enforces the 100ms frame budget.");
        Assert.Equal(20 / game.Config.MediumSecondsPerWeek * game.Config.DaysPerWeek,
            game.ElapsedDays - initialDays, 8);
    }
}
