using System.Globalization;
using TermCity.Core.Buildings;
using TermCity.Core.Roads;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public enum MessageKind
{
    Info,
    Success,
    Error,
}

/// <summary>The headed sections of the side panel, each of which can be collapsed.</summary>
public enum PanelSection
{
    Demand,
    City,
    Zones,
}

/// <summary>
/// Everything about the player's view of the game that is not simulation state: the cursor, the selected area,
/// the scrolled camera and the status message. Pure logic with no UI dependency so it is fully testable.
/// </summary>
public sealed partial class GameSession
{
    private const int ScrollMargin = 1;

    public const int MinZoom = -2;
    public const int MaxZoom = 1;

    private bool _anchorFromShift;

    public GameSession(CityGame game, string? savePath = null, bool showGuide = false)
    {
        SavePath = savePath ?? SaveGameStore.DefaultPath;
        Game = game;
        Game.Changed += OnGameChanged;
        Cursor = new Pos(game.Map.Width / 2, game.Map.Height / 2);
        ViewWidth = 80;
        ViewHeight = 24;
        CenterOn(Cursor);
        InitializeFeedback();
        if (showGuide)
        {
            Game.Paused = true;
            GuideVisible = !Game.GuideDismissed;
            ShowGuide();
        }
    }

    public CityGame Game { get; private set; }

    public string SavePath { get; }

    public Pos Cursor { get; private set; }

    /// <summary>The highlighted area, if any. Actions apply to this, or to the cursor cell when there is none.</summary>
    public CellRect? Selection { get; private set; }

    /// <summary>Set while a selection is being extended (mouse drag, Shift+arrows or Space mode).</summary>
    public Pos? Anchor { get; private set; }

    public int CameraX { get; private set; }

    public int CameraY { get; private set; }

    public int ViewWidth { get; private set; }

    public int ViewHeight { get; private set; }

    public string Message { get; private set; } = "Welcome to TermCity! Press F1 for help.";

    public MessageKind MessageKind { get; private set; } = MessageKind.Info;

    public event Action? Changed;

    /// <summary>Raised when only the camera moved (scrolling, panning, zooming), so just the map and minimap need redrawing.</summary>
    public event Action? CameraChanged;

    /// <summary>
    /// Raised when only the cursor or the selection changed. The side panels do not depend on either, so just the map
    /// and the status line need redrawing, which is much cheaper than a full redraw and makes clicks feel immediate.
    /// </summary>
    public event Action? SelectionChanged;

    public CellRect ActiveArea => Selection ?? BlockAt(Cursor);

    /// <summary>The part of the map currently on screen, in map cells.</summary>
    public CellRect ViewRect => new(CameraX, CameraY, VisibleCellsX, VisibleCellsY);

    // ---- Zoom -------------------------------------------------------------------------------------------------
    // Level 0 draws one map cell per character. Negative levels zoom out: each character stands for a square block of
    // 2 or 4 cells. Level +1 zooms in: each map cell is drawn two characters wide.

    public int ZoomLevel { get; private set; }

    /// <summary>Map cells (per side) summarised by one character when zoomed out; 1 otherwise.</summary>
    public int Stride => ZoomLevel < 0 ? 1 << -ZoomLevel : 1;

    /// <summary>Characters wide that one map cell is drawn when zoomed in; 1 otherwise.</summary>
    public int SpanX => ZoomLevel > 0 ? 2 : 1;

    public int VisibleCellsX => ZoomLevel < 0 ? ViewWidth * Stride : (ViewWidth + SpanX - 1) / SpanX;

    public int VisibleCellsY => ZoomLevel < 0 ? ViewHeight * Stride : ViewHeight;

    /// <summary>The zoom as a magnification: 1x is normal, 2x is zoomed in, 0.5x and below are zoomed out.</summary>
    public string ZoomLabel => ZoomLevel switch
    {
        < 0 => (1.0 / Stride).ToString("0.###", CultureInfo.InvariantCulture) + "x",
        > 0 => $"{SpanX}x",
        _ => "1x",
    };

