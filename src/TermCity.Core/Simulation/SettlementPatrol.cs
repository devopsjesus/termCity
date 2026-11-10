using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    private int? _patrolSignature;
    private double _patrolRetrySeconds;
    private BaileyGateSide? _patrolStagingSide;
    private int _patrolNavigationVersion = -1;

    internal bool CornerPost(WallComponent wall)
    {
        if (!wall.IsTower) return false;
        if (wall.IsOriginal)
            return (wall.Cell.X == Bounds.X || wall.Cell.X == Bounds.Right) &&
                (wall.Cell.Y == Bounds.Y || wall.Cell.Y == Bounds.Bottom);
        var directions = Walls.Where(w => !w.IsTower && w.Start is not null &&
            World.WallNetwork.PointSegmentDistance(wall.Center, w.Start.Value, w.End!.Value) < .1f)
            .Select(w => w.Tangent).ToArray();
        return directions.Any(a => directions.Any(b => Math.Abs(a.X * b.Y - a.Y * b.X) > .1f));
    }

    internal bool OnApproachSide(WallComponent wall, BaileyGateSide side) => side switch
    {
        BaileyGateSide.Right => Math.Abs(wall.Center.X - Bounds.Right - .5f) < .6f,
        BaileyGateSide.Left => Math.Abs(wall.Center.X - Bounds.X - .5f) < .6f,
        BaileyGateSide.Bottom => Math.Abs(wall.Center.Y - Bounds.Bottom - .5f) < .6f,
        _ => Math.Abs(wall.Center.Y - Bounds.Y - .5f) < .6f,
    };

    private IEnumerable<Pos> PatrolSpots(WallComponent wall)
    {
        yield return wall.Cell;
        if (!wall.IsTower) yield break;
        int radius = (int)Math.Ceiling(WorldScale.TowerWidth / 2);
        foreach (var cell in new CellRect(wall.Cell.X - radius, wall.Cell.Y - radius,
            radius * 2 + 1, radius * 2 + 1).Cells().OrderBy(p =>
                Vector2.DistanceSquared(new(p.X + .5f, p.Y + .5f), wall.Center)))
            if (cell != wall.Cell && ReferenceEquals(WallWalkSupport(cell), wall) &&
                WallDamageLayout.ContainsFootprint(wall, cell.X + .5, cell.Y + .5, -.65f))
                yield return cell;
    }

    private void AssignPatrols(CityGame game, double seconds, BaileyGateSide? stagingSide)
    {
        var hash = new HashCode();
        hash.Add(TopologyRevision);
        hash.Add(game.Map.NavigationVersion);
        hash.Add(stagingSide.HasValue);
        hash.Add(stagingSide);
        hash.Add(EnemyList.Any(e => e.Health > 0));
        foreach (var unit in DefenderList.OrderBy(u => u.Id))
        {
            hash.Add(unit.Id);
            hash.Add(unit.Order);
        }
        int signature = hash.ToHashCode();
        _patrolRetrySeconds -= seconds;
        if (_patrolSignature == signature && (_patrolRetrySeconds > 0 ||
            !DefenderList.Any(u => u.Kind == DefenderKind.Archer && u.Order == DefenderOrder.Automatic &&
                u.TargetEnemyId is null && (u.PatrolCell is null || u.PathTarget is null ||
                    stagingSide is { } side && !Walls.Any(w => w.Id == u.PatrolWallId && OnApproachSide(w, side)) ||
                    u.PathIndex >= u.Path.Count && u.PatrolCell is { } goal &&
                    WallWalkSupport(goal) is { } support && Vector2.DistanceSquared(
                        new((float)u.X, (float)u.Y), WallWalkPoint(goal, support)) > .01f))))
            return;
        _patrolSignature = signature;
        _patrolRetrySeconds = 3;
        bool layoutChanged = _patrolNavigationVersion != game.Map.NavigationVersion;
        _patrolNavigationVersion = game.Map.NavigationVersion;
        if (_patrolStagingSide != stagingSide)
        {
            _patrolStagingSide = stagingSide;
            foreach (var guard in DefenderList.Where(u => u.Kind == DefenderKind.Soldier &&
                u.Order == DefenderOrder.Automatic && u.TargetEnemyId is null))
            {
                guard.PatrolWallId = null;
                guard.Path.Clear();
                guard.PathIndex = 0;
                guard.PathTarget = null;
            }
        }
        var spans = NetworkSpans().Where(s => WallNetwork.Connected(s.Id)).ToArray();
        var posts = Walls.Where(w => SupportsWallWalk(w) && !w.IsGate && !w.IsGateWing &&
            (WallNetwork.Connected(w.Id * 4) || w.IsTower && spans.Any(s =>
                World.WallNetwork.PointSegmentDistance(w.Center, s.A, s.B) < .1f))).ToArray();
        var occupied = new List<(int Wall, Vector2 Point)>();
        foreach (var unit in DefenderList.Where(u => u.Kind == DefenderKind.Archer &&
            u.Order != DefenderOrder.Automatic && u.Elevation > .5f))
            if (WallWalkSupport(new((int)unit.X, (int)unit.Y)) is { } support)
                occupied.Add((support.Id, new((float)unit.X, (float)unit.Y)));
        foreach (var unit in DefenderList.Where(u => u.Kind == DefenderKind.Archer &&
            u.Order == DefenderOrder.Automatic).OrderBy(u => u.Id))
        {
            var candidates = posts.SelectMany(w => PatrolSpots(w).Select(cell =>
                (Wall: w, Cell: cell, Point: WallWalkPoint(cell, w))))
                .Where(p => occupied.All(other => Vector2.DistanceSquared(other.Point, p.Point) >=
                    Math.Pow(WorldScale.HumanRadius * 2, 2)))
                .OrderBy(p => stagingSide is { } side && !OnApproachSide(p.Wall, side) ? 1 : 0)
                .ThenBy(p => p.Wall.IsTower ? 0 : 1)
                .ThenBy(p => occupied.Count(other => other.Wall == p.Wall.Id))
                .ThenBy(p => CornerPost(p.Wall) ? 0 : 1)
                .ThenBy(p => unit.PatrolWallId == p.Wall.Id && (unit.PatrolCell ?? unit.PathTarget) == p.Cell ? 0 : 1)
                .ThenBy(p => p.Wall.IsTower ?
                    Vector2.Dot(p.Point - p.Wall.Center, WallDamageLayout.LadderInward(p.Wall)) : 0)
                .ThenBy(p => Vector2.DistanceSquared(p.Point, new((float)unit.X, (float)unit.Y)))
                .ThenBy(p => p.Wall.Id).ThenBy(p => p.Cell.Y).ThenBy(p => p.Cell.X);
            Pos? chosen = null;
            foreach (var candidate in candidates)
            {
                bool retaining = !layoutChanged && unit.PatrolWallId == candidate.Wall.Id &&
                    unit.PatrolCell == candidate.Cell && unit.PathTarget == candidate.Cell &&
                    (unit.PathIndex < unit.Path.Count || unit.Elevation > .5f &&
                        Vector2.DistanceSquared(new((float)unit.X, (float)unit.Y), candidate.Point) < .01f);
                var path = retaining ? unit.Path : DefenderPath(game, unit, candidate.Cell,
                    ignoreBodies: true, stayInside: unit.Elevation > .01f || EstablishedAt(new((int)unit.X, (int)unit.Y)));
                if (path is null) continue;
                bool changed = unit.PatrolWallId != candidate.Wall.Id || unit.PatrolCell != candidate.Cell;
                unit.PatrolWallId = candidate.Wall.Id;
                unit.PatrolCell = chosen = candidate.Cell;
                occupied.Add((candidate.Wall.Id, candidate.Point));
                if (changed && unit.TargetEnemyId is null)
                {
                    unit.Path = path;
                    unit.PathIndex = 0;
                    unit.PathTarget = candidate.Cell;
                    unit.PathRetrySeconds = 0;
                }
                break;
            }
            if (chosen is null)
            {
                unit.PatrolWallId = null;
                unit.PatrolCell = null;
            }
        }
    }

    private IOrderedEnumerable<SiegeEnemy> DefenseTargets(SettlementDefender unit)
    {
        return EnemyList.Where(e => e.Health > 0)
            .OrderBy(e => (1d + DefenderList.Count(other => other.Id != unit.Id && other.Health > 0 &&
                other.Order != DefenderOrder.Move && other.TargetEnemyId == e.Id)) / e.Definition.ThreatWeight)
            .ThenBy(e => e.Id == unit.TargetEnemyId ? 0 : 1)
            .ThenBy(e => Math.Pow(e.X - unit.X, 2) + Math.Pow(e.Y - unit.Y, 2))
            .ThenBy(e => e.Id);
    }

    private SiegeEnemy? AssignGuardTarget(CityGame game, SettlementDefender unit)
    {
        DefenderPathSearch? paths = null;
        try
        {
            foreach (var enemy in DefenseTargets(unit))
            {
                if (!EstablishedAt(new((int)enemy.X, (int)enemy.Y))) continue;
                if (Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2) <= Math.Pow(MeleeDistance(enemy), 2) &&
                    GroundFightVisible(game, unit.X, unit.Y, enemy.X, enemy.Y))
                    return enemy;
                if (unit.TargetEnemyId == enemy.Id && unit.PathIndex < unit.Path.Count) return enemy;
                paths ??= DefenderPaths(game, unit, EnemyList.Where(candidate => candidate.Health > 0)
                    .Select(candidate => new Pos((int)candidate.X, (int)candidate.Y))
                    .Where(EstablishedAt).Distinct().ToArray(),
                    stayInside: EstablishedAt(new((int)unit.X, (int)unit.Y)));
                if (paths.CanReach(new((int)enemy.X, (int)enemy.Y))) return enemy;
            }
            return null;
        }
        finally { paths?.Dispose(); }
    }
}
