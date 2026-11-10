using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record CameraPathWork(GameSession Session, GameMap Map, CellRect View,
        int Navigation, int Paths, int Terrain, Task<PreparedPathScene> Task);
    private CameraPathWork? _cameraPathWork;
    private CameraPathWork? _cameraPathsIncompatible;
    private GameSession? _cameraPathSession;
    private PreparedPathScene? _cameraPathsReady;
    private bool _waitingForCameraPaths, _cameraPathsFailed;

    private static CellRect BufferedView(CellRect visible, GameMap map)
    {
        const int chunk = TerrainSprite.ChunkCells, margin = 12;
        int left = Math.Max(0, visible.X / chunk * chunk - margin);
        int top = Math.Max(0, visible.Y / chunk * chunk - margin);
        int right = Math.Min(map.Width - 1, (visible.Right / chunk + 1) * chunk + margin - 1);
        int bottom = Math.Min(map.Height - 1, (visible.Bottom / chunk + 1) * chunk + margin - 1);
        return new(left, top, right - left + 1, bottom - top + 1);
    }

    private bool PrepareCameraPaths(GameSession session, CellRect view)
    {
        PollCameraPaths();
        var map = session.Game.Map;
        if (_cameraPathsIncompatible is { } incompatible && ReferenceEquals(incompatible.Map, map) &&
            incompatible.View == view && incompatible.Navigation == map.NavigationVersion &&
            incompatible.Paths == map.FootpathVersion && incompatible.Terrain == map.TerrainVersion) return true;
        _cameraPathsIncompatible = null;
        if (_cameraPathsReady is { } ready && Matches(ready, map, view)) return true;
        _cameraPathsReady = null;
        if (_cameraPathWork is null)
        {
            _cameraPathSession = session;
            var request = new PathSceneRequest(PathLayoutView(view, map), _roadLayout!);
            _cameraPathWork = new(session, map, view, map.NavigationVersion, map.FootpathVersion,
                map.TerrainVersion, session.Game.PreparePathPresentationAsync(request));
        }
        _waitingForCameraPaths = true;
        return false;
    }

    private void PollCameraPaths()
    {
        if (_cameraPathWork is not { Task.IsCompleted: true } work) return;
        _cameraPathWork = null;
        if (work.Task.IsFaulted)
        {
            GD.PushError($"Camera path preparation failed: {work.Task.Exception}");
            work.Session.SetMessage("Camera path preparation failed; see the Godot log.", MessageKind.Error);
            _cameraPathsFailed = true;
            _waitingForCameraPaths = false;
        }
        else
        {
            var result = work.Task.GetAwaiter().GetResult();
            if (ReferenceEquals(work.Map, work.Session.Game.Map) &&
                work.Navigation == work.Map.NavigationVersion && work.Paths == work.Map.FootpathVersion &&
                work.Terrain == work.Map.TerrainVersion)
            {
                if (result.Yards.All(pair => work.Map.HouseYardCells.TryGetValue(
                    work.Map.Index(pair.Key.X, pair.Key.Y), out int cell) &&
                    cell == work.Map.Index(pair.Value.Cell.X, pair.Value.Cell.Y)))
                    _cameraPathsReady = result;
                else _cameraPathsIncompatible = work;
            }
        }
        if (GetParent() is Control parent) parent.QueueRedraw();
    }

    private static bool Matches(PreparedPathScene paths, GameMap map, CellRect view) =>
        paths.View == PathLayoutView(view, map) && paths.Navigation == map.NavigationVersion &&
        paths.Paths == map.FootpathVersion && paths.Terrain == map.TerrainVersion;

    private PreparedPathScene? TakeCameraPaths(GameMap map, CellRect view)
    {
        if (_cameraPathsReady is not { } paths || !Matches(paths, map, view)) return null;
        _cameraPathsReady = null;
        return paths;
    }

    private async Task AwaitCameraPaths()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_waitingForCameraPaths || GroundVisiblePending)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds > 20)
                throw new InvalidOperationException($"Camera presentation stalled: paths={_waitingForCameraPaths}, " +
                    $"ground={GroundVisiblePending}, geometry={_cameraGeometryPending}, view={_view}, " +
                    $"visible={_cameraVisibleView}, mounted={_mountedGroundChunks.Count}, " +
                    $"worker={_cameraGroundWork is not null}, prepared={_preparedGround.Count}.");
            if (_cameraPathWork is { } work) await work.Task;
            if (_cameraGroundWork is not null) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            PollCameraPaths();
            if (_cameraPathSession is { } session)
            {
                PollCameraGround(session);
                MountBufferedGround();
            }
            Sync(_cameraPathSession ??
                throw new InvalidOperationException("Pending camera paths have no owning session."), DisplaySize);
            if (_waitingForCameraPaths || GroundVisiblePending)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private void CompleteCameraPaths()
    {
        while (_waitingForCameraPaths || GroundVisiblePending)
        {
            if (_cameraPathWork is { } work) work.Task.GetAwaiter().GetResult();
            if (_cameraGroundWork is { } ground) ground.Task.GetAwaiter().GetResult();
            PollCameraPaths();
            if (_cameraPathSession is { } session)
            {
                PollCameraGround(session);
                MountBufferedGround();
            }
            Sync(_cameraPathSession ??
                throw new InvalidOperationException("Pending camera paths have no owning session."), DisplaySize);
        }
    }
}
