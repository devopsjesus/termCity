using TermCity.Core.World;
using TermCity.Core.Simulation;
using System.Numerics;

namespace TermCity.Core.Rendering;

public static class FootpathLayout
{
    public sealed record Stroke(IReadOnlyList<Vector2> Points, int Wear, bool StartsAtRoad = false, bool EndsAtRoad = false);
    public readonly record struct Section(Vector2 Center, Vector2 Left, Vector2 Right);

    public static IReadOnlyList<Stroke> Strokes(GameMap map)
    {
        if (!map.PathLayer.Any(wear => wear > 0)) return [];
        var strokes = new List<Stroke>();
        var covered = new HashSet<int>();
        bool Worn(int cell) => map.PathLayer[cell] > 0 && !map.RoadLayer[cell] && map.BuildingLayer[cell] == 0;
        Vector2 Center(int cell) => new(cell % map.Width + 0.5f, cell / map.Width + 0.5f);
        foreach (var journey in TravelRoutes.FootpathJourneys(map))
        {
            int start = 0;
            while (start < journey.Length)
            {
                if (!Worn(journey[start])) { start++; continue; }
                int end = start;
                while (end + 1 < journey.Length && Worn(journey[end + 1])) end++;
                int wear = 0;
                var points = new List<Vector2>();
                bool startsAtRoad = start > 0 && map.RoadLayer[journey[start - 1]];
                bool endsAtRoad = end + 1 < journey.Length && map.RoadLayer[journey[end + 1]];
                if (start > 0 && (map.RoadLayer[journey[start - 1]] || map.BuildingLayer[journey[start - 1]] != 0))
                    points.Add(startsAtRoad ? Center(journey[start - 1]) :
                        (Center(journey[start - 1]) + Center(journey[start])) / 2);
                for (int index = start; index <= end; index++)
                {
                    points.Add(Center(journey[index]));
                    covered.Add(journey[index]);
                    wear = Math.Max(wear, map.PathLayer[journey[index]]);
                }
                if (end + 1 < journey.Length && (map.RoadLayer[journey[end + 1]] || map.BuildingLayer[journey[end + 1]] != 0))
                    points.Add(endsAtRoad ? Center(journey[end + 1]) :
                        (Center(journey[end]) + Center(journey[end + 1])) / 2);
                strokes.Add(new(Smooth(points), wear, startsAtRoad, endsAtRoad));
                start = end + 1;
            }
        }
        for (int cell = 0; cell < map.PathLayer.Length; cell++)
            if (Worn(cell) && !covered.Contains(cell)) strokes.Add(new([Center(cell)], map.PathLayer[cell]));
        return strokes;
    }

