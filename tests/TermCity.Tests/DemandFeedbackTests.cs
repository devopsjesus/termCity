using TermCity.Core.Simulation;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DemandFeedbackTests
{
    [Theory]
    [InlineData(CityRules.Classic, ZoneType.Industrial)]
    [InlineData(CityRules.Classic, ZoneType.Commercial)]
    [InlineData(CityRules.Full, ZoneType.Industrial)]
    [InlineData(CityRules.Full, ZoneType.Commercial)]
    public void DesignatingMoreVacantBusinessesDoesNotCreateResidentialDemand(CityRules rules, ZoneType zone)
    {
        var game = TestCity.Flat(rules: rules);
        Assert.True(game.Designate(new(0, 21, 30, 1), ZoneType.Residential).Success);
        Assert.True(game.Designate(new(40, 18, 5, 1), zone).Success);
        double before = game.Demand.Residential;
        for (int x = 45; x < 75; x++)
        {
            Assert.True(game.Designate(new(x, 18, 1, 1), zone).Success);
            Assert.Equal(before, game.Demand.Residential);
        }
    }

    [Fact]
    public void EmptyZoningDoesNotHideContinuingResidentialArrivals()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(0, 0, 0, 0, 0,
            new(20, 0, 20), new(50, 0, 50), new(50, 0, 50), 0, 0, 0);
        Assert.Equal(1, Demand.Compute(stats, config).Residential);
        Assert.Equal(Demand.Compute(stats, config).Residential,
            Demand.Compute(stats with { Residential = new(1_000, 0, 1_000) }, config).Residential);
    }

    [Fact]
    public void VacantHomesPreventBusinessZoningFromManufacturingHousingShortages()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(100, 80, 20, 0, 20,
            new(100, 20, 100), new(4, 2, 4), new(4, 2, 4), 0, 0, 0);
        var before = Demand.Compute(stats, config);
        var after = Demand.Compute(stats with { Industrial = new(1_000, 2, 1_000) }, config);
        Assert.Equal(before.Residential, after.Residential);
        Assert.Equal(before.Commercial, after.Commercial);
        Assert.True(after.Industrial <= before.Industrial);
    }

    [Fact]
    public void DensePopulationAndGrowthCapacityUseTheSameHousingMeasure()
    {
        var config = new GameConfig { Rules = CityRules.Full };
        var stats = new CityStats(500, 400, 100, 0, 8,
            new(20, 8, 20), new(0, 0, 0), new(0, 0, 0), 0, 0, 0);
        Assert.Equal(100, Demand.SupportingHomes(stats, config));
        Assert.Equal(Demand.ExpectedArrivals(stats, config, ZoneType.Commercial) / 5,
            Demand.Compute(stats, config).Commercial);
        Assert.Equal(Demand.ExpectedArrivals(stats, config, ZoneType.Industrial) / 10,
            Demand.Compute(stats, config).Industrial);
    }

    [Fact]
    public void DenseCityDemandMatchesActualSupportedBusinessSlots()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(60, 20, 0));
        game.Touch();
        Assert.True(game.Designate(new(30, 21, 2, 1), ZoneType.Commercial).Success);
        Assert.Equal(1, game.SupportedCells(ZoneType.Commercial));
        Assert.Equal(GrowthStatus.LowBusinessClimate, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
        Assert.Equal(0, game.Demand.Commercial);
        Assert.Equal(0, game.Demand.Industrial);
        double residential = game.Demand.Residential;
        Assert.True(game.Designate(new(40, 21, 20, 1), ZoneType.Industrial).Success);
        Assert.Equal(residential, game.Demand.Residential);
    }

    [Fact]
    public void BusinessPendingRemovalConsumesCapacityUntilItsBuildingLeaves()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(250, 200, 50, 0, 50,
            new(100, 50, 100), new(0, 0, 0), new(10, 4, 10, 1), 0, 0, 0);
        Assert.Equal(0, Demand.Compute(stats, config).Industrial);
        Assert.Equal(0, Demand.Compute(stats with { Industrial = new(10, 5, 10) }, config).Industrial);
        Assert.True(Demand.Compute(stats with { Industrial = new(10, 4, 10) }, config).Industrial > 0);
    }

    [Fact]
    public void AddingEmptyHomesDoesNotInventBusinessDemand()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(100, 80, 20, 0, 20,
            new(30, 20, 30), new(4, 2, 4), new(4, 2, 4), 0, 0, 0);
        var before = Demand.Compute(stats, config);
        var after = Demand.Compute(stats with { Residential = new(1_000, 20, 1_000) }, config);
        Assert.Equal(before.Commercial, after.Commercial);
        Assert.Equal(before.Industrial, after.Industrial);
        Assert.True(after.Residential <= before.Residential);
    }

    [Theory]
    [InlineData(CityRules.Classic)]
    [InlineData(CityRules.Full)]
    public void ResidentialDemandRemainsVisibleWhenZonedPlotsActuallyAttractResidents(CityRules rules)
    {
        var game = TestCity.Flat(rules: rules);
        Assert.True(game.Designate(new(10, 21, 24, 8), ZoneType.Residential).Success);
        if (rules == CityRules.Full)
        {
            var fuel = game.Map.Content.Buildings.Get("Woodlot");
            game.Map.SetBuildingFootprint(fuel, fuel.FootprintAt(36, 22));
            var water = game.Map.Content.Buildings.Get("Town Well");
            game.Map.SetBuildingFootprint(water, water.FootprintAt(36, 26));
            game.Touch();
        }
        Assert.True(game.Demand.Residential > 0);
        for (int week = 0; week < 4 && game.Stats.Population == 0; week++) game.AdvanceWeek();
        Assert.True(game.Stats.Population > 0);
        Assert.Contains("Expected arrivals:", CityReport.ZoneDetails(game, ZoneType.Residential));
    }

    [Theory]
    [InlineData(ZoneType.Commercial)]
    [InlineData(ZoneType.Industrial)]
    public void BusinessDemandSurvivesZoningAndFallsWhenSupportedBusinessesActuallyOpen(ZoneType zone)
    {
        var game = TestCity.Flat();
        var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        for (int x = 0; x < 20; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, home);
            game.Map.SetHousehold(x, 21, new(2, 2, 1));
        }
        game.Touch();
        double before = game.Demand.For(zone);
        Assert.True(before > 0);
        Assert.True(game.Designate(new(35, 21, 18, 4), zone).Success);
        Assert.Equal(before, game.Demand.For(zone));
        for (int week = 0; week < 8 && game.Demand.For(zone) > 0; week++) game.AdvanceWeek();
        Assert.Equal(game.SupportedCells(zone), game.Stats.For(zone).Occupied);
        Assert.Equal(0, game.Demand.For(zone));
    }

    [Fact]
    public void MissingCustomersShowNoCommercialDemandAndNoCommercialGrowth()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.True(game.Designate(new(35, 21, 18, 4), ZoneType.Commercial).Success);
        Assert.Equal(0, game.Demand.Commercial);
        Assert.Equal(0, game.ExpectedGrowthArrivals(ZoneType.Commercial));
        Assert.Equal(GrowthStatus.NeedsHomes, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
        game.AdvanceWeek();
        Assert.Equal(0, game.Stats.Commercial.Filled);
        Assert.Contains("Needs 10 occupied homes", CityReport.ZoneDetails(game, ZoneType.Commercial));
    }

    [Fact]
    public void ZeroTradeClimateSuppressesDemandAndExplainsWhyCommercialPlotsDoNotGrow()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuildingFootprint(home, new CellRect(10, 21, 2, 2));
        game.Map.SetHousehold(10, 21, new(60, 20, 0));
        game.Touch();
        game.Designate(new(35, 21, 18, 4), ZoneType.Commercial);
        Assert.True(game.SupportedCells(ZoneType.Commercial) > 0);
        Assert.Equal(0, game.Indicators.BusinessClimate);
        Assert.Equal(0, game.Demand.Commercial);
        Assert.Contains("trade climate", CityReport.ZoneDetails(game, ZoneType.Commercial));
        game.AdvanceWeek();
        Assert.Equal(0, game.Stats.Commercial.Filled);
    }

    [Fact]
    public void SupplyingResidentsMakesCommercialDemandPositiveAndBusinessesActuallyOpen()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        for (int x = 10; x < 30; x += 2)
        {
            var plot = new CellRect(x, 21, 2, 2);
            foreach (var cell in plot.Cells()) game.Map.SetZone(cell.X, cell.Y, ZoneType.Residential);
            game.Map.SetBuildingFootprint(home, plot);
            game.Map.SetHousehold(x, 21, new(4, 2, 0));
        }
        var fuel = game.Map.Content.Buildings.Get("Woodlot");
        var water = game.Map.Content.Buildings.Get("Town Well");
        for (int x = 10; x < 40; x += 6)
        {
            game.Map.SetBuildingFootprint(fuel, fuel.FootprintAt(x, 10));
            game.Map.SetBuildingFootprint(water, water.FootprintAt(x, 16));
        }
        game.Touch();
        for (int day = 0; day < 14; day++) game.AdvanceDay();
        Assert.True(game.Indicators.BusinessClimate > 0);
        double before = game.Demand.Commercial;
        Assert.True(before > 0);
        game.Designate(new(40, 21, 18, 4), ZoneType.Commercial);
        Assert.Equal(before, game.Demand.Commercial);
        for (int week = 0; week < 16 && game.Stats.Commercial.Filled == 0; week++) game.AdvanceWeek();
        Assert.True(game.Stats.Commercial.Filled > 0,
            $"Climate {game.Indicators.BusinessClimate}, {GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Message}");
    }

    [Fact]
    public void ForecastReflectsAttractionClimateAndSlowProbabilisticGrowth()
    {
        var stats = new CityStats(50, 40, 10, 0, 10,
            new(100, 10, 100), new(100, 0, 100), new(100, 0, 100), 0, 0, 0);
        var config = new GameConfig { Rules = CityRules.Full };
        Assert.Equal(0, Demand.Compute(stats, config, attraction: 0).Residential);
        Assert.Equal(0, Demand.Compute(stats, config, businessClimate: 0).Commercial);
        var slow = Demand.Compute(stats, config, attraction: 0.1, businessClimate: 0.1);
        Assert.InRange(slow.Residential, 0.01, 0.2);
        Assert.InRange(slow.Commercial, 0.01, 0.3);
        Assert.True(Demand.Compute(stats, config).Commercial > slow.Commercial);
    }

    [Fact]
    public void DemandReadsDoNotChangeSimulationRandomnessOrSaveState()
    {
        var game = TestCity.Flat();
        string before = TermCity.Core.Persistence.SaveGameStore.Serialize(game);
        for (int i = 0; i < 10; i++)
        {
            _ = game.Demand;
            _ = CityReport.ZoneDetails(game, ZoneType.Residential);
        }
        Assert.Equal(before, TermCity.Core.Persistence.SaveGameStore.Serialize(game));
    }

    [Fact]
    public void PositiveDemandSeparatelyExplainsAnUnusableCommercialPlot()
    {
        var game = TestCity.Flat();
        var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        for (int x = 10; x < 30; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, home);
        }
        game.Touch();
        var highway = game.Map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
        foreach (var cell in CellLines.Between(new(30, 5), new(50, 15)))
            game.Map.SetRoad(cell.X, cell.Y, highway);
        game.Touch();
        var partial = Enumerable.Range(0, game.Map.Width * game.Map.Height).Select(game.Map.PosOf).First(p =>
            !game.Map.HasRoad(p.X, p.Y) && game.Map.BuildingAt(p.X, p.Y) is null &&
            game.Network.RoadOverlapsCell(game.Map.Index(p.X, p.Y)));
        game.Map.SetZone(partial.X, partial.Y, ZoneType.Commercial);
        game.Touch();
        Assert.True(game.Demand.Commercial > 0);
        Assert.Equal(GrowthStatus.RoadOverlap, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
        Assert.Contains("angled road", CityReport.ZoneDetails(game, ZoneType.Commercial));
        game.AdvanceWeek();
        Assert.Equal(0, game.Stats.Commercial.Filled);
    }

    [Fact]
    public void FullyBuiltMultiCellZonesAskForLandRatherThanReportingUnreachablePlots()
    {
        var game = TestCity.Flat();
        var plot = new CellRect(10, 21, 2, 1);
        game.Designate(plot, ZoneType.Residential);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.ForZone(ZoneType.Residential)!, plot);
        game.Map.SetHousehold(10, 21, new(2, 2, 1));
        game.Touch();
        Assert.True(game.Demand.Residential > 0);
        Assert.Equal(GrowthStatus.NoVacancies, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        Assert.Contains("Zone more land.", CityReport.ZoneDetails(game, ZoneType.Residential));
    }
}
