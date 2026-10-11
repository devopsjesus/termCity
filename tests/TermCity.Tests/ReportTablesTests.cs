using TermCity.Core.Session;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class ReportTablesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportTablesKeepSizeProgressAndGuideContainsMilestones(bool completedWeek)
    {
        var game = TestCity.Flat();
        if (completedWeek) TestCity.Advance(game, 1);
        var session = new GameSession(game);
        var sections = ReportTables.Build(session);
        Assert.Single(sections);
        Assert.All(sections.SelectMany(section => section.Rows), row => Assert.Equal(2, row.Length));
        var summary = sections[0].Rows;
        Assert.Equal($"{game.Stats.Population:N0}", summary.Single(row => row[0].Text == "Population")[1].Text);
        var pages = ReportContent.Build(game);
        Assert.Equal(["Overview", "Population", "Economy", "Services", "Growth", "Food & Seasons", "Events"], pages.Select(page => page.Title));
        var last = pages.Single(page => page.Title == "Population").Groups.Single(group => group.Title == "LAST COMPLETED WEEK");
        if (completedWeek)
            Assert.Equal(Fmt.Money(game.LastReport!.Income), last.Rows.Single(row => row[0].Text == "Income")[1].Text);
        else Assert.Equal("No completed week yet.", last.Rows[0][1].Text);
        Assert.All(pages.Single(page => page.Title == "Growth").Groups, zone => Assert.Equal(8, zone.Rows.Count));
        string milestones = GuideContent.Tabs(game).Single(tab => tab.Title == "Milestones").Text;
        foreach (var milestone in CityProgression.Milestones)
        {
            Assert.Contains($"{milestone.Name}: {milestone.Population:N0} souls", milestones);
            Assert.Contains(CityProgression.Unlocks(game, milestone), milestones.Replace("\n", " "));
        }
        Assert.Equal(CityProgression.SizeProgress(game).CurrentName, summary.Single(row => row[0].Text == "Current size")[1].Text);
        Assert.DoesNotContain(sections.SelectMany(section => section.Rows), row => row[0].Text == "Unlocks");
    }

    [Theory]
    [InlineData(CityRules.Classic)]
    [InlineData(CityRules.Full)]
    public void ReportTabsShowActualPopulationAndItemizedFinance(CityRules rules)
    {
        var game = TestCity.Flat(rules: rules);
        var session = new GameSession(game);
        session.ShowReport();
        Assert.Equal(7, session.Prompt!.Tabs!.Count);
        for (int index = 0; index < 7; index++)
        {
            session.SelectTab(index);
            var rendered = ReportTables.Build(session);
            var expected = ReportContent.Build(game)[index];
            Assert.Equal(expected.Groups.Select(group => group.Title), rendered.Select(group => group.Title));
            Assert.All(rendered.SelectMany(group => group.Rows), row => Assert.Equal(2, row.Length));
        }
        var pages = ReportContent.Build(game);
        var population = pages.Single(page => page.Title == "Population").Groups[0].Rows;
        Assert.Equal($"{game.Stats.Adults:N0}", population.Single(row => row[0].Text == "Adults")[1].Text);
        var economy = pages.Single(page => page.Title == "Economy").Groups[0].Rows;
        Assert.Equal(Fmt.Money(game.Finance.Expenses), economy.Single(row => row[0].Text == "Expenses")[1].Text);
        Assert.Equal(Fmt.Money(game.Finance.ServiceUpkeep), economy.Single(row => row[0].Text == "Services")[1].Text);
        Assert.Equal(Fmt.Money(game.Finance.Interest), economy.Single(row => row[0].Text == "Loan interest")[1].Text);
        Assert.DoesNotContain(pages.SelectMany(page => page.Groups).SelectMany(group => group.Rows), row => row[0].Text == "Unlocks");
        if (rules == CityRules.Classic)
            Assert.Contains("do not simulate services", pages.Single(page => page.Title == "Services").Text);
    }

    [Theory]
    [InlineData(0, 0, 100, 0)]
    [InlineData(50, 0, 100, 0.5)]
    [InlineData(300, 100, 500, 0.5)]
    [InlineData(50, 100, 500, 0)]
    [InlineData(600, 100, 500, 1)]
    public void SizeProgressUsesTheCurrentToNextIntervalAndClamps(int population, int from, int target, double expected)
    {
        Assert.Equal(expected, new CitySizeProgress("Current", "Next", population, from, target).Fraction);
    }

    [Fact]
    public void LargestCitySizeHasCompletedProgress() =>
        Assert.Equal(1, new CitySizeProgress("Great City", null, 10_000, 10_000, null).Fraction);

    [Fact]
    public void FoodTabReportsFamineReservesAndSeasonalEffects()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Hunger = 0.4;
        game.GrainWeeks = 0;
        var food = ReportContent.Build(game).Single(page => page.Title == "Food & Seasons");
        var rows = food.Groups[0].Rows;
        Assert.Equal("FAMINE", rows.Single(row => row[0].Text == "Food status")[1].Text);
        Assert.Equal("0.0 weeks", rows.Single(row => row[0].Text == "Grain reserve")[1].Text);
        Assert.Contains("40", rows.Single(row => row[0].Text == "Hunger")[1].Text);
        Assert.Equal(4, food.Groups.Count(group => Enum.GetNames<Season>().Any(name => name.ToUpperInvariant() == group.Title)));
        Assert.Contains("No harvest or famine events", food.Text);
    }
}
