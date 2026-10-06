namespace TermCity.Core.Util;

/// <summary>Stable, well-mixed hash of a cell coordinate, used to vary glyphs without storing per-cell state.</summary>
public static class CellHash
{
    public static int Pick(int x, int y, int count)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            h *= 2246822519u;
            h ^= h >> 15;
            return (int)(h % (uint)count);
        }
    }
}
