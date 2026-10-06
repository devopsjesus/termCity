using Terminal.Gui.Input;
using TermCity.App.Views;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

// Terminal.Gui keeps some process-wide state, so UI tests run one at a time.
[Collection("UI")]
public class UiTests
{
    private static GameSession NewSession(out string savePath)
    {
        savePath = Path.Combine(Path.GetTempPath(), "termcity-ui-" + Guid.NewGuid().ToString("N") + ".json");
        var game = CityGame.New(new GameConfig { Seed = 12345 });
        return new GameSession(game, savePath);
    }

    [Fact]
    public async Task ShowsHudMinimapDemandAndHints()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            string screen = await ui.Screen();
            Assert.Contains("TermCity", screen);
            Assert.Contains("$50,000", screen);
            Assert.Contains("Year 1, Week 01", screen);
            Assert.Contains("MINIMAP", screen);
            Assert.Contains("DEMAND", screen);
            Assert.Contains("Medium", screen);
            Assert.Matches(@"\[=*>\.*\]", screen); // the week bar: one character per day
            Assert.Contains("F1 Help", screen);
        });
    }

    [Fact]
    public async Task MovingTheCursorRedrawsOnlyWhatChangedAndLeavesTheSameScreen()
    {
        var session = NewSession(out _);
        session.Game.Paused = true; // the clock would change the week bar between snapshots
        await UiHarness.Run(session, async ui =>
        {
            await ui.Screen();
            foreach (var key in new[] { Key.CursorRight, Key.CursorRight, Key.CursorDown, Key.CursorLeft.WithShift, Key.CursorDown.WithShift, Key.CursorUp, Key.CursorLeft })
            {
                await ui.Press(key);
                await Task.Delay(60);
                string incremental = await ui.Cells();
                await ui.Screen(); // forces a complete redraw
                Assert.Equal(await ui.Cells(), incremental);
            }
        });
    }

    [Fact]
    public async Task ArrowKeysAndCtrlArrowsMoveTheCursor()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            var start = session.Cursor;
            await ui.Press(Key.CursorRight);
            Assert.Equal(start.Offset(1, 0), session.Cursor);
            await ui.Press(Key.CursorDown);
            Assert.Equal(start.Offset(1, 1), session.Cursor);

            // Ctrl+Arrow moves a full screen length (the map view is wider than 80 columns with the side panel).
            await ui.Press(Key.CursorRight.WithCtrl);
            Assert.Equal(session.Game.Map.Width - 1, session.Cursor.X);
            await ui.Press(Key.CursorLeft.WithCtrl);
            Assert.Equal(session.Game.Map.Width - 1 - (session.ViewWidth - 1), session.Cursor.X);
            Assert.True(session.ViewWidth >= 80);        });
    }

    /// <summary>An open, buildable cell near the middle of the map (the exact middle can be road or water).</summary>
    private static Pos OpenCell(GameSession session)
    {
        var game = session.Game;
        for (int r = 0; ; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    var p = new Pos(game.Map.Width / 2 + dx + 10, game.Map.Height / 2 + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && game.CanBuildOn(p.X, p.Y))
                    {
                        return p;
                    }
                }
            }
        }
    }

    private const MouseFlags Press = MouseFlags.LeftButtonPressed;
    private const MouseFlags Drag = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport;
    private const MouseFlags Release = MouseFlags.LeftButtonReleased;
    private const MouseFlags Shift = MouseFlags.Shift;

    [Fact]
    public async Task ShiftClickSelectsACellAndShiftDragSelectsAnArea()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(Press | Shift, 10, 5);
            await ui.Mouse(Release | Shift, 10, 5);
            var cell = session.ScreenToMap(10, 4);
            Assert.Equal(CellRect.Single(cell), session.Selection);

            await ui.Mouse(Press | Shift, 10, 5);
            await ui.Mouse(Drag | Shift, 15, 8);
            await ui.Mouse(Release | Shift, 15, 8);
            Assert.Equal(new CellRect(cell.X, cell.Y, 6, 4), session.Selection);
        });
    }

    [Fact]
    public async Task CtrlClickSelectsToo()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(Press | MouseFlags.Ctrl, 10, 5);
            await ui.Mouse(Drag | MouseFlags.Ctrl, 13, 7);
            await ui.Mouse(Release | MouseFlags.Ctrl, 13, 7);
            var cell = session.ScreenToMap(10, 4);
            Assert.Equal(new CellRect(cell.X, cell.Y, 4, 3), session.Selection);
        });
    }

    [Fact]
    public async Task LeftDragPansTheMapAndPlainClickOnlyMovesTheCursor()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            int x = session.CameraX, y = session.CameraY;
            var under = session.ScreenToMap(40, 10); // screen row 11 is view row 10 (row 0 is the status bar)

            // Dragging the map right and down by (8, 2) brings the map to the left and above into view.
            await ui.Mouse(Press, 40, 11);
            await ui.Mouse(Drag, 44, 12);
            await ui.Mouse(Drag, 48, 13);
            await ui.Mouse(Release, 48, 13);
            Assert.Equal(x - 8, session.CameraX);
            Assert.Equal(y - 2, session.CameraY);
            Assert.Null(session.Selection);
            // The cell that was grabbed is still under the pointer.
            Assert.Equal(under, session.ScreenToMap(48, 12));

            // A click without dragging does not move the map or select; it places the cursor.
            int cx = session.CameraX, cy = session.CameraY;
            await ui.Mouse(Press, 30, 8);
            await ui.Mouse(Release, 30, 8);
            Assert.Equal(cx, session.CameraX);
            Assert.Equal(cy, session.CameraY);
            Assert.Equal(session.ScreenToMap(30, 7), session.Cursor);
            Assert.Null(session.Selection);
        });
    }

    [Fact]
    public async Task ShiftDragToTheEdgeScrollsTheMapWhileSelecting()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            int cameraBefore = session.CameraX;
            await ui.Mouse(Press | Shift, 40, 10);
            await ui.Mouse(Drag | Shift, 0, 10);
            await Task.Delay(600);
            await ui.Mouse(Release | Shift, 0, 10);

            Assert.True(session.CameraX < cameraBefore, $"camera {cameraBefore} -> {session.CameraX}");
            Assert.True(session.Selection!.Value.Width > 40);
        });
    }

    [Fact]
    public async Task RestingThePointerNearAnEdgeScrollsTheMapAndMovingAwayStops()
    {
        var session = NewSession(out _);
        session.ToggleEdgeScroll(); // it is off by default
        await UiHarness.Run(session, async ui =>
        {
            int x = session.CameraX;
            // Two columns in from the left edge of the map view, no buttons held.
            await ui.Mouse(MouseFlags.PositionReport, 2, 10);
            await Task.Delay(500);
            Assert.True(session.CameraX < x, $"camera {x} -> {session.CameraX}");
            Assert.Null(session.Selection);

            await ui.Mouse(MouseFlags.PositionReport, 40, 10);
            await Task.Delay(100);
            int settled = session.CameraX;
            await Task.Delay(400);
            Assert.Equal(settled, session.CameraX);
        });
    }

    [Fact]
    public async Task HoveringNearTheTopAndRightEdgesScrollsInThoseDirections()
    {
        var session = NewSession(out _);
        session.ToggleEdgeScroll();
        await UiHarness.Run(session, async ui =>
        {
            int y = session.CameraY;
            await ui.Mouse(MouseFlags.PositionReport, 40, 1);
            await Task.Delay(500);
            Assert.True(session.CameraY < y, $"camera y {y} -> {session.CameraY}");

            await ui.Mouse(MouseFlags.PositionReport, 40, 12);
            int x = session.CameraX;
            // The map view is 86 columns wide at 120x30 (x = 0-85 on screen; the side panel starts at 86).
            await ui.Mouse(MouseFlags.PositionReport, 84, 12);
            await Task.Delay(500);
            Assert.True(session.CameraX > x, $"camera x {x} -> {session.CameraX}");
        });
    }

    [Fact]
    public async Task EdgeScrollingIsOffByDefaultCanBeTurnedOnAndKeyboardUseStopsIt()
    {
        var session = NewSession(out _);
        Assert.False(session.EdgeScrollEnabled);
        await UiHarness.Run(session, async ui =>
        {
            int x = session.CameraX;
            await ui.Mouse(MouseFlags.PositionReport, 1, 10);
            await Task.Delay(500);
            Assert.Equal(x, session.CameraX); // resting the pointer at the edge does nothing

            await ui.Press(Key.E);
            Assert.True(session.EdgeScrollEnabled);
            // Pressing a key suppresses hover scrolling until the mouse moves again.
            await ui.Mouse(MouseFlags.PositionReport, 40, 10);
            await Task.Delay(120); // long enough for the edge-scroll timer to notice the move
            await ui.Mouse(MouseFlags.PositionReport, 1, 10);
            await Task.Delay(300);
            Assert.True(session.CameraX < x);

            // Any key press hands control back to the keyboard.
            await ui.Press(Key.CursorRight);
            int stopped = session.CameraX;
            await Task.Delay(400);
            Assert.Equal(stopped, session.CameraX);
        });
    }
    [Fact]
    public async Task RightClickOpensContextMenuWithAreaSubmenuAndZoningWorksFromIt()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(Press | Shift, 10, 5);
            await ui.Mouse(Drag | Shift, 13, 7);
            await ui.Mouse(Release | Shift, 13, 7);
            await ui.Mouse(MouseFlags.RightButtonReleased, 13, 7);

            string menu = await ui.Screen();
            Assert.Contains("Area", menu);
            Assert.Contains("Road", menu);
            Assert.Contains("Demolish", menu);
            Assert.Contains("4x3 selected (12 cells)", menu);
            // The Area submenu lists all three zone types.
            await ui.Press(Key.CursorRight);
            menu = await ui.Screen();
            Assert.Contains("Residential", menu);
            Assert.Contains("Commercial", menu);
            Assert.Contains("Industrial", menu);

            await ui.Press(Key.CursorDown);
            await ui.Press(Key.Enter);
            Assert.Equal(12, session.Game.Stats.Commercial.Zoned);
            Assert.Equal(50_000, session.Game.Money);
        });
    }

    [Fact]
    public async Task EnterOpensMenuForKeyboardUsersAndRoadsCostMoney()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.CursorRight.WithShift);
            await ui.Press(Key.Enter);
            await ui.Press(Key.CursorDown); // Area -> Road
            await ui.Press(Key.CursorRight); // open the Road submenu (Street is first)
            Assert.Contains("Highway", await ui.Screen());
            await ui.Press(Key.Enter);
            Assert.NotNull(session.Preview);
            Assert.Equal(50_000, session.Game.Money);
            await ui.Press(Key.Enter); // confirm the road preview
            Assert.Equal(50_000 - 2 * 500, session.Game.Money);
        });
    }

    [Fact]
    public async Task HotkeysAndHelpAndPauseWork()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            session.PlaceCursor(OpenCell(session));
            await ui.Press(Key.R);
            Assert.Equal(1, session.Game.Stats.Residential.Zoned);

            await ui.Press(Key.F1);
            Assert.Contains("TERMCITY - HELP", await ui.Screen());
            await ui.Press(Key.Esc);
            Assert.DoesNotContain("TERMCITY - HELP", await ui.Screen());

            await ui.Press(Key.P);
            Assert.True(session.Game.Paused);
            Assert.Contains("PAUSED", await ui.Screen());
            await ui.Press(Key.D3);
            Assert.Equal(GameSpeed.Fast, session.Game.Speed);
            Assert.False(session.Game.Paused);
        });
    }

    [Fact]
    public async Task ClockRunsWithoutAnyInput()
    {
        var session = NewSession(out _);
        session.Game.Speed = GameSpeed.Fast;
        await UiHarness.Run(session, async ui =>
        {
            // A fast week lasts 1.4 s; the game runs by itself with no input at all.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (session.Game.Week * 7 + session.Game.Day < 8 && sw.ElapsedMilliseconds < 8000)
            {
                await Task.Delay(100);
            }

            Assert.True(session.Game.Week * 7 + session.Game.Day >= 8, $"week={session.Game.Week} day={session.Game.Day}");
        });
    }

    [Fact]
    public async Task SaveAndLoadHotkeys()
    {
        var session = NewSession(out string path);
        try
        {
            await UiHarness.Run(session, async ui =>
            {
                session.PlaceCursor(OpenCell(session));
                await ui.Press(Key.R);
                await ui.Press(Key.F5);
                Assert.True(File.Exists(path));
                await ui.Press(Key.D);
                await ui.Press(Key.Enter);
                Assert.Equal(0, session.Game.Stats.Residential.Zoned);
                await ui.Press(Key.F9);
                Assert.NotNull(session.Prompt);
                await ui.Press(Key.CursorDown); // load without replacing the quick-save
                await ui.Press(Key.Enter);
                Assert.Equal(1, session.Game.Stats.Residential.Zoned);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MouseWheelScrollsTheMap()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            int y = session.CameraY;
            await ui.Mouse(MouseFlags.WheeledUp, 20, 10);
            Assert.True(session.CameraY < y);
            await ui.Mouse(MouseFlags.WheeledDown, 20, 10);
            await ui.Mouse(MouseFlags.WheeledDown, 20, 10);
            Assert.True(session.CameraY > y - 3);
        });
    }

    [Fact]
    public async Task ClickingTheMinimapMovesTheCamera()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            session.ScrollCamera(-1000, -1000);
            Assert.Equal(0, session.CameraX);

            // The minimap occupies the right-hand 30 columns; click near its right edge.
            await ui.Mouse(MouseFlags.LeftButtonPressed, 115, 5);
            await ui.Mouse(MouseFlags.LeftButtonReleased, 115, 5);
            Assert.True(session.CameraX > 40, $"CameraX={session.CameraX}");
        });
    }
    [Fact]
    public async Task LargeMapScrollsAndMinimapJumpsAcrossIt()
    {
        var game = CityGame.New(new GameConfig { Seed = 5, MapWidth = 640, MapHeight = 192 });
        var session = new GameSession(game, Path.Combine(Path.GetTempPath(), "termcity-ui-" + Guid.NewGuid().ToString("N") + ".json"));
        await UiHarness.Run(session, async ui =>
        {
            Assert.Contains("MINIMAP", await ui.Screen());

            // Ctrl+Arrow moves one screen length, not to the far edge of the large map.
            var start = session.Cursor;
            await ui.Press(Key.CursorRight.WithCtrl);
            Assert.Equal(start.X + session.ViewWidth - 1, session.Cursor.X);

            // The right-hand end of the minimap is the east edge of the map.
            await ui.Mouse(MouseFlags.LeftButtonPressed, 118, 5);
            await ui.Mouse(MouseFlags.LeftButtonReleased, 118, 5);
            Assert.True(session.CameraX > 450, $"CameraX={session.CameraX}");

            // Zoning and building still work far from the origin.
            await ui.Press(Key.R);
            Assert.Equal(1, session.Game.Stats.Residential.Zoned);
        });
    }
    [Fact]
    public async Task AltWheelScrollsSidewaysAndPlainWheelScrollsVertically()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            int x = session.CameraX, y = session.CameraY;
            await ui.Mouse(MouseFlags.WheeledDown | MouseFlags.Alt, 20, 10);
            Assert.True(session.CameraX > x, $"x {x} -> {session.CameraX}");
            Assert.Equal(y, session.CameraY);

            await ui.Mouse(MouseFlags.WheeledUp | MouseFlags.Alt, 20, 10);
            await ui.Mouse(MouseFlags.WheeledUp | MouseFlags.Alt, 20, 10);
            Assert.True(session.CameraX < x);

            int row = session.CameraY, col = session.CameraX;
            await ui.Mouse(MouseFlags.WheeledDown, 20, 10);
            Assert.True(session.CameraY > row);
            Assert.Equal(col, session.CameraX);

            // Shift is no longer a horizontal-scroll modifier: the wheel scrolls vertically as usual.
            row = session.CameraY;
            col = session.CameraX;
            await ui.Mouse(MouseFlags.WheeledDown | Shift, 20, 10);
            Assert.True(session.CameraY > row);
            Assert.Equal(col, session.CameraX);
        });
    }
    [Fact]
    public async Task CtrlWheelAndPlusMinusKeysZoom()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(MouseFlags.WheeledDown | MouseFlags.Ctrl, 40, 10);
            Assert.Equal(-1, session.ZoomLevel);
            Assert.Contains(" 0.5x ", await ui.ScreenWith(" 0.5x "));

            await ui.Mouse(MouseFlags.WheeledUp | MouseFlags.Ctrl, 40, 10);
            Assert.Equal(0, session.ZoomLevel);

            await ui.Press((Key)'+'); // the + key
            Assert.Equal(1, session.ZoomLevel);
            await ui.Press((Key)'-');
            await ui.Press((Key)'-');
            Assert.Equal(-1, session.ZoomLevel);
            await ui.Press(Key.D0);
            Assert.Equal(0, session.ZoomLevel);
        });
    }

    [Fact]
    public async Task ZoomedOutMapStillDrawsAndClicksStillWork()
    {
        var game = CityGame.New(new GameConfig { Seed = 9, MapWidth = 640, MapHeight = 192 });
        var session = new GameSession(game, Path.Combine(Path.GetTempPath(), "termcity-ui-" + Guid.NewGuid().ToString("N") + ".json"));
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press((Key)'-');
            await ui.Press((Key)'-');
            Assert.Equal(-2, session.ZoomLevel);
            Assert.Equal(4, session.Stride);

            // Shift+click selects a whole 4x4 block.
            await ui.Mouse(Press | Shift, 30, 10);
            await ui.Mouse(Release | Shift, 30, 10);
            Assert.Equal(16, session.Selection!.Value.Area);
            var block = session.Selection.Value;

            await ui.Press(Key.R);

            // Every cell of the block is zoned except any that are water.
            int land = block.Cells().Count(c => session.Game.Map.TerrainAt(c.X, c.Y).Buildable);
            Assert.InRange(land, 1, 16);
            Assert.Equal(land, session.Game.Stats.Residential.Zoned);
            Assert.Contains(" 0.25x ", await ui.Screen());
        });
    }

    [Fact]
    public async Task ZoomedInDrawsEveryCellTwoCharactersWide()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press((Key)'+');
            Assert.Equal(2, session.SpanX);
            string screen = await ui.Screen();
            Assert.Contains(" 2x ", screen);

            // Each cell takes two characters, so the map view now shows half as many cells.
            Assert.Equal((session.ViewWidth + 1) / 2, session.VisibleCellsX);
            var cell = session.ScreenToMap(10, 5);
            Assert.Equal(cell, session.ScreenToMap(11, 5));
            Assert.Equal(cell.Offset(1, 0), session.ScreenToMap(12, 5));
        });
    }

    [Fact]
    public async Task InputDebugShowsWhatTheTerminalSends()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.F12);
            Assert.True(session.InputDebug);
            await ui.Mouse(MouseFlags.WheeledDown | Shift, 20, 10);
            Assert.Contains("WheeledDown", session.Message);
            Assert.Contains("Shift", session.Message);
            await ui.Press(Key.F12);
            Assert.False(session.InputDebug);
        });
    }
    // The side panel is 34 columns wide at 120x30: it starts at screen column 86. The minimap takes rows 1-14, the zoom
    // bar is row 15, and the first section header is row 16.
    private const int PanelX = 86;
    private const int ZoomRow = 15;
    private const int FirstHeaderRow = 16;

    [Fact]
    public async Task ZoomButtonsUnderTheMinimapZoomAndShowTheLevel()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            string screen = await ui.ScreenWith("ZOOM");
            Assert.Contains("ZOOM", screen);
            Assert.Contains("[-]", screen);
            Assert.Contains("[+]", screen);
            Assert.Contains(" 1x ", screen);

            await ui.Mouse(Press, PanelX + ZoomBarView.MinusX + 1, ZoomRow);
            await ui.Mouse(Release, PanelX + ZoomBarView.MinusX + 1, ZoomRow);
            Assert.Equal(-1, session.ZoomLevel);
            Assert.Contains(" 0.5x ", await ui.ScreenWith(" 0.5x "));

            await ui.Mouse(Press, PanelX + ZoomBarView.PlusX + 1, ZoomRow);
            await ui.Mouse(Release, PanelX + ZoomBarView.PlusX + 1, ZoomRow);
            await ui.Mouse(Press, PanelX + ZoomBarView.PlusX + 1, ZoomRow);
            await ui.Mouse(Release, PanelX + ZoomBarView.PlusX + 1, ZoomRow);
            Assert.Equal(1, session.ZoomLevel);
            Assert.Contains(" 2x ", await ui.ScreenWith(" 2x "));

            // Clicking between the buttons does nothing.
            await ui.Mouse(Press, PanelX + 1, ZoomRow);
            Assert.Equal(1, session.ZoomLevel);
        });
    }

    [Fact]
    public async Task ZoomLevelIsNoLongerInTheTopBar()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press((Key)'-');
            string topRow = (await ui.Screen()).Split('\n')[0];
            Assert.DoesNotContain("Zoom", topRow);
            Assert.DoesNotContain("0.5x", topRow);
        });
    }

    [Fact]
    public async Task ClickingASectionHeaderCollapsesItAndShowsARightPointingCaret()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            string screen = await ui.Screen();
            Assert.Contains("▼ DEMAND", screen);
            Assert.Contains("▼ CITY", screen);
            Assert.Contains("▼ ZONES", screen);
            Assert.Contains("Population", screen);

            // While DEMAND is open, the CITY header is 5 rows below it: the header, three bars and a blank row.
            Assert.False(session.IsCollapsed(PanelSection.City));
            await ui.Mouse(Press, PanelX + 5, FirstHeaderRow + 5);
            Assert.True(session.IsCollapsed(PanelSection.City));

            screen = await ui.Screen();
            Assert.Contains("► CITY", screen);
            Assert.DoesNotContain("Population", screen);
            Assert.Contains("▼ DEMAND", screen);

            await ui.Mouse(Press, PanelX + 5, FirstHeaderRow + 5);
            Assert.False(session.IsCollapsed(PanelSection.City));
            Assert.Contains("Population", await ui.Screen());
        });
    }

    [Fact]
    public async Task FunctionKeysFoldTheSections()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Press(Key.F2);
            Assert.True(session.IsCollapsed(PanelSection.Demand));
            await ui.Press(Key.F3);
            Assert.True(session.IsCollapsed(PanelSection.City));
            await ui.Press(Key.F4);
            Assert.True(session.IsCollapsed(PanelSection.Zones));

            string screen = await ui.Screen();
            Assert.Contains("► DEMAND", screen);
            Assert.Contains("► CITY", screen);
            Assert.Contains("► ZONES", screen);

            await ui.Press(Key.F2);
            Assert.False(session.IsCollapsed(PanelSection.Demand));
            Assert.Contains("▼ DEMAND", await ui.Screen());
        });
    }

    [Fact]
    public async Task CellDetailsAreOnTheBottomLineAndRoadCostIsGone()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            session.SetMessage(string.Empty);
            session.PlaceCursor(OpenCell(session));
            await Task.Delay(150);
            string[] lines = (await ui.Screen()).Split('\n');
            string bottom = lines.Where(l => l.Trim().Length > 0).Last();
            Assert.True(bottom.Contains($"({session.Cursor.X},{session.Cursor.Y})") && bottom.Contains("Grass"), "bottom line was: " + bottom);

            string screen = string.Join('\n', lines);
            Assert.DoesNotContain("Road:", screen);
            Assert.DoesNotContain("Roads", screen);
            Assert.DoesNotContain("CELL", screen);
            Assert.DoesNotContain("SELECTION", screen);

            // A multi-cell selection is described on the same line.
            var cursor = session.Cursor;
            await ui.Mouse(Press | Shift, 10, 5);
            await ui.Mouse(Drag | Shift, 13, 7);
            await ui.Mouse(Release | Shift, 13, 7);
            session.SetMessage(string.Empty);
            await Task.Delay(150);
            lines = (await ui.Screen()).Split('\n');
            Assert.True(lines.Any(l => l.Contains("Selection 4x3 · 12 cells")), "screen was:\n" + string.Join("\n", lines));
        });
    }

    [Fact]
    public async Task MessagesShowForAFewSecondsThenGiveWayToCellDetails()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            session.SetMessage("Hello there");
            await Task.Delay(100);
            Assert.True(session.MessageVisible);
            Assert.Contains("Hello there", await ui.ScreenWith("Hello there"));
            Assert.True(GameSession.MessageDurationMs >= 3000 && GameSession.MessageDurationMs <= 8000);
        });
    }
    [Fact]
    public async Task InputDebugAlsoReportsHowWellTheApplicationLoopIsKeepingUp()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            Assert.DoesNotContain("loop gap", await ui.Screen());
            await ui.Press(Key.F12);
            session.SetMessage(string.Empty);
            string screen = await ui.ScreenWith("loop gap", 4000);
            Assert.Contains("loop gap", screen);
            Assert.Contains("worst", screen);
            Assert.True(session.LoopGapMs < 1000, $"gap={session.LoopGapMs}");
        });
    }

    [Fact]
    public async Task AStalledLoopStopsEdgeScrollingUntilThePointerMovesAgain()
    {
        var session = NewSession(out _);
        session.ToggleEdgeScroll();
        await UiHarness.Run(session, async ui =>
        {
            var map = ui.Window.SubViews.OfType<MapView>().Single();
            int x = session.CameraX;
            await ui.Mouse(MouseFlags.PositionReport, 40, 10);
            await Task.Delay(100);
            await ui.Mouse(MouseFlags.PositionReport, 2, 10);
            await Task.Delay(400);
            Assert.True(session.CameraX < x, "the pointer parked near the edge should scroll the map");

            // The application was stalled (in the background, say): where the pointer was is no longer trustworthy.
            ui.App.Invoke(() => map.SuppressHover());
            await Task.Delay(150);
            int stopped = session.CameraX;
            await Task.Delay(500);
            Assert.Equal(stopped, session.CameraX);

            // Moving the pointer again brings it back.
            await ui.Mouse(MouseFlags.PositionReport, 3, 10);
            await Task.Delay(100);
            await ui.Mouse(MouseFlags.PositionReport, 2, 10);
            await Task.Delay(400);
            Assert.True(session.CameraX < stopped);
        });
    }

    [Fact]
    public async Task SpaceBarPausesAndUnpausesTheGame()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            Assert.False(session.Game.Paused);
            await ui.Press(Key.Space);
            Assert.True(session.Game.Paused);
            Assert.Contains("PAUSED", await ui.ScreenWith("PAUSED"));

            await ui.Press(Key.Space);
            Assert.False(session.Game.Paused);

            // P still works too, and Space no longer starts a selection.
            await ui.Press(Key.P);
            Assert.True(session.Game.Paused);
            await ui.Press(Key.P);
            Assert.Null(session.Anchor);
            Assert.Null(session.Selection);
        });
    }

    [Fact]
    public async Task TheSKeyStartsAndFinishesAKeyboardSelection()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            session.PlaceCursor(OpenCell(session));
            var start = session.Cursor;
            await ui.Press((Key)'s');
            Assert.NotNull(session.Anchor);
            await ui.Press(Key.CursorRight);
            await ui.Press(Key.CursorDown);
            await ui.Press((Key)'s');
            Assert.Null(session.Anchor);
            Assert.Equal(new CellRect(start.X, start.Y, 2, 2), session.Selection);
        });
    }

    [Fact]
    public async Task AClickHighlightsTheCellAtOnceWithoutWaitingForTheButtonToComeUp()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            await ui.Mouse(Press, 30, 10);
            Assert.Equal(session.ScreenToMap(30, 9), session.Cursor);

            // And it is then drawn: the highlighted cell is the inverse-coloured one.
            Assert.Contains("(", await ui.Screen());

            // Dragging from there pans the map instead, and leaves the highlight where it was on the map.
            var highlighted = session.Cursor;
            await ui.Mouse(Drag, 34, 11);
            await ui.Mouse(Release, 34, 11);
            Assert.Equal(highlighted, session.Cursor);
            Assert.Null(session.Selection);
        });
    }

    [Fact]
    public async Task TheMinimapShowsTheMapAsALandscapeAndClicksBesideItGoToItsEdge()
    {
        var session = NewSession(out _);
        await UiHarness.Run(session, async ui =>
        {
            var map = session.Game.Map;
            var (left, width, height) = MinimapView.FitInPanel(map.Width, map.Height, 34, 26);
            Assert.True(width > height);
            Assert.Equal(MinimapView.SideMargin, left);

            // Click in the margin to the right of the picture: the nearest edge of the map, the east.
            session.ScrollCamera(-10_000, 0);
            if (left + width < 34)
            {
                await ui.Mouse(Press, PanelX + 33, 5);
                await ui.Mouse(Release, PanelX + 33, 5);
                Assert.True(session.CameraX > map.Width / 4, $"CameraX={session.CameraX}");
            }
            // Click in the middle of the picture: the middle of the map.
            await ui.Mouse(Press, PanelX + left + width / 2, 1 + height / 4);
            await ui.Mouse(Release, PanelX + left + width / 2, 1 + height / 4);
            Assert.InRange(session.Cursor.X + session.CameraX, 0, map.Width * 2);
            Assert.InRange(session.CameraX + session.ViewWidth / 2, map.Width * 35 / 100, map.Width * 65 / 100);
        });
    }
}