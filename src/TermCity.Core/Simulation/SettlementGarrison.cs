using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;
using System.Collections;

namespace TermCity.Core.Simulation;

public enum DefenderKind { Archer, Soldier }
public enum DefenderOrder { Automatic, Move, Attack }

public sealed class SettlementDefender
{
    public int Id { get; internal set; }
    public Pos Home { get; internal set; }
    public DefenderKind Kind { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double PreviousX { get; internal set; }
    public double PreviousY { get; internal set; }
    public float Elevation { get; internal set; }
    public float PreviousElevation { get; internal set; }
    public double Health { get; internal set; } = 160;
    public double MaxHealth => 160;
    public DefenderOrder Order { get; internal set; }
    public int? TargetEnemyId { get; internal set; }
    public Pos? Destination { get; internal set; }
    public double AttackCooldown { get; internal set; }
    public const double MovementSpeed = 3.5;
    public const double ClimbSpeed = 2.4;
    internal List<Pos> Path = [];
    internal Pos? PathTarget;
    internal int? PathEnemyId;
    internal int PathIndex;
    internal int? PatrolWallId;
    internal Pos? PatrolCell;
    internal double PathRetrySeconds;

    internal SettlementDefender SnapshotCopy()
    {
        var copy = (SettlementDefender)MemberwiseClone();
        copy.Path = new(Path);
        return copy;
    }

    internal void CopySnapshot(SettlementDefender source)
    {
        Id = source.Id; Home = source.Home; Kind = source.Kind;
        X = source.X; Y = source.Y;
        PreviousX = source.PreviousX; PreviousY = source.PreviousY;
        Elevation = source.Elevation; PreviousElevation = source.PreviousElevation;
        Health = source.Health; Order = source.Order;
        TargetEnemyId = source.TargetEnemyId; Destination = source.Destination;
        AttackCooldown = source.AttackCooldown;
        Path = new(source.Path);
        PathTarget = source.PathTarget; PathEnemyId = source.PathEnemyId;
        PathIndex = source.PathIndex;
        PatrolWallId = source.PatrolWallId; PatrolCell = source.PatrolCell;
        PathRetrySeconds = source.PathRetrySeconds;
    }
}

public sealed partial class SettlementState
{
    internal readonly List<SettlementDefender> DefenderList = [];
    internal readonly Dictionary<Pos, double> RosterTimers = [];
    internal int NextDefenderId = 1;
    public IReadOnlyList<SettlementDefender> Defenders => DefenderList.AsReadOnly();
    public const int MaximumDefenders = 64;

    public static int RosterCapacity(BuildingType type) =>
        type.Name is "Archer Barracks" or "Guard Post" ? 4 : 0;

    private string? GarrisonSiteError(CityGame game, BuildingType type, CellRect area)
    {
        if (RosterCapacity(type) == 0) return null;
        if (!area.Cells().All(p => AcceptsArrivals(game, p)))
            return "Place the garrison inside connected, enclosed land with gate access.";
        return Walls.Where(w => w.IsGate).Any(gate => area.Cells().Any(p =>
            Math.Abs(p.X - gate.Cell.X) + Math.Abs(p.Y - gate.Cell.Y) < 3))
            ? "Keep the gate approach clear so defenders can reach the walls and exterior." : null;
    }

    internal string? GarrisonPlacementError(CityGame game, BuildingType type, CellRect area) => GarrisonSiteError(game, type, area);

    public ActionResult CommandDefenders(CityGame game, IReadOnlyCollection<int> ids, Pos? destination, int? enemyId)
    {
        _defenderPathCells.Clear();
        _defenderWalkHeights.Clear();
        var units = DefenderList.Where(d => ids.Contains(d.Id) && d.Health > 0).ToArray();
        if (units.Length == 0 || units.Length != ids.Distinct().Count())
            return ActionResult.Fail("Choose living defenders from this settlement.");
        if ((destination is null) == (enemyId is null))
            return ActionResult.Fail("Choose a destination or an enemy, not both.");
        var enemy = enemyId is { } id ? EnemyList.FirstOrDefault(e => e.Id == id && e.Health > 0) : null;
        if (enemyId is not null && enemy is null) return ActionResult.Fail("That enemy is no longer present.");
        Pos target = destination ?? new((int)enemy!.X, (int)enemy.Y);
        var paths = new List<List<Pos>>();
        foreach (var unit in units)
        {
            var path = DefenderPath(game, unit, target);
            if (path is null) return ActionResult.Fail("That destination cannot be reached through open ground and the gate.");
            paths.Add(path);
        }
        for (int i = 0; i < units.Length; i++)
        {
            var unit = units[i];
            unit.Order = enemy is null ? DefenderOrder.Move : DefenderOrder.Attack;
            unit.TargetEnemyId = enemy?.Id;
            unit.Destination = destination;
            unit.Path = paths[i];
            unit.PathIndex = 0;
            unit.PathTarget = target;
            unit.PathRetrySeconds = 0;
        }
        Revision++;
        return ActionResult.Ok(enemy is null ? "Defenders moving; automatic defense resumes on arrival." :
            "Defenders attacking; automatic defense resumes when the target falls.");
    }

    private bool GroundPassable(CityGame game, Pos p)
    {
        if (!game.Map.InBounds(p) || !TravelRoutes.Walkable(game.Map, game.Map.Index(p.X, p.Y)) ||
            game.Map.BuildingAt(p.X, p.Y) is not null) return false;
        return !GroundBlocked(new(p.X + .5f, p.Y + .5f));
    }

    private bool DefenderBodyBlocked(SettlementDefender unit, double x, double y, float elevation,
        float heightClearance = .3f)
    {
        double radiusSquared = Math.Pow(WorldScale.HumanRadius * 2, 2);
        foreach (var other in DefenderList)
        {
            if (other.Id == unit.Id || Math.Abs(elevation - other.Elevation) >= heightClearance) continue;
            double next = Math.Pow(x - other.X, 2) + Math.Pow(y - other.Y, 2);
            if (next >= radiusSquared) continue;
            double current = Math.Pow(unit.X - other.X, 2) + Math.Pow(unit.Y - other.Y, 2);
            double currentSeparation = current + Math.Pow(unit.Elevation - other.Elevation, 2);
            double nextSeparation = next + Math.Pow(elevation - other.Elevation, 2);
            if (current < radiusSquared && Math.Abs(unit.Elevation - other.Elevation) < heightClearance &&
                nextSeparation > currentSeparation + .000001 &&
                (currentSeparation > .000001 || unit.Id < other.Id)) continue;
            return true;
        }
        return false;
    }

