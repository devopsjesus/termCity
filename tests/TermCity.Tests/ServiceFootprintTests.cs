using System.Text.Json.Nodes;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ServiceFootprintTests
{
    [Theory]
    [InlineData("Sheriff's Hall", 10, 5)]
    [InlineData("Parish Church", 10, 10)]
    [InlineData("Cathedral", 15, 15)]
    [InlineData("Castle", 20, 15)]
    public void ServiceSizesReflectTheirScale(string name, int width, int height)
    {
        var type = new GameContent().Buildings.Get(name);
        Assert.Equal(width, type.Width);
        Assert.Equal(height, type.Height);
    }

    [Fact]
    public void SingleClickOccupiesWholeFootprintButCountsAndChargesOneBuilding()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 250;
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        int money = game.Money;
        Assert.Equal(new Quote(50, type.Cost, 0), game.QuoteBuilding(type, new(10, 10, 1, 1)));
        Assert.True(game.PlaceBuilding(type, new(10, 10, 1, 1)).Success);
        Assert.Equal(money - type.Cost, game.Money);
        Assert.Same(type, game.Map.BuildingAt(10, 10));
        Assert.Same(type, game.Map.BuildingAt(19, 14));
        Assert.Single(game.Map.ServiceCells);
        Assert.Equal(1, game.Services.BuildingCounts[(int)ServiceKind.Police]);
        Assert.Equal(type.WeeklyUpkeep, game.Finance.ServiceUpkeep);
        Assert.Equal(type.Strength, game.Services.Coverage(ServiceKind.Police, game.Map.Index(10, 10)));
        Assert.Equal("^\\\n#]", CellRenderer.Render(game, 19, 10).Glyph);
        Assert.Equal(type.Glyphs[0], new BlockSampler(game).Sample(19, 10, 2).Glyph);
    }

    [Theory]
    [InlineData("water")]
    [InlineData("road")]
    [InlineData("zone")]
    [InlineData("building")]
    [InlineData("edge")]
    public void BlockedFootprintIsNeverPartiallyPlaced(string obstacle)
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        int x = obstacle == "edge" ? game.Map.Width - 1 : 10;
        if (obstacle == "water") game.Map.SetTerrain(11, 10, game.Map.Content.Terrains.Get("Water"));
        if (obstacle == "road") game.Map.SetRoad(11, 10, true);
        if (obstacle == "zone") game.Map.SetZone(11, 10, ZoneType.Residential);
        if (obstacle == "building") game.Map.SetBuilding(11, 10, game.Map.Content.Buildings.Get("Town Well"));
        int money = game.Money;
        Assert.False(game.CanPlaceBuilding(type, x, 10));
        Assert.False(game.PlaceBuilding(type, new(x, 10, 1, 1)).Success);
        Assert.Null(game.Map.BuildingAt(x, 10));
        Assert.Equal(money, game.Money);
        Assert.Empty(game.Map.BuildingFootprints);
    }

    [Fact]
    public void SelectedAreasTileNonOverlappingCompleteBuildings()
    {
        var game = TestCity.Flat();
        game.HighestMilestone = 250;
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        Assert.Equal(new Quote(100, type.Cost * 2, 25), game.QuoteBuilding(type, new(10, 10, 25, 5)));
        Assert.True(game.PlaceBuilding(type, new(10, 10, 25, 5)).Success);
        Assert.Equal(2, game.Map.ServiceCells.Count);
        Assert.Null(game.Map.BuildingAt(34, 10));
    }

    [Fact]
    public void HillCostsApplyAcrossTheWholeFootprint()
    {
        var game = TestCity.Flat();
        game.HighestMilestone = 250;
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        game.Map.SetTerrain(11, 10, game.Map.Content.Terrains.Get("Hill"));
        Assert.Equal((int)(type.Cost * 1.01), game.QuoteBuilding(type, new(10, 10, 1, 1)).Cost);
    }

    [Fact]
    public void RoadAccessIsABonusRatherThanAnActivationRequirement()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        game.Map.SetBuildingFootprint(type, type.FootprintAt(10, 5));
        game.Touch();
        Assert.Equal(1, game.Services.ActiveCounts[(int)ServiceKind.Police]);
        game.Map.SetRoad(21, 5, game.Map.Content.Roads.Default);
        for (int x = 21; x < game.Map.Width; x++) game.Map.SetRoad(x, 5, true);
        game.Touch();
        Assert.True(game.Network.IsServed(game.Map.Index(10, 5)));
        Assert.Equal(0, game.Network.AccessRank(game.Map.Index(10, 5)));
        Assert.Equal(1, game.Network.AccessRank(game.Map.Index(19, 5)));
        Assert.True(game.Network.IsServed(game.Map.Index(19, 5)));
        Assert.Equal(1, game.Services.ActiveCounts[(int)ServiceKind.Police]);
    }

    [Fact]
    public void PreviewDemolitionPersistenceAndUndoKeepOneWholeBuilding()
    {
        var session = new GameSession(TestCity.Flat());
        session.Game.HighestMilestone = 250;
        session.Game.Paused = true;
        session.PlaceCursor(new(10, 10));
        var type = session.Game.Map.Content.Buildings.Get("Sheriff's Hall");
        session.PreviewBuilding(type);
        Assert.Equal(new CellRect(10, 10, 10, 5), session.Preview!.Area);
        Assert.True(session.Preview.IsValid(session.Game, 19, 14));
        Assert.True(session.ConfirmPreview().Success);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        Assert.Single(loaded.Map.ServiceCells);
        Assert.Equal(new CellRect(10, 10, 10, 5), loaded.Map.BuildingFootprintAt(19, 14));
        session.PlaceCursor(new(19, 14));
        session.PreviewDemolish();
        Assert.Equal(50, session.Preview!.Quote.Cells);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Null(session.Game.Map.BuildingAt(10, 10));
        Assert.Empty(session.Game.Map.BuildingFootprints);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Same(type, session.Game.Map.BuildingAt(19, 14));
        Assert.Single(session.Game.Map.ServiceCells);
    }

    [Fact]
    public void MissingAndMalformedFootprintsAreRejected()
    {
        var game = TestCity.Flat();
        game.HighestMilestone = 250;
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        Assert.True(game.PlaceBuilding(type, new CellRect(10, 10, 1, 1)).Success);
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json.AsObject().Remove("BuildingFootprints");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        json["BuildingFootprints"] = JsonNode.Parse("[]");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        json["BuildingFootprints"] = JsonNode.Parse("""[{"X":10,"Y":10,"Width":1,"Height":1}]""");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }
}
