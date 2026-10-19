using TermCity.Core.World;
using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.Terrain;

namespace TermCity.Core.Simulation;

/// <summary>Least-time routes over land, worn paths and roads. Water requires a bridge.</summary>
public sealed class TravelRoutes
{
    public const double UnitsPerDay = 24;
    public const float MaximumFootpathStep = 0.12f;
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

    internal static bool CanFootpathStep(GameMap map, int first, int last)
    {
        if (!Walkable(map, first) || !Walkable(map, last) ||
            IsFootpathBridgeCell(map, first) || IsFootpathBridgeCell(map, last) ||
            !FootpathGround(first) || !FootpathGround(last)) return false;
        float rise = Math.Abs(FootpathHeight(first) - FootpathHeight(last));
        return rise <= MaximumFootpathStep;

        bool FootpathGround(int cell)
        {
            if (!IsFootpathHillCell(map, cell) || map.RoadLayer[cell]) return true;
            var position = map.PosOf(cell);
            return map.JunctionRoadLayout?.AtCell(position.X, position.Y).Any(segment =>
                !segment.Bridge && Rendering.RoadMeshLayout.OverlapsCell(segment.Road, position.X, position.Y)) == true;
        }

        float FootpathHeight(int cell)
        {
            if (map.FootpathHeights is null)
            {
                map.FootpathHeights = new float[map.Width * map.Height];
                Array.Fill(map.FootpathHeights, float.NaN);
            }
            if (!float.IsNaN(map.FootpathHeights[cell])) return map.FootpathHeights[cell];
            int x = cell % map.Width, y = cell / map.Width;
            float height = Rendering.TerrainSprite.Elevation(map, x, y);
            if (map.RoadCount > 0)
            {
                var roads = map.JunctionRoadLayout ??= new Rendering.RoadMeshLayout(map,
                    map.TravelRoadPaths ??= Rendering.RoadCurves.Extract(map, (_, _) => false));
                var center = new System.Numerics.Vector2(x + 0.5f, y + 0.5f);
                if (roads.InRoadInterior(center)) height = roads.HeightAt(center);
            }
            return map.FootpathHeights[cell] = height;
        }
    }

    internal static bool IsFootpathHillCell(GameMap map, int cell) =>
        map.Content.Terrains[map.TerrainLayer[cell]].Name == DefaultTerrains.HillName;

