using System.Diagnostics;
using Godot;
using TermCity.Core.Session;
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
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
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
        foreach (double zoom in new[] { 0, GameSession.ThreeQuarterZoom, -1, -2, 1 })
        {
            session.SetZoom(zoom);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(center);
            session.ClearSelection();
            watch.Restart();
            int streamed = view.ObjectChunksBuilt;
            int uploaded = view.ObjectInstancesUploaded;
            double streamWork = view.ObjectStreamingMilliseconds;
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            watch.Stop();
            GD.Print($"TERMCITY_OBJECT_COLD zoom={zoom} elapsed={watch.Elapsed.TotalMilliseconds:0.0}ms " +
                $"work={view.ObjectStreamingMilliseconds - streamWork:0.0}ms chunks={view.ObjectChunksBuilt - streamed} " +
                $"instances={view.ObjectInstancesUploaded - uploaded} nodes={view.SceneObjectCount}");
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
            await view.AwaitFootpathMesh();
            watch.Stop();
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
        await VerifyContinuousScrolling(host, budgetFailures);
        await VerifyBuildingCache(host);
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
            await view.AwaitFootpathMesh();
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
                $"ground={view.GroundBuildMilliseconds:0.0}ms ground-worker={view.GroundWorkerMilliseconds:0.0}ms objects={view.ObjectBuildMilliseconds:0.0}ms " +
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
        await view.AwaitFootpathMesh();
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

    private static async Task VerifyContinuousScrolling(Main host, List<string> budgetFailures)
    {
        var session = host.Session;
        var view = host.Map.Perspective!;
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(session.Game.Map.Width / 2, session.Game.Map.Height / 2));
        session.ClearSelection();
        view.Sync(session, host.Map.Size);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var samples = new List<double>();
        double worstSync = 0, worstGround = 0, worstObjects = 0;
        int builds = view.GeometryBuilds;
        int reused = 0, previousBuilds = builds;
        var start = host.Map.Size / 2;
        var anchor = host.Map.BeginPerspectivePan(start);
        for (int step = 1; step <= 90; step++)
        {
            int beforeBuild = view.GeometryBuilds, gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var watch = Stopwatch.StartNew();
            host.Map.PanPerspective(anchor, start - new Vector2(step * 10, step * 4));
            view.Sync(session, host.Map.Size);
            if (view.GeometryBuilds != previousBuilds)
            {
                reused += view.BuildingsReusedLastBuild;
                previousBuilds = view.GeometryBuilds;
                worstGround = Math.Max(worstGround, view.GroundBuildMilliseconds);
                worstObjects = Math.Max(worstObjects, view.ObjectBuildMilliseconds);
            }
            double sync = watch.Elapsed.TotalMilliseconds;
            worstSync = Math.Max(worstSync, sync);
            view.RequestRender();
            host.Map.QueueRedraw();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            samples.Add(elapsed);
            if (elapsed >= 60)
                GD.Print($"TERMCITY_SCROLL_SPIKE step={step} frame={elapsed:0.0}ms sync={sync:0.0}ms " +
                    $"rebuilds={view.GeometryBuilds - beforeBuild} gc={GC.CollectionCount(0) - gc0}/" +
                    $"{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
        }
        samples.Sort();
        double p90 = samples[(int)(samples.Count * .9)], worst = samples[^1];
        GD.Print($"TERMCITY_CONTINUOUS_SCROLL frames={samples.Count} rebuilds={view.GeometryBuilds - builds} " +
            $"sync-worst={worstSync:0.0}ms frame-p90={p90:0.0}ms frame-worst={worst:0.0}ms " +
            $"ground-worst={worstGround:0.0}ms objects-worst={worstObjects:0.0}ms buildings-reused={reused}");
        if (p90 >= 60)
            budgetFailures.Add($"Continuous scrolling frame p90 was {p90:0.0}ms; budget is 60ms.");
    }

    private static async Task VerifyBuildingCache(Main host)
    {
        var session = host.Session;
        if (session.Game.Map.BuildingFootprints.Count == 0) return;
        var view = host.Map.Perspective!;
        var area = session.Game.Map.BuildingFootprints.Values.OrderBy(plot =>
            Math.Abs(plot.X - session.Game.Map.Width / 2) + Math.Abs(plot.Y - session.Game.Map.Height / 2)).First();
        session.CenterOn(new(area.X, area.Y));
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        view.ValidateCityInstanceBuffers = true;
        try
        {
            int reused = view.ObjectChunksReused;
            int built = view.ObjectChunksBuilt;
            view.InvalidateStaticScene(discardBuildingCache: false);
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            Require(view.BuildingsReusedLastBuild > 0 ||
                view.ObjectChunksReused > reused && view.ObjectChunksBuilt == built,
                "Rebuilding a static view must reuse unchanged building geometry or complete retained chunks.");
            view.ValidateObjectBuffers();
            var geometry = view.BuildingGeometry;
            var roofs = view.BuildingRoofGeometry;
            view.InvalidateStaticScene(discardBuildingCache: true);
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            view.ValidateObjectBuffers();
            Require(geometry.SequenceEqual(view.BuildingGeometry) && roofs.SequenceEqual(view.BuildingRoofGeometry),
                $"Cached building bounds, colors, roof triangles and picking must match a fresh rebuild: " +
                $"boxes={geometry.Count}/{view.BuildingGeometry.Count}, roofs={roofs.Count}/{view.BuildingRoofGeometry.Count}, " +
                $"box-diff={geometry.Zip(view.BuildingGeometry).FirstOrDefault(pair => !Equals(pair.First, pair.Second))}, " +
                $"roof-diff={roofs.Zip(view.BuildingRoofGeometry).FirstOrDefault(pair => !Equals(pair.First, pair.Second))}.");
            await view.AwaitObjectPrefetch();
            var anchor = new Pos(area.X, area.Y);
            var token = view.CachedObjectToken(anchor);
            session.CenterOn(new(Math.Min(session.Game.Map.Width - 1, area.X + 48), area.Y));
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            session.CenterOn(anchor);
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            Require(token is not null && ReferenceEquals(token, view.CachedObjectToken(anchor)),
                "An out-and-back camera move must reuse complete cached ownership chunks.");
            await view.AwaitObjectPrefetch();
            var beforeEdit = view.CaptureCurrentOwnershipCache();
            var beforeEditAnchor = view.CachedObjectToken(anchor);
            int index = session.Game.Map.Index(anchor.X, anchor.Y);
            int rotation = session.Game.Map.BuildingRotations.GetValueOrDefault(index);
            bool hadRotation = session.Game.Map.BuildingRotations.ContainsKey(index);
            session.Game.Map.BuildingRotations[index] = (rotation + 2) % 4;
            session.Game.Touch();
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            await view.AwaitObjectPrefetch();
            Require(view.ChangedOwnershipChunks(beforeEdit) is > 0 and <= 9 &&
                !ReferenceEquals(beforeEditAnchor, view.CachedObjectToken(anchor)),
                "A local building rotation must regenerate its dependency neighborhood, not the whole scene.");
            if (hadRotation) session.Game.Map.BuildingRotations[index] = rotation;
            else session.Game.Map.BuildingRotations.Remove(index);
            session.Game.Touch();
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            Require(view.CpuCacheBytes <= view.CpuCacheBudgetBytes || view.WarmingSuspended,
                "Cache overage must suspend discretionary warming.");
            view.PrintCacheDiagnostics("performance");
            view.PrintObjectUploadProfile("performance");
            GD.Print($"TERMCITY_OBJECT_CACHE_OK out-and-back=retained local-edit=bounded chunks={view.ObjectCacheCount} " +
                $"encoded-bytes={view.ObjectEncodedCacheBytes} resident-buffer-bytes={view.ObjectResidentBufferBytes} " +
                $"instance-upload-bytes={view.ObjectUploadBytes}");
        }
        finally { view.ValidateCityInstanceBuffers = false; }
        GD.Print("TERMCITY_BUILDING_CACHE_OK bounds=exact roofs=exact bulk-transforms=exact bulk-colors=validated");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
