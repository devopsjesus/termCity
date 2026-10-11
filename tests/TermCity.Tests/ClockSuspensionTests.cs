using TermCity.Core.Session;

namespace TermCity.Tests;

public class ClockSuspensionTests
{
    [Fact]
    public void ExplicitResumeRunsClockWhileDialogRemainsOpen()
    {
        var session = new GameSession(TestCity.Flat());
        session.ShowReport();
        var prompt = session.Prompt;
        Assert.True(session.ClockSuspended);
        session.TogglePause();
        Assert.False(session.ClockSuspended);
        double before = session.Game.ElapsedDays;
        session.Update(0.1);
        Assert.True(session.Game.ElapsedDays > before);
        Assert.Same(prompt, session.Prompt);
        session.TogglePause();
        Assert.True(session.ClockSuspended);
        session.ClosePrompt();
        Assert.True(session.Game.Paused);
    }

    [Fact]
    public void DialogAndPreviewPauseClockWithoutChangingManualPause()
    {
        var session = new GameSession(TestCity.Flat());
        Assert.False(session.ClockSuspended);
        session.ShowReport();
        Assert.True(session.ClockSuspended);
        Assert.False(session.Game.Paused);
        double before = session.Game.ElapsedDays;
        session.Update(1);
        Assert.Equal(before, session.Game.ElapsedDays);
        session.ClosePrompt();
        Assert.False(session.ClockSuspended);
        session.PreviewRoad();
        Assert.True(session.ClockSuspended);
        session.CancelPreview();
        Assert.False(session.ClockSuspended);
        session.TogglePause();
        session.ShowGuide();
        session.ClosePrompt();
        Assert.True(session.ClockSuspended);
    }
}
