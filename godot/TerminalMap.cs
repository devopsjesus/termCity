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

    public void AdvanceAnimation(double delta, bool focused)
    {
        if (Grid.AdvanceAnimation(delta, focused))
        {
            QueueRedraw();
        }
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

    public void ZoomBy(int delta, Vector2? pointer = null)
    {
        if (delta == 0) return;
        RefreshCells();
        var anchor = pointer ?? Size / 2;
        if (!Grid.TryCell(anchor.X, anchor.Y, out var before)) return;
        var world = Session.ScreenToMap(before.X, before.Y);
        Session.ZoomBy(delta, before.X, before.Y);
        RefreshCells();
        if (Grid.TryCell(anchor.X, anchor.Y, out var after))
            Session.PanCamera(world, after.X, after.Y);
    }

    private void ResizeGrid()
    {
        Grid.Resize(Size.X, Size.Y, Session.ZoomLevel > 0 ? 2 : 1);
        Invalidate(true);
    }

    public override void _Draw()
    {
        RefreshCells();
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        int scale = Grid.PixelWidth / TerminalGrid.CellWidth;
        int fontSize = FontSize * scale;
        float baseline = (Grid.PixelHeight - CellFont.GetHeight(fontSize)) / 2 + CellFont.GetAscent(fontSize);
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                var position = new Vector2(x * Grid.PixelWidth, y * Grid.PixelHeight);
                DrawRect(new Rect2(position, new Vector2(Grid.PixelWidth, Grid.PixelHeight)),
                    ToColor(visual.Background));
            }
        }
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                var offset = Grid.OffsetAt(Session, x, y);
                var position = new Vector2(x * Grid.PixelWidth + offset.X * scale,
                    y * Grid.PixelHeight + baseline + offset.Y * scale);
                DrawString(CellFont, position, visual.Glyph,
                    HorizontalAlignment.Center, Grid.PixelWidth, fontSize,
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
