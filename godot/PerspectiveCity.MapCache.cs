using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private enum ChunkLifecycle { Preparing, CpuReady, GpuStaging, Published }
    private const long PreparationReserve = 32L * 1024 * 1024;
    internal long CpuCacheBudgetBytes { get; set; } = 256L * 1024 * 1024;
    private readonly Dictionary<(int X, int Y), long> _groundUse = [], _groundBytes = [];
    private readonly Dictionary<(int X, int Y), ulong> _groundStamps = [];
    private long _cacheUse;
    private int _warmingCursor;
    private ObjectVersion? _warmingVersion;
    private readonly HashSet<Pos> _warmingVisited = [];
    private readonly HashSet<(int X, int Y)> _preparationGround = [];
    private readonly Dictionary<Pos, List<CellRect>> _ownedBuildings = [];
    private readonly Dictionary<Pos, List<(Pos Owner, HouseYard Yard)>> _ownedYards = [];
    private ObjectVersion? _ownershipVersion;
    private IReadOnlyDictionary<Pos, HouseYard>? _ownershipYards;
    private System.Diagnostics.Process? _cacheMemoryProcess;
    private long _sampledCacheProcessPeak, _sampledCacheVideoPeak;
    internal int WarmedChunks => _warmingVisited.Count;
    internal int TotalMapChunks => _streamMap is { } map
        ? ((map.Width + 15) / 16) * ((map.Height + 15) / 16) : 0;
    internal long ObjectManagedBytes => _objectChunks.Values.Concat(_alternateLodChunks.Values).Distinct()
        .Sum(chunk => chunk.ManagedBytes);
    internal long GroundManagedBytes => _groundBytes.Values.Sum();
    internal long BuildingManagedBytes => _buildingCache.Values.Sum(BuildingBytes);
    internal long SharedDependencyBytes => (_streamMap is { } map ? (long)map.Width * map.Height * 4 : 0) +
        _ownedBuildings.Values.Sum(areas => 64L + areas.Capacity * 16L) +
        _ownedYards.Values.Sum(yards => 64L + yards.Capacity * 48L) +
        _houseYards.Count * 160L + (_roadLayout?.Segments.Count ?? 0) * 512L +
        (_roadSurfaces?.EstimatedManagedBytes ?? 0);
    internal long CpuCacheBytes => ObjectManagedBytes + GroundManagedBytes + BuildingManagedBytes +
        SharedDependencyBytes + ObjectResidentBufferBytes + IndependentTerrainPixelBytes + GroundPageManagedBytes +
        NativeGroundMetadataBytes + ObjectPoolMetadataBytes + PathManagedBytes;
    internal long CpuCacheOverageBytes => Math.Max(0, CpuCacheBytes - CpuCacheBudgetBytes);
    private static bool CacheMemoryPressure => GC.GetGCMemoryInfo() is { HighMemoryLoadThresholdBytes: > 0 } memory &&
        memory.MemoryLoadBytes > memory.HighMemoryLoadThresholdBytes * .85;
    private long CpuRetentionTargetBytes => CacheMemoryPressure ? CpuCacheBudgetBytes * 3 / 4 : CpuCacheBudgetBytes;
    private long GpuRetentionTargetBytes => CacheMemoryPressure ? GpuCacheBudgetBytes * 3 / 4 : GpuCacheBudgetBytes;
    internal bool WarmingSuspended => CpuCacheBytes + PreparationReserve >= CpuRetentionTargetBytes || CacheMemoryPressure;
    internal long EstimatedGpuBytes => ObjectResidentBufferBytes + PathNativeBytes +
        (_terrainAtlas is null ? 0 : WorldTerrainTexture.AtlasWidth * WorldTerrainTexture.AtlasHeight * 4) +
        _visibleObjectChunks.Values.Sum(chunk => chunk.RoofBuffers.Values.Sum(buffer => (long)buffer.Points.Length * 32) +
            chunk.Geometry.Zones.Values.Sum(vertices => (long)vertices.Count * 32)) +
        _roadChunks.Keys.Sum(key => _groundBytes.GetValueOrDefault(key)) +
        _groundMaterials.Values.Sum(material =>
            (long)material.Texture.GetWidth() * material.Texture.GetHeight() * 4 +
            (long)material.Variation.GetWidth() * material.Variation.GetHeight() +
            (long)material.Flow.GetWidth() * material.Flow.GetHeight() * 16 +
            (long)material.Shore.GetWidth() * material.Shore.GetHeight() * 16);
    internal long EstimatedInFlightBytes => (_cameraGroundWork?.SnapshotBytes ?? _groundBatchContext?.Bytes ?? 0) +
        (_cameraGroundWork?.Completed.Sum(item => GroundBytes(item.Chunk)) ?? 0) +
        (_preparingObject is null && _objectEncodings.Count == 0 ? 0 : PreparationReserve / 2);

    private static RoadSurfaceMeshes.MeshData ZonePacket(List<SurfaceVertex> vertices) =>
        new(vertices.Select(vertex => vertex.Point).ToArray(),
            Enumerable.Repeat(Colors.White, vertices.Count).ToArray(),
            vertices.Select(vertex => vertex.Uv).ToArray(), vertices.Select(vertex => vertex.Normal).ToArray());

    private static long BuildingBytes(CachedBuilding building) => 512 +
        building.Batches.Values.Sum(values => 32L + values.Capacity * 48L) +
        building.InstanceBuffers.Values.Sum(values => 24L + values.LongLength * 4) +
        building.Roof.Capacity * 36L + building.RoofGroups.Values.Sum(values => 24L + values.LongLength * 36) +
        building.Dirt.Capacity * 36L + building.Boxes.LongLength * 36 + building.Picks.LongLength * 36 +
        building.Foundations.LongLength * 32 + building.RoofFaces.LongLength * 44 +
        building.Highlights.LongLength * 56 + building.Entrances.LongLength * 20;

    private static long ObjectBytes(ObjectChunk chunk)
    {
        var geometry = chunk.Geometry;
        return 1024 + (chunk.Buffers.Count + chunk.RoofBuffers.Count + geometry.Zones.Count) * 64L +
            geometry.Trees.Count * 64L + chunk.Buffers.Values.Sum(values => 24L + values.LongLength * 4) +
            chunk.RoofBuffers.Values.Sum(values => 72L + values.Points.LongLength * 32) +
            geometry.Zones.Values.Sum(values => 64L + values.Capacity * 36L) +
            (geometry.Boxes.Capacity + geometry.Picks.Capacity + geometry.Yards.Capacity) * 36L +
            geometry.Foundations.Capacity * 32L + geometry.Faces.Capacity * 44L +
            geometry.Highlights.Capacity * 56L + geometry.Entrances.Capacity * 20L +
            geometry.ZoneFaces.Capacity * 44L +
            geometry.Natural.Values.Concat(geometry.Structures.Values).Sum(group => 96L + group.Boxes.Capacity * 24L);
    }

    private static long GroundBytes(RoadSurfaceMeshes.PreparedChunk chunk)
    {
        static long MeshBytes(RoadSurfaceMeshes.MeshData? mesh) => mesh is null ? 0 : 128 +
            mesh.Points.LongLength * 12 + mesh.Colors.LongLength * 16 +
            (mesh.Uvs?.LongLength ?? 0) * 8 + (mesh.Normals?.LongLength ?? 0) * 12 +
            (mesh.Indices?.LongLength ?? 0) * sizeof(int);
        return 512 + (chunk.Textures?.Bytes ?? 0) + MeshBytes(chunk.Ground) + MeshBytes(chunk.Roads) + MeshBytes(chunk.Banks) +
            chunk.Faces.Values.Concat(chunk.LandFaces.Values).Distinct()
                .Sum(values => 56L + values.LongLength * 12) +
            chunk.CutEdges.Capacity * 16L + (chunk.CutCenters.Capacity + chunk.WallSamples.Capacity) * 12L +
            (chunk.ShoreSamples.Capacity + chunk.BridgeSideSamples.Capacity) * 24L;
    }

    private void IndexObjectOwners(GameSession session)
    {
        var version = ObjectVersionFor(session);
        if (_ownershipVersion == version && ReferenceEquals(_ownershipYards, _houseYards)) return;
        _ownershipVersion = version;
        _ownershipYards = _houseYards;
        _ownedBuildings.Clear();
        _ownedYards.Clear();
        foreach (var area in session.Game.Map.BuildingFootprints.Values)
        {
            var key = ObjectKey(new(area.X, area.Y));
            if (!_ownedBuildings.TryGetValue(key, out var areas)) _ownedBuildings[key] = areas = [];
            areas.Add(area);
        }
        foreach (var (owner, yard) in _houseYards)
        {
            var key = ObjectKey(owner);
            if (!_ownedYards.TryGetValue(key, out var yards)) _ownedYards[key] = yards = [];
            yards.Add((owner, yard));
        }
    }

    private Vector3[]? GroundFaces(GameMap map, int x, int y, bool land)
    {
        // Preparation must sample its CPU dependency, never unrelated published/stale terrain.
        if (_objectCapture is not null &&
            _preparedGround.TryGetValue((x / TerrainSprite.ChunkCells, y / TerrainSprite.ChunkCells), out var packet))
            return (land ? packet.LandFaces : packet.Faces).GetValueOrDefault(map.Index(x, y));
        return (land ? _zoneGroundFaces : _groundFaces).GetValueOrDefault(map.Index(x, y));
    }

    private void AdvanceWholeMapWarming(GameSession session)
    {
        if (!WholeMapWarmingEnabled || NavigationCoolingDown || PersistentNavigationCoolingDown || MinimapJumpPending ||
            ObjectStreamingPending || _cameraGeometryPending || _cameraGroundWork is not null ||
            _objectPreparation is not null || _objectEncodings.Count > 0 || _objectRequests.Count > 0 ||
            _uploadingObject is not null || WarmingSuspended || TotalMapChunks == 0) return;
        int columns = (session.Game.Map.Width + ObjectChunkSize - 1) / ObjectChunkSize;
        for (int count = 0; count < TotalMapChunks; count++)
        {
            int index = _warmingCursor++ % TotalMapChunks;
            var key = new Pos(index % columns, index / columns);
            if (!_warmingVisited.Add(key)) continue;
            if (!_objectChunks.TryGetValue(key, out var chunk) || chunk.Validated != ObjectVersionFor(session))
                _objectRequests.Enqueue(key);
            return;
        }
    }

    private void TrimCpuCache()
    {
        TrimPathPages();
        ReleaseResidentGroundReplay();
        long bytes = CpuCacheBytes;
        long target = CpuRetentionTargetBytes;
        if (bytes <= target) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var pair in _alternateLodChunks.OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (bytes <= target) break;
            _alternateLodChunks.Remove(pair.Key);
            DisposeObjectGeometry(pair.Value);
            bytes -= pair.Value.ManagedBytes;
        }
        foreach (var key in _terrainPixels.Keys.Where(key => !_preparedGround.ContainsKey(key) &&
            !GroundPinned(key)).OrderBy(key => UsefulTerrain(key)).ThenBy(key => _groundUse.GetValueOrDefault(key)).ToArray())
        {
            if (bytes <= target) break;
            bytes -= _terrainPixels[key].Bytes;
            _terrainPixels.Remove(key);
        }
        // Building replay is optional: complete object packets already own independent encoded data.
        if (_objectPreparation is null)
        {
            bytes -= BuildingManagedBytes;
            _buildingCache.Clear();
            _roadSurfaces?.ReleasePreparationCache();
            bytes = CpuCacheBytes;
        }
        var candidates = _objectChunks.Where(pair => !_requiredObjectChunks.Contains(pair.Key) &&
                !_visibleObjectChunks.ContainsKey(pair.Key) && _uploadingObject != pair.Value &&
                _preparingObject?.Key != pair.Key)
            .Select(pair => (Object: true, Key: pair.Key, Used: pair.Value.Used, Bytes: pair.Value.ManagedBytes,
                Nearby: _wantedObjectChunks.Contains(pair.Key)))
            .Concat(_preparedGround.Where(pair => !GroundPinned(pair.Key)).Select(pair =>
                (Object: false, Key: new Pos(pair.Key.X, pair.Key.Y), Used: _groundUse.GetValueOrDefault(pair.Key),
                    Bytes: _groundBytes.GetValueOrDefault(pair.Key), Nearby: GroundNearby(pair.Key))))
            .OrderBy(item => item.Nearby)
            .ThenBy(item => TerrainPriorityEnabled && !item.Object && UsefulTerrain((item.Key.X, item.Key.Y)) ? 1 : 0)
            .ThenBy(item => item.Used).ToArray();
        long groundBytes = GroundManagedBytes;
        var protectedGround = new List<((int X, int Y) Key, long Bytes)>();
        foreach (var candidate in candidates)
        {
            if (bytes <= target) break;
            if (candidate.Object)
            {
                DisposeObjectGeometry(_objectChunks[candidate.Key]);
                _objectChunks.Remove(candidate.Key);
            }
            else
            {
                var key = (candidate.Key.X, candidate.Key.Y);
                if (ProtectCpuTerrain(key, groundBytes))
                {
                    protectedGround.Add((key, candidate.Bytes));
                    continue;
                }
                RemovePreparedGround(key);
                _terrainPixels.Remove(key);
                groundBytes -= candidate.Bytes;
            }
            bytes -= candidate.Bytes;
        }
        // Visible dependencies may exceed the target; reservations must not pin discretionary data.
        foreach (var candidate in protectedGround)
        {
            if (bytes <= target) break;
            RemovePreparedGround(candidate.Key);
            _terrainPixels.Remove(candidate.Key);
            bytes -= candidate.Bytes;
        }
        CpuRetirementMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private bool GroundNearby((int X, int Y) key) =>
        _cameraGeometryPending && key.X >= _cameraVisibleView.X / 16 && key.X <= _cameraVisibleView.Right / 16 &&
        key.Y >= _cameraVisibleView.Y / 16 && key.Y <= _cameraVisibleView.Bottom / 16 ||
        key.X >= _view.X / TerrainSprite.ChunkCells && key.X <= _view.Right / TerrainSprite.ChunkCells &&
        key.Y >= _view.Y / TerrainSprite.ChunkCells && key.Y <= _view.Bottom / TerrainSprite.ChunkCells;

    private bool GroundPinned((int X, int Y) key) => !CanReleaseGroundReplay && _roadChunks.ContainsKey(key) || GroundNearby(key) ||
        _cameraGeometryPending && key.X >= _cameraVisibleView.X / TerrainSprite.ChunkCells &&
        key.X <= _cameraVisibleView.Right / TerrainSprite.ChunkCells &&
        key.Y >= _cameraVisibleView.Y / TerrainSprite.ChunkCells &&
        key.Y <= _cameraVisibleView.Bottom / TerrainSprite.ChunkCells ||
        _preparingObject is not null && _preparationGround.Contains(key);

    internal void ValidateRetainedGround()
    {
        if (_streamSession is not { } session) throw new InvalidOperationException("No cache session.");
        foreach (var (key, cached) in _preparedGround)
        {
            var fresh = _roadSurfaces!.Fresh().Prepare(session, key.X, key.Y);
            static bool Same(RoadSurfaceMeshes.MeshData? first, RoadSurfaceMeshes.MeshData? second) =>
                first is null ? second is null : second is not null &&
                first.Points.SequenceEqual(second.Points) && first.Colors.SequenceEqual(second.Colors) &&
                (first.Uvs ?? []).SequenceEqual(second.Uvs ?? []) &&
                (first.Normals ?? []).SequenceEqual(second.Normals ?? []) &&
                (first.Indices ?? []).SequenceEqual(second.Indices ?? []);
            static bool Faces(Dictionary<int, Vector3[]> first, Dictionary<int, Vector3[]> second) =>
                first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var vertices) &&
                    pair.Value.SequenceEqual(vertices));
            if (!Same(cached.Ground, fresh.Ground) || !Same(cached.Roads, fresh.Roads) ||
                !Same(cached.Banks, fresh.Banks) || !Faces(cached.Faces, fresh.Faces) ||
                !Faces(cached.LandFaces, fresh.LandFaces) || !cached.CutEdges.SequenceEqual(fresh.CutEdges) ||
                !cached.CutCenters.SequenceEqual(fresh.CutCenters))
                throw new InvalidOperationException($"Retained ground geometry differs from fresh preparation at {key}.");
            var pixels = GroundTexturePacket.Prepare(session.Game.Map, key.X, key.Y);
            if (cached.Textures is not { } textures || !textures.Terrain.SequenceEqual(pixels.Terrain) ||
                !textures.Variations.SequenceEqual(pixels.Variations) ||
                !textures.Flow.SequenceEqual(pixels.Flow) || !textures.Shore.SequenceEqual(pixels.Shore) ||
                textures.Width != pixels.Width || textures.Height != pixels.Height || textures.RiverFlow != pixels.RiverFlow)
                throw new InvalidOperationException($"Cached terrain/flow/shore pixels differ at {key}.");
        }
        foreach (var chunk in _objectChunks.Values)
            if (chunk.State == ChunkLifecycle.CpuReady && (chunk.RoofNode is not null || chunk.Slots.Count != 0))
                throw new InvalidOperationException("CPU-ready ownership chunks retain native residency.");
        GD.Print("TERMCITY_CACHE_GROUND_EXACT_OK triangles=true picking=true zones=true cpu-ready=managed");
    }

    internal async Task AwaitCachePreparation()
    {
        while (ObjectWorkPending && ProcessMode != ProcessModeEnum.Disabled)
        {
            ThrowObjectStreamingFailure();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        ThrowObjectStreamingFailure();
    }

    internal void TrackCacheMemory()
    {
        _cacheMemoryProcess ??= System.Diagnostics.Process.GetCurrentProcess();
        SampleCacheMemory();
    }

    private void SampleCacheMemory()
    {
        if (_cacheMemoryProcess is not { } process) return;
        process.Refresh();
        _sampledCacheProcessPeak = Math.Max(_sampledCacheProcessPeak, process.WorkingSet64);
        _sampledCacheVideoPeak = Math.Max(_sampledCacheVideoPeak,
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
    }

    internal void PrintCacheDiagnostics(string phase)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GD.Print($"TERMCITY_MAP_CACHE phase={phase} prepared={ObjectCacheCount}/{TotalMapChunks} visited={WarmedChunks} " +
            $"objects-estimate={ObjectManagedBytes} ground-estimate={GroundManagedBytes} buildings-estimate={BuildingManagedBytes} " +
            $"shared-estimate={SharedDependencyBytes} " +
            $"cpu-estimate={CpuCacheBytes} budget={CpuCacheBudgetBytes} pinned-overage={CpuCacheOverageBytes} " +
            $"inflight-reserve={EstimatedInFlightBytes} upload-backing={ObjectResidentBufferBytes} " +
            $"gpu-estimate={EstimatedGpuBytes} driver-video={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed)} " +
            $"destination-gpu={DestinationGpuBytes} destination-gpu-budget={GpuCacheBudgetBytes} destination-gpu-overage={GpuCacheOverageBytes} " +
            $"retained-frame-gpu={CompleteFrameGpuBytes} ground-upload-bytes={GroundUploadBytes} " +
            $"terrain-cpu-target={TerrainCpuTarget} terrain-gpu-target={TerrainGpuTarget} " +
            $"terrain-priority={TerrainPriorityEnabled} terrain-recent-chunks={_terrainDestinations.Count} " +
            $"terrain-pressure-driven={_pressureDrivenTerrain} system-memory-pressure={CacheMemoryPressure} " +
            $"cpu-trim-target={CpuRetentionTargetBytes} native-trim-target={GpuRetentionTargetBytes} " +
            $"terrain-halo-chunks={TerrainHaloCount} " +
            $"terrain-retire-memory={_terrainRetirements.GetValueOrDefault("memory")} " +
            $"terrain-retire-cpu-memory={_terrainRetirements.GetValueOrDefault("cpu-memory")} " +
            $"terrain-retire-nodes={_terrainRetirements.GetValueOrDefault("nodes")} " +
            $"terrain-retire-policy={_terrainRetirements.GetValueOrDefault("policy")} " +
            $"terrain-retire-invalidated={_terrainRetirements.GetValueOrDefault("invalidation")} " +
            $"independent-terrain-pixels={IndependentTerrainPixelBytes} " +
            $"recent-managed={RecentRegionManagedBytes} recent-native-estimate={RecentRegionNativeBytes} " +
            $"active-nodes={ActiveSceneObjectCount} resident-nodes={SceneObjectCount} " +
            $"managed-heap={GC.GetTotalMemory(false)} process-working-set={process.WorkingSet64} " +
            $"process-peak-working-set={process.PeakWorkingSet64} sampled-process-peak={_sampledCacheProcessPeak} " +
            $"sampled-driver-video-peak={_sampledCacheVideoPeak} suspended={WarmingSuspended}");
    }
}
