using TermCity.Core.Session;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class BuildingAffordabilityTests
{
    [Fact]
    public void OpenApothecaryMenuRespondsToGainingAndLosingFundsWithoutReplacement()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 100;
        game.Money = 7_999;
        var session = new GameSession(game);
        var apothecary = game.Map.Content.Buildings.Get("Apothecary");
        session.ShowPrompt("Physic", "", [new("Apothecary", () => session.PreviewBuilding(apothecary))]);
        var prompt = session.Prompt;
        Assert.NotNull(session.BuildingPurchaseError(apothecary));
        game.Money = 8_000;
        game.NotifyStateChanged();
        Assert.Null(session.BuildingPurchaseError(apothecary));
        Assert.Same(prompt, session.Prompt);
        game.Money = 7_999;
        game.NotifyStateChanged();
        Assert.NotNull(session.BuildingPurchaseError(apothecary));
        Assert.Same(prompt, session.Prompt);
    }

    [Fact]
    public void MotteAndBaileyCannotStartPlacementAt12070Gold()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 10_000;
        game.Money = 12_070;
        var session = new GameSession(game);
        var motte = game.Map.Content.Buildings.Get("Motte and Bailey");
        Assert.Equal(14_000, motte.Cost);
        session.ShowPrompt("Buildings", "", [new("Cancel", session.CancelPrompt)]);
        var prompt = session.Prompt;
        session.PreviewBuilding(motte);
        Assert.Null(session.Preview);
        Assert.Same(prompt, session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Contains("14,000g", session.Message);
        Assert.Contains("12,070g", session.Message);
        Assert.Equal(12_070, game.Money);
        game.Money = 14_000;
        Assert.Null(session.BuildingPurchaseError(motte));
        session.PreviewBuilding(motte);
        Assert.True(session.BuildingToolActive);
        Assert.Equal(14_000, game.Money);
    }

    [Fact]
    public void AllPurchasableBuildingsRejectFundsBelowTheirBaseCost()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 10_000;
        var session = new GameSession(game);
        foreach (var building in game.Map.Content.Buildings.Where(type => type.PlayerPlaceable && type.Cost > 0))
        {
            game.Money = building.Cost - 1;
            Assert.Contains("Not enough gold", session.BuildingPurchaseError(building));
            game.Money = building.Cost;
            Assert.Null(session.BuildingPurchaseError(building));
        }
    }
}
