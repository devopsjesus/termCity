using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;
using TermCity.Core.Util;
using System.Reflection;

namespace TermCity.Tests;

public class SimulationSnapshotTests
{
    [Fact]
    public void SupersededPresentationPacketsCannotReplaceTheCurrentScene()
    {
        var game = TestCity.Flat();
        var view = new CellRect(10, 10, 20, 20);
        var worker = game.CreateSimulationSnapshot();
        var roads = new RoadMeshLayout(worker.Map, RoadCurves.Extract(worker.Map, (_, _) => false));
        var packet = PreparedPathScene.Create(worker, new(view, roads));
        game.Map.SetRoad(15, 15, !game.Map.HasRoad(15, 15));
        Assert.False(game.TryPublishPreparedPaths(packet));
        Assert.Null(game.TakePreparedPaths(view));
        var current = game.CreateSimulationSnapshot();
        var updated = PreparedPathScene.Create(current, new(view,
            new RoadMeshLayout(current.Map, RoadCurves.Extract(current.Map, (_, _) => false))));
        Assert.True(game.TryPublishPreparedPaths(updated));
        Assert.Same(updated, game.TakePreparedPaths(view));
        Assert.Null(game.TakePreparedPaths(view));
    }

    [Fact]
    public void CompactPublicationsPreserveLivingActorAndProjectileIdentities()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        game.Update(.2);
        var state = game.Settlement!;
        var enemy = state.Enemies.First();
        var unit = state.Defenders.First();
        var shot = new SiegeProjectile { Id = state.NextAttackId++, TargetId = enemy.Id, Windup = .05, TravelSeconds = .1 };
        state.ProjectileList.Add(shot);
        var worker = game.CreateSimulationSnapshot();
        worker.Settlement!.Enemies.Single(copy => copy.Id == enemy.Id).Health--;
        worker.Settlement.Defenders.Single(copy => copy.Id == unit.Id).AttackCooldown = .25;
        worker.Settlement.Projectiles.Single(copy => copy.Id == shot.Id).Elapsed = .04;
        game.PublishSimulationSnapshot(worker, [], copyMap: false);
        Assert.Same(enemy, state.Enemies.Single(copy => copy.Id == enemy.Id));
        Assert.Same(unit, state.Defenders.Single(copy => copy.Id == unit.Id));
        Assert.Same(shot, state.Projectiles.Single(copy => copy.Id == shot.Id));
        Assert.Equal(worker.Settlement.Enemies.Single(copy => copy.Id == enemy.Id).Health, enemy.Health);
        Assert.Equal(.25, unit.AttackCooldown);
        Assert.Equal(.04, shot.Elapsed);
    }

    [Fact]
    public void RemovedTowerInvalidatesCachedDefenderSupportsBeforeSnapshotCapture()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        var state = game.Settlement!;
        state.PrepareGarrisonNavigation(game);
        var tower = state.Walls.First(wall => wall.IsTower);
        state.RemoveWall(tower);
        var snapshot = game.CreateSimulationSnapshot();
        Assert.DoesNotContain(snapshot.Settlement!.Walls, wall => wall.Id == tower.Id);
    }

    [Fact]
    public void CompactPublicationsDoNotRepublishConsumedPathPackets()
    {
        var game = TestCity.Flat();
        var worker = game.CreateSimulationSnapshot();
        var view = new CellRect(10, 10, 20, 20);
        var roads = new RoadMeshLayout(worker.Map, RoadCurves.Extract(worker.Map, (_, _) => false));
        worker.PreparePathPresentation(new(view, roads));
        var packet = worker.TakePreparedPaths(view);
        Assert.NotNull(packet);
        game.PublishSimulationSnapshot(worker, [], copyMap: false, preparedPaths: packet);
        Assert.Same(packet, game.TakePreparedPaths(view));
        game.PublishSimulationSnapshot(worker, [], copyMap: false);
        Assert.Null(game.TakePreparedPaths(view));
    }

    [Fact]
    public void CachedWalkwayLaddersReferOnlyToTheirOwnSnapshotWalls()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        var original = game.Settlement!;
        var tower = original.Walls.First(wall => wall.IsTower && original.TowerWalkwayLadders(wall).Count > 0);
        var snapshot = game.CreateSimulationSnapshot().Settlement!;
        var copied = snapshot.Walls.Single(wall => wall.Id == tower.Id);
        var ladders = snapshot.TowerWalkwayLadders(copied);
        Assert.NotEmpty(ladders);
        Assert.All(ladders, ladder =>
        {
            Assert.Same(snapshot, ladder.Walkway.Settlement);
            Assert.Same(snapshot.Walls.Single(wall => wall.Id == ladder.Walkway.Id), ladder.Walkway);
        });
    }

    [Fact]
    public void SettlementSnapshotPublicationCopiesEveryRuntimeScalarAndPreservesWallObjects()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        game.Paused = false;
        for (int step = 0; step < 30; step++) game.Update(.05);
        var snapshot = game.CreateSimulationSnapshot();
        var originalState = game.Settlement!;
        var wall = originalState.Walls[0];
        snapshot.Update(.05);
        game.PublishSimulationSnapshot(snapshot, []);
        Assert.Same(originalState, game.Settlement);
        Assert.Same(wall, game.Settlement!.Walls[0]);
        EqualScalars(snapshot.Settlement!, game.Settlement);
        foreach (var expected in snapshot.Settlement!.Walls)
            EqualScalars(expected, game.Settlement.Walls.Single(actual => actual.Id == expected.Id));

        static void EqualScalars<T>(T expected, T actual) where T : class
        {
            foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                    Assert.True(Equals(field.GetValue(expected), field.GetValue(actual)),
                        $"Snapshot publication lost {typeof(T).Name}.{field.Name}.");
        }
    }

    [Fact]
    public void ImmutableElevationSamplesFollowTerrainVersionsWithoutChangingEarlierSnapshots()
    {
        var original = TestCity.Flat();
        var first = TerrainSprite.CaptureElevations(original.Map);
        Assert.Same(first, TerrainSprite.CaptureElevations(original.Map));
        Assert.Throws<ArgumentOutOfRangeException>(() => first[first.Width, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => first[-1, 0]);
        var snapshot = original.CreateSimulationSnapshot();
        Assert.Same(first, TerrainSprite.CaptureElevations(snapshot.Map));
        snapshot.Map.SetTerrain(10, 10, snapshot.Map.Content.Terrains.Get("Water"));
        var changed = TerrainSprite.CaptureElevations(snapshot.Map);
        Assert.NotSame(first, changed);
        Assert.Equal(0, first[10, 10]);
        Assert.Equal(-.04f, changed[10, 10]);
        Assert.Same(first, TerrainSprite.CaptureElevations(original.Map));
    }

    [Fact]
    public void PreparedPathGeometryMatchesTheSerialLayoutAndIsConsumedOnce()
    {
        var original = TestCity.Flat();
        var map = original.Map;
        var cottage = map.Content.Buildings.Get("Cottage");
        map.SetBuildingFootprint(cottage, new(20, 20, 1, 1));
        map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(26, 20, 1, 1));
        map.SetZone(20, 21, ZoneType.Residential);
        for (int day = 0; day < 4; day++) _ = TravelRoutes.AdvancePathWear(map, out _);
        string before = SaveGameStore.Serialize(original);
        var snapshot = original.CreateSimulationSnapshot();
        var serial = original.CreateSimulationSnapshot();
        var view = new CellRect(10, 10, 30, 30);
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => false));
        var request = new PathSceneRequest(view, roads);
        snapshot.PreparePathPresentation(request);
        var expected = PreparedPathScene.Create(serial, request);
        var prepared = snapshot.TakePreparedPaths(view);
        Assert.NotNull(prepared);
        Assert.Equal(expected.Yards.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X),
            prepared.Yards.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X));
        Assert.Equal(expected.Footpaths.Surfaces.SelectMany(surface => surface.Polygons).SelectMany(polygon => polygon),
            prepared.Footpaths.Surfaces.SelectMany(surface => surface.Polygons).SelectMany(polygon => polygon));
        Assert.Null(snapshot.TakePreparedPaths(view));
        Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(snapshot));
        Assert.Equal(before, SaveGameStore.Serialize(original));
    }

    [Fact]
    public void PublicationRetainsTheVisibleRoadNetworkWhenRoadsHaveNotChanged()
    {
        var original = TestCity.Flat();
        var roads = new RoadVectorLayer();
        Assert.True(roads.Sync(original));
        var network = original.Network;
        var snapshot = original.CreateSimulationSnapshot();
        snapshot.AdvanceDay();
        original.PublishSimulationSnapshot(snapshot, []);
        Assert.Same(network, original.Network);
        Assert.False(roads.Sync(original));
    }

    [Theory]
    [InlineData(CityScenario.Random)]
    [InlineData(CityScenario.SanFrancisco)]
    public async Task IsolatedDayUpdatesMatchSerialStateAndDoNotMutatePresentation(CityScenario scenario)
    {
        var original = CityGame.New(new GameConfig { Seed = 42, Scenario = scenario, Rules = CityRules.Full });
        original.Paused = false;
        var serial = original.CreateSimulationSnapshot();
        _ = original.Stats;
        _ = original.Services;
        _ = original.Indicators;
        var map = original.Map;
        for (int day = 0; day < 9; day++)
        {
            string before = SaveGameStore.Serialize(original);
            var snapshot = original.CreateSimulationSnapshot();
            var job = Task.Run(() =>
            {
                snapshot.AdvanceDay();
                _ = snapshot.Stats;
                _ = snapshot.Services;
                _ = snapshot.Indicators;
            });
            _ = original.Stats;
            _ = original.Services;
            _ = original.Indicators;
            _ = FootpathLayout.Strokes(original.Map);
            await job;
            Assert.Equal(before, SaveGameStore.Serialize(original));
            serial.AdvanceDay();
            Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(snapshot));
            Assert.Equal(serial.Rng.State, snapshot.Rng.State);
            original.PublishSimulationSnapshot(snapshot, []);
            Assert.Same(map, original.Map);
            Assert.Equal(SaveGameStore.Serialize(serial), SaveGameStore.Serialize(original));
        }
    }

    [Fact]
    public void SnapshotsOwnMutableMapAndFundingState()
    {
        var original = TestCity.Flat();
        _ = TravelRoutes.NearestRoad(original.Map);
        _ = TravelRoutes.Compute(original.Map, [original.Map.Index(10, 10)]);
        _ = original.Network.RoadAtCellCenter(10, 20);
        string before = SaveGameStore.Serialize(original);
        var snapshot = original.CreateSimulationSnapshot();
        snapshot.Map.SetRoad(10, 10, true);
        snapshot.Map.SetZone(12, 12, ZoneType.Residential);
        snapshot.Map.SetPathWear(14, 14, 3);
        snapshot.Budget.Roads = .25;
        snapshot.Taxes.Residential = .1;
        _ = snapshot.Network.RoadAtCellCenter(10, 20);
        Assert.Equal(before, SaveGameStore.Serialize(original));
        Assert.NotSame(original.Map.TravelStepCosts, snapshot.Map.TravelStepCosts);
    }

    [Fact]
    public void FortifiedSnapshotsOwnWallsEnemiesAndTransientCombatState()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        Assert.True(game.CanSnapshotSimulation);
        string before = SaveGameStore.Serialize(game);
        var snapshot = game.CreateSimulationSnapshot();
        Assert.NotSame(game.Settlement, snapshot.Settlement);
        Assert.NotSame(game.Settlement!.Walls[0], snapshot.Settlement!.Walls[0]);
        snapshot.Settlement.Walls[0].Health -= 10;
        snapshot.Settlement.EnemyList[0].X += .1;
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }
}
