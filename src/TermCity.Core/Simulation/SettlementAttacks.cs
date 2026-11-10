using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    internal int NextAttackId = 1;
    public const int MaximumPendingAttacks = 1024;
    private readonly Dictionary<int, (int Band, WallMaterial Material, IReadOnlyList<WallFragment> Parts)> _attackFragments = [];
    internal void InvalidateAttackGeometry() => _attackFragments.Clear();
    private static Vector2 Position(SiegeEnemy enemy) => new((float)enemy.X, (float)enemy.Y);
    private static float Ground(CityGame game, Vector2 p) => TerrainSprite.Elevation(game.Map,
        Math.Clamp((int)p.X, 0, game.Map.Width - 1), Math.Clamp((int)p.Y, 0, game.Map.Height - 1));

    public bool DropTargetVisible(CityGame game, WallComponent wall, DropFootprint footprint, Vector2 point,
        WallDefenseKind? kind = null)
    {
        if (!footprint.Contains(point) || !game.Map.InBounds((int)point.X, (int)point.Y) ||
            !game.Map.TerrainAt((int)point.X, (int)point.Y).Buildable ||
            Vector2.Distance(footprint.AcquisitionOrigin, point) > WallDefenses.Get(kind ?? wall.Defense).Range + WallNetwork.Tolerance) return false;
        var edge = footprint.Center - footprint.Forward * (footprint.Size / 2 - .03f);
        return !Blocked(game, new(edge.X, Ground(game, edge) + .1f, edge.Y),
            new(point.X, Ground(game, point) + .1f, point.Y), wall.Id);
    }

    internal bool ReleaseMountedAttack(CityGame game, WallComponent wall, WallDefenseKind kind)
    {
        if (ProjectileList.Count >= MaximumPendingAttacks) return false;
        var profile = WallDefenses.Get(kind);
        bool dropped = kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks;
        foreach (var target in EnemyList.Where(e => e.Health > 0 && (dropped ||
            Vector2.Distance(Position(e), wall.Center) >= profile.MinimumRange &&
            Vector2.Distance(Position(e), wall.Center) <= profile.Range))
            .OrderBy(e => Vector2.DistanceSquared(Position(e), wall.Center)).ThenBy(e => e.Id))
        {
            if (kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
            {
                var footprint = DropAttackLayout.Footprints(wall, kind).Where(f => DropTargetVisible(game, wall, f, Position(target), kind))
                    .OrderBy(f => f.Station).Select(f => (DropFootprint?)f).FirstOrDefault();
                if (footprint is not { } drop) continue;
                var station = TowerDefenseArtwork.Stations(wall)[drop.Station];
                float rise = TowerDefenseArtwork.Rise(wall, game.Map);
                var lip = TowerDefenseArtwork.Lip(station, rise, 1.05f);
                var origin = new Vector3(wall.Center.X + lip.X,
                    Ground(game, wall.Center) + WallDamageLayout.ActorWalkHeight(wall, game.Map) + lip.Y,
                    wall.Center.Y + lip.Z);
                float endHeight = Ground(game, drop.Center) + .03f;
                float fall = TowerDefenseArtwork.FallSeconds(origin.Y - endHeight);
                AddAttack(new SiegeProjectile
                {
                    Kind = kind, SourceWallId = wall.Id, TargetId = target.Id, Damage = profile.Damage,
                    StartX = origin.X, StartY = origin.Z, WorldStartHeight = origin.Y,
                    EndX = drop.Center.X, EndY = drop.Center.Y, WorldEndHeight = endHeight,
                    StartHeight = origin.Y - Ground(game, new(origin.X, origin.Z)), EndHeight = .03f,
                    DropHeight = Math.Max(.1f, origin.Y - endHeight), Windup = TowerDefenseArtwork.WindupSeconds,
                    TravelSeconds = fall, FootprintCenter = drop.Center, FootprintForward = drop.Forward, StationIndex = drop.Station,
                    CoverageOrigin = drop.AcquisitionOrigin,
                });
                return true;
            }

            float height = Ground(game, wall.Center) + WallDamageLayout.ActorWalkHeight(wall, game.Map) +
                (kind == WallDefenseKind.None ? WorldScale.Feet(4.5f) : kind == WallDefenseKind.Ballista ? .7f : 1.5f);
            var toward = Vector2.Normalize(Position(target) - wall.Center);
            var originPoint = wall.Center;
            if (kind == WallDefenseKind.None)
                originPoint += toward * Math.Min(WallDamageLayout.Width(wall), WallDamageLayout.Depth(wall)) * .4f;
            if (TryMissile(game, target, kind, new(originPoint.X, height, originPoint.Y), profile.Damage, wall.Id, 0))
                return true;
        }
        return false;
    }

    public IReadOnlyList<Vector2[]> DropCoverage(CityGame game, WallComponent wall, WallDefenseKind kind, int? facing = null)
    {
        var result = new List<Vector2[]>();
        foreach (var footprint in DropAttackLayout.Footprints(wall, kind, facing))
        {
            const int steps = 12;
            float step = footprint.Size / steps;
            for (int y = 0; y < steps; y++)
                for (int x = 0; x < steps; x++)
                {
                    var center = footprint.Center + footprint.Right * ((x + .5f) * step - footprint.Size / 2) +
                        footprint.Forward * ((y + .5f) * step - footprint.Size / 2);
                    var patch = new DropFootprint(center, footprint.Forward, step, footprint.Station);
                    if (patch.Polygon.Append(center).All(p => DropTargetVisible(game, wall, footprint, p, kind)))
                        result.Add(patch.Polygon);
                }
        }
        return result;
    }

    private void AddAttack(SiegeProjectile attack)
    {
        if (NextAttackId == int.MaxValue) throw new InvalidOperationException("The committed attack ID space is exhausted.");
        attack.Id = NextAttackId++;
        ProjectileList.Add(attack);
    }

    private bool TryMissile(CityGame game, SiegeEnemy target, WallDefenseKind kind, Vector3 start,
        double damage, int wallId, int defenderId, bool commit = true)
    {
        if (ProjectileList.Count >= MaximumPendingAttacks) return false;
        var targetPoint = Position(target);
        float speed = kind == WallDefenseKind.Trebuchet ? 10 : kind == WallDefenseKind.Ballista ? 45 : 35;
        float seconds = Math.Clamp(Vector2.Distance(new(start.X, start.Z), targetPoint) / speed, .12f, 4);
        var velocity = target.MotionInitialized
            ? new Vector2((float)(target.X - target.PreviousX), (float)(target.Y - target.PreviousY)) / (float)StepSeconds
            : Vector2.Zero;
        if (velocity.Length() > target.Definition.MovementSpeed(target.Tier) * 1.1)
            velocity = Vector2.Zero;
        var end = targetPoint + velocity * seconds;
        if (!game.Map.InBounds((int)end.X, (int)end.Y)) return false;
        var shot = new SiegeProjectile
        {
            Kind = kind, SourceWallId = wallId, SourceDefenderId = defenderId, TargetId = target.Id, Damage = damage,
            StartX = start.X, StartY = start.Z, WorldStartHeight = start.Y,
            EndX = end.X, EndY = end.Y,
            WorldEndHeight = Ground(game, end) + (kind == WallDefenseKind.Trebuchet ? .03f : (float)target.BodyScale * .8f),
            TravelSeconds = seconds, ArcHeight = kind == WallDefenseKind.Trebuchet ? Math.Max(3, Vector2.Distance(new(start.X, start.Z), end) * .22f) : 0,
            Windup = defenderId > 0 ? CombatProfiles.Archer.Windup : kind == WallDefenseKind.Ballista ? .25 : kind == WallDefenseKind.Trebuchet ? .6 : 0,
        };
        shot.StartHeight = shot.WorldStartHeight - Ground(game, new(start.X, start.Z));
        shot.EndHeight = shot.WorldEndHeight - Ground(game, end);
        Vector3 previous = start;
        for (int i = 1; i <= 24; i++)
        {
            var point = AttackPoint(shot, i / 24f);
            if (Blocked(game, previous, point, wallId) || AllyBlocks(game, previous, point, defenderId)) return false;
            previous = point;
        }
        if (commit) AddAttack(shot);
        return true;
    }

    public static Vector3 AttackPoint(SiegeProjectile shot, float progress)
    {
        float t = Math.Clamp(progress, 0, 1);
        var start = new Vector3((float)shot.StartX, shot.WorldStartHeight, (float)shot.StartY);
        var end = new Vector3((float)shot.EndX, shot.WorldEndHeight, (float)shot.EndY);
        return Vector3.Lerp(start, end, t) + Vector3.UnitY * (MathF.Sin(t * MathF.PI) * shot.ArcHeight);
    }

    private bool AllyBlocks(CityGame game, Vector3 a, Vector3 b, int sourceId) => DefenderList.Any(unit =>
    {
        float baseHeight = Ground(game, new((float)unit.X, (float)unit.Y)) + unit.Elevation;
        return unit.Id != sourceId && unit.Health > 0 && RayBox(a, b,
            new((float)unit.X - WorldScale.HumanRadius, baseHeight, (float)unit.Y - WorldScale.HumanRadius),
            new((float)unit.X + WorldScale.HumanRadius, baseHeight + WorldScale.HumanHeight, (float)unit.Y + WorldScale.HumanRadius)) is not null;
    });

    private bool Blocked(CityGame game, Vector3 a, Vector3 b, int sourceWallId)
    {
        int samples = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b) * 8));
        for (int step = 1; step <= samples; step++)
        {
            var p = Vector3.Lerp(a, b, step / (float)samples);
            if (!game.Map.InBounds((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))) return true;
            if (p.Y < Ground(game, new(p.X, p.Z)) - .02f) return true;
            if (game.Map.BuildingAt((int)p.X, (int)p.Z) is { } building)
            {
                var area = game.Map.BuildingFootprintAt((int)p.X, (int)p.Z);
                float height = building.Zone == ZoneType.Residential
                    ? HouseAppearance.At(building, area.X, area.Y).Height + HouseAppearance.At(building, area.X, area.Y).RoofRise
                    : Math.Max(1.6f, .9f + building.Level * .2f);
                if (p.Y < Ground(game, new(p.X, p.Z)) + height * building.ModelScale) return true;
            }
            foreach (var wall in WallsNear(new(p.X, p.Z)))
            {
                if (wall.Id == sourceWallId || wall.Health <= 0) continue;
                var delta = new Vector2(p.X, p.Z) - wall.Center;
                float along = Vector2.Dot(delta, wall.Tangent), across = -delta.X * wall.Tangent.Y + delta.Y * wall.Tangent.X;
                float y = p.Y - WallDamageLayout.GroundHeight(wall, game.Map);
                int band = WallDamageLayout.DamageBand(wall);
                if (!_attackFragments.TryGetValue(wall.Id, out var cached) || cached.Band != band || cached.Material != wall.Material)
                {
                    cached = (band, wall.Material, WallDamageLayout.Fragments(wall));
                    _attackFragments[wall.Id] = cached;
                }
                if (cached.Parts.Any(part =>
                    Math.Abs(along - part.Center.X) <= part.Size.X / 2 &&
                    Math.Abs(across - part.Center.Z) <= part.Size.Z / 2 &&
                    Math.Abs(y - part.Center.Y) <= part.Size.Y / 2)) return true;
            }
        }
        return false;
    }

    private static float? RayBox(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
    {
        var delta = b - a;
        float near = 0, far = 1;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(delta[axis]) < .000001f) { if (a[axis] < min[axis] || a[axis] > max[axis]) return null; continue; }
            float first = (min[axis] - a[axis]) / delta[axis], last = (max[axis] - a[axis]) / delta[axis];
            near = Math.Max(near, Math.Min(first, last)); far = Math.Min(far, Math.Max(first, last));
            if (near > far) return null;
        }
        return near;
    }

    internal void AdvanceCommittedAttacks(CityGame game, double seconds)
    {
        foreach (var shot in ProjectileList.Where(s => s.Id > 0).ToArray())
        {
            double previous = shot.Elapsed;
            shot.Elapsed += seconds;
            shot.Progress = Math.Min(1, shot.Elapsed / shot.FlightSeconds);
            if (shot.Applied) continue;
            if (previous < shot.Windup && shot.SourceDefenderId > 0)
            {
                var source = DefenderList.FirstOrDefault(d => d.Id == shot.SourceDefenderId && d.Health > 0);
                if (source is null || source.Order == DefenderOrder.Move ||
                    !shot.Melee && Vector2.Distance(new((float)source.X, (float)source.Y),
                        new((float)shot.StartX, (float)shot.StartY)) > .4f)
                { shot.Applied = true; continue; }
            }
            if (shot.Melee)
            {
                if (shot.Elapsed < shot.Windup) continue;
                shot.Applied = true;
                var unit = DefenderList.FirstOrDefault(d => d.Id == shot.SourceDefenderId && d.Health > 0);
                var target = EnemyList.FirstOrDefault(e => e.Id == shot.TargetId && e.Health > 0);
                if (unit is not null && target is not null && unit.Elevation < .3 &&
                    Math.Abs(Ground(game, new((float)unit.X, (float)unit.Y)) + unit.Elevation -
                        Ground(game, Position(target))) <= WorldScale.Feet(4) &&
                    Vector2.Distance(new((float)unit.X, (float)unit.Y), Position(target)) <= MeleeDistance(target) &&
                    GroundFightVisible(game, unit.X, unit.Y, target.X, target.Y) &&
                    !AllyBlocks(game, new((float)unit.X, Ground(game, new((float)unit.X, (float)unit.Y)) + .7f, (float)unit.Y),
                        new((float)target.X, Ground(game, Position(target)) + .7f, (float)target.Y), unit.Id))
                    DamageEnemy(target, shot.Damage, SiegeDeathCause.Blade, new((float)unit.X, (float)unit.Y));
                continue;
            }
            if (shot.Kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
            {
                if (shot.Elapsed + 1e-9 < shot.ContactSeconds) continue;
                shot.Applied = true;
                var footprint = new DropFootprint(shot.FootprintCenter, shot.FootprintForward, DropAttackLayout.Size(shot.Kind), shot.StationIndex);
                var edge = footprint.Center - footprint.Forward * (footprint.Size / 2 - .03f);
                foreach (var target in EnemyList.Where(e => e.Health > 0 && footprint.Contains(Position(e)) &&
                    Vector2.Distance(Position(e), shot.CoverageOrigin) <= WallDefenses.Get(shot.Kind).Range + WallNetwork.Tolerance).ToArray())
                    if (Math.Abs(Ground(game, Position(target)) - shot.WorldEndHeight) < .3f &&
                        !Blocked(game, new(edge.X, Ground(game, edge) + .1f, edge.Y),
                            new((float)target.X, Ground(game, Position(target)) + .1f, (float)target.Y), shot.SourceWallId))
                        DamageEnemy(target, shot.Damage, shot.Kind == WallDefenseKind.OilCauldron ? SiegeDeathCause.Oil : SiegeDeathCause.Crushing,
                            new((float)shot.StartX, (float)shot.StartY));
                continue;
            }
            float t0 = (float)Math.Clamp((previous - shot.Windup) / shot.TravelSeconds, 0, 1);
            float t1 = (float)Math.Clamp((shot.Elapsed - shot.Windup) / shot.TravelSeconds, 0, 1);
            if (t1 <= t0) continue;
            var a = AttackPoint(shot, t0); var b = AttackPoint(shot, t1);
            if (shot.Kind != WallDefenseKind.Trebuchet)
            {
                SiegeEnemy? nearest = null;
                float fraction = 1;
                foreach (var enemy in EnemyList)
                {
                    if (enemy.Health <= 0) continue;
                    float radius = (float)enemy.CollisionRadius;
                    float ground = Ground(game, Position(enemy));
                    var previousPosition = enemy.MotionInitialized
                        ? new Vector3((float)enemy.PreviousX, 0, (float)enemy.PreviousY)
                        : new Vector3((float)enemy.X, 0, (float)enemy.Y);
                    var currentPosition = new Vector3((float)enemy.X, 0, (float)enemy.Y);
                    var near = RayBox(a - previousPosition, b - currentPosition,
                        new(-radius, ground, -radius), new(radius, ground + (float)enemy.BodyScale * 1.5f, radius));
                    if (near is null || near > fraction || near == fraction && nearest is not null && enemy.Id > nearest.Id) continue;
                    nearest = enemy; fraction = near.Value;
                }
                var endpoint = Vector3.Lerp(a, b, fraction);
                if (Blocked(game, a, endpoint, shot.SourceWallId) || AllyBlocks(game, a, endpoint, shot.SourceDefenderId))
                    shot.Applied = true;
                else if (nearest is not null)
                {
                    DamageEnemy(nearest, shot.Damage, SiegeDeathCause.Arrow, new((float)shot.StartX, (float)shot.StartY));
                    shot.Applied = true;
                }
                if (t1 >= 1) shot.Applied = true;
                continue;
            }
            int steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b) * 12));
            for (int i = 1; i <= steps && !shot.Applied; i++)
            {
                var p = AttackPoint(shot, t0 + (t1 - t0) * i / steps);
                if (Blocked(game, a, p, shot.SourceWallId) || AllyBlocks(game, a, p, shot.SourceDefenderId))
                {
                    shot.Applied = true;
                    if (shot.Kind == WallDefenseKind.Trebuchet) ArtilleryImpact(game, shot, p);
                    break;
                }
                a = p;
            }
            if (!shot.Applied && t1 >= 1)
            {
                shot.Applied = true;
                if (shot.Kind == WallDefenseKind.Trebuchet) ArtilleryImpact(game, shot, b);
            }
        }
    }

    internal void ArtilleryImpact(CityGame game, SiegeProjectile shot, Vector3 point)
    {
        shot.EndX = point.X; shot.EndY = point.Z; shot.WorldEndHeight = point.Y;
        foreach (var enemy in EnemyList.Where(e => e.Health > 0))
        {
            float distance = Vector2.Distance(new(point.X, point.Z), Position(enemy));
            if (distance > 2.5f || point.Y > Ground(game, Position(enemy)) + (float)enemy.BodyScale * 1.5f ||
                Blocked(game, point + Vector3.UnitY * .15f,
                new((float)enemy.X, Ground(game, Position(enemy)) + .15f, (float)enemy.Y), 0)) continue;
            DamageEnemy(enemy, shot.Damage - 270 * distance / 2.5, SiegeDeathCause.Crushing, new(point.X, point.Z));
        }
    }
}
