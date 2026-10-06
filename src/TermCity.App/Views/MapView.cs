using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;
using Attribute = Terminal.Gui.Drawing.Attribute;

using Pos = TermCity.Core.Util.Pos;

namespace TermCity.App.Views;

/// <summary>
/// The scrollable city map. Draws the visible window of the world and turns keyboard and mouse input into
/// <see cref="GameSession"/> operations (cursor movement, selection, building actions).
/// </summary>
internal sealed class MapView : View
{
    private static readonly Rgb Black = Rgb.Hex(0x000000);
    private static readonly Rgb CursorBackground = Rgb.Hex(0xf5f5f5);
    private static readonly Rgb CursorForeground = Rgb.Hex(0x101010);
    private static readonly Rgb SelectionTint = Rgb.Hex(0x58a6ff);

    private enum PointerMode
    {
        None,

        /// <summary>Left button held: dragging the map itself.</summary>
        Pan,

        /// <summary>Shift (or Ctrl or Alt) + left button held: selecting an area.</summary>
        Select,
    }

    // Terminals report nothing when the pointer leaves the window, so hover scrolling driven by a pointer parked in
    // the outermost terminal column is dropped after a short time without movement.
    private const long OuterEdgeStaleMs = 1200;

    // Characters moved per wheel notch.
    private const int WheelStep = 3;

    // Edge scrolling takes a step per frame, sized by the elapsed time, so the speed is the same whatever the frame rate.
    private const long EdgeScrollIntervalMs = 15;

    private readonly GameSession _session;
    private readonly Action<Point> _showContextMenu;
    private readonly Action _quit;
    private readonly HelpView _help;
    private readonly Func<bool> _menuOpen;
    private readonly EdgeScroller _scroller = new();
    private PointerMode _mode;
    private Pos _panAnchor;
    private Point _dragPointer;
    private Point _lastScreen;
    private long _lastMoveAt = Environment.TickCount64;
    private bool _hoverSuppressed;
    private long _lastTickAt = Environment.TickCount64;
    private DateTime _lastMenuOpen = DateTime.MinValue;

    public MapView(GameSession session, HelpView help, Func<bool> menuOpen, Action<Point> showContextMenu, Action quit)
    {
        _session = session;
        _help = help;
        _menuOpen = menuOpen;
        _showContextMenu = showContextMenu;
        _quit = quit;
        CanFocus = true;
    }

    /// <summary>
    /// Ignores where the pointer was last seen until it moves again. Called after the application loop stalled (the
    /// window was in the background, say): the pointer is probably somewhere else by now, and scrolling the map towards
    /// an edge it left minutes ago would be wrong.
    /// </summary>
    public void SuppressHover() => _hoverSuppressed = true;

    public void StopDrag()
    {
        if (_mode == PointerMode.None)
        {
            return;
        }

        _mode = PointerMode.None;
        _scroller.Reset();
        App?.Mouse.UngrabMouse();
    }

    /// <summary>
    /// Called on a short timer. Scrolls the map while the pointer rests near its edge, or while an area selection
    /// is being dragged there. Not used while the map itself is being dragged.
    /// </summary>
    public void EdgeScrollTick()
    {
        long now = Environment.TickCount64;
        if (now - _lastTickAt < EdgeScrollIntervalMs)
        {
            return;
        }

        double seconds = Math.Min(0.25, (now - _lastTickAt) / 1000.0);
        _lastTickAt = now;

        Point? pointer = _mode switch
        {
            PointerMode.Select => _dragPointer,
            PointerMode.Pan => null,
            _ => HoverPointer(now),
        };

        // Hovering at the edge only scrolls if switched on; dragging out a selection to the edge always does.
        if (pointer is not { } p || (_mode != PointerMode.Select && !_session.EdgeScrollEnabled))
        {
            _scroller.Reset();
            return;
        }

        var (dx, dy) = _scroller.Step(p.X, p.Y, Viewport.Width, Viewport.Height, seconds);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        _session.ScrollChars(dx, dy);
        if (_mode == PointerMode.Select)
        {
            _session.UpdateDrag(ViewToMap(p));
        }
    }

