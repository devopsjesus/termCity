using System.Collections.Concurrent;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly record struct GroundEpoch(GameMap Map, int Terrain, int Roads,
        MapOverlay Overlay, int Analysis, int TerrainStyle, int DepthStyle, bool RoadRendering);
    private sealed record GroundResult(double Milliseconds);
    private sealed record GroundWork(GroundEpoch Epoch, IReadOnlySet<(int X, int Y)> Requested,
        ConcurrentQueue<((int X, int Y) Key, RoadSurfaceMeshes.PreparedChunk Chunk)> Completed,
        Task<GroundResult> Task, CancellationTokenSource Cancellation, long SnapshotBytes, RoadSurfaceMeshes Surfaces);
    private GroundWork? _cameraGroundWork;
    private sealed record GroundBatchContext(GroundEpoch Epoch, CellRect View, GameSession Session,
        RoadSurfaceMeshes Surfaces, long Bytes);
    private GroundBatchContext? _groundBatchContext;
    private CellRect? _stagingGroundView;
    private GroundEpoch? _preparedGroundEpoch;
    private readonly Dictionary<(int X, int Y), RoadSurfaceMeshes.PreparedChunk> _preparedGround = [];
    private bool _cameraGroundFailed;
    internal double GroundWorkerMilliseconds { get; private set; }
    internal double TotalGroundWorkerMilliseconds { get; private set; }
    internal int GroundTexturePacketsBuilt { get; private set; }
    internal int GroundChunksUploaded { get; private set; }
    internal long GroundUploadBytes { get; private set; }
    internal int GroundPacketsReused { get; private set; }

    private GroundEpoch GroundVersion(GameSession session) => new(session.Game.Map,
        session.Game.Map.TerrainVersion, _roads.Revision,
        session.Overlay, session.Overlay == MapOverlay.Off ? 0 : session.Game.AnalysisVersion,
        (int)TerrainTreatment, (int)DepthTreatment, RoadRenderingEnabled);

    private void CancelGroundPreparation()
    {
        _groundBatchContext = null;
        _stagingGroundView = null;
        if (_cameraGroundWork is not { } work) return;
        work.Cancellation.Cancel();
        try { work.Task.GetAwaiter().GetResult(); }
        finally
        {
            work.Cancellation.Dispose();
            _cameraGroundWork = null;
        }
    }

    private void RemovePreparedGround((int X, int Y) key)
    {
        _preparedGround.Remove(key);
        _groundUse.Remove(key);
        _groundBytes.Remove(key);
        _groundStamps.Remove(key);
    }

    private static void HashGroundValue(ref ulong hash, int value) =>
        hash = unchecked((hash ^ (uint)value) * 1099511628211);

    private void MixGroundDependency(ref ulong hash, GameMap map, int x, int y)
    {
        HashGroundValue(ref hash, map.TerrainAt(x, y).Id);
        HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(TerrainSprite.Elevation(map, x, y)));
        if (_roadLayout is not { } roads) return;
        foreach (var road in roads.AtCell(x, y))
        {
            HashGroundValue(ref hash, road.Type.Id);
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.A.X));
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.A.Y));
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.B.X));
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.B.Y));
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.HeightA));
            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(road.HeightB));
        }
    }

    private ulong GroundStamp(GameSession session, (int X, int Y) key)
    {
        var map = session.Game.Map;
        ulong hash = 14695981039346656037;
        void Mix(int value) => hash = unchecked((hash ^ (uint)value) * 1099511628211);
        Mix((int)TerrainTreatment); Mix((int)DepthTreatment); Mix((int)session.Overlay);
        Mix(RoadRenderingEnabled ? 1 : 0);
        if (session.Overlay != MapOverlay.Off) Mix(session.Game.AnalysisVersion);
        const int span = TerrainSprite.ChunkCells;
        for (int y = Math.Max(0, key.Y * span - 4); y < Math.Min(map.Height, (key.Y + 1) * span + 4); y++)
            for (int x = Math.Max(0, key.X * span - 4); x < Math.Min(map.Width, (key.X + 1) * span + 4); x++)
                MixGroundDependency(ref hash, map, x, y);
        var flowField = TerrainSprite.Flow(map);
        for (int y = key.Y * span - 1; y <= (key.Y + 1) * span; y++)
            for (int x = key.X * span - 1; x <= (key.X + 1) * span; x++)
            {
                Mix(ShorelineLayout.CoastalCellDistance(map, x, y));
                if (!map.InBounds(x, y) || map.TerrainAt(x, y).Name != TermCity.Core.Terrain.DefaultTerrains.WaterName)
                    continue;
                var flow = flowField.At(x, y);
                var track = RiverRippleLayout.At(map, flowField, x, y);
                Mix(BitConverter.SingleToInt32Bits(flow.Direction.X));
                Mix(BitConverter.SingleToInt32Bits(flow.Direction.Y));
                Mix(flow.River ? 1 : 0);
                Mix(BitConverter.SingleToInt32Bits(flow.Distance));
                Mix(BitConverter.SingleToInt32Bits(track.Incoming.X));
                Mix(BitConverter.SingleToInt32Bits(track.Incoming.Y));
            }
        return hash;
    }

    private void ValidatePreparedGround(GameSession session)
    {
        ValidateTerrainPixels(session.Game.Map);
        var epoch = GroundVersion(session);
        if (_preparedGroundEpoch == epoch) return;
        if (_preparedGroundEpoch is { } previous && !ReferenceEquals(previous.Map, epoch.Map))
        {
            CancelGroundPreparation();
            foreach (var key in _preparedGround.Keys.ToArray()) RemovePreparedGround(key);
        }
        else
            foreach (var key in _preparedGround.Keys.ToArray())
                if (_groundStamps.GetValueOrDefault(key) != GroundStamp(session, key)) RemovePreparedGround(key);
        _preparedGroundEpoch = epoch;
    }

    private void RetainGround(GameSession session, (int X, int Y) key, RoadSurfaceMeshes.PreparedChunk packet)
    {
        _groundPreparationMilliseconds += packet.PreparationMilliseconds;
        _borderPreparationMilliseconds += packet.RoadBorderMilliseconds;
        _texturePreparationMilliseconds += packet.TexturePreparationMilliseconds;
        if (packet.Textures is not null) GroundTexturePacketsBuilt++;
        _preparedGround[key] = packet;
        RecordTerrainNativeCost(key, packet);
        if (TerrainPriorityEnabled && packet.Textures is { } textures) _terrainPixels[key] = textures;
        _groundBytes[key] = GroundBytes(packet);
        _groundStamps[key] = GroundStamp(session, key);
        _groundUse[key] = ++_cacheUse;
    }

    private RoadSurfaceMeshes.PreparedChunk PrepareGroundPacket(GameSession session, (int X, int Y) key)
    {
        ValidatePreparedGround(session);
        if (!_preparedGround.TryGetValue(key, out var packet))
        {
            packet = PrepareGroundData(session, _roadSurfaces!, key.X, key.Y,
                GroundTextureCachingEnabled && !(_lazyOffscreenTextures && _objectCapture is not null),
                TerrainPriorityEnabled ? _terrainPixels.GetValueOrDefault(key) : null);
            RetainGround(session, key, packet);
        }
        else GroundPacketsReused++;
        _groundUse[key] = ++_cacheUse;
        return packet;
    }

    private static RoadSurfaceMeshes.PreparedChunk PrepareGroundData(GameSession session,
        RoadSurfaceMeshes surfaces, int x, int y, bool textures, GroundTexturePacket? retainedPixels = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var packet = surfaces.Prepare(session, x, y);
        double ground = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pixels = textures ? retainedPixels ?? GroundTexturePacket.Prepare(session.Game.Map, x, y) : null;
        return packet with { Textures = pixels, PreparationMilliseconds = ground,
            TexturePreparationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    }

    private static RoadSurfaceMeshes.PreparedChunk PrepareGroundTextures(GameSession session,
        RoadSurfaceMeshes.PreparedChunk retained, int x, int y, bool textures)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pixels = textures ? GroundTexturePacket.Prepare(session.Game.Map, x, y) : null;
        return retained with { Textures = pixels, PreparationMilliseconds = 0, RoadBorderMilliseconds = 0,
            TexturePreparationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    }

    private bool PrepareCameraGround(GameSession session, CellRect view)
    {
        PollCameraGround(session);
        if (_cameraGroundFailed) return true;
        ValidatePreparedGround(session);
        var epoch = GroundVersion(session);
        const int span = TerrainSprite.ChunkCells;
        var missing = new List<(int X, int Y)>();
        for (int y = view.Y / span; y <= view.Bottom / span; y++)
            for (int x = view.X / span; x <= view.Right / span; x++)
                if (!_roadChunks.ContainsKey((x, y)) &&
                    (!_preparedGround.TryGetValue((x, y), out var packet) || GroundTextureCachingEnabled && packet.Textures is null))
                    missing.Add((x, y));
        bool Visible((int X, int Y) key) => key.X >= _cameraVisibleView.X / span &&
            key.X <= _cameraVisibleView.Right / span && key.Y >= _cameraVisibleView.Y / span &&
            key.Y <= _cameraVisibleView.Bottom / span;
        if (missing.Count == 0 && _cameraGroundWork is null) _groundBatchContext = null;
        if (missing.Any(Visible) && _cameraGroundWork is { } pending &&
            !pending.Requested.Any(Visible)) pending.Cancellation.Cancel();
        if (missing.Count > 0 && _cameraGroundWork is null)
        {
            if (_groundBatchContext is not { } previous || previous.Epoch != epoch || previous.View != view)
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var snapshot = session.Game.CreateSimulationSnapshot();
                var roads = _roadLayout!.ForkForMap(snapshot.Map);
                var surfaces = _roadSurfaces!.Fork(roads);
                var captured = new GameSession(snapshot, showGuide: false);
                captured.SetOverlay(session.Overlay);
                _groundBatchContext = new(epoch, view, captured, surfaces, GC.GetAllocatedBytesForCurrentThread() - allocated);
                _groundSnapshotsCaptured++;
                _groundSnapshotMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var context = _groundBatchContext;
            bool textures = GroundTextureCachingEnabled;
            var ordered = missing.OrderBy(key => Visible(key) ? 0 : 1).Take(32).ToArray();
            var reusable = ordered.Where(key => _preparedGround.ContainsKey(key))
                .ToDictionary(key => key, key => _preparedGround[key]);
            var reusablePixels = TerrainPriorityEnabled ? ordered.Where(key => _terrainPixels.ContainsKey(key))
                .ToDictionary(key => key, key => _terrainPixels[key]) : [];
            var completed = new ConcurrentQueue<((int X, int Y), RoadSurfaceMeshes.PreparedChunk)>();
            var cancellation = new CancellationTokenSource();
            _cameraGroundWork = new(epoch, ordered.ToHashSet(), completed, Task.Run(() =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var key in ordered)
                {
                    if (cancellation.IsCancellationRequested) break;
                    var packet = reusable.TryGetValue(key, out var retained)
                        ? PrepareGroundTextures(context.Session, retained, key.X, key.Y, textures)
                        : PrepareGroundData(context.Session, context.Surfaces, key.X, key.Y, textures,
                            reusablePixels.GetValueOrDefault(key));
                    completed.Enqueue((key, packet));
                }
                return new GroundResult(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }), cancellation, context.Bytes, context.Surfaces);
        }
        return !missing.Any(Visible);
    }

    private void PollCameraGround(GameSession session)
    {
        if (_cameraGroundWork is not { } work) return;
        bool current = work.Epoch == GroundVersion(session);
        if (!current) work.Cancellation.Cancel();
        bool changed = false;
        while (work.Completed.TryDequeue(out var completed))
        {
            if (!current) continue;
            _preparedGroundEpoch = work.Epoch;
            RetainGround(session, completed.Key, completed.Chunk);
            changed = true;
        }
        if (changed && GetParent() is Godot.Control parent) parent.QueueRedraw();
        if (!work.Task.IsCompleted) return;
        _cameraGroundWork = null;
        work.Cancellation.Dispose();
        if (!work.Task.IsCompletedSuccessfully)
        {
            Godot.GD.PushError($"Camera ground preparation failed: {work.Task.Exception}");
            session.SetMessage("Camera ground preparation failed; see the Godot log.", MessageKind.Error);
            _cameraGroundFailed = true;
            return;
        }
        if (!current) return;
        _roadSurfaces!.ReusePreparedDependencies(work.Surfaces);
        var result = work.Task.GetAwaiter().GetResult();
        GroundWorkerMilliseconds = result.Milliseconds;
        TotalGroundWorkerMilliseconds += result.Milliseconds;
    }

    private bool WaitingForGround((int X, int Y) key, GameSession session) =>
        !_roadChunks.ContainsKey(key) && !_preparedGround.ContainsKey(key) &&
        _cameraGroundWork is { } work && work.Epoch == GroundVersion(session) && work.Requested.Contains(key);

    private bool GroundVisiblePending
    {
        get
        {
            if (_groundMap is null) return false;
            const int span = TerrainSprite.ChunkCells;
            for (int y = _cameraVisibleView.Y / span; y <= _cameraVisibleView.Bottom / span; y++)
                for (int x = _cameraVisibleView.X / span; x <= _cameraVisibleView.Right / span; x++)
                    if (!_mountedGroundChunks.Contains((x, y))) return true;
            return false;
        }
    }

    private void MountBufferedGround()
    {
        if (_cameraGeometryPending || _cameraPathSession is not { } session ||
            !ReferenceEquals(_groundMap, session.Game.Map) || _preparedGroundEpoch != GroundVersion(session)) return;
        if (GroundVisiblePending && _cameraGroundWork is null && session.Game.CanSnapshotSimulation)
            PrepareCameraGround(session, _view);
        const int span = TerrainSprite.ChunkCells;
        int expected = (_view.Right / span - _view.X / span + 1) * (_view.Bottom / span - _view.Y / span + 1);
        if (_mountedGroundChunks.Count == expected) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool changed = false;
        var keys = new List<(int X, int Y)>();
        for (int y = _view.Y / span; y <= _view.Bottom / span; y++)
            for (int x = _view.X / span; x <= _view.Right / span; x++) keys.Add((x, y));
        foreach (var key in keys.OrderBy(key => key.X >= _cameraVisibleView.X / span &&
            key.X <= _cameraVisibleView.Right / span && key.Y >= _cameraVisibleView.Y / span &&
            key.Y <= _cameraVisibleView.Bottom / span ? 0 : 1))
            {
                if (_mountedGroundChunks.Contains(key) ||
                    !_roadChunks.ContainsKey(key) && !_preparedGround.ContainsKey(key)) continue;
                MountGroundChunk(session, key.X, key.Y);
                changed = true;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 4)
                {
                    RequestRender();
                    return;
                }
            }
        if (changed) RequestRender();
    }

    private bool PrepareCameraPresentation(GameSession session, CellRect view)
    {
        bool paths = PrepareCameraPaths(session, view);
        bool ground = PrepareCameraGround(session, view);
        _waitingForCameraPaths = !paths || !ground;
        return paths && ground;
    }

    private void MountPendingDestinationGround()
    {
        if (!_cameraGeometryPending || _cameraPathSession is not { } session ||
            _preparedGroundEpoch != GroundVersion(session)) return;
        if (_stagingGroundView != _cameraVisibleView)
        {
            ReconcileGroundVisibility(_cameraVisibleView);
            _stagingGroundView = _cameraVisibleView;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int y = _cameraVisibleView.Y / 16; y <= _cameraVisibleView.Bottom / 16; y++)
            for (int x = _cameraVisibleView.X / 16; x <= _cameraVisibleView.Right / 16; x++)
            {
                if (_mountedGroundChunks.Contains((x, y)) ||
                    !_preparedGround.ContainsKey((x, y)) && !_roadChunks.ContainsKey((x, y))) continue;
                MountGroundChunk(session, x, y);
                RequestRender();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 4) return;
            }

    }

    private void ReconcileGroundVisibility(CellRect view)
    {
        bool Included((int X, int Y) key) => key.X >= view.X / TerrainSprite.ChunkCells &&
            key.X <= view.Right / TerrainSprite.ChunkCells && key.Y >= view.Y / TerrainSprite.ChunkCells &&
            key.Y <= view.Bottom / TerrainSprite.ChunkCells;
        foreach (var (key, node) in _groundNodes)
        {
            bool wasVisible = node.Visible;
            node.Visible = Included(key);
            if (_groundDrawPagesEnabled && !_stableGroundPages && wasVisible != node.Visible) MarkGroundPageDirty(key);
            if (node.Visible && !_groundDrawPagesEnabled) _mountedGroundChunks.Add(key);
            else if (!node.Visible) _mountedGroundChunks.Remove(key);
            if (_groundDrawPagesEnabled && node.Visible && !_mountedGroundChunks.Contains(key))
            {
                if (_stableGroundPages && GroundPageHasOwner(key)) _mountedGroundChunks.Add(key);
                else
                {
                    _pendingGroundPublication.Add(key);
                    MarkGroundPageDirty(key);
                }
            }
            if (!node.Visible) _pendingGroundPublication.Remove(key);
        }
        _mountedGroundChunks.RemoveWhere(key => !Included(key) || !_groundNodes.ContainsKey(key));
        SyncStableGroundPageVisibility();
    }
}
