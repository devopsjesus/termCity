using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using Xunit.Abstractions;

namespace TermCity.Tests;

public class CombatBalanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(WallDefenseKind.OilCauldron, 3, false)]
    [InlineData(WallDefenseKind.Rocks, 5, false)]
    [InlineData(WallDefenseKind.Ballista, 1, false)]
    [InlineData(WallDefenseKind.OilCauldron, 3, true)]
    [InlineData(WallDefenseKind.Rocks, 5, true)]
    [InlineData(WallDefenseKind.Ballista, 1, true)]
    public void EqualBudgetRealWavesRecordDamageKillsBreachesAndPendingLoad(WallDefenseKind kind, int count, bool mixed)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        game.HighestPopulation = game.HighestMilestone = mixed ? 1000 : 250;
        var state = game.Settlement!;
        state.OrcsIntroduced = state.TrollsIntroduced = mixed;
        int before = game.Money;
        foreach (var wall in state.Walls.Where(w => w.IsTower).Take(count))
            Assert.True(game.PlaceWallDefense(wall.Cell, kind).Success);
        int purchaseCost = before - game.Money;
        Assert.InRange(purchaseCost, 1, 1800);
        Assert.True(game.DebugRemoveGoblins().Success);
        foreach (var wall in state.Walls) wall.DefenseCooldown = 0;
        Assert.True(state.SpawnWave(game));
        var attackers = state.Enemies.ToArray();
        if (mixed) Assert.Contains(attackers, e => e.Definition.IsTroll);
        foreach (var wall in state.Walls) wall.ArrowCooldown = 10000;
        game.Paused = false;
        var shots = new HashSet<int>();
        int peak = 0;
        for (int step = 0; step < 300 && state.Enemies.Count > 0; step++)
        {
            state.Advance(game, .1);
            foreach (var shot in state.Projectiles) shots.Add(shot.Id);
            peak = Math.Max(peak, state.Projectiles.Count);
        }
        bool breach = game.Events.Any(e => e.Message.Contains("was breached", StringComparison.Ordinal));
        double damage = attackers.Sum(e => e.MaxHealth - e.Health);
        int killed = attackers.Count(e => e.Health <= 0);
        output.WriteLine($"REAL_WAVE {kind} mixed={mixed} cost={purchaseCost} " +
            $"initial={attackers.Length} shots={shots.Count} kills={killed} damage={damage:0.0} breach={breach} " +
            $"missing-wall-health={state.WallSections.Sum(w => w.MaxHealth - w.Health):0.0} pending-peak={peak}");
        Assert.InRange(peak, 0, SettlementState.MaximumPendingAttacks);
        Assert.All(attackers, e => Assert.InRange(e.Health, 0, e.MaxHealth));
    }

    [Theory]
    [InlineData(WallDefenseKind.OilCauldron)]
    [InlineData(WallDefenseKind.Rocks)]
    [InlineData(WallDefenseKind.Ballista)]
    [InlineData(WallDefenseKind.Trebuchet)]
    [InlineData(WallDefenseKind.None)]
    public void ControlledMixedAssaultRecordsHitsMissesAndReloadOpportunities(WallDefenseKind weapon)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y);
        wall.Defense = weapon;
        var profile = WallDefenses.Get(weapon);
        Vector2 point = weapon is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks
            ? DropAttackLayout.Footprints(wall, weapon)[0].Center
            : wall.Center + new Vector2(0, -(float)Math.Max(6, profile.MinimumRange + 2));
        if (weapon is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
        {
            var footprint = DropAttackLayout.Footprints(wall, weapon)[0];
            point = footprint.Center - footprint.Forward * .25f;
        }
        int fired = 0, hits = 0, kills = 0;
        double damage = 0;
        foreach (var species in new[] { SiegeEnemyKind.Goblin, SiegeEnemyKind.Orc, SiegeEnemyKind.StoneTroll })
        {
            var definition = SiegeEnemies.Get(species);
            int tier = species == SiegeEnemyKind.Goblin ? 1 : species == SiegeEnemyKind.Orc ? 2 : 4;
            var enemy = new SiegeEnemy { Id = state.NextEnemyId++, X = point.X, Y = point.Y,
                Kind = species, Tier = tier, Health = definition.MaxHealth(tier, 1), MaxHealth = definition.MaxHealth(tier, 1) };
            state.EnemyList.Clear(); state.EnemyList.Add(enemy); state.ProjectileList.Clear();
            double next = 0;
            for (int step = 0; step < 300 && enemy.Health > 0; step++)
            {
                double time = step * .1;
                state.AdvanceCommittedAttacks(game, .1);
                if (time + 1e-8 >= next && state.ReleaseMountedAttack(game, wall, weapon))
                { fired++; next = time + profile.ReloadSeconds; }
            }
            damage += enemy.MaxHealth - enemy.Health;
            if (enemy.Health < enemy.MaxHealth) hits++;
            if (enemy.Health <= 0) kills++;
            output.WriteLine($"{weapon} against {species}: hp={enemy.Health:0.0}/{enemy.MaxHealth:0.0}, " +
                $"interval={profile.ReloadSeconds:0.0}s, damage={enemy.MaxHealth - enemy.Health:0.0}");
        }
        output.WriteLine($"CONTROLLED {weapon}: fired={fired} damaged-species={hits}/3 kills={kills}/3 total-damage={damage:0.0}; " +
            $"cost={profile.Cost}; no hidden health, range, resistance or accuracy adjustments.");
        Assert.Equal(3, hits);
        Assert.True(fired > 0 && damage > 0);
        Assert.True(fired <= 3 * (1 + Math.Ceiling(30 / profile.ReloadSeconds)));
    }
}
