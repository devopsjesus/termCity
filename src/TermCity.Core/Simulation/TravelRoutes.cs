using System.Buffers;
using TermCity.Core.World;
using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.Terrain;
using TermCity.Core.Rendering;

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

    private TravelRoutes(TravelRoutes previous)
    {
        Cost = (double[])previous.Cost.Clone();
        Source = (int[])previous.Source.Clone();
        Next = (int[])previous.Next.Clone();
    }

    public static bool Walkable(GameMap map, int index) =>
        map.RoadLayer[index] || map.Content.Terrains[map.TerrainLayer[index]].Buildable ||
        map.HasFootbridge(index % map.Width, index / map.Width);

    public static double StepCost(GameMap map, int index)
    {
        if (map.HasFootbridge(index % map.Width, index / map.Width)) return 2;
        if (map.RoadLayer[index])
            return 1.0 / Math.Max(1, map.Content.Roads[map.RoadTypeLayer[index]].Rank) + JunctionDelayUnits(map, index);
        return (map.PathLayer[index] >= 3 ? 2 : 6) *
            map.Content.Terrains[map.TerrainLayer[index]].BuildCostModifier;
    }

    internal static bool CanFootpathStep(GameMap map, int first, int last)
    {
        int mask = GameMap.PathDirection(last % map.Width - first % map.Width, last / map.Width - first / map.Width);
        if (mask == 0) return ComputeFootpathStep(map, first, last);
        if (map.FootpathStepKnown is null || map.FootpathStepTerrain != map.TerrainVersion)
        {
            map.FootpathStepKnown = new byte[map.Width * map.Height];
            map.FootpathStepAllowed = new byte[map.Width * map.Height];
            map.FootpathStepTerrain = map.TerrainVersion;
        }
        if ((map.FootpathStepKnown[first] & mask) != 0) return (map.FootpathStepAllowed![first] & mask) != 0;
        bool allowed = ComputeFootpathStep(map, first, last);
        map.FootpathStepKnown[first] |= (byte)mask;
        if (allowed) map.FootpathStepAllowed![first] |= (byte)mask;
        return allowed;
    }

    private static bool ComputeFootpathStep(GameMap map, int first, int last)
    {
        if (!CanTraverse(map, first, last)) return false;
        if (map.HasFootbridge(first % map.Width, first / map.Width) ||
            map.HasFootbridge(last % map.Width, last / map.Width))
            return Walkable(map, first) && Walkable(map, last) &&
                map.FootpathsAllowedAt(first % map.Width, first / map.Width) &&
                map.FootpathsAllowedAt(last % map.Width, last / map.Width);
        if (map.SettlementBounds is not null &&
            (!map.FootpathsAllowedAt(first % map.Width, first / map.Width) ||
                !map.FootpathsAllowedAt(last % map.Width, last / map.Width))) return false;
        if (!Walkable(map, first) || !Walkable(map, last) ||
            IsFootpathBridgeCell(map, first) || IsFootpathBridgeCell(map, last) ||
            !FootpathGround(first) || !FootpathGround(last)) return false;
        float rise = Math.Abs(FootpathHeight(first) - FootpathHeight(last));
        return rise <= MaximumFootpathStep;

        bool FootpathGround(int cell)
        {
            if (!IsFootpathHillCell(map, cell) || map.RoadLayer[cell]) return true;
            var position = map.PosOf(cell);
            if (map.JunctionRoadLayout is not { } roads) return false;
            var segments = roads.AtCell(position.X, position.Y);
            for (int i = 0; i < segments.Count; i++)
                if (!segments[i].Bridge && Rendering.RoadMeshLayout.OverlapsCell(
                    segments[i].Road, position.X, position.Y)) return true;
            return false;
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

    private static bool CanTraverse(GameMap map, int first, int last)
    {
        if (map.Fortifications is not { } walls) return true;
        int mask = GameMap.PathDirection(last % map.Width - first % map.Width, last / map.Width - first / map.Width);
        if (mask == 0) return Walk();
        if (map.TravelWallKnown is null || !ReferenceEquals(map.TravelWallState, walls) ||
            map.TravelWallTopology != walls.TopologyRevision)
        {
            map.TravelWallKnown = new byte[map.Width * map.Height];
            map.TravelWallAllowed = new byte[map.Width * map.Height];
            map.TravelWallState = walls;
            map.TravelWallTopology = walls.TopologyRevision;
        }
        if ((map.TravelWallKnown[first] & mask) != 0) return (map.TravelWallAllowed![first] & mask) != 0;
        bool allowed = Walk();
        map.TravelWallKnown[first] |= (byte)mask;
        if (allowed) map.TravelWallAllowed![first] |= (byte)mask;
        return allowed;

        bool Walk() => walls.CanWalk(new(first % map.Width + .5f, first / map.Width + .5f),
            new(last % map.Width + .5f, last / map.Width + .5f));
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
        routes.Flood(map, queue, avoidBuildings);
        if (!avoidBuildings)
        {
            if (map.TravelCache.Count >= ServiceKinds.Count + 4) map.TravelCache.Clear();
            map.TravelCache[key] = routes;
        }
        return routes;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static double CachedStepCost(GameMap map, int index)
    {
        if (map.TravelStepCosts is null)
        {
            map.TravelStepCosts = new double[map.Width * map.Height];
            Array.Fill(map.TravelStepCosts, double.NaN);
        }
        return double.IsNaN(map.TravelStepCosts[index])
            ? map.TravelStepCosts[index] = Walkable(map, index) ? StepCost(map, index) : double.PositiveInfinity
            : map.TravelStepCosts[index];
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private int Flood(GameMap map, PriorityQueue<int, (double Cost, int Index)> queue, bool avoidBuildings,
        RouteRepair? repair = null, int maximumVisited = int.MaxValue)
    {
        int visited = 0;
        while (visited < maximumVisited && queue.TryDequeue(out int index, out var priority))
        {
            if (priority.Cost != Cost[index]) continue;
            visited++;
            repair?.Visited(index);
            RelaxNeighbors(map, queue, index, avoidBuildings, repair);
        }
        return visited;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void RelaxNeighbors(GameMap map, PriorityQueue<int, (double Cost, int Index)> queue,
        int index, bool avoidBuildings, RouteRepair? repair = null)
    {
        int x = index % map.Width, y = index / map.Width;
        if (map.TravelStepCosts is null) _ = CachedStepCost(map, index);
        var costs = map.TravelStepCosts!;
        double step = costs[index];
        if (double.IsNaN(step)) step = CachedStepCost(map, index);
        foreach (var (dx, dy) in RoadNetwork.Neighbors)
        {
            if (!map.InBounds(x + dx, y + dy)) continue;
            int next = map.Index(x + dx, y + dy);
            if (avoidBuildings && map.BuildingLayer[next] != 0) continue;
            double nextStep = costs[next];
            if (double.IsNaN(nextStep)) nextStep = CachedStepCost(map, next);
            double cost = Cost[index] + (step + nextStep) / 2;
            if (cost >= Cost[next] - 1e-9 || !CanTraverse(map, index, next)) continue;
            if (repair is not null) repair.Improve(next, index, cost, queue);
            else
            {
                Cost[next] = cost;
                Source[next] = Source[index];
                Next[next] = index;
                queue.Enqueue(next, (cost, next));
            }
        }
    }

    private sealed class RouteRepair(GameMap map, TravelRoutes routes, int[] firstChild, int[] sibling,
        int[] marked, bool[] pending)
    {
        private readonly int[] _marked = marked;
        private readonly bool[] _pending = pending;
        private readonly List<int> _shifted = [];
        private int _revision;
        public void Visited(int cell) => _pending[cell] = false;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void QueueImprovement(int cell, double[] costs, PriorityQueue<int, (double Cost, int Index)> queue)
        {
            if (_pending[cell]) return;
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                double cost = routes.Cost[cell] + (costs[cell] + costs[next]) / 2;
                if (cost >= routes.Cost[next] - 1e-9 || !CanTraverse(map, cell, next)) continue;
                queue.Enqueue(cell, (routes.Cost[cell], cell));
                _pending[cell] = true;
                return;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Improve(int cell, int parent, double cost, PriorityQueue<int, (double Cost, int Index)> queue)
        {
            double delta = cost - routes.Cost[cell];
            int previous = routes.Next[cell];
            if (previous >= 0)
            {
                int child = firstChild[previous], before = -1;
                while (child != cell) { before = child; child = sibling[child]; }
                if (before < 0) firstChild[previous] = sibling[cell];
                else sibling[before] = sibling[cell];
            }
            sibling[cell] = firstChild[parent];
            firstChild[parent] = cell;
            routes.Next[cell] = parent;
            routes.Cost[cell] = cost;
            _shifted.Clear();
            _shifted.Add(cell);
            int stamp = ++_revision, owner = routes.Source[parent];
            for (int i = 0; i < _shifted.Count; i++)
            {
                int current = _shifted[i];
                _marked[current] = stamp;
                routes.Source[current] = owner;
                if (current != cell) routes.Cost[current] += delta;
                for (int child = firstChild[current]; child >= 0; child = sibling[child]) _shifted.Add(child);
            }
            // A uniform subtree shift cannot improve its internal edges. Only
            // boundary vertices need the priority queue, even beyond a town gate.
            foreach (int current in _shifted)
            {
                if (_pending[current])
                {
                    queue.Enqueue(current, (routes.Cost[current], current));
                    continue;
                }
                int x = current % map.Width;
                if (x > 0 && _marked[current - 1] != stamp ||
                    x + 1 < map.Width && _marked[current + 1] != stamp ||
                    current >= map.Width && _marked[current - map.Width] != stamp ||
                    current + map.Width < routes.Cost.Length && _marked[current + map.Width] != stamp)
                {
                    queue.Enqueue(current, (routes.Cost[current], current));
                    _pending[current] = true;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void UpdateCompactedPaths(GameMap map, List<int> changed)
    {
        map.LastTravelRepairMilliseconds = 0;
        map.LastRepairedFields = map.TravelCache.Count;
        map.LastCompactedCells = changed.Count;
        map.LastRepairTreeMilliseconds = map.LastRepairScanMilliseconds = map.LastRepairFloodMilliseconds = 0;
        map.LastRepairFloodNodes = 0;
        if (changed.Count == 0) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (map.TravelStepCosts is { } steps)
            foreach (int index in changed) steps[index] = double.NaN;
        foreach (int index in changed) _ = CachedStepCost(map, index);
        var costs = map.TravelStepCosts!;
        var fields = map.TravelCache.Where(pair => changed.Any(index => double.IsFinite(pair.Value.Cost[index]))).ToArray();
        bool parallel = map.Fortifications is null && fields.Length > 1 &&
            costs.Length >= 8192 && Environment.ProcessorCount >= 4;
        if (parallel)
        {
            // Workers only read the graph. Fill lazy edge/cost caches before
            // releasing independent immutable route fields to the bounded pool.
            for (int index = 0; index < costs.Length; index++) _ = CachedStepCost(map, index);
        }
        var results = new CompactedField[fields.Length];
        if (parallel)
            Parallel.For(0, fields.Length, new ParallelOptions { MaxDegreeOfParallelism = 3 },
                index => results[index] = Repair(fields[index].Value));
        else
            for (int index = 0; index < fields.Length; index++) results[index] = Repair(fields[index].Value);
        for (int index = 0; index < fields.Length; index++)
        {
            var result = results[index];
            map.TravelCache[fields[index].Key] = result.Routes;
            map.LastRepairTreeMilliseconds += result.Tree;
            map.LastRepairScanMilliseconds += result.Scan;
            map.LastRepairFloodMilliseconds += result.Flood;
            map.LastRepairFloodNodes += result.Nodes;
        }
        map.LastTravelRepairMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        CompactedField Repair(TravelRoutes previous)
        {
            // A lower edge cost matters only if it can strictly improve either endpoint.
            bool improves = false;
            foreach (int index in changed)
            {
                int x = index % map.Width, y = index / map.Width;
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    if (!map.InBounds(x + dx, y + dy)) continue;
                    int next = map.Index(x + dx, y + dy);
                    double cost = previous.Cost[next] + (CachedStepCost(map, index) + CachedStepCost(map, next)) / 2;
                    if (cost < previous.Cost[index] - 1e-9 && CanTraverse(map, next, index) ||
                        previous.Cost[index] + (CachedStepCost(map, index) + CachedStepCost(map, next)) / 2 <
                            previous.Cost[next] - 1e-9 && CanTraverse(map, index, next))
                    {
                        improves = true;
                        break;
                    }
                }
                if (improves) break;
            }
            if (!improves) return new(previous, 0, 0, 0, 0);
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            var routes = new TravelRoutes(previous);
            var queue = new PriorityQueue<int, (double Cost, int Index)>();
            foreach (int index in changed)
            {
                int x = index % map.Width, y = index / map.Width;
                foreach (var (dx, dy) in RoadNetwork.Neighbors)
                {
                    if (!map.InBounds(x + dx, y + dy)) continue;
                    int next = map.Index(x + dx, y + dy);
                    double cost = routes.Cost[next] + (CachedStepCost(map, index) + CachedStepCost(map, next)) / 2;
                    if (cost >= routes.Cost[index] - 1e-9 || !CanTraverse(map, next, index)) continue;
                    routes.Cost[index] = cost;
                    routes.Source[index] = routes.Source[next];
                    routes.Next[index] = next;
                }
                if (double.IsFinite(routes.Cost[index])) queue.Enqueue(index, (routes.Cost[index], index));
            }
            int nodes = routes.Flood(map, queue, avoidBuildings: false,
                maximumVisited: Math.Max(4096, changed.Count * 4));
            if (queue.Count == 0)
                return new(routes, 0, 0, 0, nodes);
            // Sparse changes finish above. A larger influence region uses an
            // exact tree repair instead of flooding the entire map repeatedly.
            Array.Copy(previous.Cost, routes.Cost, routes.Cost.Length);
            Array.Copy(previous.Source, routes.Source, routes.Source.Length);
            Array.Copy(previous.Next, routes.Next, routes.Next.Length);
            queue.Clear();
            int count = routes.Next.Length;
            var firstChild = ArrayPool<int>.Shared.Rent(count);
            var sibling = ArrayPool<int>.Shared.Rent(count);
            var marked = ArrayPool<int>.Shared.Rent(count);
            var pending = ArrayPool<bool>.Shared.Rent(count);
            try
            {
                Array.Fill(firstChild, -1, 0, count);
                Array.Clear(marked, 0, count);
                Array.Clear(pending, 0, count);
                var tree = new Queue<int>();
                for (int index = 0; index < routes.Next.Length; index++)
                {
                    int parent = routes.Next[index];
                    if (parent >= 0)
                    {
                        sibling[index] = firstChild[parent];
                        firstChild[parent] = index;
                    }
                    else if (routes.Cost[index] == 0) tree.Enqueue(index);
                }
                // Existing tree edges stay valid when paths only become faster. Shift
                // their descendants in linear time before searching for better joins.
                while (tree.TryDequeue(out int parent))
                    for (int child = firstChild[parent]; child >= 0; child = sibling[child])
                    {
                        routes.Cost[child] = routes.Cost[parent] +
                            (costs[parent] + costs[child]) / 2;
                        tree.Enqueue(child);
                    }
                double treeTime = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                var repair = new RouteRepair(map, routes, firstChild, sibling, marked, pending);
                for (int index = 0; index < routes.Cost.Length; index++)
                    if (routes.Cost[index] < previous.Cost[index])
                        repair.QueueImprovement(index, costs, queue);
                foreach (int index in changed)
                    if (double.IsFinite(routes.Cost[index]))
                        repair.QueueImprovement(index, costs, queue);
                double scanTime = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                stage = System.Diagnostics.Stopwatch.GetTimestamp();
                nodes += routes.Flood(map, queue, avoidBuildings: false, repair);
                return new(routes, treeTime, scanTime,
                    System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds, nodes);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(firstChild);
                ArrayPool<int>.Shared.Return(sibling);
                ArrayPool<int>.Shared.Return(marked);
                ArrayPool<bool>.Shared.Return(pending);
            }
        }
    }

    private readonly record struct CompactedField(TravelRoutes Routes, double Tree, double Scan, double Flood, int Nodes);

    internal static TravelRoutes NearestRoad(GameMap map)
    {
        if (map.PathRoutes is { } cached)
        {
            if (map.RoadAccessChanges.Count == 0) return cached;
            var repaired = RepairRoadAccess(map, cached);
            map.RoadAccessChanges.Clear();
            return map.PathRoutes = repaired;
        }
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        var roads = map.JunctionRoadLayout ??= new Rendering.RoadMeshLayout(map,
            map.TravelRoadPaths ??= Rendering.RoadCurves.Extract(map, (_, _) => false));
        var targets = map.FootpathRoadTargets;
        if (targets is null)
        {
            var cells = new SortedSet<int>(map.RoadCells.Where(cell => !IsFootpathBridgeCell(map, cell)));
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
                            cells.Add(map.Index(x, y));
            }
            map.FootpathRoadTargets = targets = cells.ToArray();
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
        map.RoadAccessChanges.Clear();
        return routes;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static TravelRoutes RepairRoadAccess(GameMap map, TravelRoutes previous)
    {
        var routes = new TravelRoutes(previous);
        int count = routes.Cost.Length;
        var firstChild = new int[count];
        var sibling = new int[count];
        Array.Fill(firstChild, -1);
        for (int cell = 0; cell < count; cell++)
            if (previous.Next[cell] is >= 0 and var parent)
            {
                sibling[cell] = firstChild[parent];
                firstChild[parent] = cell;
            }
        bool Allowed(int cell) => Walkable(map, cell) && map.BuildingLayer[cell] == 0 &&
            !map.IsHouseYardCell(cell);
        var invalid = new List<int>();
        foreach (int cell in map.RoadAccessChanges)
            if (routes.Cost[cell] > 0 && double.IsFinite(routes.Cost[cell]) && !Allowed(cell))
                Invalidate(cell);
        for (int i = 0; i < invalid.Count; i++)
            for (int child = firstChild[invalid[i]]; child >= 0; child = sibling[child])
                if (double.IsFinite(routes.Cost[child])) Invalidate(child);

        var queue = new PriorityQueue<int, (double Cost, int Index)>();
        foreach (int cell in invalid) SeedNeighbors(cell);
        foreach (int cell in map.RoadAccessChanges) SeedNeighbors(cell);
        while (queue.TryDequeue(out int cell, out var priority))
        {
            if (priority.Cost != routes.Cost[cell]) continue;
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                double cost = routes.Cost[cell] + 1;
                if (cost >= routes.Cost[next] || !Allowed(next) || !CanFootpathStep(map, cell, next)) continue;
                routes.Cost[next] = cost;
                routes.Source[next] = routes.Source[cell];
                routes.Next[next] = cell;
                queue.Enqueue(next, (cost, next));
            }
        }
        // Preserve the original multi-source FIFO tie order, including after
        // reopening a route. Equal distances alone would change daily path wear.
        Array.Fill(routes.Source, -1);
        Array.Fill(routes.Next, -1);
        var ordered = new int[count];
        int head = 0, tail = 0;
        foreach (int target in map.FootpathRoadTargets!)
        {
            routes.Source[target] = target;
            ordered[tail++] = target;
        }
        while (head < tail)
        {
            int cell = ordered[head++];
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                if (!map.InBounds(x + dx, y + dy)) continue;
                int next = map.Index(x + dx, y + dy);
                if (routes.Source[next] >= 0 || routes.Cost[next] != routes.Cost[cell] + 1 ||
                    !CanFootpathStep(map, cell, next)) continue;
                routes.Source[next] = routes.Source[cell];
                routes.Next[next] = cell;
                ordered[tail++] = next;
            }
        }
        return routes;

        void Invalidate(int cell)
        {
            routes.Cost[cell] = double.PositiveInfinity;
            routes.Source[cell] = routes.Next[cell] = -1;
            invalid.Add(cell);
        }

        void SeedNeighbors(int cell)
        {
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
                if (map.InBounds(x + dx, y + dy))
                {
                    int next = map.Index(x + dx, y + dy);
                    if (double.IsFinite(routes.Cost[next])) queue.Enqueue(next, (routes.Cost[next], next));
                }
        }
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

    private static (int Start, int Owner) RoadWearApproach(GameMap map, CellRect area, TravelRoutes routes)
    {
        var access = BuildingEntranceLayout.Accesses(map, area)
            .Where(candidate => routes.Source[candidate.Start] >= 0)
            .OrderBy(candidate => routes.Cost[candidate.Start]).ThenBy(candidate => candidate.Start).FirstOrDefault();
        return access is null ? (-1, -1) : (access.Start, access.Property);
    }

    internal static double RoadDistance(GameMap map, CellRect area, TravelRoutes routes)
    {
        var (start, _) = RoadApproach(map, area, routes);
        return start < 0 ? double.PositiveInfinity : routes.Cost[start];
    }

    // Keep the existing name-based smoke probe unambiguous.
    internal static bool WearPaths(GameMap map) => AdvancePathWear(map, out _);

    internal static bool AdvancePathWear(GameMap map, out bool visualChanged)
    {
        if (map.BuildingFootprints.Count == 0) { visualChanged = false; return false; }
        long stage = System.Diagnostics.Stopwatch.GetTimestamp();
        bool changed = SettlementFootbridges.Advance(map);
        map.LastFootbridgeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        bool artworkChanged = changed;
        var compacted = new List<int>();
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var path in BetweenBuildings(map))
            Wear(path);
        map.LastBuildingJourneyMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        var routes = map.SettlementBounds is null && map.RoadCount > 0 ? NearestRoad(map) : null;
        if (routes is not null)
            foreach (var area in map.BuildingFootprints.OrderBy(p => p.Key).Select(p => p.Value))
            {
                var (start, _) = RoadWearApproach(map, area, routes);
                Wear(RoadPath(start));
            }
        map.LastRoadJourneyMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var journey in FootpathJourneys(map))
        {
            for (int step = 1; step < journey.Length; step++)
            {
                int first = journey[step - 1], last = journey[step];
                bool Used(int cell) => map.PathLayer[cell] > 0 ||
                    map.BuildingLayer[cell] != 0 || map.IsHouseYardCell(cell) || IsPathRoadTarget(map, cell);
                if ((map.PathLayer[first] > 0 || map.PathLayer[last] > 0) &&
                    Used(first) && Used(last) && CanFootpathStep(map, first, last))
                {
                    bool linked = map.LinkPathCells(first, last);
                    changed |= linked;
                    artworkChanged |= linked;
                }
            }
        }
        map.LastPathLinkMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        UpdateCompactedPaths(map, compacted);
        visualChanged = artworkChanged;
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
                    if (old is 0 or 2) { artworkChanged = true; map.FootpathVersion++; }
                    if (old == 2) compacted.Add(cell);
                    if (map.FeatureLayer[cell] != 0) { artworkChanged = true; map.FootpathVersion++; }
                    map.FeatureLayer[cell] = 0;
                    changed = true;
                }
                if (old == 0) break;
            }
        }
    }

    internal static IEnumerable<int[]> FootpathJourneys(GameMap map)
    {
        if (map.FootpathJourneyCache is { } cached && map.FootpathJourneyNavigation == map.NavigationVersion &&
            ReferenceEquals(map.FootpathJourneyBuildings, map.BuildingPaths) &&
            ReferenceEquals(map.FootpathJourneyRoads, map.PathRoutes))
            return cached;
        var journeys = CollectFootpathJourneys(map).ToArray();
        map.FootpathJourneyBuildings = map.BuildingPaths;
        map.FootpathJourneyRoads = map.PathRoutes;
        map.FootpathJourneyNavigation = map.NavigationVersion;
        return map.FootpathJourneyCache = journeys;
    }

    private static IEnumerable<int[]> CollectFootpathJourneys(GameMap map)
    {
        foreach (var path in BetweenBuildings(map))
        {
            if (path.Length == 0) continue;
            yield return path;
        }
        if (map.SettlementBounds is not null || map.RoadCount == 0) yield break;
        var routes = NearestRoad(map);
        foreach (var area in map.BuildingFootprints.Values)
        {
            var (start, ownerCell) = RoadWearApproach(map, area, routes);
            if (start < 0) continue;
            var journey = new List<int> { ownerCell };
            for (int cell = start; cell >= 0; cell = routes.Next[cell]) journey.Add(cell);
            yield return journey.ToArray();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static int[][] BetweenBuildings(GameMap map)
    {
        if (map.BuildingPaths is { } cached) return cached;
        if (map.BuildingFootprints.Count < 2) return map.BuildingPaths = [];
        bool settlement = map.SettlementBounds is not null;
        var best = new Dictionary<(int A, int B), (int A, int B, double Distance)>();
        HashSet<int> served = settlement || map.RoadCount == 0 ? [] : map.BuildingFootprints
            .Where(pair => RoadApproach(map, pair.Value, NearestRoad(map)).Start >= 0).Select(pair => pair.Key).ToHashSet();
        if (served.Count == map.BuildingFootprints.Count) return map.BuildingPaths = [];
        if (!settlement)
        {
            int candidates = 0;
            bool nonResidential = false;
            foreach (var (owner, area) in map.BuildingFootprints)
                if (!served.Contains(owner) && BuildingEntranceLayout.Accesses(map, area).Count > 0)
                {
                    candidates++;
                    nonResidential |= map.Content.Buildings[map.BuildingLayer[owner]].Zone != ZoneType.Residential;
                }
            if (candidates < 2 || !nonResidential) return map.BuildingPaths = [];
        }
        var routes = new TravelRoutes(map.Width * map.Height);
        var queue = new Queue<int>();
        void Connect(int ownerA, int a, int ownerB, int b)
        {
            if (ownerA == ownerB || served.Contains(ownerA) || served.Contains(ownerB) ||
                !settlement && map.Content.Buildings[map.BuildingLayer[ownerA]].Zone == ZoneType.Residential &&
                map.Content.Buildings[map.BuildingLayer[ownerB]].Zone == ZoneType.Residential) return;
            double distance = routes.Cost[a] + routes.Cost[b] + (a == b ? 0 : 1);
            if (settlement)
            {
                var key = ownerA < ownerB ? (ownerA, ownerB) : (ownerB, ownerA);
                if (!best.TryGetValue(key, out var previous) || distance < previous.Distance)
                    best[key] = ownerA < ownerB ? (a, b, distance) : (b, a, distance);
            }
            else
            {
                if (!best.TryGetValue((ownerA, ownerA), out var previousA) || distance < previousA.Distance)
                    best[(ownerA, ownerA)] = (a, b, distance);
                if (!best.TryGetValue((ownerB, ownerB), out var previousB) || distance < previousB.Distance)
                    best[(ownerB, ownerB)] = (b, a, distance);
            }
        }
        foreach (var (owner, area) in map.BuildingFootprints.OrderBy(p => p.Key))
            foreach (var access in BuildingEntranceLayout.Accesses(map, area))
                {
                    int cell = access.Start;
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
        var parents = new Dictionary<int, int>();
        int Root(int owner)
        {
            if (!parents.TryGetValue(owner, out int root)) return parents[owner] = owner;
            while (root != parents[root]) root = parents[root] = parents[parents[root]];
            return parents[owner] = root;
        }
        int PropertyEdge(int owner, int cell) => BuildingEntranceLayout.Accesses(map, map.BuildingFootprints[owner])
            .First(access => access.Start == cell).Property;
        var connections = settlement
            ? best.OrderBy(p => p.Value.Distance).ThenBy(p => p.Key.A).ThenBy(p => p.Key.B)
            : best.OrderBy(p => p.Key.A).ThenBy(p => p.Key.B);
        foreach (var (owners, connection) in connections)
        {
            if (settlement)
            {
                int first = Root(owners.A), last = Root(owners.B);
                if (first == last) continue;
                parents[last] = first;
            }
            var path = new List<int>();
            for (int cell = connection.A; cell >= 0; cell = routes.Next[cell]) path.Add(cell);
            path.Reverse();
            if (connection.A != connection.B)
                for (int cell = connection.B; cell >= 0; cell = routes.Next[cell]) path.Add(cell);
            int firstProperty = PropertyEdge(routes.Source[path[0]], path[0]);
            int lastProperty = PropertyEdge(routes.Source[path[^1]], path[^1]);
            path.Insert(0, firstProperty);
            path.Add(lastProperty);
            paths.Add(path.ToArray());
        }
        return map.BuildingPaths = paths.ToArray();
    }
}
