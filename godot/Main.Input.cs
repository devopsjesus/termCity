using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class Main
{
    private double _pinchZoom;

    private readonly EdgeScroller _scroller = new();

    private const int ScrollStep = 3;

    private double _trackpadX, _trackpadY, _trackpadZoom;

    private int _trackpadZoomLevel = int.MinValue;

    private Vector2 _pointer;

    private bool _hover;

    private string _lastInput = "";

    private bool _selecting;

    private bool _panning;

    private Pos _panAnchor;

    public override void _Input(InputEvent input)
    {
        if (!_started) return;
        if (input is InputEventKey dialogKey && Session.Prompt is not null && HandleDialogKey(dialogKey))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMagnifyGesture magnify)
        {
            if (Session.Prompt is null && !_editingName)
            {
                var pointer = magnify.Position - Map.GlobalPosition;
                if (!new Rect2(Vector2.Zero, Map.Size).HasPoint(pointer)) pointer = Map.Size / 2;
                ApplyPinchZoom(magnify.Factor, pointer);
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_editingName)
        {
            if (input is InputEventKey { Pressed: true } nameKey)
            {
                if (nameKey.Keycode == Key.A && (nameKey.CtrlPressed || nameKey.MetaPressed))
                {
                    _nameEditor.SelectAll();
                    GetViewport().SetInputAsHandled();
                }
                else if (nameKey.Keycode is Key.Enter or Key.KpEnter or Key.Escape)
                {
                    FinishRename(nameKey.Keycode != Key.Escape);
                    GetViewport().SetInputAsHandled();
                }
                else if (OS.HasFeature("macos") && nameKey.Keycode == Key.Delete)
                {
                    int end = _nameEditor.HasSelection() ? _nameEditor.GetSelectionToColumn() : _nameEditor.CaretColumn;
                    int start = _nameEditor.HasSelection() ? _nameEditor.GetSelectionFromColumn() : Math.Max(0, end - 1);
                    _nameEditor.DeleteText(start, end);
                    _nameEditor.CaretColumn = start;
                    GetViewport().SetInputAsHandled();
                }
                return;
            }
            if (input is InputEventMouseButton { Pressed: true } mouse &&
                !_nameEditor.GetGlobalRect().HasPoint(mouse.Position) && !FinishRename(true))
            {
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (input is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left or MouseButton.Middle })
        {
            StopPointerGesture();
        }
        if (input is InputEventKey { Pressed: true } && Session.Prompt is null)
        {
            if (Session.Preview is not null && input is InputEventKey previewKey)
            {
                var focus = GetViewport().GuiGetFocusOwner();
                bool confirmationFocused = focus is Button &&
                    (_modal.IsAncestorOf(focus) || _linePreview.IsAncestorOf(focus));
                if (confirmationFocused && previewKey.Keycode is Key.Enter or Key.KpEnter ||
                    !Session.RoadToolActive && !Session.BuildingToolActive &&
                    previewKey.Keycode is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
                {
                    return;
                }
            }
            _UnhandledInput(input);
        }
        else if (input is InputEventKey { Pressed: true, Keycode: Key.Escape } && Session.Prompt is not null)
        {
            _helpVisible = false;
            Session.CancelPrompt();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!_started || _editingName || input is not InputEventKey { Pressed: true } key)
        {
            return;
        }
        if (HandleKey(key))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private bool HandleKey(InputEventKey key)
    {
        var code = key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode;
        _hover = false;
        ResetTrackpadScroll();
        _lastInput = $"Key {code}";
        if (Session.Prompt is not null)
        {
            HandleDialogKey(key);
            return true;
        }
        if (Session.Preview is not null)
        {
            if (Session.BuildingToolActive && code is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int buildingDx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
                int buildingDy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
                if (key.CtrlPressed) Session.JumpCursor(buildingDx, buildingDy);
                else Session.MoveCursor(buildingDx, buildingDy);
            }
            else if (!key.CtrlPressed && !key.MetaPressed && !key.AltPressed && code is Key.Enter or Key.Y or Key.C)
            {
                Session.ConfirmPreview();
            }
            else if (code is Key.Escape or Key.N or Key.A)
            {
                Session.CancelPreview();
            }
            else if (Session.RoadToolActive && code is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int lineDx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
                int lineDy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
                if (key.CtrlPressed) Session.JumpCursor(lineDx, lineDy);
                else Session.MoveCursor(lineDx, lineDy);
            }
            return true;
        }
        if ((key.CtrlPressed || key.MetaPressed) && code == Key.Z)
        {
            Session.RequestUndo();
            return true;
        }
        int dx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
        int dy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
        if (code is Key.Pageup or Key.Pagedown or Key.Home or Key.End)
        {
            Session.JumpCursor(code == Key.Home ? -1 : code == Key.End ? 1 : 0,
                code == Key.Pageup ? -1 : code == Key.Pagedown ? 1 : 0, key.ShiftPressed);
            return true;
        }
        if (dx != 0 || dy != 0)
        {
            if (key.CtrlPressed)
            {
                Session.JumpCursor(dx, dy, key.ShiftPressed);
            }
            else
            {
                Session.MoveCursor(dx, dy, key.ShiftPressed);
            }
            return true;
        }
        if (key.Echo)
        {
            return true;
        }
        if (key.Unicode is '+' or '=' || code is Key.Plus or Key.Equal or Key.KpAdd)
        {
            Map.ZoomBy(1);
            return true;
        }
        if (key.Unicode is '-' or '_' || code is Key.Minus or Key.KpSubtract)
        {
            Map.ZoomBy(-1);
            return true;
        }
        if (code == Key.Key0 || key.Unicode == '0')
        {
            Map.ZoomBy(-Session.ZoomLevel);
            return true;
        }
        if (key.CtrlPressed || key.MetaPressed || key.AltPressed)
        {
            if (code == Key.Q && (key.CtrlPressed || key.MetaPressed)) Session.RequestQuit();
            else return false;
            return true;
        }
        if (key.Unicode == '?')
        {
            ShowHelp();
            return true;
        }
        switch (code)
        {
            case Key.P or Key.Space: Session.TogglePause(); break;
            case Key.Key1: Session.SetSpeed(GameSpeed.Slow); break;
            case Key.Key2: Session.SetSpeed(GameSpeed.Medium); break;
            case Key.Key3: Session.SetSpeed(GameSpeed.Fast); break;
            case Key.R: Session.Zone(ZoneType.Residential); break;
            case Key.C: Session.Zone(ZoneType.Commercial); break;
            case Key.I: Session.Zone(ZoneType.Industrial); break;
            case Key.B: Session.PreviewRoad(); break;
            case Key.T: Session.BeginRoadLine(); break;
            case Key.U: Session.Dezone(); break;
            case Key.D or Key.Delete: Session.PreviewDemolish(); break;
            case Key.O: Session.CycleOverlay(); break;
            case Key.V: CycleEffects(); break;
            case Key.S: Session.ToggleSelectionMode(); break;
            case Key.E: Session.ToggleEdgeScroll(); break;
            case Key.Enter or Key.M: Session.ShowAreaMenu(); break;
            case Key.F1 or Key.Question: ShowHelp(); break;
            case Key.F3: ShowFontDialog(); break;
            case Key.F5: Session.QuickSave(); break;
            case Key.F6: Session.ShowGuide(); break;
            case Key.F7: Session.ShowReport(); break;
            case Key.F8: Session.ShowGrowthReport(); break;
            case Key.F9: Session.RequestLoad(Session.SavePath); break;
            case Key.F10: if (!key.Echo) ToggleMusic(); break;
            case Key.F12: Session.ToggleInputDebug(); break;
            case Key.Escape: ShowCityMenu(); break;
            case Key.Q: Session.RequestQuit(); break;
            default: return false;
        }
        return true;
    }

    private void ApplyPinchZoom(float factor, Vector2 pointer)
    {
        if (_editingName || Session.Prompt is not null || Session.Preview is not null) return;
        if (!float.IsFinite(factor) || factor <= 0)
        {
            GD.PushWarning("Ignored invalid pinch magnification factor.");
            return;
        }
        if (_trackpadZoomLevel != Session.ZoomLevel) ResetTrackpadScroll();
        _pinchZoom += Math.Log(factor) / Math.Log(1.2);
        int steps = (int)Math.Truncate(_pinchZoom);
        _pinchZoom -= steps;
        if (steps != 0)
        {
            Map.ZoomBy(steps, pointer);
            _trackpadZoomLevel = Session.ZoomLevel;
        }
        _lastInput = $"Pinch {factor:F3}";
    }

    private void OnMapInput(InputEvent input)
    {
        Map.RefreshCells();
        if (_editingName || Session.Prompt is not null ||
            (Session.Preview is not null && !Session.RoadToolActive && !Session.BuildingToolActive))
        {
            StopPointerGesture();
            return;
        }
        if (input is InputEventMagnifyGesture magnify)
        {
            ApplyPinchZoom(magnify.Factor, magnify.Position);
            Map.AcceptEvent();
        }
        else if (input is InputEventPanGesture pan)
        {
            if (!Map.Grid.TryCell(pan.Position.X, pan.Position.Y, out var cell))
            {
                return;
            }
            if (_trackpadZoomLevel != Session.ZoomLevel)
            {
                ResetTrackpadScroll();
            }
            if (pan.CtrlPressed || pan.MetaPressed)
            {
                _trackpadX = _trackpadY = 0;
                _trackpadZoom -= pan.Delta.Y;
                int zoom = (int)Math.Truncate(_trackpadZoom);
                _trackpadZoom -= zoom;
                if (zoom != 0)
                {
                    Map.ZoomBy(zoom, pan.Position);
                    _trackpadZoomLevel = Session.ZoomLevel;
                }
            }
            else
            {
                _trackpadZoom = 0;
                bool horizontal = pan.ShiftPressed || pan.AltPressed;
                _trackpadX += (pan.Delta.X + (horizontal ? pan.Delta.Y : 0)) * ScrollStep;
                _trackpadY += (horizontal ? 0 : pan.Delta.Y) * ScrollStep;
                int columns = (int)Math.Truncate(_trackpadX);
                int rows = (int)Math.Truncate(_trackpadY);
                _trackpadX -= columns;
                _trackpadY -= rows;
                Session.ScrollCamera(columns * Session.Stride, rows * Session.Stride);
            }
            _lastInput = $"Trackpad {pan.Delta} {cell}";
            Map.AcceptEvent();
        }
        else if (input is InputEventMouseButton button)
        {
            if (!button.Pressed)
            {
                if (button.ButtonIndex is MouseButton.Left or MouseButton.Middle)
                {
                    StopPointerGesture();
                }
                return;
            }
            if (!Map.Grid.TryCell(button.Position.X, button.Position.Y, out var cell))
            {
                return;
            }
            var position = Session.ScreenToMap(cell.X, cell.Y);
            if (!Session.Game.Map.InBounds(position) &&
                button.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right)
            {
                return;
            }
            switch (button.ButtonIndex)
            {
                case MouseButton.Left:
                    if (Session.BuildingToolActive)
                    {
                        Session.MoveBuildingPlacement(position);
                        Session.ConfirmPreview();
                        break;
                    }
                    _pointer = button.Position;
                    _selecting = Session.RoadToolActive || button.ShiftPressed || button.CtrlPressed || button.AltPressed || button.MetaPressed;
                    _panning = !_selecting;
                    if (_selecting) Session.BeginDrag(position, extend: button.ShiftPressed && !button.CtrlPressed && !button.AltPressed && !button.MetaPressed);
                    else { Session.SelectCell(position); _panAnchor = position; }
                    break;
                case MouseButton.Middle:
                    _panning = true;
                    _panAnchor = position;
                    break;
                case MouseButton.Right:
                    Session.PrepareContextMenuAt(position);
                    Session.ShowAreaMenu();
                    break;
                case MouseButton.WheelUp:
                case MouseButton.WheelDown:
                    int direction = button.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
                    if (button.CtrlPressed || button.MetaPressed)
                    {
                        Map.ZoomBy(-direction, button.Position);
                    }
                    else
                    {
                        bool horizontal = button.ShiftPressed || button.AltPressed;
                        Session.ScrollChars(horizontal ? direction * ScrollStep : 0, horizontal ? 0 : direction * ScrollStep);
                    }
                    break;
                case MouseButton.WheelLeft: Session.ScrollChars(-ScrollStep, 0); break;
                case MouseButton.WheelRight: Session.ScrollChars(ScrollStep, 0); break;
            }
            _lastInput = $"Mouse {button.ButtonIndex} {cell}";
            Map.AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion)
        {
            _pointer = motion.Position;
            _hover = new Rect2(Vector2.Zero, Map.Size).HasPoint(motion.Position);
            var cell = PointerCell(motion.Position);
            if (Session.BuildingToolActive)
            {
                Session.MoveBuildingPlacement(Session.ScreenToMap(cell.X, cell.Y));
            }
            else if (_selecting)
            {
                Session.UpdateDrag(Session.ScreenToMap(cell.X, cell.Y));
            }
            else if (_panning)
            {
                Session.PanCamera(_panAnchor, cell.X, cell.Y);
            }
            Map.AcceptEvent();
        }
    }

    private void StopPointerGesture()
    {
        bool selecting = _selecting;
        _selecting = false;
        _panning = false;
        _scroller.Reset();
        ResetTrackpadScroll();
        if (selecting)
        {
            Session.EndSelection();
        }
    }

    private void ResetTrackpadScroll()
    {
        _trackpadX = _trackpadY = _trackpadZoom = 0;
        _pinchZoom = 0;
        _trackpadZoomLevel = Session.ZoomLevel;
    }

    private Pos PointerCell(Vector2 pointer) => new(
        Math.Clamp((int)(pointer.X / Map.Grid.PixelWidth), 0, Map.Grid.Columns - 1),
        Math.Clamp((int)(pointer.Y / Map.Grid.PixelHeight), 0, Map.Grid.Rows - 1));

    private void EdgeScroll(double delta)
    {
        Session.RecordLoopGap((long)(delta * 1000));
        if (Session.Prompt is not null || Session.Preview is not null && !Session.RoadToolActive ||
            _panning || !_selecting && (!Session.EdgeScrollEnabled || !_hover))
        {
            _scroller.Reset();
            return;
        }
        var cell = PointerCell(_pointer);
        var (dx, dy) = _scroller.Step(cell.X, cell.Y, Map.Grid.Columns, Map.Grid.Rows, Math.Min(delta, 0.25));
        if (dx == 0 && dy == 0) return;
        Session.ScrollChars(dx, dy);
        if (_selecting) Session.UpdateDrag(Session.ScreenToMap(cell.X, cell.Y));
    }
}
