using System.Numerics;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class OrcAttackSpacingTests
{
    private static CityGame Fixture()
    {
        var game = WorldScaleTests.Founded(0);
        game.HighestPopulation = game.HighestMilestone = 250;
        var state = game.Settlement!;
        state.WaveNumber = 2; state.OrcsIntroduced = true;
        state.EnemyList.Clear(); state.NextWaveSeconds = 120;
        game.SetFunding(TermCity.Core.Buildings.ServiceKind.Defence, 0);
        return game;
    }

    private static SiegeEnemy Add(CityGame game, WallComponent wall, Vector2 position, List<Pos>? route = null)
    {
        var state = game.Settlement!;
        var enemy = new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2,
            X = position.X, Y = position.Y, Health = 432, MaxHealth = 432,
            Route = route ?? [wall.Cell],
        };
        state.EnemyList.Add(enemy);
        return enemy;
    }

    private static double Clearance(SettlementState state, SiegeEnemy enemy)
    {
        var target = state.AttackableWall(enemy);
        Assert.NotNull(target);
        return Vector2.Distance(new((float)enemy.X, (float)enemy.Y),
            SettlementState.WallAttackPoint(target, enemy.X, enemy.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExistingCloseAttackersStepBackOnEveryWallSideAndRemainSaveable(int side)
    {
        var game = Fixture(); var state = game.Settlement!;
        var outward = side switch { 0 => -Vector2.UnitY, 1 => Vector2.UnitX, 2 => Vector2.UnitY, _ => -Vector2.UnitX };
        var wall = state.Walls.First(w => !w.IsTower && !w.IsGate && !w.IsGateWing &&
            (side == 0 ? w.Cell.Y == state.Bounds.Y : side == 1 ? w.Cell.X == state.Bounds.Right :
                side == 2 ? w.Cell.Y == state.Bounds.Bottom : w.Cell.X == state.Bounds.X) &&
            state.Walls.Where(t => t.IsTower).All(t => Vector2.Distance(t.Center, w.Center) > 5));
        float half = Math.Abs(outward.X) > .5f ? WallDamageLayout.HalfSize(wall).X : WallDamageLayout.HalfSize(wall).Y;
        var enemy = Add(game, wall, wall.Center + outward * (half + .5f));
        var start = new Vector2((float)enemy.X, (float)enemy.Y);
        for (int tick = 0; tick < 25; tick++)
        {
            var before = new Vector2((float)enemy.X, (float)enemy.Y);
            state.Advance(game, .1);
            Assert.InRange(Vector2.Distance(before, new((float)enemy.X, (float)enemy.Y)), 0, .1051);
            var loaded = Assert.Single(SaveGameStore.Deserialize(SaveGameStore.Serialize(game)).Settlement!.Enemies);
            Assert.Equal(enemy.X, loaded.X, 6); Assert.Equal(enemy.Y, loaded.Y, 6);
        }
        Assert.True(Vector2.Dot(new((float)enemy.X - start.X, (float)enemy.Y - start.Y), outward) > .7f);
        Assert.InRange(Clearance(state, enemy), OrcCombatAnimation.WallStandOff - .00001, OrcCombatAnimation.WallReach);
        Assert.True(enemy.OrcAttacking);
    }

    [Fact]
    public void EmbeddedLegacyContactCanEscapeOutwardWithoutTeleporting()
    {
        var game = Fixture(); var state = game.Settlement!;
        var wall = state.Walls.First(w => !w.IsTower && !w.IsGate && w.Cell.Y == state.Bounds.Y &&
            state.Walls.Where(t => t.IsTower).All(t => Vector2.Distance(t.Center, w.Center) > 5));
        var enemy = Add(game, wall, wall.Center);
        for (int tick = 0; tick < 40; tick++) state.Advance(game, .1);
        Assert.True(enemy.Y < wall.Center.Y, $"start={wall.Center}, actual={enemy.X},{enemy.Y}, " +
            $"building={game.Map.BuildingAt((int)enemy.X, (int)enemy.Y)?.Name}, " +
            $"outward={-WallDamageLayout.LadderInward(wall)}, target={state.AttackableWall(enemy)?.Cell}");
        Assert.True(enemy.OrcAttacking, $"Embedded orc stopped at {enemy.X},{enemy.Y}, " +
            $"target={state.AttackableWall(enemy)?.Cell}, route={enemy.RouteIndex}/{enemy.Route.Count}");
        Assert.InRange(Clearance(state, enemy), OrcCombatAnimation.WallStandOff - .00001, OrcCombatAnimation.WallReach);
        SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
    }

    [Fact]
    public void FreshApproachStopsAtTheSwingEnvelopeRatherThanTheOldBodyRadius()
    {
        var game = Fixture(); var state = game.Settlement!;
        var wall = state.Walls.First(w => !w.IsTower && !w.IsGate && w.Cell.Y == state.Bounds.Y &&
            state.Walls.Where(t => t.IsTower).All(t => Vector2.Distance(t.Center, w.Center) > 5));
        var route = Enumerable.Range(wall.Cell.Y - 3, 4).Select(y => new Pos(wall.Cell.X, y)).ToList();
        var enemy = Add(game, wall, new(wall.Cell.X + .5f, wall.Cell.Y - 3.5f), route);
        for (int tick = 0; tick < 50; tick++) state.Advance(game, .1);
        Assert.True(enemy.OrcAttacking);
        Assert.InRange(Clearance(state, enemy), OrcCombatAnimation.WallStandOff - .00001, OrcCombatAnimation.WallReach);
        var position = new Vector2((float)enemy.X, (float)enemy.Y);
        for (int tick = 0; tick < 5; tick++) state.Advance(game, .1);
        Assert.Equal(position, new Vector2((float)enemy.X, (float)enemy.Y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TowerAndGateClearanceUsesTheirActualFaces(bool gate)
    {
        var game = Fixture(); var state = game.Settlement!;
        var wall = gate ? state.Walls.Single(w => w.IsGate) :
            state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y && w.Cell.X > state.Bounds.X + 5);
        var outward = gate ? Vector2.UnitX : -Vector2.UnitY;
        var half = WallDamageLayout.HalfSize(wall);
        var enemy = Add(game, wall, wall.Center + outward * ((gate ? half.X : half.Y) + .2f));
        for (int tick = 0; tick < 30; tick++) state.Advance(game, .1);
        Assert.True(enemy.OrcAttacking);
        Assert.InRange(Clearance(state, enemy), OrcCombatAnimation.WallStandOff - .00001, OrcCombatAnimation.WallReach);
        SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
    }

    [Fact]
    public void CornerRetreatCompletesTheSideStepThenBacksAwayWithoutAlternatingHeadings()
    {
        var game = Fixture(); var state = game.Settlement!;
        var tower = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Bottom &&
            w.Cell.X > state.Bounds.X + 20 && w.Cell.X < state.Bounds.Right - 5);
        var wall = state.Walls.Single(w => w.Cell == tower.Cell.Offset(-2, 0));
        var position = new Vector2(tower.Center.X - 2.52503f, state.Bounds.Bottom + 1.5f);
        var enemy = Add(game, wall, position, [wall.Cell.Offset(0, 1), wall.Cell]);
        int reversals = 0;
        Vector2? previous = null;
        int attackingTicks = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            state.Advance(game, .1);
            if (enemy.Health <= 0 || !state.Enemies.Contains(enemy)) break;
            var pose = SiegeMotion.Enemy(state, enemy);
            if (previous is { } direction && Vector2.Dot(direction, pose.Forward) < -.5f) reversals++;
            previous = pose.Forward;
            if (enemy.OrcAttacking)
            {
                attackingTicks++;
                Assert.True(state.AttackableWall(enemy) is not null,
                    $"t={tick} live={state.Enemies.Contains(enemy)} pos={enemy.X},{enemy.Y} hp={enemy.Health} wall={wall.Health}");
                Assert.InRange(Clearance(state, enemy), OrcCombatAnimation.WallStandOff - .00001, OrcCombatAnimation.WallReach);
            }
            SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        }
        Assert.True(attackingTicks >= 10, $"Corner orc remained stuck at {enemy.X},{enemy.Y}");
        Assert.True(reversals <= 2, $"Corner retreat reversed {reversals} times in six seconds.");
    }

    [Fact]
    public void NewOrcWaveStartsAtDifferentRealApproachDepthsReproducibly()
    {
        var first = Fixture(); var second = Fixture();
        Assert.True(first.Settlement!.SpawnWave(first));
        Assert.True(second.Settlement!.SpawnWave(second));
        var a = first.Settlement.Enemies.Where(e => e.Kind == SiegeEnemyKind.Orc).ToArray();
        var b = second.Settlement.Enemies.Where(e => e.Kind == SiegeEnemyKind.Orc).ToArray();
        Assert.Equal(3, a.Length);
        Assert.Equal(a.Length, a.Select(e => e.Route.Count).Distinct().Count());
        Assert.All(a, e => Assert.InRange(e.Route.Count, 6, 12));
        Assert.Equal(a.Select(e => (e.X, e.Y, e.Route.Count)), b.Select(e => (e.X, e.Y, e.Route.Count)));
        foreach (var orc in a)
            foreach (var other in first.Settlement.Enemies.Where(e => e != orc))
                Assert.True(Math.Sqrt(Math.Pow(orc.X - other.X, 2) + Math.Pow(orc.Y - other.Y, 2)) + .00001 >=
                    orc.CollisionRadius + other.CollisionRadius);
        SaveGameStore.Deserialize(SaveGameStore.Serialize(first));
    }

    [Fact]
    public void StaggeredApproachesNaturallyStartTheirCombosAtDifferentTimes()
    {
        var game = Fixture(); var state = game.Settlement!;
        Assert.True(state.SpawnWave(game));
        state.EnemyList.RemoveAll(e => e.Kind != SiegeEnemyKind.Orc);
        foreach (var wall in state.Walls)
        {
            wall.Health = wall.MaxHealth = 100000;
            wall.ArrowCooldown = wall.DefenseCooldown = 100;
        }
        var arrivals = new Dictionary<int, double>();
        for (int tick = 0; tick < 240 && arrivals.Count < 3; tick++)
        {
            state.Advance(game, .1);
            foreach (var enemy in state.Enemies.Where(e => e.OrcAttacking))
                arrivals.TryAdd(enemy.Id, (tick + 1) * .1);
        }
        Assert.True(arrivals.Count == 3, "Arrivals=" + string.Join(",", arrivals) + " remaining=" +
            string.Join("; ", state.Enemies.Select(e => $"{e.Id}: {e.X:F3},{e.Y:F3} next={e.Route[e.RouteIndex]} " +
                $"target={state.AttackableWall(e)?.Cell} attacking={e.OrcAttacking}")));
        Assert.Equal(3, arrivals.Values.Distinct().Count());
        Assert.True(arrivals.Values.Max() - arrivals.Values.Min() >= 1);
        Assert.Contains(arrivals.Values, time =>
        {
            double phase = (time - arrivals.Values.Min()) % OrcCombatAnimation.CycleSeconds;
            return phase > .25 && phase < OrcCombatAnimation.CycleSeconds - .25;
        });
    }

    [Fact]
    public void MeleeSpacingIsSharedBySoldierAndOrcReachWithoutChangingGoblinReach()
    {
        var orc = new SiegeEnemy { Kind = SiegeEnemyKind.Orc, Tier = 2 };
        var goblin = new SiegeEnemy { Kind = SiegeEnemyKind.Goblin, Tier = 1 };
        Assert.Equal(OrcCombatAnimation.WallReach + WorldScale.HumanRadius, SettlementState.MeleeDistance(orc));
        Assert.Equal(Math.Max(CombatProfiles.Guard.Range, goblin.CollisionRadius + WorldScale.HumanRadius + .25),
            SettlementState.MeleeDistance(goblin));
    }
}
