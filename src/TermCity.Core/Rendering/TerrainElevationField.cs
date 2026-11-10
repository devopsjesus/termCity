namespace TermCity.Core.Rendering;

public sealed class TerrainElevationField
{
    private readonly float[] _values;
    public int Width { get; }
    public int Height { get; }

    internal TerrainElevationField(int width, int height, float[] values)
    {
        Width = width;
        Height = height;
        _values = values;
    }

    public float this[int x, int y]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
            return _values[y * Width + x];
        }
    }
}
