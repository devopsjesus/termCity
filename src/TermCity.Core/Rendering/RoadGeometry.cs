using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>Whole-cell clearance and access to the visible road bed, independent of camera and zoom.</summary>
public sealed class RoadGeometry
{
    public const double ConnectionTolerance = 0.75;
    private const double Precision = 1e-9;

    private readonly int _width, _height;
    private readonly bool[] _occupied;
    private readonly byte[] _access;
    private readonly List<int> _accessCells = [];

    public RoadGeometry(GameMap map, IReadOnlyList<RoadPath> paths)
    {
        _width = map.Width;
        _height = map.Height;
        _occupied = new bool[_width * _height];
        _access = new byte[_occupied.Length];
        var field = new float[_occupied.Length];
        Array.Fill(field, float.PositiveInfinity);
        const double bed = RoadVectorLayer.BedHalfWidth;
        double toleranceX = ConnectionTolerance * RoadCurves.CellWidth;
        double toleranceY = ConnectionTolerance * RoadCurves.CellHeight;
        double marginX = bed + toleranceX, marginY = bed + toleranceY;

        foreach (var path in paths)
        {
            var distances = new Dictionary<int, float>();
            int segments = path.Closed ? path.Count : Math.Max(1, path.Count - 1);
            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % path.Count;
                double ax = path.Xs[segment], ay = path.Ys[segment], bx = path.Xs[next], by = path.Ys[next];
                int x0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, bx) - marginX - Precision) / RoadCurves.CellWidth));
                int y0 = Math.Max(0, (int)Math.Floor((Math.Min(ay, by) - marginY - Precision) / RoadCurves.CellHeight));
                int x1 = Math.Min(_width - 1, (int)Math.Floor((Math.Max(ax, bx) + marginX + Precision) / RoadCurves.CellWidth));
                int y1 = Math.Min(_height - 1, (int)Math.Floor((Math.Max(ay, by) + marginY + Precision) / RoadCurves.CellHeight));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int index = y * _width + x;
                        double left = x * RoadCurves.CellWidth, top = y * RoadCurves.CellHeight;
                        double right = left + RoadCurves.CellWidth, bottom = top + RoadCurves.CellHeight;
                        float distance = (float)DistanceToRectangle(ax, ay, bx, by, left, top, right, bottom);
                        if (!distances.TryGetValue(index, out float previous) || distance < previous)
                            distances[index] = distance;
                        if (path.Connected && map.TerrainAt(x, y).Buildable &&
                            DistanceToRectangle(ax, ay, bx, by, left - toleranceX, top - toleranceY,
                                right + toleranceX, bottom + toleranceY) <= bed + Precision)
                        {
                            if (_access[index] == 0) _accessCells.Add(index);
                            _access[index] = (byte)Math.Max(_access[index], path.Type.Rank);
                        }
                    }
                }
            }

            foreach (var (index, distance) in distances)
            {
                // Per-path rectangle minima are a conservative bound on the renderer's blended field:
                // a cell is reserved whenever any part of its interior could be occupied by the road.
                field[index] = RoadVectorLayer.SmoothMin(field[index], distance, (float)RoadVectorLayer.Blend);
                _occupied[index] = field[index] < bed;
            }
        }
    }

    public bool OverlapsCell(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height && _occupied[y * _width + x];

    public int AccessRank(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height ? _access[y * _width + x] : 0;

    public IEnumerable<(int Index, int Rank)> NearbyAccess => _accessCells.Select(index => (index, (int)_access[index]));

    private static double DistanceToRectangle(
        double ax, double ay, double bx, double by, double left, double top, double right, double bottom)
    {
        double first = 0, last = 1;
        if (Clip(ax, bx - ax, left, right, ref first, ref last) &&
            Clip(ay, by - ay, top, bottom, ref first, ref last))
            return 0;

        double distance = Math.Min(PointToRectangle(ax, ay, left, top, right, bottom),
            PointToRectangle(bx, by, left, top, right, bottom));
        distance = Math.Min(distance, PointToSegment(left, top, ax, ay, bx, by));
        distance = Math.Min(distance, PointToSegment(right, top, ax, ay, bx, by));
        distance = Math.Min(distance, PointToSegment(left, bottom, ax, ay, bx, by));
        return Math.Min(distance, PointToSegment(right, bottom, ax, ay, bx, by));
    }

    private static bool Clip(double origin, double delta, double min, double max, ref double first, ref double last)
    {
        if (Math.Abs(delta) < 1e-12) return origin >= min && origin <= max;
        double a = (min - origin) / delta, b = (max - origin) / delta;
        first = Math.Max(first, Math.Min(a, b));
        last = Math.Min(last, Math.Max(a, b));
        return first <= last;
    }

    private static double PointToRectangle(double x, double y, double left, double top, double right, double bottom)
    {
        double dx = Math.Max(Math.Max(left - x, x - right), 0);
        double dy = Math.Max(Math.Max(top - y, y - bottom), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double PointToSegment(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
        double t = length < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / length, 0, 1);
        double ex = x - ax - t * dx, ey = y - ay - t * dy;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
