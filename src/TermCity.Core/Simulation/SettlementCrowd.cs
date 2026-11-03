using TermCity.Core.Util;
using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    private readonly List<SiegeEnemy> _movementOrder = [];

    private bool EnemySpawnClear(CityGame game, double x, double y, double radius) =>
        EnemyGroundClear(game, x, y, radius) && DefenderList.All(unit => unit.Elevation >= 0.3f ||
            Math.Pow(unit.X - x, 2) + Math.Pow(unit.Y - y, 2) >= Math.Pow(radius + WorldScale.HumanRadius, 2));

    private bool EnemyGroundClear(CityGame game, double x, double y, double radius, bool approachingWall = false)
    {
        for (int cy = (int)Math.Floor(y - radius); cy <= (int)Math.Floor(y + radius); cy++)
            for (int cx = (int)Math.Floor(x - radius); cx <= (int)Math.Floor(x + radius); cx++)
            {
                double dx = Math.Max(Math.Max(cx - x, 0), x - cx - 1);
                double dy = Math.Max(Math.Max(cy - y, 0), y - cy - 1);
                if (dx * dx + dy * dy >= radius * radius - 1e-8) continue;
                if (!game.Map.InBounds(cx, cy) || !game.Map.TerrainAt(cx, cy).Buildable ||
                    game.Map.BuildingAt(cx, cy) is not null) return false;
            }
        // Routes end inside a wall; movement stops earlier at physical melee contact.
        if (approachingWall) return true;
        return !GroundBlocked(new((float)x, (float)y), radius, allowGates: false);
    }

    private (List<Pos> Spawns, Dictionary<Pos, Pos> Previous) BuildCrowdRoutes(CityGame game, double radius = 0)
    {
        var previous = new Dictionary<Pos, Pos>();
        var distance = new Dictionary<Pos, int>();
        var queue = new Queue<Pos>();
        foreach (var wall in WallList.Where(w => OuterBarrier(w) &&
            game.Map.TerrainAt(w.Cell.X, w.Cell.Y).Buildable))
        {
            distance[wall.Cell] = 0;
            queue.Enqueue(wall.Cell);
        }
        var spawns = new List<Pos>();
        while (queue.TryDequeue(out var cell))
        {
            int d = distance[cell];
            if (d >= 6) spawns.Add(cell);
            if (d >= 12) continue;
            foreach (var next in Neighbors(cell))
            {
                if (distance.ContainsKey(next) || !game.Map.InBounds(next) || EstablishedAt(next) ||
                    !game.Map.TerrainAt(next.X, next.Y).Buildable || game.Map.BuildingAt(next.X, next.Y) is not null ||
                    radius > 0 && !EnemyGroundClear(game, next.X + 0.5, next.Y + 0.5, radius,
                        approachingWall: true)) continue;
                previous[next] = cell;
                distance[next] = d + 1;
                queue.Enqueue(next);
            }
        }
        if (spawns.Count == 0) spawns.AddRange(previous.Keys);
        return (spawns, previous);
    }

    private static List<Pos> CrowdRoute(Pos spawn, Dictionary<Pos, Pos> previous)
    {
        var route = new List<Pos>();
        while (previous.TryGetValue(spawn, out var next))
        {
            route.Add(next);
            spawn = next;
        }
        return route;
    }

    private static List<Pos> ChooseCrowdSpawns(List<Pos> candidates, int count, int tier, int wave,
        SiegeEnemyKind kind = SiegeEnemyKind.Goblin)
    {
        var selected = new List<Pos>(count);
        double diameter = SiegeEnemies.Get(kind).CollisionRadius(tier) * 2;
        for (int n = 0; n < count && candidates.Count > 0; n++)
        {
            int offset = (int)(((long)wave * 37 + n * 97) % candidates.Count);
            bool found = false;
            for (int probe = 0; probe < candidates.Count; probe++)
            {
                var cell = candidates[(offset + probe) % candidates.Count];
                bool free = true;
                foreach (var placed in selected)
                    if (Math.Pow(cell.X - placed.X, 2) + Math.Pow(cell.Y - placed.Y, 2) < diameter * diameter)
                    {
                        free = false;
                        break;
                    }
                if (!free) continue;
                selected.Add(cell);
                found = true;
                break;
            }
            if (!found) break;
        }
        return selected;
    }

    private void PrepareCrowdMovement()
    {
        foreach (var goblin in EnemyList)
        {
            goblin.PreviousX = goblin.X;
            goblin.PreviousY = goblin.Y;
            goblin.PreviousWalkDistance = goblin.WalkDistance;
            goblin.MotionInitialized = true;
        }
        _movementOrder.Clear();
        _movementOrder.AddRange(EnemyList);
        _movementOrder.Sort(static (a, b) =>
        {
            int distance = RemainingRoute(a).CompareTo(RemainingRoute(b));
            return distance != 0 ? distance : a.Id.CompareTo(b.Id);
        });
    }

    private static double RemainingRoute(SiegeEnemy goblin)
    {
        var next = goblin.Route[goblin.RouteIndex];
        return goblin.Route.Count - goblin.RouteIndex - 1 +
            Math.Abs(goblin.X - next.X - 0.5) + Math.Abs(goblin.Y - next.Y - 0.5);
    }

    private bool CrowdStepIsFree(SiegeEnemy moving, double x, double y)
    {
        foreach (var other in EnemyList)
        {
            if (ReferenceEquals(moving, other)) continue;
            double radius = moving.CollisionRadius + other.CollisionRadius + 1e-6;
            double dx = moving.PreviousX - other.PreviousX, dy = moving.PreviousY - other.PreviousY;
            double vx = x - moving.PreviousX - (other.X - other.PreviousX);
            double vy = y - moving.PreviousY - (other.Y - other.PreviousY);
            double length = vx * vx + vy * vy;
            double time = length > 1e-12 ? Math.Clamp(-(dx * vx + dy * vy) / length, 0, 1) : 0;
            if (Math.Pow(dx + vx * time, 2) + Math.Pow(dy + vy * time, 2) + 1e-10 < radius * radius)
                return false;
        }
        return true;
    }

    private void MoveCrowdMember(CityGame game, SiegeEnemy goblin, double x, double y)
    {
        double fraction = 1;
        if (!CrowdStepIsFree(goblin, x, y) || !EnemyGroundClear(game, x, y, goblin.CollisionRadius))
        {
            double low = 0, high = 1;
            for (int n = 0; n < 12; n++)
            {
                double middle = (low + high) / 2;
                double px = goblin.X + (x - goblin.X) * middle, py = goblin.Y + (y - goblin.Y) * middle;
                if (CrowdStepIsFree(goblin, px, py) && EnemyGroundClear(game, px, py, goblin.CollisionRadius)) low = middle;
                else high = middle;
            }
            fraction = low;
        }
        goblin.X += (x - goblin.X) * fraction;
        goblin.Y += (y - goblin.Y) * fraction;
        goblin.WalkDistance += Math.Sqrt(Math.Pow(goblin.X - goblin.PreviousX, 2) +
            Math.Pow(goblin.Y - goblin.PreviousY, 2));
    }

    private void RerouteBlockedGoblin(CityGame game, SiegeEnemy goblin)
    {
        bool ClearSegment(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
            foreach (var other in EnemyList)
            {
                if (other == goblin) continue;
                double t = length > 1e-12 ? Math.Clamp(((other.X - ax) * dx + (other.Y - ay) * dy) / length, 0, 1) : 0;
                double radius = goblin.CollisionRadius + other.CollisionRadius;
                if (Math.Pow(ax + dx * t - other.X, 2) + Math.Pow(ay + dy * t - other.Y, 2) + 1e-10 < radius * radius)
                    return false;
            }
            return true;
        }
        bool Exterior(Pos p) => game.Map.InBounds(p) && !EstablishedAt(p) &&
            EnemyGroundClear(game, p.X + 0.5, p.Y + 0.5, goblin.CollisionRadius);
        var starts = new[] { new Pos((int)goblin.X, (int)goblin.Y), goblin.Route[goblin.RouteIndex],
            goblin.Route[Math.Max(0, goblin.RouteIndex - 1)] }.Distinct()
            .Where(p => Exterior(p) && Math.Abs(goblin.X - p.X - 0.5) + Math.Abs(goblin.Y - p.Y - 0.5) <= 1.000001 &&
                (Math.Abs(goblin.X - p.X - 0.5) < 1e-6 || Math.Abs(goblin.Y - p.Y - 0.5) < 1e-6) &&
                ClearSegment(goblin.X, goblin.Y, p.X + 0.5, p.Y + 0.5));
        var previous = new Dictionary<Pos, Pos>();
        var distances = new Dictionary<Pos, int>();
        var queue = new Queue<Pos>();
        foreach (var start in starts)
            if (distances.TryAdd(start, 1)) queue.Enqueue(start);
        while (queue.TryDequeue(out var cell))
        {
            int distance = distances[cell];
            foreach (var next in Neighbors(cell))
            {
                if (WallAt(next) is { Health: > 0 })
                {
                    var route = new List<Pos> { next, cell };
                    while (previous.TryGetValue(cell, out var from)) { route.Add(from); cell = from; }
                    route.Reverse();
                    goblin.Route = route;
                    goblin.RouteIndex = 0;
                    return;
                }
                if (distance >= MaximumRouteLength - 1 || distances.ContainsKey(next) || !Exterior(next) ||
                    !ClearSegment(cell.X + 0.5, cell.Y + 0.5, next.X + 0.5, next.Y + 0.5)) continue;
                previous.Add(next, cell);
                distances.Add(next, distance + 1);
                queue.Enqueue(next);
            }
        }
    }

    internal void SeparateLoadedCrowd(CityGame game)
    {
        var placed = new List<SiegeEnemy>(EnemyList.Count);
        Dictionary<Pos, Pos>? routes = null;
        foreach (var goblin in EnemyList.OrderBy(unit => unit.Id))
        {
            bool Free(double x, double y) => placed.All(other =>
                Math.Pow(x - other.X, 2) + Math.Pow(y - other.Y, 2) + 1e-10 >=
                Math.Pow(goblin.CollisionRadius + other.CollisionRadius, 2));
            if (!Free(goblin.X, goblin.Y))
            {
                routes ??= BuildCrowdRoutes(game).Previous;
                Pos? location = routes.Keys.OrderBy(cell =>
                    Math.Pow(cell.X + 0.5 - goblin.X, 2) + Math.Pow(cell.Y + 0.5 - goblin.Y, 2))
                    .ThenBy(cell => cell.Y).ThenBy(cell => cell.X)
                    .Where(cell => Free(cell.X + 0.5, cell.Y + 0.5)).Select(cell => (Pos?)cell).FirstOrDefault();
                if (location is not { } target)
                    throw new InvalidDataException("This saved goblin swarm has insufficient reachable dry land to separate its attackers.");
                goblin.X = target.X + 0.5;
                goblin.Y = target.Y + 0.5;
                goblin.Route = CrowdRoute(target, routes);
                goblin.RouteIndex = 0;
                goblin.MotionInitialized = false;
            }
            placed.Add(goblin);
        }
    }
}