    internal static bool IsFootpathBridgeCell(GameMap map, int index)
    {
        if (map.RoadCount == 0) return false;
        if (map.FootpathBridgeCells is null)
        {
            var cells = new bool[map.Width * map.Height];
            foreach (int road in map.RoadCells)
                cells[road] = !map.Content.Terrains[map.TerrainLayer[road]].Buildable;
            var roads = map.JunctionRoadLayout ??= new Rendering.RoadMeshLayout(map,
                map.TravelRoadPaths ??= Rendering.RoadCurves.Extract(map, (_, _) => false));
            foreach (var segment in roads.Segments.Where(segment => segment.Bridge))
            {
                int left = Math.Max(0, (int)MathF.Floor(segment.Corridor.Min(point => point.X)));
                int right = Math.Min(map.Width - 1, (int)MathF.Floor(segment.Corridor.Max(point => point.X)));
                int top = Math.Max(0, (int)MathF.Floor(segment.Corridor.Min(point => point.Y)));
                int bottom = Math.Min(map.Height - 1, (int)MathF.Floor(segment.Corridor.Max(point => point.Y)));
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                        if (Rendering.RoadMeshLayout.OverlapsCell(segment.Corridor, x, y))
                            cells[map.Index(x, y)] = true;
            }
            map.FootpathBridgeCells = cells;
        }
        return map.FootpathBridgeCells[index];
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

    internal static TravelRoutes NearestRoad(GameMap map)
    {
        if (map.PathRoutes is { } cached) return cached;
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        var roads = map.JunctionRoadLayout ??= new Rendering.RoadMeshLayout(map,
            map.TravelRoadPaths ??= Rendering.RoadCurves.Extract(map, (_, _) => false));
        var targets = new SortedSet<int>(map.RoadCells.Where(cell => !IsFootpathBridgeCell(map, cell)));
        foreach (var segment in roads.Segments.Where(segment => !segment.Bridge))
        {
            int left = Math.Max(0, (int)MathF.Floor(segment.Road.Min(p => p.X)));
            int top = Math.Max(0, (int)MathF.Floor(segment.Road.Min(p => p.Y)));
            int right = Math.Min(map.Width - 1, (int)MathF.Floor(segment.Road.Max(p => p.X)));
            int bottom = Math.Min(map.Height - 1, (int)MathF.Floor(segment.Road.Max(p => p.Y)));
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                    if (!IsFootpathBridgeCell(map, map.Index(x, y)) &&
                        Rendering.RoadMeshLayout.OverlapsCell(segment.Road, x, y))
                        targets.Add(map.Index(x, y));
        }
        foreach (int index in targets)
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
                if (routes.Source[next] >= 0 || !Walkable(map, next) || map.BuildingLayer[next] != 0 ||
                    map.IsHouseYardCell(next) ||
                    !CanFootpathStep(map, index, next)) continue;
                routes.Cost[next] = routes.Cost[index] + 1;
                routes.Source[next] = routes.Source[index];
                routes.Next[next] = index;
                queue.Enqueue(next);
            }
        }
        map.PathRoutes = routes;
        return routes;
    }

    internal static bool IsPathRoadTarget(GameMap map, int cell) =>
        map.RoadCount > 0 && !IsFootpathBridgeCell(map, cell) && NearestRoad(map).Cost[cell] == 0;

    internal static bool IsRoadsidePathStep(GameMap map, int first, int last)
    {
        if (map.RoadCount == 0) return false;
        var roads = NearestRoad(map);
        return roads.Cost[first] <= 3 && roads.Cost[first] > 0 && roads.Cost[first] == roads.Cost[last];
    }

    private static (int Start, int Owner) RoadApproach(GameMap map, CellRect area, TravelRoutes routes)
    {
        int start = -1, owner = -1;
        double best = double.PositiveInfinity;
        foreach (var p in PropertyCells(map, area))
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(p.X + dx, p.Y + dy)) continue;
                int cell = map.Index(p.X + dx, p.Y + dy);
                if (map.BuildingLayer[cell] != 0 || map.IsHouseYardCell(cell) || routes.Cost[cell] >= best ||
                    !CanFootpathStep(map, map.Index(p.X, p.Y), cell)) continue;
                best = routes.Cost[cell];
                start = cell;
                owner = map.Index(p.X, p.Y);
            }
        return (start, owner);
    }

    private static IEnumerable<Pos> PropertyCells(GameMap map, CellRect area)
    {
        foreach (var p in area.Cells()) yield return p;
        int owner = map.Index(area.X, area.Y);
        if (map.HouseYardCells.TryGetValue(owner, out int yard) && map.HouseYardOwner(yard) == owner)
            yield return map.PosOf(yard);
    }

    internal static double RoadDistance(GameMap map, CellRect area, TravelRoutes routes)
    {
        var (start, _) = RoadApproach(map, area, routes);
        return start < 0 ? double.PositiveInfinity : routes.Cost[start];
    }

    internal static bool WearPaths(GameMap map)
    {
        if (map.BuildingFootprints.Count == 0) return false;
        bool changed = false;
        foreach (var path in BetweenBuildings(map))
            Wear(path);
        var routes = map.RoadCount > 0 ? NearestRoad(map) : null;
        if (routes is not null)
            foreach (var area in map.BuildingFootprints.OrderBy(p => p.Key).Select(p => p.Value))
            {
                var (start, _) = RoadApproach(map, area, routes);
                Wear(RoadPath(start));
            }
        foreach (var journey in FootpathJourneys(map))
        {
            for (int step = 1; step < journey.Length; step++)
            {
                int first = journey[step - 1], last = journey[step];
                bool Used(int cell) => map.PathLayer[cell] > 0 ||
                    map.BuildingLayer[cell] != 0 || map.IsHouseYardCell(cell) || IsPathRoadTarget(map, cell);
                if ((map.PathLayer[first] > 0 || map.PathLayer[last] > 0) &&
                    Used(first) && Used(last) && CanFootpathStep(map, first, last))
                    changed |= map.LinkPathCells(first, last);
            }
        }
        return changed;

        IEnumerable<int> RoadPath(int start)
        {
            if (routes is null) yield break;
            for (int cell = start; cell >= 0 && routes.Cost[cell] > 0; cell = routes.Next[cell])
                yield return cell;
        }

        void Wear(IEnumerable<int> path)
        {
            // One new segment per journey per day; earlier segments gradually compact.
            int previous = -1;
            foreach (int cell in path)
            {
                if (previous >= 0 && !CanFootpathStep(map, previous, cell)) break;
                previous = cell;
                if (IsPathRoadTarget(map, cell) || IsFootpathHillCell(map, cell) || map.BuildingLayer[cell] != 0 ||
                    map.IsHouseYardCell(cell) || !Walkable(map, cell)) continue;
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

    internal static IEnumerable<int[]> FootpathJourneys(GameMap map)
    {
        foreach (var path in BetweenBuildings(map))
        {
            if (path.Length == 0) continue;
            var journey = new List<int>();
            int BuildingNeighbor(int cell)
            {
                var position = map.PosOf(cell);
                foreach (var (deltaX, deltaY) in RoadNetwork.Neighbors)
                    if (map.InBounds(position.X + deltaX, position.Y + deltaY))
                    {
                        int neighbor = map.Index(position.X + deltaX, position.Y + deltaY);
                        if ((map.BuildingLayer[neighbor] != 0 || map.IsHouseYardCell(neighbor)) &&
                            CanFootpathStep(map, cell, neighbor)) return neighbor;
                    }
                return -1;
            }
            int first = BuildingNeighbor(path[0]), last = BuildingNeighbor(path[^1]);
            if (first >= 0) journey.Add(first);
            journey.AddRange(path);
            if (last >= 0) journey.Add(last);
            yield return journey.ToArray();
        }
        if (map.RoadCount == 0) yield break;
        var routes = NearestRoad(map);
        foreach (var area in map.BuildingFootprints.Values)
        {
            var (start, ownerCell) = RoadApproach(map, area, routes);
            if (start < 0) continue;
            var journey = new List<int> { ownerCell };
            for (int cell = start; cell >= 0; cell = routes.Next[cell]) journey.Add(cell);
            yield return journey.ToArray();
        }
    }

    private static int[][] BetweenBuildings(GameMap map)
    {
        if (map.BuildingPaths is { } cached) return cached;
        if (map.BuildingFootprints.Count < 2) return map.BuildingPaths = [];
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        var best = new Dictionary<int, (int A, int B, double Distance)>();
        HashSet<int> served = map.RoadCount == 0 ? [] : map.BuildingFootprints
            .Where(pair => RoadApproach(map, pair.Value, NearestRoad(map)).Start >= 0).Select(pair => pair.Key).ToHashSet();
        if (served.Count == map.BuildingFootprints.Count) return map.BuildingPaths = [];
        void Connect(int ownerA, int a, int ownerB, int b)
        {
            if (ownerA == ownerB || served.Contains(ownerA) || served.Contains(ownerB) ||
                map.Content.Buildings[map.BuildingLayer[ownerA]].Zone == ZoneType.Residential &&
                map.Content.Buildings[map.BuildingLayer[ownerB]].Zone == ZoneType.Residential) return;
            double distance = routes.Cost[a] + routes.Cost[b] + (a == b ? 0 : 1);
            if (!best.TryGetValue(ownerA, out var previousA) || distance < previousA.Distance)
                best[ownerA] = (a, b, distance);
            if (!best.TryGetValue(ownerB, out var previousB) || distance < previousB.Distance)
                best[ownerB] = (b, a, distance);
        }
        foreach (var (owner, area) in map.BuildingFootprints.OrderBy(p => p.Key))
            foreach (var p in PropertyCells(map, area))
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    int x = p.X + dx, y = p.Y + dy;
                    if (!map.InBounds(x, y)) continue;
                    int cell = map.Index(x, y);
                    if (!Walkable(map, cell) || map.BuildingLayer[cell] != 0 || map.IsHouseYardCell(cell) ||
                        !CanFootpathStep(map, map.Index(p.X, p.Y), cell)) continue;
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
                if (!Walkable(map, next) || map.BuildingLayer[next] != 0 || map.IsHouseYardCell(next) ||
                    !CanFootpathStep(map, cell, next)) continue;
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
