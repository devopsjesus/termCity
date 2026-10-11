using TermCity.Core.Simulation;
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
    public void ExistingBusinessesCanCreateHousingNeedButEmptyZonesCannot()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(0, 0, 0, 0, 0,
            new(20, 0, 20), new(50, 0, 50), new(50, 0, 50), 0, 0, 0);
        Assert.Equal(0, Demand.Compute(stats, config).Residential);
        Assert.True(Demand.Compute(stats with { Industrial = new(50, 5, 50) }, config).Residential > 0);
        Assert.True(Demand.Compute(stats with { Commercial = new(50, 5, 50) }, config).Residential > 0);
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
        Assert.Equal(1, Demand.Compute(stats, config).Commercial);
        Assert.Equal(1, Demand.Compute(stats, config).Industrial);
    }

    [Fact]
    public void DenseCityDemandMatchesActualSupportedBusinessSlots()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Map.SetZone(10, 21, ZoneType.Residential);
        game.Map.SetBuilding(10, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
        game.Map.SetHousehold(10, 21, new Household(60, 20, 0));
        game.Touch();
        Assert.True(game.Designate(new(30, 21, 1, 1), ZoneType.Commercial).Success);
        Assert.Equal(1, game.SupportedCells(ZoneType.Commercial));
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
        Assert.Equal(0, game.Demand.Commercial);
        Assert.True(game.Demand.Industrial > 0);
        double residential = game.Demand.Residential;
        Assert.True(game.Designate(new(40, 21, 20, 1), ZoneType.Industrial).Success);
        Assert.Equal(residential, game.Demand.Residential);
    }

    [Fact]
    public void BusinessPendingRemovalKeepsHousingDemandUntilItsBuildingLeaves()
    {
        var config = new GameConfig { Rules = CityRules.Classic };
        var stats = new CityStats(0, 0, 0, 0, 0,
            new(20, 0, 20), new(0, 0, 0), new(10, 4, 10, 1), 0, 0, 0);
        double before = Demand.Compute(stats, config).Residential;
        Assert.Equal(before, Demand.Compute(stats with { Industrial = new(10, 5, 10) }, config).Residential);
        Assert.True(Demand.Compute(stats with { Industrial = new(10, 4, 10) }, config).Residential < before);
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
}
