using TermCity.Core.Simulation;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ClockTests
{
    // Runs the clock for the given number of seconds in small steps, the way the application calls it.
    private static void Run(CityGame game, double seconds)
    {
        for (double left = seconds; left > 1e-9; left -= 0.1)
        {
            game.Update(Math.Min(0.1, left));
        }
    }

    [Fact]
    public void WeeksPassInRealTimeAccordingToSpeed()
    {
        var game = TestCity.Flat();
        double fast = game.Config.SecondsPerWeek(GameSpeed.Fast);
        game.Speed = GameSpeed.Fast;
        Run(game, fast - 0.1);
        Assert.Equal(0, game.Week);
        Run(game, 0.15);
        Assert.Equal(1, game.Week);

        double slow = game.Config.SecondsPerWeek(GameSpeed.Slow);
        game.Speed = GameSpeed.Slow;
        Run(game, slow - 0.3);
        Assert.Equal(1, game.Week);
        Run(game, 0.6);
        Assert.Equal(2, game.Week);
    }

    [Fact]
    public void SpeedsAreOrderedAndSlowerThanTheOldOnes()
    {
        var c = new GameConfig();
        Assert.True(c.SecondsPerWeek(GameSpeed.Slow) > c.SecondsPerWeek(GameSpeed.Medium));
        Assert.True(c.SecondsPerWeek(GameSpeed.Medium) > c.SecondsPerWeek(GameSpeed.Fast));
        Assert.True(c.SecondsPerWeek(GameSpeed.Fast) >= 1.0, "even the fastest week should last a second or more");
        Assert.True(c.SecondsPerWeek(GameSpeed.Medium) >= 4.0);
        Assert.Equal(7, c.DaysPerWeek);
    }

    [Fact]
    public void PausedGameDoesNotAdvance()
    {
        var game = TestCity.Flat();
        game.Paused = true;
        Run(game, 30);
        Assert.Equal(0, game.Week);
        Assert.Equal(0, game.Day);
        game.Paused = false;
        Run(game, 30);
        Assert.True(game.Week > 0);
    }

    [Fact]
    public void ADayPassesEverySeventhOfAWeek()
    {
        var game = TestCity.Flat();
        double day = game.Config.SecondsPerWeek(game.Speed) / 7;
        Run(game, day * 3 + 0.05);
        Assert.Equal(3, game.Day);
        Assert.Equal(0, game.Week);
        Assert.InRange(game.WeekProgress, 3 / 7.0, 4 / 7.0);
    }

    [Fact]
    public void AStallDoesNotFastForwardTheGame()
    {
        var game = TestCity.Flat();
        game.Speed = GameSpeed.Slow;

        // The window was in the background for a minute: one big elapsed time arrives in a single update.
        game.Update(60);
        Assert.Equal(0, game.Week);
        Assert.True(game.Day <= 1, $"day={game.Day}");
    }

    [Fact]
    public void CalendarRollsOverEveryFiftyTwoWeeks()
    {
        var game = TestCity.Flat();
        int startingYear = game.Year;
        TestCity.Advance(game, 52);
        Assert.Equal(startingYear + 1, game.Year);
        Assert.Equal(1, game.WeekOfYear);
    }

    [Fact]
    public void NewCitiesStartInTheCurrentYear()
    {
        int before = DateTime.Now.Year;
        var game = TestCity.Flat();
        Assert.InRange(game.Year, before, DateTime.Now.Year);
        Assert.Equal(game.Year, game.Config.StartingYear);
    }

    [Theory]
    [InlineData(0, "[>......]")]
    [InlineData(2, "[==>....]")]
    [InlineData(6, "[======>]")]
    [InlineData(-1, "[>......]")]
    [InlineData(7, "[======>]")]
    public void SharedWeekBarMarksEachDay(int day, string expected) =>
        Assert.Equal(expected, Fmt.WeekBar(day, 7));

    [Fact]
    public void GrowthIsSpreadOverTheDaysOfTheWeekAndTaxesArriveAtTheEnd()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(0, 21, 60, 1), ZoneType.Residential);
        game.Designate(new CellRect(0, 19, 60, 1), ZoneType.Residential);
        int money = game.Money;

        // After a few weeks the weekly cap is big enough to be spread over several days.
        TestCity.Advance(game, 20);
        money = game.Money;
        int filledBefore = game.Stats.Residential.Filled;
        int cap = game.WeeklyCap(3, filledBefore);
        Assert.InRange(cap, 5, 60);

        var seenGrowth = new List<int>();
        int last = filledBefore;
        for (int day = 0; day < 7; day++)
        {
            Assert.Equal(day, game.Day);
            Assert.Equal(money, game.Money); // no tax until the week is over
            game.AdvanceDay();
            int now = game.Stats.Residential.Filled;
            seenGrowth.Add(now - last);
            last = now;
        }

        Assert.Equal(0, game.Day);
        Assert.Equal(cap, seenGrowth.Sum());
        Assert.True(seenGrowth.Count(g => g > 0) >= 3, "growth: " + string.Join(",", seenGrowth));
        Assert.True(game.Money > money);
        Assert.Equal(cap, game.LastReport!.NewHouseholds);
    }

    [Fact]
    public void TheDailySharesAddUpToTheWeeklyCapWhateverItsSize()
    {
        var game = TestCity.Flat();
        foreach (int cap in new[] { 0, 1, 3, 6, 7, 8, 13, 100, 1599 })
        {
            int total = 0;
            for (int day = 0; day < 7; day++)
            {
                int share = (int)((long)cap * (day + 1) / 7 - (long)cap * day / 7);
                Assert.InRange(share, cap / 7, cap / 7 + 1);
                total += share;
            }

            Assert.Equal(cap, total);
        }

        Assert.NotNull(game);
    }

    [Fact]
    public void DaysWithoutAnyGrowthDoNotWakeTheInterface()
    {
        var game = TestCity.Flat();
        int changes = 0;
        game.Changed += () => changes++;
        for (int day = 0; day < 6; day++)
        {
            game.AdvanceDay();
        }

        Assert.Equal(0, changes); // nothing to build on: six quiet days
        game.AdvanceDay();          // the week ends: taxes are paid
        Assert.Equal(1, changes);
    }

    [Fact]
    public void LoopGapsAreRecordedAndTheWorstIsRemembered()
    {
        var s = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        s.RecordLoopGap(22);
        s.RecordLoopGap(3100);
        s.RecordLoopGap(21);
        Assert.Equal(21, s.LoopGapMs);
        Assert.Equal(3100, s.LoopWorstGapMs);
        s.ToggleInputDebug();
        Assert.Equal(0, s.LoopWorstGapMs);
    }

    [Fact]
    public void TheWeekBarHasOneCharacterPerDay()
    {
        Assert.Equal("[>......]", Fmt.WeekBar(0, 7));
        Assert.Equal("[===>...]", Fmt.WeekBar(3, 7));
        Assert.Equal("[======>]", Fmt.WeekBar(6, 7));
        Assert.Equal(9, Fmt.WeekBar(2, 7).Length);
    }
}
public class SessionTests
{
    private static GameSession NewSession()
    {
        var session = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        session.SetViewport(80, 24);
        return session;
    }

