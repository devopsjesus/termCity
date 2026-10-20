using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class BuildingRotationTests
{
    private static CityGame ReadyCity()
    {
        var game = TestCity.Flat();
        game.HighestMilestone = 1_000;
        return game;
    }

    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 1, 2)]
    public void PreviewPlacementAndSavesAgreeOnRotatedFootprints(int rotation, int width, int height)
    {
        var session = new GameSession(ReadyCity());
        session.PlaceCursor(new(10, 10));
        var building = session.Game.Map.Content.Buildings.Get("Tavern");
        session.PreviewBuilding(building);
        session.RotateBuildingPlacement(rotation);
        Assert.Equal(rotation, session.Preview!.Rotation);
        Assert.Equal(new CellRect(10, 10, width, height), session.Preview.Area);
        Assert.Null(session.BuildingPlacementError);
        Assert.Equal(building.Cost, session.Preview.Quote.Cost);
        session.MoveBuildingPlacement(new(12, 12));
        var footprint = new CellRect(12, 12, width, height);
        Assert.Equal(footprint, session.Preview!.Area);
        Assert.Equal(rotation, session.Preview.Rotation);
        Assert.True(session.ConfirmPreview().Success);
        var map = session.Game.Map;
        Assert.Equal(footprint, map.BuildingFootprintAt(12, 12));
        Assert.Equal(rotation, map.BuildingRotations.GetValueOrDefault(map.Index(12, 12)));
        Assert.All(footprint.Cells(), cell => Assert.Same(building, map.BuildingAt(cell.X, cell.Y)));
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        Assert.Equal(footprint, restored.Map.BuildingFootprintAt(12, 12));
        Assert.Equal(rotation, restored.Map.BuildingRotations.GetValueOrDefault(restored.Map.Index(12, 12)));
    }

    [Fact]
    public void LeftRightAndFullTurnsPreserveThePlacementAnchor()
    {
        var session = new GameSession(ReadyCity());
        session.PlaceCursor(new(10, 10));
        var building = session.Game.Map.Content.Buildings.Get("Tavern");
        session.PreviewBuilding(building);
        session.RotateBuildingPlacement(-1);
        Assert.Equal(3, session.Preview!.Rotation);
        session.RotateBuildingPlacement(1);
        Assert.Equal(0, session.Preview!.Rotation);
        for (int turn = 0; turn < 4; turn++) session.RotateBuildingPlacement(1);
        Assert.Equal(0, session.Preview!.Rotation);
        Assert.Equal(new Pos(10, 10), session.Cursor);
        session.CancelPreview();
        session.PreviewBuilding(building);
        Assert.Equal(0, session.Preview!.Rotation);
    }

    [Fact]
    public void RotatedValidationAndCostUseTheActualCells()
    {
        var session = new GameSession(ReadyCity());
        var map = session.Game.Map;
        var building = map.Content.Buildings.Get("Tavern");
        map.SetTerrain(10, 11, map.Content.Terrains.Get("Hill"));
        session.Game.Touch();
        session.PlaceCursor(new(10, 10));
        session.PreviewBuilding(building);
        Assert.Equal(building.Cost, session.Preview!.Quote.Cost);
        session.RotateBuildingPlacement(1);
        Assert.Equal((int)Math.Round(building.Cost * 1.25), session.Preview!.Quote.Cost);
        map.SetZone(10, 11, ZoneType.Residential);
        session.Game.Touch();
        session.MoveBuildingPlacement(new(10, 10));
        Assert.Contains("zone blocks", session.BuildingPlacementError);
        Assert.Empty(session.Preview!.ValidCells!);
        Assert.False(session.ConfirmPreview().Success);
        session.RotateBuildingPlacement(-1);
        Assert.Null(session.BuildingPlacementError);
        Assert.True(session.ConfirmPreview().Success);
    }

    [Fact]
    public void RotatingAtTheMapEdgeRevalidatesWithoutMovingTheAnchor()
    {
        var session = new GameSession(ReadyCity());
        var map = session.Game.Map;
        session.PlaceCursor(new(map.Width - 1, 10));
        session.PreviewBuilding(map.Content.Buildings.Get("Tavern"));
        Assert.Contains("fully inside", session.BuildingPlacementError);
        session.RotateBuildingPlacement(1);
        Assert.Null(session.BuildingPlacementError);
        Assert.Equal(new Pos(map.Width - 1, 10), session.Cursor);
        Assert.True(session.ConfirmPreview().Success);
    }

    [Fact]
    public void DemolitionClearsRotationAndUndoRestoresIt()
    {
        var session = new GameSession(ReadyCity());
        session.Game.Paused = true;
        session.PlaceCursor(new(10, 10));
        session.PreviewBuilding(session.Game.Map.Content.Buildings.Get("Tavern"));
        session.RotateBuildingPlacement(1);
        Assert.True(session.ConfirmPreview().Success);
        Assert.True(session.Demolish().Success);
        Assert.Empty(session.Game.Map.BuildingRotations);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(1, session.Game.Map.BuildingRotations[session.Game.Map.Index(10, 10)]);
        Assert.Equal(new CellRect(10, 10, 1, 2), session.Game.Map.BuildingFootprintAt(10, 10));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void SavesRejectInvalidRotations(int rotation)
    {
        var game = ReadyCity();
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Tavern"), CellRect.Single(new(10, 10))).Success);
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json["BuildingRotations"] = new JsonObject { [game.Map.Index(10, 10).ToString()] = rotation };
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void SavesRejectRotationMetadataWithoutABuilding()
    {
        var game = ReadyCity();
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json["BuildingRotations"] = new JsonObject { [game.Map.Index(10, 10).ToString()] = 1 };
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void OlderSavesWithoutRotationMetadataKeepTheirOriginalFootprint()
    {
        var game = ReadyCity();
        var building = game.Map.Content.Buildings.Get("Tavern");
        Assert.True(game.PlaceBuilding(building, CellRect.Single(new(10, 10))).Success);
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json.AsObject().Remove("BuildingRotations");
        var restored = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.Empty(restored.Map.BuildingRotations);
        Assert.Equal(new CellRect(10, 10, 2, 1), restored.Map.BuildingFootprintAt(10, 10));
    }

    [Fact]
    public void SavesRejectNullRotationMetadata()
    {
        var json = JsonNode.Parse(SaveGameStore.Serialize(ReadyCity()))!;
        json["BuildingRotations"] = null;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }
}
