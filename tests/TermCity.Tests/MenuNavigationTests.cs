using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class MenuNavigationTests
{
    private static GameSession Session()
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        session.PlaceCursor(new Pos(10, 18));
        return session;
    }

    [Fact]
    public void CancellingNestedMenusRestoresTheOriginalParentAndFocus()
    {
        var session = Session();
        session.ShowSessionMenu();
        var original = session.Prompt!;
        session.ShowPrompt(original.Title, original.Text,
            original.Choices.Append(new SessionChoice("Extra control", () => { })).ToArray());
        var parent = session.Prompt!;
        session.SelectPrompt(2);
        var load = session.Prompt!;
        session.SelectPrompt(4);
        session.CancelPrompt();
        Assert.Same(load, session.Prompt);
        Assert.Equal(4, session.Prompt!.SelectedIndex);
        session.SelectPrompt(5);
        Assert.Same(parent, session.Prompt);
        Assert.Equal(2, session.Prompt!.SelectedIndex);
        Assert.Equal("Extra control", session.Prompt.Choices[^1].Label);
        session.CancelPrompt();
        Assert.Null(session.Prompt);
    }

    [Fact]
    public void RefreshingAnOptionDoesNotAddAHistoryEntry()
    {
        var session = Session();
        session.ShowSessionMenu();
        var parent = session.Prompt;
        session.ShowBudgetMenu();
        session.SelectPrompt(3);
        Assert.Equal(3, session.Prompt!.SelectedIndex);
        session.SelectPrompt(3);
        session.CancelPrompt();
        Assert.Same(parent, session.Prompt);
        session.CancelPrompt();
        Assert.Null(session.Prompt);
    }

    [Fact]
    public void ChangingGuideTabsDoesNotAddHistoryEntries()
    {
        var session = Session();
        session.ShowSessionMenu();
        var parent = session.Prompt;
        session.ShowGuide();
        session.CycleTab(1);
        session.CycleTab(1);
        session.CancelPrompt();
        Assert.Same(parent, session.Prompt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CancellingRoadPreviewsRestoresTheirMenuAndSelection(int choice)
    {
        var session = Session();
        session.ShowAreaMenu();
        var area = session.Prompt;
        session.SelectPrompt(1);
        var roads = session.Prompt;
        session.SelectPrompt(choice);
        Assert.Null(session.Prompt);
        Assert.NotNull(session.Preview);
        session.CancelPreview();
        Assert.Same(roads, session.Prompt);
        Assert.Equal(choice, session.Prompt!.SelectedIndex);
        Assert.Null(session.Preview);
        Assert.False(session.RoadToolActive);
        session.CancelPrompt();
        Assert.Same(area, session.Prompt);
        session.CancelPrompt();
        session.PreviewRoad();
        session.CancelPreview();
        Assert.Null(session.Prompt);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CancellingBuildingAndDemolitionPreviewsReturnsToTheirOrigin(int choice)
    {
        var session = Session();
        session.ShowAreaMenu();
        session.SelectPrompt(choice);
        var origin = session.Prompt;
        if (choice == 2) session.SelectPrompt(0);
        session.CancelPreview();
        Assert.Equal(choice == 2 ? "Service buildings" : "Area menu", session.Prompt!.Title);
        if (choice == 2) Assert.Same(origin, session.Prompt);
    }

    [Fact]
    public void SuccessfulPlacementDoesNotReopenMenusOrLeaveAStaleReturnTarget()
    {
        var session = Session();
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        session.SelectPrompt(0);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Null(session.Prompt);
        session.PlaceCursor(new Pos(11, 18));
        session.PreviewRoad();
        session.CancelPreview();
        Assert.Null(session.Prompt);
    }

    [Theory]
    [InlineData("Quit")]
    [InlineData("New city")]
    [InlineData("Restart this seed")]
    public void CancellingProgressGuardsReturnsToTheCityMenu(string title)
    {
        var session = Session();
        session.ShowSessionMenu();
        var parent = session.Prompt;
        var game = session.Game;
        if (title == "Quit") session.RequestQuit();
        else session.RequestNewCity(restart: title == "Restart this seed");
        Assert.Equal(title, session.Prompt!.Title);
        session.SelectPrompt(2);
        Assert.Same(parent, session.Prompt);
        Assert.Same(game, session.Game);
    }

    [Fact]
    public void CancellingUndoReturnsToItsParent()
    {
        var session = Session();
        session.PreviewRoad();
        Assert.True(session.ConfirmPreview().Success);
        session.ShowSessionMenu();
        var parent = session.Prompt;
        session.RequestUndo();
        session.SelectPrompt(1);
        Assert.Same(parent, session.Prompt);
    }

    [Fact]
    public void GuardedLoadCancellationAndFailureRetainTheEnteredPath()
    {
        var session = Session();
        session.ShowSessionMenu();
        session.SelectPrompt(2);
        var menu = session.Prompt;
        session.SelectPrompt(4);
        var input = session.Prompt!;
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        input.Input = missing;
        session.SelectPrompt(0);
        Assert.Equal("Confirm load city", session.Prompt!.Title);
        session.CancelPrompt();
        Assert.Same(input, session.Prompt);
        Assert.Equal(missing, session.Prompt!.Input);
        session.SelectPrompt(0);
        session.SelectPrompt(1);
        Assert.Same(input, session.Prompt);
        Assert.Equal(missing, session.Prompt!.Input);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        session.CancelPrompt();
        Assert.Same(menu, session.Prompt);
    }

    [Fact]
    public void ReplacingTheGameClearsMenuHistory()
    {
        var session = Session();
        session.ShowSessionMenu();
        session.RequestNewCity(restart: true);
        session.SelectPrompt(1);
        Assert.Null(session.Prompt);
        session.CancelPrompt();
        Assert.Null(session.Prompt);
        session.PreviewRoad();
        session.CancelPreview();
        Assert.Null(session.Prompt);
    }

    [Fact]
    public void ShortcutsPreferFirstLettersThenAvailableSubordinateLetters()
    {
        SessionChoice[] choices =
        [
            new("Apple", () => { }), new("Apples", () => { }), new("Back", () => { }),
            new("Budget", () => { }),
        ];
        var shortcuts = MenuShortcuts.Assign(choices);
        Assert.Equal(new[] { 'A', 'P', 'B', 'U' }, shortcuts.Select(shortcut => shortcut.Letter));
        Assert.All(shortcuts, shortcut => Assert.False(shortcut.Shift));
        CheckShortcuts(new SessionPrompt("Test", "", choices));
    }

    [Fact]
    public void SubordinateLettersCanBeReassignedWithoutTakingPrimaryLetters()
    {
        SessionChoice[] choices = new[] { "Apple", "Boat", "Cab", "Cxy", "Cx" }
            .Select(label => new SessionChoice(label, () => { })).ToArray();
        var shortcuts = MenuShortcuts.Assign(choices);
        Assert.Equal(new[] { 'A', 'B', 'C', 'Y', 'X' }, shortcuts.Select(shortcut => shortcut.Letter));
        Assert.All(shortcuts, shortcut => Assert.False(shortcut.Shift));
        Assert.Equal(shortcuts, MenuShortcuts.Assign(choices));
    }

    [Fact]
    public void ExhaustedLabelLettersUseVisibleShiftHintsAndNeverNumbers()
    {
        SessionChoice[] choices = new[] { "A", "A", "A", "!!!" }
            .Select(label => new SessionChoice(label, () => { })).ToArray();
        var shortcuts = MenuShortcuts.Assign(choices);
        Assert.Equal(new MenuShortcut('A', 0), shortcuts[0]);
        Assert.Equal(new MenuShortcut('A', 0, Shift: true), shortcuts[1]);
        Assert.Equal("A [Shift]", shortcuts[1].DisplayLabel("A"));
        Assert.Equal("A [B]", shortcuts[2].DisplayLabel("A"));
        CheckShortcuts(new SessionPrompt("Test", "", choices));
    }

    [Fact]
    public void LetterActivationIsCaseInsensitiveAndHonorsShift()
    {
        var session = Session();
        int first = 0, second = 0;
        session.ShowPrompt("Test", "", [new("A", () => first++), new("A", () => second++)]);
        Assert.True(session.SelectShortcut('a'));
        Assert.True(session.SelectShortcut('a', shift: true));
        Assert.False(session.SelectShortcut('1'));
        Assert.False(session.SelectShortcut('z'));
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, session.Prompt!.SelectedIndex);
    }

    [Fact]
    public void EveryRegisteredMenuHasUniqueVisibleLetterShortcuts()
    {
        var session = Session();
        session.ShowAreaMenu();
        CheckShortcuts(session.Prompt!);
        for (int choice = 0; choice < 3; choice++)
        {
            session.SelectPrompt(choice);
            CheckShortcuts(session.Prompt!);
            session.CancelPrompt();
        }
        foreach (Action show in new Action[]
        {
            session.ShowSessionMenu, session.ShowBudgetMenu, session.ShowLoadMenu, () => session.ShowGuide(),
        })
        {
            session.ClosePrompt();
            show();
            CheckShortcuts(session.Prompt!);
        }
    }

    private static void CheckShortcuts(SessionPrompt prompt)
    {
        Assert.Equal(prompt.Choices.Count, prompt.Shortcuts.Count);
        Assert.Equal(prompt.Choices.Count, prompt.Shortcuts.Select(shortcut => (shortcut.Letter, shortcut.Shift)).Distinct().Count());
        for (int index = 0; index < prompt.Choices.Count; index++)
        {
            var shortcut = prompt.Shortcuts[index];
            string label = prompt.Choices[index].Label;
            if (shortcut.UnderlineIndex >= 0)
                Assert.Equal(shortcut.Letter, char.ToUpperInvariant(label[shortcut.UnderlineIndex]));
            else Assert.Contains($"[{shortcut.Letter}]", shortcut.DisplayLabel(label));
            Assert.False(char.IsDigit(shortcut.DisplayLabel(label)[0]));
        }
    }
}
