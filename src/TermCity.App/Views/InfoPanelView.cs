using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.App.Views;

/// <summary>
/// Side panel with demand bars, city statistics and zone counts. Each section has a header that can be clicked
/// (or toggled with F2-F4) to collapse it: a downward caret when expanded, a right-pointing one when collapsed.
/// </summary>
internal sealed class InfoPanelView : PanelView
{
    internal const string Expanded = "▼";
    internal const string Collapsed = "►";

    private readonly GameSession _session;
    private readonly List<(int Row, PanelSection Section)> _headers = [];

    public InfoPanelView(GameSession session) => _session = session;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var bg = Colors.PanelBackground;
        ClearPanel(bg);
        _headers.Clear();

        var game = _session.Game;
        var stats = game.Stats;
        int y = 0;

        if (Header(ref y, PanelSection.Demand, "DEMAND"))
        {
            var demand = game.Demand;
            foreach (var zone in Zones.Placeable)
            {
                DemandBar(y++, Zones.Get(zone), demand.For(zone));
            }
        }

        y++;
        if (Header(ref y, PanelSection.City, "CITY"))
        {
            DrawText(1, y++, $"Population  {stats.Population:N0}", Colors.PanelText, bg);
            DrawText(1, y++, $" Adults {stats.Adults}  Kids {stats.Children}", Colors.PanelDim, bg);
            DrawText(1, y++, $" Seniors {stats.Seniors}  Homes {stats.Households}", Colors.PanelDim, bg);
            DrawText(1, y++, $"Tax income  {Fmt.Money(stats.WeeklyIncome)}/wk", Colors.Good, bg);
            DrawText(1, y++, $"Tax  R {game.Taxes.Residential:P0}  C {game.Taxes.Commercial:P0}  I {game.Taxes.Industrial:P0}", Colors.PanelText, bg);
        }

        y++;
        if (Header(ref y, PanelSection.Zones, "ZONES (filled/zoned)"))
        {
            foreach (var zone in Zones.Placeable)
            {
                var info = Zones.Get(zone);
                var count = stats.For(zone);
                string note = count.Zoned > count.Served ? $" ({count.Zoned - count.Served} no road)" : string.Empty;
                if (count.AwaitingRemoval > 0)
                {
                    note += $" +{count.AwaitingRemoval} leaving";
                }
                DrawText(1, y, $"{info.Letter}", info.Foreground, bg);
                DrawText(3, y++, $"{count.Filled}/{count.Zoned}{note}", Colors.PanelText, bg);
            }

        }

        return true;
    }

    /// <summary>Draws a section header and reports whether its body should be drawn.</summary>
    private bool Header(ref int y, PanelSection section, string title)
    {
        bool open = !_session.IsCollapsed(section);
        DrawText(1, y, open ? Expanded : Collapsed, Colors.Accent, Colors.PanelBackground);
        DrawText(3, y, title, Colors.Heading, Colors.PanelBackground);
        _headers.Add((y, section));
        y++;
        return open;
    }

    private void DemandBar(int y, ZoneInfo info, double value)
    {
        var bg = Colors.PanelBackground;
        int width = Math.Max(4, Viewport.Width - 6);
        int filled = (int)Math.Round(Math.Clamp(value, 0, 1) * width);

        DrawText(1, y, info.Letter.ToString(), info.Foreground, bg);
        DrawText(3, y, new string('█', filled), info.Foreground, bg);
        DrawText(3 + filled, y, new string('░', width - filled), Colors.BarBackground, bg);
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            int row = (mouse.Position ?? Point.Empty).Y;
            foreach (var (headerRow, section) in _headers)
            {
                if (headerRow == row)
                {
                    _session.ToggleSection(section);
                    break;
                }
            }

            return true;
        }

        return base.OnMouseEvent(mouse);
    }
}
