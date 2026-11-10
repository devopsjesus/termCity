using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private bool _persistentResidency;
    private bool _persistentObjectResidency = true;
    private int _persistentWarmCursor;
    private long _lastPersistentCameraMotion;
    private bool PersistentNavigationCoolingDown => _persistentResidency &&
        System.Diagnostics.Stopwatch.GetElapsedTime(_lastPersistentCameraMotion).TotalSeconds < 1;
    private readonly Dictionary<(int X, int Y), long> _terrainNativeCosts = [];
    private readonly Dictionary<(int X, int Y), long> _nativeGroundMetadata = [];
    private bool CanReleaseGroundReplay => _persistentResidency || _pressureDrivenTerrain && !_groundDrawPagesEnabled;
    private long NativeGroundMetadataBytes =>
        _nativeGroundMetadata.Where(pair => !_preparedGround.ContainsKey(pair.Key)).Sum(pair => pair.Value) +
        (_terrainNativeCosts.Count + _nativeGroundMetadata.Count) * 48L +
        _groundDrawPages.Values.Sum(page => 512L + page.Published.Count * 128L);
    private const long NativeWarmingHeadroom = 32L * 1024 * 1024;

    private void RecordGroundMetadata((int X, int Y) key, RoadSurfaceMeshes.Chunk chunk) =>
        _nativeGroundMetadata[key] = 512L + chunk.Faces.Values.Concat(chunk.LandFaces.Values).Distinct()
            .Sum(faces => 56L + faces.LongLength * 12) + chunk.CutEdges.Capacity * 16L +
            (chunk.CutCenters.Capacity + chunk.WallSamples.Capacity) * 12L +
            (chunk.ShoreSamples.Capacity + chunk.BridgeSideSamples.Capacity) * 24L;

    private void RefreshResidentObjectVisibility()
    {
        if (!_persistentObjectResidency) return;
        var wantedPages = _wantedObjectChunks.Select(key => new Pos(key.X / _objectMaterialPageSpan,
            key.Y / _objectMaterialPageSpan)).ToHashSet();
        foreach (var (key, pool) in _objectPools)
            pool.Node.Visible = wantedPages.Contains(key.Page);
        var roofPages = _wantedObjectChunks.Select(key => new Pos(key.X / 2, key.Y / 2)).ToHashSet();
        foreach (var (key, node) in _objectRoofPages) node.Visible = roofPages.Contains(key);
    }

    private void ResetPersistentResidency()
    {
        _persistentWarmCursor = 0;
        _terrainNativeCosts.Clear();
    }

    private void RecordTerrainNativeCost((int X, int Y) key, RoadSurfaceMeshes.PreparedChunk packet)
    {
        if (!_persistentResidency || packet.Textures is null) return;
        _terrainNativeCosts[key] = MeshUploadBytes(packet.Ground) + MeshUploadBytes(packet.Roads) +
            MeshUploadBytes(packet.Banks) + (packet.Textures?.NativeBytes ?? 0);
    }

    private void AdvancePersistentTerrain()
    {
        ReleaseResidentGroundReplay();
        if (!_persistentResidency || _cameraPathSession is not { } session || _groundMap is null ||
            GroundVisiblePending || _cameraGeometryPending || NavigationCoolingDown || MinimapJumpPending ||
            PersistentNavigationCoolingDown ||
            ObjectStreamingPending || RequiredPathWorkPending ||
            _dirtyGroundPages.Count > 0 || _uploadingObject is not null || _cameraGroundWork is not null) return;
        int columns = (_groundMap.Width + 15) / 16, rows = (_groundMap.Height + 15) / 16;
        var center = new Vector2(_cameraVisibleView.X / 16f, _cameraVisibleView.Y / 16f);
        var available = _preparedGround.Keys.Where(key => !_groundNodes.ContainsKey(key) &&
            _preparedGround[key].Textures is not null)
            .OrderBy(key => new Vector2(key.X, key.Y).DistanceSquaredTo(center));
        foreach (var key in available)
        {
            var packet = _preparedGround[key];
            RecordTerrainNativeCost(key, packet);
            if (DestinationGpuBytes + _terrainNativeCosts[key] + NativeWarmingHeadroom > GpuCacheBudgetBytes) break;
            MountGroundChunk(session, key.X, key.Y, publish: false);
            return;
        }
        for (int n = 0; n < 8; n++)
        {
            int index = _persistentWarmCursor++ % (columns * rows);
            var key = (index % columns, index / columns);
            if (_groundNodes.ContainsKey(key) || _preparedGround.ContainsKey(key)) continue;
            if (WarmingSuspended || DestinationGpuBytes + NativeWarmingHeadroom >= GpuCacheBudgetBytes) return;
            int x = key.Item1 / _groundPageSpan * _groundPageSpan * 16;
            int y = key.Item2 / _groundPageSpan * _groundPageSpan * 16;
            PrepareCameraGround(session, new CellRect(x, y,
                Math.Min(_groundPageSpan * 16, _groundMap.Width - x),
                Math.Min(_groundPageSpan * 16, _groundMap.Height - y)));
            return;
        }
    }

    private void ReleaseResidentGroundReplay()
    {
        if (!CanReleaseGroundReplay) return;
        foreach (var key in _preparedGround.Keys.Where(key => _roadChunks.ContainsKey(key) &&
            (!_groundDrawPagesEnabled || GroundPageHasOwner(key)) && !GroundNearby(key) &&
                (_preparingObject is null || !_preparationGround.Contains(key))).ToArray())
        {
            RemovePreparedGround(key);
            _terrainPixels.Remove(key);
        }
    }

    internal void PrintPersistentResidency(string label)
    {
        if (!_persistentResidency) return;
        int total = _groundMap is { } map ? ((map.Width + 15) / 16) * ((map.Height + 15) / 16) : 0;
        long measured = _terrainNativeCosts.Values.Sum();
        string fit = CpuCacheBytes > CpuCacheBudgetBytes ? "regional-cpu-pressure" :
            measured + ObjectResidentBufferBytes + NativeWarmingHeadroom > GpuCacheBudgetBytes
            ? "regional-budget" : _terrainNativeCosts.Count < total ? "unmeasured-regional" : "whole-map-fits";
        GD.Print($"TERMCITY_PERSISTENT_RESIDENCY phase={label} fit={fit} samples={_terrainNativeCosts.Count}/{total} " +
            $"terrain-native={NativeTerrainBytes} measured-whole-terrain={measured} " +
            $"gpu-target={GpuCacheBudgetBytes} cpu-target={CpuCacheBudgetBytes} " +
            $"upload-headroom={NativeWarmingHeadroom} metadata={NativeGroundMetadataBytes} " +
            $"resident-terrain={_groundNodes.Count} cpu-replay={_preparedGround.Count} " +
            $"whole-native-ready={_groundNodes.Count == total && _dirtyGroundPages.Count == 0} " +
            $"objects-persistent={_persistentObjectResidency} " +
            $"active-nodes={ActiveSceneObjectCount} resident-nodes={SceneObjectCount}");
    }

    internal async Task ReportPersistentWarmCompletion()
    {
        if (!_persistentResidency || _groundMap is not { } map) return;
        int total = ((map.Width + 15) / 16) * ((map.Height + 15) / 16);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (_groundNodes.Count < total || _dirtyGroundPages.Count > 0)
        {
            if (started.Elapsed.TotalSeconds >= 15 || WarmingSuspended ||
                DestinationGpuBytes + NativeWarmingHeadroom >= GpuCacheBudgetBytes) break;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        PrintPersistentResidency("post-route-warm");
    }
}