    private WallComponent? WallWalkSupport(Pos p)
    {
        var original = WallAt(p);
        if (original is { IsOriginal: true, IsTower: true } && SupportsWallWalk(original)) return original;
        var point = new System.Numerics.Vector2(p.X + .5f, p.Y + .5f);
        var tower = WallsNear(point).Where(w => w.IsTower && SupportsWallWalk(w) &&
            WallDamageLayout.ContainsFootprint(w, point.X, point.Y, WorldScale.HumanRadius))
            .MinBy(w => System.Numerics.Vector2.DistanceSquared(point, w.Center));
        if (tower is not null) return tower;
        if (original is { IsOriginal: true } && SupportsWallWalk(original)) return original;
        return WallsNear(point, .5f).Where(w => !w.IsOriginal && SupportsWallWalk(w) &&
            WallDamageLayout.ContainsFootprint(w, point.X, point.Y, .45))
            .MinBy(w => World.WallNetwork.PointSegmentDistance(point, w.Start!.Value, w.End!.Value));
    }

    private WallComponent? LadderSupport(System.Numerics.Vector2 point) =>
        WallsNear(point, 1).Where(w => w.IsTower &&
            (WallDamageLayout.HasAccessLadder(w) &&
                System.Numerics.Vector2.DistanceSquared(point, WallDamageLayout.LadderClimbPosition(w)) <= .25f ||
             WallDamageLayout.WalkwayLadders(w).Any(ladder => System.Numerics.Vector2.DistanceSquared(point,
                ladder.Position + ladder.Direction * WallDamageLayout.LadderClimberOffset) <= .25f)))
            .MinBy(w => System.Numerics.Vector2.DistanceSquared(point, w.Center));

    private static System.Numerics.Vector2 WallWalkPoint(Pos p, WallComponent wall)
    {
        var point = new System.Numerics.Vector2(p.X + .5f, p.Y + .5f);
        if (wall.IsTower)
        {
            var half = new System.Numerics.Vector2(WorldScale.TowerWidth / 2 - .65f);
            return System.Numerics.Vector2.Clamp(point, wall.Center - half, wall.Center + half);
        }
        if (wall.Start is { } a && wall.End is { } b)
        {
            var delta = b - a;
            point = a + delta * Math.Clamp(System.Numerics.Vector2.Dot(point - a, delta) / delta.LengthSquared(), 0, 1);
        }
        return point + new System.Numerics.Vector2(-wall.Tangent.Y, wall.Tangent.X) * WallDamageLayout.WalkwayOffset(wall);
    }

    private bool JoinedWalkway(Pos p, WallComponent from, Pos next, WallComponent to)
    {
        if (from.IsOriginal && to.IsOriginal) return true;
        var a = WallWalkPoint(p, from); var b = WallWalkPoint(next, to);
        for (int step = 0; step <= 4; step++)
        {
            var point = System.Numerics.Vector2.Lerp(a, b, step / 4f);
            if (!WallsNear(point, WallDamageLayout.WalkwayWidth).Any(w => SupportsWallWalk(w) &&
                WallDamageLayout.ContainsWalkway(w, point.X, point.Y))) return false;
        }
        return true;
    }

    private readonly Dictionary<Pos, (bool Ground, WallComponent? Support)> _defenderPathCells = [];
    private readonly Dictionary<int, (Pos Cell, bool OnWall)[]> _defenderPathEdges = [];
    private readonly Dictionary<int, bool> _defenderInteriorCells = [];
    private readonly Dictionary<int, float> _defenderWalkHeights = [];
    private readonly Dictionary<int, (double X, double Y, WallComponent[] Posts)> _archerRangePosts = [];
    private sealed record ArcherReachability(bool StayInside, BitArray Transit, BitArray Nodes, int[] Barriers);
    private readonly List<ArcherReachability> _archerReachability = [];
    internal int ArcherReachabilityBuilds { get; private set; }
    private int _defenderPathNavigation = -1, _defenderPathTowerState;

    private void RefreshDefenderPathCells(CityGame game)
    {
        var towers = new HashCode();
        towers.Add(TopologyRevision);
        towers.Add(game.Map.TerrainVersion);
        towers.Add(EstablishedLand.Count);
        foreach (var wall in Walls)
        {
            towers.Add(wall.Id);
            towers.Add(wall.Material);
            towers.Add(WallDamageLayout.WalkHeight(wall) > 0);
            towers.Add(SupportsWallWalk(wall));
        }
        int state = towers.ToHashCode();
        if (_defenderPathNavigation == game.Map.NavigationVersion && _defenderPathTowerState == state) return;
        _defenderPathNavigation = game.Map.NavigationVersion;
        _defenderPathTowerState = state;
        _defenderPathCells.Clear();
        _defenderPathEdges.Clear();
        _defenderInteriorCells.Clear();
        _defenderWalkHeights.Clear();
        _archerRangePosts.Clear();
        _archerReachability.Clear();
        _garrisonNavigationVersion = -1;
    }

    private (bool Ground, WallComponent? Support) DefenderPathCell(CityGame game, Pos cell)
    {
        if (!_defenderPathCells.TryGetValue(cell, out var cached))
            _defenderPathCells[cell] = cached = (GroundPassable(game, cell), WallWalkSupport(cell));
        return cached;
    }

    private bool DefenderInteriorCell(CityGame game, Pos cell)
    {
        int key = game.Map.Index(cell.X, cell.Y);
        if (!_defenderInteriorCells.TryGetValue(key, out bool inside))
            _defenderInteriorCells[key] = inside = EstablishedAt(cell) || game.Map.HasFootbridge(cell.X, cell.Y) ||
                game.Map.HasRoad(cell.X, cell.Y) && ContainsInterior(cell) ||
                DefenderPathCell(game, cell).Support is not null || WallAt(cell)?.IsGate == true;
        return inside;
    }

    private (Pos Cell, bool OnWall)[] DefenderPathEdges(CityGame game, (Pos Cell, bool OnWall) current)
    {
        int key = game.Map.Index(current.Cell.X, current.Cell.Y) * 2 + (current.OnWall ? 1 : 0);
        if (_defenderPathEdges.TryGetValue(key, out var cached)) return cached;
        var edges = new List<(Pos Cell, bool OnWall)>();
        var p = current.Cell;
        var fromWall = DefenderPathCell(game, p).Support;
        foreach (var next in Neighbors(p))
        {
            var cell = DefenderPathCell(game, next);
            var toWall = cell.Support;
            for (int level = 0; level < 2; level++)
            {
                bool nextWall = level == 1;
                if (nextWall ? toWall is null : !cell.Ground) continue;
                bool onWall = current.OnWall;
                if (onWall && nextWall && !JoinedWalkway(p, fromWall!, next, toWall!)) continue;
                if (!onWall && !nextWall && !CanWalk(new(p.X + .5f, p.Y + .5f), new(next.X + .5f, next.Y + .5f))) continue;
                if (onWall != nextWall && !WallDamageLayout.HasAccessLadder(onWall ? fromWall! : toWall!)) continue;
                if (!onWall && nextWall && !EstablishedAt(p)) continue;
                if (onWall != nextWall)
                {
                    var access = onWall ? fromWall! : toWall!;
                    var ground = onWall ? next : p;
                    var offset = new System.Numerics.Vector2(ground.X + .5f, ground.Y + .5f) -
                        WallDamageLayout.LadderClimbPosition(access);
                    var inward = WallDamageLayout.LadderInward(access);
                    if (offset.LengthSquared() > 1.5f ||
                        Math.Abs(offset.X * inward.Y - offset.Y * inward.X) > .6f) continue;
                }
                edges.Add((next, nextWall));
            }
        }
        return _defenderPathEdges[key] = edges.ToArray();
    }

