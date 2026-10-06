using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Util;

namespace TermCity.App.Views;

/// <summary>Full-window help overlay. Hidden until toggled; any key or click dismisses it.</summary>
internal sealed class HelpView : PanelView
{
    private readonly string[] _lines;

    public HelpView()
    {
        string control = PlatformKeys.Control;
        string alt = PlatformKeys.Alt;
        string enter = PlatformKeys.Enter;
        string escape = PlatformKeys.Escape;
        string delete = PlatformKeys.Delete;
        _lines =
        [
        "TERMCITY - HELP",
        "",
        $"Move cursor   Arrows ({PlatformKeys.FullScreenJump}: full screen), or click",
        $"Pan map       Left-drag; wheel; {alt}+wheel sideways; minimap; E edges",
        $"Zoom          + / - or {control}+wheel or [-] [+]; 0 normal",
        $"Select        Shift+click/drag ({control}/{alt} work too)",
        "              Shift+Arrows, or S, arrows, S",
        $"Area menu     Right-click, or {enter} / M",
        "Zone          R homes / C shops / I factories; U dezone",
        "Road          B Street preview ($500/cell); T straight-line tool",
        $"Demolish      D / {delete} preview (free; no refund)",
        $"Preview       Nearby Yes/No popup; {enter} selects; {escape} cancels",
        $"Undo          {control}+Z; full-city rollback, with confirmation",
        "Clock         Space/P pause; 1 slow, 2 medium, 3 fast",
        "Side panel    Click heading or F2/F3/F4 to fold",
        $"Game          F10 city menu; F5 save; F9 load; {control}+Q quit",
        "Reports       F7 weekly report/milestones; F8 growth explanations",
        "Guide         F6 shows/dismisses first-city guide",
        "Diagnostics   F12: mouse/key events and loop stalls",
        "",
        "Dezoned buildings leave in 2-3 weeks; restore their zone to keep them.",
        "Zones are free; connect roads for growth. Press any key to close.",
        ];
        Visible = false;
        CanFocus = false;
    }

    public void Toggle()
    {
        Visible = !Visible;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var background = Rgb.Hex(0x0e1016);
        ClearPanel(background);

        int width = Math.Min(Viewport.Width, _lines.Max(l => l.Length) + 4);
        int height = Math.Min(Viewport.Height, _lines.Length + 2);
        int left = (Viewport.Width - width) / 2;
        int top = (Viewport.Height - height) / 2;

        for (int row = 0; row < height; row++)
        {
            string text = row == 0 || row == height - 1 ? string.Empty : "  " + _lines[row - 1];
            DrawText(left, top + row, text.PadRight(width), row == 1 ? Colors.Heading : Colors.PanelText, Colors.PanelBackground);
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) || mouse.Flags.HasFlag(MouseFlags.RightButtonPressed))
        {
            Visible = false;
        }

        return true;
    }
}
