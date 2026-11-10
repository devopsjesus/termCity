using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal long GpuCacheBudgetBytes { get; set; } = 128L * 1024 * 1024;
    private readonly Node3D _groundRoot = new();
    private readonly Dictionary<(int X, int Y), Node3D> _groundNodes = [];
    private readonly Dictionary<(int X, int Y), long> _groundGpuBytes = [], _groundGpuUse = [];
    private readonly Dictionary<(int X, int Y), ulong> _groundGpuStamps = [];
    private readonly HashSet<Pos> _recentPopulatedDestination = [];
    internal long DestinationGpuBytes => ObjectResidentBufferBytes + PathNativeBytes + _groundGpuBytes.Values.Sum() + GroundPageRetiringGpuBytes +
        (_terrainAtlas is null ? 0 : WorldTerrainTexture.AtlasWidth * WorldTerrainTexture.AtlasHeight * 4) +
        _visibleObjectChunks.Values.Sum(chunk => chunk.RoofBuffers.Values.Sum(buffer => buffer.Points.LongLength * 32) +
            chunk.Geometry.Zones.Values.Sum(vertices => vertices.Count * 32L));
    internal long GpuCacheOverageBytes => Math.Max(0, DestinationGpuBytes - GpuCacheBudgetBytes);
    internal long CompleteFrameGpuBytes => (long)_lastCompleteFrame.Size.X * _lastCompleteFrame.Size.Y * 4;
    internal int GroundNodesReused { get; private set; }
    internal sealed record NativeDestination(
        IReadOnlyDictionary<Pos, (object Geometry, int Uploads)> Objects,
        IReadOnlyDictionary<(int X, int Y), (object Geometry, object Material)> Ground);

    internal NativeDestination CaptureNativeDestination() => new(
        _requiredObjectChunks.ToDictionary(key => key, key =>
        {
            var chunk = _visibleObjectChunks[key];
            return ((object)chunk, chunk.NativeUploads);
        }),
        _mountedGroundChunks.Where(key => key.X >= _cameraVisibleView.X / 16 &&
            key.X <= _cameraVisibleView.Right / 16 && key.Y >= _cameraVisibleView.Y / 16 &&
            key.Y <= _cameraVisibleView.Bottom / 16).ToDictionary(key => key,
            key => (NativeGroundGeometry(key), (object)_groundMaterials[key])));

    private object NativeGroundGeometry((int X, int Y) key) => _groundDrawPagesEnabled
        ? _groundDrawPages[GroundPageKey(key)].Meshes[0] : _roadChunks[key];

    internal void ValidateNativeDestinationReuse(NativeDestination previous)
    {
        foreach (var (key, cached) in previous.Objects)
            if (!_visibleObjectChunks.TryGetValue(key, out var chunk) ||
                !ReferenceEquals(cached.Geometry, chunk) || cached.Uploads != chunk.NativeUploads)
                throw new InvalidOperationException($"Retained destination object {key} regenerated or reuploaded.");
        foreach (var (key, cached) in previous.Ground)
            if (!_roadChunks.ContainsKey(key) || !ReferenceEquals(cached.Geometry, NativeGroundGeometry(key)) ||
                !_groundMaterials.TryGetValue(key, out var material) || !ReferenceEquals(cached.Material, material))
                throw new InvalidOperationException($"Retained destination ground {key} regenerated or reuploaded.");
    }

    internal void ValidateGpuResidencyBudget()
    {
        if (DestinationGpuBytes <= GpuCacheBudgetBytes) return;
        if (_uploadingObject is not null)
            throw new InvalidOperationException("GPU budget inspection requires completed publication.");
        if (_groundNodes.Keys.Any(key => !GroundNearby(key)) ||
            _visibleObjectChunks.Values.GroupBy(chunk => new Pos(chunk.Key.X / _objectMaterialPageSpan, chunk.Key.Y / _objectMaterialPageSpan))
                .Any(page => !page.Any(chunk => _wantedObjectChunks.Contains(chunk.Key))))
            throw new InvalidOperationException("GPU cache exceeds its byte budget while evictable destinations remain.");
    }
    private static long MeshUploadBytes(RoadSurfaceMeshes.MeshData? mesh) => mesh is null ? 0 :
        mesh.Points.LongLength * 12 + mesh.Colors.LongLength * 16 +
        (mesh.Normals?.LongLength ?? 0) * 12 + (mesh.Uvs?.LongLength ?? 0) * 8 +
        (mesh.Indices?.LongLength ?? 0) * sizeof(int);

    private void RememberPopulatedDestination()
    {
        if (!_requiredObjectChunks.Any(key => _visibleObjectChunks.TryGetValue(key, out var chunk) && chunk.Buildings > 0))
            return;
        _recentPopulatedDestination.Clear();
        _recentPopulatedDestination.UnionWith(_requiredObjectChunks);
    }

    private void RetireGpuGround((int X, int Y) key, string reason = "invalidation", bool retainTextures = false)
    {
        if (_persistentResidency && reason == "invalidation") _terrainNativeCosts.Remove(key);
        if (_groundNodes.ContainsKey(key))
            _terrainRetirements[reason] = _terrainRetirements.GetValueOrDefault(reason) + 1;
        if (_groundNodes.Remove(key, out var node)) { _groundRoot.RemoveChild(node); node.QueueFree(); }
        if (!retainTextures && _groundMaterials.Remove(key, out var material)) material.Dispose();
        if (_groundMeshes.Remove(key, out var mesh)) mesh.Dispose();
        if (_cliffMeshes.Remove(key, out var cliff)) cliff.Dispose();
        if (_roadChunks.Remove(key, out var chunk))
        {
            _nativeGroundMetadata.Remove(key);
            chunk.Roads?.Dispose();
            foreach (var index in chunk.Faces.Keys) _groundFaces.Remove(index);
            foreach (var index in chunk.LandFaces.Keys) _zoneGroundFaces.Remove(index);
        }
        if (retainTextures && _groundMaterials.TryGetValue(key, out var retained))
            _groundGpuBytes[key] = (long)retained.Texture.GetWidth() * retained.Texture.GetHeight() * 4 +
                (long)retained.Variation.GetWidth() * retained.Variation.GetHeight() +
                (long)retained.Flow.GetWidth() * retained.Flow.GetHeight() * 16 +
                (long)retained.Shore.GetWidth() * retained.Shore.GetHeight() * 16;
        else _groundGpuBytes.Remove(key);
        _groundGpuUse.Remove(key);
        _groundGpuStamps.Remove(key);
        _mountedGroundChunks.Remove(key);
        _pendingGroundPublication.Remove(key);
        MarkGroundPageDirty(key);
    }

    private void TrimGpuResidency()
    {
        RefreshRecentRegion();
        RefreshTerrainHalo();
        TrimPathPages();
        if (_persistentResidency || _pressureDrivenTerrain) TrimCpuCache();
        long target = GpuRetentionTargetBytes;
        long cpuTarget = CpuRetentionTargetBytes, cpuBytes = CpuCacheBytes;
        bool cpuPressure = (_persistentResidency || _pressureDrivenTerrain) && cpuBytes > cpuTarget;
        if ((!_usefulResidencyOnly || !_usefulResidencyDirty) && RecentGpuResidencyEnabled &&
            DestinationGpuBytes <= target && (!NativeNodeLimitReached ||
                _persistentObjectResidency && _pressureDrivenTerrain) && !cpuPressure ||
            _uploadingObject is not null) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pages = _visibleObjectChunks.Values.GroupBy(chunk => new Pos(chunk.Key.X / _objectMaterialPageSpan, chunk.Key.Y / _objectMaterialPageSpan))
            .Where(page => !page.Any(chunk => _wantedObjectChunks.Contains(chunk.Key)))
            .Select(page => (Ground: false, Key: page.Key, Used: page.Max(chunk => chunk.Used),
                Populated: page.Any(chunk => _recentPopulatedDestination.Contains(chunk.Key)),
                GroundKeys: Array.Empty<(int X, int Y)>()))
            .Concat(_groundNodes.Keys.Concat(_groundMaterials.Keys).Distinct().Where(key => !GroundNearby(key))
                .GroupBy(key => _groundDrawPagesEnabled && _stableGroundPages ? GroundPageKey(key) : key)
                .Select(group => (Ground: true, Key: new Pos(group.Key.X, group.Key.Y),
                    Used: group.Max(key => _groundGpuUse.GetValueOrDefault(key)),
                    Populated: group.Any(key => _recentPopulatedDestination.Contains(new(key.X, key.Y))),
                    GroundKeys: group.ToArray())))
            .OrderBy(item => item.Populated)
            .ThenBy(item => TerrainPriorityEnabled && item.Ground && item.GroundKeys.Any(UsefulTerrain) ? 1 : 0)
            .ThenBy(item => item.Used).ToArray();
        var protectedGround = new List<(int X, int Y)[]>();
        foreach (var page in pages)
        {
            bool useful = page.Ground ? page.GroundKeys.Any(key =>
                UsefulNativeRegion(new(key.X, key.Y)) || _terrainHalo.Contains(key)) :
                _visibleObjectChunks.Values.Any(chunk => UsefulNativeRegion(chunk.Key) &&
                    chunk.Key.X / _objectMaterialPageSpan == page.Key.X && chunk.Key.Y / _objectMaterialPageSpan == page.Key.Y);
            bool protect = page.Ground && page.GroundKeys.Any(ProtectGpuTerrain);
            if (page.Ground && _pressureDrivenTerrain && RecentGpuResidencyEnabled &&
                DestinationGpuBytes <= target && cpuBytes <= cpuTarget) continue;
            if (!page.Ground && _persistentObjectResidency && RecentGpuResidencyEnabled &&
                DestinationGpuBytes <= target && cpuBytes <= cpuTarget) continue;
            if (RecentGpuResidencyEnabled && DestinationGpuBytes <= target && !NativeNodeLimitReached &&
                (!cpuPressure || cpuBytes <= cpuTarget) &&
                (_persistentResidency && page.Ground ||
                !_usefulResidencyOnly || useful || protect)) continue;
            if (protect)
            {
                protectedGround.Add(page.GroundKeys);
                continue;
            }
            if (page.Ground) RetireGroundPage(page.GroundKeys,
                DestinationGpuBytes > target ? "memory" :
                    cpuPressure && cpuBytes > cpuTarget ? "cpu-memory" :
                    NativeNodeLimitReached ? "nodes" : "policy");
            else
            {
                var chunks = _visibleObjectChunks.Values.Where(chunk =>
                    chunk.Key.X / _objectMaterialPageSpan == page.Key.X &&
                    chunk.Key.Y / _objectMaterialPageSpan == page.Key.Y).ToArray();
                var pools = chunks.SelectMany(chunk => chunk.Slots.Keys).Distinct().ToArray();
                foreach (var chunk in chunks) RetireObjectChunk(chunk.Key, upload: false);
                RemoveUnusedObjectPools();
                foreach (var key in pools)
                {
                    if (!_objectPools.TryGetValue(key, out var pool)) continue;
                    pool.Mesh.Buffer = pool.Data;
                    ObjectWholeBufferWrites++;
                    ObjectUploadBytes += pool.Data.LongLength * sizeof(float);
                }
            }
            if (cpuPressure) cpuBytes = CpuCacheBytes;
        }
        _usefulResidencyDirty = false;
        foreach (var keys in protectedGround)
        {
            if (DestinationGpuBytes <= target && (_pressureDrivenTerrain || !NativeNodeLimitReached) &&
                (!cpuPressure || cpuBytes <= cpuTarget)) break;
            RetireGroundPage(keys, DestinationGpuBytes > target ? "memory" :
                cpuPressure && cpuBytes > cpuTarget ? "cpu-memory" : "nodes");
            if (cpuPressure) cpuBytes = CpuCacheBytes;
        }
        if (cpuPressure) TrimCpuCache();
        GpuRetirementMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        void RetireGroundPage((int X, int Y)[] keys, string reason)
        {
            foreach (var key in keys) RetireGpuGround(key, reason);
            if (!_groundDrawPagesEnabled || !_stableGroundPages) return;
            var page = GroundPageKey(keys[0]);
            RebuildGroundDrawPage(page);
            _dirtyGroundPages.Remove(page);
        }
    }

    private void ReconcileGroundEpoch(GameSession session)
    {
        if (!ReferenceEquals(_groundMap, session.Game.Map)) ClearTerrainRetention();
        foreach (var key in _groundNodes.Keys.Concat(_roadChunks.Keys).Concat(_groundMaterials.Keys).Distinct().ToArray())
            if (!ReferenceEquals(_groundMap, session.Game.Map) ||
                _groundGpuStamps.GetValueOrDefault(key) != GroundStamp(session, key))
                RetireGpuGround(key, retainTextures: CanReleaseGroundReplay && GroundNearby(key) &&
                    ReferenceEquals(_groundMap, session.Game.Map) && _groundVersion == session.Game.Map.TerrainVersion);
    }
}
