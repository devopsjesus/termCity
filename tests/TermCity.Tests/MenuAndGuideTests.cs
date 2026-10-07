using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class MenuAndGuideTests
{
    private static GameSession Session(CityRules rules = CityRules.Full)
    {
        var session = new GameSession(TestCity.Flat(rules: rules));
        session.Game.Paused = true;
        session.PlaceCursor(new Pos(10, 19));
        return session;
    }

    [Fact]
    public void TableAlignsColumnsAndRightAlignsNumbers()
    {
        TableColumn[] columns = ["NAME", TableColumn.Right("COST")];
        var lines = TextTable.Lines(columns, [["Well", "5g"], ["Aqueduct", "120g"]]);
        Assert.Equal(4, lines.Count);
        Assert.Equal("NAME      COST", lines[0]);
        Assert.Equal("────────  ────", lines[1]);
        Assert.Equal("Well        5g", lines[2]);
        Assert.Equal("Aqueduct  120g", lines[3]);
    }

    [Fact]
    public void MenusUseTablesWithoutDashesOrColonsSeparatingTheDescription()
    {
        var session = Session();
        void CheckPrompt()
        {
            var prompt = session.Prompt!;
            Assert.NotNull(prompt.Columns);
            Assert.NotNull(TextTable.ForPrompt(prompt));
            foreach (var choice in prompt.Choices)
            {
                Assert.DoesNotContain(" - ", choice.Label);
                Assert.DoesNotContain(": ", choice.Label);
                Assert.DoesNotContain(" / ", choice.Label);
            }
        }

        session.ShowSessionMenu();
        CheckPrompt();
        session.ShowAreaMenu();
        CheckPrompt();
        for (int i = 0; i < 3; i++)
        {
            session.ShowAreaMenu();
            session.SelectPrompt(i);
            CheckPrompt();
        }

        session.ShowBudgetMenu();
        CheckPrompt();
        session.ShowLoadMenu();
        CheckPrompt();
    }

    [Fact]
    public void GuideHasTabsThatCycleAndWrap()
    {
        var session = Session();
        session.ShowGuide();
        var tabs = session.Prompt!.Tabs!;
        Assert.Equal(["Start", "Zones", "Roads", "Services", "Population", "Happiness", "Economy"], tabs.Select(t => t.Title));
        Assert.Equal(tabs[0].Text, session.Prompt.Text);
        session.CycleTab(1);
        Assert.Equal(1, session.Prompt.ActiveTab);
        Assert.Equal(tabs[1].Text, session.Prompt.Text);
        session.CycleTab(-2);
        Assert.Equal(tabs.Count - 1, session.Prompt.ActiveTab);
        session.SelectTab(4);
        Assert.Equal("Population", session.Prompt.Tabs![session.Prompt.ActiveTab].Title);
        session.SelectPrompt(0);
        Assert.Null(session.Prompt);
    }

    [Fact]
    public void GuideCloseKeepsTheTipAndDismissHidesIt()
    {
        var session = new GameSession(TestCity.Flat(), showGuide: true);
        Assert.True(session.GuideVisible);
        Assert.Null(session.Prompt);
        session.ShowGuide();
        Assert.True(session.GuideVisible);
        session.SelectPrompt(0);
        Assert.True(session.GuideVisible);
        session.ShowGuide();
        Assert.Equal("Dismiss tip", session.Prompt!.Choices[1].Label);
        session.SelectPrompt(1);
        Assert.False(session.GuideVisible);
        session.ShowGuide();
        Assert.False(session.GuideVisible);
        Assert.Single(session.Prompt!.Choices);
    }

    [Fact]
    public void GuidePagesFitTheDialogAndExplainPopulation()
    {
        var session = Session();
        foreach (var tab in GuideContent.Tabs(session.Game))
        {
            Assert.All(tab.Text.Split('\n'), line => Assert.True(line.Length <= GuideContent.MaxLineLength, $"{tab.Title}: {line}"));
        }

        string population = GuideContent.Tabs(session.Game).Single(t => t.Title == "Population").Text;
        foreach (string word in new[] { "Roads", "Homesteads", "Marketplace", "Craftworks", "ATTRACTION", "arrive" })
        {
            Assert.Contains(word, population);
        }
    }

    [Fact]
    public void HelpIsAShortTableOfControlsThatPointsToTheGuide()
    {
        string text = HelpContent.Text();
        Assert.StartsWith("KEY", text);
        Assert.Contains("Shift+click", text);
        Assert.Contains("GUIDE", HelpContent.Footer);
        Assert.True(text.Split('\n').Length < 40);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 90, line));
    }
}
