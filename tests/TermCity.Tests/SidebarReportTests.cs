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
    [Theory]
    [InlineData(GameSpeed.Fast, 21)]
    [InlineData(GameSpeed.Medium, 7)]
    [InlineData(GameSpeed.Slow, 3.5)]
    public void ServiceStatusRefreshCadenceMatchesThreeFastWeeks(GameSpeed speed, double days)
    {
        Assert.Equal(days, SidebarReport.CoverageRefreshDays(new GameConfig(), speed), 6);
    }

    [Fact]
    public void CatalogStatusUsesActualSupplyAndResidentCoverage()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 500;
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new(80, 18, 1, 1)).Success);
        Assert.Contains("300 / 0 units", SidebarReport.ServiceStatusDetails(game, ServiceKind.Power));
        Assert.Contains("0% of residents", SidebarReport.ServiceStatusDetails(game, ServiceKind.Police));
        Assert.Equal("Grain storage and reserves", SidebarReport.ServiceStatus(game, ServiceKind.Granary).Text);
        foreach (var kind in ServiceKinds.All)
            Assert.DoesNotContain("%", SidebarReport.ServiceStatus(game, kind).Text);
        string grainMessage = SidebarReport.ServiceStatus(game, ServiceKind.Granary).Text;
        game.GrainWeeks = 1;
        Assert.Equal(grainMessage, SidebarReport.ServiceStatus(game, ServiceKind.Granary).Text);
        Assert.Equal(SidebarReport.WarningColor, SidebarReport.ServiceStatus(game, ServiceKind.Granary).Color);
    }

    [Theory]
    [InlineData("Adults", 1)]
    [InlineData("Children", 1)]
    [InlineData("Elders", 1)]
    [InlineData("Hearths", 1)]
    [InlineData("Mood", 1)]
    [InlineData("Fuel", 3)]
    [InlineData("Water", 3)]
    [InlineData("Services", 3)]
    [InlineData("Complaint", 3)]
    [InlineData("Season", 5)]
    [InlineData("Grain", 5)]
    public void CityMetricsOpenTheirCorrespondingReportTab(string metric, int expected)
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        session.ShowReport(SidebarReport.ReportTabFor(metric));
        Assert.Equal(expected, session.Prompt!.ActiveTab);
        Assert.Equal(expected == 1 ? "Population" : expected == 3 ? "Services" : "Food & Seasons",
            session.Prompt.Tabs![expected].Title);
    }

    [Theory]
    [InlineData(0, "Miserable")]
    [InlineData(34.99, "Miserable")]
    [InlineData(35, "Unhappy")]
    [InlineData(49.99, "Unhappy")]
    [InlineData(50, "Fiiine")]
    [InlineData(60, "Fiiine")]
    [InlineData(64.99, "Fiiine")]
    [InlineData(65, "Content")]
    [InlineData(79.99, "Content")]
    [InlineData(80, "Delighted")]
    [InlineData(100, "Delighted")]
    public void MoodLabelsPreserveHappinessThresholds(double happiness, string expected)
    {
        Assert.Equal(expected, new CityIndicators { Happiness = happiness }.Mood);
    }

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
        game.HighestMilestone = 500;
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(2, 1, 0));
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Watch House"), new(10, 19, 1, 1)).Success);
        game.Money = -1;
        game.Hunger = 0.5;
        game.Touch();
        var rows = SidebarReport.CityRows(game);

        Assert.Equal(0d.ToString("P0"), Value("Fuel").Text);
        Assert.Equal(SidebarReport.BadColor, Value("Fuel").Color);
        Assert.Equal(SidebarReport.BadColor, Value("Water").Color);
        Assert.Contains("FAMINE", Value("Grain").Text);
        Assert.Equal($"{game.GrainWeeks:0.#} weeks FAMINE", Value("Grain").Text);
        Assert.Equal(SidebarReport.BadColor, Value("Grain").Color);
        Assert.DoesNotContain(rows, row => row[0].Text == "Services");
        Assert.Equal(game.Indicators.Mood, Value("Mood").Text);
        Assert.DoesNotContain(rows, row => row[0].Text == "Complaint");

        SidebarCell Value(string label) => Assert.Single(rows, row => row[0].Text == label)[1];
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(-1)]
    public void ServicesRowIsOmittedRegardlessOfTreasury(int money)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Money = money;
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Touch();
        Assert.DoesNotContain(SidebarReport.CityRows(game), row => row[0].Text == "Services");
    }

    [Fact]
    public void ServicesRowStaysOmittedWhenBuildingsChange()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 250;
        var area = new CellRect(10, 19, 1, 1);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Watch House"), area).Success);
        Assert.DoesNotContain(SidebarReport.CityRows(game), row => row[0].Text == "Services");
        game.Money = -1;
        Assert.DoesNotContain(SidebarReport.CityRows(game), row => row[0].Text == "Services");
        Assert.True(game.Demolish(area).Success);
        Assert.DoesNotContain(SidebarReport.CityRows(game), row => row[0].Text == "Services");
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
        Assert.Contains("Built: 1 buildings", details);
        Assert.Contains("Zoned: 2 lots", details);
        Assert.Contains("Walkable: 2 lots", details);
        Assert.Contains("Unreachable: 0 lots", details);
        Assert.Contains("Awaiting removal: 0 buildings", details);
        Assert.Contains($"Demand: {game.Demand.Residential:P0}", details);

        Assert.True(game.Dezone(new CellRect(10, 21, 1, 1)).Success);
        details = CityReport.ZoneDetails(game, ZoneType.Residential);
        Assert.Contains("Awaiting removal: 1 buildings", details);
        var session = new GameSession(game);
        session.ShowReport();
        session.SelectTab(session.Prompt!.Tabs!.ToList().FindIndex(tab => tab.Title == "Growth"));
        foreach (var zone in Zones.Placeable)
        {
            Assert.Contains(Zones.Get(zone).Name.ToUpperInvariant(), session.Prompt!.Text);
            Assert.Contains("Awaiting removal:", session.Prompt.Text);
        }
        session.ClosePrompt();
        session.ShowGrowthReport();
        foreach (var zone in Zones.Placeable)
            Assert.Contains(CityReport.ZoneDetails(game, zone), session.Prompt!.Text);
        Assert.Contains("complete vacant plots", session.Prompt!.Text);
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

        Assert.Equal(SidebarReport.WarningColor, Assert.Single(rows, row => row[0].Text == "Fuel")[1].Color);
        Assert.Equal(SidebarReport.WarningColor, Assert.Single(rows, row => row[0].Text == "Water")[1].Color);
        Assert.DoesNotContain(rows, row => row[0].Text == "Services");
        Assert.Equal(9, rows.Count);
        Assert.DoesNotContain(rows, row => new[]
        {
            "Souls", "Tithes/rents", "Standing", "Net", "Costs", "Jobs", "Idle", "Tithe", "Tolls", "Dues",
        }.Contains(row[0].Text));
    }
}
