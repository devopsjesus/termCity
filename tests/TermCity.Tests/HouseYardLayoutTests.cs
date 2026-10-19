using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class HouseYardLayoutTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void YardIsExactlyOneWholeCellOutsideTheUnchangedHousePlot(int width, int height)
    {
        var game = TestCity.Flat();
        var area = new CellRect(10, 10, width, height);
        AddHouse(game, area);
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(yard, Assert.Single(HouseYardLayout.Create(game.Map)).Value);

        Assert.Equal(new Pos(area.X + (width - 1) / 2, area.Bottom + 1), yard.Cell);
        Assert.Equal(new Pos(0, 1), yard.Direction);
        Assert.DoesNotContain(yard.Cell, area.Cells());
        Assert.Equal(1, CellRect.Single(yard.Cell).Area);
        Assert.Equal(area, Assert.Single(game.Map.BuildingFootprints).Value);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Theory]
    [InlineData(0, 10, 11)]
    [InlineData(1, 9, 10)]
    [InlineData(2, 10, 9)]
    [InlineData(3, 11, 10)]
    public void YardPrefersTheRotatedHouseFront(int rotation, int x, int y)
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        game.Map.BuildingRotations[game.Map.Index(10, 10)] = rotation;
        Assert.Equal(new Pos(x, y), Assert.Single(HouseYardLayout.Create(game.Map)).Value.Cell);
    }

    [Theory]
    [InlineData("road")]
    [InlineData("water")]
    [InlineData("tree")]
    [InlineData("building")]
    [InlineData("commercial")]
    public void YardMovesToFreeLandRatherThanCoveringAnObstacle(string obstacle)
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var map = game.Map;
        switch (obstacle)
        {
            case "road": map.SetRoad(10, 11, true); break;
            case "water": map.SetTerrain(10, 11, map.Content.Terrains.Get("Water")); break;
            case "tree": map.SetFeature(10, 11, map.Content.Features.Get("Tree")); break;
            case "building": map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(10, 11, 1, 1)); break;
            case "commercial": map.SetZone(10, 11, ZoneType.Commercial); break;
        }
        Assert.Equal(new Pos(9, 10), Assert.Single(HouseYardLayout.Create(map)).Value.Cell);
    }

    [Fact]
    public void YardRejectsActualRoadShouldersEvenWithoutLogicalRoadCells()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var path = new RoadPath(game.Map.Content.Roads.Default, true, false,
            [9 * RoadCurves.CellWidth, 12 * RoadCurves.CellWidth],
            [12 * RoadCurves.CellHeight, 12 * RoadCurves.CellHeight]);
        var roads = new RoadMeshLayout(game.Map, [path]);
        Assert.Contains(roads.AtCell(10, 11), segment => RoadMeshLayout.OverlapsCell(segment.Corridor, 10, 11));
        Assert.Equal(new Pos(9, 10), Assert.Single(HouseYardLayout.Create(game.Map, roads)).Value.Cell);
    }

    [Fact]
    public void FullyOccupiedNeighborsDoNotCreateAnOverlappingYard()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        foreach (var cell in new[] { new Pos(10, 11), new Pos(9, 10), new Pos(10, 9), new Pos(11, 10) })
            game.Map.SetFeature(cell.X, cell.Y, game.Map.Content.Features.Get("Tree"));
        Assert.Empty(HouseYardLayout.Create(game.Map));
    }

    [Fact]
    public void YardKeepsExistingFootpathsClear()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var footpaths = new FootpathSurfaceLayout(
            [new FootpathLayout.Stroke([new(10.2f, 11.5f), new(10.8f, 11.5f)], 6, false, false)], null);
        Assert.NotEmpty(footpaths.AtCell(10, 11));
        Assert.Equal(new Pos(9, 10),
            Assert.Single(HouseYardLayout.Create(game.Map, footpaths: footpaths)).Value.Cell);
    }

    [Fact]
    public void EstablishedGardenSurvivesANewPathAndPathsReachItsBoundaryWithoutWearingIt()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        TravelRoutes.NearestRoad(game.Map);
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        int yardCell = game.Map.Index(yard.Cell.X, yard.Cell.Y);
        for (int day = 0; day < 20; day++) TravelRoutes.WearPaths(game.Map);
        Assert.Equal(0, game.Map.PathLayer[yardCell]);
        var journey = Assert.Single(TravelRoutes.FootpathJourneys(game.Map));
        Assert.Equal(yardCell, journey[0]);
        var roads = new RoadMeshLayout(game.Map, RoadCurves.Extract(game.Map, (_, _) => true));
        var paths = new FootpathSurfaceLayout(game.Map, roads);
        Assert.Equal(yard, Assert.Single(HouseYardLayout.Create(game.Map, roads, paths)).Value);
        Assert.All(paths.AtCell(yard.Cell.X, yard.Cell.Y), polygon =>
            Assert.False(RoadMeshLayout.OverlapsCell(polygon, yard.Cell.X, yard.Cell.Y)));
        Assert.Contains(paths.Surfaces.SelectMany(surface => surface.Polygons).SelectMany(polygon => polygon),
            point => Math.Abs(point.Y - (yard.Cell.Y + 1)) < 0.00001f &&
                point.X > yard.Cell.X && point.X < yard.Cell.X + 1);
    }

    [Fact]
    public void NewFootpathGeometryCannotHideAnEstablishedGarden()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        var crossing = new FootpathSurfaceLayout(
            [new FootpathLayout.Stroke([new(9.5f, 11.5f), new(11.5f, 11.5f)], 6)], null);
        Assert.Contains(crossing.AtCell(yard.Cell.X, yard.Cell.Y), polygon =>
            RoadMeshLayout.OverlapsCell(polygon, yard.Cell.X, yard.Cell.Y));
        Assert.Equal(yard, Assert.Single(HouseYardLayout.Create(game.Map, footpaths: crossing)).Value);
    }

    [Fact]
    public void ExistingWearInsideAGardenIsPreservedButNotRenderedOverIt()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        for (int x = 8; x <= 12; x++) game.Map.SetPathWear(x, 11, 6);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.True(restored.Map.IsHouseYardCell(restored.Map.Index(yard.Cell.X, yard.Cell.Y)));
        var paths = new FootpathSurfaceLayout(restored.Map, null);
        Assert.Equal(6, restored.Map.PathWearAt(yard.Cell.X, yard.Cell.Y));
        Assert.All(paths.AtCell(yard.Cell.X, yard.Cell.Y), polygon =>
            Assert.False(RoadMeshLayout.OverlapsCell(polygon, yard.Cell.X, yard.Cell.Y)));
        Assert.Equal(yard, Assert.Single(HouseYardLayout.Create(restored.Map, footpaths: paths)).Value);
    }

    [Fact]
    public void RoadlessWorkplaceJourneysMayEndAtGardensButNeverCrossThem()
    {
        var game = TestCity.Flat();
        foreach (int road in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(road % game.Map.Width, road / game.Map.Width, false);
        AddHouse(game, new(10, 10, 1, 1));
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        var well = game.Map.Content.Buildings.Get("Town Well");
        game.Map.SetBuildingFootprint(well, new(5, 11, 1, 1));
        game.Map.SetBuildingFootprint(well, new(15, 11, 1, 1));
        for (int day = 0; day < 20; day++) TravelRoutes.WearPaths(game.Map);
        int garden = game.Map.Index(yard.Cell.X, yard.Cell.Y);
        Assert.Equal(0, game.Map.PathLayer[garden]);
        var journeys = TravelRoutes.FootpathJourneys(game.Map).ToArray();
        Assert.NotEmpty(journeys);
        Assert.All(journeys, journey => Assert.DoesNotContain(garden, journey.Skip(1).SkipLast(1)));
        var paths = new FootpathSurfaceLayout(game.Map, null);
        Assert.Equal(yard, Assert.Single(HouseYardLayout.Create(game.Map, footpaths: paths)).Value);
    }

    [Fact]
    public void HidingOrRestoringAGardenInvalidatesFootpathRoutesAndDemolitionClearsItsOwner()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var yard = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        int garden = game.Map.Index(yard.Cell.X, yard.Cell.Y);
        var protectedRoutes = TravelRoutes.NearestRoad(game.Map);
        Assert.True(double.IsPositiveInfinity(protectedRoutes.Cost[garden]));
        game.Map.SetZone(yard.Cell.X, yard.Cell.Y, ZoneType.None);
        var unprotected = TravelRoutes.NearestRoad(game.Map);
        Assert.NotSame(protectedRoutes, unprotected);
        Assert.True(double.IsFinite(unprotected.Cost[garden]));
        game.Map.SetZone(yard.Cell.X, yard.Cell.Y, ZoneType.Residential);
        Assert.True(double.IsPositiveInfinity(TravelRoutes.NearestRoad(game.Map).Cost[garden]));
        game.Map.SetBuilding(10, 10, null);
        Assert.False(game.Map.IsHouseYardCell(garden));
    }

    [Fact]
    public void DevelopingTheYardHidesItWithoutRelocationAndDemolitionRemovesItsAssignment()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        Assert.Equal(new Pos(10, 11), Assert.Single(HouseYardLayout.Create(game.Map)).Value.Cell);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Town Well"), new(10, 11, 1, 1));
        Assert.Empty(HouseYardLayout.Create(game.Map));
        Assert.Equal(game.Map.Index(10, 11), Assert.Single(game.Map.HouseYardCells).Value);
        game.Map.SetBuilding(10, 11, null);
        Assert.Equal(new Pos(10, 11), Assert.Single(HouseYardLayout.Create(game.Map)).Value.Cell);
        game.Map.SetBuilding(10, 10, null);
        Assert.Empty(HouseYardLayout.Create(game.Map));
        Assert.Empty(game.Map.HouseYardCells);
    }

    [Fact]
    public void AdjacentHousesNeverShareAYardAndReloadPreservesItsAppearance()
    {
        var game = TestCity.Flat();
        foreach (var area in new[] { new CellRect(10, 10, 1, 1), new CellRect(10, 12, 1, 1), new CellRect(11, 10, 1, 1) })
            AddHouse(game, area);
        game.Touch();
        var yards = HouseYardLayout.Create(game.Map);
        Assert.Equal(3, yards.Count);
        Assert.Equal(yards.Count, yards.Values.Select(yard => yard.Cell).Distinct().Count());
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(yards.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X),
            HouseYardLayout.Create(restored.Map).OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X));
        Assert.Equal(game.Stats, restored.Stats);
    }

    [Fact]
    public void EdgeHousesUseOnlyInBoundsLand()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        AddHouse(game, new(map.Width - 1, map.Height - 1, 1, 1));
        var yard = Assert.Single(HouseYardLayout.Create(map)).Value;
        Assert.True(map.InBounds(yard.Cell));
        Assert.Equal(new Pos(map.Width - 2, map.Height - 1), yard.Cell);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void PropertyOutlineIsOneClosedBoundaryWithoutInternalHouseOrYardEdges(int width, int height)
    {
        var area = new CellRect(10, 10, width, height);
        foreach (var yard in new[] { new Pos(10, 9), new Pos(10, area.Bottom + 1),
            new Pos(9, 10), new Pos(area.Right + 1, 10) })
        {
            var edges = HouseYardLayout.Outline(area, yard);
            var cells = area.Cells().Append(yard).ToHashSet();
            Assert.Equal(2 * (width + height) + 2, edges.Count);
            Assert.Equal(edges.Count, edges.Distinct().Count());
            foreach (var edge in edges)
            {
                Assert.Contains(edge.Cell, cells);
                Assert.Equal(1, Math.Abs(edge.Start.X - edge.End.X) + Math.Abs(edge.Start.Y - edge.End.Y));
                Assert.Single(edges, next => next.Start == edge.End);
                var across = new Pos(edge.Cell.X + edge.End.Y - edge.Start.Y,
                    edge.Cell.Y - (edge.End.X - edge.Start.X));
                Assert.DoesNotContain(across, cells);
            }
            int doubledArea = edges.Sum(edge => edge.Start.X * edge.End.Y - edge.End.X * edge.Start.Y);
            Assert.Equal(2 * (area.Area + 1), doubledArea);
        }
    }

    [Fact]
    public void HousesWithoutYardsStillHighlightEveryCellAndRejectDisconnectedExtensions()
    {
        var area = new CellRect(10, 10, 2, 2);
        Assert.Equal(8, HouseYardLayout.Outline(area).Count);
        Assert.Throws<ArgumentException>(() => HouseYardLayout.Outline(area, new Pos(10, 10)));
        Assert.Throws<ArgumentException>(() => HouseYardLayout.Outline(area, new Pos(14, 14)));
    }

    [Fact]
    public void YardsNeverExtendBeyondTheDesignatedResidentialArea()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        map.SetZone(10, 10, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(10, 10, 1, 1));
        Assert.Empty(HouseYardLayout.Create(map));

        map.SetZone(9, 10, ZoneType.Residential);
        Assert.Equal(new Pos(9, 10), Assert.Single(HouseYardLayout.Create(map)).Value.Cell);

        map.SetZone(9, 10, ZoneType.None);
        Assert.Empty(HouseYardLayout.Create(map));
    }

    [Fact]
    public void NewEarlierHouseCannotStealAnEstablishedYard()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 12, 1, 1));
        game.Map.SetZone(10, 13, ZoneType.None);
        game.Map.SetZone(9, 12, ZoneType.None);
        var original = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        Assert.Equal(new Pos(10, 11), original.Cell);
        AddHouse(game, new(10, 10, 1, 1));
        var yards = HouseYardLayout.Create(game.Map);
        Assert.Equal(original, yards[new(10, 12)]);
        Assert.NotEqual(original.Cell, yards[new(10, 10)].Cell);
    }

    [Fact]
    public void SavedBlockedYardStaysFixedEvenWhenAnotherNeighborIsFree()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var original = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        game.Map.SetZone(original.Cell.X, original.Cell.Y, ZoneType.None);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Empty(HouseYardLayout.Create(loaded.Map));
        Assert.Equal(game.Map.HouseYardCells, loaded.Map.HouseYardCells);
        loaded.Map.SetZone(original.Cell.X, original.Cell.Y, ZoneType.Residential);
        Assert.Equal(original, Assert.Single(HouseYardLayout.Create(loaded.Map)).Value);
    }

    [Fact]
    public void UpgradingAndRotatingAHouseNeverReassignItsYard()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var original = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        game.Map.BuildingRotations[game.Map.Index(10, 10)] = 1;
        Assert.Equal(original, Assert.Single(HouseYardLayout.Create(game.Map)).Value);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Burgage House"), new(10, 10, 1, 2));
        Assert.Empty(HouseYardLayout.Create(game.Map));
        Assert.Equal(game.Map.Index(original.Cell.X, original.Cell.Y),
            Assert.Single(game.Map.HouseYardCells).Value);
        game.Map.BuildingRotations.Clear();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Empty(HouseYardLayout.Create(loaded.Map));
        Assert.Equal(game.Map.HouseYardCells, loaded.Map.HouseYardCells);
    }

    [Fact]
    public void OldSavesAssignYardsOnceAndMalformedAssignmentsAreRejected()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        var original = Assert.Single(HouseYardLayout.Create(game.Map)).Value;
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json.AsObject().Remove("HouseYardCells");
        var loaded = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.Empty(loaded.Map.HouseYardCells);
        Assert.Equal(original, Assert.Single(HouseYardLayout.Create(loaded.Map)).Value);

        foreach (int invalid in new[] { -1, game.Map.Width * game.Map.Height, game.Map.Index(30, 30) })
        {
            json["HouseYardCells"] = new System.Text.Json.Nodes.JsonObject
            {
                [game.Map.Index(10, 10).ToString(System.Globalization.CultureInfo.InvariantCulture)] = invalid,
            };
            Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        }
        json["HouseYardCells"] = null;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void SaveRejectsYardsSharedByTwoHousesOrOwnedByANonHouse()
    {
        var game = TestCity.Flat();
        AddHouse(game, new(10, 10, 1, 1));
        AddHouse(game, new(10, 12, 1, 1));
        HouseYardLayout.Create(game.Map);
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!;
        string first = game.Map.Index(10, 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string second = game.Map.Index(10, 12).ToString(System.Globalization.CultureInfo.InvariantCulture);
        json["HouseYardCells"] = new System.Text.Json.Nodes.JsonObject
        {
            [first] = game.Map.Index(10, 11),
            [second] = game.Map.Index(10, 11),
        };
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        json["HouseYardCells"] = new System.Text.Json.Nodes.JsonObject
        {
            [game.Map.Index(30, 30).ToString(System.Globalization.CultureInfo.InvariantCulture)] = game.Map.Index(30, 31),
        };
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    private static void AddHouse(CityGame game, CellRect area)
    {
        foreach (var cell in area.Cells()) game.Map.SetZone(cell.X, cell.Y, ZoneType.Residential);
        foreach (var cell in new CellRect(area.X - 1, area.Y - 1, area.Width + 2, area.Height + 2).Cells())
            if (game.Map.InBounds(cell) && game.Map.ZoneAt(cell.X, cell.Y) == ZoneType.None)
                game.Map.SetZone(cell.X, cell.Y, ZoneType.Residential);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Cottage"), area);
        game.Map.SetHousehold(area.X, area.Y, new(2, 2, 1));
        game.Touch();
    }
}
