using System.Numerics;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class SiegeMotionTests
{
    [Fact]
    public void MovementAndGaitContinueBetweenCombatTicksWithoutChangingSavedState()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        game.Update(0.2);
        var siege = game.Settlement!;
        var goblin = siege.Enemies[0];
        var first = SiegeMotion.Enemy(siege, goblin);
        int revision = siege.Revision;
        game.Update(0.1);
        Assert.Equal(revision, siege.Revision);
        var middle = SiegeMotion.Enemy(siege, goblin);
        Assert.True(Vector2.Distance(first.Position, middle.Position) > 0.01f);
        Assert.NotEqual(first.Stride, middle.Stride);
        Assert.InRange(Vector2.Dot(middle.Forward, Vector2.Normalize(new Vector2(
            (float)(goblin.X - goblin.PreviousX), (float)(goblin.Y - goblin.PreviousY)))), 0.999f, 1.001f);
        game.Paused = true;
        game.Update(0.5);
        Assert.Equal(middle, SiegeMotion.Enemy(siege, goblin));
        string saved = SaveGameStore.Serialize(game);
        SiegeMotion.Enemy(siege, goblin);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void StationaryAttackersFaceTheirActualTargetWallInsteadOfTheCamera()
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        var wall = new Pos(25, 25);
        var goblin = new SiegeEnemy
        {
            Id = 1, X = 25.5, Y = 24.5, PreviousX = 25.5, PreviousY = 24.5,
            MotionInitialized = true, Route = [wall], AttackCooldown = 1,
        };
        var pose = SiegeMotion.Enemy(siege, goblin);
        Assert.Equal(Vector2.UnitY, pose.Forward);
        Assert.Equal(0, pose.Stride);
        Assert.Equal(0, pose.Bob);
        Assert.True(pose.Attack > 0.9f);
        goblin.Route = [new(15, 35)];
        goblin.X = 14.5;
        goblin.Y = 35.5;
        goblin.PreviousX = goblin.X;
        goblin.PreviousY = goblin.Y;
        Assert.Equal(Vector2.UnitX, SiegeMotion.Enemy(siege, goblin).Forward);
    }

    [Fact]
    public void LoadingDoesNotInterpolateAttackersFromAnUninitializedOrigin()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        game.Update(0.2);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        foreach (var goblin in loaded.Settlement!.Enemies)
            Assert.Equal(new Vector2((float)goblin.X, (float)goblin.Y), SiegeMotion.Enemy(loaded.Settlement, goblin).Position);
    }

    [Fact]
    public void WallAttackHasAContinuousStrikeRecoilAndWindUpCycle()
    {
        Assert.Equal(1, SiegeMotion.AttackSwing(0));
        Assert.Equal(0, SiegeMotion.AttackSwing(0.3));
        Assert.InRange(SiegeMotion.AttackSwing(0.85), -1.001f, -0.999f);
        Assert.True(SiegeMotion.AttackSwing(0.99) > 0.9f);
        Assert.Equal(1, SiegeMotion.AttackSwing(1));
        Assert.InRange(Math.Abs(SiegeMotion.AttackSwing(0.99999) - SiegeMotion.AttackSwing(0)), 0, 0.001f);
    }

    [Fact]
    public void ArrowsHaveShortFlightsAndContinuousSubTickProgress()
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        var arrow = new SiegeProjectile { StartX = 10, StartY = 10, EndX = 20, EndY = 10, Kind = WallDefenseKind.None };
        Assert.InRange(arrow.FlightSeconds * 2, 0.15, 0.6);
        siege.Accumulator = 0.025;
        float first = SiegeMotion.ProjectileProgress(siege, arrow);
        siege.Accumulator = 0.075;
        Assert.True(SiegeMotion.ProjectileProgress(siege, arrow) > first);
        arrow.Progress = 0.99;
        Assert.Equal(1, SiegeMotion.ProjectileProgress(siege, arrow));
        arrow.Kind = WallDefenseKind.Trebuchet;
        Assert.Equal(0.65, arrow.FlightSeconds);
    }

    [Theory]
    [InlineData(WallDefenseKind.None, 0, .12)]
    [InlineData(WallDefenseKind.None, .2, .12)]
    [InlineData(WallDefenseKind.Ballista, .25, .3)]
    [InlineData(WallDefenseKind.Trebuchet, .6, 1.5)]
    public void CommittedFlightsAdvanceBetweenTicksAndRemainContinuousAtRollover(
        WallDefenseKind kind, double windup, double travel)
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        var shot = new SiegeProjectile { Id = 1, Kind = kind, Windup = windup,
            TravelSeconds = travel, Elapsed = windup, StartX = 10, StartY = 10,
            EndX = 20, EndY = 10, WorldStartHeight = 7, WorldEndHeight = .8f };
        var saved = SavedAttack.Capture(shot);
        float previous = -1;
        foreach (double fraction in new[] { .01, .025, .05, .075, .099999 })
        {
            siege.Accumulator = fraction;
            float progress = SiegeMotion.ProjectileFlightProgress(siege, shot);
            Assert.True(progress > previous);
            Assert.Equal((float)(fraction / travel), progress, 5);
            previous = progress;
        }
        Assert.Equal(saved, SavedAttack.Capture(shot));
        var before = SettlementState.AttackPoint(shot, previous);
        shot.Elapsed += .1;
        siege.Accumulator = 0;
        var after = SettlementState.AttackPoint(shot, SiegeMotion.ProjectileFlightProgress(siege, shot));
        Assert.InRange(Vector3.Distance(before, after), 0, .001f);
    }

    [Theory]
    [InlineData(.12)]
    [InlineData(.04)]
    public void CommittedFlightClampsWindupAndContactWithoutApplyingDamage(double travel)
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        var shot = new SiegeProjectile { Id = 1, Windup = .25, TravelSeconds = travel, Elapsed = .2 };
        siege.Accumulator = .025;
        Assert.True(SiegeMotion.ProjectileElapsed(siege, shot) < shot.Windup);
        Assert.Equal(0, SiegeMotion.ProjectileFlightProgress(siege, shot));
        siege.Accumulator = .075;
        Assert.True(SiegeMotion.ProjectileElapsed(siege, shot) > shot.Windup);
        Assert.InRange(SiegeMotion.ProjectileFlightProgress(siege, shot), .001f, .999f);
        shot.Elapsed = shot.ContactSeconds;
        Assert.Equal(1, SiegeMotion.ProjectileFlightProgress(siege, shot));
        Assert.False(shot.Applied);
    }

    [Theory]
    [InlineData(GameSpeed.Slow)]
    [InlineData(GameSpeed.Medium)]
    [InlineData(GameSpeed.Fast)]
    public void CommittedVisualClockUsesExistingSpeedAndFreezesWhilePaused(GameSpeed speed)
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        siege.WaveNumber = 1;
        game.Speed = speed;
        var shot = new SiegeProjectile { Id = 1, TravelSeconds = 1 };
        game.Paused = false;
        game.Update(.01);
        double expected = .01 * game.Config.MediumSecondsPerWeek / game.Config.SecondsPerWeek(speed);
        Assert.Equal(expected, SiegeMotion.ProjectileElapsed(siege, shot), 8);
        game.Paused = true;
        string saved = SaveGameStore.Serialize(game);
        float frozen = SiegeMotion.ProjectileFlightProgress(siege, shot);
        game.Update(.5);
        Assert.Equal(frozen, SiegeMotion.ProjectileFlightProgress(siege, shot));
        Assert.Equal(saved, SaveGameStore.Serialize(game));
        game.Paused = false;
        game.Update(.01);
        Assert.True(SiegeMotion.ProjectileFlightProgress(siege, shot) > frozen);
    }
}
