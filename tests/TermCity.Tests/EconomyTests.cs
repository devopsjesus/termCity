using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class EconomyTests
{
    [Fact]
    public void StartsWithFiftyThousandDollars()
    {
        Assert.Equal(50_000, TestCity.Flat().Money);
    }

    [Fact]
    public void RoadsCostFiveHundredPerCell()
    {
        var game = TestCity.Flat();
        var result = game.BuildRoad(new CellRect(10, 18, 1, 2));
        Assert.True(result.Success, result.Message);
        Assert.Equal(1000, result.Cost);
        Assert.Equal(49_000, game.Money);
        Assert.True(game.Map.HasRoad(10, 18));
        Assert.True(game.Map.HasRoad(10, 19));
    }

    [Fact]
    public void RoadsOnHillsCostMore()
    {
        var game = TestCity.Flat();
        game.Map.SetTerrain(5, 5, game.Map.Content.Terrains.Get("Hill"));
        Assert.Equal(750, game.RoadCostAt(5, 5));
    }

    [Fact]
    public void ZoningIsFree()
    {
        var game = TestCity.Flat();
        var result = game.Designate(new CellRect(10, 10, 5, 5), ZoneType.Residential);
        Assert.True(result.Success);
        Assert.Equal(50_000, game.Money);
        Assert.Equal(25, game.Stats.Residential.Zoned);
    }

    [Fact]
    public void CannotBuildWithoutEnoughMoneyAndNothingIsChanged()
    {
        var game = TestCity.Flat();
        var result = game.BuildRoad(new CellRect(0, 0, 101, 1)); // $50,500
        Assert.False(result.Success);
        Assert.Equal(50_000, game.Money);
        Assert.False(game.Map.HasRoad(0, 0));
    }

    [Fact]
    public void ExactlySpendingAllMoneyThenBlocksFurtherBuilding()
    {
        var game = TestCity.Flat();
        Assert.True(game.BuildRoad(new CellRect(0, 0, 100, 1)).Success);
        Assert.Equal(0, game.Money);

        var blocked = game.BuildRoad(new CellRect(0, 2, 1, 1));
        Assert.False(blocked.Success);
        Assert.Contains("out of gold", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(game.Map.HasRoad(0, 2));
    }

    [Fact]
    public void WaterAndExistingRoadsAreSkipped()
    {
        var game = TestCity.Flat();
        game.Map.SetTerrain(3, 3, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var quote = game.QuoteRoad(new CellRect(3, 3, 1, 1));
        Assert.Equal(0, quote.Cells);
        Assert.False(game.BuildRoad(new CellRect(3, 3, 1, 1)).Success);
        Assert.False(game.BuildRoad(new CellRect(3, 20, 1, 1)).Success);
        Assert.False(game.Designate(new CellRect(3, 3, 1, 1), ZoneType.Commercial).Success);
    }

    [Fact]
    public void BuildingOverTreesClearsThem()
    {
        var game = TestCity.Flat();
        game.Map.SetFeature(4, 4, game.Map.Content.Features.Get("Tree"));
        Assert.True(game.BuildRoad(new CellRect(4, 4, 1, 1)).Success);
        Assert.Null(game.Map.FeatureAt(4, 4));
    }

    [Fact]
    public void DemolishClearsEverythingInTheArea()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(5, 5, 3, 3), ZoneType.Industrial);
        Assert.True(game.Demolish(new CellRect(5, 5, 3, 3)).Success);
        Assert.Equal(0, game.Stats.Industrial.Zoned);
        Assert.False(game.Demolish(new CellRect(5, 5, 3, 3)).Success);
    }

    [Fact]
    public void WeeklyTaxIsFivePercentOfAResidentialCellsTwoHundredDollarValue()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(0, 21, 10, 1), ZoneType.Residential);
        TestCity.Advance(game, 1);
        // Three families move in the first week: 3 cells x $200 x 5% = $30.
        Assert.Equal(3, game.Stats.Residential.Filled);
        Assert.Equal(30, game.LastReport!.Income);
        Assert.Equal(50_030, game.Money);
    }

    [Fact]
    public void IncomeScalesWithTaxRate()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(0, 21, 10, 1), ZoneType.Residential);
        TestCity.Advance(game, 1);
        game.Taxes.Residential = 0.10;
        game.Touch();
        Assert.Equal(60, game.Stats.WeeklyIncome);
    }

    [Fact]
    public void WeeklyValuesPerFilledCellMatchSpec()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var buildings = map.Content.Buildings;
        void Fill(int x, int y, ZoneType z)
        {
            map.SetZone(x, y, z);
            map.SetBuilding(x, y, buildings.ForZone(z));
        }

        for (int x = 0; x < 10; x++)
        {
            Fill(x, 22, ZoneType.Residential);
            Fill(x, 23, ZoneType.Commercial);
            Fill(x, 24, ZoneType.Industrial);
        }

        game.Touch();
        // 10 x ($200 + $350 + $500) x 5%
        Assert.Equal(525, game.Stats.WeeklyIncome);
    }
}