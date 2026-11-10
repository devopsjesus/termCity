using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private const int ObjectChunkSize = 16;
    private readonly record struct ObjectVersion(int Map, int Analysis, int Navigation, int Terrain, int Yards,
        int Epoch, bool Areas, MapOverlay Overlay, int TerrainStyle, int DepthStyle, int Paths, int Measurement, bool Overview);
    private sealed class ObjectGeometry
    {
        internal readonly Dictionary<int, List<Transform3D>> Instances = [];
        internal readonly Dictionary<int, List<SurfaceVertex>> Roofs = [], Dirt = [];
        internal readonly Dictionary<CellVisual, List<SurfaceVertex>> Zones = [];
        internal readonly Dictionary<int, List<(int Start, float[] Buffer)>> InstanceSpans = [];
        internal readonly List<(Aabb Bounds, Pos Anchor, int Color)> Boxes = [], Picks = [], Yards = [];
        internal readonly List<(Aabb Bounds, Pos Anchor)> Foundations = [];
        internal readonly List<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> Faces = [];
        internal readonly List<(Transform3D Transform, Pos Anchor)> Highlights = [];
        internal readonly List<(Pos Owner, Vector3 Threshold)> Entrances = [];
        internal readonly Dictionary<Pos, PickGroup> Natural = [], Structures = [];
        internal readonly HashSet<TreeShape> Trees = [];
        internal readonly List<(Pos Cell, Vector3 A, Vector3 B, Vector3 C)> ZoneFaces = [];
    }

    private sealed class ObjectChunk(Pos key, ulong stamp)
    {
        internal readonly Pos Key = key;
        internal readonly ulong Stamp = stamp;
        internal bool Overview;
        internal readonly ObjectGeometry Geometry = new();
        internal readonly Dictionary<int, float[]> Buffers = [];
        internal readonly Dictionary<int, (Vector3[] Points, Vector3[] Normals, Vector2[] Uvs)> RoofBuffers = [];
        internal readonly Dictionary<ObjectPoolKey, (int Start, int Count)> Slots = [];
        internal int Buildings;
        internal int DoorTriangles;
        internal int ZonePlots, ZoneTriangles;
        internal double RoadOverlap, FootpathOverlap, MaximumRoadOverlap, MaximumFootpathOverlap;
        internal MeshInstance3D? RoofNode;
        internal long Used;
        internal long ManagedBytes;
        internal ChunkLifecycle State = ChunkLifecycle.Preparing;
        internal int NativeUploads;
        internal double EncodingMilliseconds;
        internal ObjectVersion Validated;
    }

    private ObjectGeometry? _objectCapture;
    private readonly Dictionary<Pos, ObjectChunk> _objectChunks = [], _visibleObjectChunks = [];
    private readonly Queue<Pos> _objectRequests = [];
    private readonly HashSet<Pos> _requiredObjectChunks = [];
    private readonly HashSet<Pos> _wantedObjectChunks = [];
    private (int X, int Y, int Right, int Bottom, ObjectVersion Version)? _objectRequestState;
    private IEnumerator<bool>? _objectPreparation;
    private ObjectChunk? _preparingObject;
    private sealed record ObjectEncodingWork(ObjectChunk Chunk, Task Task);
    private readonly Queue<ObjectEncodingWork> _objectEncodings = [];
    private Exception? _objectStreamingFailure;
    private ObjectVersion _preparationVersion;
    private GameSession? _streamSession;
    private GameMap? _streamMap;
    private int _objectCacheEpoch;
    private bool _streamingObjects;
    private bool _objectGroundWaiting;
    private double _lastObjectWork;
    private Vector2? _objectRequestCenter;
    private Vector2 _objectTravelDirection;
    internal int ObjectChunksBuilt { get; private set; }
    internal int ObjectChunksReused { get; private set; }
    internal double ObjectStreamingMilliseconds { get; private set; }
    internal int ObjectCacheCount => _objectChunks.Count;
    internal long ObjectEncodedCacheBytes => _objectChunks.Values.Sum(chunk =>
        chunk.Buffers.Values.Sum(buffer => (long)buffer.Length * sizeof(float)) +
        chunk.RoofBuffers.Values.Sum(buffer => (long)buffer.Points.Length * 8 * sizeof(float)));
    internal object? CachedObjectToken(Pos owner) => _objectChunks.GetValueOrDefault(ObjectKey(owner));
    internal IReadOnlyDictionary<Pos, object> CaptureCurrentOwnershipCache()
    {
        if (_streamSession is not { } session) throw new InvalidOperationException("No object cache session.");
        var version = ObjectVersionFor(session);
        return _objectChunks.Where(pair => pair.Value.Validated == version ||
            ObjectStamp(session, pair.Key) == pair.Value.Stamp)
            .ToDictionary(pair => pair.Key, pair => (object)pair.Value);
    }

    internal int ChangedOwnershipChunks(IReadOnlyDictionary<Pos, object> previous) => previous.Count(pair =>
        _objectChunks.TryGetValue(pair.Key, out var current) && !ReferenceEquals(pair.Value, current));
    internal bool ObjectStreamingPending => _streamingObjects &&
        (_requiredObjectChunks.Any(key => !_visibleObjectChunks.ContainsKey(key)) ||
        _objectEncodings.Any(work => _requiredObjectChunks.Contains(work.Chunk.Key)) ||
        _preparingObject is { } preparation && _requiredObjectChunks.Contains(preparation.Key) ||
        _uploadingObject is { } upload && _requiredObjectChunks.Contains(upload.Key) ||
        _objectRequests.Any(key => _requiredObjectChunks.Contains(key)));
    private bool ObjectWorkPending => _objectPreparation is not null || _objectEncodings.Count > 0 || _objectRequests.Count > 0 ||
        _uploadingObject is not null;

    internal bool IsMapObjectPresented(Pos cell)
    {
        if (!_streamingObjects || _streamSession is not { } session) return true;
        var map = session.Game.Map;
        if (!map.InBounds(cell)) return false;
        if (map.BuildingAt(cell.X, cell.Y) is not null)
        {
            var anchor = map.PosOf(map.BuildingAnchor(map.Index(cell.X, cell.Y)));
            return _visibleObjectChunks.TryGetValue(ObjectKey(anchor), out var chunk) &&
                chunk.Geometry.Structures.ContainsKey(anchor) &&
                (chunk.Validated == ObjectVersionFor(session) || ObjectStamp(session, chunk.Key) == chunk.Stamp);
        }
        if (_visibleObjectChunks.TryGetValue(ObjectKey(cell), out var old) &&
            (old.Geometry.Structures.ContainsKey(cell) ||
                map.FeatureAt(cell.X, cell.Y) is null && old.Geometry.Natural.ContainsKey(cell)))
            return false;
        return map.FeatureAt(cell.X, cell.Y) is null || !TerrainSprite.NaturalFeatureVisible(session.Game, cell.X, cell.Y) ||
            _visibleObjectChunks.TryGetValue(ObjectKey(cell), out var feature) && feature.Geometry.Natural.ContainsKey(cell) &&
            (feature.Validated == ObjectVersionFor(session) || ObjectStamp(session, feature.Key) == feature.Stamp);
    }

    private static Pos ObjectKey(Pos anchor) => new(anchor.X / ObjectChunkSize, anchor.Y / ObjectChunkSize);
    private ObjectVersion ObjectVersionFor(GameSession session) => new(session.Game.MapVersion,
        session.AreaOverlaysVisible && session.Overlay != MapOverlay.Off ? session.Game.AnalysisVersion : 0,
        session.Game.Map.NavigationVersion, session.Game.Map.TerrainVersion,
        session.Game.Map.HouseYardCells.Count, _objectCacheEpoch, session.AreaOverlaysVisible, session.Overlay,
        (int)TerrainTreatment, (int)DepthTreatment, session.AreaOverlaysVisible ? session.Game.Map.FootpathVersion : 0,
        (MeasureZoneRoadOverlap ? 1 : 0) | (MeasureZoneFootpathOverlap ? 2 : 0), _overviewLodActive);
    private static CellRect ObjectBounds(Pos key, GameMap map) => new(key.X * ObjectChunkSize, key.Y * ObjectChunkSize,
        Math.Min(ObjectChunkSize, map.Width - key.X * ObjectChunkSize),
        Math.Min(ObjectChunkSize, map.Height - key.Y * ObjectChunkSize));

    private IEnumerable<ObjectChunk> VisibleObjectData => _streamingObjects
        ? _visibleObjectChunks.Where(pair => _wantedObjectChunks.Contains(pair.Key))
            .OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X).Select(pair => pair.Value)
        : [];
    private IEnumerable<(Aabb Bounds, Pos Anchor, int Color)> SceneBoxes =>
        _sceneBoxes.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Boxes));
    private IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> SceneRoofFaces =>
        _sceneRoofFaces.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Faces));
    private IEnumerable<(Transform3D Transform, Pos Anchor)> SceneHighlights =>
        _sceneHighlights.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Highlights));
    private IEnumerable<(Aabb Bounds, Pos Anchor, int Color)> SceneYards =>
        _sceneYardBoxes.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Yards));
    private IEnumerable<(Aabb Bounds, Pos Anchor, int Color)> ScenePickingBoxes =>
        _scenePickBoxes.Concat(_sceneYardBoxes).Concat(VisibleObjectData.SelectMany(chunk =>
            chunk.Geometry.Picks.Concat(chunk.Geometry.Yards)));

    private void RebuildStreamedObjects(GameSession session)
    {
        if (!ReferenceEquals(_streamMap, session.Game.Map))
        {
            ClearObjectStreaming();
            _streamMap = session.Game.Map;
        }
        _streamingObjects = true;
        DrawFootpaths(session);
        DrawCivilianFootbridges(session.Game.Map);
        DrawDefenseModelPreview();
        DrawWallConstructionPreview();
        DrawDepthStudySubjects();
        BuildStructurePicking();
        FlushBatches(_world);
        RequestObjectChunks(session);
        UpdateObjectStatistics();
        _staticSceneKey = StaticArtworkKey(session.Game.Map);
    }

    private void RefreshPublishedObjects()
    {
        UpdateObjectStatistics();
        _highlightGeometryVersion = -1;
        if (_streamSession is { } session)
        {
            RebuildIndicators(session);
            SyncSelectionHighlight(session);
        }
        if (GetParent() is Control parent) parent.QueueRedraw();
    }

    private void UpdateObjectStatistics()
    {
        _builtCount = VisibleObjectData.Sum(chunk => chunk.Buildings);
        BuildingsDrawn = _builtCount;
        DoorApproachTrianglesDrawn = VisibleObjectData.Sum(chunk => chunk.DoorTriangles);
        ZonePlotsDrawn = VisibleObjectData.Sum(chunk => chunk.ZonePlots);
        ZoneTrianglesChecked = VisibleObjectData.Sum(chunk => chunk.ZoneTriangles);
        ZoneRoadOverlapArea = VisibleObjectData.Sum(chunk => chunk.RoadOverlap);
        ZoneFootpathOverlapArea = VisibleObjectData.Sum(chunk => chunk.FootpathOverlap);
        MaximumZoneTriangleRoadOverlapArea = VisibleObjectData.Select(chunk => chunk.MaximumRoadOverlap).DefaultIfEmpty().Max();
        MaximumZoneTriangleFootpathOverlapArea = VisibleObjectData.Select(chunk => chunk.MaximumFootpathOverlap).DefaultIfEmpty().Max();
        foreach (var chunk in VisibleObjectData) _sceneTrees.UnionWith(chunk.Geometry.Trees);
    }

    private async Task AwaitObjectStreaming()
    {
        if (ProcessMode == ProcessModeEnum.Disabled) return;
        while (ObjectStreamingPending && ProcessMode != ProcessModeEnum.Disabled)
        {
            ThrowObjectStreamingFailure();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        ThrowObjectStreamingFailure();
    }

    internal async Task AwaitObjectPrefetch()
    {
        while ((_objectPreparation is not null && _wantedObjectChunks.Contains(_preparingObject!.Key) ||
            _objectEncodings.Any(work => _wantedObjectChunks.Contains(work.Chunk.Key)) ||
            _objectRequests.Any(key => _wantedObjectChunks.Contains(key)) || _uploadingObject is not null) &&
            ProcessMode != ProcessModeEnum.Disabled)
        {
            ThrowObjectStreamingFailure();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        ThrowObjectStreamingFailure();
    }

    private void CompleteObjectStreaming()
    {
        while (ObjectStreamingPending)
        {
            ThrowObjectStreamingFailure();
            AdvanceObjectChunks();
        }
    }

    private void ThrowObjectStreamingFailure()
    {
        if (_objectStreamingFailure is { } failure)
            throw new InvalidOperationException("Map object preparation failed; see the Godot log.", failure);
    }

    private ulong ObjectStamp(GameSession session, Pos key)
    {
        var map = session.Game.Map;
        var bounds = ObjectBounds(key, map);
        ulong hash = 14695981039346656037;
        void Mix(int value) => hash = unchecked((hash ^ (uint)value) * 1099511628211);
        void MixGround(int x, int y) => MixGroundDependency(ref hash, map, x, y);
        Mix(_objectCacheEpoch);
        Mix(_overviewLodActive ? 1 : 0);
        Mix((int)TerrainTreatment);
        Mix((int)DepthTreatment);
        Mix((MeasureZoneRoadOverlap ? 1 : 0) | (MeasureZoneFootpathOverlap ? 2 : 0));
        Mix(session.AreaOverlaysVisible ? 1 : 0);
        if (session.AreaOverlaysVisible)
        {
            Mix((int)session.Overlay);
            if (session.Overlay != MapOverlay.Off) Mix(session.Game.AnalysisVersion);
        }
        for (int y = Math.Max(0, bounds.Y - 4); y <= Math.Min(map.Height - 1, bounds.Bottom + 4); y++)
            for (int x = Math.Max(0, bounds.X - 4); x <= Math.Min(map.Width - 1, bounds.Right + 4); x++)
            {
                MixGround(x, y);
                Mix(map.FeatureAt(x, y)?.Id ?? 0);
                Mix((int)map.ZoneAt(x, y));
                if (session.AreaOverlaysVisible)
                {
                    Mix(map.PathWearAt(x, y));
                    Mix(_yardCells.Contains(new(x, y)) ? 1 : 0);
                }
                Mix(map.BuildingAt(x, y)?.Id ?? 0);
                if (map.BuildingAt(x, y) is not null && map.IsBuildingAnchor(map.Index(x, y)))
                    Mix(map.BuildingRotations.GetValueOrDefault(map.Index(x, y)));
            }
        IndexObjectOwners(session);
        foreach (var area in _ownedBuildings.GetValueOrDefault(key) ?? [])
            {
                int owner = map.Index(area.X, area.Y);
                Mix(owner); Mix(area.Width); Mix(area.Height);
                Mix(map.BuildingRotations.GetValueOrDefault(owner));
                foreach (var cell in area.Cells())
                {
                    MixGround(cell.X, cell.Y);
                }
                if (_houseYards.TryGetValue(new(area.X, area.Y), out var yard))
                {
                    Mix(yard.Cell.X); Mix(yard.Cell.Y);
                    Mix(yard.Direction.X); Mix(yard.Direction.Y);
                    MixGround(yard.Cell.X, yard.Cell.Y);
                    Mix(map.FeatureAt(yard.Cell.X, yard.Cell.Y)?.Id ?? 0);
                    Mix((int)map.ZoneAt(yard.Cell.X, yard.Cell.Y));
                }
            }
        return hash;
    }

    private void RequestObjectChunks(GameSession session)
    {
        _streamSession = session;
        IndexObjectOwners(session);
        var warmingVersion = ObjectVersionFor(session);
        if (_warmingVersion != warmingVersion)
        {
            _warmingVersion = warmingVersion;
            _warmingVisited.Clear();
            _warmingCursor = 0;
        }
        _requiredObjectChunks.Clear();
        _wantedObjectChunks.Clear();
        var map = session.Game.Map;
        var visible = _cameraVisibleView;
        for (int y = visible.Y / ObjectChunkSize; y <= visible.Bottom / ObjectChunkSize; y++)
            for (int x = visible.X / ObjectChunkSize; x <= visible.Right / ObjectChunkSize; x++)
                _requiredObjectChunks.Add(new(x, y));
        for (int y = _view.Y / ObjectChunkSize; y <= _view.Bottom / ObjectChunkSize; y++)
            for (int x = _view.X / ObjectChunkSize; x <= _view.Right / ObjectChunkSize; x++)
                _wantedObjectChunks.Add(new(x, y));
        foreach (var area in map.BuildingFootprints.Values)
        {
            if (area.Right + 4 >= visible.X && area.X - 4 <= visible.Right &&
                area.Bottom + 4 >= visible.Y && area.Y - 4 <= visible.Bottom)
                _requiredObjectChunks.Add(ObjectKey(new(area.X, area.Y)));
            if (area.Right + 4 >= _view.X && area.X - 4 <= _view.Right &&
                area.Bottom + 4 >= _view.Y && area.Y - 4 <= _view.Bottom)
                _wantedObjectChunks.Add(ObjectKey(new(area.X, area.Y)));
        }
        _wantedObjectChunks.UnionWith(_requiredObjectChunks);
        _usefulResidencyDirty = true;
        if (_preparingObject is { } preparation &&
            !(MinimapJumpPending ? _requiredObjectChunks : _wantedObjectChunks).Contains(preparation.Key))
        {
            _objectPreparation?.Dispose();
            DisposeObjectGeometry(preparation);
            _objectPreparation = null;
            _preparingObject = null;
            _warmingVisited.Remove(preparation.Key);
        }
        foreach (var queued in _objectRequests)
            if (!_objectChunks.ContainsKey(queued)) _warmingVisited.Remove(queued);
        _objectRequests.Clear();
        var version = ObjectVersionFor(session);
        var center = new Vector2(visible.X + visible.Width / 2f, visible.Y + visible.Height / 2f);
        if (_objectRequestCenter is { } previous && center.DistanceSquaredTo(previous) > .01f)
            _objectTravelDirection = (center - previous).Normalized();
        _objectRequestCenter = center;
        foreach (var key in _wantedObjectChunks.OrderBy(key => _requiredObjectChunks.Contains(key) ? 0 : 1).ThenBy(key =>
        {
            var delta = new Vector2((key.X + .5f) * ObjectChunkSize, (key.Y + .5f) * ObjectChunkSize) - center;
            return delta.Length() - delta.Dot(_objectTravelDirection) * .5f;
        }))
        {
            if (_visibleObjectChunks.TryGetValue(key, out var chunk) && chunk.Validated == version)
            {
                chunk.Used = ++_cacheUse;
                ObjectChunksReused++;
            }
            else if (_preparingObject?.Key != key && _uploadingObject?.Key != key &&
                !_objectEncodings.Any(work => work.Chunk.Key == key)) _objectRequests.Enqueue(key);
        }
        RememberPopulatedDestination();
        TrimGpuResidency();
        RemoveUnusedObjectPools();
        _objectRequestState = (visible.X / ObjectChunkSize, visible.Y / ObjectChunkSize,
            visible.Right / ObjectChunkSize, visible.Bottom / ObjectChunkSize, version);
    }

    private void RefreshObjectRequests(GameSession session)
    {
        var visible = _cameraVisibleView;
        var state = (visible.X / ObjectChunkSize, visible.Y / ObjectChunkSize,
            visible.Right / ObjectChunkSize, visible.Bottom / ObjectChunkSize, ObjectVersionFor(session));
        if (_streamingObjects && _objectRequestState != state) RequestObjectChunks(session);
    }

    private IEnumerator<bool> PrepareObjectChunk(GameSession session, ObjectChunk chunk)
    {
        var map = session.Game.Map;
        var bounds = ObjectBounds(chunk.Key, map);
        foreach (bool step in EnsureObjectGround(session, bounds)) yield return step;
        var chunkPaths = session.AreaOverlaysVisible ? new FootpathSurfaceLayout(map, _roadLayout, bounds) : null;
        foreach (var cell in bounds.Cells())
        {
            if (map.FeatureAt(cell.X, cell.Y) is { } feature &&
                TerrainSprite.NaturalFeatureVisible(session.Game, cell.X, cell.Y))
            {
                foreach (bool step in EnsureObjectGround(session, new(cell.X, cell.Y, 1, 1))) yield return step;
                var at = new Vector3(cell.X + .5f, RenderedGroundHeight(map, cell.X + .5f, cell.Y + .5f), cell.Y + .5f);
                if (feature.Name == "Tree") Tree(at);
                else Rock(at, CellHash.Pick(cell.X, cell.Y, 6), .8f + CellHash.Pick(cell.X, cell.Y, 4) * .1f,
                    CellHash.Pick(cell.X, cell.Y, 4));
            }
            if (session.AreaOverlaysVisible && map.ZoneAt(cell.X, cell.Y) != ZoneType.None &&
                map.BuildingAt(cell.X, cell.Y) is null && !_yardCells.Contains(cell))
            {
                foreach (bool step in EnsureObjectGround(session, new(cell.X, cell.Y, 1, 1))) yield return step;
                int plots = ZonePlotsDrawn, triangles = ZoneTrianglesChecked;
                double roads = ZoneRoadOverlapArea, paths = ZoneFootpathOverlapArea;
                double maximumRoad = MaximumZoneTriangleRoadOverlapArea, maximumPath = MaximumZoneTriangleFootpathOverlapArea;
                MaximumZoneTriangleRoadOverlapArea = MaximumZoneTriangleFootpathOverlapArea = 0;
                var previousPaths = _footpathLayout;
                _footpathLayout = chunkPaths;
                try { ZonePlot(session, cell); }
                finally { _footpathLayout = previousPaths; }
                chunk.ZonePlots += ZonePlotsDrawn - plots;
                chunk.ZoneTriangles += ZoneTrianglesChecked - triangles;
                chunk.RoadOverlap += ZoneRoadOverlapArea - roads;
                chunk.FootpathOverlap += ZoneFootpathOverlapArea - paths;
                chunk.MaximumRoadOverlap = Math.Max(chunk.MaximumRoadOverlap, MaximumZoneTriangleRoadOverlapArea);
                chunk.MaximumFootpathOverlap = Math.Max(chunk.MaximumFootpathOverlap, MaximumZoneTriangleFootpathOverlapArea);
                ZonePlotsDrawn = plots;
                ZoneTrianglesChecked = triangles;
                ZoneRoadOverlapArea = roads;
                ZoneFootpathOverlapArea = paths;
                MaximumZoneTriangleRoadOverlapArea = maximumRoad;
                MaximumZoneTriangleFootpathOverlapArea = maximumPath;
            }
            yield return true;
        }
        foreach (var area in _ownedBuildings.GetValueOrDefault(chunk.Key)?.ToArray() ?? [])
            {
                foreach (bool step in EnsureObjectGround(session, area)) yield return step;
                if (map.BuildingAt(area.X, area.Y) is { } building &&
                    map.BuildingFootprintAt(area.X, area.Y) == area)
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    Building(map, area, building);
                    _buildingEmissionMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                yield return true;
            }

        foreach (var cell in bounds.Cells())
            if (map.BuildingAt(cell.X, cell.Y) is { } type && map.IsBuildingAnchor(map.Index(cell.X, cell.Y)) &&
                !map.BuildingFootprints.ContainsKey(map.Index(cell.X, cell.Y)))
            {
                foreach (bool step in EnsureObjectGround(session, new(cell.X, cell.Y, 1, 1))) yield return step;
                Building(map, new(cell.X, cell.Y, 1, 1), type);
                yield return true;
            }
        foreach (var (owner, yard) in _ownedYards.GetValueOrDefault(chunk.Key)?.ToArray() ?? [])
            {
                foreach (bool step in EnsureObjectGround(session, new(yard.Cell.X, yard.Cell.Y, 1, 1))) yield return step;
                _drawingYard = true;
                try { HouseYard(map, yard, owner); }
                finally { _drawingYard = false; }
                yield return true;
            }
        var groups = chunk.Geometry.Instances.GroupBy(pair =>
        {
            var material = Material(pair.Key, solid: true);
            return material.AlbedoTexture is not null ? pair.Key :
                material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded ? -1 : -2;
        }).ToArray();
        if (chunk.Geometry.Dirt.Count > 0)
            foreach (var (color, vertices) in chunk.Geometry.Dirt)
            {
                if (!chunk.Geometry.Roofs.TryGetValue(color, out var roof)) chunk.Geometry.Roofs.Add(color, roof = []);
                roof.AddRange(vertices);
            }
        _objectEncodings.Enqueue(new(chunk, Task.Run(() => EncodeObjectGeometry(chunk, groups))));
    }

    private static void EncodeObjectGeometry(ObjectChunk chunk,
        IEnumerable<IGrouping<int, KeyValuePair<int, List<Transform3D>>>> groups)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var box in chunk.Geometry.Picks.Concat(chunk.Geometry.Yards))
            RecordPickBox(chunk.Geometry.Structures, box.Anchor, box.Bounds);
        foreach (var group in groups)
        {
            int stride = group.Key < 0 ? 16 : 12;
            var buffer = new float[group.Sum(pair => pair.Value.Count) * stride];
            int offset = 0;
            foreach (var (color, transforms) in group)
            {
                EncodeInstanceBufferInto(buffer, offset, color, transforms, group.Key < 0,
                    chunk.Geometry.InstanceSpans.GetValueOrDefault(color));
                offset += transforms.Count * stride;
            }
            chunk.Buffers.Add(group.Key, buffer);
        }
        chunk.Geometry.Instances.Clear();
        chunk.Geometry.InstanceSpans.Clear();
        foreach (var (color, vertices) in chunk.Geometry.Roofs)
        {
            var points = new Vector3[vertices.Count];
            var normals = new Vector3[vertices.Count];
            var uvs = new Vector2[vertices.Count];
            for (int index = 0; index < vertices.Count; index++)
            {
                points[index] = vertices[index].Point;
                normals[index] = vertices[index].Normal;
                uvs[index] = vertices[index].Uv;
            }
            chunk.RoofBuffers.Add(color, (points, normals, uvs));
        }
        chunk.Geometry.Roofs.Clear();
        chunk.Geometry.Dirt.Clear();
        chunk.EncodingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private IEnumerable<bool> EnsureObjectGround(GameSession session, CellRect area)
    {
        const int span = TerrainSprite.ChunkCells;
        var map = session.Game.Map;
        int left = Math.Max(0, area.X - 4), top = Math.Max(0, area.Y - 4);
        int right = Math.Min(map.Width - 1, area.Right + 4), bottom = Math.Min(map.Height - 1, area.Bottom + 4);
        for (int y = top / span; y <= bottom / span; y++)
            for (int x = left / span; x <= right / span; x++)
            {
                if (CanReleaseGroundReplay && _roadChunks.ContainsKey((x, y)) &&
                    _groundGpuStamps.GetValueOrDefault((x, y)) == GroundStamp(session, (x, y))) continue;
                if (_preparationGround.Contains((x, y)) && _preparedGround.ContainsKey((x, y))) continue;
                while (WaitingForGround((x, y), session))
                {
                    _objectGroundWaiting = true;
                    yield return true;
                }
                _preparationGround.Add((x, y));
                PrepareGroundPacket(session, (x, y));
                yield return true;
            }
    }

    private static void DisposeObjectGeometry(ObjectChunk? chunk)
    {
        if (chunk is null) return;
        chunk.Geometry.Zones.Clear();
    }

    private void AdvanceObjectChunks(double frameMilliseconds = 16.7)
    {
        if (!_streamingObjects || _objectStreamingFailure is not null || _streamSession is not { } session) return;
        var version = ObjectVersionFor(session);
        if (_preparingObject is { } pending && _preparationVersion != version &&
            ObjectStamp(session, pending.Key) != pending.Stamp)
        {
            _objectPreparation?.Dispose();
            DisposeObjectGeometry(pending);
            _objectPreparation = null;
            _preparingObject = null;
            _objectRequests.Enqueue(pending.Key);
        }
        _preparationVersion = version;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double budget = Math.Clamp(50 - Math.Max(0, frameMilliseconds - _lastObjectWork), 4, 20);
        while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < budget)
        {
            if (_uploadingObject is not null)
            {
                AdvanceObjectUpload();
                continue;
            }
            if (_objectEncodings.TryPeek(out var encoding) && encoding.Task.IsCompleted)
            {
                _objectEncodings.Dequeue();
                if (!encoding.Task.IsCompletedSuccessfully)
                {
                    _objectStreamingFailure = encoding.Task.Exception is { } error
                        ? error : new TaskCanceledException(encoding.Task);
                    DisposeObjectGeometry(encoding.Chunk);
                    session.SetMessage("Map object preparation failed; see the Godot log.", MessageKind.Error);
                    GD.PushError($"Map object preparation failed: {_objectStreamingFailure}");
                    return;
                }
                encoding.Task.GetAwaiter().GetResult();
                _objectEncodingMilliseconds += encoding.Chunk.EncodingMilliseconds;
                CompleteObjectChunk(session, encoding.Chunk, version);
                continue;
            }
            if (_objectPreparation is null)
            {
                if (_objectEncodings.Count >= 2) break;
                if ((MinimapJumpPending || NavigationCoolingDown) && _objectRequests.TryPeek(out var queued) &&
                    !_requiredObjectChunks.Contains(queued)) break;
                if (!_objectRequests.TryDequeue(out var key)) break;
                if (!_requiredObjectChunks.Contains(key) && WarmingSuspended) continue;
                ulong stamp = ObjectStamp(session, key);
                if (_visibleObjectChunks.TryGetValue(key, out var visible) && visible.Stamp == stamp)
                {
                    visible.Validated = version;
                    visible.Used = ++_cacheUse;
                    ObjectChunksReused++;
                    continue;
                }
                if (_objectChunks.TryGetValue(key, out var cached) && cached.Stamp == stamp)
                {
                    cached.Validated = version;
                    cached.Used = ++_cacheUse;
                    if (_requiredObjectChunks.Contains(key)) BeginObjectUpload(cached);
                    continue;
                }
                if (_alternateLodChunks.Remove((key, _overviewLodActive), out var alternate))
                {
                    if (alternate.Stamp == stamp)
                    {
                        CompleteObjectChunk(session, alternate, version, rebuilt: false);
                        ObjectChunksReused++;
                        continue;
                    }
                    DisposeObjectGeometry(alternate);
                }
                _preparingObject = new(key, stamp);
                _preparingObject.Overview = _overviewLodActive;
                _preparationGround.Clear();
                _preparationVersion = version;
                _objectPreparation = PrepareObjectChunk(session, _preparingObject);
            }
            var chunk = _preparingObject!;
            var view = _view;
            int buildings = BuildingsDrawn, dirt = DoorApproachTrianglesDrawn;
            _objectCapture = chunk.Geometry;
            _view = new(0, 0, session.Game.Map.Width, session.Game.Map.Height);
            bool more;
            _objectGroundWaiting = false;
            long emitted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { more = _objectPreparation.MoveNext(); }
            finally
            {
                _objectEmissionMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(emitted).TotalMilliseconds;
                chunk.Buildings += BuildingsDrawn - buildings;
                chunk.DoorTriangles += DoorApproachTrianglesDrawn - dirt;
                _objectCapture = null;
                _view = view;
                BuildingsDrawn = buildings;
                DoorApproachTrianglesDrawn = dirt;
            }
            if (more)
            {
                if (_objectGroundWaiting) break;
                continue;
            }
            _objectPreparation.Dispose();
            _objectPreparation = null;
            _preparingObject = null;
        }
        TrimGpuResidency();
        TrimCpuCache();
        AdvanceWholeMapWarming(session);
        if (!ObjectWorkPending && !NavigationCoolingDown && !MinimapJumpPending) CompactIdleObjectPool();
        _lastObjectWork = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ObjectStreamingMilliseconds += _lastObjectWork;
    }

    private void CompleteObjectChunk(GameSession session, ObjectChunk chunk, ObjectVersion version, bool rebuilt = true)
    {
        if (ObjectStamp(session, chunk.Key) != chunk.Stamp)
        {
            DisposeObjectGeometry(chunk);
            if (_wantedObjectChunks.Contains(chunk.Key)) _objectRequests.Enqueue(chunk.Key);
            return;
        }
        if (_objectChunks.TryGetValue(chunk.Key, out var previous) &&
            (!_visibleObjectChunks.TryGetValue(chunk.Key, out var resident) || previous != resident))
            RetainAlternateLod(previous);
        _objectChunks[chunk.Key] = chunk;
        chunk.Validated = version;
        chunk.Used = ++_cacheUse;
        chunk.ManagedBytes = ObjectBytes(chunk);
        chunk.State = ChunkLifecycle.CpuReady;
        if (rebuilt) ObjectChunksBuilt++;
        if (ObjectChunksBuilt % 128 == 0) PrintCacheDiagnostics("progress");
        if (_requiredObjectChunks.Contains(chunk.Key)) BeginObjectUpload(chunk);
    }
}
