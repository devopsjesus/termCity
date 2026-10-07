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

    public enum AnimationKind { None, Hill, Tree }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int Rebuilds { get; private set; }
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
        // Godot's screen Y axis is the inverse of Bevy's world Y axis.
        return kind == AnimationKind.Hill ? (0, -step * direction) : (-step * direction, 0);
    }

    public void Resize(float width, float height)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Grid size must be finite and non-negative.");
        }

        int columns = Math.Max(1, (int)(width / CellWidth));
        int rows = Math.Max(1, (int)(height / CellHeight));
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        Columns = columns;
        Rows = rows;
        _cells = new CellVisual?[checked(columns * rows)];
        _animations = new AnimationKind[_cells.Length];
    }

    public bool TryCell(float x, float y, out Pos cell)
    {
        cell = default;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 ||
            x >= Columns * CellWidth || y >= Rows * CellHeight)
        {
            return false;
        }

        cell = new((int)(x / CellWidth), (int)(y / CellHeight));
        return true;
    }

    public void Fill(GameSession session)
    {
        session.SetViewport(Columns, Rows);
        var sampler = new BlockSampler(session.Game);
        var hillGlyphs = session.Game.Map.Content.Terrains
            .Where(terrain => terrain.Generator is HillGenerator)
            .SelectMany(terrain => terrain.Glyphs).ToHashSet();
        var tree = session.Game.Map.Content.Features.Find("Tree");
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                var position = session.ScreenToMap(x, y);
                _cells[y * Columns + x] = session.Game.Map.InBounds(position)
                    ? session.ZoomLevel < 0
                        ? sampler.Sample(position.X, position.Y, session.Stride)
                        : CellRenderer.Render(session.Game, position.X, position.Y)
                    : null;
                var visual = _cells[y * Columns + x];
                _animations[y * Columns + x] = visual is null || !ShouldAnimate(position)
                    ? AnimationKind.None
                    : hillGlyphs.Contains(visual.Value.Glyph) &&
                        (session.Stride > 1 || session.Game.Map.TerrainAt(position.X, position.Y).Generator is HillGenerator)
                        ? AnimationKind.Hill
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
        if (session.Preview is { } preview && Intersects(preview.Area, block))
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

    private static bool Intersects(CellRect left, CellRect right) =>
        left.X <= right.Right && left.Right >= right.X &&
        left.Y <= right.Bottom && left.Bottom >= right.Y;
}
