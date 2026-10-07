namespace TermCity.Core.Util;

/// <summary>A map cell coordinate.</summary>
public readonly record struct Pos(int X, int Y)
{
    public Pos Offset(int dx, int dy) => new(X + dx, Y + dy);
}

public static class CellLines
{
    /// <summary>
    /// The cells on the straight line from <paramref name="a"/> to <paramref name="b"/>, at any angle, with each cell
    /// sharing an edge with the next (never only a corner), so the line is a road the simulation can follow.
    /// </summary>
    public static List<Pos> Between(Pos a, Pos b)
    {
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        int sx = Math.Sign(b.X - a.X), sy = Math.Sign(b.Y - a.Y);
        var cells = new List<Pos>(dx + dy + 1) { a };
        int x = a.X, y = a.Y, ix = 0, iy = 0;
        while (ix < dx || iy < dy)
        {
            if ((1 + 2 * ix) * dy < (1 + 2 * iy) * dx)
            {
                x += sx;
                ix++;
            }
            else
            {
                y += sy;
                iy++;
            }

            cells.Add(new Pos(x, y));
        }

        return cells;
    }
}

/// <summary>An inclusive rectangle of map cells.</summary>
public readonly record struct CellRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width - 1;

    public int Bottom => Y + Height - 1;

    public int Area => Width * Height;

    public static CellRect FromCorners(Pos a, Pos b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new CellRect(x, y, Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    public static CellRect Single(Pos p) => new(p.X, p.Y, 1, 1);

    /// <summary>The smallest rectangle covering both, so every row and column of each is included.</summary>
    public CellRect Union(CellRect other)
    {
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new CellRect(x, y, Math.Max(Right, other.Right) - x + 1, Math.Max(Bottom, other.Bottom) - y + 1);
    }

    public bool Contains(int x, int y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Contains(Pos p) => Contains(p.X, p.Y);

    public IEnumerable<Pos> Cells()
    {
        for (int y = Y; y <= Bottom; y++)
        {
            for (int x = X; x <= Right; x++)
            {
                yield return new Pos(x, y);
            }
        }
    }

    public override string ToString() => Area == 1 ? $"({X},{Y})" : $"({X},{Y}) {Width}x{Height}";
}

/// <summary>A 24-bit colour. Core stays UI-agnostic; the front end maps this to its own colour type.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Rgb Blend(Rgb a, Rgb b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public Rgb Scale(double factor) => new(
        (byte)Math.Clamp(R * factor, 0, 255),
        (byte)Math.Clamp(G * factor, 0, 255),
        (byte)Math.Clamp(B * factor, 0, 255));
}
