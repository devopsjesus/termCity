using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class GoblinCrowdTests
{
    private static CityGame Swarm(int tier = 1)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 100, MapHeight = 80, StartingMoney = 100_000,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0, FloodChancePerWeek = 0,
            OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        Assert.True(game.FoundSettlement(new(20, 25, 32, 32)).Success);
        game.HighestPopulation = tier == 5 ? 2_500 : 100;
        game.HighestMilestone = 100;
        Assert.True(game.Settlement!.SpawnWave(game, 120));
        return game;
    }

    private static void Spaced(SettlementState state, double fraction = 1)
    {
        for (int first = 0; first < state.Goblins.Count; first++)
            for (int last = first + 1; last < state.Goblins.Count; last++)
            {
                var a = state.Goblins[first];
                var b = state.Goblins[last];
                double ax = a.MotionInitialized ? a.PreviousX + (a.X - a.PreviousX) * fraction : a.X;
                double ay = a.MotionInitialized ? a.PreviousY + (a.Y - a.PreviousY) * fraction : a.Y;
                double bx = b.MotionInitialized ? b.PreviousX + (b.X - b.PreviousX) * fraction : b.X;
                double by = b.MotionInitialized ? b.PreviousY + (b.Y - b.PreviousY) * fraction : b.Y;
                double distance = Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
                Assert.True(distance + 1e-7 >= a.CollisionRadius + b.CollisionRadius,
                    $"Goblins {a.Id}/{b.Id} overlap at blend {fraction}: distance {distance}, " +
                    $"required {a.CollisionRadius + b.CollisionRadius}.");
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void FullSwarmsStaySeparatedAtSpawnAndThroughoutInterpolatedMovement(int tier)
    {
        var game = Swarm(tier);
        var state = game.Settlement!;
        Assert.Equal(120, state.Goblins.Count);
        Assert.Equal(120, state.Goblins.Select(g => (g.X, g.Y)).Distinct().Count());
        Spaced(state);
        foreach (var wall in state.Walls) wall.IsTower = false;
        bool reachedWall = false;
        for (int step = 0; step < 80; step++)
        {
            game.Update(0.2);
            foreach (double fraction in new[] { 0d, 0.25, 0.5, 0.75, 1 }) Spaced(state, fraction);
            reachedWall |= state.Walls.Any(wall => wall.Health < wall.MaxHealth);
        }
        Assert.True(reachedWall);
    }

    [Fact]
    public void QueuedAttackersFindOpenWallFrontagesInsteadOfStandingBehindTheSameTarget()
    {
        var game = Swarm();
        var state = game.Settlement!;
        state.GoblinList.Clear();
        foreach (var wall in state.Walls) wall.IsTower = false;
        var target = new Pos(30, 25);
        for (int n = 0; n < 3; n++)
            state.GoblinList.Add(new Goblin
            {
                Id = n + 1, X = 30.5, Y = 24.5 - n, Health = 96, MaxHealth = 96, Tier = 1,
                Route = Enumerable.Range(0, n + 1).Select(i => new Pos(30, 24 - n + i)).Append(target).ToList(),
            });
        var attacked = new HashSet<int>();
        for (int n = 0; n < 140; n++)
        {
            state.Advance(game, 0.1);
            foreach (var unit in state.Goblins)
                if (unit.AttackCooldown > 0) attacked.Add(unit.Id);
            Spaced(state, 0.5);
            Spaced(state);
        }
        Assert.Equal(3, attacked.Count);
        Assert.Contains(state.Goblins, unit => unit.Route[^1] != target);
        var template = new SettlementState(state.Bounds);
        for (int n = 0; n < state.Walls.Count; n++) state.WallList[n].IsTower = template.Walls[n].IsTower;
        var copy = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        for (int n = 0; n < 10; n++)
        {
            state.Advance(game, 0.1);
            copy.Settlement!.Advance(copy, 0.1);
        }
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(copy));
    }

    [Fact]
    public void BlockedGoblinsQueueInsteadOfStackingOrAttackingThroughTheFrontRank()
    {
        var game = Swarm();
        var state = game.Settlement!;
        state.GoblinList.Clear();
        foreach (var wall in state.Walls) wall.IsTower = false;
        var target = state.Walls.Single(wall => wall.Cell == new Pos(30, 25));
        Goblin Unit(int id, double y) => new()
        {
            Id = id, X = 30.5, Y = y, Health = 500, MaxHealth = 500, Tier = 1,
            Route = [new(30, 24), target.Cell],
        };
        var front = Unit(1, 24.5);
        var rear = Unit(2, 23.5);
        state.GoblinList.AddRange([front, rear]);
        for (int n = 0; n < 10; n++) game.Update(0.2);
        Spaced(state);
        Assert.Equal(target.MaxHealth - 6, target.Health);
        Assert.True(rear.Y < front.Y - 0.9);
        Assert.Equal(0, SiegeMotion.Goblin(state, rear).Attack);
        game.Update(0.2);
        Assert.Equal(target.MaxHealth - 12, target.Health);
    }

    [Fact]
    public void SavesRemainDeterministicAndOldStackedAttackersAreSafelySeparatedOnLoad()
    {
        var game = Swarm();
        var state = game.Settlement!;
        var first = state.GoblinList[0];
        var second = state.GoblinList[1];
        second.X = first.X;
        second.Y = first.Y;
        second.Route = new(first.Route);
        second.RouteIndex = first.RouteIndex;
        int count = state.Goblins.Count;
        ulong random = game.Rng.State;
        var migrated = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(count, migrated.Settlement!.Goblins.Count);
        Assert.Equal(random, migrated.Rng.State);
        Spaced(migrated.Settlement);
        string saved = SaveGameStore.Serialize(migrated);
        var copy = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(copy));
        for (int n = 0; n < 30; n++) { migrated.Update(0.2); copy.Update(0.2); }
        Assert.Equal(SaveGameStore.Serialize(migrated), SaveGameStore.Serialize(copy));
        Spaced(migrated.Settlement);
    }

    [Fact]
    public void ForcedSwarmWithoutEnoughPhysicalSpaceFailsWithoutReplacingTheCurrentWave()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = Swarm();
        Assert.True(game.DebugRemoveGoblins().Success);
        var state = game.Settlement!;
        var water = game.Map.Content.Terrains.Get("Water");
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
                if (!state.Bounds.Contains(new(x, y)) && (x != 30 || y is not (23 or 24)))
                    game.Map.SetTerrain(x, y, water);
        game.Touch();
        string saved = SaveGameStore.Serialize(game);
        Assert.False(game.DebugStartSiegeWave(3).Success);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }
}
