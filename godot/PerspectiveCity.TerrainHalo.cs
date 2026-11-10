using Godot;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private int TerrainHaloEnterChunks = 1, TerrainHaloExitChunks = 2;
    private readonly HashSet<(int X, int Y)> _terrainHalo = [];
    private (int Left, int Top, int Right, int Bottom)? _terrainHaloWindow;
    internal int TerrainHaloCount => _terrainHalo.Count;
    private readonly Dictionary<string, int> _terrainRetirements = [];
    internal (int Memory, int Nodes, int Policy, int Invalidated) TerrainRetirementCounts =>
        (_terrainRetirements.GetValueOrDefault("memory") + _terrainRetirements.GetValueOrDefault("cpu-memory"),
            _terrainRetirements.GetValueOrDefault("nodes"),
            _terrainRetirements.GetValueOrDefault("policy"), _terrainRetirements.GetValueOrDefault("invalidation"));
    internal (int Enter, int Exit) TerrainHaloRadii => (TerrainHaloEnterChunks, TerrainHaloExitChunks);

    private void RefreshTerrainHalo()
    {
        if (_groundMap is not { } map) return;
        const int span = TerrainSprite.ChunkCells;
        var window = (Left: _cameraVisibleView.X / span, Top: _cameraVisibleView.Y / span,
            Right: _cameraVisibleView.Right / span, Bottom: _cameraVisibleView.Bottom / span);
        if (_terrainHaloWindow == window) return;
        _terrainHaloWindow = window;
        _terrainHalo.RemoveWhere(key => key.X < window.Left - TerrainHaloExitChunks ||
            key.X > window.Right + TerrainHaloExitChunks || key.Y < window.Top - TerrainHaloExitChunks ||
            key.Y > window.Bottom + TerrainHaloExitChunks);
        int right = (map.Width - 1) / span, bottom = (map.Height - 1) / span;
        for (int y = Math.Max(0, window.Top - TerrainHaloEnterChunks);
            y <= Math.Min(bottom, window.Bottom + TerrainHaloEnterChunks); y++)
            for (int x = Math.Max(0, window.Left - TerrainHaloEnterChunks);
                x <= Math.Min(right, window.Right + TerrainHaloEnterChunks); x++)
                _terrainHalo.Add((x, y));
        _usefulResidencyDirty = true;
    }

    private void MountTerrainHalo()
    {
        if (_cameraGeometryPending || GroundVisiblePending || NavigationCoolingDown || PersistentNavigationCoolingDown ||
            _cameraPathSession is not { } session || _preparedGroundEpoch != GroundVersion(session)) return;
        RefreshTerrainHalo();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var key in _terrainHalo.OrderBy(key => _groundUse.GetValueOrDefault(key)))
        {
            if (_groundNodes.ContainsKey(key) || !_preparedGround.TryGetValue(key, out var packet) ||
                packet.Textures is null) continue;
            long estimated = MeshUploadBytes(packet.Ground) + MeshUploadBytes(packet.Roads) +
                MeshUploadBytes(packet.Banks) + packet.Textures.NativeBytes;
            if (DestinationGpuBytes + estimated > GpuCacheBudgetBytes ||
                !_persistentResidency && SceneObjectCount + 3 >= 480) break;
            MountGroundChunk(session, key.X, key.Y, publish: false);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
        }
    }

    internal async Task VerifyTerrainHaloReuse()
    {
        if (_cameraPathSession is not { } session) throw new InvalidOperationException("Halo check needs a session.");
        var savedView = _cameraVisibleView;
        var originalCamera = (session.CameraX, session.CameraY);
        RefreshTerrainHalo();
        var resident = _groundNodes.Where(pair => _terrainHalo.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (resident.Count == 0) throw new InvalidOperationException("Halo check needs resident terrain.");
        for (int n = 0; n < 8; n++)
        {
            int offset = n % 2 == 0 ? 16 : 0;
            session.ScrollCamera(originalCamera.CameraX + offset - session.CameraX,
                originalCamera.CameraY - session.CameraY);
            Sync(session, DisplaySize);
            await AwaitFootpathMesh();
            RefreshTerrainHalo();
            TrimGpuResidency();
            foreach (var (key, node) in resident)
                if (_terrainHalo.Contains(key) && DestinationGpuBytes <= GpuCacheBudgetBytes &&
                    SceneObjectCount < 480 && (!_groundNodes.TryGetValue(key, out var current) || current != node))
                    throw new InvalidOperationException($"Halo terrain churned at {key} across a chunk boundary.");
        }
        _cameraVisibleView = savedView;
        session.ScrollCamera(originalCamera.CameraX - session.CameraX, originalCamera.CameraY - session.CameraY);
        RefreshTerrainHalo();
        Sync(session, DisplaySize);
        await AwaitFootpathMesh();
        GD.Print($"TERMCITY_TERRAIN_HALO_OK retained={resident.Count} boundary-crossings=8 " +
            $"enter={TerrainHaloEnterChunks} exit={TerrainHaloExitChunks} budgets=unchanged");
    }
}
