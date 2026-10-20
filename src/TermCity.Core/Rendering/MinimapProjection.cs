using System.Numerics;

namespace TermCity.Core.Rendering;

public readonly struct MinimapProjection
{
    private readonly Vector2 _mapSize;
    private readonly int _quarterTurns;

    public MinimapProjection(int width, int height, int quarterTurns)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        _mapSize = new(width, height);
        _quarterTurns = (quarterTurns % 4 + 4) % 4;
    }

    private static Vector2 Rotate(Vector2 fraction, int turns) => turns switch
    {
        1 => new(1 - fraction.Y, fraction.X),
        2 => Vector2.One - fraction,
        3 => new(fraction.Y, 1 - fraction.X),
        _ => fraction
    };

    public Vector2 Fraction(Vector2 point) => Rotate(point / _mapSize, _quarterTurns);

    public Vector2 MapPoint(Vector2 fraction) => Rotate(fraction, (4 - _quarterTurns) % 4) * _mapSize;

    public static (Vector2 Start, Vector2 End)? ClipSegment(Vector2 start, Vector2 end)
    {
        var delta = end - start;
        float enter = 0, exit = 1;
        foreach (var (position, direction) in new[] { (start.X, delta.X), (start.Y, delta.Y) })
        {
            if (MathF.Abs(direction) < 0.000001f)
            {
                if (position < 0 || position > 1) return null;
                continue;
            }
            float first = -position / direction, last = (1 - position) / direction;
            enter = Math.Max(enter, Math.Min(first, last));
            exit = Math.Min(exit, Math.Max(first, last));
            if (enter > exit) return null;
        }
        return (start + delta * enter, start + delta * exit);
    }
}
