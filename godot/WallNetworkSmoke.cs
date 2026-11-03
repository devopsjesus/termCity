using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;
using NVector2 = System.Numerics.Vector2;
using System.Reflection;
using System.Diagnostics;

namespace TermCity.GodotApp;

internal static class WallNetworkSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        await WallWaterSmoke.Run(host, capture);
        var game = CityGame.New(new GameConfig { MapWidth = 128, MapHeight = 128, Seed = 42, StartingMoney = 2_000_000 });
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Base);
            }
        game.Touch();
        Require(game.FoundSettlement(new(40, 40, 40, 40)).Success, "The wall-network fixture must found a real bailey.");
        game.Paused = true;
        var session = new GameSession(game);
        var state = game.Settlement!;
        var view = new PerspectiveCity { Name = "WallNetworkValidation", WaterMotionEnabled = false };
        host.AddChild(view);
        var size = new Vector2(960, 720);
        session.SetViewport(80, 32);
        void SyncAt(Vector2 center)
        {
            view.Sync(session, size);
            view.SetCameraOffset(session, center - new Vector2(
                session.CameraX + session.ViewWidth * session.Stride / 2f,
                session.CameraY + session.ViewHeight * session.Stride / 2f));
            view.Sync(session, size);
        }
        void Build(NVector2 a, NVector2 b, bool gate = false)
        {
            var result = game.BuildWallRun(a, b, gate);
            Require(result.Success, result.Message);
        }
        Build(new(20.5f, 25.5f), new(35.5f, 35.5f));
        Build(new(35.5f, 35.5f), new(20.5f, 45.5f));
        Build(new(20.5f, 45.5f), new(5.5f, 35.5f));
        Build(new(5.5f, 35.5f), new(20.5f, 25.5f));
        foreach (var span in state.Walls.Where(w => !w.IsOriginal && !w.IsTower && !w.IsGate))
        {
            var normal = new NVector2(-span.Tangent.Y, span.Tangent.X);
            Require(WallDamageLayout.Fragments(span).Where(part => part.Kind == WallFragmentKind.Trim)
                .All(part => state.WallNetwork.ContainsAny(span.Center + normal * part.Center.Z)),
                "Every closed diagonal-wall catwalk must face the real enclosed interior.");
        }
        Require(!state.AcceptsArrivals(game, new(20, 35)), "An isolated diamond cannot attract residents.");
        var wall = state.Walls.First(w => w.Start is not null && !w.IsTower &&
            state.Walls.Where(t => t.IsTower).All(t => NVector2.Distance(t.Center, w.Center) > 6));
        foreach (int zoom in new[] { -1, 0, 1 })
            for (int turn = 0; turn < 4; turn++)
            {
                session.SetZoom(zoom);
                view.Rotate(90);
                session.CenterOn(new(20, 35));
                SyncAt(new(wall.Center.X, wall.Center.Y));
                await Frames(host);
                Require(view.TimberWallPartsDrawn > state.Walls.Count * 5,
                    "Real timber settlement walls and towers must use individual palisade logs, not slab placeholders.");
                Require(view.WallTopWindingCorrect, "Clipped wall/tower decks must face upward under backface culling.");
                var tangent = new Vector3(wall.Tangent.X, 0, wall.Tangent.Y);
                var normal = new Vector3(-wall.Tangent.Y, 0, wall.Tangent.X);
                var origin = new Vector3(wall.Center.X, 0, wall.Center.Y);
                var points = WallDamageLayout.Fragments(wall).Where(p => p.Kind == WallFragmentKind.Body).Select(p =>
                    origin + tangent * p.Center.X + normal * p.Center.Z +
                    Vector3.Up * (p.Center.Y + p.Size.Y / 2 - .02f)).ToArray();
                bool picked = points.Any(point => new Rect2(Vector2.Zero, size).HasPoint(view.ScreenPoint(point)) &&
                    view.PickBuilding(view.ScreenPoint(point), out _) && view.PickedWallId == wall.Id);
                if (!picked)
                    foreach (var candidate in state.Walls.Where(w => w.Start is not null && !w.IsTower))
                    {
                        var direction = new Vector3(candidate.Tangent.X, 0, candidate.Tangent.Y);
                        var across = new Vector3(-candidate.Tangent.Y, 0, candidate.Tangent.X);
                        var root = new Vector3(candidate.Center.X, 0, candidate.Center.Y);
                        picked = WallDamageLayout.Fragments(candidate).Any(part =>
                        {
                            var point = root + direction * part.Center.X + across * part.Center.Z +
                                Vector3.Up * (part.Center.Y + part.Size.Y / 2 - .02f);
                            var screen = view.ScreenPoint(point);
                            return new Rect2(Vector2.Zero, size).HasPoint(screen) &&
                                view.PickBuilding(screen, out _) && view.PickedWallId == candidate.Id;
                        });
                        if (picked) { wall = candidate; break; }
                    }
                Require(picked, $"A frontmost visible diagonal span must be picked at zoom {zoom}, rotation {turn}; towers may occlude rear spans.");
                int geometry = view.GeometryBuilds, fortifications = view.SiegeGeometryBuilds;
                session.PlaceCursor(wall.Cell);
                session.RememberWallPick(wall.Id);
                session.SelectCell(wall.Cell);
                SyncAt(new(wall.Center.X, wall.Center.Y));
                Require(view.HighlightedStructure == wall.Cell && view.HighlightedBoxCount > 0 &&
                    view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == fortifications,
                    "Selecting a diagonal span must highlight the picked stable ID without rebuilding either scene.");
                session.ClearSelection();
                SyncAt(new(20.5f, 35.5f));
                await Frames(host);
                if (zoom == 0) Capture(view, capture is null ? null : Path.ChangeExtension(capture, $".diagonal-{turn}.png"));
            }
        var gatePlan = game.PlanGate(new(28, 30.5f), game.GateAngleAt(new(28, 30.5f)));
        Require(gatePlan.Valid, gatePlan.Error ?? "The angled gate must fit.");
        Build(gatePlan.Start, gatePlan.End, true);
        Build(new(35.5f, 35.5f), new(40.5f, 45.5f));
        Require(state.AcceptsArrivals(game, new(20, 35)),
            "The closed diamond must become eligible after wall connection and real gate access.");
        var gateWall = state.Walls.Last(w => w.IsGate);
        session.PlaceCursor(gateWall.Cell);
        session.RememberWallPick(gateWall.Id);
        session.SelectCell(gateWall.Cell);
        session.SetZoom(0);
        session.CenterOn(gateWall.Cell);
        SyncAt(new(gateWall.Center.X, gateWall.Center.Y));
        await Frames(host);
        Capture(view, capture is null ? null : Path.ChangeExtension(capture, ".angled-gate.png"));
        Require(view.AccessLadderWallIds.All(id => state.Walls.Any(w => w.Id == id && WallDamageLayout.HasAccessLadder(w))) &&
            !view.AccessLadderWallIds.Contains(gateWall.Id),
            "Connected angled gates must not generate access ladders.");
        typeof(CityGame).GetProperty(nameof(game.HighestPopulation))!.SetValue(game, 1_000);
        typeof(CityGame).GetProperty(nameof(game.HighestMilestone))!.SetValue(game, 1_000);
        var generatedTower = state.Walls.First(w => w.JunctionTower);
        foreach (var kind in new[] { WallDefenseKind.OilCauldron, WallDefenseKind.Rocks,
            WallDefenseKind.Ballista, WallDefenseKind.Trebuchet })
        {
            session.PlaceCursor(generatedTower.Cell);
            session.RememberWallPick(generatedTower.Id);
            session.SelectCell(generatedTower.Cell);
            session.PreviewWallDefense(kind);
            Require(session.FixedPlacementError is null && session.SelectedWallComponent?.Id == generatedTower.Id,
                $"{kind} must preview on the generated tower's full platform, not its logical anchor span.");
            var mounted = session.ConfirmPreview();
            Require(mounted.Success && generatedTower.Defense == kind, mounted.Message);
            if (kind == WallDefenseKind.OilCauldron)
            {
                foreach (var footprint in DropAttackLayout.Footprints(generatedTower, kind))
                {
                    int x = (int)MathF.Floor(footprint.Center.X), y = (int)MathF.Floor(footprint.Center.Y);
                    game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Hill"));
                }
                game.Touch();
            }
            session.SelectCell(generatedTower.Cell);
            session.CenterOn(generatedTower.Cell);
            SyncAt(new(generatedTower.Center.X, generatedTower.Center.Y));
            await Frames(host);
            if (kind == WallDefenseKind.OilCauldron)
                Require(view.WeaponRangeHeights.Count > 0 &&
                    view.WeaponRangeHeights.Max() - view.WeaponRangeHeights.Min() > .1f,
                    "The rendered oil guide must cover distinct real terrace layers, not a single plane.");
            if (capture is not null)
            {
                var camera = (Camera3D)typeof(PerspectiveCity).GetField("_camera",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                var transform = camera.Transform;
                float span = camera.Size;
                var target = new Vector3(generatedTower.Center.X,
                    WallDamageLayout.TopHeight(generatedTower) / 2, generatedTower.Center.Y);
                float azimuth = Mathf.DegToRad(view.Azimuth), elevation = Mathf.DegToRad(view.Elevation);
                camera.Position = target + new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation),
                    Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation)) * 100;
                camera.LookAt(target, Vector3.Up);
                camera.Size = 12;
                camera.MakeCurrent();
                camera.ForceUpdateTransform();
                view.RequestRender();
                await Frames(host);
                view.RequestRender();
                var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                RenderingServer.ForceDraw(false);
                await rendered;
                Capture(view, Path.ChangeExtension(capture, $".generated-tower-{kind}.png"));
                camera.Transform = transform;
                camera.Size = span;
                camera.ForceUpdateTransform();
                view.RequestRender();
            }
            Require(game.PlaceWallDefense(generatedTower.Cell, WallDefenseKind.None, generatedTower.Id).Success,
                "The generated tower's mounted defense must remain removable.");
        }
        GD.Print("TERMCITY_GENERATED_TOWER_WEAPONS_OK kinds=4 preview=valid purchase=mounted removal=retained");
        var save = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(save);
        Require(restored.Settlement!.Walls.Count == state.Walls.Count &&
            restored.Settlement.AcceptsArrivals(restored, new(20, 35)),
            "Network geometry, gate access and territory must survive save/load.");
        var sampleEnemy = state.Walls.First(w => w.IsTower);
        var enemy = new SiegeEnemy();
        typeof(SiegeEnemy).GetProperty(nameof(SiegeEnemy.Kind))!.SetValue(enemy, SiegeEnemyKind.Goblin);
        typeof(SiegeEnemy).GetProperty(nameof(SiegeEnemy.Tier))!.SetValue(enemy, 1);
        typeof(SiegeEnemy).GetProperty(nameof(SiegeEnemy.X))!.SetValue(enemy, 20.5d);
        typeof(SiegeEnemy).GetProperty(nameof(SiegeEnemy.Y))!.SetValue(enemy, 35.5d);
        typeof(SiegeEnemy).GetProperty(nameof(SiegeEnemy.Health))!.SetValue(enemy, 1d);
        var damage = typeof(SettlementState).GetMethod("DamageEnemy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        damage.Invoke(state, [enemy, 1d, SiegeDeathCause.Arrow, sampleEnemy.Center]);
        typeof(SettlementState).GetProperty(nameof(SettlementState.Revision))!.SetValue(state, state.Revision + 1);
        SyncAt(new((float)enemy.X, (float)enemy.Y));
        await Frames(host);
        Require(view.DeathsDrawn == 1, "An arrow death must remain visible after combat removes the living actor.");
        Capture(view, capture is null ? null : Path.ChangeExtension(capture, ".death-hit.png"));
        typeof(SettlementState).GetMethod("AdvanceDeaths", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [.7d]);
        typeof(SettlementState).GetProperty(nameof(SettlementState.Revision))!.SetValue(state, state.Revision + 1);
        SyncAt(new((float)enemy.X, (float)enemy.Y));
        await Frames(host);
        Capture(view, capture is null ? null : Path.ChangeExtension(capture, ".death-fall.png"));
        typeof(SettlementState).GetMethod("AdvanceDeaths", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [2d]);
        typeof(SettlementState).GetProperty(nameof(SettlementState.Revision))!.SetValue(state, state.Revision + 1);
        view.Sync(session, size);
        Require(view.DeathsDrawn == 0, "Completed death animation must release the transient corpse.");
        session.BeginWallPlacement();
        session.MoveFixedPlacement(new(20, 60));
        Require(session.ConfirmPreview().Success, "The wall tool must accept its first anchor without spending.");
        session.MoveFixedPlacement(new(15, 70));
        int before = game.Money;
        SyncAt(new(18, 65));
        await Frames(host);
        Require(session.Preview!.WallPlan is { Valid: true }, "The extension must have an exact, valid preview.");
        Capture(view, capture is null ? null : Path.ChangeExtension(capture, ".preview.png"));
        Require(session.ConfirmPreview().Success && game.Money < before && session.WallToolActive,
            "Confirming a run must atomically buy it and retain the chained wall tool.");
        int count = state.Walls.Count;
        session.RequestUndo();
        session.SelectPrompt(0);
        Require(session.Game.Settlement!.Walls.Count < count && session.WallToolActive,
            "Undo must restore the whole run and resume the wall tool safely.");
        session.CancelPreview();
        session.ClosePrompt();
        session.ClearSelection();
        session.SetZoom(0);
        await VerifyPalisadePerformance(host, view, () => SyncAt(new(20.5f, 35.5f)));
        host.RemoveChild(view);
        view.QueueFree();
        await Frames(host);
        await VerifyTimberThumbnails(host, capture);
        GD.Print("TERMCITY_WALL_NETWORK_OK angles=arbitrary rotations=4 zooms=3 picking=oriented selection=cached closure=connected+gated undo=atomic save=roundtrip");
    }

    private static async Task VerifyPalisadePerformance(Main host, PerspectiveCity view, Action sync)
    {
        view.SiegeAnimationEnabled = false;
        sync();
        await Frames(host);
        int geometry = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
        var frames = new List<double>();
        double worstSync = 0, worstPick = 0;
        for (int i = 0; i < 16; i++)
        {
            long frame = Stopwatch.GetTimestamp();
            view.Rotate(1);
            long start = Stopwatch.GetTimestamp();
            sync();
            worstSync = Math.Max(worstSync, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = Stopwatch.GetTimestamp();
            view.PickBuilding(view.DisplaySize / 2, out _);
            worstPick = Math.Max(worstPick, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (i >= 2) frames.Add(Stopwatch.GetElapsedTime(frame).TotalMilliseconds);
        }
        Require(view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == walls,
            "Small camera movements must reuse cached palisade geometry.");
        frames.Sort();
        double p90 = frames[(int)(frames.Count * .9)];
        var failures = new List<string>();
        if (worstSync >= 50) failures.Add($"Palisade camera sync took {worstSync:0.0}ms; budget is 50ms.");
        if (worstPick >= 50) failures.Add($"Palisade picking took {worstPick:0.0}ms; budget is 50ms.");
        if (DisplayServer.GetName() != "headless" && p90 >= 60)
            failures.Add($"Palisade frame p90 was {p90:0.0}ms; budget is 60ms.");
        if (view.SceneObjectCount >= 512) failures.Add($"Palisades used {view.SceneObjectCount} render nodes; budget is 512.");
        GD.Print($"TERMCITY_PALISADE_PERFORMANCE_{(failures.Count == 0 ? "OK" : "OVER_BUDGET")} " +
            $"display={DisplayServer.GetName()} parts={view.TimberWallPartsDrawn} nodes={view.SceneObjectCount} " +
            $"sync={worstSync:0.0}ms pick={worstPick:0.0}ms frame-p90={p90:0.0}ms");
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
    }

    private static async Task VerifyTimberThumbnails(Main host, string? capture)
    {
        var panel = new TerminalFrame { Position = new(90, 140), Size = new(380, 220) };
        var previews = new HBoxContainer();
        panel.AddChild(previews);
        foreach (bool gate in new[] { false, true })
        {
            var preview = host.CreateModelPreview(gate ? "PalisadeGateValidation" : "PalisadeWallValidation",
                wall: true, gate: gate);
            preview.CustomMinimumSize = new(170, 170);
            previews.AddChild(preview);
        }
        host.AddChild(panel);
        try
        {
            await Frames(host);
            await GodotSmoke.VerifyModelPreviews(host, panel);
            await SettlementSmoke.Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".timber-thumbnails.png"));
        }
        finally
        {
            host.RemoveChild(panel);
            panel.QueueFree();
            await Frames(host);
        }
        GD.Print("TERMCITY_TIMBER_WALL_THUMBNAILS_OK subjects=wall+gate canonical=palisade cache=shared");
    }

    private static void Capture(PerspectiveCity view, string? path)
    {
        if (path is null) return;
        Require(DisplayServer.GetName() != "headless", "Wall captures require a graphical display.");
        using var image = view.CaptureDisplayImage();
        Require(image.SavePng(path) == Error.Ok, "The wall-network capture must save.");
    }
}
