using System.Numerics;
using System.Text.Json.Nodes;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class OrcCombatAnimationTests
{
    [Fact]
    public void FasterSwingKeepsAuthoredPoseTimeAndPreservesDamagePerSecond()
    {
        var enemy = new SiegeEnemy { Kind = SiegeEnemyKind.Orc, Tier = 2 };
        OrcCombatAnimation.Advance(enemy, .1);
        Assert.Equal(.125, enemy.OrcAttackSeconds, 10);
        Assert.Equal(1.25, OrcCombatAnimation.PlaybackRate);
        Assert.Equal(2.9866666666666664, OrcCombatAnimation.CycleSeconds, 10);
        double damage = 0;
        OrcCombatAnimation.Stop(enemy);
        for (int tick = 0; tick < 30; tick++) damage += OrcCombatAnimation.Advance(enemy, .1);
        Assert.Equal(enemy.Definition.AttackDamage(2) * OrcCombatAnimation.CycleSeconds, damage, 7);
        Assert.Equal(.8, OrcCombatAnimation.CycleSeconds / OrcCombatAnimation.Duration, 10);
    }

    [Fact]
    public void ThreeContactsPreservePreviousAverageDamageRate()
    {
        var enemy = new SiegeEnemy { Kind = SiegeEnemyKind.Orc, Tier = 2 };
        double damage = 0;
        var contacts = new List<double>();
        for (int tick = 0; tick < 90; tick++)
        {
            double hit = OrcCombatAnimation.Advance(enemy, .1);
            if (hit > 0) contacts.Add((tick + 1) * .1);
            damage += hit;
        }
        Assert.Equal(9, contacts.Count);
        Assert.Equal(enemy.Definition.AttackDamage(2) * OrcCombatAnimation.CycleSeconds * 3, damage, 7);
        foreach (double impact in OrcCombatAnimation.ImpactSeconds)
            Assert.Contains(contacts, time => time >= impact / OrcCombatAnimation.PlaybackRate - 1e-8 &&
                time <= impact / OrcCombatAnimation.PlaybackRate + .1);
    }

    [Fact]
    public void ImpactIntervalsAreHalfOpenAndWrapWithoutDuplicates()
    {
        foreach (double contact in OrcCombatAnimation.ImpactSeconds)
        {
            Assert.Equal(1, OrcCombatAnimation.ImpactsBetween(contact - .01, contact));
            Assert.Equal(0, OrcCombatAnimation.ImpactsBetween(contact, contact + .01));
        }
        Assert.Equal(3, OrcCombatAnimation.ImpactsBetween(0, OrcCombatAnimation.Duration));
        Assert.Equal(1, OrcCombatAnimation.ImpactsBetween(OrcCombatAnimation.Duration,
            OrcCombatAnimation.Duration + OrcCombatAnimation.ImpactSeconds[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrcCombatAnimation.ImpactsBetween(0, double.NaN));
        foreach (double contact in OrcCombatAnimation.ImpactSeconds)
        {
            Assert.InRange(OrcCombatAnimation.Swing(contact - .05), -.999f, -.8f);
            Assert.Equal(1, OrcCombatAnimation.Swing(contact));
            Assert.InRange(OrcCombatAnimation.Swing(contact + .05), .7f, 1f);
        }
    }

    [Fact]
    public void LeavingCombatCancelsTheComboRatherThanApplyingAnOffTargetHit()
    {
        var enemy = new SiegeEnemy { Kind = SiegeEnemyKind.Orc, Tier = 2 };
        for (int i = 0; i < 8; i++) Assert.Equal(0, OrcCombatAnimation.Advance(enemy, .1));
        OrcCombatAnimation.Stop(enemy);
        Assert.False(enemy.OrcAttacking);
        Assert.Equal(0, enemy.OrcAttackSeconds);
        Assert.Equal(0, OrcCombatAnimation.Advance(enemy, .1));
    }

    [Fact]
    public void WallDamageAndVisualPhaseShareTheSameThreeContacts()
    {
        var game = WorldScaleTests.Founded(250);
        var state = game.Settlement!;
        state.EnemyList.Clear();
        state.WaveNumber = 2;
        state.OrcsIntroduced = true;
        foreach (var wall in state.Walls) wall.ArrowCooldown = wall.DefenseCooldown = 100;
        game.SetFunding(ServiceKind.Defence, 0);
        state.NextWaveSeconds = 120;
        var target = state.Walls.First(w => !w.IsTower && !w.IsGate && w.Cell.Y == state.Bounds.Y &&
            state.Walls.Where(t => t.IsTower).All(t => Vector2.Distance(t.Center, w.Center) > 5));
        target.Health = target.MaxHealth = 100000;
        var enemy = new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2, X = target.Cell.X + .5,
            Y = target.Cell.Y - .5, Health = 432, MaxHealth = 432,
            Route = [target.Cell], MotionInitialized = true,
        };
        state.EnemyList.Add(enemy);
        target = state.AttackableWall(enemy)!;
        Assert.NotNull(target);
        target.Health = target.MaxHealth = 100000;
        var damages = new List<(double Time, double Damage)>();
        double previous = target.Health;
        for (int tick = 0; tick < 37; tick++)
        {
            state.Advance(game, .1);
            if (target.Health < previous)
                damages.Add(((tick + 1) * .1, previous - target.Health + state.RepairRatePerSecond(game) * .1));
            previous = target.Health;
        }
        Assert.True(enemy.OrcAttacking, $"Orc did not settle into attack: {enemy.X},{enemy.Y}, " +
            $"clearance={Vector2.Distance(new((float)enemy.X, (float)enemy.Y), SettlementState.WallAttackPoint(target, enemy.X, enemy.Y))}");
        Assert.Equal(3, damages.Count);
        Assert.Equal(enemy.Definition.AttackDamage(2) * OrcCombatAnimation.CycleSeconds, damages.Sum(d => d.Damage), 6);
        Assert.True(enemy.OrcAttacking);
        Assert.True(SiegeMotion.Enemy(state, enemy).OrcAttacking);
    }

    [Fact]
    public void EngagedSoldierReceivesOnlyComboContactsAtTheSameAverageRate()
    {
        var game = SettlementGarrisonTests.Founded(250);
        var home = SettlementGarrisonTests.PlaceGarrison(game, "Guard Post");
        var state = game.Settlement!;
        state.WaveNumber = 2; state.OrcsIntroduced = true;
        state.EnemyList.Clear();
        state.DefenderList.Clear();
        var area = game.VacantPlots(TermCity.Core.World.ZoneType.Residential).First();
        var enemy = new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2,
            X = area.X + .5, Y = area.Y + .5, Health = 432, MaxHealth = 432,
            Route = [state.Walls.First(w => !w.IsTower && !w.IsGate).Cell],
        };
        var guard = new SettlementDefender
        {
            Id = 1, Home = home, Kind = DefenderKind.Soldier,
            X = enemy.X + OrcCombatAnimation.WallStandOff + TermCity.Core.World.WorldScale.HumanRadius, Y = enemy.Y,
            Health = 10000, AttackCooldown = 100, Order = DefenderOrder.Attack, TargetEnemyId = enemy.Id,
        };
        state.EnemyList.Add(enemy); state.DefenderList.Add(guard);
        state.BindGarrisonGame(game);
        Assert.NotNull(state.EngagedDefender(enemy));
        int hits = 0; double previous = guard.Health;
        for (int tick = 0; tick < 37; tick++)
        {
            state.Advance(game, .1);
            if (guard.Health < previous) hits++;
            previous = guard.Health;
        }
        Assert.Equal(3, hits);
        Assert.Equal(enemy.Definition.AttackDamage(2) * OrcCombatAnimation.CycleSeconds, 10000 - guard.Health, 6);
    }

    [Fact]
    public void SaveAndSnapshotKeepTheExactComboPhaseAndOlderSavesStartNeutral()
    {
        var game = SettlementGarrisonTests.Founded(250);
        var state = game.Settlement!;
        state.OrcsIntroduced = true;
        state.WaveNumber = 2;
        var target = state.Walls.First(w => !w.IsTower && !w.IsGate && w.Cell.Y == state.Bounds.Y);
        var enemy = new SiegeEnemy
        {
            Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2, X = target.Cell.X + .5,
            Y = target.Cell.Y - .5, Health = 432, MaxHealth = 432, Route = [target.Cell], OrcAttacking = true,
            OrcAttackSeconds = 1.8, PreviousOrcAttackSeconds = 1.7,
        };
        state.EnemyList.Add(enemy);
        game.HighestPopulation = game.HighestMilestone = 250;
        string saved = SaveGameStore.Serialize(game);
        var restored = Assert.Single(SaveGameStore.Deserialize(saved).Settlement!.Enemies);
        Assert.True(restored.OrcAttacking);
        Assert.Equal(1.8, restored.OrcAttackSeconds);
        Assert.Equal(1.8, restored.PreviousOrcAttackSeconds);
        var copy = enemy.SnapshotCopy();
        Assert.Equal(enemy.OrcAttackSeconds, copy.OrcAttackSeconds);
        var node = JsonNode.Parse(saved)!;
        var old = node["Settlement"]!["Goblins"]![0]!.AsObject();
        old.Remove("OrcAttacking"); old.Remove("OrcAttackSeconds");
        Assert.False(Assert.Single(SaveGameStore.Deserialize(node.ToJsonString()).Settlement!.Enemies).OrcAttacking);
        old["OrcAttackSeconds"] = 10;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void PhaseInterpolationWrapsSmoothlyAndDoesNotAdvanceWhenPaused()
    {
        var state = new SettlementState(new(10, 10, 24, 24), BaileyGateSide.Right);
        var enemy = new SiegeEnemy
        {
            Kind = SiegeEnemyKind.Orc, Tier = 2, X = 15.5, Y = 9.5, Route = [new(15, 10)],
            OrcAttacking = true, PreviousOrcAttackSeconds = 3.7, OrcAttackSeconds = .066666666666666,
        };
        state.PresentationFraction = .5;
        double first = SiegeMotion.Enemy(state, enemy).OrcAnimationSeconds;
        Assert.InRange(first, .016, .017);
        Assert.Equal(first, SiegeMotion.Enemy(state, enemy).OrcAnimationSeconds);
        Assert.Equal(4, new SiegeDeath(Vector2.Zero, 0, false, default, SiegeEnemyKind.Orc, 1,
            SiegeDeathCause.Arrow, Vector2.UnitY).Duration);
    }
}
