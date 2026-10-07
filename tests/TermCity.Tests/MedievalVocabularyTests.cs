using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MedievalVocabularyTests
{
    [Theory]
    [InlineData(0, "0g")]
    [InlineData(412, "412g")]
    [InlineData(1_234_567, "1,234,567g")]
    [InlineData(-950, "-950g")]
    public void MoneyIsFormattedInGold(int amount, string expected) =>
        Assert.Equal(expected, Fmt.Money(amount));

    [Fact]
    public void NoPlayerVisibleServiceNameIsModern()
    {
        string[] banned = ["Police", "Power", "Park", "Clinic", "Hospital", "University", "Fire Station", "Factory", "Skyscraper"];
        var content = new GameContent();
        var names = content.Buildings.Select(b => b.Name)
            .Concat(content.Roads.Select(r => r.Name))
            .Concat(content.Terrains.Select(t => t.Name))
            .Concat(Zones.Placeable.Select(z => Zones.Get(z).Name))
            .Concat(Enum.GetValues<ServiceKind>().Select(CityReport.ServiceName));
        foreach (string name in names)
        {
            Assert.DoesNotContain(banned, b => name.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void OldNamesStillResolveToTheirMedievalSuccessors()
    {
        var content = new GameContent();
        Assert.Same(content.Buildings.Get("Cottage"), content.Buildings.Get("House"));
        Assert.Same(content.Buildings.Get("Sheriff's Hall"), content.Buildings.Get("Police Station"));
        Assert.Same(content.Roads.Get(DefaultRoads.KingsRoadName), content.Roads.Get("Highway"));
        Assert.Same(content.Roads.Get(DefaultRoads.TrackName), content.Roads.Get("Street"));
        Assert.Same(content.Terrains.Get(DefaultTerrains.GrassName), content.Terrains.Get("Grass"));
    }

    [Fact]
    public void SavesWrittenBeforeTheMedievalSettingOrWithItsStandInNamesStillLoad()
    {
        var game = CityGame.New(new GameConfig { Scenario = CityScenario.SanDiego, MapWidth = 640, MapHeight = 384, Seed = 5 });
        string json = SaveGameStore.Serialize(game)
            .Replace("\"Scenario\": \"SanDiego\"", "\"Scenario\": \"Genoa\"")
            .Replace("\"Cottage\"", "\"House\"")
            .Replace("\"Dirt Track\"", "\"Street\"")
            .Replace("\"Meadow\"", "\"Grass\"");
        var loaded = SaveGameStore.Deserialize(json);
        Assert.Equal(CityScenario.SanDiego, loaded.Config.Scenario);
        Assert.Equal(game.Map.TerrainLayer, loaded.Map.TerrainLayer);
        Assert.Equal(game.Map.BuildingLayer, loaded.Map.BuildingLayer);
        Assert.Equal(game.Map.RoadLayer, loaded.Map.RoadLayer);
    }

    [Fact]
    public void TheBudgetNamesTheMedievalLevies()
    {
        Assert.Equal("Hearth tithe and rents", BudgetMenuNames(ZoneType.Residential));
        Assert.Equal("Market tolls", BudgetMenuNames(ZoneType.Commercial));
        Assert.Equal("Guild dues", BudgetMenuNames(ZoneType.Industrial));
    }

    private static string BudgetMenuNames(ZoneType zone) => TermCity.Core.Session.GameSession.TaxName(zone);
}