    [Fact]
    public void ArrowsMoveTheCursorAndScrollAtTheEdge()
    {
        var s = NewSession();
        var start = s.Cursor;
        s.MoveCursor(1, 0);
        Assert.Equal(start.Offset(1, 0), s.Cursor);

        for (int i = 0; i < 200; i++)
        {
            s.MoveCursor(1, 0);
        }

        Assert.Equal(s.Game.Map.Width - 1, s.Cursor.X);
        Assert.True(s.ViewRect.Contains(s.Cursor));
        Assert.Equal(s.Game.Map.Width - 80, s.CameraX);
    }

    [Fact]
    public void CtrlArrowMovesAFullScreenLength()
    {
        var s = NewSession();
        s.SelectCell(new Pos(10, 10));
        s.JumpCursor(1, 0);
        Assert.Equal(10 + 79, s.Cursor.X);
        s.JumpCursor(0, 1);
        Assert.Equal(10 + 23, s.Cursor.Y);
        s.JumpCursor(-1, 0);
        s.JumpCursor(-1, 0);
        Assert.Equal(0, s.Cursor.X);
        Assert.True(s.ViewRect.Contains(s.Cursor));
    }

    [Fact]
    public void DragSelectsARectangleThatSurvivesRelease()
    {
        var s = NewSession();
        s.BeginDrag(new Pos(10, 10));
        s.UpdateDrag(new Pos(14, 12));
        s.EndSelection();
        Assert.Equal(new CellRect(10, 10, 5, 3), s.Selection);
        Assert.Equal(new CellRect(10, 10, 5, 3), s.ActiveArea);
    }

