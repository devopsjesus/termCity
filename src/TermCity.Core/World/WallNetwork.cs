using System.Numerics;

namespace TermCity.Core.World;

public readonly record struct WallNetworkSpan(int Id, Vector2 A, Vector2 B, bool IsGate = false, bool Intact = true);

public sealed record WallNetworkRegion(IReadOnlyList<Vector2> Polygon, IReadOnlySet<int> SpanIds, bool ConnectedToRoot)
{
    public bool Contains(Vector2 point) => WallNetwork.PointInside(Polygon, point);
}

public sealed class WallNetwork
{
    public const float Tolerance = 0.0001f;
    private readonly WallNetworkSpan[] _spans;
    private readonly Dictionary<(int X, int Y), List<WallNetworkSpan>> _spatial = [];
    private readonly HashSet<int> _connected = [];
    public IReadOnlyList<WallNetworkRegion> Regions { get; }

    public WallNetwork(IEnumerable<WallNetworkSpan> spans, Vector2 root, IReadOnlySet<int>? rootSpanIds = null)
    {
        if (!Finite(root)) throw new ArgumentOutOfRangeException(nameof(root));
        var all = spans.ToArray();
        if (all.Select(s => s.Id).Distinct().Count() != all.Length || all.Any(s =>
            s.Id <= 0 || !Finite(s.A) || !Finite(s.B) || Vector2.DistanceSquared(s.A, s.B) <= Tolerance * Tolerance))
            throw new ArgumentException("Walls require unique positive IDs and finite, nonzero spans.", nameof(spans));
        _spans = all.Where(s => s.Intact).ToArray();
        foreach (var span in _spans)
            for (int y = (int)MathF.Floor(Math.Min(span.A.Y, span.B.Y)); y <= (int)MathF.Floor(Math.Max(span.A.Y, span.B.Y)); y++)
                for (int x = (int)MathF.Floor(Math.Min(span.A.X, span.B.X)); x <= (int)MathF.Floor(Math.Max(span.A.X, span.B.X)); x++)
                {
                    if (!_spatial.TryGetValue((x, y), out var values)) _spatial.Add((x, y), values = []);
                    values.Add(span);
                }
        var cuts = _spans.Select(_ => new List<float> { 0, 1 }).ToArray();
        for (int i = 0; i < _spans.Length; i++)
            for (int j = i + 1; j < _spans.Length; j++)
            {
                var a = _spans[i]; var b = _spans[j];
                var da = a.B - a.A; var db = b.B - b.A;
                double cross = Cross(da, db);
                if (Math.Abs(cross) > 1e-10)
                {
                    float t = (float)(Cross(b.A - a.A, db) / cross);
                    float u = (float)(Cross(b.A - a.A, da) / cross);
                    if (t >= -Tolerance && t <= 1 + Tolerance && u >= -Tolerance && u <= 1 + Tolerance)
                    {
                        cuts[i].Add(Math.Clamp(t, 0, 1));
                        cuts[j].Add(Math.Clamp(u, 0, 1));
                    }
                }
                else if (PointLineDistance(b.A, a.A, a.B) <= Tolerance)
                {
                    AddProjection(cuts[i], b.A, a); AddProjection(cuts[i], b.B, a);
                    AddProjection(cuts[j], a.A, b); AddProjection(cuts[j], a.B, b);
                }
            }
        var points = new List<Vector2>();
        var pointIds = new Dictionary<(long X, long Y), int>();
        int Node(Vector2 point)
        {
            var key = ((long)Math.Round(point.X / (Tolerance / 2)), (long)Math.Round(point.Y / (Tolerance / 2)));
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    if (pointIds.TryGetValue((key.Item1 + dx, key.Item2 + dy), out int neighbor) &&
                        Vector2.DistanceSquared(points[neighbor], point) <= Tolerance * Tolerance) return neighbor;
            int id;
            id = points.Count;
            points.Add(point);
            pointIds.Add(key, id);
            return id;
        }
        var edges = new Dictionary<(int A, int B), HashSet<int>>();
        for (int i = 0; i < _spans.Length; i++)
        {
            var parameters = cuts[i].Order().ToArray();
            for (int k = 1; k < parameters.Length; k++)
            {
                if (parameters[k] - parameters[k - 1] <= Tolerance) continue;
                int a = Node(Vector2.Lerp(_spans[i].A, _spans[i].B, parameters[k - 1]));
                int b = Node(Vector2.Lerp(_spans[i].A, _spans[i].B, parameters[k]));
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var ids)) edges.Add(key, ids = []);
                ids.Add(_spans[i].Id);
            }
        }
        var neighbors = Enumerable.Range(0, points.Count).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in edges.Keys) { neighbors[a].Add(b); neighbors[b].Add(a); }
        for (int i = 0; i < neighbors.Length; i++)
        {
            int node = i;
            neighbors[i].Sort((a, b) => Math.Atan2(points[a].Y - points[node].Y, points[a].X - points[node].X)
                .CompareTo(Math.Atan2(points[b].Y - points[node].Y, points[b].X - points[node].X)));
        }
        var components = Enumerable.Repeat(-1, points.Count).ToArray();
        int componentCount = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (components[i] >= 0) continue;
            var queue = new Queue<int>();
            queue.Enqueue(i); components[i] = componentCount;
            while (queue.TryDequeue(out int node))
                foreach (int next in neighbors[node])
                    if (components[next] < 0) { components[next] = componentCount; queue.Enqueue(next); }
            componentCount++;
        }
        var rootComponents = new HashSet<int>();
        if (rootSpanIds is not null)
        {
            foreach (var (edge, ids) in edges)
                if (ids.Any(rootSpanIds.Contains)) rootComponents.Add(components[edge.A]);
        }
        else if (edges.Count > 0)
        {
            var nearest = edges.Keys.MinBy(e => PointSegmentDistance(root, points[e.A], points[e.B]));
            rootComponents.Add(components[nearest.A]);
        }
        foreach (var (edge, ids) in edges)
            if (rootComponents.Contains(components[edge.A])) _connected.UnionWith(ids);
        var regions = new List<WallNetworkRegion>();
        var visited = new HashSet<(int A, int B)>();
        foreach (var edge in edges.Keys)
            foreach (var initial in new[] { edge, (A: edge.B, B: edge.A) })
            {
                if (visited.Contains(initial)) continue;
                var polygon = new List<Vector2>();
                var sources = new HashSet<int>();
                var current = initial;
                int limit = edges.Count * 2 + 1;
                do
                {
                    visited.Add(current);
                    polygon.Add(points[current.A]);
                    var key = current.A < current.B ? current : (A: current.B, B: current.A);
                    sources.UnionWith(edges[key]);
                    var choices = neighbors[current.B];
                    int reverse = choices.IndexOf(current.A);
                    int next = choices[(reverse + choices.Count - 1) % choices.Count];
                    current = (current.B, next);
                    if (--limit <= 0) throw new InvalidOperationException("Wall face traversal did not close.");
                } while (current != initial);
                double area = 0;
                for (int i = 0; i < polygon.Count; i++) area += Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
                if (area > Tolerance)
                {
                    var outline = polygon.Where((point, i) =>
                    {
                        var before = point - polygon[(i + polygon.Count - 1) % polygon.Count];
                        var after = polygon[(i + 1) % polygon.Count] - point;
                        return Math.Abs(Cross(before, after)) > 1e-10 || Vector2.Dot(before, after) <= 0;
                    }).ToArray();
                    regions.Add(new(outline, sources, rootComponents.Contains(components[initial.A])));
                }
            }
        Regions = regions.AsReadOnly();
    }

    public bool Connected(int spanId) => _connected.Contains(spanId);
    public WallNetworkRegion? RegionAt(Vector2 point) => Regions.FirstOrDefault(r => r.Contains(point));
    public bool Contains(Vector2 point) => Regions.Any(r => r.ConnectedToRoot && r.Contains(point));
    public bool ContainsAny(Vector2 point) => Regions.Any(r => r.Contains(point));

    public bool SegmentBlocked(Vector2 a, Vector2 b, bool allowGates = true)
    {
        for (int y = (int)MathF.Floor(Math.Min(a.Y, b.Y) - Tolerance); y <= (int)MathF.Floor(Math.Max(a.Y, b.Y) + Tolerance); y++)
            for (int x = (int)MathF.Floor(Math.Min(a.X, b.X) - Tolerance); x <= (int)MathF.Floor(Math.Max(a.X, b.X) + Tolerance); x++)
                if (_spatial.TryGetValue((x, y), out var spans))
                    foreach (var span in spans)
                        if ((!allowGates || !span.IsGate) && Crosses(span, a, b)) return true;
        return false;
    }

    private static bool Crosses(WallNetworkSpan span, Vector2 a, Vector2 b)
    {
        var direction = b - a; var wall = span.B - span.A;
        double cross = Cross(direction, wall);
        if (Math.Abs(cross) < 1e-10)
        {
            return PointSegmentDistance(a, span.A, span.B) <= Tolerance ||
                PointSegmentDistance(b, span.A, span.B) <= Tolerance;
        }
        double t = Cross(span.A - a, wall) / cross;
        double u = Cross(span.A - a, direction) / cross;
        return t >= -Tolerance && t <= 1 + Tolerance && u >= -Tolerance && u <= 1 + Tolerance;
    }

    public static bool PointInside(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if (PointSegmentDistance(point, a, b) <= Tolerance) return false;
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public static float PointSegmentDistance(Vector2 point, Vector2 a, Vector2 b)
    {
        var delta = b - a;
        float square = delta.LengthSquared();
        return Vector2.Distance(point, a + delta * (square > 0 ? Math.Clamp(Vector2.Dot(point - a, delta) / square, 0, 1) : 0));
    }

    public static IReadOnlyList<(float Start, float End)> UncoveredIntervals(Vector2 a, Vector2 b,
        IEnumerable<WallNetworkSpan> existing)
    {
        if (!Finite(a) || !Finite(b) || Vector2.DistanceSquared(a, b) <= Tolerance * Tolerance)
            throw new ArgumentException("Choose two distinct finite wall endpoints.");
        var delta = b - a;
        var covered = new List<(float Start, float End)>();
        foreach (var span in existing)
        {
            if (PointLineDistance(span.A, a, b) > Tolerance || PointLineDistance(span.B, a, b) > Tolerance) continue;
            float t0 = Vector2.Dot(span.A - a, delta) / delta.LengthSquared();
            float t1 = Vector2.Dot(span.B - a, delta) / delta.LengthSquared();
            float start = Math.Clamp(Math.Min(t0, t1), 0, 1), end = Math.Clamp(Math.Max(t0, t1), 0, 1);
            if (end > start + Tolerance) covered.Add((start, end));
        }
        var result = new List<(float Start, float End)>();
        float cursor = 0;
        foreach (var interval in covered.OrderBy(i => i.Start))
        {
            if (interval.Start > cursor + Tolerance) result.Add((cursor, interval.Start));
            cursor = Math.Max(cursor, interval.End);
        }
        if (cursor < 1 - Tolerance) result.Add((cursor, 1));
        return result;
    }

    private static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
    private static double PointLineDistance(Vector2 point, Vector2 a, Vector2 b) =>
        Math.Abs(Cross(point - a, b - a)) / Vector2.Distance(a, b);
    private static void AddProjection(List<float> cuts, Vector2 point, WallNetworkSpan span)
    {
        var delta = span.B - span.A;
        float t = Vector2.Dot(point - span.A, delta) / delta.LengthSquared();
        if (t >= -Tolerance && t <= 1 + Tolerance) cuts.Add(Math.Clamp(t, 0, 1));
    }
}
