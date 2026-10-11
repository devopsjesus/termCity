using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MilestoneDialogTests
{
    [Fact]
    public void CrossingThresholdPausesAndOpensReachedMilestoneWithBuildingTables()
    {
        var game = TestCity.Flat();
        var session = new GameSession(game);
        for (int x = 0; x < 20; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
            game.Map.SetHousehold(x, 21, new Household(3, 2, 0));
        }
        game.Touch();
        Assert.True(game.Paused);
        Assert.Equal(MilestoneReport.Title, session.Prompt!.Title);
        Assert.Equal(0, session.Prompt.ActiveTab);
        var page = MilestoneReport.Build(game, 0);
        foreach (string building in new[] { "FIRE WATCH", "WATCH HOUSE", "SHERIFF'S HALL", "APOTHECARY", "CHANTRY SCHOOL" })
        {
            var group = page.Groups.Single(group => group.Title == building);
            Assert.Contains(group.Rows, row => row[0].Text == "Cost");
            Assert.Contains(group.Rows, row => row[0].Text == "Coverage");
        }
        session.SelectPrompt(1);
        Assert.Equal(1, session.Prompt!.ActiveTab);
        Assert.Equal("Village", session.Prompt.Tabs![1].Title);
        session.SelectPrompt(0);
        Assert.Equal(0, session.Prompt!.ActiveTab);
        session.SelectPrompt(2);
        Assert.Null(session.Prompt);
        Assert.True(game.Paused);
        game.Touch();
        Assert.Null(session.Prompt);
    }

    [Fact]
    public void EveryMilestoneListsExactlyItsNewlyUnlockedBuildings()
    {
        var game = TestCity.Flat();
        for (int index = 0; index < CityProgression.Milestones.Count; index++)
        {
            var page = MilestoneReport.Build(game, index);
            var expected = game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
                building.MinPopulation > 0 &&
                CityProgression.RequiredPopulation(building) == CityProgression.Milestones[index].Population);
            foreach (var building in expected)
                Assert.Contains(page.Groups, group => group.Title == building.Name.ToUpperInvariant());
            Assert.All(page.Groups.SelectMany(group => group.Rows), row => Assert.Equal(2, row.Length));
            Assert.All(page.Groups.SelectMany(group => group.Rows), row =>
            {
                Assert.Equal(TermCity.Core.Util.Rgb.Hex(0x70d7ff), row[0].Color);
                Assert.Equal(TermCity.Core.Util.Rgb.Hex(0x9fb4c7), row[1].Color);
            });
        }
    }
}
