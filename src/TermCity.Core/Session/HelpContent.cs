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
            ["Shift+click", "Resize selection from its original anchor"],
            ["Ctrl or Alt+drag", "Start a new selection"],
            ["Shift+arrows, or S arrows S", "Select with the keyboard"],
            ["Enter, M, right-click", "Area menu: zone, roads, services, demolish"],
            ["Esc", "Game settings, or close a dialog / cancel placement"],
        ]),
        ("BUILD",
        [
            ["Left icon sidebar", "Road / service catalogs; Add starts placement"],
            ["R  C  I  U", "Zone homesteads, marketplace, craftworks; dezone"],
            ["T  B", "Straight road tool, track preview"],
            ["D, Delete", "Demolish, free of charge"],
            ["[ / ]", "Rotate placing building left/right; otherwise the map"],
            ["Enter or Y, Esc or N", "Confirm or cancel a placement"],
            ["Ctrl+Z", "Undo up to 10 recent actions while paused"],
        ]),
        ("GAME",
        [
            ["Space, P; Ctrl/Command+P in text", "Pause/resume anywhere; Enter picks actions"],
            ["1  2  3", "Slow, medium, fast clock"],
            ["F5  F9  F6", "Quick-save, quick-load, guide"],
            ["F7  F8", "Weekly report, growth and road access"],
            ["F10", "Mute or unmute music"],
            ["O / Shift+O", "Cycle overlays / toggle last overlay"],
            ["V  E", "Effects, edge scrolling"],
            ["F3, Esc then Resize sidebar", "Font size, sidebar width"],
        ]),
    ];

    public static string Text(bool macOS = false)
    {
        var sections = Sections.Select(section => (section.Section, Rows: section.Rows.Select(row =>
            macOS && row[0] == "Ctrl+Z" ? new[] { "Command+Z", row[1] } : row).ToArray())).ToArray();
        var all = sections.SelectMany(s => s.Rows).Select(r => (IReadOnlyList<string>)r).ToArray();
        var widths = TextTable.Widths(Columns, all);
        var lines = new List<string>
        {
            TextTable.Row(Columns, widths, Columns.Select(c => c.Header).ToArray()),
            TextTable.Rule(widths),
        };
        foreach (var (section, rows) in sections)
        {
            lines.Add(string.Empty);
            lines.Add(section);
            lines.AddRange(rows.Select(row => TextTable.Row(Columns, widths, row)));
        }

        return string.Join("\n", lines);
    }
}
