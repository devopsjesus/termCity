using System.Numerics;
using TermCity.Core.World;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

/// <summary>Shared rendered ribbon geometry and a cell index for exact overlay subtraction.</summary>
public sealed class FootpathSurfaceLayout
{
    public sealed record Surface(int Wear, IReadOnlyList<Vector2[]> Polygons);
    public IReadOnlyList<Surface> Surfaces { get; }
    private readonly Dictionary<(int X, int Y), List<Vector2[]>> _cells = [];
    private Dictionary<(int X, int Y), List<(Vector2 A, Vector2 B)>>? _boundaryCells;
    private readonly HashSet<Vector2[]> _boundaryPolygons = [];
    private readonly HashSet<(Vector2 A, Vector2 B)> _boundaryInputEdges = [];
    private readonly HashSet<(long AX, long AY, long BX, long BY)> _boundarySegments = [];

    public FootpathSurfaceLayout(GameMap map, RoadMeshLayout? roads)
        : this(FootpathLayout.Strokes(map), roads, map) { }

    public FootpathSurfaceLayout(GameMap map, RoadMeshLayout? roads, CellRect visible)
        : this(FootpathLayout.Strokes(map), roads, map, visible) { }

    public FootpathSurfaceLayout(GameMap map, IReadOnlyList<FootpathLayout.Stroke> strokes,
        RoadMeshLayout? roads, CellRect visible) : this(strokes, roads, map, visible) { }

    public FootpathSurfaceLayout(IEnumerable<FootpathLayout.Stroke> strokes, RoadMeshLayout? roads)
        : this(strokes, roads, null) { }

