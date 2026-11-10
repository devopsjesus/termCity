using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    // Reservations are shares of the existing budgets, not additional allocations.
    private const long TerrainCpuReservation = 128L * 1024 * 1024;
    private const long TerrainGpuReservation = 64L * 1024 * 1024;
    private readonly HashSet<(int X, int Y)> _terrainDestinations = [];
    private readonly Queue<HashSet<(int X, int Y)>> _recentTerrainViews = [];
    private readonly Dictionary<(int X, int Y), GroundTexturePacket> _terrainPixels = [];
    private TermCity.Core.World.GameMap? _terrainPixelMap;
    private int _terrainPixelVersion = -1;
    internal bool TerrainPriorityEnabled { get; private set; } = true;
    internal long TerrainCpuTarget => Math.Min(TerrainCpuReservation, CpuCacheBudgetBytes / 2);
    internal long TerrainGpuTarget => Math.Min(TerrainGpuReservation, GpuCacheBudgetBytes / 2);
    private long NativeTerrainBytes => _groundGpuBytes.Values.Sum();
    private long IndependentTerrainPixelBytes => _terrainPixels.Where(pair =>
        !_preparedGround.TryGetValue(pair.Key, out var packet) || !ReferenceEquals(packet.Textures, pair.Value))
        .Sum(pair => pair.Value.Bytes);

    private void ValidateTerrainPixels(TermCity.Core.World.GameMap map)
    {
        if (ReferenceEquals(_terrainPixelMap, map) && _terrainPixelVersion == map.TerrainVersion) return;
        if (_persistentResidency) ResetPersistentResidency();
        _terrainPixels.Clear();
        _terrainPixelMap = map;
        _terrainPixelVersion = map.TerrainVersion;
    }

    private void RememberTerrainDestination()
    {
        if (_groundMap is null) return;
        var keys = new HashSet<(int X, int Y)>();
        for (int y = _cameraVisibleView.Y / 16; y <= _cameraVisibleView.Bottom / 16; y++)
            for (int x = _cameraVisibleView.X / 16; x <= _cameraVisibleView.Right / 16; x++)
                keys.Add((x, y));
        if (_recentTerrainViews.LastOrDefault() is { } newest && newest.SetEquals(keys)) return;
        _recentTerrainViews.Enqueue(keys);
        while (_recentTerrainViews.Count > 3) _recentTerrainViews.Dequeue();
        _terrainDestinations.Clear();
        foreach (var view in _recentTerrainViews) _terrainDestinations.UnionWith(view);
    }

    private bool UsefulTerrain((int X, int Y) key) => GroundNearby(key) || _terrainDestinations.Contains(key) ||
        _terrainHalo.Contains(key) || InRecentRegion(new(key.X, key.Y));

    private bool ProtectCpuTerrain((int X, int Y) key, long remainingGroundBytes) =>
        TerrainPriorityEnabled && UsefulTerrain(key) &&
        remainingGroundBytes <= TerrainCpuTarget;

    private bool ProtectGpuTerrain((int X, int Y) key) =>
        TerrainPriorityEnabled && UsefulTerrain(key) && NativeTerrainBytes <= TerrainGpuTarget &&
        !NativeNodeLimitReached;

    private void ClearTerrainRetention()
    {
        _terrainDestinations.Clear();
        _recentTerrainViews.Clear();
        _terrainPixels.Clear();
        _terrainPixelMap = null;
        _terrainPixelVersion = -1;
        _terrainHalo.Clear();
        _terrainHaloWindow = null;
        _terrainRetirements.Clear();
        _recentRouteBounds = null;
        ResetPersistentResidency();
    }

    internal void ValidateTerrainRetentionBudget()
    {
        if (CpuCacheBytes > CpuCacheBudgetBytes &&
            _preparedGround.Keys.Any(key => !GroundPinned(key)))
            throw new InvalidOperationException("CPU terrain reservation prevented eviction above the total budget.");
        ValidateGpuResidencyBudget();
    }

    internal async Task ValidateTerrainPixelsSurviveRoadEdit(TermCity.Core.Session.GameSession session)
    {
        var map = session.Game.Map;
        var candidates = _preparedGround.Keys.SelectMany(key =>
            new CellRect(key.X * 16, key.Y * 16,
                Math.Min(16, map.Width - key.X * 16), Math.Min(16, map.Height - key.Y * 16)).Cells()
                .Where(cell => !map.HasRoad(cell.X, cell.Y) && map.TerrainAt(cell.X, cell.Y).Buildable)
                .Select(cell => (Key: key, Cell: cell)));
        var chosen = candidates.First();
        var key = chosen.Key;
        var pixels = _preparedGround[key].Textures ??
            throw new InvalidOperationException("Terrain retention fixture needs prepared pixels.");
        var cell = chosen.Cell;
        map.SetRoad(cell.X, cell.Y, true);
        session.Game.Touch();
        Sync(session, DisplaySize);
        await AwaitFootpathMesh();
        PrepareGroundPacket(session, key);
        if (!_terrainPixels.TryGetValue(key, out var after) || !ReferenceEquals(pixels, after))
            throw new InvalidOperationException("A road edit discarded unchanged terrain/flow/shore pixels.");
        map.SetRoad(cell.X, cell.Y, false);
        session.Game.Touch();
        Sync(session, DisplaySize);
        await AwaitFootpathMesh();
        Godot.GD.Print("TERMCITY_TERRAIN_RETENTION_EDIT_OK road-geometry=invalidated base-pixels=retained");
    }
}
