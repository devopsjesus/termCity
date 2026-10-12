using System.Numerics;
using TermCity.Core.Roads;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public sealed class RoadMeshLayout
{
    public const float HalfWidth = 0.3f;
    public const float ShoulderWidth = 0.12f;
    public const float SurfaceLift = 0;
    public const float MinimumTerraceWidth = 0.25f;
    public const float ShallowCutHeight = 0.12f;
    public const float ValleyBridgeDrop = 0.35f;
    public const float MaximumValleyBridgeSpan = 8;

    public sealed record Segment(Vector2 A, Vector2 B, float HeightA, float HeightB, RoadType Type,
        RoadPath Path, Vector2[] Road, Vector2[] Corridor, bool Bridge,
        Vector2 LeftA, Vector2 LeftB, Vector2 RightA, Vector2 RightB)
    {
        public bool Connected => Path.Connected;
    }

    private readonly Dictionary<int, List<Segment>> _cells = [];
    private readonly GameMap _map;
    private readonly Dictionary<Vector2, float> _heights = [];
    public IReadOnlyList<Segment> Segments { get; }

    public RoadMeshLayout(GameMap map, IReadOnlyList<RoadPath> paths)
    {
        _map = map;
        var segments = new List<Segment>();
        foreach (var path in paths)
        {
            var points = new List<Vector2>();
            var heights = new List<float>();
            var water = new List<bool>();
            var raw = Enumerable.Range(0, path.Count).Select(i => (X: path.Xs[i], Y: path.Ys[i])).ToList();
            if (path.Closed) raw.Add(raw[0]);
            var simplified = RoadCurves.Straighten(raw, new bool[raw.Count], 0.2);
            for (int i = 0; i < simplified.Count; i++)
            {
                var end = new Vector2((float)simplified[i].X / RoadCurves.CellWidth,
                    (float)simplified[i].Y / RoadCurves.CellHeight);
                if (i == 0) { Add(end); continue; }
                var start = points[^1];
                int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(start, end)));
                for (int step = 1; step <= steps; step++)
                    Add(Vector2.Lerp(start, end, step / (float)steps));
            }
            (float Height, bool Water) Sample(Vector2 point)
            {
                int x = Math.Clamp((int)MathF.Floor(point.X), 0, map.Width - 1);
                int y = Math.Clamp((int)MathF.Floor(point.Y), 0, map.Height - 1);
                bool isWater = !map.TerrainAt(x, y).Buildable ||
                    TerrainSprite.WaterDensity(map, x, y) > 0 &&
                    TerrainSprite.WaterSurface(map, point.X, point.Y) >= ShorelineLayout.WaterThreshold;
                return (isWater ? 0.17f : TerrainSprite.CoastalLandSurface(map, x, y, point.X, point.Y), isWater);
            }
            void Add(Vector2 point)
            {
                if (points.Count > 0 && Vector2.DistanceSquared(point, points[^1]) < 0.000001f) return;
                points.Add(point);
                var state = Sample(point);
                heights.Add(state.Height);
                water.Add(state.Water);
            }
            if (points.Count == 1)
            {
                var center = points[0];
                points[0] = center - new Vector2(HalfWidth, 0);
                points.Add(center + new Vector2(HalfWidth, 0));
                heights.Add(heights[0]);
                water.Add(water[0]);
            }
            var bridgeSegments = Enumerable.Repeat(false, points.Count - 1).ToList();
            var deckHeights = new Dictionary<int, float>();
            void Span(int first, int last, float left, float right)
            {
                float length = 0;
                for (int i = first + 1; i <= last; i++) length += Vector2.Distance(points[i - 1], points[i]);
                float walked = 0;
                for (int i = first; i <= last; i++)
                {
                    if (i > first) { walked += Vector2.Distance(points[i - 1], points[i]); bridgeSegments[i - 1] = true; }
                    deckHeights[i] = left + (right - left) * (length > 0 ? walked / length : 0);
                }
            }
            for (int i = 0; i < points.Count; i++)
            {
                if (!water[i]) continue;
                int end = i;
                while (end + 1 < points.Count && water[end + 1]) end++;
                int first = Math.Max(0, i - 1), last = Math.Min(points.Count - 1, end + 1);
                Span(first, last, Math.Max(0.17f, heights[first]) + 0.06f, Math.Max(0.17f, heights[last]) + 0.06f);
                i = end;
            }
            for (int first = 0; first < points.Count - 2; first++)
            {
                if (water[first] || heights[first] < TerrainSprite.HillBaseHeight) continue;
                float length = 0, lowest = heights[first];
                for (int last = first + 1; last < points.Count; last++)
                {
                    length += Vector2.Distance(points[last - 1], points[last]);
                    if (length > MaximumValleyBridgeSpan) break;
                    lowest = Math.Min(lowest, heights[last]);
                    if (water[last]) break;
                    if (last - first < 2 || Math.Min(heights[first], heights[last]) - lowest < ValleyBridgeDrop)
                        continue;
                    Span(first, last, heights[first] + 0.06f, heights[last] + 0.06f);
                    first = last - 1;
                    break;
                }
            }
            var terrainHeights = heights.ToArray();
            for (int pass = 0; pass < 4; pass++)
            {
                var smoothed = heights.ToArray();
                for (int i = 1; i < heights.Count - 1; i++)
                    smoothed[i] = (heights[i - 1] + heights[i] * 2 + heights[i + 1]) / 4;
                for (int i = 0; i < heights.Count; i++) heights[i] = smoothed[i];
                if (path.Closed) heights[0] = heights[^1] = (heights[0] + heights[^1]) / 2;
            }
            for (int i = 1; i < heights.Count - 1; i++)
                if (terrainHeights[i - 1] == terrainHeights[i] && terrainHeights[i + 1] == terrainHeights[i] &&
                    Math.Abs(heights[i] - terrainHeights[i]) < ShallowCutHeight / 2)
                    heights[i] = terrainHeights[i];
            foreach (var (index, height) in deckHeights) heights[index] = height;
            for (int i = 1; i < points.Count - 1;)
            {
                var before = points[i] - points[i - 1];
                var after = points[i + 1] - points[i];
                if (heights[i - 1] == heights[i] && heights[i + 1] == heights[i] &&
                    bridgeSegments[i - 1] == bridgeSegments[i] &&
                    Vector2.Distance(points[i - 1], points[i + 1]) <= 6 &&
                    Math.Abs(before.X * after.Y - before.Y * after.X) < 0.0001f * Math.Max(before.Length(), after.Length()) &&
                    Vector2.Dot(before, after) > 0)
                {
                    points.RemoveAt(i);
                    heights.RemoveAt(i);
                    bridgeSegments.RemoveAt(i);
                }
                else i++;
            }
            var offsets = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 before = points[i] - points[Math.Max(0, i - 1)];
                Vector2 after = points[Math.Min(points.Count - 1, i + 1)] - points[i];
                if (path.Closed && (i == 0 || i == points.Count - 1))
                {
                    before = points[0] - points[^2];
                    after = points[1] - points[0];
                }
                if (before.LengthSquared() < 0.000001f) before = after;
                if (after.LengthSquared() < 0.000001f) after = before;
                var n1 = Perpendicular(Vector2.Normalize(before));
                var n2 = Perpendicular(Vector2.Normalize(after));
                var sum = n1 + n2;
                var normal = sum.LengthSquared() < 0.000001f ? n2 : Vector2.Normalize(sum);
                offsets[i] = normal / Math.Max(0.5f, Vector2.Dot(normal, n2));
            }
            for (int i = 1; i < points.Count; i++)
            {
                Vector2[] Ribbon(float width) => Hull(
                [
                    points[i - 1] + offsets[i - 1] * width, points[i] + offsets[i] * width,
                    points[i] - offsets[i] * width, points[i - 1] - offsets[i - 1] * width,
                ]);
                var segment = new Segment(points[i - 1], points[i], heights[i - 1], heights[i],
                    path.Type, path, Ribbon(HalfWidth), Ribbon(HalfWidth + ShoulderWidth), bridgeSegments[i - 1],
                    points[i - 1] + offsets[i - 1] * (HalfWidth + ShoulderWidth),
                    points[i] + offsets[i] * (HalfWidth + ShoulderWidth),
                    points[i - 1] - offsets[i - 1] * (HalfWidth + ShoulderWidth),
                    points[i] - offsets[i] * (HalfWidth + ShoulderWidth));
                segments.Add(segment);
                const float reach = 1.01f;
                int left = Math.Max(0, (int)MathF.Floor(Math.Min(segment.A.X, segment.B.X) - reach));
                int right = Math.Min(map.Width - 1, (int)MathF.Floor(Math.Max(segment.A.X, segment.B.X) + reach));
                int top = Math.Max(0, (int)MathF.Floor(Math.Min(segment.A.Y, segment.B.Y) - reach));
                int bottom = Math.Min(map.Height - 1, (int)MathF.Floor(Math.Max(segment.A.Y, segment.B.Y) + reach));
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        int cell = map.Index(x, y);
                        if (!_cells.TryGetValue(cell, out var nearby)) _cells.Add(cell, nearby = []);
                        nearby.Add(segment);
                    }
            }
        }
        Segments = segments;
    }

    public IReadOnlyList<Segment> AtCell(int x, int y) =>
        _map.InBounds(x, y) && _cells.TryGetValue(_map.Index(x, y), out var segments) ? segments : Array.Empty<Segment>();

    public Segment? RoadAt(Vector2 point)
    {
        Segment? best = null;
        foreach (var segment in AtCell(Math.Clamp((int)MathF.Floor(point.X), 0, _map.Width - 1),
            Math.Clamp((int)MathF.Floor(point.Y), 0, _map.Height - 1)))
            if (ContainsRibbon(segment.Road, point) && (best is null || segment.Type.Rank > best.Type.Rank ||
                segment.Type.Rank == best.Type.Rank && segment.Bridge && !best.Bridge))
                best = segment;
        return best;
    }

    public bool InCorridor(Vector2 point) => AtCell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y))
        .Any(segment => ContainsRibbon(segment.Corridor, point) || ContainsRibbon(segment.Road, point));

    public bool InGroundCorridor(Vector2 point) => AtCell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y))
        .Any(segment => !segment.Bridge && (ContainsRibbon(segment.Corridor, point) || ContainsRibbon(segment.Road, point)));

    public bool InRoadInterior(Vector2 point) => RoadAt(point + new Vector2(0.001f, 0)) is not null &&
        RoadAt(point - new Vector2(0.001f, 0)) is not null &&
        RoadAt(point + new Vector2(0, 0.001f)) is not null &&
        RoadAt(point - new Vector2(0, 0.001f)) is not null;

    public float HeightAt(Vector2 point)
    {
        if (!TryHeightAt(point, out float height))
            throw new InvalidOperationException($"Road surface point {point} must have a nearby centerline.");
        return height;
    }

    public float SupportedShoulderHeight(Vector2 point, float terrainHeight)
    {
        float grade = HeightAt(point);
        if (grade == terrainHeight) return grade;
        var nearby = AtCell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));
        if (nearby.Any(segment => ContainsRibbon(segment.Road, point) ||
            segment.Bridge && ContainsRibbon(segment.Corridor, point))) return grade;
        float distanceSquared = float.PositiveInfinity;
        foreach (var segment in nearby)
            for (int i = 0; i < segment.Road.Length; i++)
            {
                var a = segment.Road[i]; var delta = segment.Road[(i + 1) % segment.Road.Length] - a;
                float t = Math.Clamp(Vector2.Dot(point - a, delta) / delta.LengthSquared(), 0, 1);
                distanceSquared = Math.Min(distanceSquared, Vector2.DistanceSquared(point, a + delta * t));
            }
        float blend = Math.Clamp(MathF.Sqrt(distanceSquared) / ShoulderWidth, 0, 1);
        float difference = terrainHeight - grade;
        float taper = Math.Clamp((Math.Abs(difference) - ShallowCutHeight) / ShallowCutHeight, 0, 1);
        taper = taper * taper * (3 - 2 * taper);
        return grade + Math.Clamp(difference, -ShallowCutHeight, ShallowCutHeight) * (1 - taper) * blend;
    }

    public bool TryHeightAt(Vector2 point, out float height)
    {
        if (_heights.TryGetValue(point, out height)) return true;
        height = 0;
        float sum = 0, weight = 0;
        var nearby = AtCell(Math.Clamp((int)MathF.Floor(point.X), 0, _map.Width - 1),
            Math.Clamp((int)MathF.Floor(point.Y), 0, _map.Height - 1));
        bool bridge = nearby.Any(segment => segment.Bridge && ContainsRibbon(segment.Corridor, point));
        if (nearby.Count > 0)
        {
            float baseline = nearby.First(segment => !bridge || segment.Bridge).HeightA;
            bool uniform = true;
            foreach (var segment in nearby)
            {
                if (bridge && !segment.Bridge) continue;
                if (segment.HeightA != baseline || segment.HeightB != baseline) { uniform = false; break; }
            }
            if (uniform)
            {
                height = baseline + SurfaceLift;
                if (_heights.Count > 200_000) _heights.Clear();
                _heights[point] = height;
                return true;
            }
        }
        foreach (var segment in nearby)
        {
            if (bridge && !segment.Bridge) continue;
            var delta = segment.B - segment.A;
            float t = Math.Clamp(Vector2.Dot(point - segment.A, delta) / delta.LengthSquared(), 0, 1);
            float distance = Vector2.DistanceSquared(point, segment.A + delta * t);
            if (distance > 1) continue;
            float influence = (1 - distance) * (1 - distance) / (0.04f + distance);
            sum += (segment.HeightA + (segment.HeightB - segment.HeightA) * t) * influence;
            weight += influence;
        }
        if (weight == 0) return false;
        height = sum / weight + SurfaceLift;
        if (_heights.Count > 200_000) _heights.Clear();
        _heights[point] = height;
        return true;
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            var edge = b - a;
            float length = edge.LengthSquared();
            if (length > 0)
            {
                float t = Math.Clamp(Vector2.Dot(point - a, edge) / length, 0, 1);
                if (Vector2.DistanceSquared(point, a + edge * t) < 0.00000004f) return true;
            }
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static bool IsThinRemnant(IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count < 3) throw new ArgumentException("A terrace polygon needs at least three vertices.", nameof(polygon));
        for (int i = 0; i < polygon.Count; i++)
        {
            var edge = polygon[(i + 1) % polygon.Count] - polygon[i];
            if (edge.LengthSquared() < 0.00000001f) continue;
            var normal = Perpendicular(Vector2.Normalize(edge));
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var p in polygon)
            {
                float projection = Vector2.Dot(p - polygon[i], normal);
                min = Math.Min(min, projection); max = Math.Max(max, projection);
            }
            if (max - min < MinimumTerraceWidth) return true;
        }
        return false;
    }

    public static Vector2[] RoundedTerraceCorner(Vector2 before, Vector2 corner, Vector2 after)
    {
        var incoming = before - corner;
        var outgoing = after - corner;
        if (incoming.LengthSquared() < 0.000001f || outgoing.LengthSquared() < 0.000001f)
            throw new ArgumentException("A terrace join needs two nonzero edges.");
        float reach = Math.Min(0.35f, Math.Min(incoming.Length(), outgoing.Length()) * 0.45f);
        var start = corner + Vector2.Normalize(incoming) * reach;
        var end = corner + Vector2.Normalize(outgoing) * reach;
        var clearance = new Vector2[10];
        clearance[0] = corner;
        for (int i = 0; i <= 8; i++)
        {
            float t = i / 8f;
            clearance[i + 1] = (1 - t) * (1 - t) * start + 2 * (1 - t) * t * corner + t * t * end;
        }
        return clearance;
    }

    private static Vector2 Perpendicular(Vector2 direction) => new(-direction.Y, direction.X);

    private static bool ContainsRibbon(Vector2[] polygon, Vector2 point)
    {
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var edge = polygon[(i + 1) % polygon.Length] - a;
            var offset = point - a;
            if (edge.X * offset.Y - edge.Y * offset.X < -0.0002f * Math.Max(Math.Abs(edge.X), Math.Abs(edge.Y)))
                return false;
        }
        return true;
    }

    private static Vector2[] Hull(Vector2[] points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var hull = new List<Vector2>();
        static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            while (hull.Count > lower && Cross(hull[^2], hull[^1], sorted[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull.ToArray();
    }
}
