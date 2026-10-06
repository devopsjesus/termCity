namespace TermCity.Core.Session;

/// <summary>
/// Turns "the pointer is near the edge of the map view" into camera movement. The closer the pointer is to the
/// edge the faster the map scrolls. Pure logic (no UI), so it can be tested without a terminal.
/// </summary>
public sealed class EdgeScroller
{
    /// <summary>Width of the sensitive band, in cells, at the left and right edges.</summary>
    public const int HorizontalZone = 3;

    /// <summary>Height of the sensitive band, in rows, at the top and bottom edges (cells are about twice as tall as wide).</summary>
    public const int VerticalZone = 2;

    // Cells per second, indexed by depth into the zone (1 = just inside the band, up to the zone size at the very edge).
    // Vertical speeds are lower because a row covers about twice the distance of a column.
    private static readonly double[] HorizontalSpeed = [0, 12, 28, 48];
    private static readonly double[] VerticalSpeed = [0, 6, 12];

    private double _carryX;
    private double _carryY;

    /// <summary>Scroll direction and depth for a pointer position: -1/0/1 and how far into the band it is.</summary>
    public static (int Dx, int DepthX, int Dy, int DepthY) Zone(int x, int y, int width, int height)
    {
        int dx = 0, depthX = 0, dy = 0, depthY = 0;

        // Skip an axis when the view is too small for the bands not to overlap.
        if (width >= 2 * HorizontalZone + 2)
        {
            if (x < HorizontalZone)
            {
                dx = -1;
                depthX = HorizontalZone - Math.Max(0, x);
            }
            else if (x >= width - HorizontalZone)
            {
                dx = 1;
                depthX = x - (width - HorizontalZone - 1);
            }
        }

        if (height >= 2 * VerticalZone + 2)
        {
            if (y < VerticalZone)
            {
                dy = -1;
                depthY = VerticalZone - Math.Max(0, y);
            }
            else if (y >= height - VerticalZone)
            {
                dy = 1;
                depthY = y - (height - VerticalZone - 1);
            }
        }

        return (dx, Math.Min(depthX, HorizontalZone), dy, Math.Min(depthY, VerticalZone));
    }

    /// <summary>Whole cells to scroll for a pointer held at (x, y) for <paramref name="seconds"/>.</summary>
    public (int Dx, int Dy) Step(int x, int y, int width, int height, double seconds)
    {
        var (dx, depthX, dy, depthY) = Zone(x, y, width, height);

        _carryX = dx == 0 ? 0 : _carryX + dx * HorizontalSpeed[depthX] * seconds;
        _carryY = dy == 0 ? 0 : _carryY + dy * VerticalSpeed[depthY] * seconds;

        int stepX = (int)Math.Truncate(_carryX);
        int stepY = (int)Math.Truncate(_carryY);
        _carryX -= stepX;
        _carryY -= stepY;
        return (stepX, stepY);
    }

    public void Reset() => _carryX = _carryY = 0;
}