    /// <summary>
    /// Where the mouse pointer rests inside the map view, or null if it is elsewhere. Plain pointer movement is
    /// not delivered to views as events, so the application's last known mouse position is polled instead.
    /// </summary>
    private Point? HoverPointer(long now)
    {
        if (App?.Mouse is not { } mouse || _help.Visible || _menuOpen())
        {
            return null;
        }

        var screen = mouse.LastMousePosition ?? Point.Empty;
        if (screen != _lastScreen)
        {
            _lastScreen = screen;
            _lastMoveAt = now;
            _hoverSuppressed = false;
        }

        // Pressing a key hands control back to the keyboard until the mouse moves again.
        if (_hoverSuppressed || !mouse.CachedViewsUnderMouse.Contains(this))
        {
            return null;
        }

        if (screen.X == 0 && now - _lastMoveAt > OuterEdgeStaleMs)
        {
            return null;
        }

        var view = ScreenToViewport(screen);
        bool inside = view.X >= 0 && view.Y >= 0 && view.X < Viewport.Width && view.Y < Viewport.Height;
        return inside ? view : null;
    }
    private Pos ViewToMap(Point view)
    {
        var map = _session.Game.Map;
        var p = _session.ScreenToMap(view.X, view.Y);
        return new Pos(Math.Clamp(p.X, 0, map.Width - 1), Math.Clamp(p.Y, 0, map.Height - 1));
    }

    // ---- Drawing ----------------------------------------------------------------------------------------------

    private Rectangle _marked = Rectangle.Empty;

