using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class CastleTests
{
    private static CityGame Town()
    {
        var game = TestCity.Flat(1, new GameConfig { StartingMoney = 5_000_000, DefaultTaxRate = 0.09 }, CityRules.Full);
        Assert.True(game.Designate(new CellRect(10, 18, 50, 2), ZoneType.Residential).Success);
        Assert.True(game.Designate(new CellRect(10, 21, 50, 2), ZoneType.Residential).Success);
        return game;
    }

    private static BuildingType Type(CityGame game, string name) => game.Map.Content.Buildings.Get(name);

    [Fact]
    public void ServiceKindsCoverTheMedievalAdditions()
    {
        Assert.Equal(12, ServiceKinds.Count);
        Assert.Contains(ServiceKind.Defence, ServiceKinds.Area);
        Assert.Contains(ServiceKind.Faith, ServiceKinds.Area);
        Assert.Contains(ServiceKind.Trade, ServiceKinds.Area);
        Assert.True(ServiceKind.Granary.IsUtility());
        Assert.False(ServiceKind.Defence.IsUtility());
    }

    [Fact]
    public void CastleTiersAreRankedAndGatedByTownSize()
    {
        var game = Town();
        Assert.Equal(1, Type(game, "Motte and Bailey").SeatRank);
        Assert.Equal(2, Type(game, "Stone Keep").SeatRank);
        Assert.Equal(3, Type(game, "Castle").SeatRank);
        Assert.Equal(0, Type(game, "Motte and Bailey").MinPopulation);
        Assert.True(Type(game, "Castle").MinPopulation > Type(game, "Stone Keep").MinPopulation);

        var refused = game.PlaceBuilding(Type(game, "Castle"), new CellRect(62, 19, 1, 1));
        Assert.False(refused.Success);
        Assert.Contains("souls", refused.Message);
        Assert.Equal(0, game.Map.BuildingLayer[game.Map.Index(30, 19)]);
    }

    [Fact]
    public void AMottaAndBaileyGarrisonsTheLandAndRaisesTheSeat()
    {
        var game = Town();
        Assert.Equal(0, game.Services.SeatRank);
        int near = game.Map.Index(55, 18), far = game.Map.Index(10, 22);
        Assert.Equal(0, game.Services.Coverage(ServiceKind.Defence, near));

        Assert.True(game.PlaceBuilding(Type(game, "Motte and Bailey"), new CellRect(62, 19, 1, 1)).Success);
        game.Touch();

        Assert.Equal(1, game.Services.SeatRank);
        Assert.True(game.Services.Coverage(ServiceKind.Defence, near) > 40);
        Assert.Equal(0, game.Services.Coverage(ServiceKind.Defence, far));
    }

    [Fact]
    public void SeatPullGrowsWithTheCastleTier()
    {
        Assert.Equal(1, CityAnalysis.SeatPull(0));
        Assert.True(CityAnalysis.SeatPull(1) > CityAnalysis.SeatPull(0));
        Assert.True(CityAnalysis.SeatPull(3) > CityAnalysis.SeatPull(2));
        Assert.Equal(CityAnalysis.SeatPull(3), CityAnalysis.SeatPull(9));
    }

    [Fact]
    public void DefenceAndFaithCoverageMakeHomesHappierAndSafer()
    {
        var game = Town();
        int cell = game.Map.Index(55, 18);
        double bareValue = CityAnalysis.LandValue(game, cell);

        Assert.True(game.PlaceBuilding(Type(game, "Motte and Bailey"), new CellRect(62, 19, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(Type(game, "Chapel"), new CellRect(64, 19, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(Type(game, "Market Cross"), new CellRect(66, 19, 1, 1)).Success);
        game.Touch();

        Assert.True(CityAnalysis.LandValue(game, cell) > bareValue + 5);
        Assert.True(game.Services.Coverage(ServiceKind.Faith, cell) > 0);
        Assert.True(game.Services.Coverage(ServiceKind.Trade, cell) > 0);
    }

    [Fact]
    public void BudgetFundsTheNewServicesAndKeepsGranariesFree()
    {
        var budget = new Budget();
        budget.SetFunding(ServiceKind.Defence, 0.5);
        budget.SetFunding(ServiceKind.Faith, 0.25);
        Assert.Equal(0.5, budget.Funding(ServiceKind.Defence));
        Assert.Equal(0.25, budget.Funding(ServiceKind.Faith));
        Assert.Equal(1, budget.Funding(ServiceKind.Trade));
        Assert.Equal(1, budget.Funding(ServiceKind.Granary));
        Assert.Equal(ServiceKinds.Count, budget.Snapshot().Length);
    }

    [Fact]
    public void IncompleteFundingIsRejected()
    {
        var budget = new Budget();
        Assert.Throws<ArgumentException>(() => budget.Restore([0.4, 1, 1, 0.3, 1, 1, 1, 1]));
    }

    [Fact]
    public void ACastleSavesAndLoads()
    {
        var game = Town();
        Assert.True(game.PlaceBuilding(Type(game, "Motte and Bailey"), new CellRect(62, 19, 1, 1)).Success);
        game.Touch();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(1, loaded.Services.SeatRank);
    }

    [Fact]
    public void OverlaysAndNamesExistForTheNewServices()
    {
        Assert.Equal("Garrison", TermCity.Core.Rendering.CityReport.ServiceName(ServiceKind.Defence));
        Assert.Contains("garrison", TermCity.Core.Rendering.MapOverlays.Label(TermCity.Core.Rendering.MapOverlay.Defence));
    }
}
