using System.Diagnostics;
using Godot;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class CameraPerformanceSmoke
{
    public static async Task Run(Main host, string? capture, bool loadedSave = false, int profileSeconds = 45,
        GameSpeed profileSpeed = GameSpeed.Medium, bool quitTest = false)
    {
        var session = host.Session;
        var game = session.Game;
        if (loadedSave && game.Settlement is not null)
        {
            await VerifySavedCombat(host, capture, profileSeconds, profileSpeed, quitTest);
            return;
        }
        game.Paused = true;
        var budgetFailures = new List<string>();
        game.ProfileUpdates = true;
        session.ClosePrompt();
        session.ClearSelection();
        session.ClearDefenderSelection();
        session.SetOverlay(TermCity.Core.Rendering.MapOverlay.Off);
        session.SetAreaOverlays(false);
        host.Map.SetPerspective(true);
        session.SetZoom(0);
        if (game.Settlement is { } town)
        {
            Require(game.DebugRemoveGoblins().Success, "Camera profiling needs a peaceful comparison.");
            session.CenterOn(new(town.Bounds.X + town.Bounds.Width / 2, town.Bounds.Y + town.Bounds.Height / 2));
        }

        static async Task VerifySavedCombat(Main host, string? capture, int profileSeconds, GameSpeed profileSpeed, bool quitTest)
        {
            Require(DisplayServer.GetName() != "headless", "Saved combat cadence measures actual rendered frames.");
            var session = host.Session;
            var game = session.Game;
            game.Paused = true;
            game.ProfileUpdates = true;
            session.ClosePrompt();
            session.ClearSelection();
            session.ClearDefenderSelection();
            session.SetOverlay(TermCity.Core.Rendering.MapOverlay.Off);
            session.SetAreaOverlays(false);
            host.Map.SetPerspective(true);
            session.SetZoom(0);
            var siege = game.Settlement!;
            Require(siege.Enemies.Count > 0, "Saved combat cadence requires a checkpoint with living attackers.");
            var tracked = siege.Enemies.OrderByDescending(enemy =>
                Math.Abs(TermCity.Core.Rendering.SiegeMotion.Enemy(siege, enemy).Stride)).First();
            session.CenterOn(new((int)tracked.X, (int)tracked.Y));
            host.Map.RefreshCells();
            await Frames(host);
            var view = host.Map.Perspective!;
            var readiness = Stopwatch.StartNew();
            while (!view.PathPresentationReady || !view.VisibleTerrainReady || view.ObjectStreamingPending ||
                session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            {
                Require(readiness.Elapsed.TotalSeconds < 60,
                    $"Saved combat scene did not become ready: {view.PreparationPendingDescription} " +
                    $"simulation={session.BackgroundSimulationPending} backlog={session.PendingSimulationSeconds:0.000}s.");
                view.RequestRender();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            view.ValidateObjectBuffers();
            Require(view.FortificationBuffersMatch, "Saved combat must begin with exact native wall buffers.");
            GD.Print($"TERMCITY_SAVED_COMBAT_BASELINE population={game.Stats.Population} attackers={siege.Enemies.Count} " +
                $"defenders={siege.Defenders.Count} walls={siege.Walls.Count} render={view.RenderSize} quality={view.RenderQuality}");
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".baseline.png"));
            var frames = new List<double>();
            var frameCosts = new List<PerspectiveCity.FrameTimings>();
            var samples = new List<(double Frame, double Host, double Publication, double Capture,
                bool Map, bool Completed, double Worker, double Backlog, long Objects, long Paths, double Autosave)>();
            var workerCosts = new List<(ulong Frame, double Total, TermCity.Core.Session.SimulationTimings Costs)>();
            view.EnableFrameTimings(host.GetViewport());
            var displacements = new List<double>();
            int id = tracked.Id;
            Vector3? previousPosition = null;
            double worstHost = 0, worstActor = 0, peakBacklog = 0;
            int eligibleMotionFrames = 0, heldMotionFrames = 0;
            int changedPoseFrames = 0;
            IReadOnlyDictionary<(bool Friendly, int Id), int>? previousPoses = null;
            int captures = session.SimulationStateCaptures;
            game.Speed = profileSpeed;
            game.Paused = false;
            var watchdog = new Godot.Timer { OneShot = true, WaitTime = 5 };
            host.AddChild(watchdog);
            watchdog.Timeout += () =>
            {
                GD.PushError("Saved combat cadence stopped receiving rendered frames.");
                host.GetTree().Quit(1);
            };
            var duration = Stopwatch.StartNew();
            long previousFrame = Stopwatch.GetTimestamp();
            long previousObjects = view.ObjectUploadBytes, previousPaths = view.FootpathUploadBytes;
            int previousAutosaves = session.AutosaveStateCaptures;
            while (duration.Elapsed.TotalSeconds < profileSeconds)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                view.RequestRender();
                host.Map.QueueRedraw();
                watchdog.Start();
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                watchdog.Stop();
                long rendered = Stopwatch.GetTimestamp();
                frames.Add(Stopwatch.GetElapsedTime(previousFrame, rendered).TotalMilliseconds);
                previousFrame = rendered;
                frameCosts.Add(view.ReadFrameTimings(host.GetViewport()));
                samples.Add((frames[^1], host.CameraSimulationMilliseconds,
                    session.SimulationPublicationMilliseconds, session.SimulationCaptureMilliseconds,
                    session.SimulationMapPublished, session.SimulationCompleted,
                    session.LastBackgroundSimulationMilliseconds, session.PendingSimulationSeconds,
                    view.ObjectUploadBytes - previousObjects, view.FootpathUploadBytes - previousPaths,
                    session.AutosaveStateCaptures != previousAutosaves ? session.LastAutosaveCaptureMilliseconds : 0));
                previousAutosaves = session.AutosaveStateCaptures;
                if (session.SimulationCompleted)
                    workerCosts.Add((frameCosts[^1].Frame, session.LastBackgroundSimulationMilliseconds,
                        session.LastSimulationTimings));
                previousObjects = view.ObjectUploadBytes;
                previousPaths = view.FootpathUploadBytes;
                worstHost = Math.Max(worstHost, host.CameraSimulationMilliseconds);
                worstActor = Math.Max(worstActor, view.SiegeActorBuildMilliseconds);
                peakBacklog = Math.Max(peakBacklog, session.PendingSimulationSeconds);
                var poses = view.RenderedActorPoseHashes();
                if (previousPoses is not null && poses.Any(pair =>
                    previousPoses.TryGetValue(pair.Key, out int previousHash) && previousHash != pair.Value))
                    changedPoseFrames++;
                previousPoses = poses;
                if (session.PresentedSiege is { } presented)
                {
                    var movingEnemy = presented.Enemies.FirstOrDefault(candidate =>
                        Math.Abs(TermCity.Core.Rendering.SiegeMotion.Enemy(presented, candidate).Stride) > .00001 &&
                        view.RenderedEnemyPosition(candidate.Id) is { } position &&
                        new Rect2(Vector2.Zero, host.Map.Size).HasPoint(view.ScreenPoint(position)));
                    if (movingEnemy is not null && id != movingEnemy.Id)
                    {
                        id = movingEnemy.Id;
                        previousPosition = null;
                    }
                }
                var current = view.RenderedEnemyPosition(id);
                var enemy = session.PresentedSiege?.Enemies.FirstOrDefault(candidate => candidate.Id == id);
                bool moving = enemy is not null && current is { } visible &&
                    new Rect2(Vector2.Zero, host.Map.Size).HasPoint(view.ScreenPoint(visible)) &&
                    Math.Abs(TermCity.Core.Rendering.SiegeMotion.Enemy(session.PresentedSiege!, enemy).Stride) > .00001;
                if (current is { } at && previousPosition is { } before && moving)
                {
                    double displacement = before.DistanceTo(at);
                    displacements.Add(displacement);
                    eligibleMotionFrames++;
                    if (displacement < .000001) heldMotionFrames++;
                }
                previousPosition = current;
            }
            watchdog.QueueFree();
            double measuredSeconds = duration.Elapsed.TotalSeconds;
            game.Paused = true;
            double backlogAtStop = session.PendingSimulationSeconds;
            GD.Print($"TERMCITY_SAVED_COMBAT_STOP speed={profileSpeed} backlog={backlogAtStop:0.000}s " +
                $"frames={frames.Count} measured={measuredSeconds:0.0}s worst={frames.Max():0.0}ms");
            if (quitTest)
            {
                var quitWatchdog = new Godot.Timer { OneShot = true, WaitTime = 5 };
                host.AddChild(quitWatchdog);
                quitWatchdog.Timeout += () =>
                {
                    GD.PushError("Queued simulation prevented quit within the 5-second shutdown budget.");
                    host.GetTree().Quit(1);
                };
                quitWatchdog.Start();
                session.RequestQuit();
                if (session.Prompt?.Title == "Quit") session.SelectPrompt(1);
                await Task.Delay(TimeSpan.FromSeconds(6));
                throw new InvalidOperationException("The quit diagnostic did not exit.");
            }
            var drain = Stopwatch.StartNew();
            while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
            {
                Require(drain.Elapsed.TotalSeconds < 30, "Saved combat could not drain retained time.");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            await Capture(host, capture);
            frames.Sort();
            displacements.Sort();
            static double Percentile(List<double> samples, double percentile) =>
                samples.Count == 0 ? 0 : samples[Math.Min(samples.Count - 1, (int)(samples.Count * percentile))];
            foreach (int index in Enumerable.Range(0, samples.Count).OrderByDescending(index => samples[index].Frame).Take(20))
            {
                var cost = frameCosts[index];
                var sample = samples[index];
                GD.Print($"TERMCITY_MATCHED_FRAME frame={cost.Frame} render-cpu={cost.RenderCpu:0.00}ms " +
                    $"gpu={cost.RenderGpu:0.00}ms gpu-supported={cost.RenderGpu > 0} setup={cost.SetupCpu:0.00}ms " +
                    $"renderer-host={cost.Host:0.00}ms actors={cost.Actors:0.00}ms walls={cost.Walls:0.00}ms " +
                    $"regenerated={cost.RegeneratedWalls} wall-phases={cost.WallAssembly:0.00}/{cost.WallPages:0.00}/{cost.WallTops:0.00}ms " +
                    $"top-phases={cost.Extensions:0.00}/{cost.Clipping:0.00}/{cost.TopUploads:0.00}ms top-writes={cost.TopWrites} " +
                    $"elapsed={sample.Frame:0.00}ms simulation-host={sample.Host:0.00}ms " +
                    $"publication={sample.Publication:0.00}ms capture={sample.Capture:0.00}ms map={sample.Map} " +
                    $"completed={sample.Completed} worker={sample.Worker:0.00}ms backlog={sample.Backlog:0.000}s " +
                    $"object-bytes={sample.Objects} path-bytes={sample.Paths} " +
                    $"autosave-capture={sample.Autosave:0.00}ms " +
                    $"allocated-global={cost.Allocated} gc={cost.Gen0}/{cost.Gen1}/{cost.Gen2}");
            }
            foreach (var work in workerCosts.OrderByDescending(work => work.Total).Take(10))
                GD.Print($"TERMCITY_WORKER_EVENT frame={work.Frame} total={work.Total:0.00}ms " +
                    $"siege={work.Costs.Siege:0.00}ms day={work.Costs.Day:0.00}ms analysis={work.Costs.Analysis:0.00}ms " +
                    $"navigation={work.Costs.Navigation:0.00}ms capture={work.Costs.Capture:0.00}ms " +
                    $"path-wear={work.Costs.PathWear:0.00}ms patrol={work.Costs.Patrol:0.00}ms " +
                    $"journeys={work.Costs.BuildingJourneys:0.00}/{work.Costs.RoadJourneys:0.00}/{work.Costs.PathLinks:0.00}ms " +
                    $"population={work.Costs.Population:0.00}ms week-finish={work.Costs.WeekFinish:0.00}ms " +
                    $"footbridges={work.Costs.Footbridges:0.00}ms travel-repair={work.Costs.TravelRepair:0.00}ms");
            GD.Print($"TERMCITY_SPIKE_FREQUENCY seconds={measuredSeconds:0.0} " +
                $"over25={samples.Count(sample => sample.Frame > 25)} over50={samples.Count(sample => sample.Frame > 50)} " +
                $"wall-frames={frameCosts.Count(cost => cost.Walls > 0)} top-write-frames={frameCosts.Count(cost => cost.TopWrites > 0)} " +
                $"gc-frames={frameCosts.Count(cost => cost.Gen0 + cost.Gen1 + cost.Gen2 > 0)} " +
                $"map-publications={samples.Count(sample => sample.Map)} object-frames={samples.Count(sample => sample.Objects > 0)} " +
                $"path-frames={samples.Count(sample => sample.Paths > 0)} " +
                $"publication-worst={samples.Max(sample => sample.Publication):0.00}ms capture-worst={samples.Max(sample => sample.Capture):0.00}ms " +
                $"autosave-frames={samples.Count(sample => sample.Autosave > 0)} autosave-capture-worst={samples.Max(sample => sample.Autosave):0.00}ms");
            GD.Print($"TERMCITY_FRAME_COSTS render-cpu-p95={Percentile(frameCosts.Select(cost => cost.RenderCpu).Order().ToList(), .95):0.00}ms " +
                $"gpu-p95={Percentile(frameCosts.Select(cost => cost.RenderGpu).Order().ToList(), .95):0.00}ms " +
                $"allocated-global={frameCosts.Sum(cost => cost.Allocated)} gc={frameCosts.Sum(cost => cost.Gen0)}/" +
                $"{frameCosts.Sum(cost => cost.Gen1)}/{frameCosts.Sum(cost => cost.Gen2)}");
            GD.Print($"TERMCITY_SAVED_COMBAT_CADENCE frames={frames.Count} p50={Percentile(frames, .5):0.0}ms " +
                $"p90={Percentile(frames, .9):0.0}ms p95={Percentile(frames, .95):0.0}ms p99={Percentile(frames, .99):0.0}ms " +
                $"worst={frames[^1]:0.0}ms host-worst={worstHost:0.0}ms actor-worst={worstActor:0.0}ms " +
                $"backlog-peak={peakBacklog:0.000}s backlog-stop={backlogAtStop:0.000}s " +
                $"captures={session.SimulationStateCaptures - captures} combat-publications={session.CombatOnlyPublications} " +
                $"pose-change-frames={changedPoseFrames} motion-frames={eligibleMotionFrames} held={heldMotionFrames} " +
                $"displacement-p95={Percentile(displacements, .95):0.00000} " +
                $"drain={drain.Elapsed.TotalMilliseconds:0.0}ms retries={session.RetriedSimulationUpdates}");
            Require(session.PendingSimulationSeconds == 0, "Saved combat must retain and publish every elapsed slice.");
            Require(changedPoseFrames > 30, "Saved combat needs enough actual rendered animation-pose changes.");
            Require(view.FortificationBuffersMatch, "Saved combat must preserve exact animated/native wall buffers.");
            Require(view.ActorBuffersMatch, "Saved combat actor buffers and picking must use the authored transforms.");
            var failures = new List<string>();
            if (Percentile(frames, .9) >= 60 || frames[^1] >= 100)
                failures.Add("Saved combat exceeds the existing 60ms p90 / 100ms worst frame budget.");
            if (Percentile(frames, .95) > 25 || Percentile(frames, .99) > 50)
                failures.Add("Saved combat exceeds the additional 25ms p95 / 50ms p99 smoothness targets.");
            if (eligibleMotionFrames > 30 && heldMotionFrames > eligibleMotionFrames * .05)
                failures.Add("Moving actor presentation holds exceed the 5% cadence target.");
            if (failures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", failures));
            GD.Print("TERMCITY_SAVED_COMBAT_OK elapsed=retained walls=exact actors=continuous");
        }
        if (game.Settlement is null) session.CenterOn(new(game.Map.Width / 2, game.Map.Height / 2));
        host.Map.RefreshCells();
        await Frames(host);
        await GodotSmoke.VerifyPlacementMapNavigation(host, capture,
            Math.Min(game.Map.Width, game.Map.Height) < 160 ? "Town Well" : "Woodlot");
        GodotSmoke.VerifySmoothPerspectivePanning(host);
        await GodotSmoke.VerifyMinimapRotation(host, capture);
        await VerifyOverhead();
        if (game.Settlement is { } settlement)
            session.CenterOn(new(settlement.Bounds.X + settlement.Bounds.Width / 2,
                settlement.Bounds.Y + settlement.Bounds.Height / 2));
        else session.CenterOn(new(game.Map.Width / 2, game.Map.Height / 2));
        await Frames(host);
        var profileCenter = game.Settlement is { } bailey
            ? new Pos(bailey.Bounds.X + bailey.Bounds.Width / 2, bailey.Bounds.Y + bailey.Bounds.Height / 2)
            : new Pos(game.Map.Width / 2, game.Map.Height / 2);
        var renderer = host.Map.Perspective!;
        await renderer.VerifyTerrainHaloReuse();
        await PerspectiveCity.VerifyTerrainContinuity(host);
        renderer.ValidateObjectBuffers();
        Require(renderer.SceneObjectCount < 512, "Progressive object streaming must preserve the batched render-node budget.");
        Require(renderer.NativeRenderSize == PerspectiveCity.NativePixelSize(host.Map.Size, host.Map.GetScreenTransform().Scale.Abs()) &&
            renderer.RenderSize == renderer.NativeRenderSize * renderer.RenderScale &&
            renderer.EdgeAntialiasing == (renderer.RenderQuality == DiagnosticRenderQuality.Native4x
                ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Msaa8X),
            "Camera diagnostics must use their explicit quality treatment without changing normal defaults.");
        GD.Print($"TERMCITY_CAMERA_BASELINE scenario={game.Config.Scenario} map={game.Map.Width}x{game.Map.Height} " +
            $"settlement={game.Settlement is not null} window={host.GetWindow().Size} " +
            $"overlay={session.Overlay} areas={session.AreaOverlaysVisible} " +
            $"render={host.Map.Perspective!.RenderSize}");
        await Drag("paused", GameSpeed.Medium, paused: true, seconds: 3);
        foreach (var speed in Enum.GetValues<GameSpeed>())
            await Drag($"running-{speed}", speed, paused: false, seconds: game.Config.SecondsPerWeek(speed) + 1);
        if (game.Settlement is not null)
        {
            Require(game.DebugStartSiegeWave(24).Success, "Camera profiling needs real moving attackers.");
            await Drag("combat", GameSpeed.Medium, paused: false, seconds: game.Config.MediumSecondsPerWeek + 1);
        }
        await Drag("buffer-crossing", GameSpeed.Medium, paused: true, seconds: 4, crossing: true);
        game.Paused = true;
        session.CenterOn(profileCenter);
        await Frames(host);
        await Capture(host, capture);
        GD.Print("TERMCITY_CAMERA_CONTROLS_OK drag=left+middle keyboard=edge+arrows trackpad=pan+pinch " +
            "zoom=wheel+keys rotation=buttons+keys tilt=3 minimap=click+drag placement=preserved " +
            "off-map=rejected fractional=continuous narrow=terminal overhead=retained");
        if (budgetFailures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", budgetFailures));

        async Task Drag(string name, GameSpeed speed, bool paused, double seconds, bool crossing = false)
        {
            if (DisplayServer.GetName() != "headless") host.GetWindow().GrabFocus();
            game.Paused = true;
            game.Speed = speed;
            session.CenterOn(profileCenter);
            host.Map.Perspective!.ResetCameraOffset();
            await Frames(host);
            var start = host.Map.Size / 2;
            Input(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = start });
            game.Paused = paused;
            double days = game.ElapsedDays;
            var view = host.Map.Perspective!;
            var samples = new List<double>();
            double worstInput = 0, worstVisible = 0, worstSimulation = 0, worstHud = 0;
            int builds = view.GeometryBuilds, pathBuilds = view.PathSceneBuilds, spikes = 0, freezes = 0;
            var watchdog = new Godot.Timer { OneShot = true, WaitTime = 5 };
            host.AddChild(watchdog);
            watchdog.Timeout += () =>
            {
                GD.PushError("Camera profiling stopped receiving rendered frames; keep the smoke window visible.");
                host.GetTree().Quit(1);
            };
            var duration = Stopwatch.StartNew();
            long previousRendered = Stopwatch.GetTimestamp();
            Vector2 point = start;
            while (duration.Elapsed.TotalSeconds < seconds || session.BackgroundSimulationPending ||
                session.PendingSimulationSeconds > 0)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                double t = duration.Elapsed.TotalSeconds;
                if (t >= seconds) game.Paused = true;
                Require(t < seconds + 15, "Camera profiling could not drain its retained simulation time.");
                point = start + new Vector2((float)Math.Sin(t * .7) * (crossing ? host.Map.Size.X : 180),
                    (float)Math.Sin(t * .5) * (crossing ? host.Map.Size.Y * .45f : 80));
                int geometry = view.GeometryBuilds, version = game.MapVersion;
                int navigation = game.Map.NavigationVersion, buildings = game.Map.BuildingFootprints.Count;
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                var frame = Stopwatch.StartNew();
                Input(new InputEventMouseMotion { Position = point, ButtonMask = MouseButtonMask.Middle });
                double input = frame.Elapsed.TotalMilliseconds;
                view.RequestRender();
                host.Map.QueueRedraw();
                watchdog.Start();
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                watchdog.Stop();
                long rendered = Stopwatch.GetTimestamp();
                double visible = frame.Elapsed.TotalMilliseconds;
                double elapsed = Stopwatch.GetElapsedTime(previousRendered, rendered).TotalMilliseconds;
                previousRendered = rendered;
                samples.Add(elapsed);
                worstInput = Math.Max(worstInput, input);
                worstVisible = Math.Max(worstVisible, visible);
                worstSimulation = Math.Max(worstSimulation, host.CameraSimulationMilliseconds);
                worstHud = Math.Max(worstHud, host.CameraHudMilliseconds);
                if (elapsed > 50) spikes++;
                if (elapsed > 100) freezes++;
                if (elapsed > 60)
                    GD.Print($"TERMCITY_CAMERA_SPIKE phase={name} frame={elapsed:0.0}ms input={input:0.0}ms " +
                        $"simulation={host.CameraSimulationMilliseconds:0.0}ms hud={host.CameraHudMilliseconds:0.0}ms " +
                        $"background-last={session.LastBackgroundSimulationMilliseconds:0.0}ms " +
                        $"backlog={session.PendingSimulationSeconds:0.000}s " +
                        $"siege={game.LastSiegeUpdateMilliseconds:0.0}ms patrol={game.LastPatrolAssignmentMilliseconds:0.0}ms " +
                        $"day={game.LastDayUpdateMilliseconds:0.0}ms wear={game.LastPathWearMilliseconds:0.0}ms " +
                        $"journeys={game.Map.LastBuildingJourneyMilliseconds:0.0}/{game.Map.LastRoadJourneyMilliseconds:0.0}/" +
                        $"{game.Map.LastPathLinkMilliseconds:0.0}ms " +
                        $"hud-parts={host.CameraInspectorMilliseconds:0.0}/{host.CameraPeasantMilliseconds:0.0}/" +
                        $"{host.CameraPanelMilliseconds:0.0}ms " +
                        $"repair={game.Map.LastTravelRepairMilliseconds:0.0}ms " +
                        $"fields={game.Map.LastRepairedFields} compacted={game.Map.LastCompactedCells} " +
                        $"services={game.ServiceComputeMilliseconds:0.0}ms " +
                        $"rebuilds={view.GeometryBuilds - geometry} map-changes={game.MapVersion - version} " +
                        $"navigation={game.Map.NavigationVersion - navigation} buildings={game.Map.BuildingFootprints.Count - buildings} " +
                        $"ground={view.GroundBuildMilliseconds:0.0}ms objects={view.ObjectBuildMilliseconds:0.0}ms " +
                        $"solid-upload={view.SolidUploadMilliseconds:0.0}ms reused-buildings={view.BuildingsReusedLastBuild} " +
                        $"reused-batches={view.StaticBatchesReusedLastBuild} " +
                        $"paths={view.FootpathBuildMilliseconds:0.0}ms yards={view.YardBuildMilliseconds:0.0}ms " +
                        $"path-layout={view.FootpathLayoutMilliseconds:0.0}ms path-prepare={view.FootpathPrepareMilliseconds:0.0}ms " +
                        $"path-worker={view.FootpathWorkerMilliseconds:0.0}ms " +
                        $"renderer-host={view.RendererFrameMilliseconds:0.0}ms streaming-host=" +
                        $"{view.GroundFrameMilliseconds:0.0}/{view.PathFrameMilliseconds:0.0}/{view.ObjectFrameMilliseconds:0.0}ms " +
                        $"gc={GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
            }
            game.Paused = true;
            Input(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false, Position = point });
            watchdog.QueueFree();
            samples.Sort();
            double Percentile(double p) => samples[Math.Min(samples.Count - 1, (int)(samples.Count * p))];
            GD.Print($"TERMCITY_CAMERA_SAMPLE phase={name} frames={samples.Count} p50={Percentile(.5):0.0}ms " +
                $"p90={Percentile(.9):0.0}ms p95={Percentile(.95):0.0}ms p99={Percentile(.99):0.0}ms " +
                $"worst={samples[^1]:0.0}ms input-worst={worstInput:0.0}ms simulation-worst={worstSimulation:0.0}ms " +
                $"input-to-render-worst={worstVisible:0.0}ms " +
                $"hud-worst={worstHud:0.0}ms over50={spikes} over100={freezes} " +
                $"background-last={session.LastBackgroundSimulationMilliseconds:0.0}ms " +
                $"backlog={session.PendingSimulationSeconds:0.000}s retries={session.RetriedSimulationUpdates} " +
                $"rebuilds={view.GeometryBuilds - builds} paths-only={view.PathSceneBuilds - pathBuilds} " +
                $"days={game.ElapsedDays - days:0.000}");
            if (Percentile(.9) >= 60)
                budgetFailures.Add($"{name} frame p90 {Percentile(.9):0.0}ms exceeds 60ms.");
            if (samples[^1] >= 100 || worstVisible >= 100)
                budgetFailures.Add($"{name} worst frame {samples[^1]:0.0}ms / input-to-render {worstVisible:0.0}ms " +
                    $"exceeds 100ms ({freezes} frames).");
            if (worstInput >= 50)
                budgetFailures.Add($"{name} input handler {worstInput:0.0}ms exceeds 50ms.");
            Require(paused ? game.ElapsedDays == days : game.ElapsedDays > days,
                "Camera dragging must preserve the requested paused/running simulation state.");
        }

        void Input(InputEvent input) => host.Map.EmitSignal(Control.SignalName.GuiInput, input);
        async Task VerifyOverhead()
        {
            host.Map.SetPerspective(false);
            session.SetZoom(0);
            var center = new Pos(game.Map.Width / 2, game.Map.Height / 2);
            session.PlaceCursor(center);
            session.CenterOn(center);
            host.Map.RefreshCells();
            await Frames(host);
            var pointer = host.Map.Size / 2;
            var before = (session.CameraX, session.CameraY);
            Input(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = pointer });
            Input(new InputEventMouseMotion { Position = pointer - new Vector2(30, 20) });
            Input(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = false,
                Position = pointer - new Vector2(30, 20)
            });
            Require((session.CameraX, session.CameraY) != before, "Overhead middle-drag must still move the camera.");
            session.PlaceCursor(center);
            host._UnhandledInput(new InputEventKey { Keycode = Key.Right, Pressed = true, CtrlPressed = true });
            Require(session.Cursor != center, "Keyboard cursor/camera navigation must remain available.");
            host.Map.SetPerspective(true);
            session.ClearSelection();
            session.SetZoom(0);
            session.CenterOn(center);
            await Frames(host);
            var view = host.Map.Perspective!;
            var camera = new Vector2(session.CameraX, session.CameraY) + view.CameraOffset;
            Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer });
            Input(new InputEventMouseMotion { Position = pointer - new Vector2(30, 20) });
            Input(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = pointer - new Vector2(30, 20)
            });
            Require((new Vector2(session.CameraX, session.CameraY) + view.CameraOffset).DistanceTo(camera) > .01f,
                "Unmodified left-drag must continue to pan the perspective view.");
            session.ClearSelection();
        }
    }
}