    internal void PrepareGarrisonNavigation(CityGame game)
    {
        if (!game.Map.BuildingFootprints.Values.Any(area =>
            RosterCapacity(game.Map.BuildingAt(area.X, area.Y)!) > 0)) return;
        RefreshDefenderPathCells(game);
        var bounds = WallBounds;
        for (int y = Math.Max(0, bounds.Y - 3); y <= Math.Min(game.Map.Height - 1, bounds.Bottom + 3); y++)
            for (int x = Math.Max(0, bounds.X - 3); x <= Math.Min(game.Map.Width - 1, bounds.Right + 3); x++)
            {
                var cell = new Pos(x, y);
                if (!_defenderPathCells.ContainsKey(cell))
                    _defenderPathCells.Add(cell, (GroundPassable(game, cell), WallWalkSupport(cell)));
            }
    }

    internal List<Pos>? DefenderPath(CityGame game, SettlementDefender unit, Pos target, bool ignoreBodies = false,
        bool stayInside = false)
    {
        using var paths = DefenderPaths(game, unit, [target], ignoreBodies, stayInside);
        return paths.PathTo(target);
    }

    internal sealed class DefenderSearchWorkspace(int nodes)
    {
        internal readonly int[] Visited = new int[nodes], Previous = new int[nodes], Queue = new int[nodes];
        internal int Generation;
        internal void Begin()
        {
            if (Generation == int.MaxValue) { Array.Clear(Visited); Generation = 0; }
            Generation++;
        }
    }
    private readonly Stack<DefenderSearchWorkspace> _defenderSearchPool = [];

    internal sealed class DefenderPathSearch : IDisposable
    {
        internal readonly Dictionary<Pos, int> Goals = [];
        private DefenderSearchWorkspace? _workspace;
        private Stack<DefenderSearchWorkspace>? _pool;
        private int _width, _first;
        private bool _disposed;
        internal int VisitedCount;
        internal Pos? StartGoal;
        internal bool Centered;
        internal IEnumerator<Pos>? Search;
        internal HashSet<Pos> Remaining = [];

        internal void Initialize(DefenderSearchWorkspace workspace, Stack<DefenderSearchWorkspace> pool, int width, int first)
        {
            _workspace = workspace; _pool = pool; _width = width; _first = first;
        }

        internal bool CanReach(Pos target)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (StartGoal == target) return true;
            while (!Goals.ContainsKey(target) && Remaining.Contains(target) && Search is { } search)
            {
                if (search.MoveNext()) continue;
                search.Dispose();
                Search = null;
            }
            return Goals.ContainsKey(target);
        }

        internal List<Pos>? PathTo(Pos target)
        {
            if (!CanReach(target)) return null;
            if (StartGoal == target) return Centered ? [] : [target];
            var node = Goals[target];
            var path = new List<Pos>();
            for (var step = node; step != _first; step = _workspace!.Previous[step])
                path.Add(new(step / 2 % _width, step / 2 / _width));
            path.Reverse();
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Search?.Dispose();
            Search = null;
            if (_workspace is { } workspace) _pool!.Push(workspace);
            _workspace = null;
            _pool = null;
        }
    }

