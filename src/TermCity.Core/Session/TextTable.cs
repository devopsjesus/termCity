namespace TermCity.Core.Session;

/// <summary>A column of a terminal-style table: a header and whether its cells are right-aligned (numbers, money).</summary>
public readonly record struct TableColumn(string Header, bool RightAligned = false)
{
    public static implicit operator TableColumn(string header) => new(header);

    public static TableColumn Right(string header) => new(header, true);
}

/// <summary>Lays rows out as aligned monospace columns under a header and a box-drawing rule.</summary>
public static class TextTable
{
    public const string Gap = "  ";
    public const char RuleGlyph = '─';

    public static int[] Widths(IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = new int[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            widths[c] = columns[c].Header.Length;
            foreach (var row in rows)
            {
                if (c < row.Count) widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        return widths;
    }

    public static string Row(IReadOnlyList<TableColumn> columns, int[] widths, IReadOnlyList<string> cells)
    {
        var parts = new string[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            string cell = c < cells.Count ? cells[c] : string.Empty;
            parts[c] = columns[c].RightAligned ? cell.PadLeft(widths[c]) : cell.PadRight(widths[c]);
        }

        return string.Join(Gap, parts).TrimEnd();
    }

    public static string Rule(int[] widths) => string.Join(Gap, widths.Select(w => new string(RuleGlyph, w)));

    /// <summary>The header, the rule and then one line per row.</summary>
    public static List<string> Lines(IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = Widths(columns, rows);
        var lines = new List<string>
        {
            Row(columns, widths, columns.Select(c => c.Header).ToArray()),
            Rule(widths),
        };
        lines.AddRange(rows.Select(row => Row(columns, widths, row)));
        return lines;
    }

    public static string Text(IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows) =>
        string.Join("\n", Lines(columns, rows));

    /// <summary>The lines of a prompt's choices as a table: header, rule and a row per choice (cells aligned, no index).</summary>
    public static (string Header, string Rule, string[] Rows)? ForPrompt(SessionPrompt prompt)
    {
        if (prompt.Columns is not { Count: > 0 } columns)
        {
            return null;
        }

        var rows = prompt.Choices.Select((choice, index) => (IReadOnlyList<string>)
            new[] { prompt.Shortcuts[index].DisplayLabel(choice.Label) }.Concat(choice.Cells ?? []).ToArray()).ToArray();
        var widths = Widths(columns, rows);
        return (Row(columns, widths, columns.Select(c => c.Header).ToArray()), Rule(widths),
            rows.Select(row => Row(columns, widths, row)).ToArray());
    }
}
