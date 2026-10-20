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
        var edges = new HashSet<(int First, int Last)>();
        var parents = new Dictionary<int, int>();
        var horizontalRuns = new Dictionary<int, int>();
        var verticalRuns = new Dictionary<int, int>();
        bool Worn(int cell) => map.PathLayer[cell] > 0 && !TravelRoutes.IsPathRoadTarget(map, cell) &&
            !TravelRoutes.IsFootpathBridgeCell(map, cell) && !TravelRoutes.IsFootpathHillCell(map, cell) &&
            map.BuildingLayer[cell] == 0 && !map.IsHouseYardCell(cell) &&
            map.TerrainAt(cell % map.Width, cell / map.Width).Buildable;
        bool Property(int cell) => map.BuildingLayer[cell] != 0 || map.IsHouseYardCell(cell);
        Vector2 Center(int cell) => new(cell % map.Width + 0.5f, cell / map.Width + 0.5f);
        (int First, int Last) Edge(int first, int last) => first < last ? (first, last) : (last, first);
        int Root(int cell)
        {
            if (map.BuildingLayer[cell] != 0) cell = map.BuildingAnchor(cell);
            else if (map.HouseYardOwner(cell) is >= 0 and var owner) cell = owner;
            if (!parents.TryGetValue(cell, out int parent)) return parents[cell] = cell;
            while (parent != parents[parent]) parent = parents[parent] = parents[parents[parent]];
            return parents[cell] = parent;
        }
        bool Join(int first, int last)
        {
            int a = Root(first), b = Root(last);
            if (a == b) return false;
            parents[b] = a;
            return true;
        }
        void JourneyEdge(int first, int last)
        {
            edges.Add(Edge(first, last));
            Join(first, last);
        }
        int RunLength(int cell, bool horizontal)
        {
            if (!Worn(cell)) return 0;
            var runs = horizontal ? horizontalRuns : verticalRuns;
            if (runs.TryGetValue(cell, out int cached)) return cached;
            int dx = horizontal ? 1 : 0, dy = horizontal ? 0 : 1;
            var start = map.PosOf(cell);
            while (map.InBounds(start.X - dx, start.Y - dy) &&
                Worn(map.Index(start.X - dx, start.Y - dy))) start = start.Offset(-dx, -dy);
            var chain = new List<int>();
            while (map.InBounds(start) && Worn(map.Index(start.X, start.Y)))
            {
                chain.Add(map.Index(start.X, start.Y));
                start = start.Offset(dx, dy);
            }
            foreach (int member in chain) runs[member] = chain.Count;
            return chain.Count;
        }
        bool MayLink(int first, int last)
        {
            if ((map.PathLinkLayer[first] | map.PathLinkLayer[last]) != 0)
                return map.HasPathLink(first, last);
            bool horizontal = first / map.Width == last / map.Width;
            return !Worn(first) || !Worn(last) ||
                RunLength(first, !horizontal) <= RunLength(first, horizontal) ||
                RunLength(last, !horizontal) <= RunLength(last, horizontal);
        }
        foreach (var journey in TravelRoutes.FootpathJourneys(map))
        {
            int start = 0;
            while (start < journey.Length)
            {
                if (!Worn(journey[start])) { start++; continue; }
                int end = start;
                while (end + 1 < journey.Length && Worn(journey[end + 1]) &&
                    TravelRoutes.CanFootpathStep(map, journey[end], journey[end + 1])) end++;
                int wear = 0;
                var points = new List<Vector2>();
                bool startsAtRoad = start > 0 && TravelRoutes.IsPathRoadTarget(map, journey[start - 1]) &&
                    TravelRoutes.CanFootpathStep(map, journey[start - 1], journey[start]);
                bool endsAtRoad = end + 1 < journey.Length && TravelRoutes.IsPathRoadTarget(map, journey[end + 1]) &&
                    TravelRoutes.CanFootpathStep(map, journey[end], journey[end + 1]);
                if (start > 0 && TravelRoutes.CanFootpathStep(map, journey[start - 1], journey[start]) &&
                    (startsAtRoad || Property(journey[start - 1])))
                {
                    points.Add(startsAtRoad ? Center(journey[start - 1]) :
                        (Center(journey[start - 1]) + Center(journey[start])) / 2);
                    JourneyEdge(journey[start - 1], journey[start]);
                }
                for (int index = start; index <= end; index++)
                {
                    points.Add(Center(journey[index]));
                    if (index > start) JourneyEdge(journey[index - 1], journey[index]);
                    covered.Add(journey[index]);
                    wear = Math.Max(wear, map.PathLayer[journey[index]]);
                }
                if (end + 1 < journey.Length && TravelRoutes.CanFootpathStep(map, journey[end], journey[end + 1]) &&
                    (endsAtRoad || Property(journey[end + 1])))
                {
                    points.Add(endsAtRoad ? Center(journey[end + 1]) :
                        (Center(journey[end]) + Center(journey[end + 1])) / 2);
                    JourneyEdge(journey[end], journey[end + 1]);
                }
                if (points.Count > 1) strokes.Add(new(Smooth(points), wear, startsAtRoad, endsAtRoad));
                start = end + 1;
            }
        }
        var residentialOnly = new HashSet<int>();
        var visited = new HashSet<int>();
        for (int cell = 0; cell < map.PathLayer.Length; cell++)
        {
            if (!Worn(cell) || covered.Contains(cell) || !visited.Add(cell)) continue;
            var component = new List<int> { cell };
            var queue = new Queue<int>();
            queue.Enqueue(cell);
            var residences = new HashSet<int>();
            bool other = false, road = false;
            while (queue.TryDequeue(out int current))
            {
                var p = map.PosOf(current);
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    if (!map.InBounds(p.X + dx, p.Y + dy)) continue;
                    int neighbor = map.Index(p.X + dx, p.Y + dy);
                    if (!TravelRoutes.CanFootpathStep(map, current, neighbor) || !MayLink(current, neighbor)) continue;
                    if (TravelRoutes.IsPathRoadTarget(map, neighbor)) road = true;
                    if (map.BuildingAt(p.X + dx, p.Y + dy) is { } building)
                    {
                        if (building.Zone == ZoneType.Residential) residences.Add(map.BuildingAnchor(neighbor));
                        else other = true;
                    }
                    else if (map.HouseYardOwner(neighbor) is >= 0 and var owner) residences.Add(owner);
                    if (!Worn(neighbor) || !visited.Add(neighbor)) continue;
                    component.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            if (!other && !road && residences.Count >= 2)
                residentialOnly.UnionWith(component);
        }
        IEnumerable<int> Candidates(int cell)
        {
            var p = map.PosOf(cell);
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
                if (map.InBounds(p.X + dx, p.Y + dy))
                {
                    int next = map.Index(p.X + dx, p.Y + dy);
                    if (TravelRoutes.CanFootpathStep(map, cell, next) &&
                        MayLink(cell, next) &&
                        !TravelRoutes.IsRoadsidePathStep(map, cell, next) &&
                        (Worn(next) && !residentialOnly.Contains(next) ||
                            TravelRoutes.IsPathRoadTarget(map, next) || Property(next))) yield return next;
                }
        }
        var candidates = new HashSet<(int First, int Last)>();
        for (int cell = 0; cell < map.PathLayer.Length; cell++)
            if (Worn(cell) && !covered.Contains(cell) && !residentialOnly.Contains(cell))
                foreach (int next in Candidates(cell)) candidates.Add(Edge(cell, next));
        var adjacency = new Dictionary<int, List<int>>();
        void AddConnection(Dictionary<int, List<int>> graph, int first, int last)
        {
            if (!graph.TryGetValue(first, out var neighbors)) graph.Add(first, neighbors = []);
            neighbors.Add(last);
        }
        foreach (var edge in candidates.OrderBy(edge => edge.First).ThenBy(edge => edge.Last))
        {
            AddConnection(adjacency, edge.First, edge.Last);
            AddConnection(adjacency, edge.Last, edge.First);
        }
        bool Junction(int cell) => covered.Contains(cell) || adjacency[cell].Count != 2 || !Worn(cell);
        var corridors = new List<List<int>>();
        var traced = new HashSet<(int First, int Last)>();
        foreach (int start in adjacency.Keys.OrderBy(cell => Junction(cell) ? 0 : 1).ThenBy(cell => cell))
            foreach (int neighbor in adjacency[start])
            {
                if (!traced.Add(Edge(start, neighbor))) continue;
                var chain = new List<int> { start, neighbor };
                int previous = start, current = neighbor;
                while (!Junction(current) && current != start)
                {
                    var options = adjacency[current];
                    int next = options[0] == previous ? options[1] : options[0];
                    if (!traced.Add(Edge(current, next))) break;
                    chain.Add(next);
                    previous = current; current = next;
                }
                if (chain[^1] == chain[0]) chain.RemoveAt(chain.Count - 1);
                corridors.Add(chain);
            }
        var connections = new Dictionary<int, List<int>>();
        foreach (var chain in corridors
            .OrderByDescending(chain => Enumerable.Range(1, chain.Count - 1)
                .All(index => map.HasPathLink(chain[index - 1], chain[index])))
            .ThenByDescending(chain => Enumerable.Range(1, chain.Count - 1).Max(index =>
                RunLength(chain[index], chain[index - 1] / map.Width == chain[index] / map.Width)))
            .ThenBy(chain => chain[0]).ThenBy(chain => chain[^1]))
        {
            if (!Join(chain[0], chain[^1])) continue;
            for (int index = 1; index < chain.Count; index++)
            {
                int first = chain[index - 1], last = chain[index];
                Join(first, last);
                AddConnection(connections, first, last);
                AddConnection(connections, last, first);
            }
        }
        IEnumerable<int> Neighbors(int cell) => connections.TryGetValue(cell, out var neighbors) ? neighbors : [];
        bool Mark(int first, int last) => edges.Add(Edge(first, last));
        for (int cell = 0; cell < map.PathLayer.Length; cell++)
        {
            if (!Worn(cell) || covered.Contains(cell) || residentialOnly.Contains(cell)) continue;
            foreach (int neighbor in Neighbors(cell))
            {
                if (!Mark(cell, neighbor)) continue;
                var points = new List<Vector2> { Center(cell) };
                int previous = cell, current = neighbor, wear = map.PathLayer[cell];
                bool endsAtRoad = false;
                while (true)
                {
                    if (!Worn(current))
                    {
                        endsAtRoad = TravelRoutes.IsPathRoadTarget(map, current);
                        points.Add(endsAtRoad ? Center(current) : (Center(previous) + Center(current)) / 2);
                        break;
                    }
                    points.Add(Center(current));
                    wear = Math.Max(wear, map.PathLayer[current]);
                    var next = Neighbors(current).Where(n => n != previous).ToArray();
                    if (covered.Contains(current) || next.Length != 1 || !Mark(current, next[0])) break;
                    previous = current;
                    current = next[0];
                }
                strokes.Add(new(Smooth(points), wear, false, endsAtRoad));
            }
        }
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
                if (segment.Bridge) continue;
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
            TravelRoutes.CanFootpathStep(map, map.Index(x, y), map.Index(cx, cy)) &&
            ((map.PathLinkLayer[map.Index(x, y)] | map.PathLinkLayer[map.Index(cx, cy)]) == 0 ||
                map.HasPathLink(map.Index(x, y), map.Index(cx, cy))) &&
            (map.PathWearAt(cx, cy) > 0 || map.HasRoad(cx, cy) || map.BuildingAt(cx, cy) is not null ||
                map.IsHouseYardCell(map.Index(cx, cy)));
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
