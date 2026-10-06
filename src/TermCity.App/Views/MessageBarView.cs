using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.App.Views;

/// <summary>
/// Bottom line. On the left: the latest message for a few seconds after something happens, otherwise details of the
/// selection or the cell under the cursor. On the right: key hints, if there is room.
/// </summary>
internal sealed class MessageBarView : PanelView
{
    private static readonly Rgb Background = Rgb.Hex(0x1d2330);
    private const string Hints = "F1 Help  F10 City  Enter Menu  Ctrl+Q Quit";

    private readonly GameSession _session;

    public MessageBarView(GameSession session) => _session = session;

    /// <summary>What the left side of the line currently says, and in what color.</summary>
    internal (string Text, Rgb Color) Status()
    {
        if (_session.Preview is { } preview)
        {
            if (_session.MessageKind == MessageKind.Error && _session.MessageVisible)
            {
                return (_session.Message + " | " + preview.Summary, Colors.Bad);
            }

            return (preview.Summary, preview.Quote.Cost > _session.Game.Money || preview.Quote.Cells == 0 ? Colors.Bad : Colors.Accent);
        }

        if (_session.MessageVisible)
        {
            Rgb color = _session.MessageKind switch
            {
                MessageKind.Success => Colors.Good,
                MessageKind.Error => Colors.Bad,
                _ => Colors.PanelText,
            };
            return (_session.Message, color);
        }

        if (_session.Selection is { Area: > 1 } selection)
        {
            return ($"Selection {selection.Width}x{selection.Height} · {selection.Area} cells", Colors.Accent);
        }

        var game = _session.Game;
        var cursor = _session.Cursor;
        var zone = game.Map.ZoneAt(cursor.X, cursor.Y);
        if (zone != ZoneType.None && game.Map.BuildingAt(cursor.X, cursor.Y) is null)
        {
            var diagnostic = GrowthDiagnostics.ForCell(game, cursor.X, cursor.Y);
            string paused = diagnostic.Paused ? " | Paused (P)" : "";
            return ($"{Zones.Get(zone).Letter} ({cursor.X},{cursor.Y}): {diagnostic.Message}{paused} | F8 details", Colors.PanelText);
        }

        if (_session.GuideVisible)
        {
            return (_session.GuideText, Colors.Accent);
        }

        return (CellInspector.Summary(_session.Game, _session.Cursor), Colors.PanelText);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        ClearPanel(Background);
        int width = Viewport.Width;

        var (status, color) = Status();
        string text = " " + status;
        DrawText(0, 0, text, color, Background);

        // With input debugging on, the right side reports how well the application loop is keeping up instead.
        string right = _session.InputDebug
            ? $"loop gap {_session.LoopGapMs} ms, worst {_session.LoopWorstGapMs} ms"
            : Hints;
        if (width >= text.Length + right.Length + 3)
        {
            DrawText(width - right.Length - 1, 0, right, _session.InputDebug ? Colors.Heading : Colors.PanelDim, Background);
        }

        return true;
    }

}
