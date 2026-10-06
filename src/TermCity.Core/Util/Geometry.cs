namespace TermCity.Core.Util;

/// <summary>A map cell coordinate.</summary>
public readonly record struct Pos(int X, int Y)
{
    public Pos Offset(int dx, int dy) => new(X + dx, Y + dy);
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
