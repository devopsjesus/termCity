using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class UndoHistoryTests
{
    [Fact]
    public void TenUndosRestoreActionsInReverseOrderAndEvictOnlyTheOldest()
    {
        var session = new GameSession(TestCity.Flat());
        session.Game.Paused = true;
        for (int index = 0; index < 12; index++)
        {
            session.PlaceCursor(new Pos(5 + index, 18));
            Assert.True(session.BuildRoad().Success);
            Assert.Equal(Math.Min(index + 1, 10), session.UndoCount);
        }
        for (int remaining = 10; remaining > 0; remaining--)
        {
            session.RequestUndo();
            Assert.Equal("Undo last action", session.Prompt!.Title);
            session.SelectPrompt(0);
            Assert.Equal(remaining - 1, session.UndoCount);
            Assert.True(session.Game.Paused);
            Assert.False(session.Game.Map.HasRoad(6 + remaining, 18));
        }
        Assert.True(session.Game.Map.HasRoad(5, 18));
        Assert.True(session.Game.Map.HasRoad(6, 18));
        Assert.Equal(49_000, session.Game.Money);
        Assert.False(session.CanUndo);
        session.RequestUndo();
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Equal("Nothing to undo.", session.Message);
    }

    [Fact]
    public void FailedActionsCancellationAndRunningRequestsDoNotConsumeHistory()
    {
        var session = new GameSession(TestCity.Flat());
        session.PlaceCursor(new Pos(10, 18));
        Assert.True(session.BuildRoad().Success);
        Assert.False(session.BuildRoad().Success);
        Assert.Equal(1, session.UndoCount);
        session.RequestUndo();
        Assert.Null(session.Prompt);
        Assert.Equal("Pause the game to undo.", session.Message);
        Assert.Equal(1, session.UndoCount);
        session.TogglePause();
        session.RequestUndo();
        session.SelectPrompt(1);
        Assert.Equal(1, session.UndoCount);
        Assert.True(session.Game.Map.HasRoad(10, 18));
        session.NewGame(session.Game.Config);
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void HelpUsesTheOperatingSystemsUndoShortcut()
    {
        Assert.Contains("Ctrl+Z", HelpContent.Text());
        Assert.Contains("Command+Z", HelpContent.Text(macOS: true));
        Assert.DoesNotContain("Ctrl+Z", HelpContent.Text(macOS: true));
        Assert.Contains("10 recent actions while paused", HelpContent.Text());
    }
}
