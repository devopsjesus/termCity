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
    public void OnlyCanonicalNamesAreRegistered()
    {
        var content = new GameContent();
        Assert.NotNull(content.Buildings.Find("Cottage"));
        Assert.NotNull(content.Roads.Find(DefaultRoads.TrackName));
        Assert.NotNull(content.Terrains.Find(DefaultTerrains.GrassName));
        Assert.Null(content.Buildings.Find("House"));
        Assert.Null(content.Buildings.Find("Police Station"));
        Assert.Null(content.Roads.Find("Highway"));
        Assert.Null(content.Roads.Find("Street"));
        Assert.Null(content.Terrains.Find("Grass"));
    }

    [Theory]
    [InlineData("Constantinople")]
    [InlineData("Naples")]
    [InlineData("Genoa")]
    [InlineData("Lubeck")]
    [InlineData("York")]
    public void ObsoleteScenarioNamesAreRejected(string scenario)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!;
        json["Config"]!["Scenario"] = scenario;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
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
