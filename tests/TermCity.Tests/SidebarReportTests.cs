using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class SidebarReportTests
{
    [Fact]
    public void ClassicCityRowsKeepOnlyDemographicDetails()
    {
        var game = TestCity.Flat();
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(3, 2, 1));
        game.Touch();
        var rows = SidebarReport.CityRows(game);

        Assert.All(rows, row => Assert.Equal(2, row.Length));
        Assert.Equal("3", Value("Adults").Text);
        Assert.Equal("2", Value("Children").Text);
        Assert.Equal("1", Value("Elders").Text);
        Assert.Equal("1", Value("Hearths").Text);
        Assert.Equal(SidebarReport.PeopleColor, Value("Adults").Color);
        Assert.Equal(["Adults", "Children", "Elders", "Hearths"], rows.Select(row => row[0].Text));
        Assert.DoesNotContain(rows, row => row[0].Text == "Fuel");
        Assert.DoesNotContain(rows, row => row[0].Text == "Mood");

        SidebarCell Value(string label) => Assert.Single(rows, row => row[0].Text == label)[1];
    }

    [Fact]
    public void FullCityRowsHighlightShortagesFamineAndInsolvency()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(2, 1, 0));
        game.Money = -1;
        game.Hunger = 0.5;
        game.Touch();
        var rows = SidebarReport.CityRows(game);

        Assert.Equal(0d.ToString("P0"), Value("Fuel").Text);
        Assert.Equal(SidebarReport.BadColor, Value("Fuel").Color);
        Assert.Equal(SidebarReport.BadColor, Value("Water").Color);
        Assert.Contains("FAMINE", Value("Grain").Text);
        Assert.Equal(SidebarReport.BadColor, Value("Grain").Color);
        Assert.Equal("Half strength", Value("Services").Text);
        Assert.Equal(SidebarReport.BadColor, Value("Services").Color);
        Assert.Equal($"{game.Indicators.Mood} ({game.Indicators.Happiness:0})", Value("Mood").Text);
        Assert.Equal(2, Assert.Single(rows, row => row[0].Text == "Complaint").Length);

        SidebarCell Value(string label) => Assert.Single(rows, row => row[0].Text == label)[1];
    }

    [Fact]
    public void ZoneHoverDetailsMatchKeyboardReportsAndKeepPendingRemovals()
    {
        var game = TestCity.Flat();
        Assert.True(game.Designate(new CellRect(10, 21, 1, 1), ZoneType.Residential).Success);
        Assert.True(game.Designate(new CellRect(30, 5, 1, 1), ZoneType.Residential).Success);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(2, 1, 0));
        game.Touch();
        string details = CityReport.ZoneDetails(game, ZoneType.Residential);
        Assert.Contains("R Homesteads", details);
        Assert.Contains("Built: 1 cells", details);
        Assert.Contains("Zoned: 2 lots", details);
        Assert.Contains("Road-served: 1 lots", details);
        Assert.Contains("Without road: 1 lots", details);
        Assert.Contains("Awaiting removal: 0 buildings", details);
        Assert.Contains($"Demand: {game.Demand.Residential:P0}", details);

        Assert.True(game.Dezone(new CellRect(10, 21, 1, 1)).Success);
        details = CityReport.ZoneDetails(game, ZoneType.Residential);
        Assert.Contains("Awaiting removal: 1 buildings", details);
        var session = new GameSession(game);
        session.ShowReport();
        foreach (var zone in Zones.Placeable)
            Assert.Contains(CityReport.ZoneDetails(game, zone), session.Prompt!.Text);
        session.ClosePrompt();
        session.ShowGrowthReport();
        foreach (var zone in Zones.Placeable)
            Assert.Contains(CityReport.ZoneDetails(game, zone), session.Prompt!.Text);
        Assert.Contains("road-served vacancies", session.Prompt!.Text);
    }

    [Fact]
    public void FullCityHealthReportIncludesTheSameZoneDetails()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        string report = CityReport.Health(game);
        foreach (var zone in Zones.Placeable) Assert.Contains(CityReport.ZoneDetails(game, zone), report);
    }

    [Fact]
    public void CityRowsKeepSuppliesAndRemoveRedundantOrFinancialMetrics()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new(80, 18, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new(85, 18, 1, 1)).Success);
        var rows = SidebarReport.CityRows(game);

        Assert.Equal(SidebarReport.GoodColor, Assert.Single(rows, row => row[0].Text == "Fuel")[1].Color);
        Assert.Equal(SidebarReport.GoodColor, Assert.Single(rows, row => row[0].Text == "Water")[1].Color);
        Assert.Equal(SidebarReport.GoodColor, Assert.Single(rows, row => row[0].Text == "Services")[1].Color);
        Assert.Equal(11, rows.Count);
        Assert.DoesNotContain(rows, row => new[]
        {
            "Souls", "Tithes/rents", "Standing", "Net", "Costs", "Jobs", "Idle", "Tithe", "Tolls", "Dues",
        }.Contains(row[0].Text));
    }
}
