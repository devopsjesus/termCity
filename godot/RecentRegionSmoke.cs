using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class RecentRegionSmoke
{
    internal static async Task Run(Main host, CacheExperiment experiment, string? capture, double? selectedZoom = null)
    {
        string label = experiment.ToString();
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Recent-region measurements require fullscreen rendered frames.");
        var session = host.Session;
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await GodotSmoke.RequireFullscreenLayout(host);
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var failures = new List<string>();
        GD.Print($"TERMCITY_TOP_FIXTURE scenario={session.Game.Config.Scenario} size={session.Game.Map.Width}x{session.Game.Map.Height} " +
            $"population={session.Game.Stats.Population} happiness={session.Game.Indicators.Happiness:0.0} " +
            $"walls={session.Game.Settlement?.Walls.Count} intact={session.Game.Settlement?.Walls.All(wall => wall.Health == wall.MaxHealth)} " +
            $"defenders={session.Game.Settlement?.Defenders.Count} cpu-budget={view.CpuCacheBudgetBytes} gpu-budget={view.GpuCacheBudgetBytes}");
        using var process = Process.GetCurrentProcess();
        string? saved = null;
        foreach (double zoom in selectedZoom is { } only ? new[] { only } : new[] { 0d, (double)GameSession.MinZoom })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            session.CenterOn(new Pos(session.Game.Map.Width / 4, session.Game.Map.Height / 2));
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            view.ValidateOverviewGeometry(session);
            saved ??= SaveGameStore.Serialize(session.Game);
            var corners = view.GroundViewCorners();
            GodotSmoke.ValidateFullDisplaySize(host);
            int screenCells = Math.Max(1, (int)Math.Ceiling(corners.Max(point => point.X) - corners.Min(point => point.X)));
            int requestedDistance = screenCells * 2;
            int originX = session.CameraX, originY = session.CameraY;
            var samples = new List<double>();
            var firstFrames = new List<double>();
            int ground = view.GroundChunksUploaded, objects = view.ObjectChunksBuilt;
            long groundBytes = view.GroundUploadBytes, objectBytes = view.ObjectUploadBytes;
            int repeatedGround = 0, repeatedObjects = 0;
            int maximumTravel = 0;
            long repeatedGroundBytes = 0, repeatedObjectBytes = 0, repeatedRoofBytes = 0, repeatedPathBytes = 0;
            long peak = 0;
            int maxResident = 0, maxActive = 0;
            var initialNative = view.CaptureNativeDestination();
            var seenObjects = initialNative.Objects.ToDictionary(pair => pair.Key, pair => pair.Value);
            var seenGround = initialNative.Ground.ToDictionary(pair => pair.Key, pair => pair.Value);
            int requiredObjectRebuilds = 0, requiredObjectPublications = 0, requiredGroundReplacements = 0;
            GD.Print($"TERMCITY_REGION_DISPLAY experiment={label} zoom={zoom} fullscreen-sized=true mode={host.GetWindow().Mode} " +
                $"window={host.GetWindow().Size} panel={host.Map.Size} screen={DisplayServer.ScreenGetSize(host.GetWindow().CurrentScreen)} " +
                $"native={view.NativeRenderSize} render={view.RenderSize} screen-cells={screenCells} requested-travel={requestedDistance} " +
                $"map-span={session.Game.Map.Width} route-may-clip={requestedDistance > session.Game.Map.Width}");
            for (int step = 0; step < 48; step++)
            {
                if (step == 24)
                {
                    repeatedGround = view.GroundChunksUploaded; repeatedObjects = view.ObjectChunksBuilt;
                    repeatedGroundBytes = view.GroundUploadBytes; repeatedObjectBytes = view.ObjectUploadBytes;
                    repeatedRoofBytes = view.ObjectRoofUploadBytes;
                    repeatedPathBytes = view.FootpathUploadBytes;
                }
                int phase = step % 24;
                int offset = (int)Math.Round(requestedDistance * (phase < 12 ? (phase + 1) / 12d : (23 - phase) / 12d));
                var previous = view.CaptureTerrainPublication();
                var watch = Stopwatch.StartNew();
                session.ScrollCamera(originX + offset - session.CameraX, originY - session.CameraY);
                maximumTravel = Math.Max(maximumTravel, session.CameraX - originX);
                view.Sync(session, host.Map.Size);
                view.RequestRender(); host.Map.QueueRedraw();
                async Task Drawn() => await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var drawn = Drawn();
                if (await Task.WhenAny(drawn, Task.Delay(TimeSpan.FromSeconds(20))) != drawn)
                    throw new InvalidOperationException($"Region measurements stopped receiving rendered frames at zoom {zoom}, step {step}.");
                await drawn;
                GodotSmoke.ValidateFullDisplaySize(host);
                firstFrames.Add(watch.Elapsed.TotalMilliseconds);
                view.ValidateTerrainContinuity(previous);
                var ready = view.AwaitFootpathMesh();
                if (await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(120))) != ready)
                    throw new InvalidOperationException($"Region readiness stalled at zoom {zoom}, step {step}: {view.PreparationPendingDescription}");
                await ready;
                samples.Add(watch.Elapsed.TotalMilliseconds);
                view.ValidateObjectBuffers();
                view.ValidateGroundDrawPages();
                var native = view.CaptureNativeDestination();
                foreach (var (key, value) in native.Objects)
                {
                    if (seenObjects.TryGetValue(key, out var old))
                    {
                        if (!ReferenceEquals(old.Geometry, value.Geometry)) requiredObjectRebuilds++;
                        else if (old.Uploads != value.Uploads) requiredObjectPublications++;
                    }
                    seenObjects[key] = value;
                }
                foreach (var (key, value) in native.Ground)
                {
                    if (seenGround.TryGetValue(key, out var old) &&
                        (!ReferenceEquals(old.Geometry, value.Geometry) || !ReferenceEquals(old.Material, value.Material)))
                        requiredGroundReplacements++;
                    seenGround[key] = value;
                }
                maxResident = Math.Max(maxResident, view.SceneObjectCount);
                maxActive = Math.Max(maxActive, view.ActiveSceneObjectCount);
                process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
                GD.Print($"TERMCITY_REGION_SAMPLE experiment={label} zoom={zoom} step={step} " +
                    $"camera={session.CameraX},{session.CameraY} ready={samples[^1]:0.0}ms " +
                    $"resident-nodes={view.SceneObjectCount} active-nodes={view.ActiveSceneObjectCount} " +
                    $"cpu={view.CpuCacheBytes} gpu={view.DestinationGpuBytes}");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            static double P90(List<double> values) => values.Order().ElementAt((int)((values.Count - 1) * .9));
            static double P95(List<double> values) => values.Order().ElementAt((int)((values.Count - 1) * .95));
            view.PrintCacheDiagnostics($"region-{label}-zoom-{zoom}");
            view.PrintObjectUploadProfile($"region-{label}-zoom-{zoom}");
            view.PrintPathPageProfile($"region-{label}-zoom-{zoom}");
            view.PrintPersistentResidency($"region-{label}-zoom-{zoom}");
            GD.Print($"TERMCITY_REGION_SUMMARY experiment={label} zoom={zoom} steps=48 ready-p90={P90(samples):0.0}ms " +
                $"ready-worst={samples.Max():0.0}ms first-frame-p90={P90(firstFrames):0.0}ms frame-worst={firstFrames.Max():0.0}ms " +
                $"ground-uploads={view.GroundChunksUploaded - ground} objects-built={view.ObjectChunksBuilt - objects} " +
                $"repeat-ground={view.GroundChunksUploaded - repeatedGround} repeat-objects={view.ObjectChunksBuilt - repeatedObjects} " +
                $"repeat-ground-bytes={view.GroundUploadBytes - repeatedGroundBytes} repeat-object-bytes={view.ObjectUploadBytes - repeatedObjectBytes} " +
                $"repeat-roof-bytes={view.ObjectRoofUploadBytes - repeatedRoofBytes} " +
                $"repeat-path-bytes={view.FootpathUploadBytes - repeatedPathBytes} " +
                $"ready-p95={P95(samples):0.0}ms second-pass-ready-p90={P90(samples.Skip(24).ToList()):0.0}ms " +
                $"first-frame-p95={P95(firstFrames):0.0}ms actual-travel={maximumTravel} clipped={maximumTravel < requestedDistance} " +
                $"required-object-rebuilds={requiredObjectRebuilds} required-object-republications={requiredObjectPublications} " +
                $"required-ground-replacements={requiredGroundReplacements} " +
                $"ground-bytes={view.GroundUploadBytes - groundBytes} object-bytes={view.ObjectUploadBytes - objectBytes} " +
                $"resident-nodes={maxResident} active-nodes={maxActive} sampled-process-peak={peak}");
            GD.Print($"TERMCITY_REGION_PAGING experiment={label} zoom={zoom} page-rebuilds={view.GroundPagesRebuilt} page-bytes={view.GroundPageUploadBytes}");
            if (samples.Max() >= 250 || P90(firstFrames) >= 60 || firstFrames.Max() >= 100 || maxActive >= 512)
                failures.Add($"zoom {zoom} exceeded unchanged readiness/frame/active-node budgets.");
            if (capture is not null)
            {
                await GodotSmoke.RenderedFrame(host);
                using var image = host.GetViewport().GetTexture().GetImage();
                var error = image.SavePng(zoom == GameSession.MinZoom ? Path.ChangeExtension(capture, ".overview.png") : capture);
                if (error != Error.Ok) throw new IOException($"Region capture failed: {error}.");
                using var mapImage = view.CaptureDisplayImage();
                error = mapImage.SavePng(Path.ChangeExtension(capture,
                    zoom == GameSession.MinZoom ? ".overview.map.png" : ".map.png"));
                if (error != Error.Ok) throw new IOException($"Region map capture failed: {error}.");
            }
        }
        await view.ReportPersistentWarmCompletion();
        view.ValidateGroundDrawPages();
        if (saved != SaveGameStore.Serialize(session.Game))
            throw new InvalidOperationException("Region cache measurements changed paused simulation state.");
        GD.Print("TERMCITY_REGION_CORRECTNESS_OK saved-state=unchanged native-buffers=exact terrain=continuous");
        if (view.OverviewLodEnabled)
            await PerspectiveCity.VerifyOverviewLodTransitions(host, experiment);
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
    }
}
