using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementFootpathTests
{
    private static readonly CellRect Bounds = new(10, 25, 40, 32);

    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 80, MapHeight = 72, StartingMoney = 100_000,
        });
        TravelRoutes.FootpathJourneys(game.Map).ToArray();
        Assert.True(game.FoundSettlement(Bounds).Success);
        foreach (var p in new Pos[] { new(15, 30), new(20, 30), new(26, 31) })
        {
            game.Map.SetZone(p.X, p.Y, ZoneType.Residential);
            game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Cottage"), CellRect.Single(p));
            game.Map.SetHousehold(p.X, p.Y, new(2, 2, 0));
        }
        game.Map.SetZone(24, 48, ZoneType.Industrial);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Workshop"), new(24, 48, 1, 1));
        game.Touch();
        return game;
    }

    private static bool Interior(Pos p) => p.X > Bounds.X && p.X < Bounds.Right &&
        p.Y > Bounds.Y && p.Y < Bounds.Bottom;

    [Fact]
    public void NearbyRoadsDoNotReplaceBuildingToBuildingJourneysInsideTheBailey()
    {
        var game = Founded();
        var map = game.Map;
        Assert.True(map.RoadCount > 0);
        var journeys = TravelRoutes.FootpathJourneys(map).ToArray();
        Assert.NotEmpty(journeys);
        Assert.All(journeys, journey =>
        {
            Assert.All(journey, cell => Assert.True(Interior(map.PosOf(cell))));
            Assert.NotNull(map.BuildingAt(journey[0] % map.Width, journey[0] / map.Width));
            Assert.NotNull(map.BuildingAt(journey[^1] % map.Width, journey[^1] / map.Width));
            Assert.NotEqual(map.BuildingAnchor(journey[0]), map.BuildingAnchor(journey[^1]));
            Assert.DoesNotContain(journey, cell => map.HasRoad(cell % map.Width, cell / map.Width));
        });
        Assert.Contains(journeys, journey => map.BuildingAt(journey[0] % map.Width, journey[0] / map.Width)!.Zone ==
            ZoneType.Residential && map.BuildingAt(journey[^1] % map.Width, journey[^1] / map.Width)!.Zone == ZoneType.Residential);
    }

    [Fact]
    public void PathsWearGraduallyAndConnectAllReachableBuildingsWithoutCrossingWalls()
    {
        var game = Founded();
        var map = game.Map;
        var journeys = TravelRoutes.FootpathJourneys(map).ToArray();
        ulong rng = game.Rng.State;
        Assert.True(TravelRoutes.WearPaths(map));
        Assert.InRange(map.PathLayer.Count(wear => wear > 0), 1, journeys.Length);
        for (int day = 0; day < 80; day++) TravelRoutes.WearPaths(map);
        Assert.Equal(rng, game.Rng.State);
        Assert.Contains(map.PathLayer, wear => wear >= 3);
        Assert.All(Enumerable.Range(0, map.PathLayer.Length).Where(i => map.PathLayer[i] > 0),
            cell => Assert.True(Interior(map.PosOf(cell))));
        var connected = new HashSet<int> { map.BuildingFootprints.Keys.Min() };
        bool expanded;
        do
        {
            expanded = false;
            foreach (var journey in journeys)
            {
                int first = map.BuildingAnchor(journey[0]), last = map.BuildingAnchor(journey[^1]);
                if (connected.Contains(first)) expanded |= connected.Add(last);
                if (connected.Contains(last)) expanded |= connected.Add(first);
            }
        } while (expanded);
        Assert.Equal(map.BuildingFootprints.Count, connected.Count);
        var strokes = FootpathLayout.Strokes(map);
        Assert.NotEmpty(strokes);
        Assert.All(strokes, stroke =>
        {
            Assert.False(stroke.StartsAtRoad);
            Assert.False(stroke.EndsAtRoad);
            Assert.All(stroke.Points, p => Assert.True(p.X > Bounds.X && p.X < Bounds.Right &&
                p.Y > Bounds.Y && p.Y < Bounds.Bottom));
        });
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(map.PathLayer, loaded.Map.PathLayer);
        Assert.Equal(map.PathLinkLayer, loaded.Map.PathLinkLayer);
        Assert.Equal(journeys.SelectMany(p => p), TravelRoutes.FootpathJourneys(loaded.Map).SelectMany(p => p));
    }

    [Fact]
    public void ExteriorBuildingsCannotPullBaileyPathsThroughTheWalls()
    {
        var game = Founded();
        game.Map.SetZone(6, 30, ZoneType.Residential);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Cottage"), new(6, 30, 1, 1));
        game.Touch();
        var journeys = TravelRoutes.FootpathJourneys(game.Map).ToArray();
        Assert.NotEmpty(journeys);
        Assert.All(journeys, journey => Assert.All(journey,
            cell => Assert.True(Interior(game.Map.PosOf(cell)))));
        for (int day = 0; day < 40; day++) TravelRoutes.WearPaths(game.Map);
        Assert.All(Enumerable.Range(0, game.Map.PathLayer.Length).Where(cell => game.Map.PathLayer[cell] > 0),
            cell => Assert.True(Interior(game.Map.PosOf(cell))));
    }

    [Fact]
    public void LoadedBaileysTrimOldOutboundTrailsAndDoNotRegenerateThem()
    {
        var game = Founded();
        var map = game.Map;
        for (int y = 21; y <= 27; y++)
        {
            map.SetPathWear(15, y, 3);
            if (y > 21) map.LinkPathCells(map.Index(15, y - 1), map.Index(15, y));
        }
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(3, loaded.Map.PathWearAt(15, 27));
        for (int y = 21; y <= Bounds.Y; y++)
        {
            Assert.Equal(0, loaded.Map.PathWearAt(15, y));
            Assert.Equal(0, loaded.Map.PathLinkLayer[map.Index(15, y)]);
        }
        for (int day = 0; day < 30; day++) TravelRoutes.WearPaths(loaded.Map);
        Assert.All(Enumerable.Range(0, map.PathLayer.Length).Where(i => loaded.Map.PathLayer[i] > 0),
            cell => Assert.True(Interior(loaded.Map.PosOf(cell))));
    }
}
