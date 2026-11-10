using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class TerrainHaloScrollSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Halo scrolling requires graphical frames.");
        var failures = new List<string>();
        var session = host.Session;
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        string? saved = null;
        host.GetWindow().Mode = Window.ModeEnum.Fullscreen;
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var view = host.Map.Perspective!;
        var map = session.Game.Map;
        var center = map.BuildingFootprints.Values.OrderBy(area => Math.Abs(area.X - map.Width / 2) +
            Math.Abs(area.Y - map.Height / 2)).Select(area => new Pos(area.X, area.Y))
            .FirstOrDefault(new Pos(map.Width / 2, map.Height / 2));
        using var process = Process.GetCurrentProcess();
        foreach (double zoom in new[] { 0d, (double)GameSession.MinZoom })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            session.CenterOn(center);
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            await host.ToSignal(host.GetTree().CreateTimer(2), SceneTreeTimer.SignalName.Timeout);
            saved ??= SaveGameStore.Serialize(session.Game);
            var camera = (session.CameraX, session.CameraY);
            int uploads = view.GroundChunksUploaded;
            long bytes = view.GroundUploadBytes;
            int instances = view.ObjectInstancesUploaded;
            long objectBytes = view.ObjectUploadBytes;
            var preparation = view.PreparationProfile;
            var retired = view.TerrainRetirementCounts;
            var samples = new List<double>();
            var frames = new List<double>();
            int maxNodes = 0, maxHalo = 0;
            long peakMemory = 0;
            int repeatUploads = 0;
            GD.Print($"TERMCITY_HALO_SCROLL_DISPLAY zoom={zoom} enter={view.TerrainHaloRadii.Enter} exit={view.TerrainHaloRadii.Exit} " +
                $"overview-lod={view.OverviewLodActive} " +
                $"fullscreen={host.GetWindow().Mode == Window.ModeEnum.Fullscreen} window={host.GetWindow().Size} " +
                $"native={view.NativeRenderSize} render={view.RenderSize} msaa={view.EdgeAntialiasing}");
            for (int step = 0; step < 48; step++)
            {
                if (step == 24) repeatUploads = view.GroundChunksUploaded;
                int phase = step % 24;
                int offset = phase < 12 ? (phase + 1) * 4 : (23 - phase) * 4;
                var previous = view.CaptureTerrainPublication();
                var watch = Stopwatch.StartNew();
                session.ScrollCamera(camera.CameraX + offset - session.CameraX, camera.CameraY - session.CameraY);
                view.Sync(session, host.Map.Size);
                view.RequestRender();
                host.Map.QueueRedraw();
                async Task Drawn() => await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var drawn = Drawn();
                if (await Task.WhenAny(drawn, Task.Delay(TimeSpan.FromSeconds(20))) != drawn)
                    throw new InvalidOperationException("Halo scrolling stopped receiving rendered frames.");
                await drawn;
                frames.Add(watch.Elapsed.TotalMilliseconds);
                view.ValidateTerrainContinuity(previous);
                var ready = view.AwaitFootpathMesh();
                if (await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(120))) != ready)
                    throw new InvalidOperationException($"Halo scrolling stalled: {view.PreparationPendingDescription}");
                await ready;
                samples.Add(watch.Elapsed.TotalMilliseconds);
                view.ValidateObjectBuffers();
                if (view.OverviewLodActive && step == 0) view.ValidateOverviewGeometry(session);
                view.ValidateGroundDrawPages();
                maxNodes = Math.Max(maxNodes, view.SceneObjectCount);
                maxHalo = Math.Max(maxHalo, view.TerrainHaloCount);
                process.Refresh();
                peakMemory = Math.Max(peakMemory, process.WorkingSet64);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            static double P90(List<double> data) => data.Order().ElementAt((int)((data.Count - 1) * .9));
            var after = view.TerrainRetirementCounts;
            view.PrintPreparationProfile("HaloScroll", zoom, preparation);
            GD.Print($"TERMCITY_HALO_SCROLL_SUMMARY zoom={zoom} enter={view.TerrainHaloRadii.Enter} exit={view.TerrainHaloRadii.Exit} " +
                $"steps=48 ready-p90={P90(samples):0.0}ms ready-worst={samples.Max():0.0}ms frame-p90={P90(frames):0.0}ms " +
                $"frame-worst={frames.Max():0.0}ms ground-uploads={view.GroundChunksUploaded - uploads} " +
                $"repeat-uploads={view.GroundChunksUploaded - repeatUploads} ground-bytes={view.GroundUploadBytes - bytes} " +
                $"retire-memory={after.Memory - retired.Memory} retire-nodes={after.Nodes - retired.Nodes} " +
                $"retire-policy={after.Policy - retired.Policy} invalidated={after.Invalidated - retired.Invalidated} " +
                $"nodes-max={maxNodes} halo-max={maxHalo} process-peak={peakMemory} " +
                $"overview-lod={view.OverviewLodActive} " +
                $"instances-uploaded={view.ObjectInstancesUploaded - instances} object-upload-bytes={view.ObjectUploadBytes - objectBytes} " +
                $"page-rebuilds={view.GroundPagesRebuilt} page-upload-bytes={view.GroundPageUploadBytes}");
            if (samples.Max() >= 250 || P90(frames) >= 60 || frames.Max() >= 100 || maxNodes >= 512)
                failures.Add($"zoom {zoom} exceeded existing readiness/frame/node budgets.");
            if (capture is not null)
            {
                await GodotSmoke.RenderedFrame(host);
                using var image = host.GetViewport().GetTexture().GetImage();
                var error = image.SavePng(zoom == GameSession.MinZoom ? Path.ChangeExtension(capture, ".overview.png") : capture);
                if (error != Error.Ok) throw new IOException($"Halo capture failed: {error}.");
            }
        }
        if (view.OverviewLodEnabled) await PerspectiveCity.VerifyOverviewLodTransitions(host);
        string afterSave = SaveGameStore.Serialize(session.Game);
        if (saved != afterSave)
        {
            var before = System.Text.Json.Nodes.JsonNode.Parse(saved!)!.AsObject();
            var after = System.Text.Json.Nodes.JsonNode.Parse(afterSave)!.AsObject();
            var changed = before.Where(pair => !System.Text.Json.Nodes.JsonNode.DeepEquals(pair.Value, after[pair.Key]))
                .Select(pair => pair.Key);
            throw new InvalidOperationException($"Halo/draw-page diagnostics changed saved simulation fields: {string.Join(", ", changed)}.");
        }
        view.ConfigureCacheExperiment(CacheExperiment.Tuned);
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        view.ValidateObjectBuffers();
        GD.Print("TERMCITY_HALO_SCROLL_CORRECTNESS_OK continuity=retained native-buffers=exact limits=unchanged");
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
    }
}