    public static IReadOnlyList<Section> RibbonSections(Stroke stroke, RoadMeshLayout? roads)
    {
        if (stroke.Points.Count == 0) return [];
        var points = stroke.Points.ToArray();
        if (points.Length > 1 && roads is not null)
        {
            if (stroke.StartsAtRoad) points[0] = RoadTarget(points[0], points[1]);
            if (stroke.EndsAtRoad) points[^1] = RoadTarget(points[^1], points[^2]);
        }
        Vector2 RoadTarget(Vector2 roadCell, Vector2 approach)
        {
            var best = roadCell;
            float distance = float.PositiveInfinity;
            foreach (var segment in roads!.AtCell((int)roadCell.X, (int)roadCell.Y))
            {
                var direction = segment.B - segment.A;
                float progress = Math.Clamp(Vector2.Dot(approach - segment.A, direction) / direction.LengthSquared(), 0, 1);
                var candidate = segment.A + direction * progress;
                float candidateDistance = Vector2.DistanceSquared(approach, candidate);
                if (candidateDistance >= distance) continue;
                best = candidate;
                distance = candidateDistance;
            }
            return best;
        }
        Vector2 Contact(Vector2 inside, Vector2 outside)
        {
            if (roads is null || !roads.InRoadInterior(inside)) return inside;
            for (int step = 0; step < 14; step++)
            {
                var middle = (inside + outside) / 2;
                if (roads.InRoadInterior(middle)) inside = middle;
                else outside = middle;
            }
            return (inside + outside) / 2;
        }
        var firstContact = points.Length > 1 ? Contact(points[0], points[1]) : points[0];
        var lastContact = points.Length > 1 ? Contact(points[^1], points[^2]) : points[^1];
        var samples = new List<Vector2> { points[0] };
        for (int index = 1; index < points.Length; index++)
        {
            float distance = Vector2.Distance(points[index - 1], points[index]);
            if (distance < 0.00001f) continue;
            int steps = Math.Max(1, (int)Math.Ceiling(distance / 0.12f));
            for (int step = 1; step <= steps; step++)
                samples.Add(Vector2.Lerp(points[index - 1], points[index], step / (float)steps));
        }
        float halfWidth = stroke.Wear >= 3 ? 0.12f : 0.08f;
        var sections = new List<Section>(samples.Count);
        for (int index = 0; index < samples.Count; index++)
        {
            var before = samples[index] - samples[Math.Max(0, index - 1)];
            var after = samples[Math.Min(samples.Count - 1, index + 1)] - samples[index];
            var tangent = before + after;
            tangent = tangent.LengthSquared() > 0.000001f ? Vector2.Normalize(tangent) : Vector2.UnitX;
            float contactDistance = Math.Min(stroke.StartsAtRoad ? Vector2.Distance(samples[index], firstContact) : float.PositiveInfinity,
                stroke.EndsAtRoad ? Vector2.Distance(samples[index], lastContact) : float.PositiveInfinity);
            float flare = Math.Clamp(1 - contactDistance / 0.45f, 0, 1);
            float radius = halfWidth * (1 + 0.85f * flare * flare * (3 - 2 * flare));
            var normal = new Vector2(-tangent.Y, tangent.X) * radius;
            sections.Add(new(samples[index], samples[index] + normal, samples[index] - normal));
        }
        return sections;
    }

    private static IReadOnlyList<Vector2> Smooth(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3) return points;
        var smooth = new List<Vector2> { points[0] };
        for (int index = 1; index < points.Count - 1; index++)
        {
            var before = points[index] - points[index - 1];
            var after = points[index + 1] - points[index];
            if (Math.Abs(before.X * after.Y - before.Y * after.X) < 0.000001f)
            {
                smooth.Add(points[index]);
                continue;
            }
            var enter = (points[index - 1] + points[index]) / 2;
            var exit = (points[index] + points[index + 1]) / 2;
            smooth.Add(enter);
            for (int step = 1; step <= 4; step++)
            {
                float progress = step / 4f;
                smooth.Add(Vector2.Lerp(Vector2.Lerp(enter, points[index], progress),
                    Vector2.Lerp(points[index], exit, progress), progress));
            }
        }
        smooth.Add(points[^1]);
        return smooth;
    }

    /// <summary>North = 1, east = 2, south = 4, west = 8; paths join neighboring paths, roads and buildings.</summary>
    public static int Connections(GameMap map, int x, int y)
    {
        bool Joins(int cx, int cy) => map.InBounds(cx, cy) &&
            (map.PathWearAt(cx, cy) > 0 || map.HasRoad(cx, cy) || map.BuildingAt(cx, cy) is not null);
        return (Joins(x, y - 1) ? 1 : 0) | (Joins(x + 1, y) ? 2 : 0) |
            (Joins(x, y + 1) ? 4 : 0) | (Joins(x - 1, y) ? 8 : 0);
    }

    public static string Glyph(int connections) => connections switch
    {
        1 or 4 or 5 => "│",
        2 or 8 or 10 => "─",
        3 => "└", 6 => "┌", 9 => "┘", 12 => "┐",
        7 => "├", 11 => "┴", 13 => "┤", 14 => "┬", 15 => "┼",
        _ => "·",
    };
}
