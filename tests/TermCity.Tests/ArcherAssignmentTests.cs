using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class ArcherAssignmentTests
{
    private static CityGame Battle(int enemies)
    {
        var game = SettlementGarrisonTests.Founded();
        var home = SettlementGarrisonTests.PlaceGarrison(game, "Archer Barracks");
        var state = game.Settlement!;
        game.SetFunding(ServiceKind.Defence, 0);
        state.WaveNumber = 1;
        var tower = state.Walls.First(w => w.IsTower && w.Cell.X == state.Bounds.Right &&
            w.Cell.Y > state.Bounds.Y + 4 && w.Cell.Y < state.Bounds.Bottom - 4);
        for (int i = 0; i < 4; i++)
            state.DefenderList.Add(new SettlementDefender
            {
                Id = state.NextDefenderId++, Home = home, Kind = DefenderKind.Archer,
                X = tower.Center.X + (i % 2 == 0 ? -.5 : .5),
                Y = tower.Center.Y + (i < 2 ? -.5 : .5),
                Elevation = WallDamageLayout.ActorWalkHeight(tower, game.Map),
            });
        for (int i = 0; i < enemies; i++)
        {
            int y = tower.Cell.Y - 5 + i * 2;
            state.EnemyList.Add(new SiegeEnemy
            {
                Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Goblin, Tier = 1,
                X = state.Bounds.Right + 1.6, Y = y + .5, Health = 96, MaxHealth = 96,
                Route = [new(state.Bounds.Right + 1, y), new(state.Bounds.Right, y)],
            });
        }
        return game;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void AutomaticArchersDivideAmongEnemiesAndKeepBalancedAssignmentsStable(int enemies)
    {
        var game = Battle(enemies);
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        var assignments = state.Defenders.ToDictionary(unit => unit.Id, unit => unit.TargetEnemyId);
        Assert.All(assignments.Values, target => Assert.NotNull(target));
        Assert.Equal(Math.Min(enemies, 4), assignments.Values.Distinct().Count());
        var loads = state.Enemies.Select(enemy => assignments.Values.Count(target => target == enemy.Id)).ToArray();
        Assert.True(loads.Max() - loads.Min() <= 1);
        for (int tick = 0; tick < 30; tick++)
        {
            state.AdvanceDefenders(game, .1);
            Assert.All(state.Defenders, unit => Assert.Equal(assignments[unit.Id], unit.TargetEnemyId));
        }
    }

    [Fact]
    public void ArchersRebalanceAfterDeathsAndConvergeOnTheLastSurvivor()
    {
        var game = Battle(4);
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        Assert.Equal(4, state.Defenders.Select(unit => unit.TargetEnemyId).Distinct().Count());
        state.EnemyList[3].Health = 0;
        state.AdvanceDefenders(game, .1);
        var loads = state.Enemies.Where(enemy => enemy.Health > 0)
            .Select(enemy => state.Defenders.Count(unit => unit.TargetEnemyId == enemy.Id)).ToArray();
        Assert.Equal(3, loads.Count(load => load > 0));
        Assert.True(loads.Max() - loads.Min() <= 1);
        state.EnemyList[1].Health = state.EnemyList[2].Health = 0;
        state.AdvanceDefenders(game, .1);
        Assert.All(state.Defenders, unit => Assert.Equal(state.EnemyList[0].Id, unit.TargetEnemyId));
    }

    [Fact]
    public void SeparateWallSectorsSendArchersToSeparateFiringPosts()
    {
        var game = Battle(0);
        var state = game.Settlement!;
        Pos[] walls = [
            new(state.Bounds.Right, state.Bounds.Y + 12),
            new(state.Bounds.X + 50, state.Bounds.Y),
            new(state.Bounds.X + 50, state.Bounds.Bottom),
            new(state.Bounds.X, state.Bounds.Y + 50),
        ];
        foreach (var wall in walls)
        {
            double x = wall.X + .5, y = wall.Y + .5;
            if (wall.X == state.Bounds.Right) x = state.Bounds.Right + 1.6;
            else if (wall.X == state.Bounds.X) x = state.Bounds.X - .6;
            else if (wall.Y == state.Bounds.Y) y = state.Bounds.Y - .6;
            else y = state.Bounds.Bottom + 1.6;
            state.EnemyList.Add(new SiegeEnemy
            {
                Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Goblin, Tier = 1,
                X = x, Y = y, Health = 96, MaxHealth = 96, Route = [wall],
            });
        }
        state.AdvanceDefenders(game, .1);
        Assert.Equal(4, state.Defenders.Select(unit => unit.TargetEnemyId).Distinct().Count());
        var goals = state.Defenders.Select(unit => unit.PathTarget).Where(goal => goal is not null).ToArray();
        Assert.True(goals.Distinct().Count() >= 3);
        for (int tick = 0; tick < 800; tick++) state.AdvanceDefenders(game, .1);
        Assert.True(state.Defenders.Max(unit => unit.X) - state.Defenders.Min(unit => unit.X) > 20);
        Assert.True(state.Defenders.Max(unit => unit.Y) - state.Defenders.Min(unit => unit.Y) > 20,
            string.Join("; ", state.Defenders.Select(unit =>
                $"{unit.Id}: {unit.X:F2},{unit.Y:F2} z={unit.Elevation:F2} target={unit.TargetEnemyId} " +
                $"goal={unit.PathTarget} path={unit.PathIndex}/{unit.Path.Count}")));
        Assert.All(state.Defenders, unit =>
        {
            var enemy = state.Enemies.Single(enemy => enemy.Id == unit.TargetEnemyId);
            Assert.True(Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2) <=
                Math.Pow(CombatProfiles.Archer.Range, 2));
        });
    }

    [Fact]
    public void ExplicitFocusFireRemainsAuthoritativeAndCountsTowardAutomaticCoverage()
    {
        var game = Battle(3);
        var state = game.Settlement!;
        var manual = state.Defenders.Take(2).ToArray();
        foreach (var unit in manual)
        {
            unit.Order = DefenderOrder.Attack;
            unit.TargetEnemyId = state.EnemyList[0].Id;
        }
        state.AdvanceDefenders(game, .1);
        Assert.All(manual, unit =>
        {
            Assert.Equal(DefenderOrder.Attack, unit.Order);
            Assert.Equal(state.EnemyList[0].Id, unit.TargetEnemyId);
        });
        var automatic = state.Defenders.Where(unit => unit.Order == DefenderOrder.Automatic).ToArray();
        Assert.DoesNotContain(automatic, unit => unit.TargetEnemyId == state.EnemyList[0].Id);
        Assert.Equal(2, automatic.Select(unit => unit.TargetEnemyId).Distinct().Count());
    }

    [Fact]
    public void TargetsWithoutAnyReachableBowPostDoNotDrawArchersAwayFromDefendableThreats()
    {
        var game = Battle(1);
        var state = game.Settlement!;
        var reachable = state.Enemies.Single();
        state.EnemyList.Add(new SiegeEnemy
        {
            Id = state.NextEnemyId++, X = 3.5, Y = 3.5, Health = 96, MaxHealth = 96, Tier = 1,
            Route = [new(4, 3)],
        });
        state.AdvanceDefenders(game, .1);
        Assert.All(state.Defenders, unit => Assert.Equal(reachable.Id, unit.TargetEnemyId));
    }

    [Fact]
    public void SavedAssignmentsRemainDistributedAfterLoadingWithoutNewSaveFields()
    {
        var game = Battle(4);
        game.Settlement!.AdvanceDefenders(game, .1);
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        var before = restored.Settlement!.Defenders.ToDictionary(unit => unit.Id, unit => unit.TargetEnemyId);
        restored.Paused = false;
        restored.Settlement.AdvanceDefenders(restored, .1);
        Assert.All(restored.Settlement.Defenders, unit => Assert.Equal(before[unit.Id], unit.TargetEnemyId));
        Assert.Equal(4, restored.Settlement.Defenders.Select(unit => unit.TargetEnemyId).Distinct().Count());
    }
}
