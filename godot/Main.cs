using Godot;
using System.Text;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class Main : Control
{
    public GameSession Session { get; private set; } = null!;

    public TerminalMap Map { get; private set; } = null!;

    private GodotOptions _options = null!;

    private bool _focused = true;

    private double _hudElapsed;

    private bool _started;
    private readonly CancellationTokenSource _debugStartupCancellation = new();

    public override async void _Ready()
    {
        try
        {
            _options = GodotOptions.Parse(OS.GetCmdlineUserArgs());
            if (_options.Help)
            {
                GD.Print(GodotOptions.Usage);
                GetTree().Quit();
                return;
            }
            Engine.MaxFps = _options.FramesPerSecond;
            if (_options.DepthComparison)
            {
                var comparison = new DepthComparison
                {
                    SmokeTest = _options.DepthComparisonSmokeTest,
                    CapturePath = _options.CapturePath,
                    TerrainStudy = _options.TerrainComparison,
                };
                AddChild(comparison);
                comparison.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                return;
            }
            if ((_options.SmokeTest || _options.DebugSettlement || _options.DebugSiege) &&
                DisplayServer.GetName() == "headless")
            {
                GetWindow().ContentScaleSize = new Vector2I(1200, 720);
                GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            }
            DateTime? startingDate = !_options.SmokeTest && !_options.DumpMap ? DateTime.Now : null;
            CityGame game;
            DebugStarterSetup? starter = null;
            Pos? settlementFocus = null;
            int regeneratedMaps = 0;
            if (_options.DebugSettlement || _options.DebugSiege)
            {
                var cancellation = _debugStartupCancellation.Token;
                string title = GetWindow().Title;
                GetWindow().Title = "TermCity - generating a map for the debug settlement";
                var preset = await Task.Run(() => SettlementDebugSetup.Create(_options.Config, _options.DebugSiege,
                    BaileyGateSide.Bottom, startingDate: startingDate, cancellationToken: cancellation), cancellation);
                if (cancellation.IsCancellationRequested) return;
                GetWindow().Title = title;
                game = preset.Game;
                settlementFocus = preset.Target;
                regeneratedMaps = preset.RegeneratedMaps;
                if (regeneratedMaps > 0)
                    GD.Print($"TERMCITY_DEBUG_SETTLEMENT_REGENERATED rejected-maps={regeneratedMaps} " +
                        $"initial-seed={_options.Config.Seed} selected-seed={game.Config.Seed}");
            }
            else
            {
                game = CityGame.New(_options.Config, startingDate: startingDate);
                game.Paused = true;
                starter = _options.DebugStarter ? DebugStarterSetup.Apply(game) : null;
            }
            if (_options.TerrainPerformanceTreatment is { } terrainTreatment)
            {
                game.Map.SetPreviewElevationProfile(TerrainStudyFixture.Profile(terrainTreatment));
                game.Touch();
            }
            string savePath = ProjectSettings.GlobalizePath(
                _options.SmokeTest ? "user://smoke/quicksave.json" : "user://quicksave.json");
            Session = new GameSession(game, savePath,
                showGuide: !_options.Load && !_options.SmokeTest && !_options.DumpMap && !_options.DebugStarter && settlementFocus is null,
                initialCameraTarget: settlementFocus ?? starter?.CameraTarget,
                debugSiegeMode: _options.DebugSiege || _options.SettlementSmokeTest || _options.InterfaceSmokeTest ||
                    _options.GarrisonSmokeTest)
            {
                StartAtCurrentWeek = !_options.SmokeTest && !_options.DumpMap,
#if DEBUG
                ConfirmQuit = _options.SmokeTest,
#endif
            };
            if (regeneratedMaps > 0)
                Session.SetMessage($"Generated a suitable {SettlementDebugSetup.DefaultWallSize}x" +
                    $"{SettlementDebugSetup.DefaultWallSize} debug settlement after regenerating " +
                    $"{regeneratedMaps} map(s). Map seed: {game.Config.Seed}.");
            if (_options.Load && !Session.LoadFrom(_options.LoadPath ?? savePath))
            {
                throw new IOException(Session.Message);
            }
            if (!_options.Load && !_options.SmokeTest && !_options.DumpMap)
                Session.SetZoom(settlementFocus is null ? 1 : -1);
            if (_options.DumpMap)
            {
                var text = new StringBuilder($"Seed {Session.Game.Config.Seed}, {Session.Game.Map.Width}x{Session.Game.Map.Height}\n");
                for (int y = 0; y < Session.Game.Map.Height; y++)
                {
                    for (int x = 0; x < Session.Game.Map.Width; x++)
                        text.Append(CellRenderer.Render(Session.Game, x, y, buildingArt: false).Glyph);
                    text.AppendLine();
                }
                GD.Print(text.ToString());
                GetTree().Quit();
                return;
            }
            LoadPreferences();
            ApplyFontSize();
#if DEBUG
            if (!_options.SmokeTest && DisplayServer.GetName() != "headless")
                GetWindow().Mode = Window.ModeEnum.Maximized;
#endif
            var font = LoadBundledFont();
            Map = new TerminalMap
            {
                Session = Session, CellFont = font,
                DepthStudyTreatment = _options.DepthPerformanceBaseline ? DepthTreatment.Baseline : DepthStudyArtwork.DefaultTreatment,
                TerrainStudyTreatment = _options.TerrainPerformanceTreatment ?? TerrainStudyTreatment.Balanced,
                OverviewLodEnabled = _options.OverviewLod,
                PersistentResidencyEnabled = _options.PersistentResidency,
                RenderQuality = _options.RenderQuality,
            };
            Map.VerifyGlyphs(Session.Game.Map.Content);
            CreateEffects();
            CreateLayout(font);
            if (!_options.SmokeTest)
            {
                Map.SetPerspective(true);
                if (!_options.Load && starter is null && settlementFocus is null) Session.CenterOn(Session.Cursor);
            }
            CreateMusic();
            CreateClicks();
            Session.Placed += PlayPlacementClick;
            Session.SiegeWaveStarted += PlayBattleHorn;
            Session.Changed += OnChanged;
            Session.CameraChanged += OnCameraChanged;
            Session.PresentationChanged += OnPresentationChanged;
            Session.SelectionChanged += OnSelectionChanged;
            Session.QuitRequested += Quit;
            GetTree().AutoAcceptQuit = false;
            GetWindow().FocusEntered += OnFocusEntered;
            GetWindow().FocusExited += OnFocusExited;
            _started = true;
            UpdateUi();
            if (_options.DebugSiege && Session.Game.Settlement is { WaveNumber: > 0 }) PlayBattleHorn();
            if (starter is not null)
                GD.Print($"TERMCITY_DEBUG_STARTER_OK spur={starter.Spur} well={starter.Well} " +
                    $"woodlot={starter.Woodlot} residential={starter.Residential} " +
                    $"camera-target={starter.CameraTarget} " +
                    $"residential-cells={Session.Game.Map.ZoneCells(TermCity.Core.World.ZoneType.Residential).Count()} " +
                    $"paused={Session.Game.Paused} clock-suspended={Session.ClockSuspended}");
            if (settlementFocus is not null)
                GD.Print($"TERMCITY_DEBUG_SETTLEMENT_OK siege={_options.DebugSiege} population={game.Stats.Population} " +
                    $"bounds={game.Settlement!.Bounds} goblins={game.Settlement.Enemies.Count} focus={settlementFocus}");
            if (_options.SmokeTest)
            {
                Callable.From(RunSmokeTest).CallDeferred();
            }
        }
        catch (OperationCanceledException) when (_debugStartupCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            GD.PushError($"TermCity Godot startup failed: {error.Message}");
            GetTree().Quit(1);
        }
    }

    internal double CameraSimulationMilliseconds { get; private set; }
    internal double CameraHudMilliseconds { get; private set; }
    internal double CameraInspectorMilliseconds, CameraPeasantMilliseconds, CameraPanelMilliseconds;
    private bool _updatingSimulation, _simulationUiDirty;

    public override void _Process(double delta)
    {
        if (!_started)
        {
            return;
        }
        if (_musicPump is { IsFaulted: true } && !_musicStopped)
        {
            GD.PushError($"Music playback failed: {_musicPump.Exception}");
            Session.SetMessage("Music playback failed; playback stopped.", MessageKind.Error);
            StopMusic();
        }
        if (_options.CameraPerformanceTest)
        {
            CameraSimulationMilliseconds = 0;
            CameraHudMilliseconds = 0;
            CameraInspectorMilliseconds = CameraPeasantMilliseconds = CameraPanelMilliseconds = 0;
        }
        bool simulationFocused = _focused || _options.CameraPerformanceTest || _options.MinimapTraversalTest;
        bool advanceClock = simulationFocused && (!_editingName || !Session.Game.Paused);
        if (advanceClock || Session.BackgroundPresentationPending || Session.BackgroundSimulationPending ||
            Session.PendingSimulationSeconds > 0)
        {
            long started = _options.CameraPerformanceTest ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _updatingSimulation = true;
            try
            {
                if (Map.PerspectiveEnabled && Session.Game.CanSnapshotSimulation ||
                    Session.BackgroundSimulationPending || Session.PendingSimulationSeconds > 0)
                    Session.UpdateResponsive(advanceClock ? delta : 0, Map.PerspectiveEnabled
                        ? Map.Perspective?.PathPresentationRequest(Session) : null);
                else Session.Update(advanceClock ? delta : 0);
            }
            catch (TermCity.Core.Session.BackgroundSimulationException error)
            {
                GD.PushError(error.ToString());
                Session.SetMessage(error.Message, MessageKind.Error);
                if (_options.SmokeTest) GetTree().Quit(1);
            }
            finally { _updatingSimulation = false; }
            if (_options.CameraPerformanceTest)
                CameraSimulationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (advanceClock) EdgeScroll(delta);
        }
        UpdateEffects(delta);
        Map.AdvanceAnimation(delta, simulationFocused);
        _hudElapsed += delta;
        if (_simulationUiDirty || _hudElapsed >= 0.1)
        {
            bool force = _simulationUiDirty;
            _simulationUiDirty = false;
            _hudElapsed = 0;
            UpdateUi(force);
        }
    }

    private void OnChanged()
    {
        Map.Invalidate(true);
        if (_updatingSimulation) _simulationUiDirty = true;
        else UpdateUi();
    }

    private void OnCameraChanged()
    {
        Map.Perspective?.ResetCameraOffset();
        Map.Invalidate(true);
        _panel.RefreshCamera();
        UpdateDebugSiegeControls();
    }

    private void OnPresentationChanged()
    {
        Map.Perspective?.PublishSimulationPaths(Session);
        Map.Invalidate(true);
    }

    private void OnSelectionChanged()
    {
        Map.Invalidate(false);
        UpdateUi();
    }

    private void OnFocusEntered()
    {
        _focused = true;
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0;
    }

    private void OnFocusExited()
    {
        _focused = false;
        _music.StreamPaused = true;
        StopSoundEffects();
        if (_options.CameraPerformanceTest) return;
        StopPointerGesture();
        _hover = false;
        _panel.Minimap.StopDrag();
    }

    public override void _Notification(int what)
    {
        if (_started && what == NotificationResized) FitHeaderFonts();
        if (_started && what == NotificationWMCloseRequest)
        {
            if (!FinishRename(true)) return;
            Session.RequestQuit();
        }
        if (_started && what == NotificationResized && _modal.Visible)
        {
            CenterModal();
        }
    }

    private bool _quitting;
    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        SetProcess(false);
        StopMusic();
        GameSession session = Session;
        session.Game.Paused = true;
        var shutdown = System.Diagnostics.Stopwatch.StartNew();
        double backlog = session.PendingSimulationSeconds;
        try
        {
            await session.StopForQuitAsync();
        }
        catch (TermCity.Core.Session.BackgroundSimulationException error)
        {
            GD.PushError(error.ToString());
            session.SetMessage(error.Message, MessageKind.Error);
            _quitting = false;
            SetProcess(true);
            return;
        }
        if (_options.CameraPerformanceTest)
            GD.Print($"TERMCITY_QUIT_COMPLETE backlog-discarded={backlog:0.000}s shutdown={shutdown.Elapsed.TotalMilliseconds:0.0}ms");
        GetTree().Quit();
    }

    public override void _ExitTree()
    {
        _debugStartupCancellation.Cancel();
        _debugStartupCancellation.Dispose();
        if (!_started)
        {
            return;
        }
        Session.Placed -= PlayPlacementClick;
        Session.SiegeWaveStarted -= PlayBattleHorn;
        Session.Changed -= OnChanged;
        Session.CameraChanged -= OnCameraChanged;
        Session.PresentationChanged -= OnPresentationChanged;
        Session.SelectionChanged -= OnSelectionChanged;
        Session.QuitRequested -= Quit;
        Session.RequestSimulationStop();
        Director?.Dispose();
        GetWindow().FocusEntered -= OnFocusEntered;
        GetWindow().FocusExited -= OnFocusExited;
        StopMusic();
        DisposeSoundEffects();
    }

    private async void RunSmokeTest()
    {
        try
        {
            SetFontSize(MinFontSize);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            bool headless = DisplayServer.GetName() == "headless";
            if (_options.MediumCheckpointsPath is { } checkpointDirectory)
            {
                if (!headless) throw new InvalidOperationException("Checkpoint generation requires --headless; it does not benchmark rendered frames.");
                var result = await Task.Run(() => MediumGrowthCheckpoints.Generate(_options.Config,
                    checkpointDirectory, report: GD.Print, resumePath: _options.LoadPath));
                GD.Print($"TERMCITY_MEDIUM_CHECKPOINTS complete={result.Complete} peak={result.PeakPopulation} " +
                    $"days={result.Days:0} output={Path.GetFullPath(checkpointDirectory)} limit={result.Limit}");
                StopMusic();
                GetTree().Quit(result.Complete ? 0 : 2);
                return;
            }
            if (!headless)
            {
                GetWindow().GrabFocus();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (_options.PersistentResidencyTest)
            {
                var verification = PerspectiveCity.VerifyPersistentResidency(this, _options.CacheExperiment == CacheExperiment.Current
                    ? CacheExperiment.PersistentScene : _options.CacheExperiment);
                if (await Task.WhenAny(verification, Task.Delay(TimeSpan.FromSeconds(120))) != verification)
                    throw new InvalidOperationException("Persistent residency validation stopped progressing within its watchdog.");
                await verification;
                GD.Print("TERMCITY_PERSISTENT_RESIDENCY_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.FootpathArtworkSmokeTest)
            {
                await FootpathArtworkSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_FOOTPATH_ART_SMOKE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.InterfaceSmokeTest)
            {
                await GodotSmoke.VerifyInterface(this, _options.CapturePath);
                GD.Print("TERMCITY_INTERFACE_SMOKE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.GarrisonSmokeTest)
            {
                await GarrisonSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_GARRISON_SMOKE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.CameraPerformanceTest)
            {
                await CameraPerformanceSmoke.Run(this, _options.CapturePath, _options.Load, _options.CameraProfileSeconds,
                    _options.BenchmarkSpeed, _options.CameraQuitTest);
                GD.Print("TERMCITY_CAMERA_PERFORMANCE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.WholeMapCacheTest)
            {
                await WholeMapCacheSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_WHOLE_MAP_CACHE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.RecentRegionTest)
            {
                Map.SetPerspective(true);
                Map.Perspective!.ConfigureCacheExperiment(_options.CacheExperiment == CacheExperiment.Current
                    ? CacheExperiment.Tuned : _options.CacheExperiment);
                await RecentRegionSmoke.Run(this, _options.CacheExperiment == CacheExperiment.Current
                    ? CacheExperiment.Tuned : _options.CacheExperiment, _options.CapturePath, _options.BenchmarkZoom);
                GD.Print("TERMCITY_RECENT_REGION_OK");
                StopMusic(); GetTree().Quit(); return;
            }
            if (_options.HaloScrollTest)
            {
                Map.SetPerspective(true);
                var view = Map.Perspective!;
                view.ConfigureCacheExperiment(_options.CacheExperiment == CacheExperiment.Current
                    ? CacheExperiment.Tuned : _options.CacheExperiment);
                await TerrainHaloScrollSmoke.Run(this, _options.CapturePath);
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.MinimapTraversalTest)
            {
                Map.SetPerspective(true);
                var view = Map.Perspective!;
                view.ConfigureCacheExperiment(_options.CacheExperiment);
                await MinimapTraversalSmoke.Run(this, _options.CapturePath, _options.CacheExperiment.ToString(),
                    () => view.PresentedTexture, () => (view.CpuRetirementMilliseconds, view.GpuRetirementMilliseconds),
                    _options.BenchmarkZoom, _options.BenchmarkSpeed);
                GD.Print("TERMCITY_MINIMAP_TRAVERSAL_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.MinimapJumpTest)
            {
                await MinimapJumpSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_MINIMAP_JUMP_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.WallNetworkSmokeTest)
            {
                await WallNetworkSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_WALL_NETWORK_SMOKE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.WorldScaleSmokeTest)
            {
                await WorldScaleSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_WORLD_SCALE_SMOKE_OK");
                StopMusic();
                GetTree().Quit();
                return;
            }
            if (_options.SettlementSmokeTest)
            {
                await SettlementSmoke.Run(this, _options.CapturePath);
                GD.Print("TERMCITY_SETTLEMENT_SMOKE_OK");
                StopMusic();
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                GetTree().Quit();
                return;
            }
            var window = GetWindow();
            var initialSize = headless ? window.ContentScaleSize : window.Size;
            if (headless)
            {
                window.ContentScaleSize = new Vector2I(960, MinimumWindowHeight);
            }
            else
            {
                window.Size = new Vector2I(960, MinimumWindowHeight);
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_options.BuildingSmokeTest)
            {
                GodotSmoke.Run(this);
                await GodotSmoke.RunNativeInput(this);
            }
            if (_options.MilestoneSmokeTest)
                await GodotSmoke.VerifyReachedMilestoneDialog(this, _options.CapturePath);
            else await GodotSmoke.VerifyBuildingRendering(this);
            if (_options.WoodlotSmokeTest)
            {
                await GodotSmoke.VerifyWoodlotArtwork(this, _options.CapturePath);
                await GodotSmoke.VerifyActualSubmenuModels(this, _options.CapturePath);
            }
            if (_options.PlacementSmokeTest && !_options.PerspectiveSmokeTest)
                await GodotSmoke.VerifyPlacementMapNavigation(this, _options.CapturePath);
            if (_options.PerspectiveSmokeTest) await GodotSmoke.VerifyPerspectiveRendering(this, _options.CapturePath);
            if (_options.PerspectivePerformanceTest) await PerspectivePerformanceSmoke.Run(this);
            if (headless)
            {
                window.ContentScaleSize = initialSize;
            }
            else
            {
                window.Size = initialSize;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var center = new Pos(Session.Game.Map.Width / 2, Session.Game.Map.Height / 2);
            Session.PlaceCursor(_options.BuildingSmokeTest ? center.Offset(23, 14) : center);
            Session.CenterOn(center);
            Session.SetMessage("Godot integration checks passed.", MessageKind.Success);
            if (_options.CapturePath is { } path)
            {
                if (DisplayServer.GetName() == "headless")
                {
                    throw new InvalidOperationException("Screenshots require a graphical display.");
                }
                await GodotSmoke.RenderedFrame(this);
                var error = GetViewport().GetTexture().GetImage().SavePng(path);
                if (error != Error.Ok)
                {
                    throw new IOException($"Screenshot failed: {error}.");
                }
                if (_options.PerspectiveSmokeTest)
                {
                    Map.ZoomBy(-1);
                    for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await GodotSmoke.RenderedFrame(this);
                    var halfZoomError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".zoom05.png"));
                    if (halfZoomError != Error.Ok) throw new IOException($"Half-zoom screenshot failed: {halfZoomError}.");
                    Map.ZoomBy(1);
                    Map.ZoomBy(1);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await GodotSmoke.RenderedFrame(this);
                    var zoomError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".zoom2.png"));
                    if (zoomError != Error.Ok) throw new IOException($"Zoom screenshot failed: {zoomError}.");
                    Map.ZoomBy(-1);
                    Session.CenterOn(new Pos(Session.Game.Map.Width / 2, Session.Game.Map.Height / 2 + 23));
                    Map.Invalidate(true);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await GodotSmoke.RenderedFrame(this);
                    var riverError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".river.png"));
                    if (riverError != Error.Ok) throw new IOException($"River screenshot failed: {riverError}.");
                    var green = Session.Game.Map.ServiceCells.Select(Session.Game.Map.PosOf)
                        .First(p => Session.Game.Map.BuildingAt(p.X, p.Y)!.Name == "Village Green");
                    Session.SetZoom(-1);
                    Map.RefreshCells();
                    Session.SelectCell(green);
                    Session.CenterOn(green);
                    Map.Invalidate(true);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await GodotSmoke.RenderedFrame(this);
                    var rangeError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".ranges.png"));
                    if (rangeError != Error.Ok) throw new IOException($"Range screenshot failed: {rangeError}.");
                }
            }
            GD.Print("TERMCITY_GODOT_SMOKE_OK");
            StopMusic();
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            GetTree().Quit();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException or
            System.Reflection.AmbiguousMatchException or System.Reflection.TargetInvocationException)
        {
            GD.PushError($"TermCity Godot smoke failed: {error}");
            GetTree().Quit(1);
        }
    }
}
