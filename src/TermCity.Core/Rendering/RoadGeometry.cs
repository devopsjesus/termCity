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
    private readonly Dictionary<int, RoadPath?> _centerRoads = [];
    private readonly RoadMeshLayout _mesh;

    public RoadGeometry(GameMap map, IReadOnlyList<RoadPath> paths)
    {
        _width = map.Width;
        _height = map.Height;
        _occupied = new bool[_width * _height];
        _access = new byte[_occupied.Length];
        _mesh = new RoadMeshLayout(map, paths);

        foreach (var segment in _mesh.Segments)
        {
            double bed = segment.Type.WidthCells / 2.0;
            double margin = bed + ConnectionTolerance;
            double ax = segment.A.X, ay = segment.A.Y, bx = segment.B.X, by = segment.B.Y;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(Math.Min(ax, bx) - margin, segment.Road.Min(p => p.X)) - Precision));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(Math.Min(ay, by) - margin, segment.Road.Min(p => p.Y)) - Precision));
            int x1 = Math.Min(_width - 1, (int)Math.Floor(Math.Max(Math.Max(ax, bx) + margin, segment.Road.Max(p => p.X)) + Precision));
            int y1 = Math.Min(_height - 1, (int)Math.Floor(Math.Max(Math.Max(ay, by) + margin, segment.Road.Max(p => p.Y)) + Precision));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int index = y * _width + x;
                    _occupied[index] |= RoadMeshLayout.OverlapsCell(segment.Road, x, y);
                    if (map.TerrainAt(x, y).Buildable &&
                        DistanceToRectangle(ax, ay, bx, by, x - ConnectionTolerance, y - ConnectionTolerance,
                            x + 1 + ConnectionTolerance, y + 1 + ConnectionTolerance) <= bed + Precision)
                    {
                        if (_access[index] == 0) _accessCells.Add(index);
                        _access[index] = (byte)Math.Max(_access[index], segment.Type.Rank);
                    }
                }
            }
        }
    }

    public bool OverlapsCell(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height && _occupied[y * _width + x];

    public int AccessRank(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height ? _access[y * _width + x] : 0;

    public IEnumerable<(int Index, int Rank)> NearbyAccess => _accessCells.Select(index => (index, (int)_access[index]));

    public RoadPath? RoadAtCellCenter(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height) return null;
        int index = y * _width + x;
        if (_centerRoads.TryGetValue(index, out var cached)) return cached;
        var result = _mesh.RoadAt(new(x + 0.5f, y + 0.5f))?.Path;
        _centerRoads[index] = result;
        return result;
    }

    public RoadMeshLayout.Segment? RoadAt(System.Numerics.Vector2 point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= _width && point.Y <= _height ? _mesh.RoadAt(point) : null;

    internal bool HasParallelRoad(System.Numerics.Vector2 point, System.Numerics.Vector2 direction, float width)
    {
        direction = System.Numerics.Vector2.Normalize(direction);
        foreach (var segment in _mesh.AtCell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y)))
        {
            var delta = segment.B - segment.A;
            float length = delta.Length();
            if (length < 0.000001f) continue;
            var tangent = delta / length;
            if (Math.Abs(System.Numerics.Vector2.Dot(direction, tangent)) < 0.965926f) continue;
            float along = System.Numerics.Vector2.Dot(point - segment.A, tangent);
            if (along < 0 || along > length) continue;
            float across = Math.Abs((point.X - segment.A.X) * tangent.Y - (point.Y - segment.A.Y) * tangent.X);
            if (across < (width + segment.Type.WidthCells) / 2 + RoadMeshLayout.ShoulderWidth) return true;
        }
        return false;
    }

    public bool IsBridgeAtCellCenter(int x, int y)
    {
        return _mesh.RoadAt(new(x + 0.5f, y + 0.5f))?.Bridge == true;
    }

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
