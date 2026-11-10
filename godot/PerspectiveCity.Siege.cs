using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly Node3D _fortifications = new();
    private readonly Node3D _siegeActors = new();
    private readonly List<(Aabb Bounds, Pos Anchor, int Color)> _siegePickBoxes = [];
    private readonly Dictionary<int, Transform3D> _siegePickTransforms = [];
    private readonly Dictionary<int, int> _siegePickWallIds = [];
    public int? PickedWallId { get; private set; }
    private readonly Dictionary<int, MultiMeshInstance3D> _wallInstances = [];
    private readonly Dictionary<int, List<Transform3D>> _wallBatches = [];
    private readonly Dictionary<int, MultiMeshInstance3D> _actorInstances = [];
    private readonly Dictionary<int, List<Transform3D>> _actorBatches = [];
    private readonly Dictionary<MultiMesh, float[]> _siegeBuffers = [];
    private readonly Dictionary<MultiMesh, Transform3D[]> _siegeTransforms = [];
    private readonly Dictionary<SiegeEnemyKind, IReadOnlyList<TrollPart>> _trollModels = [];
    private readonly Dictionary<(bool Friendly, int Id), (Basis Facing, ulong Frame)> _actorFacings = [];
    private double _actorFrameDelta;
    internal int ActorBufferUploads { get; private set; }
    internal int WallSnapshotsCapturedLastSync { get; private set; }
    private readonly Dictionary<int, (int Color, int Index)> _projectileInstances = [];
    internal Vector3? RenderedProjectilePosition(int id) =>
        _projectileInstances.TryGetValue(id, out var slot)
            ? _actorBatches[slot.Color][slot.Index].Origin : null;
    private readonly List<WallComponent> _burningWalls = [];
    private readonly Dictionary<int, IReadOnlyList<WallFlameSurface>> _flameSurfaces = [];
    private readonly Dictionary<WallComponent, (WallPartState State, IReadOnlyList<PalisadeWallPart> Parts)> _wallPartCache = [];
    private readonly record struct WallPartState(WallMaterial Material, int Band, bool Healthy, bool Intact, bool Burning,
        float Exterior, float Width, float Depth, Pos Cell, System.Numerics.Vector2 Center, System.Numerics.Vector2 Tangent);
    private int _wallPartsTerrainVersion = -1;
    private SettlementState? _renderedSettlement;
    private int _siegeRevision = -1, _wallGeometryTerrainVersion = -1;
    private readonly Dictionary<int, WallGeometrySnapshot> _wallGeometrySnapshots = [];
    private readonly record struct WallGeometryState(Pos Cell, Vector3 Top, System.Numerics.Vector2 Center,
        System.Numerics.Vector2 Tangent, WallMaterial Material, int Upgrade, int Damage, bool Healthy,
        bool Intact, bool Burning, bool Original, bool Tower, bool Gate, bool GateWing, bool Junction,
        WallDefenseKind Defense, int Facing, float Exterior, float Width, float Depth);
    private readonly record struct LadderGeometry(System.Numerics.Vector2 Position, System.Numerics.Vector2 Direction,
        int WallId, float Height);
    private sealed record WallGeometrySnapshot(WallComponent Wall, WallGeometryState State,
        (System.Numerics.Vector2 Position, System.Numerics.Vector2 Inward)? GroundLadder, LadderGeometry[] Ladders);
    private double _siegeFraction = -1;
    private double _siegeVisualSeconds = -1;

    public int WallsDrawn { get; private set; }
    internal int TimberWallPartsDrawn { get; private set; }
    public int EnemiesDrawn { get; private set; }
    public int SiegeGeometryBuilds { get; private set; }
    internal int SiegeAppendBuilds { get; private set; }
    internal int WallsRegeneratedLastSync { get; private set; }
    internal IReadOnlyList<(int Wall, int Color, Transform3D Transform)> FortificationGeometry =>
        _siegePickBoxes.Select((box, index) => (_siegePickWallIds[index], box.Color, _siegePickTransforms[index])).ToArray();
    internal bool FortificationBuffersMatch =>
        MatchSpatialSiege(_wallBatches, _wallInstances, false) && MatchSpatialSiege(_extensionWallBatches, _extensionWallInstances, false);
    internal bool EncodedFortificationBuffersMatch =>
        MatchSpatialSiege(_wallBatches, _wallInstances, true) && MatchSpatialSiege(_extensionWallBatches, _extensionWallInstances, true);
    internal string FortificationMismatch
    {
        get
        {
            foreach (var (batches, owner) in new[] { (_wallBatches, _wallInstances), (_extensionWallBatches, _extensionWallInstances) })
                foreach (var (color, batch) in batches)
                {
                    if (!_siegePages.TryGetValue(owner, out var group)) return $"missing spatial group color={color}";
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (!group.Slots.TryGetValue((color, i), out var slot)) return $"missing spatial slot color={color} index={i}";
                        if (!batch[i].IsEqualApprox(slot.Page.Mesh.GetInstanceTransform(slot.Index)))
                            return $"color={color} slot={i} expected={batch[i]} actual={slot.Page.Mesh.GetInstanceTransform(slot.Index)}";
                    }
                }
            return "";
        }
    }
    internal bool ActorBuffersMatch => MatchSpatialSiege(_actorBatches, _actorInstances, false, bounded: false);

    internal void InvalidateFortificationCache()
    {
        _wallPartCache.Clear();
        _wallArtifacts.Clear();
        _wallGeometryTerrainVersion = -1;
    }
    internal double SiegeGeometryBuildMilliseconds { get; private set; }
    internal double WallAssemblyMilliseconds, WallPageMilliseconds, WallTopMilliseconds;
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
    internal (Pos Cell, bool Gate)? WallConstructionPreview { get; init; }
    internal Aabb? WallPreviewBounds { get; private set; }

    private void DrawWallConstructionPreview()
    {
        if (WallConstructionPreview is not { } preview) return;
        var center = new System.Numerics.Vector2(preview.Cell.X + .5f, preview.Cell.Y + .5f);
        var wall = WallDamageLayout.Preview(center - new System.Numerics.Vector2(2, 0),
            center + new System.Numerics.Vector2(2, 0), WallMaterial.Wood, preview.Gate);
        WallPreviewBounds = null;
        foreach (var model in WallModelParts(wall, _map))
        {
            var part = model.Fragment;
            var position = new Vector3(center.X + part.Center.X, part.Center.Y, center.Y + part.Center.Z);
            var size = new Vector3(part.Size.X, part.Size.Y, part.Size.Z);
            BatchBox(model.Color, new Transform3D(Basis.Identity.Scaled(size), position));
            var bounds = new Aabb(position - size / 2, size);
            WallPreviewBounds = WallPreviewBounds is { } previous ? previous.Merge(bounds) : bounds;
        }
    }

    private void DrawDefenseModelPreview()
    {
        if (DefenseModelPreview is not { } preview) return;
        var batches = new Dictionary<int, List<Transform3D>>();
        DrawMountedDefense(batches, new(preview.Cell.X + 0.5f, 0, preview.Cell.Y + 0.5f), preview.Kind);
        DefensePreviewBounds = null;
        foreach (var (color, instances) in batches)
            foreach (var transform in instances)
            {
                BatchBox(color, transform);
                var bounds = DefenseBounds(transform);
                DefensePreviewBounds = DefensePreviewBounds is { } previous ? previous.Merge(bounds) : bounds;
            }
    }

    private void InitializeSiegeRendering()
    {
        _viewport.AddChild(_fortifications);
        _viewport.AddChild(_siegeActors);
        _oilSheetMesh.MaterialOverride = _oilMaterial;
        _siegeActors.AddChild(_oilSheetMesh);
    }

    public Vector3 WallTop(WallComponent wall) => new(wall.Center.X,
        WallGround(wall) + WallHeight(wall), wall.Center.Y);

    private float WallGround(WallComponent wall) => WallDamageLayout.GroundHeight(wall, _map!);

    private static Basis WallBasis(WallComponent wall) =>
        new(new Vector3(wall.Tangent.X, 0, wall.Tangent.Y), Vector3.Up,
            new Vector3(-wall.Tangent.Y, 0, wall.Tangent.X));

    private static float WallHeight(WallComponent wall) => WallDamageLayout.TopHeight(wall);

    private void SyncSiege(GameSession session)
    {
        var settlement = session.PresentedSiege;
        bool replaced = !ReferenceEquals(_renderedSettlement, settlement);
        if (replaced || _wallPartsTerrainVersion != session.Game.Map.TerrainVersion)
        {
            _wallPartCache.Clear();
            _wallPartsTerrainVersion = session.Game.Map.TerrainVersion;
        }
        if (replaced)
        {
            _aimAngles.Clear();
            _aimStarts.Clear();
            _actorFacings.Clear();
            _actorRigs.Clear();
        }
        bool revisionChanged = replaced || _siegeRevision != (settlement?.Revision ?? -1) ||
            _wallGeometryTerrainVersion != session.Game.Map.TerrainVersion;
        double fraction = settlement?.InterpolationFraction ?? 0;
        int selectedHash = session.SelectedDefenderIds.Aggregate(17, (hash, id) => unchecked(hash * 31 + id));
        bool selectionChanged = selectedHash != _defenderSelectionHash;
        _defenderSelectionHash = selectedHash;
        if (!selectionChanged && !revisionChanged && (settlement is null ||
            fraction == _siegeFraction && settlement.VisualSeconds == _siegeVisualSeconds ||
            settlement.Enemies.Count == 0 && settlement.Projectiles.Count == 0 && settlement.VisibleRepairingWall is null &&
            settlement.Defenders.Count == 0 && settlement.Deaths.Count == 0 &&
            settlement.UpgradingWall is null &&
            _burningWalls.Count == 0)) return;
        _renderedSettlement = settlement;
        _siegeRevision = settlement?.Revision ?? -1;
        _siegeFraction = fraction;
        _siegeVisualSeconds = settlement?.VisualSeconds ?? -1;
        if (revisionChanged)
        {
            var snapshots = new Dictionary<int, WallGeometrySnapshot>();
            _burningWalls.Clear();
            WallSnapshotsCapturedLastSync = 0;
            if (settlement is not null)
            {
                var states = settlement.Walls.Select(wall => (Wall: wall, State: CaptureWallState(settlement, wall))).ToArray();
                bool supportChanged = _wallGeometryTerrainVersion != session.Game.Map.TerrainVersion ||
                    states.Length != _wallGeometrySnapshots.Count ||
                    states.Any(value => !_wallGeometrySnapshots.TryGetValue(value.Wall.Id, out var previous) ||
                        !ReferenceEquals(value.Wall, previous.Wall) || previous.State != value.State);
                foreach (var (wall, state) in states)
                {
                    bool burning = WallDamageLayout.IsBurning(wall);
                    if (burning) _burningWalls.Add(wall);
                    if (_wallGeometrySnapshots.TryGetValue(wall.Id, out var previous) &&
                        ReferenceEquals(wall, previous.Wall) && state == previous.State &&
                        !replaced && _wallGeometryTerrainVersion == session.Game.Map.TerrainVersion &&
                        (!wall.IsTower || !supportChanged))
                        snapshots.Add(wall.Id, previous);
                    else
                    {
                        snapshots.Add(wall.Id, CaptureWallGeometry(settlement, wall, state));
                        WallSnapshotsCapturedLastSync++;
                    }
                }
            }
            var live = settlement is null ? [] : settlement.Enemies.Select(enemy => (false, enemy.Id))
                .Concat(settlement.Defenders.Select(unit => (true, unit.Id))).ToHashSet();
            foreach (var stale in _actorFacings.Keys.Where(key => !live.Contains(key)).ToArray())
                _actorFacings.Remove(stale);
            foreach (var stale in _actorRigs.Keys.Where(key => !live.Contains(key)).ToArray())
                _actorRigs.Remove(stale);
            foreach (var stale in _aimStarts.Keys.Where(id => !snapshots.ContainsKey(id)).ToArray())
                _aimStarts.Remove(stale);
            bool terrainChanged = _wallGeometryTerrainVersion != session.Game.Map.TerrainVersion;
            bool existingChanged = _wallGeometrySnapshots.Any(pair =>
                !snapshots.TryGetValue(pair.Key, out var current) || !SameWallGeometry(pair.Value, current));
            if (replaced || terrainChanged || existingChanged || snapshots.Count != _wallGeometrySnapshots.Count)
            {
                long geometryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                bool append = !replaced && !terrainChanged && !existingChanged &&
                    snapshots.Count > _wallGeometrySnapshots.Count &&
                    snapshots.Values.Where(snapshot => !_wallGeometrySnapshots.ContainsKey(snapshot.Wall.Id))
                        .All(snapshot => !snapshot.Wall.IsOriginal);
                var previousWalls = append ? _wallBatches.ToDictionary(pair => pair.Key, pair => pair.Value.Count) : null;
                var previousExtensions = append ? _extensionWallBatches.ToDictionary(pair => pair.Key, pair => pair.Value.Count) : null;
                int firstTop = append ? _wallTopFaces.Count : 0;
                if (!append)
                {
                    var survivingWalls = snapshots.Values.Select(snapshot => snapshot.Wall).ToHashSet();
                    foreach (var wall in _wallPartCache.Keys.Where(wall => !survivingWalls.Contains(wall)).ToArray())
                        _wallPartCache.Remove(wall);
                    foreach (var id in _wallArtifacts.Keys.Where(id => !snapshots.ContainsKey(id)).ToArray())
                        _wallArtifacts.Remove(id);
                    foreach (var id in _flameSurfaces.Keys.Where(id => !snapshots.TryGetValue(id, out var current) ||
                        !_wallGeometrySnapshots.TryGetValue(id, out var previous) || previous.State != current.State).ToArray())
                        _flameSurfaces.Remove(id);
                    _siegePickBoxes.Clear();
                    _siegePickTransforms.Clear();
                    _siegePickWallIds.Clear();
                    _wallHighlightBoxes.Clear();
                    _wallTopFaces.Clear();
                    TimberWallPartsDrawn = 0;
                    foreach (var values in _wallBatches.Values) values.Clear();
                    foreach (var values in _extensionWallBatches.Values) values.Clear();
                    _movingDefenseParts.Clear();
                    _accessLadderWallIds.Clear();
                    _aimedParts.Clear();
                    _defenseAngles.Clear();
                    WallsDrawn = 0;
                }
                foreach (var wall in _burningWalls)
                    if (!_flameSurfaces.ContainsKey(wall.Id))
                        _flameSurfaces.Add(wall.Id, WallDamageLayout.FlameSurfaces(wall,
                            WallModelParts(wall, _map).Select(part => part.Fragment).ToArray()));
                WallsRegeneratedLastSync = 0;
                WallArtifactsReusedLastSync = 0;
                if (settlement is not null)
                    foreach (var wall in settlement.Walls)
                    {
                        if (append && _wallGeometrySnapshots.ContainsKey(wall.Id)) continue;
                        DrawOrReuseWall(settlement, snapshots[wall.Id], replaced || terrainChanged);
                        WallsDrawn++;
                    }
                WallAssemblyMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(geometryStarted).TotalMilliseconds;
                long uploadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                SyncSiegeBatches(_fortifications, _wallInstances, _wallBatches,
                    appendFrom: previousWalls, buffered: true);
                WallPageMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;
                long topsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                SyncWallTops(firstTop, previousExtensions);
                WallTopMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(topsStarted).TotalMilliseconds;
                _wallGeometrySnapshots.Clear();
                foreach (var pair in snapshots) _wallGeometrySnapshots.Add(pair.Key, pair.Value);
                _wallGeometryTerrainVersion = session.Game.Map.TerrainVersion;
                if (append) SiegeAppendBuilds++;
                SiegeGeometryBuilds++;
                SiegeGeometryBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(geometryStarted).TotalMilliseconds;
                _wallTimingFrame = Engine.GetProcessFrames();
            }
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var batch in _actorBatches.Values) batch.Clear();
        _projectileInstances.Clear();
        foreach (var batch in _splashBatches.Values) batch.Clear();
        var actors = _actorBatches;
        ActorRigsReusedLastFrame = 0;
        OrcPartsDrawn = OrcsDrawn = 0;
        _siegeActorPickBoxes.Clear();
        EnemiesDrawn = 0;
        RepairCrewDrawn = false;
        UpgradeCrewDrawn = false;
        FirefightingCrewDrawn = false;
        RepairWaterSegmentsDrawn = 0;
        BurningWallsDrawn = 0;
        OilFlowSegmentsDrawn = DroppedStonesDrawn = DefenseSplashesDrawn = 0;
        OilSheetTriangles = 0;
        LargestDefenseSplashRadius = 0;
        if (settlement is not null)
        {
            AnimateMountedDefenses();
            AnimateAimedWeapons();
            foreach (var goblin in settlement.Enemies)
            {
                int signature = HashCode.Combine(goblin.Kind, goblin.Tier, goblin.AppearanceSeed, SiegeAnimationEnabled);
                var endpoint = new Vector3((float)goblin.X, 0, (float)goblin.Y);
                endpoint.Y = GroundLevel(endpoint);
                if (ReuseInvisibleRig(actors, false, goblin.Id, signature, endpoint))
                {
                    EnemiesDrawn++;
                    continue;
                }
                var pose = SiegeMotion.Enemy(settlement, goblin);
                var facing = SmoothActorFacing(false, goblin.Id, new(pose.Forward.X, 0, pose.Forward.Y));
                pose = pose with { Forward = new(-facing.Z.X, -facing.Z.Z) };
                if (!SiegeAnimationEnabled) pose = pose with { Stride = 0, Bob = 0, Sway = 0, Attack = 0 };
                var at = new Vector3(pose.Position.X, 0, pose.Position.Y);
                at.Y = GroundLevel(at);
                BeginActorRig(false, goblin.Id, signature, new(facing, at));
                DrawEnemy(actors, at, pose, goblin);
                EndActorRig();
                EnemiesDrawn++;
            }
            DrawGarrisonActors(actors, session, settlement);
            foreach (var shot in settlement.Projectiles)
            {
                if (shot.Melee) continue;
                if (shot.Id > 0 && shot.Kind == WallDefenseKind.Trebuchet && !shot.Applied)
                {
                    var target = new Vector3((float)shot.EndX, shot.WorldEndHeight + .05f, (float)shot.EndY);
                    foreach (float radius in new[] { .75f, 2.5f })
                        for (int step = 0; step < 24; step++)
                        {
                            float angle = step * Mathf.Tau / 24;
                            var point = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                            point.Y = GroundLevel(point) + .05f;
                            SiegeBox(actors, point, new(.16f, .025f, .08f), 0xe5cf79);
                        }
                }
                float t = SiegeMotion.ProjectileProgress(settlement, shot);
                if (t >= 1) continue;
                if (shot.Kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
                {
                    DrawDroppedAttack(actors, settlement, shot, t);
                    continue;
                }
                var start = new Vector3((float)shot.StartX, shot.StartHeight, (float)shot.StartY);
                var end = new Vector3((float)shot.EndX, shot.EndHeight, (float)shot.EndY);
                start.Y += GroundLevel(start);
                end.Y += GroundLevel(end);
                bool arrow = shot.Kind is WallDefenseKind.None or WallDefenseKind.Ballista;
                float arc = shot.Kind == WallDefenseKind.Trebuchet ? 5 : arrow ? 0.15f : 1;
                if (shot.Id > 0)
                {
                    if (SiegeMotion.ProjectileElapsed(settlement, shot) < shot.Windup || shot.Applied)
                    {
                        if (shot.Applied && shot.Kind == WallDefenseKind.Trebuchet)
                            SiegeBox(actors, new((float)shot.EndX, shot.WorldEndHeight + .04f, (float)shot.EndY),
                                new(.45f, .1f, .45f), 0x8b806b);
                        continue;
                    }
                    t = SiegeMotion.ProjectileFlightProgress(settlement, shot);
                    start.Y = shot.WorldStartHeight; end.Y = shot.WorldEndHeight; arc = shot.ArcHeight;
                }
                var at = start.Lerp(end, t) + Vector3.Up * (Mathf.Sin(t * Mathf.Pi) * arc);
                var direction = (end - start + Vector3.Up * (Mathf.Cos(t * Mathf.Pi) * Mathf.Pi * arc)).Normalized();
                var orientation = Basis.LookingAt(direction, Math.Abs(direction.Y) > 0.98f ? Vector3.Right : Vector3.Up);
                int projectileColor = arrow ? 0xe4d3a0 : 0xa4a599;
                if (shot.Id > 0)
                    _projectileInstances.Add(shot.Id, (projectileColor, actors.GetValueOrDefault(projectileColor)?.Count ?? 0));
                SiegeBox(actors, at, arrow ? new(0.035f, 0.035f, 0.55f) : new(0.25f, 0.25f, 0.25f),
                    shot.Kind == WallDefenseKind.OilCauldron ? 0xc78739 : arrow ? 0xe4d3a0 : 0xa4a599,
                    orientation: orientation);
                if (arrow)
                {
                    SiegeBox(actors, at + direction * 0.28f, new(0.08f, 0.04f, 0.10f), 0xa4a599, orientation: orientation);
                    foreach (float angle in new[] { 0f, Mathf.Pi / 2 })
                        SiegeBox(actors, at - direction * .21f, new(.15f, .02f, .15f), 0xc6bf9c,
                            orientation: orientation * new Basis(Vector3.Forward, angle));
                }
            }
            DrawSiegeDeaths(actors, settlement);
            if (settlement.VisibleRepairingSection is { } repair)
            {
                DrawRepairCrew(actors, settlement, repair);
                RepairCrewDrawn = true;
            }
            if (settlement.Enemies.Count == 0 && settlement.UpgradingSection is { } upgrade &&
                upgrade.Id != settlement.VisibleRepairingSection?.Id)
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
        SyncOilSheet();
        SyncSiegeBatches(_siegeActors, _actorInstances, actors, buffered: true);
        SyncSiegeBatches(_siegeActors, _splashInstances, _splashBatches, _splashDisk, buffered: true);
        SiegeActorBuilds++;
        SiegeActorBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _actorTimingFrame = Engine.GetProcessFrames();
        RequestRender();
    }

    private WallGeometryState CaptureWallState(SettlementState settlement, WallComponent wall) =>
        new(wall.Cell, WallTop(wall), wall.Center, wall.Tangent, wall.Material,
            WallUpgradeArtwork.VisualStep(settlement, wall), WallDamageLayout.DamageBand(wall),
            wall.Health == wall.MaxHealth, wall.Health > 0, WallDamageLayout.IsBurning(wall), wall.IsOriginal,
            wall.IsTower, wall.IsGate, wall.IsGateWing, wall.JunctionTower, wall.Defense, wall.DefenseFacing,
            WallDamageLayout.ExteriorSide(wall), WallDamageLayout.Width(wall), WallDamageLayout.Depth(wall));

    private WallGeometrySnapshot CaptureWallGeometry(SettlementState settlement, WallComponent wall, WallGeometryState state)
    {
        var ground = wall.IsTower && wall.Health > 0 ? settlement.GroundLadder(wall) : null;
        var ladders = wall.IsTower ? WallDamageLayout.WalkwayLadders(wall).Select(ladder =>
            new LadderGeometry(ladder.Position, ladder.Direction, ladder.Walkway.Id,
                WallDamageLayout.GroundHeight(ladder.Walkway, _map!) + WallDamageLayout.WalkHeight(ladder.Walkway))).ToArray() : [];
        return new(wall, state, ground, ladders);
    }

    private static bool SameWallGeometry(WallGeometrySnapshot before, WallGeometrySnapshot after) =>
        ReferenceEquals(before.Wall, after.Wall) && before.State == after.State &&
        before.GroundLadder == after.GroundLadder && before.Ladders.SequenceEqual(after.Ladders);

    private void DrawEnemy(Dictionary<int, List<Transform3D>> batches, Vector3 ground, SiegeEnemyPose pose, SiegeEnemy goblin)
    {
        if (goblin.Kind == SiegeEnemyKind.Orc)
        {
            DrawOrc(batches, ground, pose, goblin);
            return;
        }
        float size = (float)goblin.BodyScale;
        float strike = Math.Max(0, pose.Attack), windup = Math.Max(0, -pose.Attack);
        var facing = Basis.LookingAt(new Vector3(pose.Forward.X, 0, pose.Forward.Y), Vector3.Up);
        var body = ground + facing * new Vector3(pose.Sway * size, (pose.Bob + strike * 0.025f) * size, -strike * 0.09f * size);
        void Part(Vector3 offset, Vector3 scale, int color, float swing = 0)
        {
            var at = body + facing * (offset * size);
            var basis = facing * new Basis(Vector3.Right, swing);
            if (_deathActorTransform is { } collapse)
            {
                at = collapse * at;
                basis = collapse.Basis * basis;
            }
            int tint = _deathCharred ? 0x493b29 : color;
            var transform = SiegeBox(batches, at, scale * size, tint, orientation: basis);
            CaptureActorPart(tint, transform);
            if (_deathActorTransform is null)
                _siegeActorPickBoxes.Add((new(basis.Scaled(scale * size), at), goblin.Id, false));
        }
        if (goblin.Definition.IsTroll)
        {
            if (!_trollModels.TryGetValue(goblin.Kind, out var model))
                _trollModels[goblin.Kind] = model = TrollArtwork.Parts(goblin);
            foreach (var part in model)
            {
                var offset = new Vector3(part.Center.X, part.Center.Y, part.Center.Z);
                float swing = offset.Y < 0.4f ? pose.Stride * Math.Sign(offset.X) * 0.35f : 0;
                if (Math.Abs(offset.X) / goblin.Definition.WidthMultiplier >= 0.39 && part.Size.Y >= 0.6f)
                {
                    float reach = (float)(goblin.CollisionRadius / goblin.BodyScale) + 0.35f / size;
                    float angle = MathF.Asin(Math.Clamp((reach + offset.Z - part.Size.Z / 2 - 0.09f) /
                        part.Size.Y, 0, 0.98f));
                    swing = strike * angle - windup * 0.6f - pose.Stride * Math.Sign(offset.X) * 0.25f;
                    var shoulder = offset + Vector3.Up * part.Size.Y / 2;
                    offset = shoulder + new Basis(Vector3.Right, swing) * (offset - shoulder);
                }
                Part(offset, new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, swing);
            }
            return;
        }
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

    private void DrawRepairCrew(Dictionary<int, List<Transform3D>> batches, SettlementState siege, WallComponent targetWall, bool masonry = false)
    {
        var cell = targetWall.Cell;
        var inward = cell.Y == siege.Bounds.Bottom ? Vector3.Forward :
            cell.Y == siege.Bounds.Y ? Vector3.Back :
            cell.X == siege.Bounds.X ? Vector3.Right : Vector3.Left;
        if (!targetWall.IsOriginal)
        {
            var normal = new System.Numerics.Vector2(-targetWall.Tangent.Y, targetWall.Tangent.X);
            int side = siege.WallNetwork.Contains(targetWall.Center + normal) ? 1 : -1;
            inward = new(normal.X * side, 0, normal.Y * side);
        }
        var across = new Vector3(-inward.Z, 0, inward.X);
        float phase = SiegeAnimationEnabled
            ? (float)((masonry ? siege.UpgradeAnimationPhase : siege.RepairAnimationPhase) +
                siege.InterpolationFraction * 0.1) * Mathf.Tau : 0;
        var site = new Vector3(targetWall.Center.X, WallGround(targetWall), targetWall.Center.Y);
        float height = WallHeight(targetWall);
        bool tower = targetWall.IsTower;
        bool firefighting = !masonry && RepairCrewArtwork.FightingFire(targetWall);
        FirefightingCrewDrawn |= firefighting;
        var scaffold = site - inward * (tower ? WorldScale.Feet(9) : WallDamageLayout.Depth(targetWall) / 2 + 0.4f);
        var facing = Basis.LookingAt(inward, Vector3.Up);
        SiegeBox(batches, scaffold + Vector3.Up * ((height + 0.15f) / 2), new(0.07f, height + 0.15f, 0.07f), 0xa69666);
        SiegeBox(batches, scaffold + Vector3.Up * (height + 0.15f), new(0.8f, 0.07f, 0.28f), 0x79634b,
            orientation: facing);
        for (int i = 0; i < 2; i++)
        {
            float step = i == 1 ? MathF.Sin(phase) : 0;
            const float workerScale = 1.35f / RepairCrewArtwork.WorkerHeight;
            var worker = scaffold + across * ((i - 0.5f) * 0.75f + step * 0.08f) + Vector3.Up * (height + 0.19f);
            void Part(Vector3 offset, Vector3 size, int color, Basis? local = null) =>
                SiegeBox(batches, worker + facing * (offset * workerScale), size * workerScale,
                    color, orientation: facing * (local ?? Basis.Identity));
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
                if (pose.Pour >= 0 && _flameSurfaces.TryGetValue(targetWall.Id, out var surfaces) && surfaces.Count > 0)
                {
                    var surface = surfaces[i % surfaces.Count];
                    var wallBasis = WallBasis(targetWall);
                    var end = site + wallBasis * new Vector3(surface.Position.X, surface.Position.Y, surface.Position.Z);
                    var start = worker + facing * ((bucket + tilt * new Vector3(0, tall / 2, -width / 2)) * workerScale);
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
        ? map.Footbridges.FirstOrDefault(bridge => bridge.Complete &&
            bridge.Cells().Contains(new((int)point.X, (int)point.Z)))?.DeckHeight(map) ??
                TerrainSprite.Elevation(map, (int)point.X, (int)point.Z) : 0;

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
        Dictionary<int, List<Transform3D>> batches, Mesh? mesh = null,
        IReadOnlyDictionary<int, int>? appendFrom = null, bool buffered = false)
    {
        SyncSpatialSiege(root, instances, batches, mesh, buffered);
    }

    private void UpdateWallTransform(int color, int index, Transform3D transform)
    {
        var slot = SiegeSlot(_wallInstances, color, index);
        var mesh = slot.Mesh;
        mesh.SetInstanceTransform(slot.Index, transform);
        mesh.CustomAabb = mesh.CustomAabb.Merge(transform * mesh.Mesh.GetAabb());
        _wallBatches[color][index] = transform;
        if (_siegeTransforms.TryGetValue(mesh, out var transforms) && slot.Index < transforms.Length)
        {
            transforms[slot.Index] = transform;
            WriteInstance(_siegeBuffers[mesh], slot.Index * 12, transform);
        }
    }

    private Basis SmoothActorFacing(bool friendly, int id, Vector3 forward)
    {
        var desired = Basis.LookingAt(forward.Normalized(), Vector3.Up);
        var key = (friendly, id);
        ulong frame = Engine.GetProcessFrames();
        if (_actorFacings.TryGetValue(key, out var previous))
        {
            if (previous.Frame == frame) return previous.Facing;
            float blend = (float)(1 - Math.Exp(-12 * _actorFrameDelta));
            desired = new Basis(previous.Facing.GetRotationQuaternion().Slerp(desired.GetRotationQuaternion(), blend));
        }
        _actorFacings[key] = (desired, frame);
        return desired;
    }

    private void DrawWall(Dictionary<int, List<Transform3D>> batches, SettlementState settlement, WallComponent wall)
    {
        var highlight = new List<Transform3D>();
        _wallHighlightBoxes.Add(wall.Id, highlight);
        if (wall.IsGateWing) return;
        var top = WallTop(wall);
        float height = WallHeight(wall);
        float baseY = top.Y - height;
        var basis = WallBasis(wall);
        var origin = new Vector3(top.X, baseY, top.Z);
        foreach (var model in CachedWallModelParts(wall))
        {
            var fragment = model.Fragment;
            int color = model.Color;
            if (PalisadeArtwork.IsTexturedColor(color)) TimberWallPartsDrawn++;
            var center = origin + basis * new Vector3(fragment.Center.X, fragment.Center.Y, fragment.Center.Z);
            var localSize = new Vector3(fragment.Size.X, fragment.Size.Y, fragment.Size.Z);
            var transform = SiegeBox(wall.IsOriginal ? batches : _extensionWallBatches, center, localSize, color, orientation: basis);
            RecordWallTop(transform, color, !wall.IsOriginal);
            highlight.Add(transform);
            var worldSize = basis.X.Abs() * localSize.X + basis.Y.Abs() * localSize.Y + basis.Z.Abs() * localSize.Z;
            _siegePickTransforms[_siegePickBoxes.Count] = transform;
            _siegePickWallIds[_siegePickBoxes.Count] = wall.Id;
            _siegePickBoxes.Add((new Aabb(center - worldSize / 2, worldSize), wall.Cell, color));
        }
        DrawWallUpgrade(batches, settlement, wall);
        DrawAccessLadder(batches, settlement, wall, highlight);
        if (wall.Health > 0 && wall.Defense != WallDefenseKind.None)
        {
            if (wall.Defense is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
            {
                DrawDroppedDefense(batches, wall, MountedDefenseOrigin(wall), highlight);
                return;
            }
            var mounted = new Dictionary<int, List<Transform3D>>();
            var mount = MountedDefenseOrigin(wall);
            DrawMountedDefense(mounted, mount, wall.Defense, wall);
            foreach (var (color, transforms) in mounted)
            {
                if (!batches.TryGetValue(color, out var batch)) batches.Add(color, batch = []);
                foreach (var transform in transforms)
                {
                    int instanceIndex = batch.Count, highlightIndex = highlight.Count, pickIndex = _siegePickBoxes.Count;
                    batch.Add(transform); highlight.Add(transform);
                    var inverseAim = WeaponAim(wall).Inverse();
                    var canonical = new Transform3D(inverseAim * transform.Basis,
                        mount + inverseAim * (transform.Origin - mount));
                    _aimedParts.Add(new(wall, mount, canonical, color, instanceIndex, pickIndex, highlightIndex));
                    var size = transform.Basis.X.Abs() + transform.Basis.Y.Abs() + transform.Basis.Z.Abs();
                    _siegePickTransforms[_siegePickBoxes.Count] = transform;
                    _siegePickWallIds[_siegePickBoxes.Count] = wall.Id;
                    _siegePickBoxes.Add((new Aabb(transform.Origin - size / 2, size), wall.Cell, color));
                }
            }
        }
    }

    private IEnumerable<PalisadeWallPart> CachedWallModelParts(WallComponent wall)
    {
        if (wall.IsTower || wall.IsGate)
            return WallModelParts(wall, _map, foundations: true);
        var state = new WallPartState(wall.Material, WallDamageLayout.DamageBand(wall),
            wall.Health == wall.MaxHealth, wall.Health > 0, WallDamageLayout.IsBurning(wall), WallDamageLayout.ExteriorSide(wall),
            WallDamageLayout.Width(wall), WallDamageLayout.Depth(wall), wall.Cell, wall.Center, wall.Tangent);
        if (!_wallPartCache.TryGetValue(wall, out var cached) || cached.State != state)
            _wallPartCache[wall] = cached = (state, WallModelParts(wall, _map, foundations: true).ToArray());
        return cached.Parts;
    }

    private void DrawWallFlames(Dictionary<int, List<Transform3D>> batches, SettlementState siege, WallComponent wall)
    {
        var at = new Vector3(wall.Cell.X + 0.5f,
            TerrainSprite.Elevation(_map!, wall.Cell.X, wall.Cell.Y), wall.Cell.Y + 0.5f);
        double phase = SiegeAnimationEnabled ? siege.VisualSeconds : 0;
        var basis = WallBasis(wall);
        var surfaces = _flameSurfaces[wall.Id];
        for (int i = 0; i < surfaces.Count; i++)
        {
            foreach (var part in WallDamageLayout.FlameParts(surfaces[i], phase,
                wall.Cell.X * 13 + wall.Cell.Y * 7 + i * 31))
                SiegeBox(batches, at + basis * new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                    new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, orientation: basis);
        }
    }

    private Vector3 MountedDefenseOrigin(WallComponent wall, WallDefenseKind? kind = null) => new(wall.Center.X,
        TerrainSprite.Elevation(_map!, wall.Cell.X, wall.Cell.Y) +
            WallDamageLayout.ActorWalkHeight(wall, _map) +
            ((kind ?? wall.Defense) is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks ? 0 : .05f), wall.Center.Y);

    private void DrawMountedDefense(Dictionary<int, List<Transform3D>> batches, Vector3 top, WallDefenseKind kind,
        WallComponent? wall = null, bool animate = true, int? facing = null)
    {
        if (kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
        {
            var pose = animate ? DefensePose(wall, kind) : (-1, 0f);
            foreach (var part in TowerDefenseArtwork.Parts(wall, kind, TowerDefenseArtwork.Rise(wall, _map), facing))
            {
                var transform = DefenseTransform(TowerDefenseArtwork.Pose(part,
                    part.Station == pose.Item1 ? pose.Item2 : 0), top);
                if (!batches.TryGetValue(part.Color, out var batch)) batches.Add(part.Color, batch = []);
                batch.Add(transform);
            }
            return;
        }
        SiegeBox(batches, top + new Vector3(0, 0.09f, 0), new(0.8f, 0.18f, 0.8f), 0xa69666);
        switch (kind)
        {
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
        if (wall is not null)
        {
            var aim = WeaponAim(wall);
            foreach (var values in batches.Values)
                for (int i = 0; i < values.Count; i++)
                    values[i] = new(aim * values[i].Basis, top + aim * (values[i].Origin - top));
        }
    }

    private void DrawSettlementGhost(GameSession session, PlacementPreview preview)
    {
        if (preview.WallPlan is { } plan)
        {
            int color = plan.Valid ? 0x64c788 : 0xe47676;
            var tangent = plan.End - plan.Start;
            if (tangent.LengthSquared() < .00001f) return;
            tangent = System.Numerics.Vector2.Normalize(tangent);
            var spans = plan.Spans.Count > 0 ? plan.Spans : new[] { (A: plan.Start, B: plan.End) };
            var ghosts = new List<WallComponent>();
            var material = session.Game.Settlement?.UpgradeTargetMaterial ?? session.Game.Settlement?.Material ?? WallMaterial.Wood;
            foreach (var span in spans)
            {
                int count = plan.Gate ? 1 : Math.Max(1, (int)Math.Ceiling(System.Numerics.Vector2.Distance(span.A, span.B)));
                for (int i = 0; i < count; i++)
                    ghosts.Add(WallDamageLayout.Preview(System.Numerics.Vector2.Lerp(span.A, span.B, i / (float)count),
                        System.Numerics.Vector2.Lerp(span.A, span.B, (i + 1) / (float)count), material, plan.Gate));
            }
            foreach (var point in plan.Towers) ghosts.Add(WallDamageLayout.PreviewTower(point, material));
            foreach (var ghost in ghosts)
            {
                if (session.Game.Settlement is { } settlement)
                    WallDamageLayout.OrientPreview(ghost, settlement, preview.WallEnclosures);
                var point = ghost.Cell;
                if (!session.Game.Map.InBounds(point)) continue;
                var basis = WallBasis(ghost);
                var origin = new Vector3(ghost.Center.X, WallGround(ghost), ghost.Center.Y);
                foreach (var model in WallModelParts(ghost, _map, foundations: true))
                {
                    var part = model.Fragment;
                    BatchBox(color, new Transform3D(basis * Basis.Identity.Scaled(new(part.Size.X, part.Size.Y, part.Size.Z)),
                        origin + basis * new Vector3(part.Center.X, part.Center.Y, part.Center.Z)));
                }
            }
        }
        else if (preview.Kind == PlacementKind.Settlement)
        {
            int color = preview.ValidCells?.Count > 0 ? 0x64c788 : 0xe47676;
            foreach (var cell in preview.Cells ?? Enumerable.Empty<Pos>())
            {
                if (!session.Game.Map.InBounds(cell)) continue;
                float ground = TerrainSprite.Elevation(session.Game.Map, cell.X, cell.Y);
                bool horizontal = cell.Y == preview.Area.Y || cell.Y == preview.Area.Bottom;
                var center = new System.Numerics.Vector2(cell.X + .5f, cell.Y + .5f);
                var direction = horizontal ? System.Numerics.Vector2.UnitX : System.Numerics.Vector2.UnitY;
                var ghost = WallDamageLayout.Preview(center - direction / 2, center + direction / 2, WallMaterial.Wood);
                var basis = WallBasis(ghost);
                foreach (var model in WallModelParts(ghost, _map))
                {
                    var part = model.Fragment;
                    BatchBox(color, new Transform3D(basis * Basis.Identity.Scaled(new(part.Size.X, part.Size.Y, part.Size.Z)),
                        new Vector3(center.X, ground, center.Y) + basis * new Vector3(part.Center.X, part.Center.Y, part.Center.Z)));
                }
            }
        }
        else if (preview.Kind == PlacementKind.WallDefense &&
            session.SelectedWallComponent is { } wall)
        {
            var batches = new Dictionary<int, List<Transform3D>>();
            DrawMountedDefense(batches, MountedDefenseOrigin(wall, preview.Defense), preview.Defense, wall, animate: false,
                facing: preview.Rotation == 2 ? -1 : 1);
            foreach (var (color, instances) in batches)
                foreach (var transform in instances) BatchBox(PreviewColor(color), transform);
        }
    }
}
