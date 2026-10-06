using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TermCity.Core.Util;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermCity.App.Views;

/// <summary>Base for simple custom-drawn text panels.</summary>
internal abstract class PanelView : View
{
    /// <summary>Fills the whole viewport with the panel background.</summary>
    protected void ClearPanel(Rgb background)
    {
        SetAttribute(Colors.Attr(Colors.PanelText, background));
        string blank = new(' ', Math.Max(0, Viewport.Width));
        for (int y = 0; y < Viewport.Height; y++)
        {
            AddStr(0, y, blank);
        }
    }

    protected void DrawText(int x, int y, string text, Rgb foreground, Rgb background)
    {
        if (y < 0 || y >= Viewport.Height || x >= Viewport.Width || text.Length == 0)
        {
            return;
        }

        if (x + text.Length > Viewport.Width)
        {
            text = text[..Math.Max(0, Viewport.Width - x)];
        }

        SetAttribute(Colors.Attr(foreground, background));
        AddStr(x, y, text);
    }
}
