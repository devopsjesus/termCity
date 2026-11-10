using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class WholeMapCacheSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        if (capture is not null && DisplayServer.GetName() == "headless")
            throw new InvalidOperationException("Cache captures require a graphical display.");
        var window = host.GetWindow();
        if (DisplayServer.GetName() == "headless") window.ContentScaleSize = new Vector2I(960, 800);
        else window.Size = new Vector2I(960, 800);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var session = host.Session;
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        view.TrackCacheMemory();
        var timer = Stopwatch.StartNew();
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        view.PrintCacheDiagnostics("visible-ready");
        GD.Print($"TERMCITY_CACHE_STARTUP elapsed={timer.Elapsed.TotalMilliseconds:0.0}ms");
        GD.Print($"TERMCITY_CACHE_HEAP phase=visible-ready collected={GC.GetTotalMemory(true)}");
        var map = session.Game.Map;
        string? saved = view.TotalMapChunks <= 15 ? SaveGameStore.Serialize(session.Game) : null;
        var targets = new[] { new Pos(map.Width / 5, map.Height / 5),
            new Pos(map.Width * 4 / 5, map.Height * 4 / 5), new Pos(map.Width / 5, map.Height * 4 / 5) };

        async Task Jump(Pos target, string phase)
        {
            timer.Restart();
            var pointer = host.Minimap.ScreenPoint(target);
            host.Minimap.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer });
            host.Minimap.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pointer });
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            await view.AwaitFootpathMesh();
            await GodotSmoke.RenderedFrame(host);
            view.ValidateObjectBuffers();
            GD.Print($"TERMCITY_CACHE_JUMP phase={phase} target={target} ready={timer.Elapsed.TotalMilliseconds:0.0}ms");
        }

        foreach (var target in targets) await Jump(target, "cold");
        timer.Restart();
        int preparing = view.ObjectChunksBuilt;
        while (view.WarmedChunks < view.TotalMapChunks && !view.WarmingSuspended)
        {
            if (timer.Elapsed.TotalMinutes > 5)
                throw new InvalidOperationException("Whole-map preparation did not finish within five minutes.");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await view.AwaitCachePreparation();
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        view.PrintCacheDiagnostics("warm");
        await view.VerifyTerrainHaloReuse();
        GD.Print($"TERMCITY_CACHE_PREPARATION chunks={view.ObjectChunksBuilt - preparing} " +
            $"elapsed={timer.Elapsed.TotalMilliseconds:0.0}ms collected-heap={GC.GetTotalMemory(true)}");
        int built = view.ObjectChunksBuilt;
        bool fits = view.ObjectCacheCount == view.TotalMapChunks;
        if (view.TotalMapChunks <= 15) view.ValidateRetainedGround();
        foreach (var target in targets) await Jump(target, "warm");
        if (fits && view.TotalMapChunks <= 15)
            for (int y = 0; y < map.Height; y += 16)
                for (int x = 0; x < map.Width; x += 16)
                    await Jump(new(x, y), "every-region");
        if (fits && view.ObjectChunksBuilt != built)
            throw new InvalidOperationException("Warm navigation regenerated retained geometry.");
        if (saved is not null && saved != SaveGameStore.Serialize(session.Game))
            throw new InvalidOperationException("Warming changed serialized simulation state.");
        long savedBudget = view.CpuCacheBudgetBytes;
        view.CpuCacheBudgetBytes = 1;
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await view.AwaitCachePreparation();
        view.PrintCacheDiagnostics("pressure");
        view.ValidateTerrainRetentionBudget();
        int pressureBuilt = view.ObjectChunksBuilt;
        for (int frame = 0; frame < 60; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!view.WarmingSuspended || view.ObjectChunksBuilt != pressureBuilt)
            throw new InvalidOperationException("Memory pressure caused a background prepare/evict loop.");
        view.CpuCacheBudgetBytes = savedBudget;
        if (DisplayServer.GetName() == "headless")
            GD.Print("TERMCITY_CACHE_NATIVE_VALIDATION_SKIPPED reason=dummy-renderer backing-arrays=exact");
        if (capture is not null)
        {
            await GodotSmoke.RenderedFrame(host);
            var error = host.GetViewport().GetTexture().GetImage().SavePng(capture);
            if (error != Error.Ok) throw new IOException($"Cache capture failed: {error}.");
        }
        GD.Print($"TERMCITY_CACHE_RETENTION_OK full-map={fits} pressure-loop=false");
        if (saved is not null) await VerifyLifecycle(host, saved);
    }

    private static async Task VerifyLifecycle(Main host, string saved)
    {
        var session = new GameSession(SaveGameStore.Deserialize(saved), showGuide: false);
        var probe = new PerspectiveCity();
        probe.ConfigureCacheExperiment(CacheExperiment.Current);
        host.AddChild(probe);
        try
        {
            probe.Sync(session, host.Map.Size);
            await probe.AwaitFootpathMesh();
            await probe.AwaitCachePreparation();
            await probe.ValidateTerrainPixelsSurviveRoadEdit(session);
            var map = session.Game.Map;
            var tokens = new Dictionary<Pos, object?>();
            for (int y = 0; y < map.Height; y += 16)
                for (int x = 0; x < map.Width; x += 16)
                    tokens[new(x, y)] = probe.CachedObjectToken(new(x, y));
            var cell = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
                .First(pos => map.FeatureAt(pos.X, pos.Y) is not null);
            map.SetFeature(cell.X, cell.Y, null);
            session.Game.Touch();
            int before = probe.ObjectChunksBuilt;
            probe.Sync(session, host.Map.Size);
            await probe.AwaitFootpathMesh();
            await probe.AwaitObjectPrefetch();
            if (probe.ObjectChunksBuilt - before is < 1 or > 9)
                throw new InvalidOperationException("A local feature edit did not stay within its dependency neighborhood.");
            foreach (var (owner, token) in tokens)
                if (Math.Abs(owner.X / 16 - cell.X / 16) > 1 || Math.Abs(owner.Y / 16 - cell.Y / 16) > 1)
                    if (token is not null && !ReferenceEquals(token, probe.CachedObjectToken(owner)))
                        throw new InvalidOperationException("A local edit replaced unrelated retained geometry.");
            session.SetAreaOverlays(true);
            session.SetOverlay(MapOverlay.LandValue);
            probe.Sync(session, host.Map.Size);
            await probe.AwaitFootpathMesh();
            probe.SetActive(false);
            probe.ProcessMode = Node.ProcessModeEnum.Disabled;
            probe.ProcessMode = Node.ProcessModeEnum.Inherit;
            probe.SetActive(true);
            if (DisplayServer.GetName() != "headless")
            {
                await probe.AwaitCompleteFrame();
                probe.BeginMinimapJump(session);
            }
            var replacement = new GameSession(SaveGameStore.Deserialize(saved), showGuide: false);
            probe.Sync(replacement, host.Map.Size);
            probe.PrepareCompleteFrame();
            if (probe.MinimapJumpPending)
                throw new InvalidOperationException("Map replacement did not cancel the old minimap presentation.");
            await probe.AwaitFootpathMesh();
            foreach (var (owner, token) in tokens)
                if (token is not null && ReferenceEquals(token, probe.CachedObjectToken(owner)))
                    throw new InvalidOperationException("Map replacement retained old-map chunks.");
            probe.ValidateRetainedGround();
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
            if (probe.CpuCacheBytes != 0)
                throw new InvalidOperationException("Renderer disposal retained managed cache ownership.");
        }
        GD.Print("TERMCITY_CACHE_LIFECYCLE_OK local-edit=bounded save=unchanged load=isolated mode-switch=cancelled disposal=drained");
    }
}
