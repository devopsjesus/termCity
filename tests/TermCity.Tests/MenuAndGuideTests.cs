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
        Assert.Equal(["Start", "Zones", "Roads", "Services", "Population", "Happiness", "Economy", "Glossary"], tabs.Select(t => t.Title));
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
        Assert.Contains("YOUR NEXT STEP", session.Prompt!.Tabs![0].Text);
        session.SelectPrompt(0);
        Assert.True(session.GuideVisible);
        session.ShowGuide();
        Assert.Equal("Dismiss tip", session.Prompt!.Choices[1].Label);
        session.SelectPrompt(1);
        Assert.False(session.GuideVisible);
        session.ShowGuide();
        Assert.False(session.GuideVisible);
        Assert.DoesNotContain("YOUR NEXT STEP", session.Prompt!.Tabs![0].Text);
        Assert.Equal("Enable guide", session.Prompt!.Choices[1].Label);
        session.SelectPrompt(1);
        Assert.True(session.GuideVisible);
        Assert.False(session.Game.GuideDismissed);
        session.ShowGuide();
        Assert.Contains("YOUR NEXT STEP", session.Prompt!.Tabs![0].Text);
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
        Assert.Contains("F10", text);
        Assert.Contains("GUIDE", HelpContent.Footer);
        Assert.True(text.Split('\n').Length < 40);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 90, line));
    }

    [Fact]
    public void PlaceableMenuHelpUsesRegisteredStatsAndActualAreaIconsWithoutActivating()
    {
        var session = Session();
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        var prompt = session.Prompt!;
        foreach (var building in session.Game.Map.Content.Buildings.Where(b => b.PlayerPlaceable))
        {
            int index = prompt.Choices.ToList().FindIndex(c => c.Label == building.Name);
            prompt.HelpIndex = index;
            var help = prompt.ActiveHelp!;
            Assert.Equal(ChoiceHelpContent.Icons(building.Glyphs), help.Icons);
            Assert.Equal(building.Description, help.Description);
            Assert.Contains(Fmt.Money(building.Cost), help.Details);
            Assert.Contains("Upkeep:", help.Details);
            if (building.Capacity > 0) Assert.Contains($"Capacity: {building.Capacity:N0}", help.Details);
            if (building.Radius > 0)
            {
                Assert.Contains($"Radius: {building.Radius} cells", help.Details);
                Assert.Contains($"strength: {building.Strength}/100", help.Details);
            }
            if (building.RequiresWaterNearby) Assert.Contains("nearby open water", help.Details);
            Assert.Contains("connected road access", help.Details);
            foreach (var area in TermCity.Core.Rendering.AreaOfEffect.ForBuilding(building, new(0, 0)))
            {
                Assert.Contains(ChoiceHelpContent.Icons(area.Glyphs), help.Details);
                Assert.Contains($"radius {area.Radius} cells", help.Details);
            }
            Assert.Same(prompt, session.Prompt);
            Assert.Null(session.Preview);
        }
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        foreach (var choice in session.Prompt!.Choices.Where(c => c.Label != "Back"))
        {
            Assert.NotNull(choice.Help);
            Assert.Contains("Traffic capacity:", choice.Help!.Details);
        }
    }

    [Fact]
    public void GlossaryIncludesEveryRegisteredTypeAndEffectGlyph()
    {
        var session = Session();
        string glossary = GuideContent.Glossary(session.Game);
        foreach (var building in session.Game.Map.Content.Buildings)
            Assert.Contains(building.Name, glossary);
        foreach (var road in session.Game.Map.Content.Roads)
            Assert.Contains(road.Name, glossary);
        foreach (var terrain in session.Game.Map.Content.Terrains)
            Assert.Contains(terrain.Name, glossary);
        foreach (var feature in session.Game.Map.Content.Features)
            Assert.Contains(feature.Name, glossary);
        foreach (string glyph in TermCity.Core.Effects.EffectGlyphs.All().Distinct())
            Assert.Contains(glyph, glossary);
        Assert.Contains("area-of-effect", glossary);
        Assert.Contains("context", glossary);
        Assert.Contains("money animations are not active in gameplay", glossary);
    }
}
