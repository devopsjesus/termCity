namespace TermCity.Core.Rendering;

/// <summary>
/// Finds the nearest point on the drawn road curves, so things that travel along a road (cars, walkers) can follow the
/// curve the player sees rather than the staircase of cells beneath it. Coordinates are pixels at normal size.
/// </summary>
public sealed class RoadGuide
{
    /// <summary>The furthest a point may be from a curve and still be drawn onto it, in pixels.</summary>
    public const double Reach = RoadCurves.StraightenTolerance + 6;

    private const double BucketSize = 32;

    private readonly List<(double Ax, double Ay, double Bx, double By)> _segments = [];
    private readonly Dictionary<long, List<int>> _buckets = [];

    public RoadGuide(IReadOnlyList<RoadPath> paths)
    {
        foreach (var path in paths)
        {
            int count = path.Closed ? path.Count : path.Count - 1;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % path.Count;
                _segments.Add((path.Xs[i], path.Ys[i], path.Xs[j], path.Ys[j]));
                long key = Key((int)Math.Floor(path.Xs[i] / BucketSize), (int)Math.Floor(path.Ys[i] / BucketSize));
                if (!_buckets.TryGetValue(key, out var list))
                {
                    _buckets[key] = list = [];
                }

                list.Add(_segments.Count - 1);
            }
        }
    }

    public bool IsEmpty => _segments.Count == 0;

    /// <summary>The nearest point on any curve within <see cref="Reach"/> of (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool TrySnap(double x, double y, out double snappedX, out double snappedY)
    {
        snappedX = x;
        snappedY = y;
        int bx = (int)Math.Floor(x / BucketSize), by = (int)Math.Floor(y / BucketSize);
        double best = Reach * Reach;
        bool found = false;
        for (int j = by - 1; j <= by + 1; j++)
        {
            for (int i = bx - 1; i <= bx + 1; i++)
            {
                if (!_buckets.TryGetValue(Key(i, j), out var list))
                {
                    continue;
                }

                foreach (int s in list)
                {
                    var (ax, ay, cx, cy) = _segments[s];
                    double dx = cx - ax, dy = cy - ay, len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
                    double px = ax + dx * t, py = ay + dy * t;
                    double d2 = (x - px) * (x - px) + (y - py) * (y - py);
                    if (d2 < best)
                    {
                        best = d2;
                        snappedX = px;
                        snappedY = py;
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
