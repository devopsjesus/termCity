using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class TravelCacheTests
{
    [Fact]
    public void CompactionWithoutAnyStrictImprovementRetainsImmutableField()
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 81, MapHeight = 49 }).Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        int cell = map.Index(20, 20);
        map.PathLayer[cell] = 3;
        var before = TravelRoutes.Compute(map, [map.Index(5, 5)]);
        var costs = (double[])before.Cost.Clone();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        TravelRoutes.UpdateCompactedPaths(map, [cell]);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Same(before, TravelRoutes.Compute(map, [map.Index(5, 5)]));
        Assert.Equal(costs, before.Cost);
        Assert.True(allocated < 16 * 1024, $"A no-op route repair allocated {allocated} bytes.");
    }

    [Fact]
    public void CompactedSourceStillRepairsOutgoingEdges()
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 81, MapHeight = 49 }).Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        int source = map.Index(20, 20);
        var before = TravelRoutes.Compute(map, [source]);
        var costs = (double[])before.Cost.Clone();
        map.PathLayer[source] = 3;
        TravelRoutes.UpdateCompactedPaths(map, [source]);
        var repaired = TravelRoutes.Compute(map, [source]);
        Assert.NotSame(before, repaired);
        Assert.Equal(costs, before.Cost);
        map.TravelCache.Clear();
        Assert.Equal(TravelRoutes.Compute(map, [source]).Cost, repaired.Cost);
    }

    [Fact]
    public void IndependentTravelFieldRepairsMatchSerialResultsExactly()
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 160, MapHeight = 96 }).Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        _ = TravelRoutes.Compute(map, [map.Index(5, 5)]);
        _ = TravelRoutes.Compute(map, [map.Index(145, 85)]);
        var previous = map.TravelCache.ToArray();
        var changed = Enumerable.Range(6, 12).Select(x => map.Index(x, 5)).ToList();
        foreach (int cell in changed) map.PathLayer[cell] = 3;
        TravelRoutes.UpdateCompactedPaths(map, changed);
        var concurrent = map.TravelCache.ToDictionary();
        foreach (var (key, original) in previous)
        {
            map.TravelCache.Clear();
            map.TravelCache.Add(key, original);
            TravelRoutes.UpdateCompactedPaths(map, changed);
            var serial = map.TravelCache[key];
            Assert.Equal(serial.Cost, concurrent[key].Cost);
            Assert.Equal(serial.Source, concurrent[key].Source);
            Assert.Equal(serial.Next, concurrent[key].Next);
        }
    }

    [Fact]
    public void RoadlessHomeOnlyJourneysStayEmptyUntilANonResidentialDestinationExists()
    {
        var map = TestCity.Flat().Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        var cottage = map.Content.Buildings.Get("Cottage");
        map.SetBuildingFootprint(cottage, new(20, 20, 1, 1));
        map.SetBuildingFootprint(cottage, new(40, 20, 1, 1));
        _ = TravelRoutes.AdvancePathWear(map, out _);
        Assert.Empty(map.BuildingPaths!);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(30, 20, 1, 1));
        _ = TravelRoutes.AdvancePathWear(map, out _);
        Assert.NotEmpty(map.BuildingPaths!);
    }

    [Theory]
    [InlineData(160, 96)]
    [InlineData(161, 97)]
    public void LargeInfluenceCompactionTreeRepairMatchesFreshDistances(int width, int height)
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = width, MapHeight = height }).Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        int[] sources = [map.Index(5, 5)];
        for (int step = 0; step < 6; step++)
        {
            var before = TravelRoutes.Compute(map, sources);
            var original = (double[])before.Cost.Clone();
            var changed = Enumerable.Range(0, 3).Select(offset => map.Index(6 + step * 3 + offset, 5)).ToList();
            foreach (int cell in changed) map.PathLayer[cell] = 3;
            TravelRoutes.UpdateCompactedPaths(map, changed);
            Assert.True(map.LastRepairTreeMilliseconds > 0, "The fixture must exercise the large-influence fallback.");
            var repaired = TravelRoutes.Compute(map, sources);
            Assert.Equal(original, before.Cost);
            map.TravelCache.Clear();
            var fresh = TravelRoutes.Compute(map, sources);
            for (int cell = 0; cell < original.Length; cell++)
                Assert.True(Math.Abs(repaired.Cost[cell] - fresh.Cost[cell]) < .00000001 ||
                    repaired.Cost[cell] == fresh.Cost[cell],
                    $"Step={step}, cell={cell}: repaired={repaired.Cost[cell]}, fresh={fresh.Cost[cell]}");
        }
    }

    [Fact]
    public void CompactionDoesNotCloneAnUnreachableTravelField()
    {
        var map = TestCity.Flat().Map;
        var before = TravelRoutes.Compute(map, []);
        int cell = map.Index(20, 20);
        map.PathLayer[cell] = 3;
        TravelRoutes.UpdateCompactedPaths(map, [cell]);
        Assert.Same(before, TravelRoutes.Compute(map, []));
    }

    [Fact]
    public void WallTraversalCacheIsDiscardedWhenTopologyChanges()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Bottom).Game;
        var map = game.Map;
        var siege = game.Settlement!;
        int source = Enumerable.Range(0, map.Width * map.Height).First(cell => TravelRoutes.Walkable(map, cell));
        _ = TravelRoutes.Compute(map, [source]);
        var before = map.TravelWallKnown;
        Assert.NotNull(before);
        siege.InvalidateWallTopology();
        Assert.Null(map.TravelWallKnown);
        _ = TravelRoutes.Compute(map, [source]);
        Assert.NotNull(map.TravelWallKnown);
        Assert.NotSame(before, map.TravelWallKnown);
    }

    [Fact]
    public void RoadRepairDisconnectsAndReconnectsAnEntireRegion()
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 }).Map;
        foreach (int road in map.RoadCells.ToArray())
            map.SetRoad(road % map.Width, road / map.Width, false);
        map.SetRoad(5, 24, true);
        _ = TravelRoutes.NearestRoad(map);
        var cottage = map.Content.Buildings.Get("Cottage");
        for (int y = 0; y < map.Height; y++) map.SetBuilding(10, y, cottage);
        var blocked = TravelRoutes.NearestRoad(map);
        Assert.True(double.IsPositiveInfinity(blocked.Cost[map.Index(20, 24)]));
        map.PathRoutes = null;
        Assert.Equal(TravelRoutes.NearestRoad(map).Cost, blocked.Cost);
        map.SetBuilding(10, 24, null);
        var reopened = TravelRoutes.NearestRoad(map);
        Assert.True(double.IsFinite(reopened.Cost[map.Index(20, 24)]));
        map.PathRoutes = null;
        Assert.Equal(TravelRoutes.NearestRoad(map).Cost, reopened.Cost);
    }

    [Fact]
    public void RoadRepairTracksYardRegistrationFeaturesAndZoning()
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 }).Map;
        var area = new CellRect(20, 20, 1, 1);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), area);
        int owner = map.Index(20, 20), yard = map.Index(20, 21);
        map.SetZone(20, 21, ZoneType.Residential);
        _ = TravelRoutes.NearestRoad(map);
        map.RegisterHouseYard(owner, yard);
        Check();
        map.SetZone(20, 21, ZoneType.None);
        Check();
        map.SetZone(20, 21, ZoneType.Residential);
        Check();
        map.SetFeature(20, 21, map.Content.Features.Get("Tree"));
        Check();
        map.SetFeature(20, 21, null);
        Check();
        map.SetBuilding(20, 20, null);
        Check();

        void Check()
        {
            var repaired = TravelRoutes.NearestRoad(map);
            map.PathRoutes = null;
            Assert.Equal(TravelRoutes.NearestRoad(map).Cost, repaired.Cost);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void LocalBuildingRoadRepairsMatchFreshDistancesAndPreservePriorResults(int seed)
    {
        var map = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 }).Map;
        var random = new Random(seed);
        var cottage = map.Content.Buildings.Get("Cottage");
        for (int step = 0; step < 30; step++)
        {
            var before = TravelRoutes.NearestRoad(map);
            var original = (double[])before.Cost.Clone();
            for (int change = 0; change < 8; change++)
            {
                int x = random.Next(map.Width), y = random.Next(map.Height);
                if (!map.HasRoad(x, y))
                    map.SetBuilding(x, y, map.BuildingAt(x, y) is null ? cottage : null);
            }
            var repaired = TravelRoutes.NearestRoad(map);
            Assert.Equal(original, before.Cost);
            map.PathRoutes = null;
            var fresh = TravelRoutes.NearestRoad(map);
            Assert.Equal(fresh.Cost, repaired.Cost);
            Assert.Equal(fresh.Source, repaired.Source);
            Assert.Equal(fresh.Next, repaired.Next);
            for (int cell = 0; cell < repaired.Cost.Length; cell++)
                if (repaired.Next[cell] is >= 0 and var next)
                {
                    Assert.Equal(repaired.Cost[cell] - 1, repaired.Cost[next]);
                    Assert.True(TravelRoutes.CanFootpathStep(map, next, cell));
                }
        }
    }

    [Fact]
    public void FootpathStepCacheTracksTerrainChanges()
    {
        var map = TestCity.Flat().Map;
        int first = map.Index(20, 20), last = map.Index(21, 20);
        map.SetRoad(20, 20, false);
        map.SetRoad(21, 20, false);
        Assert.True(TravelRoutes.CanFootpathStep(map, first, last));
        map.SetTerrain(21, 20, map.Content.Terrains.Get("Water"));
        Assert.False(TravelRoutes.CanFootpathStep(map, first, last));
        map.SetTerrain(21, 20, map.Content.Terrains.Base);
        Assert.True(TravelRoutes.CanFootpathStep(map, first, last));
    }

    [Fact]
    public void BuildingChangesReuseRoadTargetsButRoadChangesInvalidateThem()
    {
        var map = TestCity.Flat().Map;
        var before = TravelRoutes.NearestRoad(map);
        var targets = map.FootpathRoadTargets;
        Assert.NotNull(targets);
        map.SetBuilding(10, 10, map.Content.Buildings.Get("Cottage"));
        Assert.NotSame(before, TravelRoutes.NearestRoad(map));
        Assert.Same(targets, map.FootpathRoadTargets);
        var cell = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => !map.HasRoad(p.X, p.Y));
        map.SetRoad(cell.X, cell.Y, true);
        Assert.Null(map.FootpathRoadTargets);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void CompactedPathRepairMatchesFreshRoutesWithoutMutatingPriorResults(int seed)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 });
        var map = game.Map;
        foreach (int road in map.RoadCells.ToArray()) map.SetRoad(road % map.Width, road / map.Width, false);
        var random = new Random(seed);
        int[] sources = [map.Index(5, 5), map.Index(70, 40)];
        for (int step = 0; step < 12; step++)
        {
            var before = TravelRoutes.Compute(map, sources);
            var original = (double[])before.Cost.Clone();
            var changed = Enumerable.Range(0, 20).Select(_ => random.Next(map.Width * map.Height)).Distinct().ToList();
            foreach (int cell in changed) map.PathLayer[cell] = 3;
            TravelRoutes.UpdateCompactedPaths(map, changed);
            var repaired = TravelRoutes.Compute(map, sources);
            Assert.Equal(original, before.Cost);
            map.TravelCache.Clear();
            var fresh = TravelRoutes.Compute(map, sources);
            for (int cell = 0; cell < original.Length; cell++)
                Assert.True(Math.Abs(repaired.Cost[cell] - fresh.Cost[cell]) < .00000001 ||
                    repaired.Cost[cell] == fresh.Cost[cell],
                    $"Seed={seed}, step={step}, cell={cell}: repaired={repaired.Cost[cell]}, fresh={fresh.Cost[cell]}");
        }
    }

    [Fact]
    public void SlowerPathsAndChangedRoadsInvalidateCachedStepCosts()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 });
        var map = game.Map;
        int source = map.Index(5, 5), destination = map.Index(6, 5);
        map.SetPathWear(6, 5, 3);
        double compacted = TravelRoutes.Compute(map, [source]).Cost[destination];
        map.SetPathWear(6, 5, 0);
        double ground = TravelRoutes.Compute(map, [source]).Cost[destination];
        Assert.True(ground > compacted);
        map.SetRoad(6, 5, true);
        Assert.True(TravelRoutes.Compute(map, [source]).Cost[destination] < ground);
    }

    [Fact]
    public void EmptyFootbridgeLookupDoesNotAllocateInRouteHotLoops()
    {
        var map = TestCity.Flat().Map;
        for (int i = 0; i < 100; i++) _ = map.HasFootbridge(i, i);
        long start = GC.GetAllocatedBytesForCurrentThread();
        int found = 0;
        for (int i = 0; i < 10000; i++)
            if (map.HasFootbridge(i % map.Width, i % map.Height)) found++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, found);
        Assert.Equal(0, allocated);
    }
}
