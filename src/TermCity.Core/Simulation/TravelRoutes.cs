using TermCity.Core.World;
using TermCity.Core.Buildings;

namespace TermCity.Core.Simulation;

/// <summary>Least-time routes over land, worn paths and roads. Water requires a bridge.</summary>
public sealed class TravelRoutes
{
    public const double UnitsPerDay = 24;
    public double[] Cost { get; }
    public int[] Source { get; }
    public int[] Next { get; }

    private TravelRoutes(int count)
    {
        Cost = new double[count];
        Source = new int[count];
        Next = new int[count];
        Array.Fill(Cost, double.PositiveInfinity);
        Array.Fill(Source, -1);
        Array.Fill(Next, -1);
    }

    public static bool Walkable(GameMap map, int index) =>
        map.RoadLayer[index] || map.Content.Terrains[map.TerrainLayer[index]].Buildable;

    public static double StepCost(GameMap map, int index)
    {
        if (map.RoadLayer[index])
            return 1.0 / Math.Max(1, map.Content.Roads[map.RoadTypeLayer[index]].Rank) + JunctionDelayUnits(map, index);
        return (map.PathLayer[index] >= 3 ? 2 : 6) *
            map.Content.Terrains[map.TerrainLayer[index]].BuildCostModifier;
    }

    public static double JunctionDelayUnits(GameMap map, int index)
    {
        if (map.JunctionDelays is null)
        {
            map.JunctionDelays = new double[map.Width * map.Height];
            Array.Fill(map.JunctionDelays, double.NaN);
        }
        if (!double.IsNaN(map.JunctionDelays[index])) return map.JunctionDelays[index];
        int x = index % map.Width, y = index / map.Width;
        int count = 0, minRank = int.MaxValue, maxRank = 0;
        foreach (var (dx, dy) in RoadNetwork.Neighbors)
            if (map.InBounds(x + dx, y + dy) && map.RoadTypeAt(x + dx, y + dy) is { } road)
            {
                count++;
                minRank = Math.Min(minRank, road.Rank);
                maxRank = Math.Max(maxRank, road.Rank);
            }
        if (count < 3 || minRank == maxRank) return map.JunctionDelays[index] = 0;
        var paths = map.TravelRoadPaths ??= Rendering.RoadCurves.Extract(map, (_, _) => false);
        var layout = map.JunctionRoadLayout ??= new Rendering.RoadMeshLayout(map, paths);
        var arms = new List<(System.Numerics.Vector2 Direction, int Rank)>();
        var junction = new System.Numerics.Vector2(x + 0.5f, y + 0.5f);
        foreach (var group in layout.AtCell(x, y).GroupBy(segment => segment.Path))
        {
            float nearest = float.PositiveInfinity;
            var direction = System.Numerics.Vector2.Zero;
            foreach (var segment in group)
            {
                var a = segment.A; var b = segment.B;
                var delta = b - a;
                if (delta.LengthSquared() < 0.000001f) continue;
                float t = Math.Clamp(System.Numerics.Vector2.Dot(junction - a, delta) / delta.LengthSquared(), 0, 1);
                float distance = System.Numerics.Vector2.DistanceSquared(junction, a + delta * t);
                if (distance >= nearest) continue;
                nearest = distance;
                direction = System.Numerics.Vector2.Normalize(delta);
            }
            if (nearest <= 1) arms.Add((direction, group.Key.Type.Rank));
        }
        double delay = 0;
        if (arms.Count >= 2)
        {
            int highest = arms.Max(arm => arm.Rank);
            foreach (var minor in arms.Where(arm => arm.Rank < highest))
                foreach (var major in arms.Where(arm => arm.Rank == highest))
                {
                    double cosine = Math.Abs(System.Numerics.Vector2.Dot(minor.Direction, major.Direction));
                    delay = Math.Max(delay, 0.75 * Math.Clamp((cosine - Math.Sqrt(0.5)) / (1 - Math.Sqrt(0.5)), 0, 1));
                }
        }
        return map.JunctionDelays[index] = delay;
    }

