namespace TermCity.Core.Session;

/// <summary>The short controls reference behind F1. How to play lives in the guide.</summary>
public static class HelpContent
{
    public const string Title = "TermCity help";

    public const string Footer = "How to play, and what makes the town grow, is in the GUIDE (F6).";

    private static readonly TableColumn[] Columns = ["KEY", "ACTION"];

    private static readonly (string Section, string[][] Rows)[] Sections =
    [
        ("VIEW",
        [
            ["Arrows", "Move the cursor"],
            ["Ctrl+arrows, Home, End, PgUp, PgDn", "Jump a screen"],
            ["Drag, wheel, trackpad, minimap", "Pan the map"],
            ["+  -  0, pinch, Ctrl+wheel", "Zoom in, out, back to normal"],
        ]),
        ("SELECT",
        [
            ["Drag", "Select a box of cells"],
            ["Shift+click", "Grow the selection to take in the clicked cell"],
            ["Ctrl or Alt+drag", "Start a new selection"],
            ["Shift+arrows, or S arrows S", "Select with the keyboard"],
            ["Enter, M, right-click", "Area menu: zone, roads, services, demolish"],
            ["Esc", "Deselect, or open the city menu"],
        ]),
        ("BUILD",
        [
            ["R  C  I  U", "Zone homesteads, marketplace, craftworks; dezone"],
            ["T  B", "Straight road tool, track preview"],
            ["D, Delete", "Demolish, free of charge"],
            ["Enter or Y, Esc or N", "Confirm or cancel a placement"],
            ["Ctrl+Z", "Undo the last action"],
        ]),
        ("GAME",
        [
            ["Space, P  1  2  3", "Pause or resume; slow, medium, fast clock"],
            ["F5  F9  F6", "Quick-save, quick-load, guide"],
            ["F7  F8", "Weekly report, growth and road access"],
            ["O  V  E", "Map overlays, effects, edge scrolling"],
            ["F3, Esc then Resize sidebar", "Font size, sidebar width"],
        ]),
    ];

    public static string Text()
    {
        var all = Sections.SelectMany(s => s.Rows).Select(r => (IReadOnlyList<string>)r).ToArray();
        var widths = TextTable.Widths(Columns, all);
        var lines = new List<string>
        {
            TextTable.Row(Columns, widths, Columns.Select(c => c.Header).ToArray()),
            TextTable.Rule(widths),
        };
        foreach (var (section, rows) in Sections)
        {
            lines.Add(string.Empty);
            lines.Add(section);
            lines.AddRange(rows.Select(row => TextTable.Row(Columns, widths, row)));
        }

        return string.Join("\n", lines);
    }
}
