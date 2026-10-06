using Terminal.Gui.Input;
using TermCity.App.Views;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

[Collection("UI")]
public class SessionFeatureUiTests
{
    private static GameSession Session(bool guide = false)
    {
        var session = new GameSession(TestCity.Flat(),
            Path.Combine(Path.GetTempPath(), "termcity-session-ui-" + Guid.NewGuid().ToString("N") + ".json"), showGuide: guide);
        session.Game.Paused = true;
        return session;
    }

    [Fact]
    public async Task RoadPreviewChangesColorsWithoutSpendingAndEscapeRestoresScreen()
    {
        var session = Session();
        session.PlaceCursor(new Pos(80, 40));
        session.SetMessage("");
        await UiHarness.Run(session, async ui =>
        {
            await ui.Screen();
            string original = await ui.Cells();
            await ui.Press(Key.B);
            Assert.Contains("$500", await ui.Screen());
            Assert.NotNull(session.Preview);
            Assert.Equal(50_000, session.Game.Money);
            Assert.NotEqual(original, await ui.Cells());
            await ui.Press(Key.Esc);
            await ui.Screen();
            Assert.Equal(original, await ui.Cells());
            await ui.Press(Key.B);
            await ui.Press(Key.Enter);
            Assert.True(session.Game.Map.HasRoad(80, 40));
            Assert.Equal(49_500, session.Game.Money);
            Assert.Null(session.Preview);
        });
    }