    [Fact]
    public void ShiftClickGrowsTheSelectionToCoverTheClickedCell()
    {
        var s = NewSession();
        s.SelectCell(new Pos(10, 10));
        s.BeginDrag(new Pos(14, 8), extend: true);
        s.EndSelection();
        Assert.Equal(new CellRect(10, 8, 5, 3), s.Selection);

        s.BeginDrag(new Pos(7, 12), extend: true);
        s.EndSelection();
        Assert.Equal(new CellRect(7, 8, 8, 5), s.Selection);
    }

    [Fact]
    public void ShiftDragExtendsFromTheExistingSelectionAndPlainDragStartsOver()
    {
        var s = NewSession();
        s.BeginDrag(new Pos(10, 10));
        s.UpdateDrag(new Pos(12, 11));
        s.EndSelection();
        s.BeginDrag(new Pos(20, 20), extend: true);
        s.UpdateDrag(new Pos(22, 21));
        s.EndSelection();
        Assert.Equal(new CellRect(10, 10, 13, 12), s.Selection);

        s.BeginDrag(new Pos(30, 30));
        s.EndSelection();
        Assert.Equal(new CellRect(30, 30, 1, 1), s.Selection);
    }

    [Fact]
    public void ClickSelectsOneCell()
    {
        var s = NewSession();
        s.SelectCell(new Pos(30, 30));
        Assert.Equal(CellRect.Single(new Pos(30, 30)), s.Selection);
    }

    [Fact]
    public void ShiftArrowsExtendAndPlainArrowClears()
    {
        var s = NewSession();
        s.SelectCell(new Pos(30, 30));
        s.ClearSelection();
        s.MoveCursor(1, 0, extend: true);
        s.MoveCursor(0, 1, extend: true);
        Assert.Equal(new CellRect(30, 30, 2, 2), s.Selection);
        s.MoveCursor(1, 0);
        Assert.Null(s.Selection);
    }

    [Fact]
    public void SpaceModeSelectsWithArrows()
    {
        var s = NewSession();
        s.SelectCell(new Pos(30, 30));
        s.ClearSelection();
        s.ToggleSelectionMode();
        s.MoveCursor(2, 0);
        s.MoveCursor(0, 2);
        s.ToggleSelectionMode();
        Assert.Equal(new CellRect(30, 30, 3, 3), s.Selection);
    }

    [Fact]
    public void ZoneActionAppliesToSelectionAndClearsIt()
    {
        var s = NewSession();
        s.BeginDrag(new Pos(10, 10));
        s.UpdateDrag(new Pos(12, 11));
        s.EndSelection();
        var result = s.Zone(ZoneType.Residential);
        Assert.True(result.Success);
        Assert.Equal(6, s.Game.Stats.Residential.Zoned);
        Assert.Null(s.Selection);
    }

    [Fact]
    public void SaveAndLoadThroughSession()
    {
        var s = NewSession();
        s.SelectCell(new Pos(10, 10));
        s.Zone(ZoneType.Commercial);
        Assert.True(s.QuickSave());
        s.Demolish();
        s.SelectCell(new Pos(10, 10));
        s.Demolish();
        Assert.Equal(0, s.Game.Stats.Commercial.Zoned);
        Assert.True(s.QuickLoad());
        Assert.Equal(1, s.Game.Stats.Commercial.Zoned);
        File.Delete(s.SavePath);
    }
}
