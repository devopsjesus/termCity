using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.App.Views;

/// <summary>Top bar: money, date, population and clock state.</summary>
internal sealed class HudView : PanelView
{
    private static readonly Rgb Background = Rgb.Hex(0x1d2330);

    private readonly GameSession _session;

    public HudView(GameSession session) => _session = session;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var game = _session.Game;
        ClearPanel(Background);

        int x = 1;
        void Segment(string text, Rgb color)
        {
            DrawText(x, 0, text, color, Background);
            x += text.Length;
        }

        Segment(" TermCity ", Colors.Heading);
        Segment("  ", Colors.PanelDim);
        Segment(Fmt.Money(game.Money), game.Money > 0 ? Colors.Good : Colors.Bad);
        Segment("  |  ", Colors.PanelDim);
        Segment($"Year {game.Year}, Week {game.WeekOfYear:00} ", Colors.PanelText);
        Segment(WeekBar(game.Day, game.Config.DaysPerWeek), Colors.Accent);
        Segment("  |  ", Colors.PanelDim);
        Segment($"Pop {game.Stats.Population:N0}", Colors.PanelText);
        Segment("  |  ", Colors.PanelDim);

        string clock = game.Paused ? "|| PAUSED" : game.Speed switch
        {
            GameSpeed.Slow => "> Slow",
            GameSpeed.Medium => ">> Medium",
            _ => ">>> Fast",
        };
        Segment(clock, game.Paused ? Colors.Heading : Colors.Accent);

        if (game.Money <= 0)
        {
            Segment("  OUT OF MONEY", Colors.Bad);
        }

        return true;
    }

    /// <summary>One character per day of the week: days gone by are filled, today is marked with an arrow.</summary>
    internal static string WeekBar(int day, int daysPerWeek)
    {
        day = Math.Clamp(day, 0, daysPerWeek - 1);
        return "[" + new string('=', day) + ">" + new string('.', daysPerWeek - day - 1) + "]";
    }
}