    internal DefenderPathSearch DefenderPaths(CityGame game, SettlementDefender unit,
        IReadOnlyCollection<Pos> targets, bool ignoreBodies = false, bool stayInside = false)
    {
        RefreshDefenderPathCells(game);
        var result = new DefenderPathSearch();
        (bool Ground, WallComponent? Support, bool Passable) Cell(Pos p)
        {
            var cached = DefenderPathCell(game, p);
            var support = unit.Kind == DefenderKind.Archer ? cached.Support : null;
            return (cached.Ground, support, cached.Ground || support is not null);
        }
        var remaining = targets.Where(target => game.Map.InBounds(target) && Cell(target).Passable).ToHashSet();
        result.Remaining = remaining;
        if (remaining.Count == 0) return result;
        var start = new Pos((int)unit.X, (int)unit.Y);
        if (remaining.Remove(start))
        {
            result.StartGoal = start;
            result.Centered = Math.Abs(unit.X - start.X - .5) + Math.Abs(unit.Y - start.Y - .5) < .01;
            if (remaining.Count == 0) return result;
        }
        var first = (Cell: start, OnWall: unit.Kind == DefenderKind.Archer && unit.Elevation > .01f &&
            Cell(start).Support is not null);
        var workspace = _defenderSearchPool.Count > 0 ? _defenderSearchPool.Pop() :
            new DefenderSearchWorkspace(checked(game.Map.Width * game.Map.Height * 2));
        workspace.Begin();
        int firstNode = game.Map.Index(start.X, start.Y) * 2 + (first.OnWall ? 1 : 0);
        result.Initialize(workspace, _defenderSearchPool, game.Map.Width, firstNode);
        result.Search = Search().GetEnumerator();
        return result;

        IEnumerable<Pos> Search()
        {
            const double bodyRadius = WorldScale.HumanRadius * 2;
            var blockers = !ignoreBodies && unit.Kind == DefenderKind.Archer ? new HashSet<int>() : null;
            var bodies = new Dictionary<Pos, List<SettlementDefender>>();
            if (!ignoreBodies)
                foreach (var other in DefenderList)
                {
                    if (other.Id == unit.Id) continue;
                    for (int y = (int)Math.Floor(other.Y - bodyRadius); y <= (int)Math.Floor(other.Y + bodyRadius); y++)
                        for (int x = (int)Math.Floor(other.X - bodyRadius); x <= (int)Math.Floor(other.X + bodyRadius); x++)
                        {
                            var cell = new Pos(x, y);
                            if (!bodies.TryGetValue(cell, out var bucket)) bodies[cell] = bucket = [];
                            bucket.Add(other);
                        }
                }
            var previous = workspace.Previous;
            var visited = workspace.Visited;
            var queue = workspace.Queue;
            int head = 0, tail = 1, generation = workspace.Generation;
            queue[0] = firstNode;
            visited[firstNode] = generation;
            while (head < tail)
            {
                int currentNode = queue[head++];
                var current = (Cell: new Pos(currentNode / 2 % game.Map.Width, currentNode / 2 / game.Map.Width),
                    OnWall: currentNode % 2 != 0);
                foreach (var node in DefenderPathEdges(game, current))
                {
                    var next = node.Cell;
                    if (!game.Map.InBounds(next)) continue;
                    int nextNode = game.Map.Index(next.X, next.Y) * 2 + (node.OnWall ? 1 : 0);
                    if (visited[nextNode] == generation || unit.Kind != DefenderKind.Archer && node.OnWall ||
                        stayInside && !DefenderInteriorCell(game, next)) continue;
                    bool occupied = false;
                    if (!ignoreBodies)
                    {
                        var toWall = Cell(next).Support;
                        var destination = node.OnWall ? WallWalkPoint(next, toWall!) :
                            new System.Numerics.Vector2(next.X + .5f, next.Y + .5f);
                        if (bodies.TryGetValue(new((int)Math.Floor(destination.X), (int)Math.Floor(destination.Y)),
                            out var nearby))
                        {
                            float height = 0;
                            if (node.OnWall && !_defenderWalkHeights.TryGetValue(toWall!.Id, out height))
                            {
                                height = WallDamageLayout.ActorWalkHeight(toWall, game.Map);
                                _defenderWalkHeights[toWall.Id] = height;
                            }
                            foreach (var other in nearby)
                                if (Math.Abs(other.Elevation - height) < .3f &&
                                    Math.Pow(other.X - destination.X, 2) + Math.Pow(other.Y - destination.Y, 2) <
                                        bodyRadius * bodyRadius)
                                {
                                    occupied = true;
                                    break;
                                }
                        }
                        if (occupied)
                        {
                            blockers?.Add(nextNode);
                            if (!remaining.Contains(next)) continue;
                        }
                    }
                    previous[nextNode] = currentNode;
                    visited[nextNode] = generation;
                    result.VisitedCount++;
                    if (remaining.Remove(next))
                    {
                        result.Goals.Add(next, nextNode);
                        yield return next;
                        if (remaining.Count == 0) yield break;
                    }
                    // An occupied goal is a valid endpoint, never a route through another defender.
                    if (!occupied) queue[tail++] = nextNode;
                }
            }
            if (blockers is not null)
            {
                var transit = new BitArray(checked(game.Map.Width * game.Map.Height * 2));
                for (int i = 0; i < tail; i++) transit[queue[i]] = true;
                var nodes = new BitArray(transit);
                foreach (int node in blockers) nodes[node] = true;
                var barriers = blockers.Where(node => DefenderPathEdges(game,
                    (new(node / 2 % game.Map.Width, node / 2 / game.Map.Width), node % 2 != 0)).Any(next =>
                    game.Map.InBounds(next.Cell) && (!stayInside || DefenderInteriorCell(game, next.Cell)) &&
                    !nodes[game.Map.Index(next.Cell.X, next.Cell.Y) * 2 + (next.OnWall ? 1 : 0)])).ToArray();
                if (_archerReachability.Count == 4) _archerReachability.RemoveAt(0);
                _archerReachability.Add(new(stayInside, transit, nodes, barriers));
                ArcherReachabilityBuilds++;
            }
        }
    }

    private static bool SupportsWallWalk(WallComponent wall) => wall.Health > 0 &&
        (!(wall.IsGate || wall.IsGateWing) || WallDamageLayout.DamageBand(wall) < 4);

    public bool DefenderCanReach(CityGame game, SettlementDefender unit, Pos target)
    {
        _defenderPathCells.Clear();
        _defenderWalkHeights.Clear();
        return DefenderPath(game, unit, target) is not null;
    }

    internal bool SavedDefenderCellValid(GameMap map, DefenderKind kind, Pos cell)
    {
        if (!map.InBounds(cell)) return false;
        if (TravelRoutes.Walkable(map, map.Index(cell.X, cell.Y))) return true;
        var point = new System.Numerics.Vector2(cell.X + .5f, cell.Y + .5f);
        return kind == DefenderKind.Archer && WallsNear(point, .5f).Any(w =>
            WallDamageLayout.ContainsFootprint(w, point.X, point.Y, .5f));
    }

