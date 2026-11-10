using System.Numerics;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class BuildingEntranceTests
{
    private static CityGame Flat()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 160, MapHeight = 160 });
        foreach (int road in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(road % game.Map.Width, road / game.Map.Width, false);
        game.Touch();
        return game;
    }

    private static CellRect Place(CityGame game, string name, Pos at, int rotation = 0)
    {
        var type = game.Map.Content.Buildings.Get(name);
        var area = type.FootprintAt(at.X, at.Y, rotation);
        foreach (var cell in area.Cells()) game.Map.SetZone(cell.X, cell.Y, type.Zone);
        game.Map.SetBuildingFootprint(type, area);
        if (rotation != 0) game.Map.BuildingRotations[game.Map.Index(at.X, at.Y)] = rotation;
        game.Touch();
        return area;
    }

    [Fact]
    public void EveryBuildingFamilyHasExplicitFiniteAccessAtEveryRotation()
    {
        var game = Flat();
        foreach (var type in game.Map.Content.Buildings)
            for (int rotation = 0; rotation < 4; rotation++)
            {
                var area = type.FootprintAt(40, 40, rotation);
                var entries = BuildingEntranceLayout.ForBuilding(game.Map, area, type, rotation);
                Assert.NotEmpty(entries);
                foreach (var entry in entries)
                {
                    Assert.True(float.IsFinite(entry.Threshold.X) && float.IsFinite(entry.Threshold.Y) &&
                        float.IsFinite(entry.Threshold.Z), type.Name);
                    Assert.InRange(entry.Direction.Length(), .999f, 1.001f);
                    Assert.True(entry.Width > 0, type.Name);
                    Assert.Equal(new Vector2(entry.Threshold.X, entry.Threshold.Z), entry.Approach[^1]);
                    Assert.Equal(entry.Boundary, entry.Approach[0]);
                    if (entry.Direction.Y != 0)
                        Assert.Equal(entry.Direction.Y > 0 ? area.Bottom + 1 : area.Y, entry.Boundary.Y);
                    else Assert.Equal(entry.Direction.X > 0 ? area.Right + 1 : area.X, entry.Boundary.X);
                }
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CourtyardApproachEndsAtTheRecessedDoorInsteadOfThePlotFront(int rotation)
    {
        var game = Flat();
        var type = game.Map.Content.Buildings.Get("Courtyard House");
        var area = type.FootprintAt(40, 40, rotation);
        var entrance = Assert.Single(BuildingEntranceLayout.ForBuilding(game.Map, area, type, rotation));
        var appearance = HouseAppearance.At(type, area.X, area.Y);
        int width = type.ModelWidth, depth = type.ModelDepth;
        var block = appearance.BlocksOnPlot(width, depth, type.Level)[0];
        var actual = appearance.EntranceOnPlot(width, depth, type.Level);
        Assert.Equal(block.Z + block.Depth / 2, actual.Threshold.Z);
        Assert.True(actual.Threshold.Z < 0);
        var offset = BuildingEntranceLayout.Rotate(new(actual.Threshold.X, actual.Threshold.Z), rotation) * type.ModelScale;
        var center = BuildingEntranceLayout.Center(game.Map, area, type);
        Assert.Equal(new Vector3(center.X + offset.X, center.Y + actual.Threshold.Y * type.ModelScale,
            center.Z + offset.Y), entrance.Threshold);
    }

    [Fact]
    public void LegacyFootprintsUseTheSameFittedScaleAsTheirDoorArtwork()
    {
        var game = Flat();
        var type = game.Map.Content.Buildings.Get("Cottage");
        var area = new CellRect(40, 40, 1, 1);
        Assert.Equal(1, BuildingEntranceLayout.ModelScale(type, area, 0));
        var entry = Assert.Single(BuildingEntranceLayout.ForBuilding(game.Map, area, type));
        Assert.InRange(entry.Threshold.X, 40, 41);
        Assert.InRange(entry.Threshold.Z, 40, 41);
        Assert.Equal(BuildingDetailLayout.DoorWidth, entry.Width);
    }

    [Fact]
    public void WearJourneysUseDoorSideCellsAndKeepSavedWear()
    {
        var game = Flat();
        Place(game, "Cottage", new(20, 30), 0);
        Place(game, "Apothecary", new(45, 30), 3);
        for (int day = 0; day < 45; day++) TravelRoutes.WearPaths(game.Map);
        var journeys = TravelRoutes.FootpathJourneys(game.Map).ToArray();
        Assert.NotEmpty(journeys);
        foreach (var path in journeys)
            foreach (var (property, start) in new[] { (path[0], path[1]), (path[^1], path[^2]) })
            {
                int owner = game.Map.BuildingAnchor(property);
                Assert.Contains(BuildingEntranceLayout.Accesses(game.Map, game.Map.BuildingFootprints[owner]),
                    access => access.Property == property && access.Start == start);
                var contact = BuildingEntranceLayout.Contact(game.Map, property, start);
                Assert.Contains(FootpathLayout.Strokes(game.Map), stroke =>
                    Vector2.Distance(stroke.Points[0], contact) < .001f ||
                    Vector2.Distance(stroke.Points[^1], contact) < .001f);
            }
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Fact]
    public void AnObstructedDoorDoesNotInventARearEntrance()
    {
        var game = Flat();
        var area = Place(game, "Cottage", new(20, 30));
        Place(game, "Workshop", new(45, 30));
        var access = BuildingEntranceLayout.Accesses(game.Map, area).ToArray();
        Assert.NotEmpty(access);
        foreach (var candidate in access)
        {
            var cell = game.Map.PosOf(candidate.Start);
            game.Map.SetTerrain(cell.X, cell.Y, game.Map.Content.Terrains.Get("Water"));
        }
        game.Touch();
        Assert.Empty(BuildingEntranceLayout.Accesses(game.Map, area));
        Assert.Empty(TravelRoutes.FootpathJourneys(game.Map));
        Assert.Contains(CellInspector.Describe(game, new(area.X, area.Y)), line => line.Contains("Entrance approach obstructed"));
    }

    [Fact]
    public void DoorFacingDoesNotChangeRoadDistanceOrTravelCostFormulas()
    {
        var game = Flat();
        var area = Place(game, "Cottage", new(20, 30));
        for (int x = 10; x < 40; x++) game.Map.SetRoad(x, 26, true);
        game.Touch();
        double before = TravelRoutes.RoadDistance(game.Map, area, TravelRoutes.NearestRoad(game.Map));
        double cost = TravelRoutes.StepCost(game.Map, game.Map.Index(20, 29));
        game.Map.BuildingRotations[game.Map.Index(area.X, area.Y)] = 2;
        game.Map.InvalidateTravel();
        Assert.Equal(before, TravelRoutes.RoadDistance(game.Map, area, TravelRoutes.NearestRoad(game.Map)));
        Assert.Equal(cost, TravelRoutes.StepCost(game.Map, game.Map.Index(20, 29)));
        Assert.All(BuildingEntranceLayout.Accesses(game.Map, area), entry => Assert.True(entry.Start / game.Map.Width < area.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnOwnedFrontGardenUsesItsClearWalkwayWithoutMovingTheGarden(int rotation)
    {
        var game = Flat();
        var area = Place(game, "Cottage", new(20, 30), rotation);
        int owner = game.Map.Index(area.X, area.Y);
        var entry = Assert.Single(BuildingEntranceLayout.ForBuilding(game.Map, area, game.Map.Content.Buildings.Get("Cottage")));
        var yardCenter = entry.Boundary + entry.Direction * .5f;
        var yard = new Pos((int)Math.Floor(yardCenter.X), (int)Math.Floor(yardCenter.Y));
        game.Map.SetZone(yard.X, yard.Y, ZoneType.Residential);
        game.Map.RegisterHouseYard(owner, game.Map.Index(yard.X, yard.Y));
        var updated = Assert.Single(BuildingEntranceLayout.ForBuilding(game.Map, area, game.Map.Content.Buildings.Get("Cottage")));
        Assert.True(updated.Approach.Count > 2);
        Assert.All(updated.Approach.Skip(1).SkipLast(1), point =>
            Assert.True(Vector2.Dot(point - entry.Boundary, entry.Direction) < 0));
        Assert.Contains(BuildingEntranceLayout.Accesses(game.Map, area), access => access.Property == game.Map.Index(yard.X, yard.Y) &&
            access.Start == game.Map.Index(yard.X + (int)entry.Direction.X, yard.Y + (int)entry.Direction.Y));
        Assert.Equal(game.Map.Index(yard.X, yard.Y), game.Map.HouseYardCells[owner]);
        Assert.True(HouseYardLayout.BedCenter - HouseYardLayout.BedWidth / 2 > HouseYardLayout.WalkwayWidth / 2);
    }
}
