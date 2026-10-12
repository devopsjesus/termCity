using System.Diagnostics;
using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class PerspectivePerformanceSmoke
{
    public static async Task Run(Main host)
    {
        var session = host.Session;
        session.Game.Paused = true;
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var center = new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2);
        var watch = new Stopwatch();
        double worstCold = 0, worstSelection = 0, worstPan = 0, worstPick = 0;
        var frames = new List<double>();
        int worstNodes = 0;
        foreach (int zoom in new[] { 0, -1, -2, 1 })
        {
            session.SetZoom(zoom);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(center);
            session.ClearSelection();
            watch.Restart();
            view.Sync(session, host.Map.Size);
            watch.Stop();
            float zoomScale = zoom > 0 ? 2 : 1f / session.Stride;
            float expectedSpan = Math.Max(4, host.Map.Size.Y / (22 * zoomScale * PerspectiveCity.NormalMagnification));
            Require(Math.Abs(view.CameraSpan - expectedSpan) < 0.0001f,
                "The normal view must use the closer 1.8x baseline at every zoom.");
            Require(view.RenderSize == new Vector2I((int)host.Map.Size.X, (int)host.Map.Size.Y),
                "The scene must render at native map-panel resolution, not upscale half-resolution details.");
            worstCold = Math.Max(worstCold, watch.Elapsed.TotalMilliseconds);
            worstNodes = Math.Max(worstNodes, view.SceneObjectCount);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            int builds = view.GeometryBuilds;
            for (int n = 0; n < 12; n++)
            {
                session.SelectCell(center.Offset(n % 4, n / 4));
                watch.Restart();
                view.Sync(session, host.Map.Size);
                watch.Stop();
                worstSelection = Math.Max(worstSelection, watch.Elapsed.TotalMilliseconds);
                Require(view.GeometryBuilds == builds, "Selecting land must not rebuild terrain, roads, trees or buildings.");
                watch.Restart();
                host.Map.TryPickBuilding(host.Map.Size / 2, out _);
                watch.Stop();
                worstPick = Math.Max(worstPick, watch.Elapsed.TotalMilliseconds);
                session.ScrollCamera(1, 0);
                watch.Restart();
                view.Sync(session, host.Map.Size);
                watch.Stop();
                worstPan = Math.Max(worstPan, watch.Elapsed.TotalMilliseconds);
                Require(view.GeometryBuilds == builds, "Small camera pans must reuse buffered scene geometry.");
                watch.Restart();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (n >= 2) frames.Add(watch.Elapsed.TotalMilliseconds);
            }
            session.ScrollCamera(48, 0);
            watch.Restart();
            view.Sync(session, host.Map.Size);
            worstCold = Math.Max(worstCold, watch.Elapsed.TotalMilliseconds);
            Require(view.GeometryBuilds > builds, "Crossing the buffered view must load newly visible geometry.");
        }
        Require(worstNodes < 512, $"Perspective scene has {worstNodes} separate render nodes; geometry must be batched.");
        Require(worstCold < 1500, $"A cold scene rebuild took {worstCold:0.0}ms.");
        Require(worstSelection < 50, $"A selection took {worstSelection:0.0}ms; budget is 50ms.");
        Require(worstPan < 50, $"A buffered camera pan took {worstPan:0.0}ms; budget is 50ms.");
        Require(worstPick < 50, $"Building picking took {worstPick:0.0}ms; budget is 50ms.");
        frames.Sort();
        double p90 = frames[(int)(frames.Count * 0.9)];
        Require(p90 < 60, $"Large-map interactive frame p90 was {p90:0.0}ms; budget is 60ms.");
        session.SetZoom(0);
        host.Map.RefreshCells();
        double minimapWorst = 0;
        foreach (var target in new[] { new Pos(session.Game.Map.Width / 5, session.Game.Map.Height / 5),
            new Pos(session.Game.Map.Width * 4 / 5, session.Game.Map.Height * 4 / 5),
            new Pos(session.Game.Map.Width / 5, session.Game.Map.Height * 4 / 5) })
        {
            watch.Restart();
            var pointer = host.Minimap.ScreenPoint(target);
            host.Minimap.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer });
            host.Minimap.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pointer });
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            double cpu = watch.Elapsed.TotalMilliseconds;
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (DisplayServer.GetName() != "headless")
            {
                view.RequestRender();
                host.Map.QueueRedraw();
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            minimapWorst = Math.Max(minimapWorst, watch.Elapsed.TotalMilliseconds);
            GD.Print($"TERMCITY_MINIMAP_SAMPLE target={target.X},{target.Y} cpu={cpu:0.0}ms " +
                $"click-to-render={watch.Elapsed.TotalMilliseconds:0.0}ms " +
                $"ground={view.GroundBuildMilliseconds:0.0}ms objects={view.ObjectBuildMilliseconds:0.0}ms " +
                $"terrain-pixels={view.TerrainPixelsMilliseconds:0.0}ms road-pixels={view.RoadPixelsMilliseconds:0.0}ms");
            var projected = view.ScreenPoint(new(target.X + 0.5f, 0, target.Y + 0.5f));
            Require(projected.DistanceTo(host.Map.Size / 2) < host.Map.Grid.PixelHeight * 2,
                "A real minimap click must center the requested destination.");
        }
        Require(minimapWorst < 250, $"Minimap click-to-render took {minimapWorst:0.0}ms; budget is 250ms.");
        GD.Print($"TERMCITY_PERSPECTIVE_PERFORMANCE_OK nodes={worstNodes} cold={worstCold:0.0}ms " +
            $"selection={worstSelection:0.0}ms pan={worstPan:0.0}ms pick={worstPick:0.0}ms frame-p90={p90:0.0}ms " +
            $"minimap-click-to-render={minimapWorst:0.0}ms");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
