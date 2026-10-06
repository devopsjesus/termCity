using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public sealed class TerminalGrid
{
    public const int CellWidth = 12;
    public const int CellHeight = 22;
    private CellVisual?[] _cells = [];

    public int Columns { get; private set; }
    public int Rows { get; private set; }

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
            }
        }
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
