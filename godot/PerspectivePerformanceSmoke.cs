using System.Diagnostics;
using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal sealed class PerformanceBudgetExceededException(string message) : InvalidOperationException(message) { }

internal static class PerspectivePerformanceSmoke
{
    public static async Task Run(Main host)
    {
        var budgetFailures = new List<string>();
        void Budget(bool condition, string message) { if (!condition) budgetFailures.Add(message); }
        var session = host.Session;
        session.Game.Paused = true;
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var window = host.GetWindow();
        GD.Print($"TERMCITY_DISPLAY_BASELINE window={window.Size} content={window.ContentScaleSize} " +
            $"content-factor={window.ContentScaleFactor} canvas-scale={host.Map.GetScreenTransform().Scale} " +
            $"viewport={host.GetViewport().GetVisibleRect().Size} " +
            $"framebuffer={host.GetViewport().GetTexture().GetSize()} " +
            $"screen-scale={DisplayServer.ScreenGetScale(window.CurrentScreen)}");
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
            float expectedSpan = Math.Max(4, host.Map.Size.Y / (22 * PerspectiveCity.Magnification(zoom)));
            Require(Math.Abs(view.CameraSpan - expectedSpan) < 0.0001f,
                "Perspective views must use the tuned normal/close scale and unchanged overview zoom scales.");
            var native = PerspectiveCity.NativePixelSize(host.Map.Size, host.Map.GetScreenTransform().Scale.Abs());
            Require(view.NativeRenderSize == native &&
                view.RenderSize == native * PerspectiveCity.SupersampleScale &&
                view.EdgeAntialiasing == Viewport.Msaa.Msaa8X,
                "The scene must use exact physical-panel sizing, 2x supersampling and 8x MSAA.");
            worstCold = Math.Max(worstCold, watch.Elapsed.TotalMilliseconds);
            worstNodes = Math.Max(worstNodes, view.SceneObjectCount);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(view.Texture.GetSize() == view.RenderSize,
                "The driver must allocate the requested high-quality target rather than silently reduce its size.");
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
        Budget(worstNodes < 512, $"Perspective scene has {worstNodes} separate render nodes; geometry must be batched.");
        Budget(worstCold < 1500, $"A cold scene rebuild took {worstCold:0.0}ms.");
        Budget(worstSelection < 50, $"A selection took {worstSelection:0.0}ms; budget is 50ms.");
        Budget(worstPan < 50, $"A buffered camera pan took {worstPan:0.0}ms; budget is 50ms.");
        Budget(worstPick < 50, $"Building picking took {worstPick:0.0}ms; budget is 50ms.");
        frames.Sort();
        double p90 = frames[(int)(frames.Count * 0.9)];
        Budget(p90 < 60, $"Large-map interactive frame p90 was {p90:0.0}ms; budget is 60ms.");
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
                var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                RenderingServer.ForceDraw();
                await rendered;
            }
            minimapWorst = Math.Max(minimapWorst, watch.Elapsed.TotalMilliseconds);
            GD.Print($"TERMCITY_MINIMAP_SAMPLE target={target.X},{target.Y} cpu={cpu:0.0}ms " +
                $"click-to-render={watch.Elapsed.TotalMilliseconds:0.0}ms " +
                $"ground={view.GroundBuildMilliseconds:0.0}ms objects={view.ObjectBuildMilliseconds:0.0}ms " +
                $"terrain-pixels={view.TerrainPixelsMilliseconds:0.0}ms road-pixels={view.RoadPixelsMilliseconds:0.0}ms " +
                $"road-borders={view.RoadBorderMilliseconds:0.0}ms");
            var projected = view.ScreenPoint(new(target.X + 0.5f, 0, target.Y + 0.5f));
            Require(projected.DistanceTo(host.Map.Size / 2) < host.Map.Grid.PixelHeight * 2,
                $"A real minimap click must center the requested destination: target={target}, screen={projected}, " +
                $"center={host.Map.Size / 2}, navigation={session.MapNavigationAllowed}, prompt={session.Prompt?.Title}, " +
                $"minimap={host.Minimap.Size}, picture={host.Minimap.Picture}.");
        }
        var previewStart = Enumerable.Range(0, session.Game.Map.Width * session.Game.Map.Height)
            .Select(session.Game.Map.PosOf).Where(p => p.X < session.Game.Map.Width - 12 &&
                session.Game.CanBuildOn(p.X, p.Y))
            .OrderBy(p => Math.Abs(p.X - center.X) + Math.Abs(p.Y - center.Y)).First();
        session.PlaceCursor(previewStart);
        session.CenterOn(previewStart);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        int previewBuilds = view.GeometryBuilds;
        session.BeginRoadLine(session.Game.Map.Content.Roads.Default);
        double worstRoadPreview = 0;
        for (int length = 4; length <= 10; length += 2)
        {
            watch.Restart();
            session.UpdateDrag(previewStart.Offset(length, 0));
            view.Sync(session, host.Map.Size);
            watch.Stop();
            worstRoadPreview = Math.Max(worstRoadPreview, watch.Elapsed.TotalMilliseconds);
            Require(view.GeometryBuilds == previewBuilds,
                "Updating a road-placement preview must not rebuild the existing city.");
        }
        session.CancelPreview();
        GD.Print($"TERMCITY_ROAD_PREVIEW_PERFORMANCE worst={worstRoadPreview:0.0}ms existing-city-rebuilds=0");
        long estimatedTarget = (long)view.RenderSize.X * view.RenderSize.Y * (8 * 8 + 4);
        GD.Print($"TERMCITY_RENDER_QUALITY native={view.NativeRenderSize} render={view.RenderSize} msaa=8x " +
            $"estimated-target-bytes={estimatedTarget} " +
            $"driver-video-bytes={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed)} " +
            $"driver-texture-bytes={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed)}");
        Budget(worstRoadPreview < 100, $"Road preview updates took {worstRoadPreview:0.0}ms; budget is 100ms.");
        Budget(minimapWorst < 250, $"Minimap click-to-render took {minimapWorst:0.0}ms; budget is 250ms.");
        GD.Print($"TERMCITY_PERSPECTIVE_PERFORMANCE_{(budgetFailures.Count == 0 ? "OK" : "OVER_BUDGET")} nodes={worstNodes} cold={worstCold:0.0}ms " +
            $"selection={worstSelection:0.0}ms pan={worstPan:0.0}ms pick={worstPick:0.0}ms frame-p90={p90:0.0}ms " +
            $"minimap-click-to-render={minimapWorst:0.0}ms");
        if (budgetFailures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", budgetFailures));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
