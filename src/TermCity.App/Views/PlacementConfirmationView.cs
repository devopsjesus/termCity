using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using Pos = Terminal.Gui.ViewBase.Pos;

namespace TermCity.App.Views;

internal sealed class PlacementConfirmationView : PanelView
{
    private readonly GameSession _session;
    private readonly MapView _map;
    private PlacementPreview? _preview;
    private bool _yes = true;

    public PlacementConfirmationView(GameSession session, MapView map)
    {
        _session = session;
        _map = map;
        Visible = false;
        CanFocus = true;
        X = Pos.Func(_ => Bounds().X);
        Y = Pos.Func(_ => Bounds().Y);
        Width = Dim.Func(_ => Math.Min(42, _map.Viewport.Width));
        Height = 7;
    }

    private Point Bounds()
    {
        var area = _session.Preview?.Area ?? _session.ActiveArea;
        int cellX = Math.Clamp(_session.Cursor.X, area.X, area.Right);
        int cellY = Math.Clamp(_session.Cursor.Y, area.Y, area.Bottom);
        int x = Math.Clamp((cellX - _session.CameraX) * _session.SpanX / _session.Stride, 0, Math.Max(0, _map.Viewport.Width - 1));
        int y = Math.Clamp((cellY - _session.CameraY) / _session.Stride, 0, Math.Max(0, _map.Viewport.Height - 1));
        int width = Math.Min(42, _map.Viewport.Width);
        int left, top;
        if (x + _session.SpanX + width + 1 <= _map.Viewport.Width)
        {
            left = x + _session.SpanX + 1;
            top = y - 2;
        }
        else if (x - width - 1 >= 0)
        {
            left = x - width - 1;
            top = y - 2;
        }
        else
        {
            left = x - width / 2;
            top = y + 8 <= _map.Viewport.Height ? y + 1 : y - 7;
        }

        return new Point(_map.Frame.X + Math.Clamp(left, 0, Math.Max(0, _map.Viewport.Width - width)),
            _map.Frame.Y + Math.Clamp(top, 0, Math.Max(0, _map.Viewport.Height - 7)));
    }

    public void Synchronize()
    {
        bool wasVisible = Visible;
        var preview = _session.Preview;
        if (!ReferenceEquals(preview, _preview))
        {
            _preview = preview;
            _yes = true;
        }

        Visible = preview is not null && _session.Prompt is null;
        if (Visible && !_session.RoadToolActive)
        {
            _map.StopDrag();
            SetFocus();
        }
        else if (wasVisible && !Visible && _session.Prompt is null)
        {
            _map.SetFocus();
        }

        SuperView?.SetNeedsLayout();
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_session.Preview is not { } preview)
        {
            return true;
        }

        var bg = Colors.PanelBackground;
        ClearPanel(bg);
        int width = Viewport.Width;
        string edge = "+" + new string('-', Math.Max(0, width - 2)) + "+";
        DrawText(0, 0, edge, Colors.Accent, bg);
        DrawText(0, 6, edge, Colors.Accent, bg);
        for (int row = 1; row < 6; row++)
        {
            DrawText(0, row, "|", Colors.Accent, bg);
            DrawText(width - 1, row, "|", Colors.Accent, bg);
        }

        string title = preview.Kind == PlacementKind.Demolish ? "Demolish here?" : $"Build {preview.Name}?";
        DrawLine(1, title, Colors.Heading);
        DrawLine(2, $"{preview.Quote.Cells} cell(s), {preview.Quote.Skipped} skipped", Colors.PanelText);
        DrawLine(3, "Cost: " + Fmt.Money(preview.Quote.Cost),
            preview.Quote.Cost > _session.Game.Money || preview.Quote.Cells == 0 ? Colors.Bad : Colors.PanelText);
        DrawText(3, 4, "[Yes]", _yes ? Colors.Good : Colors.PanelDim, bg);
        DrawText(11, 4, "[No]", !_yes ? Colors.Accent : Colors.PanelDim, bg);
        DrawLine(5, "Enter selects | Esc = No", Colors.PanelDim);
        return true;
    }

    private void DrawLine(int row, string text, Rgb color)
    {
        int available = Math.Max(0, Viewport.Width - 4);
        DrawText(2, row, text[..Math.Min(text.Length, available)], color, Colors.PanelBackground);
    }

    private void Select(bool yes)
    {
        if (yes) _session.ConfirmPreview();
        else _session.CancelPreview();
    }

    protected override bool OnKeyDown(Key key)
    {
        switch (key.KeyCode & ~KeyCode.ShiftMask)
        {
            case KeyCode.Enter: Select(_yes); break;
            case KeyCode.Y: Select(true); break;
            case KeyCode.N:
            case KeyCode.Esc: Select(false); break;
            case KeyCode.Tab:
            case KeyCode.CursorLeft:
            case KeyCode.CursorRight:
            case KeyCode.CursorUp:
            case KeyCode.CursorDown:
                _yes = !_yes;
                SetNeedsDraw();
                break;
            case KeyCode.F5: _session.QuickSave(); break;
            case KeyCode.F9: _session.RequestLoad(_session.SavePath); break;
            case KeyCode.F10: _session.ShowSessionMenu(); break;
            default:
                if (key == Key.Z.WithCtrl) _session.RequestUndo();
                else if (key == Key.Q.WithCtrl) _session.RequestQuit();
                break;
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if ((mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) || mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)) &&
            mouse.Position is { Y: 4 } p)
        {
            if (p.X is >= 3 and < 8) Select(true);
            else if (p.X is >= 11 and < 15) Select(false);
        }

        return true;
    }
}
