using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Terrain;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public sealed class TerminalGrid
{
    public const int CellWidth = 12;
    public const int CellHeight = 22;
    public const double BeatSeconds = 60.0 / 80.0;
    public const double CycleSeconds = BeatSeconds * 4;
    private CellVisual?[] _cells = [];
    private AnimationKind[] _animations = [];
    private bool[] _vectorRoad = [];
    private float _width, _height;
    public int PixelWidth { get; private set; } = CellWidth;
    public int PixelHeight { get; private set; } = CellHeight;

    public enum AnimationKind { None, Hill, Tree, Water }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int Rebuilds { get; private set; }

    /// <summary>Whether roads are drawn as curves over the cells (at every zoom level, but not under a map overlay).</summary>
    public bool VectorRoads { get; private set; }

    /// <summary>Whether the cell at this screen position shows a road, so its glyph is left to the curve layer.</summary>
    public bool IsVectorRoad(int x, int y) => VectorRoads && _vectorRoad[y * Columns + x];
    public double AnimationSeconds { get; private set; }

    public bool AdvanceAnimation(double delta, bool focused)
    {
        if (!double.IsFinite(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Animation delta must be finite and non-negative.");
        }
        if (!focused)
        {
            return false;
        }
        int previousBeat = (int)(AnimationSeconds / BeatSeconds);
        AnimationSeconds = (AnimationSeconds + delta % CycleSeconds) % CycleSeconds;
        return previousBeat != (int)(AnimationSeconds / BeatSeconds);
    }

    public static bool ShouldAnimate(Pos position) =>
        CellHash.Pick(position.X ^ 0x51ed270b, position.Y ^ 0x2f6e2b1d, 10) == 0;

    public AnimationKind AnimationAt(int x, int y) => _animations[y * Columns + x];

    public (float X, float Y) OffsetAt(GameSession session, int x, int y)
    {
        var kind = AnimationAt(x, y);
        if (kind == AnimationKind.None)
        {
            return (0, 0);
        }
        var position = session.ScreenToMap(x, y);
        int beat = (int)(AnimationSeconds / BeatSeconds);
        float step = (1 - Math.Abs(beat - 2)) * 1.5f;
        float direction = ((position.X ^ position.Y) & 1) == 0 ? 1 : -1;
        // Screen Y increases downward, so vertical motion uses the inverted step.
        return kind == AnimationKind.Hill ? (0, -step * direction) : (-step * direction, 0);
    }

    public void Resize(float width, float height, int scale = 1)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Grid size must be finite and non-negative.");
        }

        if (scale is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(scale));
        _width = width;
        _height = height;
        PixelWidth = CellWidth * scale;
        PixelHeight = CellHeight * scale;
        int columns = Math.Max(1, (int)Math.Ceiling(width / PixelWidth));
        int rows = Math.Max(1, (int)Math.Ceiling(height / PixelHeight));
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        Columns = columns;
        Rows = rows;
        _cells = new CellVisual?[checked(columns * rows)];
        _animations = new AnimationKind[_cells.Length];
        _vectorRoad = new bool[_cells.Length];
    }

    public bool TryCell(float x, float y, out Pos cell)
    {
        cell = default;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 ||
            x >= _width || y >= _height)
        {
            return false;
        }

        cell = new((int)(x / PixelWidth), (int)(y / PixelHeight));
        return true;
    }

    public void Fill(GameSession session)
    {
        Resize(_width, _height, session.ZoomLevel > 0 ? 2 : 1);
        session.SetViewport(Math.Max(1, (int)(_width / PixelWidth)), Math.Max(1, (int)(_height / PixelHeight)));
        VectorRoads = session.Overlay == MapOverlay.Off;
        var sampler = new BlockSampler(session.Game) { Overlay = session.Overlay, VectorRoads = VectorRoads };
        var hillGlyphs = session.Game.Map.Content.Terrains
            .Where(terrain => terrain.Generator is HillGenerator)
            .SelectMany(terrain => terrain.Glyphs).ToHashSet();
        var waterGlyphs = session.Game.Map.Content.Terrains
            .Where(terrain => terrain.Generator is WaterGenerator)
            .SelectMany(terrain => terrain.Glyphs).ToHashSet();
        var tree = session.Game.Map.Content.Features.Find("Tree");
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                var position = session.ScreenToMap(x, y);
                bool road = false;
                _cells[y * Columns + x] = session.Game.Map.InBounds(position)
                    ? session.ZoomLevel < 0
                        ? sampler.Sample(position.X, position.Y, session.Stride, out road)
                        : CellRenderer.Render(session.Game, position.X, position.Y, session.Overlay, VectorRoads)
                    : null;
                if (session.ZoomLevel >= 0)
                {
                    road = VectorRoads && _cells[y * Columns + x] is not null && session.Game.Map.HasRoad(position.X, position.Y);
                }

                _vectorRoad[y * Columns + x] = road;
                var visual = _cells[y * Columns + x];
                _animations[y * Columns + x] = visual is null || !ShouldAnimate(position)
                    ? AnimationKind.None
                    : hillGlyphs.Contains(visual.Value.Glyph) &&
                        (session.Stride > 1 || session.Game.Map.TerrainAt(position.X, position.Y).Generator is HillGenerator)
                        ? AnimationKind.Hill
                        : waterGlyphs.Contains(visual.Value.Glyph) &&
                            (session.Stride > 1 || session.Game.Map.TerrainAt(position.X, position.Y).Generator is WaterGenerator)
                            ? AnimationKind.Water
                        : tree?.Glyphs.Contains(visual.Value.Glyph) == true
                            ? AnimationKind.Tree
                            : AnimationKind.None;
            }
        }
        Rebuilds++;
    }

    public CellVisual? VisualAt(GameSession session, int x, int y)
    {
        if (_cells[y * Columns + x] is not { } visual)
        {
            return null;
        }

        var position = session.ScreenToMap(x, y);
        var block = session.BlockAt(position);
        if (session.Selection is { } selection && Intersects(selection, block))
        {
            visual = visual with { Background = Rgb.Hex(0x4e4578) };
        }
        if (session.Preview is { } preview && preview.Touches(block))
        {
            visual = visual with
            {
                Background = Rgb.Hex(preview.IsValid(session.Game, position.X, position.Y) ? 0x2a7849 : 0x8c2d37),
            };
        }
        if (block.Contains(session.Cursor))
        {
            visual = visual with { Foreground = Rgb.Hex(0x000000), Background = Rgb.Hex(0xffdc5a) };
        }
        return visual;
    }

    /// <summary>The colour a selection, preview or cursor paints over this cell, or null if it has none.</summary>
    public Rgb? HighlightAt(GameSession session, int x, int y) =>
        _cells[y * Columns + x] is { } plain && VisualAt(session, x, y) is { } shown && shown.Background != plain.Background
            ? shown.Background
            : null;

    private static bool Intersects(CellRect left, CellRect right) =>
        left.X <= right.Right && left.Right >= right.X &&
        left.Y <= right.Bottom && left.Bottom >= right.Y;
}
