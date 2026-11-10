using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal sealed record TerrainPublication(object Epoch,
        IReadOnlyDictionary<(int X, int Y), Node3D> Nodes);

    internal TerrainPublication CaptureTerrainPublication()
    {
        var session = _cameraPathSession ?? _streamSession ??
            throw new InvalidOperationException("Terrain continuity inspection requires an owning session.");
        return new(GroundVersion(session), _groundNodes.Where(pair => pair.Value.Visible &&
            (!_groundDrawPagesEnabled || _mountedGroundChunks.Contains(pair.Key)))
            .ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    internal int ValidateTerrainContinuity(TerrainPublication previous)
    {
        var session = _cameraPathSession ?? _streamSession ??
            throw new InvalidOperationException("Terrain continuity inspection requires an owning session.");
        if (!Equals(previous.Epoch, GroundVersion(session))) return 0;
        int checkedChunks = 0;
        foreach (var (key, node) in previous.Nodes)
        {
            if (key.X < _cameraVisibleView.X / 16 || key.X > _cameraVisibleView.Right / 16 ||
                key.Y < _cameraVisibleView.Y / 16 || key.Y > _cameraVisibleView.Bottom / 16) continue;
            if (!_groundNodes.TryGetValue(key, out var current) || current != node ||
                !current.IsVisibleInTree() || !_mountedGroundChunks.Contains(key))
                throw new InvalidOperationException($"Camera movement withdrew unchanged visible terrain at {key}: " +
                    $"resident={_groundNodes.ContainsKey(key)}, same={current == node}, " +
                    $"visible={current?.IsVisibleInTree()}, mounted={_mountedGroundChunks.Contains(key)}, " +
                    $"view={_view}, camera={_cameraVisibleView}, pending={_cameraGeometryPending}.");
            checkedChunks++;
        }
        return checkedChunks;
    }

    internal static async Task VerifyTerrainContinuity(Main host)
    {
        var view = host.Map.Perspective ??
            throw new InvalidOperationException("Terrain continuity checks require perspective rendering.");
        var session = host.Session;
        int x = session.CameraX, y = session.CameraY;
        var offset = view.CameraOffset;
        int checks = 0;
        int sharedChunks = 0;
        for (int frame = 0; frame < 48; frame++)
        {
            var previous = view.CaptureTerrainPublication();
            host.Map.ScrollPerspective(new Vector2(frame < 24 ? .5f : 8, .25f));
            view.Sync(session, host.Map.Size);
            view.RequestRender();
            host.Map.QueueRedraw();
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            sharedChunks += view.ValidateTerrainContinuity(previous);
            checks++;
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        session.ScrollCamera(x - session.CameraX, y - session.CameraY);
        view.SetCameraOffset(session, offset);
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        if (sharedChunks == 0)
            throw new InvalidOperationException("Terrain continuity check did not exercise retained overlapping chunks.");
        GD.Print($"TERMCITY_TERRAIN_CONTINUITY_OK frames={checks} shared-chunks={sharedChunks} " +
            "slow-pan=subpixel crossing=buffered retained-ground=visible");
    }
}
