using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly Node3D _fortifications = new();
    private readonly Node3D _siegeActors = new();
    private readonly List<(Aabb Bounds, Pos Anchor, int Color)> _siegePickBoxes = [];
    private readonly Dictionary<int, MultiMeshInstance3D> _wallInstances = [];
    private readonly Dictionary<int, MultiMeshInstance3D> _actorInstances = [];
    private readonly Dictionary<int, List<Transform3D>> _actorBatches = [];
    private readonly List<WallComponent> _burningWalls = [];
    private readonly Dictionary<Pos, IReadOnlyList<WallFlameSurface>> _flameSurfaces = [];
    private SettlementState? _renderedSettlement;
    private int _siegeRevision = -1, _wallSignature;
    private double _siegeFraction = -1;

    public int WallsDrawn { get; private set; }
    public int GoblinsDrawn { get; private set; }
    public int SiegeGeometryBuilds { get; private set; }
    public int SiegeActorBuilds { get; private set; }
    public double SiegeActorBuildMilliseconds { get; private set; }
    private bool _siegeAnimationEnabled = true;
    public bool SiegeAnimationEnabled
    {
        get => _siegeAnimationEnabled;
        set
        {
            if (_siegeAnimationEnabled == value) return;
            _siegeAnimationEnabled = value;
            _siegeFraction = -1;
        }
    }
    public bool RepairCrewDrawn { get; private set; }
    public bool UpgradeCrewDrawn { get; private set; }
    public int BurningWallsDrawn { get; private set; }
    public bool FirefightingCrewDrawn { get; private set; }
    public int RepairWaterSegmentsDrawn { get; private set; }
    internal (Pos Cell, WallDefenseKind Kind)? DefenseModelPreview { get; init; }

    private void DrawDefenseModelPreview()
    {
        if (DefenseModelPreview is not { } preview) return;
        var batches = new Dictionary<int, List<Transform3D>>();
        DrawMountedDefense(batches, new(preview.Cell.X + 0.5f, 0, preview.Cell.Y + 0.5f), preview.Kind);
        foreach (var (color, instances) in batches)
            foreach (var transform in instances) BatchBox(color, transform);
    }

    private void InitializeSiegeRendering()
    {
        _viewport.AddChild(_fortifications);
        _viewport.AddChild(_siegeActors);
    }

    public Vector3 WallTop(WallComponent wall) => new(wall.Cell.X + 0.5f,
        TerrainSprite.Elevation(_map!, wall.Cell.X, wall.Cell.Y) + WallHeight(wall), wall.Cell.Y + 0.5f);

    private static float WallHeight(WallComponent wall) => WallDamageLayout.TopHeight(wall);

    private void SyncSiege(GameSession session)
    {
        var settlement = session.Game.Settlement;
        bool replaced = !ReferenceEquals(_renderedSettlement, settlement);
        bool revisionChanged = replaced || _siegeRevision != (settlement?.Revision ?? -1);
        double fraction = settlement?.InterpolationFraction ?? 0;
        if (!revisionChanged && (settlement is null || fraction == _siegeFraction ||
            settlement.Goblins.Count == 0 && settlement.Projectiles.Count == 0 && settlement.RepairingWall is null &&
            settlement.UpgradingWall is null &&
            _burningWalls.Count == 0)) return;
        _renderedSettlement = settlement;
        _siegeRevision = settlement?.Revision ?? -1;
        _siegeFraction = fraction;
        if (revisionChanged)
        {
            var signature = new HashCode();
            _burningWalls.Clear();
            if (settlement is not null)
            {
                signature.Add(settlement.Material);
                foreach (var wall in settlement.Walls)
                {
                    signature.Add(wall.Cell);
                    signature.Add(settlement.MaterialFor(wall));
                    signature.Add(WallUpgradeArtwork.VisualStep(settlement, wall));
                    signature.Add(WallDamageLayout.DamageBand(wall));
                    signature.Add(wall.Health == 0);
                    bool burning = WallDamageLayout.IsBurning(wall);
                    signature.Add(burning);
                    if (burning) _burningWalls.Add(wall);
                    signature.Add(wall.Defense);
                }
            }
            int wallSignature = signature.ToHashCode();
            if (replaced || wallSignature != _wallSignature)
            {
                _wallSignature = wallSignature;
                _flameSurfaces.Clear();
                foreach (var wall in _burningWalls)
                    _flameSurfaces.Add(wall.Cell, WallDamageLayout.FlameSurfaces(wall));
                _siegePickBoxes.Clear();
                _wallHighlightBoxes.Clear();
                var batches = new Dictionary<int, List<Transform3D>>();
                WallsDrawn = 0;
                if (settlement is not null)
                    foreach (var wall in settlement.Walls)
                    {
                        DrawWall(batches, settlement, wall);
                        WallsDrawn++;
                    }
                SyncSiegeBatches(_fortifications, _wallInstances, batches);
                SiegeGeometryBuilds++;
            }
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var batch in _actorBatches.Values) batch.Clear();
        var actors = _actorBatches;
        GoblinsDrawn = 0;
        RepairCrewDrawn = false;
        UpgradeCrewDrawn = false;
        FirefightingCrewDrawn = false;
        RepairWaterSegmentsDrawn = 0;
        BurningWallsDrawn = 0;
        if (settlement is not null)
        {
            foreach (var goblin in settlement.Goblins)
            {
                var pose = SiegeMotion.Goblin(settlement, goblin);
                if (!SiegeAnimationEnabled) pose = pose with { Stride = 0, Bob = 0, Sway = 0, Attack = 0 };
                var at = new Vector3(pose.Position.X, 0, pose.Position.Y);
                at.Y = GroundLevel(at);
                DrawGoblin(actors, at, pose, goblin);
                GoblinsDrawn++;
            }
            foreach (var shot in settlement.Projectiles)
            {
                float t = SiegeMotion.ProjectileProgress(settlement, shot);
                if (t >= 1) continue;
                var start = new Vector3((float)shot.StartX, 2.9f, (float)shot.StartY);
                var end = new Vector3((float)shot.EndX, 0.5f, (float)shot.EndY);
                start.Y += GroundLevel(start);
                end.Y += GroundLevel(end);
                bool arrow = shot.Kind is WallDefenseKind.None or WallDefenseKind.Ballista;
                float arc = shot.Kind == WallDefenseKind.Trebuchet ? 5 : arrow ? 0.15f : 1;
                var at = start.Lerp(end, t) + Vector3.Up * (Mathf.Sin(t * Mathf.Pi) * arc);
                var direction = (end - start + Vector3.Up * (Mathf.Cos(t * Mathf.Pi) * Mathf.Pi * arc)).Normalized();
                var orientation = Basis.LookingAt(direction, Math.Abs(direction.Y) > 0.98f ? Vector3.Right : Vector3.Up);
                SiegeBox(actors, at, arrow ? new(0.08f, 0.08f, 0.55f) : new(0.25f, 0.25f, 0.25f),
                    shot.Kind == WallDefenseKind.OilCauldron ? 0xc78739 : arrow ? 0xe4d3a0 : 0xa4a599,
                    orientation: orientation);
                if (arrow) SiegeBox(actors, at + direction * 0.28f, new(0.12f, 0.08f, 0.13f), 0xa4a599,
                    orientation: orientation);
            }
            if (settlement.RepairingWall is { } repair)
            {
                DrawRepairCrew(actors, settlement, repair);
                RepairCrewDrawn = true;
            }
            if (settlement.UpgradingWall is { } upgrade && upgrade != settlement.RepairingWall)
            {
                DrawRepairCrew(actors, settlement, upgrade, masonry: true);
                UpgradeCrewDrawn = true;
            }
            foreach (var wall in _burningWalls)
            {
                DrawWallFlames(actors, settlement, wall);
                BurningWallsDrawn++;
            }
        }
        SyncSiegeBatches(_siegeActors, _actorInstances, actors);
        SiegeActorBuilds++;
        SiegeActorBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RequestRender();
    }

    private static void DrawGoblin(Dictionary<int, List<Transform3D>> batches, Vector3 ground, GoblinPose pose, Goblin goblin)
    {
        float size = (float)goblin.BodyScale;
        float strike = Math.Max(0, pose.Attack), windup = Math.Max(0, -pose.Attack);
        var facing = Basis.LookingAt(new Vector3(pose.Forward.X, 0, pose.Forward.Y), Vector3.Up);
        var body = ground + facing * new Vector3(pose.Sway * size, (pose.Bob + strike * 0.025f) * size, -strike * 0.09f * size);
        void Part(Vector3 offset, Vector3 scale, int color, float swing = 0) =>
            SiegeBox(batches, body + facing * (offset * size), scale * size, color,
                orientation: facing * new Basis(Vector3.Right, swing));
        Part(new(0, 0.65f, 0), new(0.5f, 0.6f, 0.38f), 0x694d39, -strike * 0.18f);
        Part(new(0, 0.65f, 0.22f), new(0.4f, 0.5f, 0.12f), 0x443d32);
        Part(new(0, 1.13f, 0), new(0.5f, 0.44f, 0.46f), 0x66834c);
        Part(new(-0.32f, 1.16f, 0), new(0.25f, 0.14f, 0.15f), 0x66834c);
        Part(new(0.32f, 1.16f, 0), new(0.25f, 0.14f, 0.15f), 0x66834c);
        Part(new(0, 1.08f, -0.29f), new(0.24f, 0.17f, 0.2f), 0x718c4e);
        for (int side = -1; side <= 1; side += 2)
        {
            Part(new(side * 0.13f, 1.18f, -0.24f), new(0.09f, 0.07f, 0.04f), 0xd9d49b);
            Part(new(side * 0.13f, 1.18f, -0.265f), new(0.04f, 0.05f, 0.03f), 0x231e14);
            float step = pose.Stride * side;
            Part(new(side * 0.15f, 0.19f + Math.Max(0, step) * 0.09f, step * 0.16f),
                new(0.18f, 0.36f, 0.23f), 0x443d32, step * 0.4f);
            float arm = -step * 0.5f + (side > 0 ? windup * 0.7f - strike * 1.2f : -strike * 0.35f);
            Part(new(side * 0.35f, 0.66f + windup * 0.1f, -step * 0.12f - strike * 0.15f),
                new(0.15f, 0.48f, 0.18f), 0x66834c, arm);
        }
        if (goblin.Tier > 1) Part(new(0, 1.39f, 0), new(0.56f, 0.15f, 0.52f), 0x586b71);
        float weaponAngle = -pose.Stride * 0.5f + windup * 0.7f - strike * 1.2f;
        var weapon = new Basis(Vector3.Right, weaponAngle);
        var grip = new Vector3(0.46f, 0.6f + windup * 0.08f, -0.08f - pose.Stride * 0.12f);
        Part(grip + weapon * new Vector3(0, 0.35f, 0), new(0.09f, 1.3f, 0.09f), 0xa4a599, weaponAngle);
        Part(grip + weapon * new Vector3(0, 1.02f, 0), new(0.28f, 0.18f, 0.12f), 0x586b71, weaponAngle);
    }

    private void DrawRepairCrew(Dictionary<int, List<Transform3D>> batches, SettlementState siege, Pos cell, bool masonry = false)
    {
        var inward = cell.Y == siege.Bounds.Bottom ? Vector3.Forward :
            cell.Y == siege.Bounds.Y ? Vector3.Back :
            cell.X == siege.Bounds.X ? Vector3.Right : Vector3.Left;
        var across = new Vector3(-inward.Z, 0, inward.X);
        float phase = SiegeAnimationEnabled
            ? (float)((masonry ? siege.UpgradeAnimationPhase : siege.RepairAnimationPhase) +
                siege.InterpolationFraction * 0.1) * Mathf.Tau : 0;
        var site = new Vector3(cell.X + 0.5f, 0, cell.Y + 0.5f);
        site.Y = GroundLevel(site);
        float height = 1.9f;
        bool tower = false;
        WallComponent? targetWall = null;
        foreach (var wall in siege.Walls)
            if (wall.Cell == cell) { height = WallHeight(wall); tower = wall.IsTower; targetWall = wall; break; }
        bool firefighting = !masonry && targetWall is not null && RepairCrewArtwork.FightingFire(targetWall);
        FirefightingCrewDrawn |= firefighting;
        var scaffold = site - inward * (tower ? 0.83f : 0.36f);
        var facing = Basis.LookingAt(inward, Vector3.Up);
        SiegeBox(batches, scaffold + Vector3.Up * ((height + 0.15f) / 2), new(0.07f, height + 0.15f, 0.07f), 0xa69666);
        SiegeBox(batches, scaffold + Vector3.Up * (height + 0.15f), new(0.8f, 0.07f, 0.28f), 0x79634b,
            orientation: facing);
        for (int i = 0; i < 2; i++)
        {
            float step = i == 1 ? MathF.Sin(phase) : 0;
            var worker = scaffold + across * ((i - 0.5f) * 0.42f + step * 0.08f) + Vector3.Up * (height + 0.19f);
            void Part(Vector3 offset, Vector3 size, int color, Basis? local = null) =>
                SiegeBox(batches, worker + facing * offset, size, color, orientation: facing * (local ?? Basis.Identity));
            Part(new(0, 0.16f, 0), new(RepairCrewArtwork.WorkerWidth, 0.17f, 0.08f), i == 0 ? 0x79634b : 0x586b71);
            Part(new(0, RepairCrewArtwork.WorkerHeight - 0.05f, 0), new(0.1f, 0.1f, 0.1f), 0xc6bf9c);
            for (int side = -1; side <= 1; side += 2)
            {
                Part(new(side * 0.028f, 0.055f, step * side * 0.025f), new(0.035f, 0.11f, 0.05f), 0x443d32);
                Part(new(side * 0.065f, 0.17f, -0.02f), new(0.025f, 0.14f, 0.03f), 0xc6bf9c);
            }
            if (firefighting)
            {
                var pose = RepairCrewArtwork.BucketPose(
                    siege.RepairAnimationPhase + siege.InterpolationFraction * 0.1, i, SiegeAnimationEnabled);
                var tilt = new Basis(Vector3.Right, -pose.Tilt);
                var bucket = new Vector3(0.07f, 0.18f + pose.Lift, -0.07f - pose.Lift * 0.3f);
                void BucketPart(Vector3 offset, Vector3 size, int color) =>
                    Part(bucket + tilt * offset, size, color, tilt);
                float width = RepairCrewArtwork.BucketWidth, tall = RepairCrewArtwork.BucketHeight;
                BucketPart(new(0, -tall / 2, 0), new(width, 0.02f, width), 0x79634b);
                foreach (int side in new[] { -1, 1 })
                {
                    BucketPart(new(side * width / 2, 0, 0), new(0.018f, tall, width), 0x967a56);
                    BucketPart(new(0, 0, side * width / 2), new(width, tall, 0.018f), 0x967a56);
                    BucketPart(new(0, side * tall * 0.3f, 0), new(width + 0.02f, 0.015f, width + 0.02f), 0x586b71);
                }
                BucketPart(new(0, tall * 0.45f, 0), new(width * 0.72f, 0.012f, width * 0.72f), 0x8fc7d4);
                if (pose.Pour >= 0 && _flameSurfaces.TryGetValue(cell, out var surfaces) && surfaces.Count > 0)
                {
                    var surface = surfaces[i % surfaces.Count];
                    bool horizontal = cell.Y == siege.Bounds.Y || cell.Y == siege.Bounds.Bottom;
                    var wallBasis = horizontal ? Basis.Identity : new Basis(Vector3.Up, Mathf.Pi / 2);
                    var end = site + wallBasis * new Vector3(surface.Position.X, surface.Position.Y, surface.Position.Z);
                    var start = worker + facing * (bucket + tilt * new Vector3(0, tall / 2, -width / 2));
                    foreach (var segment in RepairCrewArtwork.BucketWater(
                        new(start.X, start.Y, start.Z), new(end.X, end.Y, end.Z), pose.Pour))
                    {
                        var a = new Vector3(segment.Start.X, segment.Start.Y, segment.Start.Z);
                        var b = new Vector3(segment.End.X, segment.End.Y, segment.End.Z);
                        var direction = b - a;
                        if (direction.LengthSquared() < 0.000001f) continue;
                        var orientation = Basis.LookingAt(direction.Normalized(),
                            Math.Abs(direction.Normalized().Y) > 0.98f ? Vector3.Right : Vector3.Up);
                        var center = (a + b) / 2;
                        float length = direction.Length() + 0.015f;
                        SiegeBox(batches, center, new(segment.Width, 0.025f, length),
                            0x8fc7d4, orientation: orientation);
                        SiegeBox(batches, center + orientation.Y * 0.014f,
                            new(segment.Width * 0.3f, 0.008f, length), 0xb9e0e8, orientation: orientation);
                        RepairWaterSegmentsDrawn++;
                    }
                    if (pose.Pour >= 0.625f)
                    {
                        float spread = (pose.Pour - 0.625f) / 0.375f;
                        SiegeBox(batches, end + Vector3.Up * 0.015f,
                            new(0.10f + spread * 0.18f, 0.025f, 0.08f + spread * 0.12f), 0x8fc7d4);
                    }
                }
            }
            else if (i == 0)
            {
                var grip = new Vector3(0.075f, 0.22f, -0.04f);
                var swing = new Basis(Vector3.Right, MathF.Sin(phase) * 1.1f);
                Part(grip + swing * new Vector3(0, RepairCrewArtwork.HammerHandleLength / 2, 0),
                    new(0.02f, RepairCrewArtwork.HammerHandleLength, 0.02f), 0x443d32, swing);
                Part(grip + swing * new Vector3(0, RepairCrewArtwork.HammerHandleLength, 0),
                    new(0.12f, 0.065f, 0.07f), 0xa4a599, swing);
            }
            else if (masonry)
                Part(new(0, 0.22f, -0.07f), new(0.18f, 0.1f, 0.12f), 0xa4a599);
            else
                Part(new(0, 0.255f, 0.07f), new(RepairCrewArtwork.BoardLength, 0.045f, 0.1f), 0xc0a16c);
        }
    }

    private float GroundLevel(Vector3 point) => _map is { } map && map.InBounds((int)point.X, (int)point.Z)
        ? TerrainSprite.Elevation(map, (int)point.X, (int)point.Z) : 0;

    private static Transform3D SiegeBox(Dictionary<int, List<Transform3D>> batches, Vector3 at, Vector3 size, int color,
        float angle = 0, Basis? orientation = null)
    {
        if (!batches.TryGetValue(color, out var batch)) batches.Add(color, batch = []);
        var basis = orientation ?? Basis.Identity;
        if (angle != 0) basis *= new Basis(Vector3.Back, angle);
        basis *= Basis.Identity.Scaled(size);
        var transform = new Transform3D(basis, at);
        batch.Add(transform);
        return transform;
    }

    private void SyncSiegeBatches(Node3D root, Dictionary<int, MultiMeshInstance3D> instances,
        Dictionary<int, List<Transform3D>> batches)
    {
        foreach (var (color, instance) in instances)
            if (!batches.ContainsKey(color)) instance.Visible = false;
        foreach (var (color, batch) in batches)
        {
            if (!instances.TryGetValue(color, out var instance))
            {
                instance = new MultiMeshInstance3D
                {
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _cube },
                    MaterialOverride = Material(color),
                };
                root.AddChild(instance);
                instances.Add(color, instance);
            }
            instance.Visible = batch.Count > 0;
            if (instance.Multimesh.InstanceCount != batch.Count) instance.Multimesh.InstanceCount = batch.Count;
            for (int i = 0; i < batch.Count; i++) instance.Multimesh.SetInstanceTransform(i, batch[i]);
        }
    }

    private void DrawWall(Dictionary<int, List<Transform3D>> batches, SettlementState settlement, WallComponent wall)
    {
        var highlight = new List<Transform3D>();
        _wallHighlightBoxes.Add(wall.Cell, highlight);
        var top = WallTop(wall);
        float height = WallHeight(wall);
        float baseY = top.Y - height;
        bool horizontal = wall.Cell.Y == settlement.Bounds.Y || wall.Cell.Y == settlement.Bounds.Bottom;
        var basis = horizontal ? Basis.Identity : new Basis(Vector3.Up, Mathf.Pi / 2);
        var material = settlement.MaterialFor(wall);
        bool wood = material == WallMaterial.Wood;
        int band = WallDamageLayout.DamageBand(wall);
        int bodyColor = wood ? band < 4 ? 0x79634b : 0x4b3b2c : band < 4 ? 0xa4a599 : 0x79796e;
        if (WallDamageLayout.IsBurning(wall)) bodyColor = wood ? 0x302a23 : 0x55544d;
        var origin = new Vector3(top.X, baseY, top.Z);
        foreach (var fragment in WallDamageLayout.Fragments(wall))
        {
            int color = fragment.Kind switch
            {
                WallFragmentKind.Trim => material == WallMaterial.ReinforcedStone ? 0x586b71 :
                    wood ? 0xa69666 : bodyColor,
                WallFragmentKind.Door => band < 4 ? 0x443d32 : 0x302a23,
                _ => bodyColor,
            };
            var center = origin + basis * new Vector3(fragment.Center.X, fragment.Center.Y, fragment.Center.Z);
            var localSize = new Vector3(fragment.Size.X, fragment.Size.Y, fragment.Size.Z);
            highlight.Add(SiegeBox(batches, center, localSize, color, orientation: basis));
            var worldSize = basis.X.Abs() * localSize.X + basis.Y.Abs() * localSize.Y + basis.Z.Abs() * localSize.Z;
            _siegePickBoxes.Add((new Aabb(center - worldSize / 2, worldSize), wall.Cell, color));
        }
        DrawWallUpgrade(batches, settlement, wall);
        if (wall.Health > 0 && wall.Defense != WallDefenseKind.None)
        {
            var mounted = new Dictionary<int, List<Transform3D>>();
            DrawMountedDefense(mounted, top + Vector3.Up * 0.05f, wall.Defense);
            foreach (var (color, transforms) in mounted)
            {
                if (!batches.TryGetValue(color, out var batch)) batches.Add(color, batch = []);
                batch.AddRange(transforms);
                highlight.AddRange(transforms);
            }
            _siegePickBoxes.Add((new Aabb(top - new Vector3(0.8f, 0, 0.8f), new(1.6f, 1.4f, 1.6f)),
                wall.Cell, bodyColor));
        }
    }

    private void DrawWallFlames(Dictionary<int, List<Transform3D>> batches, SettlementState siege, WallComponent wall)
    {
        var at = new Vector3(wall.Cell.X + 0.5f,
            TerrainSprite.Elevation(_map!, wall.Cell.X, wall.Cell.Y), wall.Cell.Y + 0.5f);
        double phase = SiegeAnimationEnabled ? (siege.Revision + siege.InterpolationFraction) * 0.1 : 0;
        bool horizontal = wall.Cell.Y == siege.Bounds.Y || wall.Cell.Y == siege.Bounds.Bottom;
        var basis = horizontal ? Basis.Identity : new Basis(Vector3.Up, Mathf.Pi / 2);
        var surfaces = _flameSurfaces[wall.Cell];
        for (int i = 0; i < surfaces.Count; i++)
        {
            foreach (var part in WallDamageLayout.FlameParts(surfaces[i], phase,
                wall.Cell.X * 13 + wall.Cell.Y * 7 + i * 31))
                SiegeBox(batches, at + basis * new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                    new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, orientation: basis);
        }
    }

    private static void DrawMountedDefense(Dictionary<int, List<Transform3D>> batches, Vector3 top, WallDefenseKind kind)
    {
        SiegeBox(batches, top + new Vector3(0, 0.09f, 0), new(0.8f, 0.18f, 0.8f), 0xa69666);
        switch (kind)
        {
            case WallDefenseKind.OilCauldron:
                SiegeBox(batches, top + new Vector3(0, 0.35f, 0), new(0.65f, 0.45f, 0.65f), 0x343b3e);
                SiegeBox(batches, top + new Vector3(0, 0.58f, 0), new(0.46f, 0.04f, 0.46f), 0x201b16);
                break;
            case WallDefenseKind.Rocks:
                for (int i = 0; i < 3; i++)
                    SiegeBox(batches, top + new Vector3((i - 1) * 0.22f, 0.32f + (i == 1 ? 0.13f : 0), 0),
                        new(0.3f, 0.3f, 0.4f), 0xa4a599);
                break;
            case WallDefenseKind.Ballista:
                SiegeBox(batches, top + new Vector3(0, 0.42f, 0), new(0.14f, 0.65f, 0.14f), 0x79634b);
                SiegeBox(batches, top + new Vector3(0, 0.65f, 0), new(1.15f, 0.12f, 0.17f), 0x443d32);
                SiegeBox(batches, top + new Vector3(0, 0.68f, 0), new(0.12f, 0.12f, 1.2f), 0xa69666);
                break;
            case WallDefenseKind.Trebuchet:
                foreach (float side in new[] { -0.27f, 0.27f })
                    SiegeBox(batches, top + new Vector3(side, 0.52f, 0), new(0.14f, 0.95f, 0.18f), 0x79634b);
                SiegeBox(batches, top + new Vector3(0.16f, 0.96f, 0), new(0.13f, 1.5f, 0.13f), 0xa69666, -0.8f);
                SiegeBox(batches, top + new Vector3(-0.28f, 0.58f, 0), new(0.32f, 0.42f, 0.36f), 0x586b71);
                break;
        }
    }

    private void DrawSettlementGhost(GameSession session, PlacementPreview preview)
    {
        if (preview.Kind == PlacementKind.Settlement)
        {
            int color = preview.ValidCells?.Count > 0 ? 0x64c788 : 0xe47676;
            foreach (var cell in preview.Cells ?? Enumerable.Empty<Pos>())
            {
                if (!session.Game.Map.InBounds(cell)) continue;
                float ground = TerrainSprite.Elevation(session.Game.Map, cell.X, cell.Y);
                bool horizontal = cell.Y == preview.Area.Y || cell.Y == preview.Area.Bottom;
                Box(new(cell.X + 0.5f, ground + 0.85f, cell.Y + 0.5f),
                    horizontal ? new(1, 1.7f, 0.3f) : new(0.3f, 1.7f, 1), color);
            }
        }
        else if (preview.Kind == PlacementKind.WallDefense &&
            session.Game.Settlement?.Walls.FirstOrDefault(w => w.Cell == session.Cursor) is { } wall)
        {
            var batches = new Dictionary<int, List<Transform3D>>();
            DrawMountedDefense(batches, WallTop(wall), preview.Defense);
            foreach (var (color, instances) in batches)
                foreach (var transform in instances) BatchBox(PreviewColor(color), transform);
        }
    }
}
