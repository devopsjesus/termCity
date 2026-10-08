using Godot;
using TermCity.Core.Effects;
using TermCity.Core.Rendering;
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
    private readonly List<GlyphDraw> _overlays = [];
    private Vector2 _shakeOffset;
    private readonly RoadVectorLayer _roads = new();
    private readonly Dictionary<(int X, int Y, int Scale, int Stride), ImageTexture?> _roadChunks = [];
    private readonly List<GlyphDraw> _mains = [];
    private readonly Dictionary<(int Radius, int Width, int Height, int Stride), IReadOnlySet<Pos>> _areaOutlines = [];
    public int AreasDrawn { get; private set; }

    /// <summary>Optional visual effects. Null or inactive means the map draws exactly as it always did.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Frames drawn so far (diagnostics and the smoke test).</summary>
    public int DrawCount { get; private set; }

    /// <summary>How many glyphs the last frame drew because of effects (modified cells, ghosts and sprites).</summary>
    public int EffectGlyphsDrawn { get; private set; }

    /// <summary>How many blocks of curved road the last frame laid over the map (diagnostics and the smoke test).</summary>
    public int RoadChunksDrawn { get; private set; }

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
        DrawCount++;
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        int scale = Grid.PixelWidth / TerminalGrid.CellWidth;
        int fontSize = FontSize * scale;
        int cellWidth = Grid.PixelWidth, cellHeight = Grid.PixelHeight;
        float baseline = (cellHeight - CellFont.GetHeight(fontSize)) / 2 + CellFont.GetAscent(fontSize);
        // Effects only exist at normal and zoomed-in views, where one screen cell is one map cell.
        var fx = Effects is { Settings.Active: true, HasVisuals: true } && Session.Stride == 1 ? Effects : null;
        var shake = fx is null ? (0f, 0f) : EffectDraw.ShakePixels(fx.Shake, cellWidth, cellHeight);
        _shakeOffset = new Vector2(shake.Item1, shake.Item2);
        int drawn = 0;
        _overlays.Clear();
        DrawSetTransform(_shakeOffset, 0f, Vector2.One);
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                var background = visual.Background;
                if (fx is not null)
                {
                    var at = Session.ScreenToMap(x, y);
                    if (fx.TryGetCell(at.X, at.Y, out var cellEffect) && cellEffect.BackgroundAmount > 0)
                    {
                        background = EffectDraw.Background(background, cellEffect);
                    }
                }
                var position = new Vector2(x * cellWidth, y * cellHeight);
                DrawRect(new Rect2(position, new Vector2(cellWidth, cellHeight)), ToColor(background));
            }
        }
        _mains.Clear();
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                if (fx is not null)
                {
                    var at = Session.ScreenToMap(x, y);
                    if (fx.TryGetCell(at.X, at.Y, out var cellEffect) && !cellEffect.IsIdentity)
                    {
                        EffectDraw.PlanCell(cellEffect, visual, x, y, cellWidth, cellHeight, shake, out var main, out var overlay);
                        if (main is { } mainDraw)
                        {
                            _mains.Add(mainDraw);
                        }
                        if (overlay is { } ghost)
                        {
                            _overlays.Add(ghost);
                        }
                        continue;
                    }
                }
                var offset = Grid.OffsetAt(Session, x, y);
                var glyphPosition = new Vector2(x * cellWidth + offset.X * scale,
                    y * cellHeight + offset.Y * scale);
                DrawCellGlyph(visual.Glyph, glyphPosition, cellWidth, cellHeight, fontSize, baseline, ToColor(visual.Foreground));
            }
        }
        // The roads cover whatever terrain they run over; effects on cells go over the roads.
        DrawRoadCurves(scale, cellWidth, cellHeight);
        DrawBuildingFootprints(cellWidth, cellHeight);
        DrawAreas(cellWidth, cellHeight, fontSize);
        foreach (var mainDraw in _mains)
        {
            DrawEffectGlyph(mainDraw, cellWidth, cellHeight, baseline, fontSize);
            drawn++;
        }
        if (fx is not null)
        {
            // Ghosts and sprites go on top of the whole map so they can float across neighbouring cells.
            foreach (var ghost in _overlays)
            {
                DrawEffectGlyph(ghost, cellWidth, cellHeight, baseline, fontSize);
                drawn++;
            }
            var origin = Session.ScreenToMap(0, 0);
            foreach (var sprite in fx.Sprites)
            {
                if (EffectDraw.PlanSprite(sprite, origin.X, origin.Y, cellWidth, cellHeight, shake,
                        Grid.Columns, Grid.Rows) is { } draw)
                {
                    DrawEffectGlyph(draw, cellWidth, cellHeight, baseline, fontSize);
                    drawn++;
                }
            }
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        EffectGlyphsDrawn = drawn;
    }

    private void DrawBuildingFootprints(int cellWidth, int cellHeight)
    {
        var origin = Session.ScreenToMap(0, 0);
        foreach (var area in Session.Game.Map.BuildingFootprints.Values)
        {
            if (area.Area <= 1) continue;
            var position = new Vector2((area.X - origin.X) * cellWidth / (float)Session.Stride,
                (area.Y - origin.Y) * cellHeight / (float)Session.Stride);
            var size = new Vector2(area.Width * cellWidth / (float)Session.Stride,
                area.Height * cellHeight / (float)Session.Stride);
            if (!new Rect2(position, size).Intersects(new Rect2(Vector2.Zero, Size))) continue;
            var type = Session.Game.Map.BuildingAt(area.X, area.Y)!;
            DrawRect(new Rect2(position + Vector2.One, size - Vector2.One * 2),
                ToColor(type.Foreground) with { A = 0.5f }, filled: false);
        }
    }

    private void DrawAreas(int cellWidth, int cellHeight, int fontSize)
    {
        var origin = Session.ScreenToMap(0, 0);
        var view = new CellRect(origin.X, origin.Y, Grid.Columns * Session.Stride, Grid.Rows * Session.Stride);
        IEnumerable<EffectArea> areas = AreaOfEffect.InView(Session.Game, view, Session.ActiveArea);
        if (Session.Preview is { Building: { } type } preview)
            areas = areas.Concat(Session.Game.PlanBuildings(type, preview.Area)
                .SelectMany(area => AreaOfEffect.ForBuilding(type, new(area.X, area.Y))));
        AreasDrawn = 0;
        const int pixel = 3;
        foreach (var group in areas.GroupBy(area => area.Center))
        {
            var center = new Vector2((group.Key.X - origin.X + 0.5f) * cellWidth / Session.Stride,
                (group.Key.Y - origin.Y + 0.5f) * cellHeight / Session.Stride);
            foreach (var area in group)
            {
                var key = (area.Radius, cellWidth, cellHeight, Session.Stride);
                if (!_areaOutlines.TryGetValue(key, out var outline))
                {
                    outline = AreaOfEffect.Outline(area.Radius, cellWidth, cellHeight, Session.Stride, pixel);
                    _areaOutlines.Add(key, outline);
                }
                foreach (var point in outline)
                {
                    var position = center + new Vector2(point.X, point.Y);
                    if (new Rect2(Vector2.Zero, Size).HasPoint(position))
                        DrawRect(new Rect2(position, new Vector2(pixel, pixel)), new Color(1, 1, 1, AreaOfEffect.RingAlpha));
                }
                AreasDrawn++;
            }
            string glyphs = string.Join(" ", group.SelectMany(area => area.Glyphs));
            float width = CellFont.GetStringSize(glyphs, fontSize: fontSize).X;
            var label = center + new Vector2(-width / 2,
                group.Max(area => area.Radius) * cellHeight / (float)Session.Stride + pixel + CellFont.GetAscent(fontSize));
            DrawString(CellFont, label, glyphs, fontSize: fontSize,
                modulate: new Color(1, 1, 1, AreaOfEffect.IconAlpha));
        }
    }

    /// <summary>
    /// Lays the curved roads over the terrain, then lets the cursor, selection and previews show through them.
    /// </summary>
    private void DrawRoadCurves(int scale, int cellWidth, int cellHeight)
    {
        RoadChunksDrawn = 0;
        if (!Grid.VectorRoads)
        {
            return;
        }

        if (_roads.Sync(Session.Game))
        {
            _roadChunks.Clear();
        }

        if (_roads.Paths.Count == 0)
        {
            return;
        }

        const int chunk = RoadVectorLayer.ChunkCells;
        int stride = Session.Stride, span = chunk * stride;
        var origin = Session.ScreenToMap(0, 0);
        int lastX = origin.X + Grid.Columns * stride - 1, lastY = origin.Y + Grid.Rows * stride - 1;
        for (int cy = Math.Max(0, origin.Y) / span; cy <= lastY / span; cy++)
        {
            for (int cx = Math.Max(0, origin.X) / span; cx <= lastX / span; cx++)
            {
                if (!_roadChunks.TryGetValue((cx, cy, scale, stride), out var texture))
                {
                    texture = _roads.Render(cx, cy, scale, stride) is { } rendered
                        ? ImageTexture.CreateFromImage(Image.CreateFromData(
                            rendered.Width, rendered.Height, false, Image.Format.Rgba8, rendered.Rgba))
                        : null;
                    _roadChunks[(cx, cy, scale, stride)] = texture;
                }

                if (texture is not null)
                {
                    var at = new Vector2((cx * chunk - origin.X / stride) * cellWidth, (cy * chunk - origin.Y / stride) * cellHeight);
                    DrawTexture(texture, at);
                    RoadChunksDrawn++;
                }
            }
        }

        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.IsVectorRoad(x, y) && Grid.HighlightAt(Session, x, y) is { } highlight)
                {
                    var tint = ToColor(highlight);
                    tint.A = 0.6f;
                    DrawRect(new Rect2(x * cellWidth, y * cellHeight, cellWidth, cellHeight), tint);
                }
            }
        }
    }

    /// <summary>Draws one effect glyph scaled about its own centre.</summary>
    private void DrawEffectGlyph(in GlyphDraw draw, int cellWidth, int cellHeight, float baseline, int fontSize)
    {
        DrawSetTransform(new Vector2(draw.CentreX, draw.CentreY), 0f, new Vector2(draw.Scale, draw.Scale));
        var colour = ToColor(draw.Color);
        colour.A = draw.Alpha;
        DrawCellGlyph(draw.Glyph, new Vector2(-cellWidth / 2f, -cellHeight / 2f),
            cellWidth, cellHeight, fontSize, baseline, colour);
        DrawSetTransform(_shakeOffset, 0f, Vector2.One);
    }

    private void DrawCellGlyph(string glyph, Vector2 topLeft, int cellWidth, int cellHeight, int fontSize,
        float baseline, Color colour)
    {
        if (!glyph.Contains('\n'))
        {
            DrawString(CellFont, topLeft + new Vector2(0, baseline), glyph,
                HorizontalAlignment.Center, cellWidth, fontSize, colour);
            return;
        }
        var rows = glyph.Split('\n');
        int size = fontSize / 2;
        float rowHeight = cellHeight / (float)rows.Length;
        float artBaseline = (rowHeight - CellFont.GetHeight(size)) / 2 + CellFont.GetAscent(size);
        for (int row = 0; row < rows.Length; row++)
            DrawString(CellFont, topLeft + new Vector2(0, row * rowHeight + artBaseline), rows[row],
                HorizontalAlignment.Center, cellWidth, size, colour);
    }

    private static Color ToColor(Rgb color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    public void VerifyGlyphs(GameContent content)
    {
        var glyphs = content.Terrains.SelectMany(t => t.Glyphs)
            .Concat(content.Features.SelectMany(f => f.Glyphs))
            .Concat(content.Buildings.SelectMany(b => b.Glyphs))
            .Concat(content.Buildings.SelectMany(b => b.FootprintArt))
            .Concat(new[] { "♧", "Ψ" })
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