    [Fact]
    public async Task RoadLineWorksWithKeyboardAndMouseAndRequiresConfirmation()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.T);
            await ui.Press(Key.CursorRight);
            await ui.Press(Key.CursorRight);
            Assert.Equal(3, session.Preview!.Area.Width);
            Assert.Equal(1, session.Preview.Area.Height);
            await ui.Press(Key.Esc);
            await ui.Press(Key.T);
            var start = session.ScreenToMap(10, 4);
            await ui.Mouse(MouseFlags.LeftButtonPressed, 10, 5);
            await ui.Mouse(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, 15, 5);
            await ui.Mouse(MouseFlags.LeftButtonReleased, 15, 5);
            Assert.Equal(new CellRect(start.X, start.Y, 6, 1), session.Preview!.Area);
            Assert.Equal(50_000, session.Game.Money);
            await ui.Press(Key.Enter);
            Assert.Equal(47_000, session.Game.Money);
            Assert.False(session.RoadToolActive);
        });
    }

    [Fact]
    public async Task CtrlZPausesWarnsAndRestoresOnlyAfterConfirmation()
    {
        var session = Session();
        session.PlaceCursor(new Pos(80, 40));
        session.Game.Paused = false;
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.B);
            await ui.Press(Key.Enter);
            await ui.Press(Key.Z.WithCtrl);
            Assert.True(session.Game.Paused);
            Assert.True(session.Game.Map.HasRoad(80, 40));
            Assert.Contains("Undo last action", await ui.Screen());
            Assert.Contains("entire city", await ui.Screen());
            await ui.Press(Key.Enter);
            Assert.False(session.Game.Map.HasRoad(80, 40));
            Assert.Equal(50_000, session.Game.Money);
            Assert.True(session.Game.Paused);
            Assert.Null(session.Prompt);
        });
    }

    [Fact]
    public async Task SessionMenuAndQuitPromptFreezeTimeAndCanBeCancelled()
    {
        var session = Session();
        session.Game.Paused = false;
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.F10);
            Assert.Contains("City menu", await ui.Screen());
            Assert.Contains("Restart this seed", await ui.Screen());
            double time = session.Game.ElapsedDays;
            await Task.Delay(300);
            Assert.Equal(time, session.Game.ElapsedDays);
            await ui.Press(Key.Esc);
            await ui.Press(Key.Q.WithCtrl);
            string quitPrompt = await ui.Screen();
            Assert.Contains("Save your city before quitting", quitPrompt);
            Assert.Contains("1. Save and quit", quitPrompt);
            Assert.Contains("2. Quit without saving", quitPrompt);
            Assert.Contains("Quick-save file:", quitPrompt);
            Assert.Contains("termcity-session-ui-", quitPrompt);
            Assert.True(quitPrompt.IndexOf("Quick-save file:", StringComparison.Ordinal) >
                quitPrompt.IndexOf("3. Cancel", StringComparison.Ordinal));
            await ui.Press(Key.Esc);
            Assert.Null(session.Prompt);
            await ui.Press(Key.F10);
            Assert.NotNull(session.Prompt);
        });
    }

    [Fact]
    public async Task GuideStartsPausedAndReportHotkeysExposeFeedback()
    {
        var session = Session(guide: true);
        await UiHarness.Run(session, async ui =>
        {
            Assert.Contains("Your first city", await ui.Screen());
            Assert.True(session.Game.Paused);
            await ui.Press(Key.Enter);
            await ui.Press(Key.F6);
            Assert.False(session.GuideVisible);
            await ui.Press(Key.F7);
            Assert.Contains("Weekly report and milestones", await ui.Screen());
            Assert.Contains("Next milestone: 100 people", await ui.Screen());
            await ui.Press(Key.Esc);
            await ui.Press(Key.F8);
            Assert.Contains("Growth and road access", await ui.Screen());
            Assert.Contains("road-served vacancies", await ui.Screen());
            await ui.Press(Key.Esc);
            await ui.Press(Key.F1);
            Assert.Contains("F10 city menu", await ui.Screen());
            Assert.Contains(OperatingSystem.IsMacOS() ? "Control+Z" : "Ctrl+Z", await ui.Screen());
        });
    }

    [Fact]
    public async Task MouseCanActivateSessionMenuChoices()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.F10);
            await ui.Screen();
            // At 120 columns the 76-column prompt begins at x=22; the report button is row 11.
            await ui.Mouse(MouseFlags.LeftButtonPressed, 30, 11);
            Assert.Equal("Weekly report and milestones", session.Prompt!.Title);
            await ui.Press(Key.Esc);
        });
    }

    [Fact]
    public async Task LoadFileInputSupportsClearingTypingAndEmptyPathErrors()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.F10);
            await ui.Press(Key.D3);
            Assert.Equal("Load city", session.Prompt!.Title);
            await ui.Press(Key.D5);
            Assert.Equal("Load file", session.Prompt!.Title);
            await ui.Press(Key.A.WithCtrl);
            Assert.Equal("", session.Prompt.Input);
            await ui.Press(Key.Enter);
            Assert.Equal(MessageKind.Error, session.MessageKind);
            Assert.Contains("enter a save-file path", await ui.Screen());
            await ui.Press(Key.A);
            Assert.Equal("a", session.Prompt.Input);
            await ui.Press(Key.A.WithCtrl);
            await ui.Paste("C:\\Cities\\my city.json");
            Assert.Equal("C:\\Cities\\my city.json", session.Prompt.Input);
        });
    }

    [Fact]
    public async Task MinimumTerminalShowsMenusHelpGrowthAndNearbyYesNoButtons()
    {
        var session = Session();
        await UiHarness.Run(session, async ui =>
        {
            try
            {
                await ui.Resize(80, 24);
                await ui.Press(Key.F10);
                string screen = await ui.Screen();
                Assert.Equal(80, ui.Window.Viewport.Width);
                Assert.Equal(24, ui.Window.Viewport.Height);
                Assert.Contains("9. Quit", screen);
                Assert.Contains(OperatingSystem.IsMacOS() ? "Escape cancels" : "Esc cancels", screen);
                await ui.Press(Key.Esc);
                await ui.Press(Key.F1);
                Assert.Contains("Press any key to close", await ui.Screen());
                await ui.Press(Key.Esc);
                await ui.Press(Key.F8);
                Assert.Contains("Paused - press P", await ui.Screen());
                await ui.Press(Key.Esc);
                await ui.Press(Key.B);
                Assert.Contains("[Yes]", await ui.Screen());
                var popup = ui.Window.SubViews.OfType<PlacementConfirmationView>().Single();
                Assert.InRange(popup.Frame.Right, 1, 46);
                Assert.InRange(popup.Frame.Bottom, 1, 23);
                await ui.Mouse(MouseFlags.LeftButtonPressed, popup.Frame.X + 12, popup.Frame.Y + 4);
                await ui.Mouse(MouseFlags.LeftButtonReleased, popup.Frame.X + 12, popup.Frame.Y + 4);
                Assert.True(session.Preview is null, await ui.Screen());
                await ui.Press(Key.B);
                await ui.Screen();
                await ui.Mouse(MouseFlags.LeftButtonPressed, popup.Frame.X + 4, popup.Frame.Y + 4);
                await ui.Mouse(MouseFlags.LeftButtonReleased, popup.Frame.X + 4, popup.Frame.Y + 4);
                Assert.Null(session.Preview);
                Assert.Equal(49_500, session.Game.Money);
            }
            finally
            {
                await ui.Resize(120, 30);
            }
        });
    }
}
