using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class BuildingStatusTests
{
    [Fact]
    public void ConnectedBuildingShowsOnlyActiveAndDisconnectedShowsRed()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var building = game.Map.Content.Buildings.Get("Woodlot");
        Assert.True(game.PlaceBuilding(building, new(80, 18, 1, 1)).Success);
        var active = CellInspector.Status(game, new Pos(80, 18))!;
        Assert.Equal("Active", active.Text);
        Assert.Equal(Rgb.Hex(0x88ee99), active.Color);
        Assert.DoesNotContain("Active (", CellInspector.Summary(game, new Pos(80, 18)));
        Assert.True(game.PlaceBuilding(building, new(30, 5, 1, 1)).Success);
        var inactive = CellInspector.Status(game, new Pos(30, 5))!;
        Assert.Contains("Disconnected", inactive.Text);
        Assert.Equal(Rgb.Hex(0xff7777), inactive.Color);
    }

    [Fact]
    public void ReducedFundingIsYellowAndNoFundingIsRed()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 100;
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Fire Watch"), new(80, 18, 1, 1)).Success);
        game.Budget.SetFunding(ServiceKind.Fire, 0.5);
        game.Touch();
        Assert.Equal(Rgb.Hex(0xffe066), CellInspector.Status(game, new Pos(80, 18))!.Color);
        game.Budget.SetFunding(ServiceKind.Fire, 0);
        game.Touch();
        Assert.Equal("Inactive: no funding", CellInspector.Status(game, new Pos(80, 18))!.Text);
        Assert.Equal(Rgb.Hex(0xff7777), CellInspector.Status(game, new Pos(80, 18))!.Color);
    }
}
