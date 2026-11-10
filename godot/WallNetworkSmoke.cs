using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;
using NVector2 = System.Numerics.Vector2;
using System.Reflection;
using System.Diagnostics;

namespace TermCity.GodotApp;

internal static class WallNetworkSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        if (DisplayServer.GetName() == "headless")
            GD.Print("TERMCITY_NATIVE_WALL_BUFFERS_SKIPPED backend=headless encoded=checked graphical=required");
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
                var publication = Stopwatch.StartNew();
                while (!view.CameraPresentationReady || !view.PathPresentationReady ||
                    !view.VisibleTerrainReady || view.ObjectStreamingPending)
                {
                    Require(publication.Elapsed.TotalSeconds < 30,
                        $"Diagonal-wall scene publication did not finish: {view.PreparationPendingDescription}");
                    view.Sync(session, size);
                    view.RequestRender();
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                }
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
                    $"Selecting a diagonal span must highlight the picked stable ID without rebuilding either scene: " +
                    $"highlight={view.HighlightedStructure} expected={wall.Cell} boxes={view.HighlightedBoxCount} " +
                    $"geometry={geometry}/{view.GeometryBuilds} walls={fortifications}/{view.SiegeGeometryBuilds} " +
                    $"reason={view.GeometryInvalidationReason}.");
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
        await VerifyIncrementalGeometry(host, capture);
        var performanceFailures = new List<string>();
        async Task Measure(Func<Task> check)
        {
            try { await check(); }
            catch (PerformanceBudgetExceededException error)
            {
                performanceFailures.Add(error.Message);
                GD.Print("TERMCITY_PERFORMANCE_BUDGET_EXCEEDED " + error.Message);
            }
        }
        await Measure(() => VerifyPlacementLatency(host, game, capture));
        await Measure(() => VerifyPopulatedPlacementLatency(host, capture));
        GD.Print("TERMCITY_WALL_NETWORK_OK angles=arbitrary rotations=4 zooms=3 picking=oriented selection=cached closure=connected+gated undo=atomic save=roundtrip");
        if (performanceFailures.Count > 0)
            throw new PerformanceBudgetExceededException(string.Join(" ", performanceFailures));
    }

    private static async Task VerifyIncrementalGeometry(Main host, string? capture)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 128, MapHeight = 128, Seed = 42, StartingMoney = 2_000_000 });
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Base);
            }
        game.Touch();
        Require(game.FoundSettlement(new(40, 40, 40, 40)).Success, "Incremental rendering needs a real founded bailey.");
        game.Paused = true;
        var session = new GameSession(game, showGuide: false);
        var view = new PerspectiveCity { Name = "IncrementalWallValidation", WaterMotionEnabled = false };
        host.AddChild(view);
        var size = new Vector2(960, 720);
        session.SetViewport(80, 32);
        session.CenterOn(new(20, 105));
        view.Sync(session, size);
        void CompareWithFull(string stage)
        {
            view.VerifyLocalWallTopParity();
            Require(DisplayServer.GetName() == "headless" ? view.EncodedFortificationBuffersMatch : view.FortificationBuffersMatch,
                $"Actual retained MultiMesh transforms must match picking/model data with bounded spare capacity: {stage}; {view.FortificationMismatch}.");
            var geometry = view.FortificationGeometry;
            var tops = view.WallTopGeometry.OrderBy(vertex => vertex.Color).ThenBy(vertex => vertex.Point.X)
                .ThenBy(vertex => vertex.Point.Y).ThenBy(vertex => vertex.Point.Z)
                .ThenBy(vertex => vertex.Uv.X).ThenBy(vertex => vertex.Uv.Y).ToArray();
            view.InvalidateFortificationCache();
            view.Sync(session, size);
            var rebuilt = view.FortificationGeometry;
            Require(geometry.Count == rebuilt.Count && geometry.Zip(rebuilt).All(pair =>
                pair.First.Wall == pair.Second.Wall && pair.First.Color == pair.Second.Color &&
                pair.First.Transform.IsEqualApprox(pair.Second.Transform)),
                $"Incremental picking, wall bodies, weapons and highlights must match a fresh full rebuild: {stage}.");
            var rebuiltTops = view.WallTopGeometry.OrderBy(vertex => vertex.Color).ThenBy(vertex => vertex.Point.X)
                .ThenBy(vertex => vertex.Point.Y).ThenBy(vertex => vertex.Point.Z)
                .ThenBy(vertex => vertex.Uv.X).ThenBy(vertex => vertex.Uv.Y).ToArray();
            Require(tops.SequenceEqual(rebuiltTops),
                $"Incremental cap triangles and pixel-material UVs must match a fresh full rebuild: {stage}.");
            Require(view.WallTopGeometry.Count == 0 || view.WallTopWindingCorrect,
                $"Clipped incremental tops must keep correct winding: {stage}.");
        }
        void Build(NVector2 a, NVector2 b, bool? append = null, bool gate = false)
        {
            int builds = view.SiegeAppendBuilds, before = game.Settlement!.Walls.Count;
            var result = game.BuildWallRun(a, b, gate);
            Require(result.Success, result.Message);
            view.Sync(session, size);
            if (append is { } expected)
                Require((view.SiegeAppendBuilds > builds) == expected,
                    $"Wall rendering must choose the safe incremental/full path: {a} to {b}, expected-append={expected}.");
            if (append == true)
                Require(view.WallsRegeneratedLastSync == game.Settlement.Walls.Count - before,
                    "An unaffected short addition must regenerate only purchased wall components.");
            CompareWithFull($"{a} to {b}");
        }
        try
        {
            Build(new(10.5f, 95.5f), new(13.5f, 95.5f), append: true);
            Build(new(13.5f, 95.5f), new(30.5f, 95.5f), append: true);
            Build(new(30.5f, 95.5f), new(30.5f, 115.5f));
            Build(new(30.5f, 115.5f), new(10.5f, 115.5f));
            Build(new(10.5f, 115.5f), new(10.5f, 95.5f), append: false);
            var gate = game.PlanGate(new(40.5f, 60.5f), Mathf.Pi / 2);
            Build(gate.Start, gate.End, append: false, gate: true);
            var damaged = game.Settlement!.Walls.First(wall => !wall.IsOriginal && !wall.IsTower && !wall.IsGate);
            typeof(WallComponent).GetProperty(nameof(WallComponent.Health))!.SetValue(damaged, damaged.MaxHealth * .25);
            typeof(SettlementState).GetProperty(nameof(SettlementState.Revision))!.SetValue(game.Settlement, game.Settlement.Revision + 1);
            view.Sync(session, size);
            CompareWithFull("damage and fire");
            Require(game.DebugRepairAllBuildings().Success, "Incremental cache tests must repair damaged sections.");
            view.Sync(session, size);
            CompareWithFull("repair");
            game.Map.SetTerrain(damaged.Cell.X, damaged.Cell.Y, game.Map.Content.Terrains.Get("Hill"));
            game.Touch();
            view.Sync(session, size);
            CompareWithFull("terrain foundations");
            typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 1_000);
            Require(game.UpgradeSettlementWalls().Success, "The renderer must invalidate gradual material upgrades.");
            var advance = typeof(SettlementState).GetMethod("AdvanceUpgrades", BindingFlags.Instance | BindingFlags.NonPublic)!;
            game.Paused = false;
            advance.Invoke(game.Settlement, [game, 1d]);
            game.Paused = true;
            view.Sync(session, size);
            CompareWithFull("partially built stone");
            game.Paused = false;
            advance.Invoke(game.Settlement, [game, game.Settlement.WallSections.Count() * SettlementState.UpgradeSecondsPerSection]);
            game.Paused = true;
            view.Sync(session, size);
            CompareWithFull("completed stone");
            Build(new(13.5f, 90.5f), new(16.5f, 90.5f), append: true);
            typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, [SaveGameStore.Deserialize(SaveGameStore.Serialize(game)), true]);
            view.Sync(session, size);
            CompareWithFull("saved game replacement");
            session.PlaceCursor(new(90, 20));
            session.BeginWallPlacement();
            Require(session.ConfirmPreview().Success, "An undo test must set a real wall anchor.");
            session.MoveFixedPlacement(new(93, 20));
            var current = session.Game;
            int walls = current.Settlement!.Walls.Count, money = current.Money;
            current.Map.SetFeature(91, 20, current.Map.Content.Features.Get("Tree"));
            current.Touch();
            view.Sync(session, size);
            int cityBuilds = view.GeometryBuilds;
            Require(session.ConfirmPreview().Success, "An undo test must purchase the whole previewed wall.");
            view.Sync(session, size);
            Require(current.Map.FeatureAt(91, 20) is null && view.GeometryBuilds > cityBuilds,
                "A wall that clears a tree must still rebuild changed city artwork.");
            CompareWithFull("feature clearing");
            session.RequestUndo();
            session.SelectPrompt(0);
            view.Sync(session, size);
            Require(session.Game.Money == money && session.Game.Settlement!.Walls.Count == walls &&
                session.Game.Map.FeatureAt(91, 20) is not null && session.WallToolActive,
                "Undo must restore the wall purchase, gold, tree and chained placement tool atomically.");
            CompareWithFull("undo");
            session.CancelPreview();
            session.ClosePrompt();
            await Frames(host);
            Capture(view, capture is null ? null : Path.ChangeExtension(capture, ".incremental-stone.png"));
        }
        finally
        {
            host.RemoveChild(view);
            view.QueueFree();
            await Frames(host);
        }
        GD.Print("TERMCITY_INCREMENTAL_WALL_GEOMETRY_OK append=net-new full=closure+gate+damage+terrain+upgrade+load caps=exact picking=exact uv=exact");
    }

    private static async Task VerifyPopulatedPlacementLatency(Main host, string? capture)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 256, MapHeight = 256, Seed = 42, StartingMoney = 2_000_000 });
        var map = game.Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, map.Content.Terrains.Base);
            }
        game.Touch();
        Require(game.FoundSettlement(new(40, 40, 128, 128)).Success, "The populated click fixture must found a real bailey.");
        int remaining = 1_000 - game.Stats.Population;
        var cottage = map.Content.Buildings.Get("Cottage");
        foreach (var plot in game.VacantPlots(ZoneType.Residential))
        {
            if (remaining <= 0) break;
            int people = Math.Min(remaining, cottage.Capacity);
            map.SetBuildingFootprint(cottage, plot);
            map.SetHousehold(plot.X, plot.Y,
                new Household((byte)Math.Min(4, people), (byte)Math.Max(0, people - 4), 0));
            remaining -= people;
        }
        Require(remaining == 0, "The populated wall fixture must house 1,000 real residents.");
        game.Touch();
        Require(game.DebugRemoveGoblins().Success, "The populated wall fixture must remove attackers before measuring placement.");
        game.Paused = true;
        var fullControl = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        var failures = new List<string>();
        foreach (bool forceFull in new[] { true, false })
        {
            try
            {
                await VerifyPlacementLatency(host, forceFull ? fullControl : game,
                    capture is null ? null : Path.ChangeExtension(capture,
                        forceFull ? ".populated-full.png" : ".populated.png"),
                    towerChecks: false, forceFullRendering: forceFull);
            }
            catch (PerformanceBudgetExceededException error)
            {
                failures.Add(error.Message);
                GD.Print("TERMCITY_PERFORMANCE_BUDGET_EXCEEDED " + error.Message);
            }
        }
        if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
    }

    private static async Task VerifyPlacementLatency(Main host, CityGame game, string? capture, bool towerChecks = true,
        bool forceFullRendering = false)
    {
        var session = host.Session;
        var originalGame = session.Game;
        var replace = typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        replace.Invoke(session, [game, true]);
        host.Map.SetPerspective(true);
        session.ClosePrompt();
        session.SetZoom(0);
        session.CenterOn(new(87, 20));
        host.Map.RefreshCells();
        await Frames(host);
        var view = host.Map.Perspective!;
        void Click(Vector2 point)
        {
            foreach (bool pressed in new[] { true, false })
                host.Map.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = pressed });
        }
        try
        {
            if (towerChecks) await VerifyTowerAnchor(host, capture);
            host.Map.RefreshCells();
            view.Rotate(PerspectiveCity.InitialAzimuth - view.Azimuth);
            view.ResetCameraOffset();
            session.CenterOn(new(87, 20));
            await Frames(host);
            session.BeginWallPlacement();
            session.MoveFixedPlacement(new(84, 20));
            view.Sync(session, host.Map.Size);
            var start = view.ScreenPoint(new(84.5f, view.RenderedGroundHeight(game.Map, 84.5f, 20.5f), 20.5f));
            Click(start);
            Require(session.Preview?.WallPlan is not null, "A real wall click must choose its first anchor.");
            await Frames(host);
            double worst = 0, worstInput = 0;
            foreach (int end in new[] { 87, 90, 93 })
            {
                session.MoveFixedPlacement(new(end, 20));
                await Frames(host);
                Require(session.Preview?.WallPlan is { Valid: true }, "Short wall latency needs a valid three-cell preview.");
                int geometry = view.GeometryBuilds;
                int walls = game.Settlement!.Walls.Count;
                var existingIds = game.Settlement.Walls.Select(wall => wall.Id).ToHashSet();
                int appends = view.SiegeAppendBuilds;
                view.Sync(session, host.Map.Size);
                var point = view.ScreenPoint(new(end + .5f, view.RenderedGroundHeight(game.Map, end + .5f, 20.5f), 20.5f));
                double services = game.ServiceComputeMilliseconds;
                var watch = Stopwatch.StartNew();
                Click(point);
                double input = watch.Elapsed.TotalMilliseconds;
                worstInput = Math.Max(worstInput, input);
                Require(game.Settlement.Walls.Count > walls,
                    $"The measured click must actually purchase the wall: message={session.Message}, cursor={session.Cursor}, " +
                    $"preview={session.Preview?.WallPlan}, pointer={point}.");
                if (forceFullRendering) view.InvalidateFortificationCache();
                view.Sync(session, host.Map.Size);
                Require(forceFullRendering ? view.WallsRegeneratedLastSync == game.Settlement.Walls.Count :
                    view.SiegeAppendBuilds > appends && view.WallsRegeneratedLastSync == game.Settlement.Walls.Count - walls,
                    "Timed wall clicks must use the requested full-rebuild control or incremental path.");
                view.RequestRender();
                host.Map.QueueRedraw();
                if (DisplayServer.GetName() == "headless")
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                else
                {
                    var drawn = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    RenderingServer.ForceDraw();
                    await drawn;
                }
                double rendered = watch.Elapsed.TotalMilliseconds;
                worst = Math.Max(worst, rendered);
                Require(DisplayServer.GetName() == "headless" ? view.EncodedFortificationBuffersMatch : view.FortificationBuffersMatch,
                    "Timed wall clicks must display the exact retained instance transforms.");
                Require(game.Settlement.Walls.Where(wall => !existingIds.Contains(wall.Id)).All(wall =>
                    new Rect2(Vector2.Zero, host.Map.Size).HasPoint(view.ScreenPoint(view.WallTop(wall)))),
                    "Click-to-render measurements must show the purchased wall in the actual visible viewport.");
                var timings = game.LastWallRunTimings;
                GD.Print($"TERMCITY_WALL_PURCHASE_STAGES render={(forceFullRendering ? "full-control" : "incremental")} " +
                    $"population={game.Stats.Population} length=3 end={end} " +
                    $"snapshot={session.LastActionSnapshotMilliseconds:0.0}ms validation={timings.Validation:0.0}ms " +
                    $"mutation={timings.Mutation:0.0}ms notify={timings.Notification:0.0}ms " +
                    $"services-in-notify={game.ServiceComputeMilliseconds - services:0.0}ms territory={timings.Territory:0.0}ms " +
                    $"preview-with-ui={session.LastWallPreviewMilliseconds:0.0}ms input={input:0.0}ms " +
                    $"wall-sync={view.SiegeGeometryBuildMilliseconds:0.0}ms first-render={rendered:0.0}ms " +
                    $"regenerated={view.WallsRegeneratedLastSync} total-walls={game.Settlement.Walls.Count}");
                Require(view.GeometryBuilds == geometry, "Short wall clicks must preserve cached city geometry.");
            }
            GD.Print($"TERMCITY_SHORT_WALL_LATENCY render={(forceFullRendering ? "full-control" : "incremental")} " +
                $"population={game.Stats.Population} input={worstInput:0.0}ms click-to-render={worst:0.0}ms " +
                $"wall-sync={view.SiegeGeometryBuildMilliseconds:0.0}ms budget=100ms runs=3 length=3");
            await SettlementSmoke.Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".short-walls.png"));
            if (DisplayServer.GetName() != "headless" && worst >= 100)
                throw new PerformanceBudgetExceededException($"Short wall click-to-render ({game.Stats.Population} residents, " +
                    $"{(forceFullRendering ? "full control" : "incremental")}) took {worst:0.0}ms; budget is 100ms.");
        }
        finally
        {
            session.CancelPreview();
            session.ClosePrompt();
            replace.Invoke(session, [originalGame, true]);
            host.Map.Invalidate(true);
            await Frames(host);
        }
    }

    private static async Task VerifyTowerAnchor(Main host, string? capture)
    {
        var session = host.Session;
        var view = host.Map.Perspective!;
        var tower = session.Game.Settlement!.Walls.First(wall => wall.JunctionTower && wall.Health == wall.MaxHealth);
        session.CenterOn(tower.Cell);
        view.Rotate(45 - view.Azimuth);
        await Frames(host);
        view.Sync(session, host.Map.Size);
        var point = view.ScreenPoint(view.WallTop(tower) + Vector3.Up * .03f);
        Require(host.Map.TryPickBuilding(point, out _) && host.Map.PickedWallId == tower.Id,
            "Tower anchoring must test a frontmost point on the real tower roof, not its ground cell.");
        session.BeginWallPlacement();
        foreach (bool pressed in new[] { true, false })
            host.Map.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = pressed });
        Require(session.Preview?.WallPlan?.Start == tower.Center,
            "Clicking the visible tower must start its wall at the exact fractional center.");
        await Frames(host);
        int index = 0;
        foreach (int zoom in new[] { -1, 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            for (int turn = 0; turn < 4; turn++)
            {
                view.Rotate(90);
                session.CenterOn(tower.Cell);
                await Frames(host);
                view.Sync(session, host.Map.Size);
                var roofPlane = view.GroundPlanePoint(view.ScreenPoint(view.WallTop(tower)));
                var centerPlane = view.GroundPlanePoint(host.Map.Size / 2);
                view.SetCameraOffset(session, view.CameraOffset +
                    new Vector2(roofPlane.X - centerPlane.X, roofPlane.Z - centerPlane.Z));
                view.Sync(session, host.Map.Size);
                await Frames(host);
                int geometry = view.GeometryBuilds;
                foreach (var direction in new[] { NVector2.UnitX, NVector2.UnitY, -NVector2.UnitX, -NVector2.UnitY })
                {
                    view.Sync(session, host.Map.Size);
                    var target = tower.Center + direction * 1.5f;
                    var pointer = view.ScreenPoint(new(target.X, view.WallTop(tower).Y, target.Y));
                    host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = pointer });
                    var plan = session.Preview!.WallPlan!;
                    Require(plan.Start == tower.Center && NVector2.DistanceSquared(plan.End, plan.Start) > .1f &&
                        NVector2.Dot(NVector2.Normalize(plan.End - plan.Start), direction) > .999f,
                        $"The wall preview must pivot to every side of the start tower: zoom={zoom}, turn={turn}, direction={direction}, " +
                        $"start={plan.Start}, end={plan.End}, cursor={session.Cursor}, pointer={pointer}, map={host.Map.Size}.");
                    await Frames(host);
                    if (zoom == 0 && turn == 0)
                        await SettlementSmoke.Capture(host, capture is null ? null :
                            Path.ChangeExtension(capture, $".tower-pivot-{index++}.png"));
                }
                Require(view.GeometryBuilds == geometry, "Rotating a tower-anchored wall preview must not rebuild the existing city.");
            }
        }
        session.SetZoom(0);
        session.CancelPreview();
        session.ClosePrompt();
        GD.Print("TERMCITY_TOWER_ANCHOR_OK pick=real-roof center=exact sides=4 rotations=4 zooms=3 origin=persistent previews=cached");
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
