using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MilestoneDialogTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    public void MilestonesWithoutUnlocksHaveOnlyTheSummary(int population)
    {
        var game = TestCity.Flat();
        game.HighestMilestone = population;
        int index = CityProgression.Milestones.ToList().FindIndex(milestone => milestone.Population == population);
        var page = MilestoneReport.Build(game, index);
        var summary = Assert.Single(page.Groups);
        Assert.Equal(CityProgression.Milestones[index].Name.ToUpperInvariant(), summary.Title);
        Assert.Contains(summary.Rows, row => row[0].Text == "Population");
        Assert.Contains(summary.Rows, row => row[0].Text == "Gold grant");
        Assert.DoesNotContain("UNLOCKED BUILDINGS", page.Text);
        Assert.DoesNotContain("All earlier buildings remain available.", page.Text);
    }

    [Fact]
    public void CrossingThresholdPausesAndOpensReachedMilestoneWithBuildingTables()
    {
        var game = TestCity.Flat();
        var session = new GameSession(game);
        for (int x = 0; x < 50; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
            game.Map.SetHousehold(x, 21, new Household(3, 2, 0));
        }
        game.Touch();
        Assert.True(game.Paused);
        Assert.Equal(MilestoneReport.Title, session.Prompt!.Title);
        Assert.Equal(1, session.Prompt.ActiveTab);
        var page = MilestoneReport.Build(game, 1);
        foreach (string building in new[] { "FIRE WATCH", "WATCH HOUSE", "SHERIFF'S HALL", "APOTHECARY", "CHANTRY SCHOOL" })
        {
            var group = page.Groups.Single(group => group.Title == building);
            Assert.Contains(group.Rows, row => row[0].Text == "Cost");
            Assert.Contains(group.Rows, row => row[0].Text == "Coverage");
        }
        Assert.Null(session.Prompt.Tabs);
        Assert.Equal("Continue", Assert.Single(session.Prompt.Choices).Label);
        session.SelectPrompt(0);
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
            int threshold = CityProgression.Milestones[index].Population;
            game.HighestMilestone = threshold - 1;
            var lockedBefore = game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
                !CityProgression.IsUnlocked(game, building)).ToArray();
            game.HighestMilestone = threshold;
            var expected = lockedBefore.Where(building => CityProgression.IsUnlocked(game, building))
                .Select(building => building.Name.ToUpperInvariant()).Order().ToArray();
            var buildingTitles = game.Map.Content.Buildings.Select(building => building.Name.ToUpperInvariant()).ToHashSet();
            var actual = page.Groups.Where(group => buildingTitles.Contains(group.Title)).Select(group => group.Title).Order();
            Assert.Equal(expected, actual);
            Assert.All(page.Groups.SelectMany(group => group.Rows), row => Assert.Equal(2, row.Length));
            Assert.All(page.Groups.SelectMany(group => group.Rows), row =>
            {
                Assert.Equal(TermCity.Core.Util.Rgb.Hex(0x70d7ff), row[0].Color);
                Assert.Equal(TermCity.Core.Util.Rgb.Hex(0x9fb4c7), row[1].Color);
            });
        }
    }

    [Fact]
    public void GrowingHamletShowsOnlyItsFiveCareBuildingsAndCannotBrowseFutureUnlocks()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 250;
        game.HighestPopulation = 250;
        var session = new GameSession(game);
        session.ShowMilestone(1);
        string[] expected = ["APOTHECARY", "CHANTRY SCHOOL", "FIRE WATCH", "SHERIFF'S HALL", "WATCH HOUSE"];
        var page = MilestoneReport.Build(game, 1);
        Assert.Equal(expected, page.Groups.Skip(1).Select(group => group.Title).Order());
        Assert.Null(session.Prompt!.Tabs);
        Assert.Equal("Continue", Assert.Single(session.Prompt.Choices).Label);
        Assert.DoesNotContain("Upcoming", session.Prompt.Text);
        Assert.DoesNotContain("GRANARY", session.Prompt.Text);
        Assert.DoesNotContain("HOSPICE", session.Prompt.Text);
        Assert.DoesNotContain("PARISH CHURCH", session.Prompt.Text);
        Assert.DoesNotContain("INFIRMARY", session.Prompt.Text);
        Assert.DoesNotContain("WOODLOT", session.Prompt.Text);
        var reached = session.Prompt;
        session.SelectTab(2);
        Assert.Same(reached, session.Prompt);
        session.CycleTab(1);
        Assert.Same(reached, session.Prompt);
        session.ShowMilestone(6);
        Assert.Equal(1, session.Prompt!.ActiveTab);
        Assert.Null(session.Prompt.Tabs);
        session.ShowGuide();
        Assert.Contains("Great City", session.Prompt!.Tabs!.Single(tab => tab.Title == "Milestones").Text);
    }

    [Fact]
    public void AnnouncementTracksThePermanentCurrentMilestoneAfterDecline()
    {
        var game = TestCity.Flat();
        game.HighestMilestone = 250;
        game.HighestPopulation = 250;
        var session = new GameSession(game);
        session.ShowMilestone(6);
        Assert.Contains("GROWING HAMLET", session.Prompt!.Text);
        Assert.Null(session.Prompt.Tabs);
        game.HighestMilestone = 500;
        game.HighestPopulation = 500;
        game.Touch();
        Assert.Null(session.Prompt!.Tabs);
        Assert.Equal(2, session.Prompt.ActiveTab);
        Assert.Contains("GRANARY", session.Prompt.Text);
        Assert.DoesNotContain("INFIRMARY", session.Prompt.Text);
    }
}
