using System.Numerics;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class OrcDefenseRallyTests
{
    private static CityGame Garrison()
    {
        var game = SettlementGarrisonTests.Founded();
        SettlementGarrisonTests.PlaceGarrison(game, "Archer Barracks");
        SettlementGarrisonTests.PlaceGarrison(game, "Guard Post");
        for (int tick = 0; tick < 70; tick++) game.Settlement!.AdvanceDefenders(game, .1);
        Assert.Equal(8, game.Settlement!.Defenders.Count);
        game.SetFunding(ServiceKind.Defence, 0);
        return game;
    }

    private static void Warn(CityGame game, BaileyGateSide side)
    {
        game.HighestPopulation = 250;
        game.HighestMilestone = 250;
        var state = game.Settlement!;
        state.OrcsIntroduced = false;
        state.PlannedOrcWave = state.WaveNumber + 1;
        state.PlannedOrcSide = side;
    }

    private static void AssignedToSide(SettlementState state, BaileyGateSide side)
    {
        foreach (var unit in state.Defenders.Where(unit => unit.Order == DefenderOrder.Automatic))
        {
            var post = Assert.Single(state.Walls, wall => wall.Id == unit.PatrolWallId);
            Assert.True(state.OnApproachSide(post, side), $"{unit.Kind} {unit.Id}: post={post.Cell}, side={side}");
            Assert.NotNull(unit.PathTarget);
        }
    }

    [Theory]
    [InlineData(BaileyGateSide.Top)]
    [InlineData(BaileyGateSide.Right)]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Left)]
    public void WarningRedirectsBothRostersAndTheyActuallyReachTheThreatenedWall(BaileyGateSide side)
    {
        var game = Garrison();
        var state = game.Settlement!;
        Warn(game, side);
        state.AdvanceDefenders(game, .1);
        for (int tick = 0; tick < 1800; tick++)
        {
            var before = state.Defenders.ToDictionary(unit => unit.Id,
                unit => new Vector2((float)unit.X, (float)unit.Y));
            state.AdvanceDefenders(game, .1);
            foreach (var unit in state.Defenders)
                Assert.InRange(Vector2.Distance(before[unit.Id], new((float)unit.X, (float)unit.Y)),
                    0, (float)SettlementDefender.MovementSpeed * .1f + .00001f);
        }
        AssignedToSide(state, side);
        foreach (var unit in state.Defenders)
        {
            var post = state.Walls.Single(wall => wall.Id == unit.PatrolWallId);
            Assert.Equal(unit.Kind == DefenderKind.Archer, unit.Elevation > .5f);
            Assert.True(Vector2.Distance(new((float)unit.X, (float)unit.Y), post.Center) <= 6,
                $"{unit.Kind} {unit.Id} stopped at {unit.X:F2},{unit.Y:F2}, z={unit.Elevation:F2}, " +
                $"post={post.Cell}, goal={unit.PathTarget}, path={unit.PathIndex}/{unit.Path.Count}.");
            if (unit.Kind == DefenderKind.Soldier)
                Assert.True(state.EstablishedAt(new((int)unit.X, (int)unit.Y)));
        }
    }

    [Fact]
    public void LaterOrcWavesRallyPatrolsWithoutExcludingOtherEnemyFronts()
    {
        var game = Garrison();
        var state = game.Settlement!;
        state.OrcsIntroduced = true;
        var top = state.Walls.First(wall => wall.Cell.Y == state.Bounds.Y && !wall.IsTower);
        var bottom = state.Walls.First(wall => wall.Cell.Y == state.Bounds.Bottom && !wall.IsTower);
        var orc = new SiegeEnemy { Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2,
            X = top.Center.X, Y = top.Center.Y - 6, Health = 160, MaxHealth = 160, Route = [top.Cell] };
        var goblin = new SiegeEnemy { Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Goblin, Tier = 1,
            X = bottom.Center.X, Y = bottom.Center.Y + 1.6, Health = 96, MaxHealth = 96, Route = [bottom.Cell] };
        state.EnemyList.Add(orc);
        state.EnemyList.Add(goblin);
        state.AdvanceDefenders(game, .1);
        Assert.Equal(BaileyGateSide.Top, state.DefenderStagingSide);
        AssignedToSide(state, BaileyGateSide.Top);
        Assert.Contains(state.Defenders, unit => unit.Kind == DefenderKind.Archer && unit.TargetEnemyId == orc.Id);
        Assert.Contains(state.Defenders, unit => unit.Kind == DefenderKind.Archer && unit.TargetEnemyId == goblin.Id);
        orc.Health = 0;
        state.EnemyList.Add(new SiegeEnemy { Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2,
            X = bottom.Center.X, Y = bottom.Center.Y + 6, Health = 160, MaxHealth = 160, Route = [bottom.Cell] });
        state.AdvanceDefenders(game, .1);
        Assert.Equal(BaileyGateSide.Bottom, state.DefenderStagingSide);
        AssignedToSide(state, BaileyGateSide.Bottom);
    }

    [Fact]
    public void ExplicitMoveOrdersAreNotOverwrittenByAnOrcWarning()
    {
        var game = Garrison();
        var state = game.Settlement!;
        var manual = state.Defenders.GroupBy(unit => unit.Kind).Select(group => group.First()).ToArray();
        var destination = state.Bounds.Cells().First(cell => state.ContainsInterior(cell) &&
            game.Map.BuildingAt(cell.X, cell.Y) is null && manual.All(unit =>
                Vector2.Distance(new((float)unit.X, (float)unit.Y), new(cell.X + .5f, cell.Y + .5f)) > 12 &&
                state.DefenderCanReach(game, unit, cell)));
        Assert.True(state.CommandDefenders(game, manual.Select(unit => unit.Id).ToArray(), destination, null).Success);
        Warn(game, BaileyGateSide.Left);
        state.AdvanceDefenders(game, .1);
        Assert.All(manual, unit =>
        {
            Assert.Equal(DefenderOrder.Move, unit.Order);
            Assert.Equal(destination, unit.Destination);
            Assert.Equal(destination, unit.PathTarget);
        });
        AssignedToSide(state, BaileyGateSide.Left);
    }

    [Fact]
    public void ALoadedWarningReassignsOldPatrolsWithoutNewSaveFields()
    {
        var game = Garrison();
        Warn(game, BaileyGateSide.Left);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        loaded.Paused = false;
        loaded.Settlement!.AdvanceDefenders(loaded, .1);
        AssignedToSide(loaded.Settlement, BaileyGateSide.Left);
    }
}
