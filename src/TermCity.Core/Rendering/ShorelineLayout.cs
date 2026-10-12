using System.Numerics;
using TermCity.Core.World;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class ShorelineLayout
{
    public const double WaterThreshold = 0.38;
    public sealed record Patch(Vector2[] Polygon, bool Water);

    public static float BeachStrength(float x, float y)
    {
        const float region = 12;
        int left = (int)MathF.Floor(x / region), top = (int)MathF.Floor(y / region);
        float tx = x / region - left, ty = y / region - top;
        static float Smooth(float value) => value * value * (3 - 2 * value);
        tx = Smooth(tx); ty = Smooth(ty);
        float a = CellHash.Pick(left + 37, top + 61, 101) / 100f;
        float b = CellHash.Pick(left + 38, top + 61, 101) / 100f;
        float c = CellHash.Pick(left + 37, top + 62, 101) / 100f;
        float d = CellHash.Pick(left + 38, top + 62, 101) / 100f;
        float noise = (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
        return Smooth(Math.Clamp((noise - 0.48f) / 0.2f, 0, 1));
    }

    public static float BeachAmount(GameMap map, float x, float y)
    {
        float inland = Math.Clamp((3.5f - CoastalDistance(map, x, y)) / 0.25f, 0, 1);
        if (inland == 0) return 0;
        float region = Math.Clamp((BeachStrength(x, y) - 0.6f) / 0.1f, 0, 1);
        return inland * region;
    }

    public static float CoastalDistance(GameMap map, float x, float y)
    {
        int left = (int)MathF.Floor(x - 0.5f), top = (int)MathF.Floor(y - 0.5f);
        float tx = x - 0.5f - left, ty = y - 0.5f - top;
        float Distance(int cx, int cy)
        {
            return CoastalCellDistance(map, cx, cy);
        }
        float a = Distance(left, top), b = Distance(left + 1, top);
        float c = Distance(left, top + 1), d = Distance(left + 1, top + 1);
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

    public static float BankAmount(GameMap map, float x, float y) =>
        Math.Clamp(((float)TerrainSprite.WaterSurface(map, x, y) - 0.28f) / 0.1f, 0, 1);

    public static int CoastalCellDistance(GameMap map, int x, int y)
    {
        int value = CoastDistances(map)[map.Index(Math.Clamp(x, 0, map.Width - 1), Math.Clamp(y, 0, map.Height - 1))];
        return value > 0 ? value - 1 : 6;
    }

    private static sbyte[] CoastDistances(GameMap map)
    {
        if (map.CoastDistances is { } cached) return cached;
        var distances = new sbyte[map.Width * map.Height];
        var queue = new Queue<int>();
        foreach (bool sea in new[] { false, true })
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    if (!map.TerrainAt(x, y).Buildable && map.IsSea(x, y) == sea)
                    {
                        int index = map.Index(x, y);
                        distances[index] = sea ? (sbyte)1 : (sbyte)-1;
                        queue.Enqueue(index);
                    }
        while (queue.TryDequeue(out int index))
        {
            int value = distances[index];
            if (Math.Abs(value) >= 5) continue;
            int x = index % map.Width, y = index / map.Width;
            foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(-1, 0), (1, 0), (0, -1), (0, 1)])
            {
                int nx = x + dx, ny = y + dy;
                if (!map.InBounds(nx, ny)) continue;
                int next = map.Index(nx, ny);
                if (distances[next] != 0) continue;
                distances[next] = (sbyte)(value + Math.Sign(value));
                queue.Enqueue(next);
            }
        }
        map.CoastDistances = distances;
        return distances;
    }

    public static bool BeachTouchesCell(GameMap map, int x, int y)
    {
        double center = TerrainSprite.WaterSurface(map, x + 0.5, y + 0.5);
        if (center == 1) return false;
        var distances = CoastDistances(map);
        bool coast = false;
        for (int dy = -1; dy <= 1 && !coast; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int value = distances[map.Index(x + dx, y + dy)];
                if (value > 0 && value <= 5) { coast = true; break; }
            }
        if (!coast) return false;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                if (BeachAmount(map, x + column * 0.5f, y + row * 0.5f) > 0) return true;
        return false;
    }

    public static bool NaturalFeatureFits(GameMap map, int x, int y)
    {
        if (!map.TerrainAt(x, y).AllowsFeatures) return false;
        double center = TerrainSprite.WaterSurface(map, x + 0.5, y + 0.5);
        if (center == 0) return true;
        if (center >= WaterThreshold) return false;
        for (int row = -1; row <= 1; row++)
            for (int column = -1; column <= 1; column++)
                if (TerrainSprite.WaterSurface(map, x + 0.5f + column * 0.4f, y + 0.5f + row * 0.4f) >= WaterThreshold)
                    return false;
        return true;
    }

    public static IReadOnlyList<Patch> Cell(GameMap map, int x, int y)
    {
        Vector2[] corners = [new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1)];
        double center = TerrainSprite.WaterSurface(map, x + 0.5, y + 0.5);
        if (center == 0 || center == 1) return [new(corners, center == 1)];
        var samples = new double[3, 3];
        bool dry = true, wet = true;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                double density = TerrainSprite.WaterSurface(map, x + column * 0.5, y + row * 0.5);
                samples[column, row] = density;
                dry &= density < WaterThreshold;
                wet &= density >= WaterThreshold;
            }
        if (dry || wet) return [new(corners, wet)];
        var patches = new List<Patch>();
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 2; column++)
            {
                Vector2 a = new(x + column * 0.5f, y + row * 0.5f);
                Vector2 b = a + new Vector2(0.5f, 0);
                Vector2 c = a + new Vector2(0.5f, 0.5f);
                Vector2 d = a + new Vector2(0, 0.5f);
                bool wa = samples[column, row] >= WaterThreshold;
                if ((samples[column + 1, row] >= WaterThreshold) == wa &&
                    (samples[column + 1, row + 1] >= WaterThreshold) == wa &&
                    (samples[column, row + 1] >= WaterThreshold) == wa)
                {
                    patches.Add(new([a, b, c, d], wa));
                    continue;
                }
                Split([a, b, c], [samples[column, row], samples[column + 1, row], samples[column + 1, row + 1]]);
                Split([a, c, d], [samples[column, row], samples[column + 1, row + 1], samples[column, row + 1]]);
            }
        return Merge(patches);

        void Split(Vector2[] triangle, double[] densities)
        {
            foreach (bool water in new[] { false, true })
            {
                var polygon = new List<Vector2>();
                for (int i = 0; i < 3; i++)
                {
                    int next = (i + 1) % 3;
                    bool inside = (densities[i] >= WaterThreshold) == water;
                    bool nextInside = (densities[next] >= WaterThreshold) == water;
                    if (inside) polygon.Add(triangle[i]);
                    if (inside != nextInside)
                    {
                        int first = triangle[i].X < triangle[next].X ||
                            triangle[i].X == triangle[next].X && triangle[i].Y < triangle[next].Y ? i : next;
                        int last = first == i ? next : i;
                        float t = (float)((WaterThreshold - densities[first]) / (densities[last] - densities[first]));
                        polygon.Add(Vector2.Lerp(triangle[first], triangle[last], t));
                    }
                }
                if (polygon.Count >= 3) patches.Add(new(polygon.ToArray(), water));
            }
        }

        static IReadOnlyList<Patch> Merge(List<Patch> patches)
        {
            var merged = new List<Patch>();
            foreach (bool water in new[] { false, true })
            {
                var edges = new HashSet<(Vector2 A, Vector2 B)>();
                foreach (var patch in patches)
                {
                    if (patch.Water != water) continue;
                    for (int i = 0; i < patch.Polygon.Length; i++)
                    {
                        var a = patch.Polygon[i]; var b = patch.Polygon[(i + 1) % patch.Polygon.Length];
                        if (a == b) continue;
                        if (!edges.Remove((b, a))) edges.Add((a, b));
                    }
                }
                var next = new Dictionary<Vector2, Vector2>();
                foreach (var edge in edges)
                    if (!next.TryAdd(edge.A, edge.B)) return patches;
                while (next.Count > 0)
                {
                    var start = next.First().Key;
                    var polygon = new List<Vector2>();
                    var point = start;
                    do
                    {
                        polygon.Add(point);
                        if (!next.Remove(point, out var end)) return patches;
                        point = end;
                    } while (point != start);
                    if (polygon.Count < 3) continue;
                    float area = 0;
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        var a = polygon[i] - start; var b = polygon[(i + 1) % polygon.Count] - start;
                        area += a.X * b.Y - a.Y * b.X;
                    }
                    if (area <= 0) return patches;
                    merged.Add(new(polygon.ToArray(), water));
                }
            }
            return merged;
        }
    }
}
