using System.Numerics;
using TermCity.Core.World;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

/// <summary>Shared rendered ribbon geometry and a cell index for exact overlay subtraction.</summary>
public sealed class FootpathSurfaceLayout
{
    public sealed record Surface(int Wear, IReadOnlyList<Vector2[]> Polygons);
    public IReadOnlyList<Surface> Surfaces { get; }
    private readonly Dictionary<(int X, int Y), List<Vector2[]>> _cells = [];

    public FootpathSurfaceLayout(GameMap map, RoadMeshLayout? roads)
        : this(FootpathLayout.Strokes(map), roads, map) { }

    public FootpathSurfaceLayout(IEnumerable<FootpathLayout.Stroke> strokes, RoadMeshLayout? roads)
        : this(strokes, roads, null) { }

    private FootpathSurfaceLayout(IEnumerable<FootpathLayout.Stroke> strokes, RoadMeshLayout? roads, GameMap? map)
    {
        var surfaces = new List<Surface>();
        foreach (var stroke in strokes)
        {
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
                            if (!map.IsHouseYardCell(cell) && !TravelRoutes.IsFootpathHillCell(map, cell)) continue;
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

    public static Vector2 TerrainSample(Vector2 point, Vector2 interior)
    {
        var direction = interior - point;
        float length = direction.Length();
        return length > 0 ? point + direction * Math.Min(1, 0.001f / length) : point;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
