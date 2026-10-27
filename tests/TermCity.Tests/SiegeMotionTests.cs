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
        var goblin = siege.Goblins[0];
        var first = SiegeMotion.Goblin(siege, goblin);
        int revision = siege.Revision;
        game.Update(0.1);
        Assert.Equal(revision, siege.Revision);
        var middle = SiegeMotion.Goblin(siege, goblin);
        Assert.True(Vector2.Distance(first.Position, middle.Position) > 0.01f);
        Assert.NotEqual(first.Stride, middle.Stride);
        Assert.InRange(Vector2.Dot(middle.Forward, Vector2.Normalize(new Vector2(
            (float)(goblin.X - goblin.PreviousX), (float)(goblin.Y - goblin.PreviousY)))), 0.999f, 1.001f);
        game.Paused = true;
        game.Update(0.5);
        Assert.Equal(middle, SiegeMotion.Goblin(siege, goblin));
        string saved = SaveGameStore.Serialize(game);
        SiegeMotion.Goblin(siege, goblin);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void StationaryAttackersFaceTheirActualTargetWallInsteadOfTheCamera()
    {
        var game = TestCity.Flat();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        var siege = game.Settlement!;
        var wall = new Pos(25, 25);
        var goblin = new Goblin
        {
            Id = 1, X = 25.5, Y = 24.5, PreviousX = 25.5, PreviousY = 24.5,
            MotionInitialized = true, Route = [wall], AttackCooldown = 1,
        };
        var pose = SiegeMotion.Goblin(siege, goblin);
        Assert.Equal(Vector2.UnitY, pose.Forward);
        Assert.Equal(0, pose.Stride);
        Assert.Equal(0, pose.Bob);
        Assert.True(pose.Attack > 0.9f);
        goblin.Route = [new(15, 35)];
        goblin.X = 14.5;
        goblin.Y = 35.5;
        goblin.PreviousX = goblin.X;
        goblin.PreviousY = goblin.Y;
        Assert.Equal(Vector2.UnitX, SiegeMotion.Goblin(siege, goblin).Forward);
    }

    [Fact]
    public void LoadingDoesNotInterpolateAttackersFromAnUninitializedOrigin()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 });
        SettlementDebugSetup.Apply(game, siege: true);
        game.Update(0.2);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        foreach (var goblin in loaded.Settlement!.Goblins)
            Assert.Equal(new Vector2((float)goblin.X, (float)goblin.Y), SiegeMotion.Goblin(loaded.Settlement, goblin).Position);
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
}
