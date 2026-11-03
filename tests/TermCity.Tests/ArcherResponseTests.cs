using System.Numerics;
using System.Reflection;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ArcherResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticArchersApproachTowerlessThreatsAndDoNotIdleBehindBlockedShots(bool blockedNearby)
    {
        var game = SettlementGarrisonTests.Founded();
        SettlementGarrisonTests.PlaceGarrison(game, "Archer Barracks");
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        var unit = Assert.Single(state.Defenders);
        game.SetFunding(ServiceKind.Defence, 0);
        var start = blockedNearby
            ? state.Walls.Single(w => w.Cell == new Pos(state.Bounds.Right, state.Bounds.Y + 20))
            : state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Bottom &&
                w.Cell.X > state.Bounds.X + 4);
        var inward = new Vector2(-start.Tangent.Y, start.Tangent.X) * WallDamageLayout.WalkwayOffset(start);
        unit.X = start.Center.X + inward.X;
        unit.Y = start.Center.Y + inward.Y;
        unit.Elevation = WallDamageLayout.ActorWalkHeight(start, game.Map);
        var target = state.Walls.Single(w => w.Cell == new Pos(state.Bounds.Right, state.Bounds.Y + 12));
        Assert.False(target.IsTower);
        var enemy = new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Goblin, Tier = 1,
            X = target.Center.X + 1.1, Y = target.Center.Y,
            Health = 96, MaxHealth = 96, Route = [target.Cell], RouteIndex = 0,
        };
        state.EnemyList.Add(enemy);
        unit.PatrolWallId = start.Id;
        unit.PathTarget = start.Cell;
        unit.Path = [start.Cell];
        unit.PathIndex = 0;
        double initialX = unit.X, initialY = unit.Y;
        double initialDistance = Vector2.Distance(new((float)unit.X, (float)unit.Y), new((float)enemy.X, (float)enemy.Y));
        if (blockedNearby)
        {
            Assert.True(initialDistance < CombatProfiles.Archer.Range);
            Assert.False((bool)typeof(SettlementState).GetMethod("TryMissile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(state, [game, enemy, WallDefenseKind.None,
                    new Vector3((float)unit.X, unit.Elevation + WorldScale.Feet(4.5f), (float)unit.Y),
                    CombatProfiles.Archer.Damage, start.Id, unit.Id, false])!);
        }
        bool moved = false, approached = false;
        for (int tick = 0; tick < 1600 && enemy.Health > 0; tick++)
        {
            state.AdvanceDefenders(game, .1);
            Assert.Equal(enemy.Id, unit.TargetEnemyId);
            moved |= Math.Pow(unit.X - initialX, 2) + Math.Pow(unit.Y - initialY, 2) > 1;
            approached |= Vector2.Distance(new((float)unit.X, (float)unit.Y), new((float)enemy.X, (float)enemy.Y)) < initialDistance - 1;
            state.AdvanceCommittedAttacks(game, .1);
            state.ProjectileList.RemoveAll(shot => shot.Progress >= 1);
        }
        Assert.True(moved && approached, $"The archer must leave the stale patrol and close on the threat: at={unit.X:F2},{unit.Y:F2}.");
        Assert.True(enemy.Health <= 0, $"The archer must reach a usable firing post, not merely move: hp={enemy.Health}, at={unit.X:F2},{unit.Y:F2}.");
        Assert.Equal(9, CombatProfiles.Archer.Range);
        Assert.Equal(40, CombatProfiles.Archer.Damage);
    }

    [Fact]
    public void OverlappingSavedArchersCanSeparateAndRespondWithoutTeleporting()
    {
        var game = SettlementGarrisonTests.Founded();
        var home = SettlementGarrisonTests.PlaceGarrison(game, "Archer Barracks");
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        var first = Assert.Single(state.Defenders);
        game.SetFunding(ServiceKind.Defence, 0);
        var tower = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Bottom &&
            w.Cell.X > state.Bounds.X + 4);
        first.X = tower.Center.X;
        first.Y = tower.Center.Y;
        first.Elevation = WallDamageLayout.ActorWalkHeight(tower, game.Map);
        var second = new SettlementDefender
        {
            Id = state.NextDefenderId++, Home = home, Kind = DefenderKind.Archer,
            X = first.X, Y = first.Y, Elevation = first.Elevation,
        };
        state.DefenderList.Add(second);
        state.EnemyList.Add(new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Goblin, Tier = 1,
            X = state.Bounds.Right + 1.6, Y = state.Bounds.Y + 12.5,
            Health = 96, MaxHealth = 96, Route = [new(state.Bounds.Right, state.Bounds.Y + 12)],
        });
        for (int tick = 0; tick < 60; tick++)
        {
            var before = state.Defenders.ToDictionary(unit => unit.Id, unit => new Vector2((float)unit.X, (float)unit.Y));
            state.AdvanceDefenders(game, .1);
            foreach (var unit in state.Defenders)
                Assert.InRange(Vector2.Distance(before[unit.Id], new((float)unit.X, (float)unit.Y)),
                    0, (float)SettlementDefender.MovementSpeed * .1f + .00001f);
        }
        Assert.True(Vector2.Distance(new((float)first.X, (float)first.Y), tower.Center) > 2);
        Assert.True(Vector2.Distance(new((float)second.X, (float)second.Y), tower.Center) > 2);
        Assert.True(Math.Abs(first.Elevation - second.Elevation) >= .3f ||
            Vector2.Distance(new((float)first.X, (float)first.Y), new((float)second.X, (float)second.Y)) >= WorldScale.HumanRadius * 2);
    }

    [Fact]
    public void SavedArcherRoutesAcrossWaterWallWalkwaysAreValidButOpenWaterStillIsNot()
    {
        var game = SettlementGarrisonTests.Founded();
        var home = SettlementGarrisonTests.PlaceGarrison(game, "Archer Barracks");
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        var unit = Assert.Single(state.Defenders);
        var wall = state.Walls.First(w => !w.IsTower && !w.IsGate && !w.IsGateWing && w.Cell.Y == state.Bounds.Y);
        game.Map.SetTerrain(wall.Cell.X, wall.Cell.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        unit.X = wall.Center.X;
        unit.Y = wall.Center.Y + WallDamageLayout.WalkwayOffset(wall);
        unit.Elevation = WallDamageLayout.ActorWalkHeight(wall, game.Map);
        unit.Order = DefenderOrder.Move;
        unit.Destination = wall.Cell;
        unit.PathTarget = wall.Cell;
        unit.Path = [wall.Cell];
        unit.PathIndex = 0;
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        Assert.Equal(home, Assert.Single(restored.Settlement!.Defenders).Home);
        Assert.True(state.SavedDefenderCellValid(game.Map, DefenderKind.Archer, wall.Cell));
        Assert.False(state.SavedDefenderCellValid(game.Map, DefenderKind.Soldier, wall.Cell));
        game.Map.SetTerrain(4, 4, game.Map.Content.Terrains.Get("Water"));
        Assert.False(state.SavedDefenderCellValid(game.Map, DefenderKind.Archer, new(4, 4)));
    }
}
