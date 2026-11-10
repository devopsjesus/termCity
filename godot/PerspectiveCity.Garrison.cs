using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Rendering;
using NVector3 = System.Numerics.Vector3;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private int _defenderSelectionHash;
    private readonly List<(Transform3D Transform, int Id, bool Friendly)> _siegeActorPickBoxes = [];
    public int DefendersDrawn { get; private set; }
    internal int ArcherArmSegmentsDrawn { get; private set; }
    internal int ArcherStringSegmentsDrawn { get; private set; }
    internal int NockedArrowsDrawn { get; private set; }
    private readonly HashSet<int> _accessLadderWallIds = [];
    internal IReadOnlySet<int> AccessLadderWallIds => _accessLadderWallIds;
    internal Vector3? RenderedDefenderPosition(int id) =>
        _siegeActorPickBoxes.FirstOrDefault(box => box.Friendly && box.Id == id) is var box && box.Friendly
            ? box.Transform.Origin : null;
    internal Vector3? RenderedEnemyPosition(int id)
    {
        foreach (var box in _siegeActorPickBoxes)
            if (!box.Friendly && box.Id == id) return box.Transform.Origin;
        return null;
    }
    internal IReadOnlyDictionary<(bool Friendly, int Id), int> RenderedActorPoseHashes()
    {
        var hashes = new Dictionary<(bool Friendly, int Id), HashCode>();
        foreach (var box in _siegeActorPickBoxes)
        {
            var key = (box.Friendly, box.Id);
            hashes.TryGetValue(key, out var hash);
            hash.Add(box.Transform);
            hashes[key] = hash;
        }
        return hashes.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashCode());
    }
    internal Vector3? RenderedDefenderForward(int id) =>
        _siegeActorPickBoxes.Where(box => box.Friendly && box.Id == id).Skip(1).FirstOrDefault() is var head && head.Friendly
            ? -head.Transform.Basis.Z.Normalized() : null;

    public Vector3 DefenderPosition(SettlementDefender unit, double fraction) =>
        new((float)(unit.PreviousX + (unit.X - unit.PreviousX) * fraction),
            Mathf.Lerp(GroundLevel(new((float)unit.PreviousX, 0, (float)unit.PreviousY)) + unit.PreviousElevation,
                GroundLevel(new((float)unit.X, 0, (float)unit.Y)) + unit.Elevation, (float)fraction),
            (float)(unit.PreviousY + (unit.Y - unit.PreviousY) * fraction));

    private void DrawGarrisonActors(Dictionary<int, List<Transform3D>> batches, GameSession session, SettlementState siege)
    {
        DefendersDrawn = 0;
        ArcherArmSegmentsDrawn = ArcherStringSegmentsDrawn = NockedArrowsDrawn = 0;
        if (siege.FirstOrcApproach is { } approach)
            foreach (var wall in siege.Walls.Where(w => approach switch
            {
                BaileyGateSide.Right => w.Cell.X == siege.Bounds.Right,
                BaileyGateSide.Left => w.Cell.X == siege.Bounds.X,
                BaileyGateSide.Bottom => w.Cell.Y == siege.Bounds.Bottom,
                _ => w.Cell.Y == siege.Bounds.Y,
            }))
                SiegeBox(batches, WallTop(wall) + Vector3.Up * 0.42f, new(0.60f, 0.05f, 0.60f), 0xffcc50);
        foreach (var unit in siege.Defenders)
        {
            var at = DefenderPosition(unit, siege.InterpolationFraction);
            int signature = HashCode.Combine(unit.Kind, SiegeAnimationEnabled);
            if (!session.SelectedDefenderIds.Contains(unit.Id) &&
                ReuseInvisibleRig(batches, true, unit.Id, signature, at))
            {
                DefendersDrawn++;
                continue;
            }
            var forward = new Vector3((float)(unit.X - unit.PreviousX), 0, (float)(unit.Y - unit.PreviousY));
            bool walking = forward.LengthSquared() > 0.00001f;
            bool climbing = Math.Abs(unit.Elevation - unit.PreviousElevation) > .001f && !walking;
            if (!walking && siege.Enemies.FirstOrDefault(e => e.Id == unit.TargetEnemyId && e.Health > 0) is { } enemy)
                forward = new((float)enemy.X - at.X, 0, (float)enemy.Y - at.Z);
            if (climbing)
            {
                var ladders = siege.Walls.Where(w => w.IsTower && w.Health > 0).SelectMany(w =>
                    WallDamageLayout.WalkwayLadders(w).Select(ladder =>
                        (Point: ladder.Position + ladder.Direction * WallDamageLayout.LadderClimberOffset, Direction: ladder.Direction))
                        .Concat(WallDamageLayout.HasAccessLadder(w)
                            ? new[] { (Point: WallDamageLayout.LadderClimbPosition(w), Direction: WallDamageLayout.LadderInward(w)) }
                            : []));
                var closest = ladders.OrderBy(ladder => System.Numerics.Vector2.DistanceSquared(ladder.Point,
                    new(at.X, at.Z))).FirstOrDefault();
                if (closest.Direction.LengthSquared() > .001f)
                    forward = new(-closest.Direction.X, 0, -closest.Direction.Y);
            }
            if (forward.LengthSquared() < 0.00001f)
            {
                var idle = SiegeMotion.IdleDefenderForward(siege, unit);
                forward = new(idle.X, 0, idle.Y);
            }
            var facing = SmoothActorFacing(true, unit.Id, forward);
            BeginActorRig(true, unit.Id, signature, new(facing, at));
            float gait = walking && SiegeAnimationEnabled
                ? (float)Math.Sin(siege.VisualSeconds * 9 + unit.Id) : 0;
            float climbGait = climbing && SiegeAnimationEnabled
                ? MathF.Sin(at.Y / WallDamageLayout.LadderRungSpacing * Mathf.Pi) : 0;
            double interval = CombatProfiles.Fighter(unit.Kind).Interval;
            float attack = !climbing && SiegeAnimationEnabled && unit.AttackCooldown > 0
                ? CombatProfiles.Swing(unit.Kind, interval - unit.AttackCooldown + siege.InterpolationFraction * .1) : 0;
            ArcherBowPose? bowPose = unit.Kind == DefenderKind.Archer && !climbing
                ? ArcherArtwork.Pose(Math.Max(0, interval - unit.AttackCooldown + siege.InterpolationFraction * .1),
                    !walking && unit.AttackCooldown > 0, SiegeAnimationEnabled) : null;
            void Part(Vector3 offset, Vector3 size, int color, float angle = 0, Basis? local = null)
            {
                const float scale = ArcherArtwork.ModelScale;
                var center = at + facing * (offset * scale);
                var basis = facing * (local ?? new Basis(Vector3.Right, angle));
                var transform = SiegeBox(batches, center, size * scale, color, orientation: basis);
                CaptureActorPart(color, transform);
                _siegeActorPickBoxes.Add((new(basis * Basis.Identity.Scaled(size * scale), center), unit.Id, true));
            }
            static Vector3 Point(NVector3 p) => new(p.X, p.Y, p.Z);
            void Segment(NVector3 start, NVector3 end, float thickness, int color, Vector3 offset = default)
            {
                var a = Point(start) + offset;
                var b = Point(end) + offset;
                var delta = b - a;
                var direction = delta.Normalized();
                var basis = Basis.LookingAt(direction, Math.Abs(direction.Y) > .98f ? Vector3.Right : Vector3.Up);
                Part((a + b) / 2, new(thickness, thickness, delta.Length()), color, local: basis);
            }
            int coat = unit.Kind == DefenderKind.Archer ? 0x59777d : 0x647875;
            Part(Point(ArcherArtwork.TorsoCenter), Point(ArcherArtwork.TorsoSize), coat,
                local: new Basis(Vector3.Up, bowPose?.TorsoTurn ?? 0));
            Part(new(0, 0.66f, 0), new(0.18f, 0.18f, 0.18f), 0xc6bf9c);
            Part(new(0, 0.77f, 0), new(0.22f, 0.07f, 0.22f), unit.Kind == DefenderKind.Archer ? 0x79634b : 0xa4a599);
            foreach (int side in new[] { -1, 1 })
            {
                Part(new(side * 0.075f, 0.14f + climbGait * side * .06f, climbing ? -.15f : gait * side * 0.06f),
                    new(0.09f, 0.28f, 0.11f), 0x443d32, (climbing ? climbGait : gait) * side * 0.3f);
                if (bowPose is not null) continue;
                float armAngle = climbing ? 2.5f + climbGait * side * .2f :
                    side > 0 ? -Math.Max(0, attack) * 1.4f + Math.Max(0, -attack) * .7f : -Math.Max(0, attack) * .35f;
                var shoulder = new Vector3(side * .18f, .55f + (climbing ? .18f : 0), -.035f);
                var armCenter = shoulder + new Basis(Vector3.Right, armAngle) * new Vector3(0, -.14f, 0);
                Part(armCenter, new(0.07f, 0.28f, 0.08f), 0xc6bf9c, armAngle);
            }
            if (unit.Kind == DefenderKind.Archer)
            {
                var pose = bowPose ?? ArcherArtwork.Pose(0, shooting: false);
                if (bowPose is not null)
                {
                    Segment(pose.LeftShoulder, pose.LeftElbow, ArcherArtwork.UpperArmThickness, 0xc6bf9c);
                    Segment(pose.LeftElbow, pose.LeftHand, ArcherArtwork.ForearmThickness, 0xc6bf9c);
                    Segment(pose.RightShoulder, pose.RightElbow, ArcherArtwork.UpperArmThickness, 0xc6bf9c);
                    Segment(pose.RightElbow, pose.RightHand, ArcherArtwork.ForearmThickness, 0xc6bf9c);
                    ArcherArmSegmentsDrawn += 4;
                    Part(Point(pose.LeftHand), Point(ArcherArtwork.BowHandSize), 0xc6bf9c);
                    Part(Point(pose.RightHand), Point(ArcherArtwork.StringHandSize), 0xc6bf9c);
                }
                var stowed = climbing ? new Vector3(.15f, .08f, .35f) : Vector3.Zero;
                Segment(pose.UpperTip, pose.UpperLimb, .028f, 0xa69666, stowed);
                Segment(pose.UpperLimb, pose.LeftHand, .035f, 0xa69666, stowed);
                Segment(pose.LeftHand, pose.LowerLimb, .035f, 0xa69666, stowed);
                Segment(pose.LowerLimb, pose.LowerTip, .028f, 0xa69666, stowed);
                Segment(pose.UpperTip, pose.StringNock, .009f, 0x443d32, stowed);
                Segment(pose.StringNock, pose.LowerTip, .009f, 0x443d32, stowed);
                ArcherStringSegmentsDrawn += 2;
                if (!climbing && pose.ArrowVisible)
                {
                    NockedArrowsDrawn++;
                    Segment(pose.ArrowNock, pose.ArrowTip, .016f, 0xc9c7a4);
                    var direction = (Point(pose.ArrowTip) - Point(pose.ArrowNock)).Normalized();
                    var basis = Basis.LookingAt(direction, Vector3.Up);
                    var tail = Point(pose.ArrowNock) + direction * .045f;
                    Part(tail, new(.075f, .012f, .065f), 0xc6bf9c, local: basis);
                    Part(tail, new(.012f, .075f, .065f), 0xc6bf9c, local: basis);
                    Part(Point(pose.ArrowTip), new(.035f, .022f, .055f), 0xa4a599, local: basis);
                }
            }
            else
            {
                float swing = -Math.Max(0, attack) * 1.4f + Math.Max(0, -attack) * .7f;
                var shoulder = new Vector3(.18f, .55f, -.035f);
                Part(shoulder + new Basis(Vector3.Right, swing) * new Vector3(.02f, -.01f, -.065f),
                    new(0.035f, 0.48f, 0.045f), 0xc8c8d0, swing);
                Part(new(-0.21f, 0.40f, -0.07f), new(0.18f, 0.28f, 0.05f), 0x59777d);
            }
            EndActorRig();
            if (session.SelectedDefenderIds.Contains(unit.Id))
            {
                foreach (int side in new[] { -1, 1 })
                {
                    SiegeBox(batches, at + new Vector3(side * 0.23f, 0.015f, 0), new(0.025f, 0.02f, 0.46f), 0x8fc7d4);
                    SiegeBox(batches, at + new Vector3(0, 0.015f, side * 0.23f), new(0.46f, 0.02f, 0.025f), 0x8fc7d4);
                }
                float fraction = (float)(unit.Health / unit.MaxHealth);
                SiegeBox(batches, at + new Vector3(-(1 - fraction) * 0.20f, WorldScale.Feet(7), 0),
                    new(0.40f * fraction, 0.035f, 0.035f), 0x7df59a);
            }
            DefendersDrawn++;
        }
    }

    private void DrawAccessLadder(Dictionary<int, List<Transform3D>> batches, SettlementState siege,
        WallComponent gate, List<Transform3D> highlight)
    {
        if (!gate.IsTower || gate.Health <= 0) return;
        float top = TerrainSprite.Elevation(_map!, gate.Cell.X, gate.Cell.Y) +
            WallDamageLayout.ActorWalkHeight(gate, _map);
        if (WallDamageLayout.HasAccessLadder(gate))
        {
            var direction = WallDamageLayout.LadderInward(gate);
            var position = WallDamageLayout.LadderPosition(gate);
            var center = new Vector3(position.X, 0, position.Y);
            var landing = WallDamageLayout.LadderClimbPosition(gate);
            float bottom = Math.Min(GroundLevel(center), GroundLevel(new(landing.X, 0, landing.Y)));
            Draw(position, direction, bottom);
            _accessLadderWallIds.Add(gate.Id);
        }
        foreach (var ladder in WallDamageLayout.WalkwayLadders(gate))
            Draw(ladder.Position, ladder.Direction, WallDamageLayout.GroundHeight(ladder.Walkway, _map!) +
                WallDamageLayout.WalkHeight(ladder.Walkway));

        void Draw(System.Numerics.Vector2 position, System.Numerics.Vector2 direction, float bottom)
        {
            var inward = new Vector3(direction.X, 0, direction.Y);
            var center = new Vector3(position.X, bottom, position.Y);
            var across = new Vector3(-inward.Z, 0, inward.X);
            float height = top - bottom;
            if (height <= 0) return;
            void Part(Vector3 position, Vector3 size, int color, Basis? basis = null)
            {
                var transform = SiegeBox(batches, position, size, color, orientation: basis);
                highlight.Add(transform);
                _siegePickTransforms[_siegePickBoxes.Count] = transform;
                _siegePickWallIds[_siegePickBoxes.Count] = gate.Id;
                _siegePickBoxes.Add((DefenseBounds(transform), gate.Cell, color));
            }
            foreach (int side in new[] { -1, 1 })
                Part(center + across * (side * 0.32f) + Vector3.Up * height / 2,
                    new(0.04f, height, 0.04f), 0xa69666);
            for (float y = WallDamageLayout.LadderRungSpacing; y < height; y += WallDamageLayout.LadderRungSpacing)
                Part(center + Vector3.Up * y, new(0.68f, 0.035f, 0.055f), 0x79634b,
                    Basis.LookingAt(inward, Vector3.Up));
        }
    }

    public bool PickSiegeActor(Vector2 pointer, out bool friendly, out int id)
    {
        friendly = false; id = 0;
        var pixel = ViewportPoint(pointer);
        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel);
        float nearest = float.PositiveInfinity;
        foreach (var actor in _siegeActorPickBoxes)
        {
            float distance = RayBoxDistance(origin, direction, actor.Transform);
            if (distance >= nearest || distance < 0) continue;
            nearest = distance; friendly = actor.Friendly; id = actor.Id;
        }
        if (id == 0) return false;
        foreach (var box in ScenePickingBoxes)
            if (RayBoxDistance(origin, direction,
                new Transform3D(Basis.Identity.Scaled(box.Bounds.Size), box.Bounds.GetCenter())) < nearest - 0.001f)
                return false;
        for (int i = 0; i < _siegePickBoxes.Count; i++)
        {
            var box = _siegePickBoxes[i];
            var transform = _siegePickTransforms.GetValueOrDefault(i,
                new Transform3D(Basis.Identity.Scaled(box.Bounds.Size), box.Bounds.GetCenter()));
            if (RayBoxDistance(origin, direction, transform) < nearest - .001f) return false;
        }
        foreach (var face in SceneRoofFaces)
        {
            var e1 = face.B - face.A; var e2 = face.C - face.A;
            var cross = direction.Cross(e2);
            float det = e1.Dot(cross);
            if (Math.Abs(det) < 0.00001f) continue;
            var offset = origin - face.A;
            float u = offset.Dot(cross) / det;
            var q = offset.Cross(e1);
            float v = direction.Dot(q) / det;
            float distance = e2.Dot(q) / det;
            if (u >= 0 && v >= 0 && u + v <= 1 && distance >= 0 && distance < nearest - 0.001f) return false;
        }
        return true;
    }
}