    /// <summary>The part of the view showing the cursor or selection, as it would be drawn right now.</summary>
    private Rectangle MarkedRect()
    {
        var cells = _session.Preview?.Area ?? _session.Selection ?? new CellRect(_session.Cursor.X, _session.Cursor.Y, 1, 1);
        int stride = _session.Stride, span = _session.SpanX;
        int camX = _session.CameraX, camY = _session.CameraY;
        int x0, x1, y0, y1;
        if (stride > 1)
        {
            x0 = Math.DivRem(cells.X - camX, stride, out _);
            x1 = Math.DivRem(cells.Right - camX, stride, out _);
            y0 = Math.DivRem(cells.Y - camY, stride, out _);
            y1 = Math.DivRem(cells.Bottom - camY, stride, out _);
        }
        else
        {
            x0 = (cells.X - camX) * span;
            x1 = (cells.Right - camX) * span + span - 1;
            y0 = cells.Y - camY;
            y1 = cells.Bottom - camY;
        }

        var rect = Rectangle.Intersect(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1), new Rectangle(0, 0, Viewport.Width, Viewport.Height));
        return rect;
    }

    /// <summary>
    /// Redraws only what a cursor or selection change touches. Every cell written to the terminal costs bytes, so a
    /// whole-map redraw for one highlighted cell would make each click and arrow key as expensive as a scroll step.
    /// </summary>
    public void InvalidateSelection()
    {
        var now = MarkedRect();
        var area = _marked.IsEmpty ? now : now.IsEmpty ? _marked : Rectangle.Union(_marked, now);
        _marked = now;
        if (!area.IsEmpty)
        {
            SetNeedsDraw(area);
        }
    }

    // The toolkit keeps the rectangle that needs redrawing but does not expose it. Reading it lets a small change
    // (the cursor moving) skip rewriting the whole map; if it cannot be read, everything is drawn as before.
    private static readonly Func<View, Rectangle>? ReadNeedsDrawRect = CreateNeedsDrawReader();

    private static Func<View, Rectangle>? CreateNeedsDrawReader()
    {
        try
        {
            var getter = typeof(View).GetProperty("NeedsDrawRect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetMethod;
            return getter is null ? null : (Func<View, Rectangle>)Delegate.CreateDelegate(typeof(Func<View, Rectangle>), getter);
        }
        catch (Exception ex) when (ex is ArgumentException or MethodAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private const int MinSkip = 4;
    private (int X, int Y, int Zoom) _drawnCamera;

    // Clearing would blank every cell, undoing the point of leaving unchanged ones alone; OnDrawingContent writes what is needed.
    protected override bool OnClearingViewport() => true;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var viewport = Viewport;
        var area = new Rectangle(0, 0, viewport.Width, viewport.Height);
        if (ReadNeedsDrawRect?.Invoke(this) is { IsEmpty: false } need)
        {
            var part = Rectangle.Intersect(need, area);
            if (!part.IsEmpty)
            {
                area = part;
            }
        }

        _session.SetViewport(viewport.Width, viewport.Height);

        var game = _session.Game;
        var map = game.Map;
        var selection = _session.Selection;
        var cursor = _session.Cursor;
        int stride = _session.Stride, span = _session.SpanX;
        var sampler = stride > 1 ? new BlockSampler(game) : null;

        // The screen is not cleared first (see OnClearingViewport), so a cell that already shows the right thing
        // costs nothing to leave alone. Most of the ground looks the same one step to the side, which is what makes
        // scrolling cheap enough for a terminal to keep up with.
        // While scrolling nearly every cell moves, and the many short gaps left by skipping cells cost the terminal more than they save.
        var camera = (_session.CameraX, _session.CameraY, _session.ZoomLevel);
        int minSkip = camera == _drawnCamera ? MinSkip : int.MaxValue;
        _drawnCamera = camera;
        var contents = App?.Driver?.Contents;
        var origin = ViewportToScreen(new Point(0, 0));
        bool Unchanged(int vx, int vy, string glyph, Attribute attr)
        {
            int sx = origin.X + vx, sy = origin.Y + vy;
            if (contents is null || sx < 0 || sy < 0 || sy >= contents.GetLength(0) || sx >= contents.GetLength(1))
            {
                return false;
            }

            var cell = contents[sy, sx];
            return cell.Attribute == attr && cell.Grapheme == glyph;
        }

        // Cells that share a colour are drawn as one string: far fewer calls into the UI toolkit than one per cell.
        var run = new StringBuilder(viewport.Width);
        var glyphs = new string[viewport.Width];
        var attrs = new Attribute[viewport.Width];
        var unchanged = new bool[viewport.Width];
        for (int vy = area.Y; vy < area.Bottom; vy++)
        {
            for (int vx = area.X; vx < area.Right; vx++)
            {
                int size = stride;
                int mx = stride > 1 ? _session.CameraX + vx * stride : _session.CameraX + vx / span;
                int my = stride > 1 ? _session.CameraY + vy * stride : _session.CameraY + vy;
                Attribute attr;
                string glyph;

                if (!map.InBounds(mx, my))
                {
                    attr = Colors.Attr(Black, Black);
                    glyph = " ";
                }
                else
                {
                    var visual = sampler is null ? CellRenderer.Render(game, mx, my) : sampler.Sample(mx, my, size);
                    var fg = visual.Foreground;
                    var bg = visual.Background;
                    glyph = span > 1 && vx % span != 0 ? Filler(map, mx, my, visual.Glyph) : visual.Glyph;

                    if (_session.Preview is { } preview && preview.Area.X < mx + size && preview.Area.Right >= mx &&
                        preview.Area.Y < my + size && preview.Area.Bottom >= my)
                    {
                        bool valid = false, blocked = false;
                        for (int py = Math.Max(my, preview.Area.Y); py <= Math.Min(my + size - 1, preview.Area.Bottom); py++)
                        {
                            for (int px = Math.Max(mx, preview.Area.X); px <= Math.Min(mx + size - 1, preview.Area.Right); px++)
                            {
                                if (preview.IsValid(game, px, py)) valid = true;
                                else blocked = true;
                            }
                        }

                        var tint = blocked ? valid ? Colors.Heading : Colors.Bad : Colors.Good;
                        fg = Rgb.Blend(fg, Rgb.Hex(0xffffff), 0.45);
                        bg = Rgb.Blend(bg, tint, 0.6);
                    }
                    else if (cursor.X >= mx && cursor.X < mx + size && cursor.Y >= my && cursor.Y < my + size)
                    {
                        fg = CursorForeground;
                        bg = CursorBackground;
                    }
                    else if (selection is { } s && s.X < mx + size && s.Right >= mx && s.Y < my + size && s.Bottom >= my)
                    {
                        bg = Rgb.Blend(bg, SelectionTint, 0.5);
                        fg = Rgb.Blend(fg, Rgb.Hex(0xffffff), 0.45);
                    }

                    attr = Colors.Attr(fg, bg);
                }

                glyphs[vx] = glyph;
                attrs[vx] = attr;
                unchanged[vx] = Unchanged(vx, vy, glyph, attr);
            }

            run.Clear();
            Attribute? runAttr = null;
            int runStart = area.X;
            for (int vx = area.X; vx < area.Right; vx++)
            {
                // Jumping over a stretch of unchanged cells costs a cursor move, so only long stretches are skipped.
                if (unchanged[vx])
                {
                    int end = vx;
                    while (end < area.Right && unchanged[end])
                    {
                        end++;
                    }

                    if (end - vx >= minSkip)
                    {
                        FlushRun(run, runAttr, runStart, vy);
                        runAttr = null;
                        vx = end - 1;
                        continue;
                    }
                }

                if (runAttr != attrs[vx])
                {
                    FlushRun(run, runAttr, runStart, vy);
                    runAttr = attrs[vx];
                    runStart = vx;
                }

                run.Append(glyphs[vx]);
            }

            FlushRun(run, runAttr, runStart, vy);
        }
        _marked = MarkedRect();
        return true;
    }

    private void FlushRun(StringBuilder run, Attribute? attr, int x, int y)
    {
        if (run.Length == 0 || attr is not { } a)
        {
            return;
        }

        SetAttribute(a);
        AddStr(x, y, run.ToString());
        run.Clear();
    }

    /// <summary>
    /// The second character of a cell drawn two characters wide: roads continue sideways into the next cell,
    /// everything else simply repeats its glyph.
    /// </summary>
    private static string Filler(GameMap map, int x, int y, string glyph)
    {
        if (map.RoadTypeAt(x, y) is not { } road)
        {
            return glyph;
        }

        return map.HasRoad(x + 1, y) ? road.GlyphFor(10) : " ";
    }
    // ---- Keyboard ---------------------------------------------------------------------------------------------

    protected override bool OnKeyDown(Key key)
    {
        _hoverSuppressed = true;

        if (_help.Visible)
        {
            _help.Visible = false;
            return true;
        }

        FrameTrace.Current?.RecordMouse($"key {key}");
        if (_session.InputDebug)
        {
            _session.SetMessage($"Key {key}");
        }

        bool ctrl = key.IsCtrl;
        bool jump = ctrl;
        bool shift = key.IsShift;
        KeyCode code = key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.CtrlMask | KeyCode.AltMask);
        bool macJumpKey = PlatformKeys.IsMacOS &&
            code is KeyCode.PageUp or KeyCode.PageDown or KeyCode.Home or KeyCode.End;

        if (code == KeyCode.Z && ctrl)
        {
            _session.RequestUndo();
            return true;
        }

        if (_session.Preview is not null)
        {
            if (code == KeyCode.Enter || (code == KeyCode.Y && !ctrl && !key.IsAlt))
            {
                _session.ConfirmPreview();
                return true;
            }

            if (code == KeyCode.Esc || (code == KeyCode.N && !ctrl && !key.IsAlt))
            {
                _session.CancelPreview();
                return true;
            }

            if (!_session.RoadToolActive &&
                (code is KeyCode.CursorUp or KeyCode.CursorDown or KeyCode.CursorLeft or KeyCode.CursorRight || macJumpKey))
            {
                return true;
            }

            if (code is KeyCode.R or KeyCode.C or KeyCode.I or KeyCode.U or KeyCode.B or KeyCode.D or KeyCode.Delete or KeyCode.S or KeyCode.T or KeyCode.M)
            {
                return true;
            }
        }

        switch (code)
        {
            case KeyCode.CursorUp: Move(0, -1, jump, shift); return true;
            case KeyCode.CursorDown: Move(0, 1, jump, shift); return true;
            case KeyCode.CursorLeft: Move(-1, 0, jump, shift); return true;
            case KeyCode.CursorRight: Move(1, 0, jump, shift); return true;
            case KeyCode.PageUp when PlatformKeys.IsMacOS: Move(0, -1, jump: true, shift); return true;
            case KeyCode.PageDown when PlatformKeys.IsMacOS: Move(0, 1, jump: true, shift); return true;
            case KeyCode.Home when PlatformKeys.IsMacOS: Move(-1, 0, jump: true, shift); return true;
            case KeyCode.End when PlatformKeys.IsMacOS: Move(1, 0, jump: true, shift); return true;
            case KeyCode.Space: _session.TogglePause(); return true;
            case KeyCode.Esc: _session.ClearSelection(); _session.SetMessage(string.Empty); return true;
            case KeyCode.Enter:
            case KeyCode.M when !ctrl:
                OpenMenuAtCursor();
                return true;
            case KeyCode.F1:
                _help.Toggle();
                return true;
            case KeyCode.F5: _session.QuickSave(); return true;
            case KeyCode.F9: _session.RequestLoad(_session.SavePath); return true;
            case KeyCode.F10: _session.ShowSessionMenu(); return true;
            case KeyCode.F6:
                if (_session.GuideVisible) _session.DismissGuide();
                else _session.ShowGuide();
                return true;
            case KeyCode.F7: _session.ShowReport(); return true;
            case KeyCode.F8: _session.ShowGrowthReport(); return true;
            case KeyCode.F2: _session.ToggleSection(PanelSection.Demand); return true;
            case KeyCode.F3: _session.ToggleSection(PanelSection.City); return true;
            case KeyCode.F4: _session.ToggleSection(PanelSection.Zones); return true;
            case KeyCode.F12: _session.ToggleInputDebug(); return true;
            case KeyCode.Q when ctrl: _quit(); return true;
        }

        if (ctrl || key.IsAlt)
        {
            return false;
        }

        switch (code)
        {
            case KeyCode.R: _session.Zone(ZoneType.Residential); return true;
            case KeyCode.C: _session.Zone(ZoneType.Commercial); return true;
            case KeyCode.I: _session.Zone(ZoneType.Industrial); return true;
            case KeyCode.U: _session.Dezone(); return true;
            case KeyCode.B: _session.PreviewRoad(); return true;
            case KeyCode.T: _session.BeginRoadLine(); return true;
            case KeyCode.D:
            case KeyCode.Delete: _session.PreviewDemolish(); return true;
            case KeyCode.P: _session.TogglePause(); return true;
            case KeyCode.E: _session.ToggleEdgeScroll(); return true;
            case KeyCode.S: _session.ToggleSelectionMode(); return true;
            case KeyCode.D0: _session.SetZoom(0); return true;
            case KeyCode.D1: _session.SetSpeed(Core.Simulation.GameSpeed.Slow); return true;
            case KeyCode.D2: _session.SetSpeed(Core.Simulation.GameSpeed.Medium); return true;
            case KeyCode.D3: _session.SetSpeed(Core.Simulation.GameSpeed.Fast); return true;
        }

        switch (key.AsRune.Value)
        {
            case '?':
                _help.Toggle();
                return true;
            case '+' or '=':
                _session.ZoomBy(1);
                return true;
            case '-' or '_':
                _session.ZoomBy(-1);
                return true;
        }

        return false;
    }

    private void Move(int dx, int dy, bool jump, bool extend)
    {
        if (jump)
        {
            _session.JumpCursor(dx, dy, extend);
        }
        else
        {
            _session.MoveCursor(dx, dy, extend);
        }
    }

    private void OpenMenuAtCursor()
    {
        var cursor = _session.Cursor;
        var screen = ViewportToScreen(new Point(cursor.X - _session.CameraX, cursor.Y - _session.CameraY));
        _showContextMenu(new Point(screen.X + 1, screen.Y + 1));
    }

    // ---- Mouse ------------------------------------------------------------------------------------------------

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (_session.Preview is not null && !_session.RoadToolActive)
        {
            return true;
        }

        var flags = mouse.Flags;
        var view = mouse.Position ?? Point.Empty;

        FrameTrace.Current?.RecordMouse($"{flags} view=({view.X},{view.Y}) mode={_mode}");

        if (_session.InputDebug && !flags.HasFlag(MouseFlags.PositionReport))
        {
            _session.SetMessage($"Mouse {flags} at view ({view.X},{view.Y})");
        }

        // In Terminal.Gui the "sideways" wheel flags are really the vertical ones plus Ctrl (WheeledLeft = WheeledUp |
        // Ctrl, WheeledRight = WheeledDown | Ctrl), so a held Ctrl and a sideways wheel look the same: both zoom here.
        // Alt + wheel scrolls sideways (terminals and Terminal.Gui do not reliably pass Shift through).
        bool wheelUp = flags.HasFlag(MouseFlags.WheeledUp), wheelDown = flags.HasFlag(MouseFlags.WheeledDown);
        if (wheelUp || wheelDown)
        {
            int direction = wheelUp ? -1 : 1;
            if (flags.HasFlag(MouseFlags.Alt))
            {
                _session.ScrollChars(direction * WheelStep, 0);
            }
            else if (flags.HasFlag(MouseFlags.Ctrl))
            {
                // Wheel up zooms in, like most maps.
                _session.ZoomBy(-direction, view.X, view.Y);
            }
            else
            {
                _session.ScrollChars(0, direction * WheelStep);
            }

            return true;
        }
        if (flags.HasFlag(MouseFlags.RightButtonClicked) || flags.HasFlag(MouseFlags.RightButtonReleased))
        {
            if (_session.Preview is not null)
            {
                return true;
            }

            // A release is normally followed by a synthesized click; only open the menu once.
            if ((DateTime.UtcNow - _lastMenuOpen).TotalMilliseconds > 250)
            {
                _lastMenuOpen = DateTime.UtcNow;
                _session.PrepareContextMenuAt(ViewToMap(view));
                _showContextMenu(mouse.ScreenPosition);
            }

            return true;
        }

        if (flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            if (!HasFocus)
            {
                SetFocus();
            }

            if (_mode == PointerMode.None)
            {
                BeginLeftButton(view, flags);
            }
            else
            {
                Drag(view);
            }

            return true;
        }

        if (_mode != PointerMode.None && flags.HasFlag(MouseFlags.PositionReport))
        {
            Drag(view);
            return true;
        }

        if (_mode != PointerMode.None && (flags.HasFlag(MouseFlags.LeftButtonReleased) || flags.HasFlag(MouseFlags.LeftButtonClicked)))
        {
            EndLeftButton(view);
            return true;
        }

        return base.OnMouseEvent(mouse);
    }

    private void BeginLeftButton(Point view, MouseFlags flags)
    {
        _dragPointer = view;
        App?.Mouse.GrabMouse(this);
        if (_session.RoadToolActive || flags.HasFlag(MouseFlags.Shift) || flags.HasFlag(MouseFlags.Ctrl) || flags.HasFlag(MouseFlags.Alt))
        {
            _mode = PointerMode.Select;
            _session.BeginDrag(ViewToMap(view));
        }
        else
        {
            // Highlight the cell the moment the button goes down; if it turns into a drag, the map is panned instead.
            _session.PlaceCursor(ViewToMap(view));
            _mode = PointerMode.Pan;
            _panAnchor = _session.ScreenToMap(view.X, view.Y);
        }
    }

    private void Drag(Point view)
    {
        _dragPointer = view;
        if (_mode == PointerMode.Select)
        {
            _session.UpdateDrag(ViewToMap(view));
        }
        else
        {
            _session.PanCamera(_panAnchor, view.X, view.Y);
        }
    }

    private void EndLeftButton(Point view)
    {
        if (_mode == PointerMode.Select)
        {
            _session.UpdateDrag(ViewToMap(view));
            _session.EndSelection();
        }
        else
        {
            _session.PanCamera(_panAnchor, view.X, view.Y);
        }

        _mode = PointerMode.None;
        _scroller.Reset();
        App?.Mouse.UngrabMouse();
    }
}