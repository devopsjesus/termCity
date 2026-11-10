using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementFootbridgeTests
{
    private static CityGame RiverTown(int width = 4)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        for (int x = 68; x < 68 + width; x++)
            for (int y = 0; y < game.Map.Height; y++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        Assert.True(game.FoundSettlement(new(40, 40, 64, 64)).Success);
        var home = game.Map.Content.Buildings.Get("Cottage");
        var site = game.VacantPlots(ZoneType.Residential).First();
        game.Map.SetBuildingFootprint(home, site);
        game.Map.SetHousehold(site.X, site.Y, new(3, 2, 0));
        game.Touch();
        return game;
    }

    [Fact]
    public void CiviliansBuildAShortBridgeThroughDailyPathWorkAndThenOpenTheOtherBank()
    {
        var game = RiverTown();
        var map = game.Map;
        var state = game.Settlement!;
        int money = game.Money, roads = map.RoadCount;
        ulong random = game.Rng.State;
        Assert.False(state.AcceptsArrivals(game, new(55, 60)));
        Assert.True(TravelRoutes.WearPaths(map));
        var bridge = Assert.Single(map.Footbridges);
        Assert.Equal(0, bridge.Progress);
        Assert.False(bridge.Complete);
        Assert.All(bridge.Cells().Skip(1).SkipLast(1), p =>
            Assert.False(TravelRoutes.Walkable(map, map.Index(p.X, p.Y))));
        for (int day = 0; day < SettlementFootbridge.ConstructionDays; day++)
        {
            Assert.True(TravelRoutes.WearPaths(map));
            game.Touch();
        }
        bridge = Assert.Single(map.Footbridges);
        Assert.True(bridge.Complete);
        Assert.True(state.AcceptsArrivals(game, new(55, 60)));
        foreach (var p in bridge.Cells().Skip(1).SkipLast(1))
        {
            Assert.Equal("Water", map.TerrainAt(p.X, p.Y).Name);
            Assert.True(TravelRoutes.Walkable(map, map.Index(p.X, p.Y)));
            Assert.False(game.CanBuildOn(p.X, p.Y));
            Assert.False(state.AcceptsArrivals(game, p));
        }
        var cells = bridge.Cells().ToArray();
        for (int i = 1; i < cells.Length; i++)
            Assert.True(TravelRoutes.CanFootpathStep(map, map.Index(cells[i - 1].X, cells[i - 1].Y),
                map.Index(cells[i].X, cells[i].Y)));
        Assert.Equal(money, game.Money);
        Assert.Equal(roads, map.RoadCount);
        Assert.Equal(random, game.Rng.State);
        Assert.True(bridge.DeckHeight(map) > WallWaterLayout.Surface(map,
            new(cells[1].X + .5f, cells[1].Y + .5f)));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier,
            X = bridge.Start.X + .5, Y = bridge.Start.Y + .5 };
        Assert.True(state.DefenderCanReach(game, unit, bridge.End));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ConstructionProgressAndCompleteCrossingsSurviveSaveLoad(int progress)
    {
        var game = RiverTown();
        for (int day = 0; day <= progress; day++) TravelRoutes.WearPaths(game.Map);
        game.Touch();
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        Assert.Equal(progress, Assert.Single(restored.Map.Footbridges).Progress);
        Assert.Equal(progress == 4, restored.Settlement!.AcceptsArrivals(restored, new(55, 60)));
        var invalid = JsonNode.Parse(saved)!;
        invalid["Footbridges"]![0]!["Progress"] = 99;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(invalid.ToJsonString()));
    }

    [Fact]
    public void WideCrossingsAndWallGratesCannotBecomeFootbridges()
    {
        var game = RiverTown(9);
        for (int day = 0; day < 8; day++) TravelRoutes.WearPaths(game.Map);
        Assert.Empty(game.Map.Footbridges);
        Assert.False(game.Settlement!.AcceptsArrivals(game, new(55, 60)));
        var bridge = new SettlementFootbridge(new(67, 39), new(72, 39), 0);
        Assert.False(SettlementFootbridges.Valid(game.Map, bridge, requireEnclosure: true));
    }

    [Fact]
    public void FootbridgePlanningDoesNotBypassAnInternalWallOrBuildWithoutResidents()
    {
        var game = RiverTown();
        var state = game.Settlement!;
        var bridge = new SettlementFootbridge(new(67, 55), new(72, 55), 0);
        Assert.True(SettlementFootbridges.Valid(game.Map, bridge, requireEnclosure: true));
        Assert.True(game.BuildWallRun(new(70.5f, 50.5f), new(70.5f, 62.5f)).Success);
        Assert.False(SettlementFootbridges.Valid(game.Map, bridge, requireEnclosure: true));
        foreach (var area in game.Map.BuildingFootprints.Values)
            game.Map.SetHousehold(area.X, area.Y, default);
        game.Touch();
        TravelRoutes.WearPaths(game.Map);
        Assert.Empty(game.Map.Footbridges);
    }

    [Fact]
    public void ConstructionPausesAtABreachAndContinuesAfterRepair()
    {
        var game = RiverTown();
        TravelRoutes.WearPaths(game.Map);
        var bridge = Assert.Single(game.Map.Footbridges);
        var wall = game.Settlement!.Walls.First(w => !w.IsGate && !w.IsTower && w.Cell.Y == 40);
        wall.Health = 0;
        TravelRoutes.WearPaths(game.Map);
        Assert.Equal(bridge.Progress, game.Map.Footbridges[0].Progress);
        Assert.Contains("paused", CellInspector.Summary(game, bridge.Start));
        wall.Health = wall.MaxHealth;
        TravelRoutes.WearPaths(game.Map);
        Assert.Equal(bridge.Progress + 1, game.Map.Footbridges[0].Progress);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(4, true)]
    public void ShorelinePrefilterPreservesExhaustiveCandidateOrder(int width, bool breach)
    {
        var game = RiverTown(width);
        if (breach)
            game.Settlement!.Walls.First(wall => !wall.IsGate && !wall.IsTower).Health = 0;
        var expected = ExhaustiveCandidate(game.Map);
        ulong random = game.Rng.State;
        bool changed = SettlementFootbridges.Advance(game.Map);
        Assert.Equal(expected is not null, changed);
        Assert.Equal(expected, game.Map.Footbridges.SingleOrDefault());
        Assert.Equal(random, game.Rng.State);
    }

    // Frozen prefilter-free planning traversal preserves both cell and neighbor tie order.
    private static SettlementFootbridge? ExhaustiveCandidate(GameMap map)
    {
        var settlement = map.Fortifications!;
        var reached = settlement.ReachableLand(map);
        foreach (var start in reached.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            if (!map.TerrainAt(start.X, start.Y).Buildable ||
                !settlement.WallNetwork.Contains(new(start.X + .5f, start.Y + .5f))) continue;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
                for (int step = 1; step <= SettlementFootbridge.MaximumWaterCells + 1; step++)
                {
                    var end = start.Offset(dx * step, dy * step);
                    if (!map.InBounds(end)) break;
                    if (map.TerrainAt(end.X, end.Y).Name == "Water") continue;
                    if (step < 2 || !map.TerrainAt(end.X, end.Y).Buildable || reached.Contains(end) ||
                        map.BuildingAt(end.X, end.Y) is not null) break;
                    var bridge = new SettlementFootbridge(start, end, 0);
                    if (SettlementFootbridges.Valid(map, bridge, requireEnclosure: true) &&
                        !map.Footbridges.Any(existing => existing.Cells().Intersect(bridge.Cells()).Any()))
                        return bridge;
                    break;
                }
        }
        return null;
    }
}