    public static TravelRoutes Compute(GameMap map, IEnumerable<int> sources, bool avoidBuildings = false)
    {
        var seeds = sources.Order().ToArray();
        if (seeds.Any(i => i < 0 || i >= map.Width * map.Height || !Walkable(map, i)))
            throw new ArgumentException("Travel sources must be walkable map cells.", nameof(sources));
        string key = string.Join(',', seeds);
        if (!avoidBuildings && map.TravelCache.TryGetValue(key, out var cached)) return cached;
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new PriorityQueue<int, (double Cost, int Index)>();
        foreach (int source in seeds)
        {
            routes.Cost[source] = 0;
            routes.Source[source] = source;
            queue.Enqueue(source, (0, source));
        }
        while (queue.TryDequeue(out int index, out var priority))
        {
            if (priority.Cost != routes.Cost[index]) continue;
            int x = index % map.Width, y = index / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                if (!Walkable(map, next) || (avoidBuildings && map.BuildingLayer[next] != 0)) continue;
                double cost = priority.Cost + (StepCost(map, index) + StepCost(map, next)) / 2;
                if (cost >= routes.Cost[next]) continue;
                routes.Cost[next] = cost;
                routes.Source[next] = routes.Source[index];
                routes.Next[next] = index;
                queue.Enqueue(next, (cost, next));
            }
        }
        if (!avoidBuildings)
        {
            if (map.TravelCache.Count >= ServiceKinds.Count + 4) map.TravelCache.Clear();
            map.TravelCache[key] = routes;
        }
        return routes;
    }

    private static TravelRoutes NearestRoad(GameMap map)
    {
        if (map.PathRoutes is { } cached) return cached;
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        foreach (int index in map.RoadCells.Order())
        {
            routes.Cost[index] = 0;
            routes.Source[index] = index;
            queue.Enqueue(index);
        }
        while (queue.TryDequeue(out int index))
        {
            int x = index % map.Width, y = index / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                if (routes.Source[next] >= 0 || !Walkable(map, next) || map.BuildingLayer[next] != 0) continue;
                routes.Cost[next] = routes.Cost[index] + 1;
                routes.Source[next] = routes.Source[index];
                routes.Next[next] = index;
                queue.Enqueue(next);
            }
        }
        map.PathRoutes = routes;
        return routes;
    }

    internal static bool WearPaths(GameMap map)
    {
        if (map.BuildingFootprints.Count == 0) return false;
        bool changed = false;
        foreach (var path in BetweenBuildings(map))
            Wear(path);
        if (map.RoadCount == 0) return changed;
        var routes = NearestRoad(map);
        foreach (var area in map.BuildingFootprints.OrderBy(p => p.Key).Select(p => p.Value))
        {
            int start = -1;
            double best = double.PositiveInfinity;
            foreach (var p in area.Cells())
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    if (!map.InBounds(p.X + dx, p.Y + dy)) continue;
                    int cell = map.Index(p.X + dx, p.Y + dy);
                    if (routes.Cost[cell] < best)
                    {
                        best = routes.Cost[cell];
                        start = cell;
                    }
                }
            Wear(RoadPath(start));
        }
        return changed;

        IEnumerable<int> RoadPath(int start)
        {
            for (int cell = start; cell >= 0 && !map.RoadLayer[cell]; cell = routes.Next[cell])
                yield return cell;
        }

        void Wear(IEnumerable<int> path)
        {
            // One new segment per journey per day; earlier segments gradually compact.
            foreach (int cell in path)
            {
                if (map.RoadLayer[cell] || map.BuildingLayer[cell] != 0 || !Walkable(map, cell)) continue;
                byte old = map.PathLayer[cell];
                if (old < 12)
                {
                    map.PathLayer[cell]++;
                    if (old == 2) map.TravelCache.Clear();
                    map.FeatureLayer[cell] = 0;
                    changed = true;
                }
                if (old == 0) break;
            }
        }
    }

    private static int[][] BetweenBuildings(GameMap map)
    {
        if (map.BuildingPaths is { } cached) return cached;
        if (map.BuildingFootprints.Count < 2) return map.BuildingPaths = [];
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        var best = new Dictionary<int, (int A, int B, double Distance)>();
        void Connect(int ownerA, int a, int ownerB, int b)
        {
            if (ownerA == ownerB) return;
            double distance = routes.Cost[a] + routes.Cost[b] + (a == b ? 0 : 1);
            if (!best.TryGetValue(ownerA, out var previousA) || distance < previousA.Distance)
                best[ownerA] = (a, b, distance);
            if (!best.TryGetValue(ownerB, out var previousB) || distance < previousB.Distance)
                best[ownerB] = (b, a, distance);
        }
        foreach (var (owner, area) in map.BuildingFootprints.OrderBy(p => p.Key))
            foreach (var p in area.Cells())
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    int x = p.X + dx, y = p.Y + dy;
                    if (!map.InBounds(x, y)) continue;
                    int cell = map.Index(x, y);
                    if (!Walkable(map, cell) || map.BuildingLayer[cell] != 0) continue;
                    if (routes.Source[cell] >= 0)
                    {
                        Connect(owner, cell, routes.Source[cell], cell);
                        continue;
                    }
                    routes.Source[cell] = owner;
                    routes.Cost[cell] = 0;
                    queue.Enqueue(cell);
                }
        while (queue.TryDequeue(out int cell))
        {
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                if (!Walkable(map, next) || map.BuildingLayer[next] != 0) continue;
                if (routes.Source[next] >= 0)
                {
                    Connect(routes.Source[cell], cell, routes.Source[next], next);
                    continue;
                }
                routes.Source[next] = routes.Source[cell];
                routes.Cost[next] = routes.Cost[cell] + 1;
                routes.Next[next] = cell;
                queue.Enqueue(next);
            }
        }
        var paths = new List<int[]>();
        foreach (var (_, connection) in best.OrderBy(p => p.Key))
        {
            var path = new List<int>();
            for (int cell = connection.A; cell >= 0; cell = routes.Next[cell]) path.Add(cell);
            path.Reverse();
            if (connection.A != connection.B)
                for (int cell = connection.B; cell >= 0; cell = routes.Next[cell]) path.Add(cell);
            paths.Add(path.ToArray());
        }
        return map.BuildingPaths = paths.ToArray();
    }
}