    /// <summary>
    /// Zooms in (positive) or out (negative). The map cell under the view position (default: the centre) stays put.
    /// </summary>
    public void ZoomBy(int delta, int? viewX = null, int? viewY = null)
    {
        int level = Math.Clamp(ZoomLevel + delta, MinZoom, MaxZoom);
        if (level == ZoomLevel)
        {
            SetMessage(delta > 0 ? "Already zoomed in as far as it goes." : "Already zoomed out as far as it goes.");
            return;
        }

        SetZoom(level, viewX, viewY);
    }

    public void SetZoom(int level, int? viewX = null, int? viewY = null)
    {
        level = Math.Clamp(level, MinZoom, MaxZoom);
        int fx = viewX ?? ViewWidth / 2, fy = viewY ?? ViewHeight / 2;
        var focus = ScreenToMap(fx, fy);
        ZoomLevel = level;
        CameraX = focus.X - ViewOffsetX(fx);
        CameraY = focus.Y - ViewOffsetY(fy);
        ClampCamera();
        Changed?.Invoke();
    }

    private int ViewOffsetX(int viewX) => ZoomLevel < 0 ? viewX * Stride : viewX / SpanX;

    private int ViewOffsetY(int viewY) => ZoomLevel < 0 ? viewY * Stride : viewY;

    /// <summary>The square of cells that one character covers when zoomed out (just the one cell otherwise).</summary>
    public CellRect BlockAt(Pos p)
    {
        int s = Stride;
        if (s == 1)
        {
            return CellRect.Single(p);
        }

        int x = p.X / s * s, y = p.Y / s * s;
        return new CellRect(x, y, Math.Min(s, Game.Map.Width - x), Math.Min(s, Game.Map.Height - y));
    }

    /// <summary>The area between two cells. Zoomed out it snaps outward to whole blocks.</summary>
    private CellRect MakeSelection(Pos a, Pos b)
    {
        if (Stride == 1)
        {
            return CellRect.FromCorners(a, b);
        }

        var first = BlockAt(a);
        var second = BlockAt(b);
        int left = Math.Min(first.X, second.X), top = Math.Min(first.Y, second.Y);
        int right = Math.Max(first.Right, second.Right), bottom = Math.Max(first.Bottom, second.Bottom);
        return new CellRect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>Raised diagnostics for input, shown in the message bar (F12) to help work out what a terminal sends.</summary>
    public bool InputDebug { get; private set; }

    /// <summary>Milliseconds between the last two ticks of the application loop (about 20 when it is keeping up).</summary>
    public long LoopGapMs { get; private set; }

    /// <summary>The longest such gap since input debugging was switched on: a long one means the loop was stalled.</summary>
    public long LoopWorstGapMs { get; private set; }

    public void RecordLoopGap(long gapMs)
    {
        LoopGapMs = gapMs;
        LoopWorstGapMs = Math.Max(LoopWorstGapMs, gapMs);
    }

    public void ToggleInputDebug()
    {
        InputDebug = !InputDebug;
        LoopWorstGapMs = 0;
        SetMessage(InputDebug ? "Input debug on: the last mouse and key events are shown here." : "Input debug off.");
    }

    private void OnGameChanged()
    {
        _revision++;
        UpdateFeedback();
        Changed?.Invoke();
    }

    /// <summary>How long a message stays in the status line before it gives way to details of the cell under the cursor.</summary>
    public const int MessageDurationMs = 5000;

    private long _messageAt = Environment.TickCount64;

    public void SetMessage(string message, MessageKind kind = MessageKind.Info)
    {
        Message = message;
        MessageKind = kind;
        _messageAt = Environment.TickCount64;
        Changed?.Invoke();
    }

    /// <summary>Whether there is a recent message to show (otherwise the status line shows the cell under the cursor).</summary>
    public bool MessageVisible => Message.Length > 0 && Environment.TickCount64 - _messageAt < MessageDurationMs;

    // ---- Side panel sections -----------------------------------------------------------------------------------

    private readonly HashSet<PanelSection> _collapsed = [];

    public bool IsCollapsed(PanelSection section) => _collapsed.Contains(section);

    public void ToggleSection(PanelSection section)
    {
        if (!_collapsed.Remove(section))
        {
            _collapsed.Add(section);
        }

        Changed?.Invoke();
    }

    // ---- Camera -----------------------------------------------------------------------------------------------

    public void SetViewport(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == ViewWidth && height == ViewHeight))
        {
            return;
        }