    private FootpathSurfaceLayout(IEnumerable<FootpathLayout.Stroke> strokes, RoadMeshLayout? roads, GameMap? map,
        CellRect? visible = null)
    {
        var surfaces = new List<Surface>();
        foreach (var stroke in strokes)
        {
            if (visible is { } view && (stroke.Points.Max(point => point.X) < view.X - 2 ||
                stroke.Points.Min(point => point.X) > view.Right + 3 ||
                stroke.Points.Max(point => point.Y) < view.Y - 2 || stroke.Points.Min(point => point.Y) > view.Bottom + 3))
                continue;
            var sections = FootpathLayout.RibbonSections(stroke, roads);
            var polygons = new List<Vector2[]>();
            for (int index = 1; index < sections.Count; index++)
            {
                var a = sections[index - 1].Left;
                var b = sections[index - 1].Right;
                var c = sections[index].Right;
                var d = sections[index].Left;
                // Select an interior diagonal, including on tight concave turns.
                if (Cross(a, b, c) >= 0 && Cross(a, c, d) >= 0)
                {
                    Add([a, b, c]);
                    Add([a, c, d]);
                }
                else
                {
                    Add([a, b, d]);
                    Add([b, c, d]);
                }
            }
            if (sections.Count > 0)
            {
                if (!stroke.StartsAtRoad) Cap(sections[0]);
                if (!stroke.EndsAtRoad && sections.Count > 1) Cap(sections[^1]);
            }
            surfaces.Add(new(stroke.Wear, polygons));

            void Cap(FootpathLayout.Section section)
            {
                float radius = Vector2.Distance(section.Left, section.Right) / 2;
                Add(Enumerable.Range(0, 12).Select(step => section.Center +
                    new Vector2(MathF.Cos(step * MathF.Tau / 12), MathF.Sin(step * MathF.Tau / 12)) * radius).ToArray());
            }

            void Add(Vector2[] polygon)
            {
                float area = 0;
                for (int i = 1; i < polygon.Length - 1; i++)
                    area += Cross(polygon[0], polygon[i], polygon[i + 1]);
                if (area == 0) return;
                if (area < 0) Array.Reverse(polygon);
                int left = (int)MathF.Floor(polygon.Min(p => p.X));
                int right = (int)MathF.Floor(polygon.Max(p => p.X));
                int top = (int)MathF.Floor(polygon.Min(p => p.Y));
                int bottom = (int)MathF.Floor(polygon.Max(p => p.Y));
                if (visible is { } bounds && (right < bounds.X - 2 || left > bounds.Right + 2 ||
                    bottom < bounds.Y - 2 || top > bounds.Bottom + 2)) return;
                var pieces = new List<Vector2[]> { polygon };
                if (roads is not null)
                {
                    var bridges = new HashSet<RoadMeshLayout.Segment>();
                    for (int y = top; y <= bottom; y++)
                        for (int x = left; x <= right; x++)
                            foreach (var segment in roads.AtCell(x, y))
                                if (segment.Bridge) bridges.Add(segment);
                    foreach (var bridge in bridges)
                    {
                        var remaining = new List<Vector2[]>();
                        foreach (var piece in pieces) ZoneSurfaceLayout.Subtract(piece, bridge.Corridor, remaining);
                        pieces = remaining;
                    }
                }
                if (map is not null)
                    for (int y = Math.Max(0, top); y <= Math.Min(map.Height - 1, bottom); y++)
                        for (int x = Math.Max(0, left); x <= Math.Min(map.Width - 1, right); x++)
                        {
                            int cell = map.Index(x, y);
                            if (map.BuildingLayer[cell] == 0 && !map.IsHouseYardCell(cell) && !TravelRoutes.IsFootpathHillCell(map, cell)) continue;
                            Vector2[] cutter = [new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1)];
                            var remaining = new List<Vector2[]>();
                            foreach (var piece in pieces) ZoneSurfaceLayout.Subtract(piece, cutter, remaining);
                            pieces = remaining;
                        }
                foreach (var piece in pieces) Store(piece);
            }

            void Store(Vector2[] polygon)
            {
                polygons.Add(polygon);
                int left = (int)MathF.Floor(polygon.Min(p => p.X));
                int right = (int)MathF.Floor(polygon.Max(p => p.X));
                int top = (int)MathF.Floor(polygon.Min(p => p.Y));
                int bottom = (int)MathF.Floor(polygon.Max(p => p.Y));
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        if (!_cells.TryGetValue((x, y), out var cell))
                            _cells.Add((x, y), cell = []);
                        cell.Add(polygon);
                    }
            }
        }
        Surfaces = surfaces;
    }

    public IReadOnlyList<Vector2[]> AtCell(int x, int y) =>
        _cells.TryGetValue((x, y), out var polygons) ? polygons : [];

    public float BoundaryDistance(Vector2 point, float maximum)
    {
        if (!float.IsFinite(maximum) || maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        EnsureBoundary(point, maximum);
        float distance = maximum;
        for (int y = (int)MathF.Floor(point.Y - maximum); y <= (int)MathF.Floor(point.Y + maximum); y++)
            for (int x = (int)MathF.Floor(point.X - maximum); x <= (int)MathF.Floor(point.X + maximum); x++)
                if (_boundaryCells!.TryGetValue((x, y), out var edges))
                    foreach (var edge in edges)
                        distance = Math.Min(distance, World.WallNetwork.PointSegmentDistance(point, edge.A, edge.B));
        return distance;
    }

    private void EnsureBoundary(Vector2 query, float maximum)
    {
        _boundaryCells ??= [];
        bool Covered(Vector2 sample)
        {
            foreach (var polygon in AtCell((int)MathF.Floor(sample.X), (int)MathF.Floor(sample.Y)))
                if (World.WallNetwork.PointInside(polygon, sample)) return true;
            return false;
        }
        static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
        var local = new HashSet<Vector2[]>();
        for (int y = (int)MathF.Floor(query.Y - maximum); y <= (int)MathF.Floor(query.Y + maximum); y++)
            for (int x = (int)MathF.Floor(query.X - maximum); x <= (int)MathF.Floor(query.X + maximum); x++)
                foreach (var polygon in AtCell(x, y))
                    if (_boundaryPolygons.Add(polygon)) local.Add(polygon);
        foreach (var polygon in local)
            for (int edge = 0; edge < polygon.Length; edge++)
            {
                var a = polygon[edge]; var b = polygon[(edge + 1) % polygon.Length];
                var canonical = a.X < b.X || a.X == b.X && a.Y <= b.Y ? (a, b) : (b, a);
                if (!_boundaryInputEdges.Add(canonical)) continue;
                var delta = b - a;
                float length = delta.Length();
                if (length <= .00001f) continue;
                var cuts = new List<float> { 0, 1 };
                var neighbors = new HashSet<Vector2[]>();
                for (int y = (int)MathF.Floor(Math.Min(a.Y, b.Y)); y <= (int)MathF.Floor(Math.Max(a.Y, b.Y)); y++)
                    for (int x = (int)MathF.Floor(Math.Min(a.X, b.X)); x <= (int)MathF.Floor(Math.Max(a.X, b.X)); x++)
                        neighbors.UnionWith(AtCell(x, y));
                foreach (var neighbor in neighbors)
                    for (int i = 0; i < neighbor.Length; i++)
                    {
                        var c = neighbor[i]; var d = neighbor[(i + 1) % neighbor.Length];
                        var other = d - c;
                        double cross = Cross(delta, other);
                        if (Math.Abs(cross) > 1e-10)
                        {
                            double t = Cross(c - a, other) / cross, u = Cross(c - a, delta) / cross;
                            if (t > 0 && t < 1 && u >= -.00001 && u <= 1.00001) cuts.Add((float)t);
                        }
                        else if (Math.Abs(Cross(c - a, delta)) < .000001)
                            foreach (var point in new[] { c, d })
                            {
                                float t = Vector2.Dot(point - a, delta) / (length * length);
                                if (t > 0 && t < 1) cuts.Add(t);
                            }
                    }
                var sorted = cuts.Order().ToArray();
                var normal = new Vector2(-delta.Y, delta.X) * (.0002f / length);
                for (int i = 1; i < sorted.Length; i++)
                {
                    if ((sorted[i] - sorted[i - 1]) * length < .00001f) continue;
                    var first = a + delta * sorted[i - 1]; var last = a + delta * sorted[i];
                    var midpoint = (first + last) / 2;
                    if (Covered(midpoint + normal) == Covered(midpoint - normal)) continue;
                    if (first.X > last.X || first.X == last.X && first.Y > last.Y) (first, last) = (last, first);
                    var key = ((long)Math.Round(first.X * 10000), (long)Math.Round(first.Y * 10000),
                        (long)Math.Round(last.X * 10000), (long)Math.Round(last.Y * 10000));
                    if (!_boundarySegments.Add(key)) continue;
                    for (int y = (int)MathF.Floor(Math.Min(first.Y, last.Y)); y <= (int)MathF.Floor(Math.Max(first.Y, last.Y)); y++)
                        for (int x = (int)MathF.Floor(Math.Min(first.X, last.X)); x <= (int)MathF.Floor(Math.Max(first.X, last.X)); x++)
                        {
                            if (!_boundaryCells.TryGetValue((x, y), out var edges)) _boundaryCells.Add((x, y), edges = []);
                            edges.Add((first, last));
                        }
                }
            }
    }

    public static Vector2 TerrainSample(Vector2 point, Vector2 interior)
    {
        var direction = interior - point;
        float length = direction.Length();
        return length > 0 ? point + direction * Math.Min(1, 0.001f / length) : point;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
