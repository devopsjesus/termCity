using System.Numerics;

namespace TermCity.Core.Rendering;

/// <summary>Terrain overlay patches with actual road and footpath footprints removed.</summary>
public static class ZoneSurfaceLayout
{
    public const float SurfaceLift = 0.018f;
    public readonly record struct SurfaceTriangle(Vector3 A, Vector3 B, Vector3 C);

    public static IReadOnlyList<Vector2[]> Patches(RoadMeshLayout? roads, int x, int y,
        FootpathSurfaceLayout? footpaths = null)
    {
        var patches = new List<Vector2[]>();
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 2; column++)
            {
                float left = x + column / 2f, top = y + row / 2f;
                patches.Add([new(left, top), new(left + 0.5f, top),
                    new(left + 0.5f, top + 0.5f), new(left, top + 0.5f)]);
            }
        if (roads is not null)
            foreach (var segment in roads.AtCell(x, y))
                Remove(segment.Road);
        if (footpaths is not null)
            foreach (var polygon in footpaths.AtCell(x, y))
                Remove(polygon);
        return patches;

        void Remove(Vector2[] cutter)
        {
            var remaining = new List<Vector2[]>();
            foreach (var patch in patches)
                Subtract(patch, cutter, remaining);
            patches = remaining;
        }
    }

    public static IReadOnlyList<SurfaceTriangle> Conform(IReadOnlyList<Vector2[]> patches, IReadOnlyList<Vector3> terrain)
    {
        if (terrain.Count % 3 != 0) throw new ArgumentException("Terrain surfaces require complete triangles.", nameof(terrain));
        var triangles = new List<SurfaceTriangle>();
        for (int face = 0; face + 2 < terrain.Count; face += 3)
        {
            var a = terrain[face]; var b = terrain[face + 1]; var c = terrain[face + 2];
            var origin = new Vector2(a.X, a.Z);
            var ab = new Vector2(b.X - a.X, b.Z - a.Z);
            var ac = new Vector2(c.X - a.X, c.Z - a.Z);
            float cross = ab.X * ac.Y - ab.Y * ac.X;
            if (Math.Abs(cross) < 0.00000001f) continue;
            Vector2[] boundary = [origin, new(b.X, b.Z), new(c.X, c.Z)];
            if (cross < 0) Array.Reverse(boundary);
            foreach (var patch in patches)
            {
                var polygon = patch;
                for (int edge = 0; edge < boundary.Length && polygon.Length >= 3; edge++)
                    polygon = Clip(polygon, boundary[edge], boundary[(edge + 1) % boundary.Length], true);
                if (!HasArea(polygon)) continue;
                for (int vertex = 1; vertex < polygon.Length - 1; vertex++)
                {
                    var first = polygon[vertex] - polygon[0];
                    var second = polygon[vertex + 1] - polygon[0];
                    if (Math.Abs((double)first.X * second.Y - (double)first.Y * second.X) < 0.00000001) continue;
                    triangles.Add(new(Project(polygon[0]), Project(polygon[vertex + 1]), Project(polygon[vertex])));
                }
            }

            Vector3 Project(Vector2 point)
            {
                var offset = point - origin;
                float u = (offset.X * ac.Y - offset.Y * ac.X) / cross;
                float v = (ab.X * offset.Y - ab.Y * offset.X) / cross;
                return new(point.X, a.Y + (b.Y - a.Y) * u + (c.Y - a.Y) * v + SurfaceLift, point.Y);
            }
        }
        return triangles;
    }

    internal static void Subtract(Vector2[] source, Vector2[] cutter, List<Vector2[]> result)
    {
        var intersection = source;
        for (int edge = 0; edge < cutter.Length && intersection.Length >= 3; edge++)
            intersection = Clip(intersection, cutter[edge], cutter[(edge + 1) % cutter.Length], true);
        if (!HasArea(intersection))
        {
            result.Add(source);
            return;
        }
        // Road ribbons and shared footpath pieces are convex. Partition their complement into
        // disjoint convex pieces, so enclosed roads never turn into filled polygon holes.
        var inside = source;
        for (int edge = 0; edge < cutter.Length && inside.Length >= 3; edge++)
        {
            var a = cutter[edge]; var b = cutter[(edge + 1) % cutter.Length];
            var outside = Clip(inside, a, b, false);
            if (HasArea(outside)) result.Add(outside);
            inside = Clip(inside, a, b, true);
        }
    }

    private static bool HasArea(Vector2[] polygon)
    {
        if (polygon.Length < 3) return false;
        double area = 0;
        for (int index = 1; index < polygon.Length - 1; index++)
        {
            var a = polygon[index] - polygon[0]; var b = polygon[index + 1] - polygon[0];
            area += (double)a.X * b.Y - (double)a.Y * b.X;
        }
        return area > 0;
    }

    private static Vector2[] Clip(Vector2[] polygon, Vector2 a, Vector2 b, bool inside)
    {
        var result = new List<Vector2>();
        var edge = b - a;
        float Distance(Vector2 point) => edge.X * (point.Y - a.Y) - edge.Y * (point.X - a.X);
        var previous = polygon[^1];
        float previousDistance = Distance(previous);
        bool previousKept = inside ? previousDistance >= 0 : previousDistance <= 0;
        foreach (var current in polygon)
        {
            float distance = Distance(current);
            bool kept = inside ? distance >= 0 : distance <= 0;
            if (kept != previousKept)
                Add(previous + (current - previous) * (previousDistance / (previousDistance - distance)));
            if (kept) Add(current);
            previous = current;
            previousDistance = distance;
            previousKept = kept;
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result.ToArray();

        void Add(Vector2 point)
        {
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }
    }
}
