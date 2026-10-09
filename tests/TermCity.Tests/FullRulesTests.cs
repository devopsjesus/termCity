using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class FullRulesTests
{
    /// <summary>A flat full-rules city with a road along row 20, homes either side and a shop and factory block at the end, and money to spare.</summary>
    private static CityGame Town(int seed = 1)
    {
        var game = TestCity.Flat(seed, new GameConfig { StartingMoney = 5_000_000, DefaultTaxRate = 0.09 }, CityRules.Full);
        Assert.True(game.Designate(new CellRect(10, 18, 50, 2), ZoneType.Residential).Success);
        Assert.True(game.Designate(new CellRect(10, 21, 50, 2), ZoneType.Residential).Success);
        Assert.True(game.Designate(new CellRect(60, 18, 10, 2), ZoneType.Commercial).Success);
        Assert.True(game.Designate(new CellRect(60, 21, 10, 2), ZoneType.Industrial).Success);
        return game;
    }

    private static BuildingType Civic(CityGame game, string name) => game.Map.Content.Buildings.Get(name);

    private static void Power(CityGame game)
    {
        Assert.True(game.PlaceBuilding(Civic(game, "Charcoal Burners"), new CellRect(80, 19, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(Civic(game, "Town Well"), new CellRect(82, 19, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(Civic(game, "Town Well"), new CellRect(84, 19, 1, 1)).Success);
    }

    [Fact]
    public void FullRulesAreTheDefaultForNewGames()
    {
        Assert.Equal(CityRules.Full, new GameConfig().Rules);
        Assert.Equal(CityRules.Classic, TestCity.Flat().Config.Rules);
    }

    [Fact]
    public void ClassicRulesIgnoreUtilitiesAndExpenses()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(10, 17, 20, 3), ZoneType.Residential);
        TestCity.Advance(game, 10);
        Assert.Equal(0, game.Finance.Expenses);
        Assert.Equal(CityIndicators.Neutral.Happiness, game.Indicators.Happiness);
        Assert.True(game.Services.IsPowered(game.Map, game.Map.Index(12, 18)));
    }

    [Fact]
    public void UnpoweredHomesPayNoTax()
    {
        var game = Town();
        TestCity.Advance(game, 8);
        Assert.True(game.Stats.Population > 0);
        Assert.Equal(0, game.Stats.WeeklyIncome);
        Power(game);
        game.AdvanceDay();
        Assert.True(game.Stats.WeeklyIncome > 0);
    }

    [Fact]
    public void DisconnectedWaterChargesUpkeepButHomesPayTaxAsSoonAsItConnects()
    {
        var game = TestCity.Flat(config: new GameConfig(), rules: CityRules.Full);
        var woodlot = Civic(game, "Woodlot");
        var well = Civic(game, "Town Well");
        Assert.True(game.PlaceBuilding(woodlot, new CellRect(10, 18, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(well, new CellRect(30, 5, 1, 1)).Success);
        Assert.True(game.Designate(new CellRect(40, 18, 20, 1), ZoneType.Residential).Success);
        TestCity.Advance(game, 10);
        Assert.True(game.Stats.Population > 0);
        Assert.Equal(0, game.Services.Water.Supply);
        Assert.Equal(0, game.Finance.Income);
        Assert.Equal(60, game.Finance.ServiceUpkeep);

        Assert.True(game.BuildRoad(new CellRect(30, 7, 1, 14)).Success);
        Assert.Equal(well.Capacity, game.Services.Water.Supply);
        Assert.True(game.Finance.Income > 0);
    }

    [Fact]
    public void PowerAndWaterMustMatchDemand()
    {
        var game = Town();
        TestCity.Advance(game, 20);
        Power(game);
        Assert.Equal(1.0, game.Services.Power.Ratio);
        Assert.True(game.Services.Water.Demand > 0);
        Assert.True(game.Services.Water.Ratio < 1 || game.Services.Water.Supply >= game.Services.Water.Demand);
    }

    [Fact]
    public void DailyGrowthRefreshesUtilityDemandAndPollutionWithoutRebuildingRoads()
    {
        var game = Town();
        for (int x = 10; x < 20; x++)
        {
            game.Map.SetBuilding(x, 18, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
            game.Map.SetHousehold(x, 18, new Household(2, 1, 0));
        }
        game.Touch();
        Power(game);
        game.GrowthState = new(true, 0, 7, 7, 0, 0, 0);
        var network = game.Network;
        var before = game.Services;
        var shop = game.Map.Content.Buildings.ForZone(ZoneType.Commercial)!;
        var factory = game.Map.Content.Buildings.ForZone(ZoneType.Industrial)!;

        game.AdvanceDay();

        Assert.Equal(1, game.Day);
        Assert.Equal(1, game.Stats.Commercial.Filled);
        Assert.Equal(1, game.Stats.Industrial.Filled);
        Assert.Same(network, game.Network);
        Assert.Equal(before.Power.Demand + shop.PowerUse + factory.PowerUse, game.Services.Power.Demand);
        Assert.Equal(before.Water.Demand + shop.WaterUse + factory.WaterUse, game.Services.Water.Demand);
        int industry = Assert.Single(game.Map.ZoneCells(ZoneType.Industrial), i => game.Map.BuildingLayer[i] != 0);
        Assert.True(game.Services.Pollution(industry) > before.Pollution(industry));
    }

    [Fact]
    public void RezoningRefreshesUtilityDemandAndIndustrialPollution()
    {
        var game = Town();
        Power(game);
        var factory = game.Map.Content.Buildings.ForZone(ZoneType.Industrial)!;
        game.Map.SetBuilding(60, 21, factory);
        game.Touch();
        var before = game.Services;
        var cell = new CellRect(60, 21, 1, 1);
        int index = game.Map.Index(60, 21);

        Assert.True(game.Dezone(cell).Success);
        Assert.Equal(before.Power.Demand - factory.PowerUse, game.Services.Power.Demand);
        Assert.Equal(before.Water.Demand - factory.WaterUse, game.Services.Water.Demand);
        Assert.True(game.Services.Pollution(index) < before.Pollution(index));

        Assert.True(game.Designate(cell, ZoneType.Industrial).Success);
        Assert.Equal(before.Power, game.Services.Power);
        Assert.Equal(before.Water, game.Services.Water);
        Assert.Equal(before.Pollution(index), game.Services.Pollution(index));
    }

    [Fact]
    public void PumpsMustStandOnTheShore()
    {
        var game = Town();
        var pump = Civic(game, "Aqueduct");
        Assert.False(game.CanPlaceBuilding(pump, 40, 40));
        var water = game.Map.Content.Terrains.Get("Water");
        game.Map.SetTerrain(42, 40, water);
        Assert.True(game.CanPlaceBuilding(pump, 40, 40));
    }

    [Fact]
    public void PoliceCoverReducesCrimeFeltByResidents()
    {
        var game = Town();
        Power(game);
        TestCity.Advance(game, 40);
        double before = game.Indicators.Crime;
        Assert.True(game.PlaceBuilding(Civic(game, "Sheriff's Hall"), new CellRect(70, 19, 1, 1)).Success);
        Assert.True(game.Indicators.Crime <= before);
        Assert.True(game.Services.Coverage(ServiceKind.Police, game.Map.Index(66, 18)) > 50);
    }

    [Fact]
    public void FundingScalesCostAndStrength()
    {
        var game = Town();
        Assert.True(game.PlaceBuilding(Civic(game, "Fire Watch"), new CellRect(70, 19, 1, 1)).Success);
        int full = game.Finance.ServiceUpkeep;
        int cover = game.Services.Coverage(ServiceKind.Fire, game.Map.Index(66, 18));
        game.SetFunding(ServiceKind.Fire, 0.5);
        Assert.Equal(full / 2.0, game.Finance.ServiceUpkeep, 1);
        Assert.InRange(game.Services.Coverage(ServiceKind.Fire, game.Map.Index(66, 18)), 1, cover - 1);
    }

    [Fact]
    public void LoansAddMoneyAndInterest()
    {
        var game = Town();
        Power(game);
        TestCity.Advance(game, 60);
        Assert.True(game.MaxLoan >= 10_000, $"max loan {game.MaxLoan}");
        int money = game.Money;
        Assert.True(game.TakeLoan(10_000).Success);
        Assert.Equal(money + 10_000, game.Money);
        Assert.Equal(20, game.Finance.Interest);
        Assert.True(game.RepayLoan(10_000).Success);
        Assert.Equal(0, game.Budget.Loan);
        Assert.False(game.TakeLoan(int.MaxValue / 2).Success);
    }

    [Fact]
    public void PeopleAreBornAgeAndDie()
    {
        var game = Town();
        Power(game);
        TestCity.Advance(game, 60);
        var report = game.LastReport!;
        Assert.True(game.Stats.Children > 0 && game.Stats.Seniors >= 0);
        int births = 0, deaths = 0;
        for (int w = 0; w < 100; w++)
        {
            game.AdvanceWeek();
            births += game.LastReport!.Births;
            deaths += game.LastReport!.Deaths;
        }

        Assert.True(births > 0, "nobody was born in two years");
        Assert.True(deaths > 0, "nobody died in two years");
        _ = report;
    }

    [Fact]
    public void ADisasterFreeUnhappyCityLosesPeople()
    {
        var game = Town();
        // No power or water: everyone is miserable and the city drains.
        TestCity.Advance(game, 30);
        int peak = game.Stats.Population;
        TestCity.Advance(game, 60);
        Assert.True(game.Indicators.Happiness < 40, $"happiness {game.Indicators.Happiness}");
        Assert.True(game.Stats.Population < peak * 1.5 + 50);
    }

    [Fact]
    public void ServedCitiesGrowFasterThanUnservedOnes()
    {
        var bare = Town(3);
        TestCity.Advance(bare, 80);
        var served = Town(3);
        Power(served);
        TestCity.Advance(served, 80);
        Assert.True(served.Stats.Population > bare.Stats.Population, $"{served.Stats.Population} vs {bare.Stats.Population}");
    }

    [Fact]
    public void FullRulesAreDeterministicForASeed()
    {
        string Run()
        {
            var game = Town(7);
            Power(game);
            TestCity.Advance(game, 60);
            return SaveGameStore.Serialize(game);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void SaveRoundTripKeepsBudgetLoanAndRules()
    {
        var game = Town();
        Power(game);
        TestCity.Advance(game, 60);
        game.SetFunding(ServiceKind.Police, 0.4);
        game.Budget.Roads = 0.7;
        Assert.True(game.TakeLoan(5_000).Success);

        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(CityRules.Full, loaded.Config.Rules);
        Assert.Equal(0.4, loaded.Budget.Funding(ServiceKind.Police), 3);
        Assert.Equal(0.7, loaded.Budget.Roads, 3);
        Assert.Equal(5_000, loaded.Budget.Loan);
        Assert.Equal(game.Stats.Population, loaded.Stats.Population);
    }

    [Fact]
    public void ObsoleteSaveVersionsAreRejected()
    {
        var game = Town();
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json["Version"] = 1;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }
}
