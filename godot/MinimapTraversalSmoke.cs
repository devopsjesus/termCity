using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class MinimapTraversalSmoke
{
    internal static async Task Run(Main host, string? capture, string label,
        Func<Texture2D>? presentedTexture = null, Func<(double Cpu, double Gpu)>? retirement = null,
        double? selectedZoom = null, GameSpeed liveSpeed = GameSpeed.Medium)
    {
        var failures = new List<string>();
        bool roadsOff = host.Map.Perspective is { RoadRenderingEnabled: false };
        foreach (double zoom in selectedZoom is { } only ? new[] { only } : new[] { 0d, (double)GameSession.MinZoom })
        {
            if (roadsOff && host.Map.Perspective is { RoadRenderingEnabled: true } view)
                view.ConfigureCacheExperiment(CacheExperiment.RoadsOff);
            try { await RunZoom(host, capture, label, zoom, presentedTexture, retirement, liveSpeed); }
            catch (PerformanceBudgetExceededException error) { failures.Add(error.Message); }
        }
        if (host.Map.Perspective is { OverviewLodEnabled: true })
            await PerspectiveCity.VerifyOverviewLodTransitions(host);
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join("; ", failures));
    }

    private static async Task RunZoom(Main host, string? capture, string label, double zoom,
        Func<Texture2D>? presentedTexture, Func<(double Cpu, double Gpu)>? retirement, GameSpeed liveSpeed)
    {
        if (DisplayServer.GetName() == "headless")
            throw new InvalidOperationException("Sustained minimap traversal requires actual graphical frames.");
        var window = host.GetWindow();
        await GodotSmoke.RequireFullscreenLayout(host);
        var session = host.Session;
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        session.SetZoom(zoom);
        host.Map.RefreshCells();
        presentedTexture ??= () => view.Texture;
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        var native = PerspectiveCity.NativePixelSize(host.Map.Size, host.Map.GetScreenTransform().Scale.Abs());
        if (view.NativeRenderSize != native || view.RenderSize != native * PerspectiveCity.SupersampleScale ||
            view.EdgeAntialiasing != Viewport.Msaa.Msaa8X)
            throw new InvalidOperationException("Traversal must preserve native resolution, supersampling and 8x MSAA at every zoom.");
        if (!window.Borderless || window.Size.X < DisplayServer.ScreenGetSize(window.CurrentScreen).X)
            throw new InvalidOperationException("Traversal must run fullscreen, not in a small diagnostic window.");
        GD.Print($"TERMCITY_TRAVERSAL_DISPLAY experiment={label} zoom={zoom} fullscreen-sized=true mode={window.Mode} " +
            $"overview-lod={view.OverviewLodActive} " +
            $"window={window.Size} panel={host.Map.Size} screen={DisplayServer.ScreenGetSize(window.CurrentScreen)} " +
            $"native={view.NativeRenderSize} render={view.RenderSize} msaa=8x");
        GodotSmoke.ValidateFullDisplaySize(host);
        await PerspectiveCity.VerifyTerrainContinuity(host);
        await host.ToSignal(host.GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
        var map = session.Game.Map;
        bool roadsOff = !view.RoadRenderingEnabled;
        string? pausedState = roadsOff ? SaveGameStore.Serialize(session.Game) : null;
        int roadCells = Enumerable.Range(0, map.Width * map.Height)
            .Count(index => map.HasRoad(index % map.Width, index / map.Width));
        if (roadsOff)
        {
            view.ValidateDiagnosticRoadsOff();
            GD.Print($"TERMCITY_ROADS_OFF_START road-cells={roadCells} simulation-roads=preserved " +
                "rendering=terrain-only road-surfaces=disabled road-cuts=disabled road-bridges=disabled");
        }
        var home = map.BuildingFootprints.Count > 0
            ? map.BuildingFootprints.Values.OrderBy(area => Math.Abs(area.X - map.Width / 2) +
                Math.Abs(area.Y - map.Height / 2)).Select(area => new Pos(area.X, area.Y)).First()
            : new Pos(map.Width / 2, map.Height / 2);
        var targets = new[] { new Pos(map.Width / 5, map.Height / 5),
            new Pos(map.Width * 4 / 5, map.Height * 4 / 5), new Pos(map.Width / 5, map.Height * 4 / 5),
            new Pos(map.Width * 4 / 5, map.Height / 5), new Pos(map.Width / 2, map.Height * 3 / 4),
            new Pos(map.Width / 3, map.Height / 2) };
        var responses = new List<double>();
        var terrainReady = new List<double>();
        var readiness = new List<double>();
        var frameTimes = new List<double>();
        var handlers = new List<double>();
        var budgetFailures = new List<string>();
        int allBuilds = view.ObjectChunksBuilt;
        long allUploads = view.ObjectUploadBytes;
        double groundWorkerStart = view.TotalGroundWorkerMilliseconds;
        var preparationStart = view.PreparationProfile;
        long peakMemory = 0;
        double runningStart = 0;
        using var process = Process.GetCurrentProcess();

        static Color[] Sample(Texture2D texture)
        {
            using var image = texture.GetImage();
            return Enumerable.Range(0, 25).Select(index =>
                image.GetPixel(Math.Clamp((index % 5 + 1) * image.GetWidth() / 6, 0, image.GetWidth() - 1),
                    Math.Clamp((index / 5 + 1) * image.GetHeight() / 6, 0, image.GetHeight() - 1))).ToArray();
        }

        for (int index = 0; index < 24; index++)
        {
            if (index == 12)
            {
                if (pausedState is not null && pausedState != SaveGameStore.Serialize(session.Game))
                    throw new InvalidOperationException("Road-off rendering altered serialized simulation state during paused traversal.");
                runningStart = session.Game.ElapsedDays;
                session.Game.Speed = liveSpeed;
                session.Game.Paused = false;
            }
            var target = index % 4 == 3 ? home : targets[(index - index / 4) % targets.Length];
            var previous = Sample(presentedTexture());
            var previousTerrain = view.CaptureTerrainPublication();
            int built = view.ObjectChunksBuilt;
            long uploaded = view.ObjectUploadBytes;
            var retired = retirement?.Invoke() ?? (0, 0);
            var watch = Stopwatch.StartNew();
            var pointer = host.Minimap.ScreenPoint(target);
            using var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer };
            using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pointer };
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, press);
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, release);
            handlers.Add(watch.Elapsed.TotalMilliseconds);
            view.Sync(session, host.Map.Size);
            double firstResponse = -1;
            double terrainResponse = -1;
            long frameStart = Stopwatch.GetTimestamp();
            do
            {
                if (watch.Elapsed.TotalSeconds > 120)
                    throw new InvalidOperationException($"Sustained minimap traversal stalled at zoom {zoom}, jump {index}, " +
                        $"experiment {label}, readiness={view.PreparationPendingDescription}.");
                view.RequestRender();
                host.Map.QueueRedraw();
                async Task Drawn() => await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var drawn = Drawn();
                if (await Task.WhenAny(drawn, Task.Delay(TimeSpan.FromSeconds(20))) != drawn)
                    throw new InvalidOperationException($"Traversal stopped receiving rendered frames at jump {index}, experiment {label}.");
                await drawn;
                GodotSmoke.ValidateFullDisplaySize(host);
                view.ValidateTerrainContinuity(previousTerrain);
                frameTimes.Add(Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
                if (terrainResponse < 0 && view.VisibleTerrainReady) terrainResponse = watch.Elapsed.TotalMilliseconds;
                if (firstResponse < 0)
                {
                    double sampledAt = watch.Elapsed.TotalMilliseconds;
                    if (!Sample(presentedTexture()).SequenceEqual(previous)) firstResponse = sampledAt;
                }
                frameStart = Stopwatch.GetTimestamp();
                if (view.FootpathMeshPending)
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            } while (view.FootpathMeshPending);
            double ready = watch.Elapsed.TotalMilliseconds;
            if (firstResponse < 0) firstResponse = ready;
            responses.Add(firstResponse);
            readiness.Add(ready);
            terrainReady.Add(terrainResponse < 0 ? ready : terrainResponse);
            view.ValidateObjectBuffers();
            if (view.OverviewLodActive && index == 0) view.ValidateOverviewGeometry(session);
            if (view.SceneObjectCount >= 512) budgetFailures.Add($"zoom {zoom} jump {index} render nodes {view.SceneObjectCount} exceeds 512");
            if (roadsOff) view.ValidateDiagnosticRoadsOff();
            process.Refresh();
            peakMemory = Math.Max(peakMemory, process.WorkingSet64);
            var afterRetired = retirement?.Invoke() ?? (0, 0);
            GD.Print($"TERMCITY_TRAVERSAL_SAMPLE experiment={label} zoom={zoom} full-overview={zoom == GameSession.MinZoom} " +
                $"jump={index} running={index >= 12} target={target} " +
                $"requested-speed={liveSpeed} actual-speed={session.Game.Speed} " +
                $"response={firstResponse:0.0}ms ready={ready:0.0}ms handler={handlers[^1]:0.0}ms " +
                $"terrain-ready={terrainReady[^1]:0.0}ms " +
                $"builds={view.ObjectChunksBuilt - built} upload-bytes={view.ObjectUploadBytes - uploaded} " +
                $"cpu-retire={afterRetired.Item1 - retired.Item1:0.0}ms gpu-retire={afterRetired.Item2 - retired.Item2:0.0}ms " +
                $"nodes={view.SceneObjectCount} heap={GC.GetTotalMemory(false)} working-set={process.WorkingSet64}");
            if (session.ZoomLevel != zoom) throw new InvalidOperationException("Traversal did not preserve the requested zoom.");
            if (ready >= 250) budgetFailures.Add($"zoom {zoom} jump {index} readiness {ready:0.0}ms exceeds 250ms");
            if (handlers[^1] >= 50) budgetFailures.Add($"zoom {zoom} jump {index} handler {handlers[^1]:0.0}ms exceeds 50ms");
            await host.ToSignal(host.GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
        }
        session.Game.Paused = true;
        if (session.Game.ElapsedDays <= runningStart)
            throw new InvalidOperationException("The running half of sustained traversal did not advance the simulation.");
        static double P90(List<double> values) => values.Order().ElementAt((int)((values.Count - 1) * .9));
        static double P95(List<double> values) => values.Order().ElementAt((int)((values.Count - 1) * .95));
        if (P90(frameTimes) >= 60) budgetFailures.Add($"frame p90 {P90(frameTimes):0.0}ms exceeds 60ms");
        if (frameTimes.Max() >= 100) budgetFailures.Add($"worst frame {frameTimes.Max():0.0}ms exceeds 100ms");
        view.PrintPreparationProfile(label, zoom, preparationStart);
        view.PrintObjectUploadProfile($"traversal-{label}-zoom-{zoom}");
        view.PrintPersistentResidency($"traversal-{label}-zoom-{zoom}");
        GD.Print($"TERMCITY_TRAVERSAL_SUMMARY experiment={label} zoom={zoom} full-overview={zoom == GameSession.MinZoom} jumps={readiness.Count} " +
            $"response-p90={P90(responses):0.0}ms ready-p90={P90(readiness):0.0}ms ready-worst={readiness.Max():0.0}ms " +
            $"response-p95={P95(responses):0.0}ms ready-p95={P95(readiness):0.0}ms frame-p95={P95(frameTimes):0.0}ms " +
            $"terrain-ready-p90={P90(terrainReady):0.0}ms " +
            $"first-half-mean={readiness.Take(12).Average():0.0}ms second-half-mean={readiness.Skip(12).Average():0.0}ms " +
            $"frame-p90={P90(frameTimes):0.0}ms frame-worst={frameTimes.Max():0.0}ms handler-worst={handlers.Max():0.0}ms " +
            $"builds={view.ObjectChunksBuilt - allBuilds} upload-bytes={view.ObjectUploadBytes - allUploads} sampled-process-peak={peakMemory} " +
            $"running-days={session.Game.ElapsedDays - runningStart:0.000} " +
            $"ground-worker-total={view.TotalGroundWorkerMilliseconds - groundWorkerStart:0.0}ms road-rendering={view.RoadRenderingEnabled} " +
            $"budget-failures={budgetFailures.Count} readback=25-pixel-first-response-probes");
        if (capture is not null)
        {
            await GodotSmoke.RenderedFrame(host);
            using var image = host.GetViewport().GetTexture().GetImage();
            var path = zoom == GameSession.MinZoom ? Path.ChangeExtension(capture, ".overview.png") : capture;
            var error = image.SavePng(path);
            if (error != Error.Ok) throw new IOException($"Traversal capture failed: {error}.");
        }
        GD.Print("TERMCITY_TRAVERSAL_CORRECTNESS_OK picking=native-buffers-exact fixtures=unchanged quality=unchanged");
        if (roadsOff)
        {
            int finalRoadCells = Enumerable.Range(0, map.Width * map.Height)
                .Count(index => map.HasRoad(index % map.Width, index / map.Width));
            if (finalRoadCells != roadCells)
                throw new InvalidOperationException("Road-off traversal changed the logical road-cell count.");
            GD.Print($"TERMCITY_ROADS_OFF_OK road-cells={finalRoadCells} saved-state=unchanged-while-paused " +
                "simulation=advanced road-surfaces=absent road-bridges=absent");
            while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            string beforeRestore = SaveGameStore.Serialize(session.Game);
            view.ConfigureCacheExperiment(CacheExperiment.Tuned);
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            await GodotSmoke.RenderedFrame(host);
            view.ValidateRoadRenderingRestored(roadCells > 0);
            view.ValidateObjectBuffers();
            if (beforeRestore != SaveGameStore.Serialize(session.Game))
                throw new InvalidOperationException("Restoring road rendering changed saved simulation state.");
            GD.Print("TERMCITY_ROADS_RESTORED_OK geometry=enabled saved-state=unchanged caches=invalidated");
        }
        if (budgetFailures.Count > 0) throw new PerformanceBudgetExceededException(string.Join("; ", budgetFailures));
    }
}
