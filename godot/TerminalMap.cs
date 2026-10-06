using Godot;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class TerminalMap : Control
{
    public const int FontSize = 18;
    public TerminalGrid Grid { get; } = new();
    public GameSession Session { get; set; } = null!;
    public Font CellFont { get; set; } = null!;
    private bool _cellsDirty = true;

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Resized += ResizeGrid;
        ResizeGrid();
    }

    public void Invalidate(bool cellsChanged)
    {
        _cellsDirty |= cellsChanged;
        QueueRedraw();
    }

    public void RefreshCells()
    {
        if (!_cellsDirty)
        {
            return;
        }
        Grid.Fill(Session);
        _cellsDirty = false;
    }

    private void ResizeGrid()
    {
        Grid.Resize(Size.X, Size.Y);
        Invalidate(true);
    }

    public override void _Draw()
    {
        RefreshCells();
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#101014"));
        float baseline = (TerminalGrid.CellHeight - CellFont.GetHeight(FontSize)) / 2 + CellFont.GetAscent(FontSize);
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                var position = new Vector2(x * TerminalGrid.CellWidth, y * TerminalGrid.CellHeight);
                DrawRect(new Rect2(position, new Vector2(TerminalGrid.CellWidth, TerminalGrid.CellHeight)),
                    ToColor(visual.Background));
                DrawString(CellFont, position + new Vector2(0, baseline), visual.Glyph,
                    HorizontalAlignment.Center, TerminalGrid.CellWidth, FontSize,
                    ToColor(visual.Foreground));
            }
        }
    }

    private static Color ToColor(Rgb color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    public void VerifyGlyphs(GameContent content)
    {
        var glyphs = content.Terrains.SelectMany(t => t.Glyphs)
            .Concat(content.Features.SelectMany(f => f.Glyphs))
            .Concat(content.Buildings.SelectMany(b => b.Glyphs))
            .Concat(content.Roads.SelectMany(r => r.Glyphs))
            .Concat(Zones.Placeable.Select(zone => Zones.Get(zone).EmptyGlyph));
        var missing = glyphs.SelectMany(glyph => glyph.EnumerateRunes())
            .Where(rune => !CellFont.HasChar(rune.Value))
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Map font is missing glyphs: {string.Join(", ", missing)}.");
        }
    }
}
