using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record MovingDefensePart(WallComponent Wall, DefensePart Part, Vector3 Origin,
        int Color, int Instance, int Pick, int Highlight);
    private readonly List<MovingDefensePart> _movingDefenseParts = [];
    private readonly Dictionary<int, float> _defenseAngles = [];
    private readonly Dictionary<int, List<Transform3D>> _splashBatches = [];
    private readonly Dictionary<int, MultiMeshInstance3D> _splashInstances = [];
    private readonly CylinderMesh _splashDisk = new()
        { TopRadius = 1, BottomRadius = 1, Height = .02f, RadialSegments = 24 };
    internal int OilFlowSegmentsDrawn { get; private set; }
    internal int DroppedStonesDrawn { get; private set; }
    internal int DefenseSplashesDrawn { get; private set; }
    internal float LargestDefenseSplashRadius { get; private set; }
    internal Aabb? DefensePreviewBounds { get; private set; }

    private static Transform3D DefenseTransform(DefensePart part, Vector3 origin)
    {
        var q = part.Rotation;
        var basis = new Basis(new Quaternion(q.X, q.Y, q.Z, q.W)) *
            Basis.Identity.Scaled(new(part.Size.X, part.Size.Y, part.Size.Z));
        return new(basis, origin + new Vector3(part.Center.X, part.Center.Y, part.Center.Z));
    }

    private static Aabb DefenseBounds(Transform3D transform)
    {
        var size = transform.Basis.X.Abs() + transform.Basis.Y.Abs() + transform.Basis.Z.Abs();
        return new(transform.Origin - size / 2, size);
    }

    private (int Station, float Angle) DefensePose(WallComponent? wall, WallDefenseKind kind)
    {
        if (wall is null || kind != WallDefenseKind.OilCauldron || !SiegeAnimationEnabled ||
            _renderedSettlement is not { } siege) return (-1, 0);
        var shot = siege.Projectiles.LastOrDefault(shot => shot.Kind == kind &&
            (shot.SourceWallId == wall.Id || shot.SourceWallId == 0 &&
                (int)shot.StartX == wall.Cell.X && (int)shot.StartY == wall.Cell.Y));
        return shot is null ? (-1, 0) :
            (shot.Id > 0 ? shot.StationIndex : TowerDefenseArtwork.NearestStation(wall, new((float)shot.EndX, (float)shot.EndY)),
                TowerDefenseArtwork.TipAngle(SiegeMotion.ProjectileProgress(siege, shot) * shot.FlightSeconds));
    }

    private void DrawDroppedDefense(Dictionary<int, List<Transform3D>> batches, WallComponent wall,
        Vector3 origin, List<Transform3D> highlight)
    {
        foreach (var part in TowerDefenseArtwork.Parts(wall, wall.Defense, TowerDefenseArtwork.Rise(wall, _map)))
        {
            if (!batches.TryGetValue(part.Color, out var batch)) batches.Add(part.Color, batch = []);
            var transform = DefenseTransform(part, origin);
            int instance = batch.Count, picking = _siegePickBoxes.Count, highlighted = highlight.Count;
            batch.Add(transform);
            highlight.Add(transform);
            _siegePickTransforms[picking] = transform;
            _siegePickWallIds[picking] = wall.Id;
            _siegePickBoxes.Add((DefenseBounds(transform), wall.Cell, part.Color));
            if (part.Moving)
                _movingDefenseParts.Add(new(wall, part, origin, part.Color, instance, picking, highlighted));
        }
    }

    private void AnimateMountedDefenses()
    {
        foreach (var group in _movingDefenseParts.GroupBy(part => part.Wall))
        {
            var pose = DefensePose(group.Key, group.Key.Defense);
            float key = pose.Angle == 0 ? 0 : pose.Station * 2 + pose.Angle;
            if (_defenseAngles.TryGetValue(group.Key.Id, out float previous) && previous == key) continue;
            _defenseAngles[group.Key.Id] = key;
            foreach (var moving in group)
            {
                var part = TowerDefenseArtwork.Pose(moving.Part,
                    moving.Part.Station == pose.Station ? pose.Angle : 0);
                var transform = DefenseTransform(part, moving.Origin);
                UpdateWallTransform(moving.Color, moving.Instance, transform);
                _siegePickBoxes[moving.Pick] = (DefenseBounds(transform), moving.Wall.Cell, moving.Color);
                _siegePickTransforms[moving.Pick] = transform;
                _wallHighlightBoxes[moving.Wall.Id][moving.Highlight] = transform;
            }
        }
    }

    private void DrawDroppedAttack(Dictionary<int, List<Transform3D>> actors, SettlementState siege,
        SiegeProjectile shot, float progress)
    {
        if (shot.Id > 0)
        {
            DrawCommittedDrop(actors, shot, progress);
            return;
        }
        var wall = shot.SourceWallId > 0 ? siege.Walls.FirstOrDefault(w => w.Id == shot.SourceWallId) :
            siege.Walls.FirstOrDefault(w => w.Cell == new Pos((int)shot.StartX, (int)shot.StartY));
        if (wall is null) return;
        var target = new NVector2((float)shot.EndX, (float)shot.EndY);
        var station = TowerDefenseArtwork.Stations(wall)[TowerDefenseArtwork.NearestStation(wall, target)];
        var root = MountedDefenseOrigin(wall);
        float rise = TowerDefenseArtwork.Rise(wall, _map);
        var lip = TowerDefenseArtwork.Lip(station, rise, 1.05f);
        var start = root + new Vector3(lip.X, lip.Y, lip.Z);
        if (shot.Kind == WallDefenseKind.Rocks)
            start = root + new Vector3(station.Position.X + station.Forward.X * .4f,
                rise + .4f, station.Position.Y + station.Forward.Y * .4f);
        var end = new Vector3((float)shot.EndX, 0, (float)shot.EndY);
        end.Y = GroundLevel(end) + .03f;
        float fall = TowerDefenseArtwork.FallSeconds(start.Y - end.Y);
        double elapsed = progress * shot.FlightSeconds;
        float impact = TowerDefenseArtwork.WindupSeconds + fall;
        float radius = (float)WallDefenses.Get(shot.Kind).SplashRadius;
        if (SiegeAnimationEnabled && shot.Kind == WallDefenseKind.OilCauldron)
        {
            AppendOilSheet(start, end, elapsed, fall, new(station.Forward.X, 0, station.Forward.Y));
        }
        else if (SiegeAnimationEnabled && shot.Kind == WallDefenseKind.Rocks)
        {
            var tangent = new Vector3(station.Forward.Y, 0, -station.Forward.X);
            for (int rock = 0; rock < 3; rock++)
            {
                float t = Math.Clamp((float)(elapsed - TowerDefenseArtwork.WindupSeconds - rock * .08) / fall, 0, 1);
                if (elapsed < TowerDefenseArtwork.WindupSeconds + rock * .08) continue;
                var destination = end + tangent * ((rock - 1) * radius * .3f);
                var point = TowerDefenseArtwork.DropPoint(new(start.X, start.Y, start.Z),
                    new(destination.X, destination.Y + .12f, destination.Z), t);
                var basis = new Basis(new Vector3(1, .7f, .4f).Normalized(), (float)elapsed * (3 + rock));
                SiegeBox(actors, new(point.X, point.Y, point.Z), new(.24f, .23f, .21f),
                    rock == 1 ? 0x79796e : 0xa4a599, orientation: basis);
                DroppedStonesDrawn++;
            }
        }
        if (!SiegeAnimationEnabled || elapsed >= impact)
        {
            float age = Math.Max(0, (float)elapsed - impact);
            float spread = !SiegeAnimationEnabled ? 1 : Math.Clamp(age / .12f, 0, 1);
            if (spread <= 0) return;
            int color = shot.Kind == WallDefenseKind.OilCauldron ? 0x68491f : 0x8b806b;
            if (!_splashBatches.TryGetValue(color, out var batch)) _splashBatches.Add(color, batch = []);
            batch.Add(new(Basis.Identity.Scaled(new(radius * spread, 1, radius * spread)), end));
            DefenseSplashesDrawn++;
            LargestDefenseSplashRadius = Math.Max(LargestDefenseSplashRadius, radius * spread);
        }
    }

    private void DrawCommittedDrop(Dictionary<int, List<Transform3D>> actors, SiegeProjectile shot, float progress)
    {
        double elapsed = progress * shot.FlightSeconds;
        var start = new Vector3((float)shot.StartX, shot.WorldStartHeight, (float)shot.StartY);
        var end = new Vector3((float)shot.EndX, shot.WorldEndHeight, (float)shot.EndY);
        bool sourcePresent = _renderedSettlement?.Walls.Any(w => w.Id == shot.SourceWallId && w.Health > 0) == true;
        if (SiegeAnimationEnabled && shot.Kind == WallDefenseKind.OilCauldron)
        {
            if (sourcePresent || elapsed <= shot.ContactSeconds) AppendOilSheet(shot, elapsed);
            AppendOilSplash(shot, elapsed);
        }
        else if (SiegeAnimationEnabled && elapsed >= shot.Windup)
        {
            for (int rock = 0; rock < 3; rock++)
            {
                float t = (float)Math.Clamp((elapsed - shot.Windup - rock * .05) / shot.TravelSeconds, 0, 1);
                var p = TowerDefenseArtwork.DropPoint(new(start.X, start.Y, start.Z), new(end.X, end.Y + .1f, end.Z), t);
                SiegeBox(actors, new(p.X + (rock - 1) * .12f, p.Y, p.Z), new(.22f, .22f, .22f),
                    0xa4a599, orientation: new Basis(Vector3.One.Normalized(), (float)elapsed * 4 + rock));
                DroppedStonesDrawn++;
            }
        }
        if (elapsed < shot.ContactSeconds && SiegeAnimationEnabled) return;
        float size = DropAttackLayout.Size(shot.Kind);
        var forward = new Vector3(shot.FootprintForward.X, 0, shot.FootprintForward.Y);
        float spread = 1;
        if (shot.Kind == WallDefenseKind.OilCauldron)
        {
            double age = elapsed - shot.ContactSeconds;
            if (!SiegeAnimationEnabled) age = Math.Max(0, age);
            spread = TowerDefenseArtwork.OilPoolSpread(age, SiegeAnimationEnabled);
            var right = new Vector3(forward.Z, 0, -forward.X);
            var patches = TowerDefenseArtwork.OilPool(size, age, SiegeAnimationEnabled);
            foreach (var patch in patches)
            {
                var at = end + right * patch.Center.X + forward * patch.Center.Y;
                at.Y = GroundLevel(at) + .04f;
                SiegeBox(actors, at, new(patch.Size.X, .025f, patch.Size.Y), 0x68491f,
                    orientation: Basis.LookingAt(forward, Vector3.Up));
            }
            if (patches.Count == 0) return;
        }
        else
            SiegeBox(actors, end + Vector3.Up * .01f, new(size, .025f, size), 0x8b806b,
                orientation: Basis.LookingAt(forward, Vector3.Up));
        DefenseSplashesDrawn++;
        LargestDefenseSplashRadius = Math.Max(LargestDefenseSplashRadius, size * spread / 2);
    }
}
