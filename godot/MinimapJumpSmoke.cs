using System.Diagnostics;
using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

internal static class MinimapJumpSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        if (DisplayServer.GetName() == "headless")
            throw new InvalidOperationException("Minimap frame-publication checks require a graphical display.");
        host.GetWindow().Size = new Vector2I(960, 800);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var session = host.Session;
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        view.ConfigureCacheExperiment(CacheExperiment.Current);
        view.TrackCacheMemory();
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        await view.AwaitCompleteFrame();
        var map = session.Game.Map;
        var home = map.BuildingFootprints.Count > 0
            ? map.BuildingFootprints.Values.OrderBy(area => Math.Abs(area.X - map.Width / 2) +
                Math.Abs(area.Y - map.Height / 2)).Select(area => new Pos(area.X, area.Y)).First()
            : new Pos(map.Width / 2, map.Height / 2);
        var away = new Pos(map.Width / 5, map.Height / 5);
        var failures = new List<string>();
        var destinations = new Dictionary<Pos, PerspectiveCity.NativeDestination>();
        int frozenFrames = 0;

        void Click(Pos target)
        {
            var pointer = host.Minimap.ScreenPoint(target);
            using var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer };
            using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pointer };
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, press);
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, release);
        }

        async Task<double> Jump(Pos target, string phase, bool warm = false)
        {
            await view.AwaitCompleteFrame();
            int built = view.ObjectChunksBuilt, textures = view.GroundTexturePacketsBuilt, ground = view.GroundChunksUploaded;
            long uploaded = view.ObjectUploadBytes;
            var watch = Stopwatch.StartNew();
            Click(target);
            double handler = watch.Elapsed.TotalMilliseconds;
            if (!view.MinimapJumpPending) throw new InvalidOperationException("A minimap jump did not freeze the presented picture.");
            var texture = view.PresentedTexture;
            using var old = texture.GetImage();
            byte[] retained = old.GetData();
            int colored = 0;
            for (int y = 0; y < old.GetHeight(); y += 8)
                for (int x = 0; x < old.GetWidth(); x += 8)
                {
                    var color = old.GetPixel(x, y);
                    if (Math.Max(color.R, Math.Max(color.G, color.B)) > .05f) colored++;
                }
            if (colored < old.GetWidth() * old.GetHeight() / 128)
                throw new InvalidOperationException("The retained complete picture is blank rather than the previous map.");
            if (host.Map.TryMapPosition(host.Map.Size / 2, out _) ||
                host.Map.TryPickBuilding(host.Map.Size / 2, out _) ||
                host.Map.TryPickFeature(host.Map.Size / 2, out _) ||
                host.Map.TryPickSiegeActor(host.Map.Size / 2, out _, out _))
                throw new InvalidOperationException("Map picking was enabled against a pending camera.");
            var cursor = session.Cursor;
            var money = session.Game.Money;
            foreach (var code in new[] { Key.Enter, Key.R, Key.Y, Key.Right })
            {
                using var key = new InputEventKey { Pressed = true, Keycode = code };
                host._UnhandledInput(key);
            }
            if (session.Cursor != cursor || session.Game.Money != money || session.Prompt is not null)
                throw new InvalidOperationException("A pending minimap picture allowed keyboard inspection or placement.");
            while (view.MinimapJumpPending)
            {
                if (watch.Elapsed.TotalSeconds > 20) throw new InvalidOperationException("Minimap jump publication stalled.");
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                if (!view.MinimapJumpPending) break;
                using var current = view.PresentedTexture.GetImage();
                if (view.PresentedTexture != texture || !current.GetData().SequenceEqual(retained))
                    throw new InvalidOperationException("A pending minimap jump displayed a blank or changing replacement frame.");
                frozenFrames++;
                if (capture is not null && frozenFrames == 1)
                {
                    using var screen = host.GetViewport().GetTexture().GetImage();
                    var error = screen.SavePng(Path.ChangeExtension(capture, ".loading.png"));
                    if (error != Error.Ok) throw new IOException($"Pending minimap capture failed: {error}.");
                }
            }
            await view.AwaitFootpathMesh();
            await GodotSmoke.RenderedFrame(host);
            double ready = view.MinimapJumpMilliseconds;
            view.ValidateObjectBuffers();
            if (warm) view.ValidateNativeDestinationReuse(destinations[target]);
            destinations[target] = view.CaptureNativeDestination();
            if (handler >= 50) failures.Add($"{phase} input handler {handler:0.0}ms exceeds 50ms.");
            if (warm && ready >= 250) failures.Add($"{phase} warm readiness {ready:0.0}ms exceeds 250ms.");
            GD.Print($"TERMCITY_JUMP_SAMPLE phase={phase} handler={handler:0.0}ms ready={ready:0.0}ms " +
                $"built={view.ObjectChunksBuilt - built} textures={view.GroundTexturePacketsBuilt - textures} " +
                $"ground-uploads={view.GroundChunksUploaded - ground} object-bytes={view.ObjectUploadBytes - uploaded}");
            return ready;
        }

        await Jump(home, "home-cold");
        await Jump(away, "away-cold");
        await Jump(home, "home-warm", warm: true);
        await Jump(away, "away-warm", warm: true);
        for (int repeat = 0; repeat < 3; repeat++)
        {
            await Jump(home, $"home-repeat-{repeat}", warm: true);
            await Jump(away, $"away-repeat-{repeat}", warm: true);
        }
        await view.AwaitCompleteFrame();
        int published = view.MinimapJumpsPublished, generation = view.MinimapJumpGeneration;
        Click(home); Click(away); Click(home);
        if (view.MinimapJumpGeneration != generation + 3) throw new InvalidOperationException("Rapid destinations were not coalesced.");
        view.Rotate(90);
        session.ZoomBy(-1);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        await view.AwaitFootpathMesh();
        if (view.MinimapJumpsPublished != published + 1)
            throw new InvalidOperationException("An obsolete minimap destination published during rapid jumps/rotation/zoom.");
        view.ValidateObjectBuffers();
        view.PrintCacheDiagnostics("jump-return");
        if (capture is not null)
        {
            await GodotSmoke.RenderedFrame(host);
            var error = host.GetViewport().GetTexture().GetImage().SavePng(capture);
            if (error != Error.Ok) throw new IOException($"Minimap jump capture failed: {error}.");
        }
        session.Game.Paused = false;
        await Jump(home, "running-home");
        await Jump(away, "running-away");
        session.Game.Paused = true;
        while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        view.GpuCacheBudgetBytes = 1;
        await view.AwaitObjectPrefetch();
        for (int frame = 0; frame < 3; frame++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        view.PrintCacheDiagnostics("jump-pressure");
        view.ValidateGpuResidencyBudget();
        view.ValidatePendingJumpLifecycle(session);
        GD.Print($"TERMCITY_JUMP_CORRECTNESS_OK frozen-frames={frozenFrames} native-return=no-reupload " +
            "picking=blocked keyboard-actions=blocked coalescing=latest rotation-zoom=atomic pressure=pins-only lifecycle=cancelled");
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
    }
}