        ViewWidth = width;
        ViewHeight = height;
        FollowCursor();
    }

    /// <summary>Scrolls by whole map cells.</summary>
    public void ScrollCamera(int dx, int dy)
    {
        int x = CameraX, y = CameraY;
        CameraX += dx;
        CameraY += dy;
        ClampCamera();
        if (x != CameraX || y != CameraY)
        {
            CameraChanged?.Invoke();
        }
    }

    /// <summary>Scrolls by a number of on-screen characters, whatever the zoom level.</summary>
    public void ScrollChars(int dx, int dy)
    {
        if (ZoomLevel < 0)
        {
            ScrollCamera(dx * Stride, dy * Stride);
        }
        else
        {
            // Zoomed in, a cell is wider than a character: round away from zero so any request still moves the map.
            ScrollCamera(Math.Sign(dx) * ((Math.Abs(dx) + SpanX - 1) / SpanX), dy);
        }
    }

    /// <summary>
    /// When on, resting the mouse pointer close to the edge of the map view scrolls the map. Off by default: it is easy
    /// to scroll by accident. (Dragging a selection to the edge always scrolls.)
    /// </summary>
    public bool EdgeScrollEnabled { get; private set; }

    public void ToggleEdgeScroll()
    {
        EdgeScrollEnabled = !EdgeScrollEnabled;
        SetMessage(EdgeScrollEnabled ? "Edge scrolling on." : "Edge scrolling off.");
    }

    /// <summary>
    /// Drags the map: moves the camera so that the map cell <paramref name="anchor"/> stays under the
    /// view position (viewX, viewY), like grabbing the map with a hand.
    /// </summary>
    public void PanCamera(Pos anchor, int viewX, int viewY)
    {
        int x = CameraX, y = CameraY;
        CameraX = anchor.X - ViewOffsetX(viewX);
        CameraY = anchor.Y - ViewOffsetY(viewY);
        ClampCamera();
        if (x != CameraX || y != CameraY)
        {
            CameraChanged?.Invoke();
        }
    }

    /// <summary>Moves the highlight cursor to a cell without selecting anything and without scrolling.</summary>
    public void PlaceCursor(Pos p)
    {
        Cursor = Clamp(p);
        Anchor = null;
        Selection = null;
        SelectionChanged?.Invoke();
    }

    public void CenterOn(Pos p)
    {
        CameraX = p.X - VisibleCellsX / 2;
        CameraY = p.Y - VisibleCellsY / 2;
        ClampCamera();
        CameraChanged?.Invoke();
    }

    /// <summary>The map cell at a position in the map view (the top-left cell of the block when zoomed out).</summary>
    public Pos ScreenToMap(int viewX, int viewY) => new(CameraX + ViewOffsetX(viewX), CameraY + ViewOffsetY(viewY));

    private void ClampCamera()
    {
        CameraX = Math.Clamp(CameraX, 0, Math.Max(0, Game.Map.Width - VisibleCellsX));
        CameraY = Math.Clamp(CameraY, 0, Math.Max(0, Game.Map.Height - VisibleCellsY));

        // Zoomed out, blocks line up with multiples of the stride so they do not shift as the camera moves.
        CameraX -= CameraX % Stride;
        CameraY -= CameraY % Stride;
    }

    private void FollowCursor()
    {
        int cameraX = CameraX, cameraY = CameraY;
        int visibleX = VisibleCellsX, visibleY = VisibleCellsY;
        int marginX = Math.Min(ScrollMargin * Stride, Math.Max(0, (visibleX - 1) / 2));
        int marginY = Math.Min(ScrollMargin * Stride, Math.Max(0, (visibleY - 1) / 2));

        if (Cursor.X < CameraX + marginX)
        {
            CameraX = Cursor.X - marginX;
        }
        else if (Cursor.X > CameraX + visibleX - 1 - marginX)
        {
            CameraX = Cursor.X - (visibleX - 1 - marginX);
        }

        if (Cursor.Y < CameraY + marginY)
        {
            CameraY = Cursor.Y - marginY;
        }
        else if (Cursor.Y > CameraY + visibleY - 1 - marginY)
        {
            CameraY = Cursor.Y - (visibleY - 1 - marginY);
        }

        ClampCamera();
        if (cameraX != CameraX || cameraY != CameraY)
        {
            CameraChanged?.Invoke();
        }

        SelectionChanged?.Invoke();
    }

    // ---- Cursor and selection ---------------------------------------------------------------------------------

    private Pos Clamp(Pos p) => new(Math.Clamp(p.X, 0, Game.Map.Width - 1), Math.Clamp(p.Y, 0, Game.Map.Height - 1));

    /// <summary>Moves the highlight cursor. With <paramref name="extend"/> (Shift) the selection grows from where the cursor was.</summary>
    public void MoveCursor(int dx, int dy, bool extend = false) => MoveCursorCells(dx * Stride, dy * Stride, extend);

    private void MoveCursorCells(int dx, int dy, bool extend)
    {
        if (RoadToolActive)
        {
            Cursor = Clamp(Cursor.Offset(dx, dy));
            RefreshRoadLine();
            FollowCursor();
            return;
        }

        if (extend && Anchor is null)
        {
            Anchor = Cursor;
            _anchorFromShift = true;
        }
        else if (!extend && Anchor is not null && _anchorFromShift)
        {
            // Shift was let go: a plain move starts over.
            Anchor = null;
            Selection = null;
        }
        else if (!extend && Anchor is null)
        {
            Selection = null;
        }

        Cursor = Clamp(Cursor.Offset(dx, dy));
        if (Anchor is { } anchor)
        {
            Selection = MakeSelection(anchor, Cursor);
        }

        FollowCursor();
    }

    /// <summary>Ctrl+arrow: moves the cursor a full screen length in a direction.</summary>
    public void JumpCursor(int dirX, int dirY, bool extend = false) =>
        MoveCursorCells(dirX * Math.Max(1, VisibleCellsX - 1), dirY * Math.Max(1, VisibleCellsY - 1), extend);

    /// <summary>Single click: selects exactly one cell.</summary>
    public void SelectCell(Pos p)
    {
        Cursor = Clamp(p);
        Anchor = null;
        Selection = BlockAt(Cursor);
        FollowCursor();
    }

    public void BeginDrag(Pos p)
    {
        if (RoadToolActive)
        {
            Cursor = Clamp(p);
            _roadAnchor = Cursor;
            RefreshRoadLine();
            return;
        }

        Cursor = Clamp(p);
        Anchor = Cursor;
        _anchorFromShift = false;
        Selection = BlockAt(Cursor);
        SelectionChanged?.Invoke();
    }

    public void UpdateDrag(Pos p)
    {
        if (RoadToolActive)
        {
            Cursor = Clamp(p);
            RefreshRoadLine();
            return;
        }

        if (Anchor is not { } anchor)
        {
            return;
        }

        Cursor = Clamp(p);
        Selection = MakeSelection(anchor, Cursor);
        SelectionChanged?.Invoke();
    }

    /// <summary>Ends a drag or keyboard selection; the selected area stays highlighted.</summary>
    public void EndSelection()
    {
        Anchor = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>S: starts keyboard selection at the cursor, or finishes it if already started.</summary>
    public void ToggleSelectionMode()
    {
        if (Anchor is null)
        {
            Anchor = Cursor;
            _anchorFromShift = false;
            Selection = BlockAt(Cursor);
            SetMessage("Selecting: move with the arrow keys, S to finish, Enter for the menu.");
        }
        else
        {
            Anchor = null;
            SetMessage(Selection is { } s ? $"Selected {s.Width}x{s.Height} ({s.Area} cells)." : string.Empty);
        }

        Changed?.Invoke();
    }

    public void ClearSelection()
    {
        CancelPreview();
        Anchor = null;
        Selection = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>Makes sure a right-click at <paramref name="p"/> acts on a sensible area.</summary>
    public void PrepareContextMenuAt(Pos p)
    {
        if (Selection is { } sel && sel.Contains(p))
        {
            Cursor = Clamp(p);
            Anchor = null;
            SelectionChanged?.Invoke();
        }
        else
        {
            SelectCell(p);
        }
    }

    // ---- Actions ----------------------------------------------------------------------------------------------

    public ActionResult Zone(ZoneType zone) => Execute(() => Game.Designate(ActiveArea, zone));

    public ActionResult Dezone() => Execute(() => Game.Dezone(ActiveArea));

    public ActionResult BuildRoad(RoadType? type = null) => Execute(() => Game.BuildRoad(ActiveArea, type));

    public ActionResult Demolish() => Execute(() => Game.Demolish(ActiveArea));

    public ActionResult PlaceBuilding(BuildingType type) => Execute(() => Game.PlaceBuilding(type, ActiveArea));

    private ActionResult Execute(Func<ActionResult> action)
    {
        string snapshot = SaveGameStore.Serialize(Game);
        double at = Game.ElapsedDays;
        var result = action();
        if (result.Success)
        {
            _undoSnapshot = snapshot;
            _undoAt = at;
        }

        return Complete(result);
    }

    private ActionResult Complete(ActionResult result)
    {
        if (result.Success)
        {
            Anchor = null;
            Selection = null;
        }

        SetMessage(result.Message, result.Success ? MessageKind.Success : MessageKind.Error);
        return result;
    }

    public void TogglePause()
    {
        Game.Paused = !Game.Paused;
        SetMessage(Game.Paused ? "Paused." : "Resumed.");
        Game.Touch();
    }

    public void SetSpeed(GameSpeed speed)
    {
        Game.Speed = speed;
        Game.Paused = false;
        SetMessage($"Speed: {speed}.");
        Game.Touch();
    }

    // ---- Games ------------------------------------------------------------------------------------------------

    public bool QuickSave()
    {
        try
        {
            SaveGameStore.Save(Game, SavePath);
            _savedRevision = _revision;
            _savedAt = Game.ElapsedDays;
            SetMessage($"Game saved to {SavePath}", MessageKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage("Save failed: " + ex.Message, MessageKind.Error);
            return false;
        }
    }

    public bool QuickLoad() => LoadFrom(SavePath);

    public bool LoadFrom(string path)
    {
        try
        {
            var loaded = SaveGameStore.Load(path, Game.Map.Content);
            ReplaceGame(loaded);
            Game.Paused = true;
            _savedRevision = _revision;
            _savedAt = Game.ElapsedDays;
            SetMessage($"Loaded {path}. The game is paused; press P to resume.", MessageKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SetMessage("Load failed: " + ex.Message, MessageKind.Error);
            return false;
        }
    }

    public void NewGame(GameConfig config)
    {
        ReplaceGame(CityGame.New(config));
        Game.Paused = true;
        GuideVisible = true;
        ShowGuide();
    }

    private void ReplaceGame(CityGame game)
    {
        CancelPreview();
        Prompt = null;
        _undoSnapshot = null;
        Game.Changed -= OnGameChanged;
        Game = game;
        Game.Changed += OnGameChanged;
        InitializeFeedback();
        GuideVisible = !game.GuideDismissed;
        _autosaveElapsed = 0;
        Anchor = null;
        Selection = null;
        Cursor = new Pos(Math.Min(Cursor.X, game.Map.Width - 1), Math.Min(Cursor.Y, game.Map.Height - 1));
        ClampCamera();
        FollowCursor();
        Game.Touch();
    }
}
