using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

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

    private List<Pos>? DefenderPath(CityGame game, SettlementDefender unit, Pos target)
    {
        if (!game.Map.InBounds(target)) return null;
        var cells = new Dictionary<Pos, (bool Ground, WallComponent? Support, bool Passable)>();
        (bool Ground, WallComponent? Support, bool Passable) Cell(Pos p)
        {
            if (cells.TryGetValue(p, out var cached)) return cached;
            bool ground = GroundPassable(game, p);
            var support = unit.Kind == DefenderKind.Archer ? WallWalkSupport(p) : null;
            bool passable = ground || support is not null;
            return cells[p] = (ground, support, passable);
        }
        if (!Cell(target).Passable) return null;
        var start = new Pos((int)unit.X, (int)unit.Y);
        if (start == target)
            return Math.Abs(unit.X - target.X - .5) + Math.Abs(unit.Y - target.Y - .5) < .01 ? [] : [target];
        var first = (Cell: start, OnWall: unit.Kind == DefenderKind.Archer && unit.Elevation > .01f &&
            Cell(start).Support is not null);
        var previous = new Dictionary<(Pos Cell, bool OnWall), (Pos Cell, bool OnWall)>();
        var visited = new HashSet<(Pos Cell, bool OnWall)> { first };
        var queue = new Queue<(Pos Cell, bool OnWall)>();
        queue.Enqueue(first);
        while (queue.TryDequeue(out var current))
        {
            var p = current.Cell;
            foreach (var next in Neighbors(p))
            {
                var cell = Cell(next);
                if (!cell.Passable) continue;
                var fromWall = Cell(p).Support;
                var toWall = cell.Support;
                for (int level = 0; level < (unit.Kind == DefenderKind.Archer ? 2 : 1); level++)
                {
                    bool nextWall = level == 1;
                    if (nextWall ? toWall is null : !cell.Ground) continue;
                    if (next != target)
                    {
                        var destination = nextWall ? WallWalkPoint(next, toWall!) :
                            new System.Numerics.Vector2(next.X + .5f, next.Y + .5f);
                        float height = nextWall ? WallDamageLayout.ActorWalkHeight(toWall!, game.Map) : 0;
                        if (DefenderList.Any(other => other.Id != unit.Id &&
                            Math.Abs(other.Elevation - height) < .3f &&
                            Math.Pow(other.X - destination.X, 2) + Math.Pow(other.Y - destination.Y, 2) <
                                Math.Pow(WorldScale.HumanRadius * 2, 2))) continue;
                    }
                    var node = (Cell: next, OnWall: nextWall);
                    if (visited.Contains(node)) continue;
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
                            Math.Abs(offset.X * inward.Y - offset.Y * inward.X) > .6f)
                            continue;
                    }
                    previous[node] = current;
                    visited.Add(node);
                    if (next == target)
                    {
                        var path = new List<Pos>();
                        for (var step = node; step != first; step = previous[step]) path.Add(step.Cell);
                        path.Reverse();
                        return path;
                    }
                    queue.Enqueue(node);
                }
            }
        }
        return null;
    }

    private static bool SupportsWallWalk(WallComponent wall) => wall.Health > 0 &&
        (!(wall.IsGate || wall.IsGateWing) || WallDamageLayout.DamageBand(wall) < 4);

    public bool DefenderCanReach(CityGame game, SettlementDefender unit, Pos target) =>
        DefenderPath(game, unit, target) is not null;

    internal bool SavedDefenderCellValid(GameMap map, DefenderKind kind, Pos cell)
    {
        if (!map.InBounds(cell)) return false;
        if (TravelRoutes.Walkable(map, map.Index(cell.X, cell.Y))) return true;
        var point = new System.Numerics.Vector2(cell.X + .5f, cell.Y + .5f);
        return kind == DefenderKind.Archer && WallsNear(point, .5f).Any(w =>
            WallDamageLayout.ContainsFootprint(w, point.X, point.Y, .5f));
    }

    private IOrderedEnumerable<WallComponent> ArcherPosts(SettlementDefender unit, SiegeEnemy? enemy) =>
        Walls.Where(w => SupportsWallWalk(w) && !w.IsGate && !w.IsGateWing)
            .OrderBy(w => enemy is null
                ? DefenderList.Where(other => other.Id != unit.Id).Sum(other =>
                    150 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))) -
                    (w.IsTower ? 12 : 0) + Math.Sqrt(Math.Pow(w.Center.X - unit.Home.X, 2) +
                        Math.Pow(w.Center.Y - unit.Home.Y, 2)) * 2 +
                    DefenderList.Count(other => other.Id != unit.Id && other.PatrolWallId == w.Id) * 100
                : Math.Pow(w.Center.X - enemy.X, 2) + Math.Pow(w.Center.Y - enemy.Y, 2) +
                    DefenderList.Where(other => other.Id != unit.Id).Sum(other =>
                        80 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))))
            .ThenBy(w => w.Cell.Y).ThenBy(w => w.Cell.X);

    private (SiegeEnemy Enemy, Pos? Goal, List<Pos>? Path)? AssignArcherTarget(CityGame game,
        SettlementDefender unit, WallComponent? support)
    {
        var candidates = EnemyList.Where(enemy => enemy.Health > 0)
            .OrderBy(enemy => DefenderList.Count(other => other.Id != unit.Id && other.Health > 0 &&
                other.Kind == DefenderKind.Archer && other.Order != DefenderOrder.Move &&
                other.TargetEnemyId == enemy.Id))
            .ThenBy(enemy => enemy.Id == unit.TargetEnemyId ? 0 : 1)
            .ThenBy(enemy => Math.Pow(enemy.X - unit.X, 2) + Math.Pow(enemy.Y - unit.Y, 2))
            .ThenBy(enemy => enemy.Id);
        foreach (var candidate in candidates)
        {
            if (unit.PathEnemyId == candidate.Id && unit.PathIndex < unit.Path.Count &&
                unit.PathTarget is { } goal && Math.Pow(goal.X + .5 - candidate.X, 2) +
                    Math.Pow(goal.Y + .5 - candidate.Y, 2) <= Math.Pow(CombatProfiles.Archer.Range, 2))
                return (candidate, null, null);
            if (ArcherHasShot(game, unit, support, candidate)) return (candidate, null, null);
            foreach (var post in ArcherPosts(unit, candidate))
            {
                var point = WallWalkPoint(post.Cell, post);
                if (Math.Pow(point.X - candidate.X, 2) + Math.Pow(point.Y - candidate.Y, 2) >
                    Math.Pow(CombatProfiles.Archer.Range, 2)) continue;
                if (DefenderList.Any(other => other.Id != unit.Id &&
                    Math.Pow(other.X - post.Cell.X - .5, 2) + Math.Pow(other.Y - post.Cell.Y - .5, 2) <= .12)) continue;
                var path = DefenderPath(game, unit, post.Cell);
                if (path is not null) return (candidate, post.Cell, path);
            }
        }
        return null;
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

    private HashSet<Pos> GateGroundReachable(CityGame game)
    {
        var reached = new HashSet<Pos>();
        var queue = new Queue<Pos>();
        foreach (var gate in Walls.Where(w => w.IsGate).Select(w => w.Cell))
            if (GroundPassable(game, gate) && reached.Add(gate)) queue.Enqueue(gate);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Neighbors(cell))
                if (!reached.Contains(next) && GroundPassable(game, next) &&
                    CanWalk(new(cell.X + .5f, cell.Y + .5f), new(next.X + .5f, next.Y + .5f)))
                {
                    reached.Add(next);
                    queue.Enqueue(next);
                }
        return reached;
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
        _defenderGame = game;
        var homes = game.Map.BuildingFootprints.Values.Where(a => ContainsInterior(new(a.X, a.Y)) &&
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
            DefenderList.Add(new SettlementDefender { Id = NextDefenderId++, Home = home,
                Kind = type.Name == "Archer Barracks" ? DefenderKind.Archer : DefenderKind.Soldier,
                X = cell.X + 0.5, Y = cell.Y + 0.5, PreviousX = cell.X + 0.5, PreviousY = cell.Y + 0.5 });
            RosterTimers[home] = 2;
        }
        foreach (var unit in DefenderList.OrderBy(d => d.Id))
        {
            var nearestGate = Walls.Where(w => w.IsGate).MinBy(w =>
                Math.Pow(w.Center.X - unit.X, 2) + Math.Pow(w.Center.Y - unit.Y, 2));
            var gate = nearestGate?.Cell ?? unit.Home;
            unit.PreviousX = unit.X; unit.PreviousY = unit.Y; unit.PreviousElevation = unit.Elevation;
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
                enemy = unit.Kind == DefenderKind.Archer ? assignment?.Enemy : EnemyList.Where(g => g.Health > 0)
                    .OrderBy(g => Math.Pow(g.X - unit.X, 2) + Math.Pow(g.Y - unit.Y, 2)).ThenBy(g => g.Id).FirstOrDefault();
                unit.TargetEnemyId = enemy?.Id;
                if (unit.Kind == DefenderKind.Archer && unit.PathEnemyId != enemy?.Id)
                {
                    unit.PathEnemyId = enemy?.Id;
                    unit.Path.Clear();
                    unit.PathIndex = 0;
                    unit.PathTarget = null;
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
                    else AddAttack(new SiegeProjectile { Melee = true, SourceDefenderId = unit.Id,
                        TargetId = enemy.Id, Damage = profile.Damage, Windup = profile.Windup,
                        StartX = unit.X, StartY = unit.Y, EndX = enemy.X, EndY = enemy.Y });
                    unit.AttackCooldown = profile.Interval;
                }
                continue;
            }
            Pos goal;
            if (unit.Order == DefenderOrder.Move && unit.Destination is { } destination) goal = destination;
            else if (unit.Kind == DefenderKind.Archer)
            {
                goal = enemy is not null && unit.PathIndex < unit.Path.Count && unit.PathTarget is { } response
                    ? response : ArcherPosts(unit, enemy)
                    .Where(w => DefenderList.All(other => other.Id == unit.Id ||
                        Math.Pow(other.X - w.Cell.X - 0.5, 2) + Math.Pow(other.Y - w.Cell.Y - 0.5, 2) > 0.12))
                    .Select(w => (Pos?)w.Cell).FirstOrDefault() ?? gate;
                if (enemy is null && unit.PatrolWallId is { } assigned &&
                    Walls.FirstOrDefault(w => w.Id == assigned && SupportsWallWalk(w)) is { } patrol)
                    goal = patrol.Cell;
                else if (enemy is null)
                    unit.PatrolWallId = Walls.FirstOrDefault(w => w.Cell == goal)?.Id;
            }
            else if (enemy is null || !EstablishedAt(new((int)enemy.X, (int)enemy.Y)))
            {
                var posts = Walls.Where(w => w.IsGate || w.IsTower && (w.JunctionTower ||
                    (w.Cell.X == Bounds.X || w.Cell.X == Bounds.Right) && (w.Cell.Y == Bounds.Y || w.Cell.Y == Bounds.Bottom)))
                    .OrderBy(w => DefenderList.Where(other => other.Id != unit.Id && other.Kind == DefenderKind.Soldier)
                        .Sum(other => 1000 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))) +
                        Math.Sqrt(Math.Pow(unit.Home.X - w.Center.X, 2) + Math.Pow(unit.Home.Y - w.Center.Y, 2)) -
                        (w.IsGate ? 12 : 0) + DefenderList.Count(other => other.Id != unit.Id &&
                            other.Kind == DefenderKind.Soldier && other.PatrolWallId == w.Id) * 200).ToArray();
                var post = posts.FirstOrDefault();
                if (enemy is null && unit.PatrolWallId is { } assigned &&
                    Walls.FirstOrDefault(w => w.Id == assigned && w.Health > 0) is { } patrol) post = patrol;
                else if (enemy is null) unit.PatrolWallId = post?.Id;
                var center = post?.Center ?? new System.Numerics.Vector2(gate.X + .5f, gate.Y + .5f);
                goal = new((int)unit.X, (int)unit.Y);
                var spots = new CellRect((int)center.X - 4, (int)center.Y - 4, 9, 9).Cells()
                    .Where(p => EstablishedAt(p) && GroundPassable(game, p) && DefenderList.All(other =>
                        other.Id == unit.Id || Math.Pow(other.X - p.X - .5, 2) + Math.Pow(other.Y - p.Y - .5, 2) > 1) &&
                        Walls.Where(w => w.IsGate).All(w => Math.Abs(
                            (p.X + .5 - w.Center.X) * w.Tangent.X + (p.Y + .5 - w.Center.Y) * w.Tangent.Y) > 2 ||
                            System.Numerics.Vector2.DistanceSquared(new(p.X + .5f, p.Y + .5f), w.Center) > 25))
                    .OrderBy(p => Math.Pow(p.X + .5 - center.X, 2) + Math.Pow(p.Y + .5 - center.Y, 2))
                    .Select(p => (Pos?)p).FirstOrDefault();
                if (spots is { } spot) goal = spot;
            }
            else goal = new((int)enemy.X, (int)enemy.Y);
            if (unit.Order == DefenderOrder.Automatic && (unit.Kind != DefenderKind.Archer || enemy is null) &&
                unit.PathIndex < unit.Path.Count && unit.PathTarget is { } underway)
                goal = underway;
            if (unit.PathTarget != goal || unit.PathIndex >= unit.Path.Count)
            {
                var path = DefenderPath(game, unit, goal);
                if (path is null && unit.Order != DefenderOrder.Automatic)
                {
                    unit.Order = DefenderOrder.Automatic; unit.TargetEnemyId = null; unit.Destination = null;
                    game.Report(new CityEvent(game.Week, EventKind.Raid,
                        $"Defender {unit.Id}'s route is blocked; automatic defense has resumed.", [], Bad: false));
                }
                unit.Path = path ?? [];
                unit.PathTarget = goal; unit.PathIndex = 0;
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
                    { unit.PathTarget = null; unit.Path.Clear(); unit.PathIndex = 0; continue; }
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