    private IOrderedEnumerable<WallComponent> ArcherPosts(SettlementDefender unit, SiegeEnemy enemy,
        IEnumerable<WallComponent>? posts = null) =>
        (posts ?? Walls.Where(w => SupportsWallWalk(w) && !w.IsGate && !w.IsGateWing))
            .OrderBy(w => Math.Pow(w.Center.X - enemy.X, 2) + Math.Pow(w.Center.Y - enemy.Y, 2) +
                    DefenderList.Where(other => other.Id != unit.Id).Sum(other =>
                        80 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))))
            .ThenBy(w => w.Cell.Y).ThenBy(w => w.Cell.X);

    private WallComponent[] ArcherRangePosts(SiegeEnemy enemy)
    {
        if (_archerRangePosts.TryGetValue(enemy.Id, out var cached) && cached.X == enemy.X && cached.Y == enemy.Y)
            return cached.Posts;
        var posts = Walls.Where(post => SupportsWallWalk(post) && !post.IsGate && !post.IsGateWing)
            .Where(post =>
            {
                var point = WallWalkPoint(post.Cell, post);
                return Math.Pow(point.X - enemy.X, 2) + Math.Pow(point.Y - enemy.Y, 2) <=
                    Math.Pow(CombatProfiles.Archer.Range, 2);
            }).ToArray();
        _archerRangePosts[enemy.Id] = (enemy.X, enemy.Y, posts);
        return posts;
    }

    private BitArray? ArcherReachabilityUpperBound(CityGame game, SettlementDefender unit, bool stayInside)
    {
        const double bodyRadius = WorldScale.HumanRadius * 2;
        var cell = new Pos((int)unit.X, (int)unit.Y);
        bool onWall = unit.Elevation > .01f && DefenderPathCell(game, cell).Support is not null;
        int start = game.Map.Index(cell.X, cell.Y) * 2 + (onWall ? 1 : 0);
        foreach (var cached in _archerReachability)
        {
            if (cached.StayInside != stayInside || !cached.Transit[start]) continue;
            bool closed = true;
            foreach (int barrier in cached.Barriers)
            {
                var at = new Pos(barrier / 2 % game.Map.Width, barrier / 2 / game.Map.Width);
                var support = barrier % 2 != 0 ? DefenderPathCell(game, at).Support : null;
                var point = support is null ? new System.Numerics.Vector2(at.X + .5f, at.Y + .5f) : WallWalkPoint(at, support);
                float height = support is null ? 0 : WallDamageLayout.ActorWalkHeight(support, game.Map);
                bool occupied = false;
                foreach (var other in DefenderList)
                    if (other.Id != unit.Id && Math.Abs(other.Elevation - height) < .3f &&
                        Math.Pow(other.X - point.X, 2) + Math.Pow(other.Y - point.Y, 2) <
                            bodyRadius * bodyRadius)
                    {
                        occupied = true;
                        break;
                    }
                if (occupied) continue;
                closed = false;
                break;
            }
            // Only reject outside a fully explored region whose blocked exits remain closed.
            // Extra bodies inside it can remove routes, but cannot create an outside route.
            if (closed) return cached.Nodes;
        }
        return null;
    }

    private (SiegeEnemy Enemy, Pos? Goal, List<Pos>? Path)? AssignArcherTarget(CityGame game,
        SettlementDefender unit, WallComponent? support)
    {
        RefreshDefenderPathCells(game);
        var candidates = DefenseTargets(unit).ToArray();
        Dictionary<int, WallComponent[]>? eligiblePosts = null;
        DefenderPathSearch? paths = null;
        HashSet<Pos>? occupiedPosts = null;
        bool stayInside = unit.Elevation > .01f || EstablishedAt(new((int)unit.X, (int)unit.Y));
        var reachable = ArcherReachabilityUpperBound(game, unit, stayInside);
        try
        {
        foreach (var candidate in candidates)
        {
            if (unit.PathEnemyId == candidate.Id && unit.PathIndex < unit.Path.Count &&
                unit.PathTarget is { } goal && Math.Pow(goal.X + .5 - candidate.X, 2) +
                    Math.Pow(goal.Y + .5 - candidate.Y, 2) <= Math.Pow(CombatProfiles.Archer.Range, 2))
                return (candidate, null, null);
            if (ArcherHasShot(game, unit, support, candidate)) return (candidate, null, null);
            if (occupiedPosts is null)
            {
                occupiedPosts = [];
                foreach (var other in DefenderList)
                {
                    if (other.Id == unit.Id) continue;
                    int x = (int)Math.Floor(other.X), y = (int)Math.Floor(other.Y);
                    if (Math.Pow(other.X - x - .5, 2) + Math.Pow(other.Y - y - .5, 2) <= .12)
                        occupiedPosts.Add(new(x, y));
                }
            }
            eligiblePosts ??= candidates.ToDictionary(enemy => enemy.Id, enemy =>
            {
                var posts = ArcherRangePosts(enemy).Where(post =>
                    game.Map.InBounds(post.Cell) && !occupiedPosts.Contains(post.Cell)).ToArray();
                if (posts.Length == 0) return posts;
                if (reachable is null) return posts;
                return posts.Where(post =>
                {
                    int node = game.Map.Index(post.Cell.X, post.Cell.Y) * 2;
                    return reachable[node] || reachable[node + 1];
                }).ToArray();
            });
            var posts = eligiblePosts[candidate.Id];
            if (posts.Length == 0) continue;
            paths ??= DefenderPaths(game, unit, eligiblePosts.Values.SelectMany(posts => posts)
                .Select(post => post.Cell).Distinct().ToArray(), stayInside: stayInside);
            foreach (var post in ArcherPosts(unit, candidate, posts.Where(post => paths.CanReach(post.Cell))))
            {
                var path = paths.PathTo(post.Cell);
                if (path is not null) return (candidate, post.Cell, path);
            }
        }
        return null;
        }
        finally { paths?.Dispose(); }
    }

    private bool ArcherHasShot(CityGame game, SettlementDefender unit, WallComponent? support, SiegeEnemy enemy) =>
        support is not null && unit.Elevation > .5f && unit.Elevation == unit.PreviousElevation &&
        Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2) <= Math.Pow(CombatProfiles.Archer.Range, 2) &&
        WallDamageLayout.ContainsWalkway(support, unit.X, unit.Y) &&
        (!support.IsTower || WallDamageLayout.ContainsFootprint(support, unit.X, unit.Y, -WorldScale.HumanRadius)) &&
        Math.Abs(unit.Elevation - WallDamageLayout.ActorWalkHeight(support, game.Map)) < .1f &&
        TryMissile(game, enemy, WallDefenseKind.None,
            new((float)unit.X, Ground(game, new((float)unit.X, (float)unit.Y)) + unit.Elevation +
                WorldScale.Feet(4.5f), (float)unit.Y), CombatProfiles.Archer.Damage, support.Id, unit.Id, commit: false);

    private HashSet<Pos>? _garrisonGround;
    private int _garrisonNavigationVersion = -1;

    private HashSet<Pos> GateGroundReachable(CityGame game)
    {
        if (_garrisonGround is not null && _garrisonNavigationVersion == game.Map.NavigationVersion)
            return _garrisonGround;
        var reached = new HashSet<Pos>();
        var queue = new Queue<Pos>();
        foreach (var gate in Walls.Where(w => w.IsGate).Select(w => w.Cell))
            if (GroundPassable(game, gate) && reached.Add(gate)) queue.Enqueue(gate);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Neighbors(cell))
                if (!reached.Contains(next) && (EstablishedAt(next) || game.Map.HasFootbridge(next.X, next.Y) ||
                    game.Map.HasRoad(next.X, next.Y) && ContainsInterior(next)) && GroundPassable(game, next) &&
                    CanWalk(new(cell.X + .5f, cell.Y + .5f), new(next.X + .5f, next.Y + .5f)))
                {
                    reached.Add(next);
                    queue.Enqueue(next);
                }
        _garrisonNavigationVersion = game.Map.NavigationVersion;
        return _garrisonGround = reached;
    }

    private bool GroundFightVisible(CityGame game, double ax, double ay, double bx, double by)
    {
        int samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2)) * 4));
        for (int n = 1; n < samples; n++)
        {
            double t = n / (double)samples;
            if (!GroundPassable(game, new((int)(ax + (bx - ax) * t), (int)(ay + (by - ay) * t)))) return false;
        }
        return true;
    }

    private CityGame? _defenderGame;
    internal void BindGarrisonGame(CityGame game) => _defenderGame = game;
    internal bool GarrisonHomeExists(Pos home) => _defenderGame is { } game && game.Map.InBounds(home) &&
        game.Map.BuildingAt(home.X, home.Y) is { } type && RosterCapacity(type) > 0;
    public static double MeleeDistance(SiegeEnemy enemy) =>
        enemy.Kind == SiegeEnemyKind.Orc ? OrcCombatAnimation.WallReach + WorldScale.HumanRadius :
        Math.Max(CombatProfiles.Guard.Range, enemy.CollisionRadius + WorldScale.HumanRadius + 0.25);

    internal SettlementDefender? EngagedDefender(SiegeEnemy enemy) => _defenderGame is { } game
        ? DefenderList.Where(d => d.Health > 0 && d.Elevation < 0.3f &&
            Math.Pow(d.X - enemy.X, 2) + Math.Pow(d.Y - enemy.Y, 2) <= Math.Pow(MeleeDistance(enemy), 2) &&
            GroundFightVisible(game, d.X, d.Y, enemy.X, enemy.Y))
            .OrderBy(d => Math.Pow(d.X - enemy.X, 2) + Math.Pow(d.Y - enemy.Y, 2)).ThenBy(d => d.Id).FirstOrDefault()
        : null;

    internal bool IsDefenderEngaged(SiegeEnemy enemy) => EngagedDefender(enemy) is not null;

    internal bool DefenderIntercepts(SiegeEnemy enemy, double damage)
    {
        if (EngagedDefender(enemy) is not { } defender) return false;
        defender.Health = Math.Max(0, defender.Health - damage);
        if (defender.Health <= 0)
        {
            RosterTimers[defender.Home] = 18;
            RememberDeath(new(new((float)defender.X, (float)defender.Y), defender.Elevation, true,
                defender.Kind, default, 1.8f, enemy.Definition.IsTroll ? SiegeDeathCause.Crushing : SiegeDeathCause.Blade,
                Away(new((float)defender.X, (float)defender.Y), new((float)enemy.X, (float)enemy.Y))));
        }
        return true;
    }

    internal void AdvanceDefenders(CityGame game, double seconds)
    {
        if (game.Paused || seconds <= 0) return;
        RefreshDefenderPathCells(game);
        _defenderWalkHeights.Clear();
        var enemies = EnemyList.Select(enemy => enemy.Id).ToHashSet();
        foreach (var stale in _archerRangePosts.Keys.Where(id => !enemies.Contains(id)).ToArray())
            _archerRangePosts.Remove(stale);
        _defenderGame = game;
        var homes = game.Map.BuildingFootprints.Values.Where(a => EstablishedAt(new(a.X, a.Y)) &&
            RosterCapacity(game.Map.BuildingAt(a.X, a.Y)!) > 0).ToArray();
        var owners = homes.Select(a => new Pos(a.X, a.Y)).ToHashSet();
        DefenderList.RemoveAll(d => d.Health <= 0 || !owners.Contains(d.Home));
        foreach (var stale in RosterTimers.Keys.Where(home => !owners.Contains(home)).ToArray()) RosterTimers.Remove(stale);
        HashSet<Pos>? reachableSpawns = null;
        foreach (var area in homes)
        {
            var home = new Pos(area.X, area.Y);
            var type = game.Map.BuildingAt(home.X, home.Y)!;
            int index = game.Map.Index(home.X, home.Y);
            if (!CityProgression.IsUnlocked(game, type) || game.Insolvent ||
                game.Budget.Effective(ServiceKind.Defence, game.Insolvent) <= 0 ||
                !game.Services.IsPowered(game.Map, index) || !game.Services.IsWatered(game.Map, index)) continue;
            RosterTimers[home] = Math.Max(0, RosterTimers.GetValueOrDefault(home) - seconds);
            if (DefenderList.Count >= MaximumDefenders || RosterTimers[home] > 0 ||
                DefenderList.Count(d => d.Home == home) >= RosterCapacity(type)) continue;
            reachableSpawns ??= GateGroundReachable(game);
            var spawn = area.Cells().SelectMany(Neighbors).Distinct().Where(p => reachableSpawns.Contains(p) &&
                DefenderList.All(d => Math.Pow(d.X - p.X - 0.5, 2) + Math.Pow(d.Y - p.Y - 0.5, 2) > 0.12))
                .OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => (Pos?)p).FirstOrDefault();
            if (spawn is not { } cell) continue;
            DefenderList.Add(new SettlementDefender
            {
                Id = NextDefenderId++,
                Home = home,
                Kind = type.Name == "Archer Barracks" ? DefenderKind.Archer : DefenderKind.Soldier,
                X = cell.X + 0.5,
                Y = cell.Y + 0.5,
                PreviousX = cell.X + 0.5,
                PreviousY = cell.Y + 0.5
            });
            RosterTimers[home] = 2;
        }
        var stagingSide = DefenderStagingSide;
        long patrolStarted = game.ProfileUpdates ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        AssignPatrols(game, seconds, stagingSide);
        if (game.ProfileUpdates)
            game.LastPatrolAssignmentMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(patrolStarted).TotalMilliseconds;
        foreach (var unit in DefenderList.OrderBy(d => d.Id))
        {
            var nearestGate = Walls.Where(w => w.IsGate).MinBy(w =>
                Math.Pow(w.Center.X - unit.X, 2) + Math.Pow(w.Center.Y - unit.Y, 2));
            var gate = nearestGate?.Cell ?? unit.Home;
            unit.PreviousX = unit.X; unit.PreviousY = unit.Y; unit.PreviousElevation = unit.Elevation;
            unit.PathRetrySeconds = Math.Max(0, unit.PathRetrySeconds - seconds);
            var supportCell = new Pos((int)unit.X, (int)unit.Y);
            var ladderSupport = unit.Kind == DefenderKind.Archer && unit.Elevation > .01f
                ? LadderSupport(new((float)unit.X, (float)unit.Y)) : null;
            var currentSupport = ladderSupport ?? WallWalkSupport(supportCell) ?? WallAt(supportCell);
            if (unit.Elevation > .5f && currentSupport is { Health: <= 0 })
            {
                RememberDeath(new(new((float)unit.X, (float)unit.Y), unit.Elevation, true, unit.Kind,
                    default, 1.8f, SiegeDeathCause.Fall, new(-currentSupport.Tangent.Y, currentSupport.Tangent.X)));
                unit.Health = 0;
                RosterTimers[unit.Home] = 18;
                continue;
            }
            if (unit.Elevation > 0 && currentSupport is { } support &&
                (unit.PathIndex >= unit.Path.Count || !SupportsWallWalk(support)))
            {
                float height = support.Health > 0 && SupportsWallWalk(support)
                    ? Math.Max(0, WallDamageLayout.ActorWalkHeight(support, game.Map) +
                        TerrainSprite.Elevation(game.Map, support.Cell.X, support.Cell.Y) -
                        TerrainSprite.Elevation(game.Map, supportCell.X, supportCell.Y)) : 0;
                unit.Elevation += Math.Clamp(height - unit.Elevation, (float)(-seconds * 1.5), (float)(seconds * 1.5));
            }
            unit.AttackCooldown = Math.Max(0, unit.AttackCooldown - seconds);
            var enemy = unit.TargetEnemyId is { } id ? EnemyList.FirstOrDefault(g => g.Id == id && g.Health > 0) : null;
            if (unit.Order == DefenderOrder.Attack && enemy is null)
            {
                unit.Order = DefenderOrder.Automatic; unit.TargetEnemyId = null; unit.PathTarget = null;
            }
            if (unit.Order == DefenderOrder.Automatic)
            {
                var assignment = unit.Kind == DefenderKind.Archer ? AssignArcherTarget(game, unit, currentSupport) : null;
                enemy = unit.Kind == DefenderKind.Archer ? assignment?.Enemy : AssignGuardTarget(game, unit);
                unit.TargetEnemyId = enemy?.Id;
                if (unit.PathEnemyId != enemy?.Id)
                {
                    unit.PathEnemyId = enemy?.Id;
                    unit.Path.Clear();
                    unit.PathIndex = 0;
                    unit.PathTarget = null;
                    unit.PathRetrySeconds = 0;
                }
                if (assignment is { Path: { } path, Goal: { } assignmentGoal })
                {
                    unit.Path = path;
                    unit.PathIndex = 0;
                    unit.PathTarget = assignmentGoal;
                }
            }
            var profile = CombatProfiles.Fighter(unit.Kind);
            double range = unit.Kind == DefenderKind.Archer ? profile.Range : enemy is null ? profile.Range : MeleeDistance(enemy);
            if (enemy is not null && unit.Order != DefenderOrder.Move &&
                Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2) <= range * range &&
                (unit.Kind != DefenderKind.Archer || ArcherHasShot(game, unit, currentSupport, enemy)) &&
                (unit.Kind == DefenderKind.Archer || GroundFightVisible(game, unit.X, unit.Y, enemy.X, enemy.Y)))
            {
                if (unit.AttackCooldown <= 0)
                {
                    if (ProjectileList.Count >= MaximumPendingAttacks) continue;
                    if (unit.Kind == DefenderKind.Archer)
                    {
                        if (!TryMissile(game, enemy, WallDefenseKind.None,
                            new((float)unit.X, Ground(game, new((float)unit.X, (float)unit.Y)) + unit.Elevation + WorldScale.Feet(4.5f), (float)unit.Y),
                            profile.Damage, currentSupport?.Id ?? 0, unit.Id)) continue;
                    }
                    else AddAttack(new SiegeProjectile
                    {
                        Melee = true,
                        SourceDefenderId = unit.Id,
                        TargetId = enemy.Id,
                        Damage = profile.Damage,
                        Windup = profile.Windup,
                        StartX = unit.X,
                        StartY = unit.Y,
                        EndX = enemy.X,
                        EndY = enemy.Y
                    });
                    unit.AttackCooldown = profile.Interval;
                }
                continue;
            }
            Pos goal;
            if (unit.Order == DefenderOrder.Move && unit.Destination is { } destination) goal = destination;
            else if (unit.Kind == DefenderKind.Archer)
            {
                goal = enemy is null ? unit.PatrolCell ?? new Pos((int)unit.X, (int)unit.Y) :
                    unit.PathIndex < unit.Path.Count && unit.PathTarget is { } response
                    ? response : ArcherPosts(unit, enemy)
                    .Where(w => DefenderList.All(other => other.Id == unit.Id ||
                        Math.Pow(other.X - w.Cell.X - 0.5, 2) + Math.Pow(other.Y - w.Cell.Y - 0.5, 2) > 0.12))
                    .Select(w => (Pos?)w.Cell).FirstOrDefault() ?? gate;
                if (enemy is null && unit.PatrolWallId is { } assigned &&
                    Walls.FirstOrDefault(w => w.Id == assigned && SupportsWallWalk(w)) is { } patrol)
                    goal = unit.PatrolCell ?? patrol.Cell;
                else if (enemy is null)
                    unit.PatrolWallId = Walls.FirstOrDefault(w => w.Cell == goal)?.Id;
            }
            else if (enemy is null || !EstablishedAt(new((int)enemy.X, (int)enemy.Y)))
            {
                var posts = Walls.Where(w => w.Health > 0 && (w.IsGate || w.IsTower && (w.JunctionTower ||
                    (w.Cell.X == Bounds.X || w.Cell.X == Bounds.Right) && (w.Cell.Y == Bounds.Y || w.Cell.Y == Bounds.Bottom)) ||
                    stagingSide is { } approach && OnApproachSide(w, approach)))
                    .OrderBy(w => stagingSide is { } side && !OnApproachSide(w, side) ? 1 : 0)
                    .ThenBy(w => DefenderList.Where(other => other.Id != unit.Id && other.Kind == DefenderKind.Soldier)
                        .Sum(other => 1000 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))) +
                        Math.Sqrt(Math.Pow(unit.Home.X - w.Center.X, 2) + Math.Pow(unit.Home.Y - w.Center.Y, 2)) -
                        (w.IsGate ? 12 : 0) + DefenderList.Count(other => other.Id != unit.Id &&
                            other.Kind == DefenderKind.Soldier && other.PatrolWallId == w.Id) * 200).ToArray();
                var post = posts.FirstOrDefault();
                if (enemy is null && unit.PatrolWallId is { } assigned &&
                    Walls.FirstOrDefault(w => w.Id == assigned && w.Health > 0 &&
                        (stagingSide is not { } side || OnApproachSide(w, side))) is { } patrol) post = patrol;
                else if (enemy is null) unit.PatrolWallId = post?.Id;
                var center = post?.Center ?? new System.Numerics.Vector2(gate.X + .5f, gate.Y + .5f);
                goal = new((int)unit.X, (int)unit.Y);
                var reachable = GateGroundReachable(game);
                var spots = new CellRect((int)center.X - 4, (int)center.Y - 4, 9, 9).Cells()
                    .Where(p => reachable.Contains(p) && DefenderList.All(other =>
                        other.Id == unit.Id || Math.Pow(other.X - p.X - .5, 2) + Math.Pow(other.Y - p.Y - .5, 2) > 1) &&
                        Walls.Where(w => w.IsGate).All(w => Math.Abs(
                            (p.X + .5 - w.Center.X) * w.Tangent.X + (p.Y + .5 - w.Center.Y) * w.Tangent.Y) > 2 ||
                            System.Numerics.Vector2.DistanceSquared(new(p.X + .5f, p.Y + .5f), w.Center) > 25))
                    .OrderBy(p => Math.Pow(p.X + .5 - center.X, 2) + Math.Pow(p.Y + .5 - center.Y, 2))
                    .Select(p => (Pos?)p).FirstOrDefault();
                if (spots is { } spot) goal = spot;
            }
            else goal = new((int)enemy.X, (int)enemy.Y);
            if (unit.Order == DefenderOrder.Automatic && enemy is null &&
                unit.PathIndex < unit.Path.Count && unit.PathTarget is { } underway)
                goal = underway;
            if (unit.PathTarget != goal || unit.PathIndex >= unit.Path.Count)
            {
                if (unit.Order == DefenderOrder.Automatic && unit.PathTarget == goal && unit.PathRetrySeconds > 0)
                    continue;
                var path = DefenderPath(game, unit, goal, stayInside: unit.Order == DefenderOrder.Automatic &&
                    (unit.Elevation > .01f || EstablishedAt(new((int)unit.X, (int)unit.Y))));
                if (path is null && unit.Order != DefenderOrder.Automatic)
                {
                    unit.Order = DefenderOrder.Automatic; unit.TargetEnemyId = null; unit.Destination = null;
                    game.Report(new CityEvent(game.Week, EventKind.Raid,
                        $"Defender {unit.Id}'s route is blocked; automatic defense has resumed.", [], Bad: false));
                }
                unit.Path = path ?? [];
                unit.PathTarget = goal; unit.PathIndex = 0;
                unit.PathRetrySeconds = path is null ? .5 : 0;
            }
            if (unit.PathIndex < unit.Path.Count)
            {
                var next = unit.Path[unit.PathIndex];
                if (!GroundPassable(game, next) && !(unit.Kind == DefenderKind.Archer &&
                    WallWalkSupport(next) is { } nextWall && SupportsWallWalk(nextWall)))
                { unit.PathTarget = null; continue; }
                var nextSupport = WallWalkSupport(next);
                bool wallStep = unit.Kind == DefenderKind.Archer && nextSupport is { Health: > 0 } &&
                    (!GroundPassable(game, next) || unit.Elevation > .01f);
                bool onWall = unit.Kind == DefenderKind.Archer && unit.Elevation > .01f &&
                    currentSupport is not null && SupportsWallWalk(currentSupport);
                if (unit.Kind == DefenderKind.Archer && onWall != wallStep &&
                    !WallDamageLayout.HasAccessLadder(onWall ? currentSupport! : nextSupport!))
                {
                    unit.PathTarget = null;
                    continue;
                }
                float wantedHeight = wallStep && WallWalkSupport(next) is { } wall
                    ? Math.Max(0, WallDamageLayout.ActorWalkHeight(wall, game.Map) +
                        TerrainSprite.Elevation(game.Map, wall.Cell.X, wall.Cell.Y) -
                        TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y)) : 0;
                WallComponent? access = wallStep && nextSupport is { IsTower: true } &&
                    (unit.Elevation < .01f || ladderSupport is not null && unit.Elevation < wantedHeight - .01f &&
                        System.Numerics.Vector2.DistanceSquared(new((float)unit.X, (float)unit.Y),
                            WallDamageLayout.LadderClimbPosition(nextSupport)) <= .25f)
                    ? nextSupport : !wallStep && unit.Elevation > .01f && currentSupport is { IsTower: true }
                        ? currentSupport : null;
                System.Numerics.Vector2? climbPosition = access is null ? null : WallDamageLayout.LadderClimbPosition(access);
                var towerAccess = currentSupport is { IsTower: true } ? currentSupport :
                    nextSupport is { IsTower: true } ? nextSupport : null;
                if (climbPosition is null && wallStep && unit.Elevation > .01f && towerAccess is not null &&
                    Math.Abs(wantedHeight - unit.Elevation) > .01f)
                {
                    var sideLadder = WallDamageLayout.WalkwayLadders(towerAccess)
                        .OrderBy(ladder => System.Numerics.Vector2.DistanceSquared(ladder.Position,
                            new((float)unit.X, (float)unit.Y))).Select(ladder => (TowerWalkwayLadder?)ladder).FirstOrDefault();
                    if (sideLadder is { } connection)
                        climbPosition = connection.Position + connection.Direction * WallDamageLayout.LadderClimberOffset;
                }
                if (climbPosition is { } ladder)
                {
                    var from = new System.Numerics.Vector2((float)unit.X, (float)unit.Y);
                    float remaining = System.Numerics.Vector2.Distance(from, ladder);
                    if (remaining > .01f)
                    {
                        float amount = Math.Min(remaining, (float)(seconds * SettlementDefender.MovementSpeed));
                        var position = from + System.Numerics.Vector2.Normalize(ladder - from) * amount;
                        if (!DefenderBodyBlocked(unit, position.X, position.Y, unit.Elevation))
                        {
                            float worldHeight = TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y) + unit.Elevation;
                            bool elevated = unit.Elevation > .01f;
                            unit.X = position.X;
                            unit.Y = position.Y;
                            if (elevated) unit.Elevation = Math.Max(0, worldHeight -
                                TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y));
                        }
                        continue;
                    }
                }
                float heightStep = (float)(seconds * SettlementDefender.ClimbSpeed);
                bool changingHeight = Math.Abs(unit.Elevation - wantedHeight) > .01f;
                float nextElevation = unit.Elevation + Math.Clamp(wantedHeight - unit.Elevation, -heightStep, heightStep);
                if (changingHeight && DefenderBodyBlocked(unit, unit.X, unit.Y, nextElevation, WorldScale.HumanHeight))
                    continue;
                unit.Elevation = nextElevation;
                if (changingHeight) continue;
                var destinationPoint = wallStep ? WallWalkPoint(next, WallWalkSupport(next)!) :
                    new System.Numerics.Vector2(next.X + .5f, next.Y + .5f);
                double dx = destinationPoint.X - unit.X, dy = destinationPoint.Y - unit.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy), step = Math.Min(distance, seconds * SettlementDefender.MovementSpeed);
                if (distance > 0.00001)
                {
                    double x = unit.X + dx / distance * step, y = unit.Y + dy / distance * step;
                    if (DefenderBodyBlocked(unit, x, y, unit.Elevation))
                    {
                        unit.PathTarget = goal;
                        unit.Path.Clear();
                        unit.PathIndex = 0;
                        unit.PathRetrySeconds = .5;
                        continue;
                    }
                    if (unit.Elevation < 0.3f && EnemyList.Any(other => other.Health > 0 &&
                        Math.Pow(x - other.X, 2) + Math.Pow(y - other.Y, 2) <
                        Math.Pow(WorldScale.HumanRadius + other.CollisionRadius, 2))) continue;
                    float worldHeight = TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y) + unit.Elevation;
                    unit.X = x; unit.Y = y;
                    if (wallStep) unit.Elevation = Math.Max(0, worldHeight -
                        TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y));
                }
                if (distance <= step + 0.00001) unit.PathIndex++;
            }
            var arrival = unit.Kind == DefenderKind.Archer && unit.Elevation > .01f && WallWalkSupport(goal) is { } goalWall
                ? WallWalkPoint(goal, goalWall) : new System.Numerics.Vector2(goal.X + .5f, goal.Y + .5f);
            if (unit.Order == DefenderOrder.Move && unit.PathIndex >= unit.Path.Count &&
                Math.Abs(unit.X - arrival.X) + Math.Abs(unit.Y - arrival.Y) < 0.01)
            {
                unit.Order = DefenderOrder.Automatic; unit.Destination = null; unit.PathTarget = null;
            }
        }
    }
}
