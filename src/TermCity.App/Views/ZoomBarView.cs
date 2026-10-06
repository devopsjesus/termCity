using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.App.Views;

/// <summary>The row under the minimap: the zoom level, with clickable [-] and [+] buttons.</summary>
internal sealed class ZoomBarView : PanelView
{
    // Column layout, kept in one place so drawing and hit-testing agree.
    internal const int TitleX = 1;
    internal const int MinusX = 8;
    internal const int LabelX = 12;
    internal const int LabelWidth = 7;
    internal const int PlusX = 20;
    internal const int ButtonWidth = 3;

    private readonly GameSession _session;

    public ZoomBarView(GameSession session)
    {
        _session = session;
        CanFocus = false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var bg = Colors.PanelBackground;
        ClearPanel(bg);

        bool canOut = _session.ZoomLevel > GameSession.MinZoom;
        bool canIn = _session.ZoomLevel < GameSession.MaxZoom;
        string label = _session.ZoomLabel;
        int pad = Math.Max(0, (LabelWidth - label.Length) / 2);

        DrawText(TitleX, 0, "ZOOM", Colors.Heading, bg);
        DrawText(MinusX, 0, "[-]", canOut ? Colors.Accent : Colors.PanelDim, bg);
        DrawText(LabelX + pad, 0, label, Colors.PanelText, bg);
        DrawText(PlusX, 0, "[+]", canIn ? Colors.Accent : Colors.PanelDim, bg);
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            int x = (mouse.Position ?? Point.Empty).X;
            if (x >= MinusX && x < MinusX + ButtonWidth)
            {
                _session.ZoomBy(-1);
            }
            else if (x >= PlusX && x < PlusX + ButtonWidth)
            {
                _session.ZoomBy(1);
            }

            return true;
        }

        return base.OnMouseEvent(mouse);
    }
}
