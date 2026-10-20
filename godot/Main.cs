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

    public override void _Ready()
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
            if (_options.SmokeTest && DisplayServer.GetName() == "headless")
            {
                GetWindow().ContentScaleSize = new Vector2I(1200, 720);
                GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            }
            var game = CityGame.New(_options.Config,
                startingDate: !_options.SmokeTest && !_options.DumpMap ? DateTime.Now : null);
            game.Paused = true;
            DebugStarterSetup? starter = _options.DebugStarter ? DebugStarterSetup.Apply(game) : null;
            string savePath = ProjectSettings.GlobalizePath(
                _options.SmokeTest ? "user://smoke/quicksave.json" : "user://quicksave.json");
            Session = new GameSession(game, savePath,
                showGuide: !_options.Load && !_options.SmokeTest && !_options.DumpMap && !_options.DebugStarter,
                initialCameraTarget: starter?.CameraTarget)
            {
                StartAtCurrentWeek = !_options.SmokeTest && !_options.DumpMap,
#if DEBUG
                ConfirmQuit = _options.SmokeTest,
#endif
            };
            if (_options.Load && !Session.LoadFrom(_options.LoadPath ?? savePath))
            {
                throw new IOException(Session.Message);
            }
            if (!_options.Load && !_options.SmokeTest && !_options.DumpMap) Session.SetZoom(1);
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
            Map = new TerminalMap { Session = Session, CellFont = font };
            Map.VerifyGlyphs(Session.Game.Map.Content);
            CreateEffects();
            CreateLayout(font);
            if (!_options.SmokeTest)
            {
                Map.SetPerspective(true);
                if (!_options.Load && starter is null) Session.CenterOn(Session.Cursor);
            }
            CreateMusic();
            CreateClicks();
            Session.Placed += PlayPlacementClick;
            Session.Changed += OnChanged;
            Session.CameraChanged += OnCameraChanged;
            Session.SelectionChanged += OnSelectionChanged;
            Session.QuitRequested += Quit;
            GetTree().AutoAcceptQuit = false;
            GetWindow().FocusEntered += OnFocusEntered;
            GetWindow().FocusExited += OnFocusExited;
            _started = true;
            UpdateUi();
            if (starter is not null)
                GD.Print($"TERMCITY_DEBUG_STARTER_OK spur={starter.Spur} well={starter.Well} " +
                    $"woodlot={starter.Woodlot} residential={starter.Residential} " +
                    $"camera-target={starter.CameraTarget} " +
                    $"residential-cells={Session.Game.Map.ZoneCells(TermCity.Core.World.ZoneType.Residential).Count()} " +
                    $"paused={Session.Game.Paused} clock-suspended={Session.ClockSuspended}");
            if (_options.SmokeTest)
            {
                Callable.From(RunSmokeTest).CallDeferred();
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            GD.PushError($"TermCity Godot startup failed: {error.Message}");
            GetTree().Quit(1);
        }
    }

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
        if (_focused && (!_editingName || !Session.Game.Paused))
        {
            Session.Update(delta);
            EdgeScroll(delta);
        }
        UpdateEffects(delta);
        Map.AdvanceAnimation(delta, _focused);
        _hudElapsed += delta;
        if (_hudElapsed >= 0.1)
        {
            _hudElapsed = 0;
            UpdateUi();
        }
    }

    private void OnChanged()
    {
        Map.Invalidate(true);
        UpdateUi();
    }

    private void OnCameraChanged()
    {
        Map.Perspective?.ResetCameraOffset();
        Map.Invalidate(true);
        UpdateUi();
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
        _clickPlayer.Stop();
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

    private void Quit()
    {
        StopMusic();
        GetTree().CreateTimer(0.1).Timeout += () => GetTree().Quit();
    }

    public override void _ExitTree()
    {
        if (!_started)
        {
            return;
        }
        Session.Placed -= PlayPlacementClick;
        Session.Changed -= OnChanged;
        Session.CameraChanged -= OnCameraChanged;
        Session.SelectionChanged -= OnSelectionChanged;
        Session.QuitRequested -= Quit;
        Director?.Dispose();
        GetWindow().FocusEntered -= OnFocusEntered;
        GetWindow().FocusExited -= OnFocusExited;
        StopMusic();
    }

    private async void RunSmokeTest()
    {
        try
        {
            SetFontSize(MinFontSize);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            bool headless = DisplayServer.GetName() == "headless";
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
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var error = GetViewport().GetTexture().GetImage().SavePng(path);
                if (error != Error.Ok)
                {
                    throw new IOException($"Screenshot failed: {error}.");
                }
                if (_options.PerspectiveSmokeTest)
                {
                    Map.ZoomBy(-1);
                    for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var halfZoomError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".zoom05.png"));
                    if (halfZoomError != Error.Ok) throw new IOException($"Half-zoom screenshot failed: {halfZoomError}.");
                    Map.ZoomBy(1);
                    Map.ZoomBy(1);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var zoomError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".zoom2.png"));
                    if (zoomError != Error.Ok) throw new IOException($"Zoom screenshot failed: {zoomError}.");
                    Map.ZoomBy(-1);
                    Session.CenterOn(new Pos(Session.Game.Map.Width / 2, Session.Game.Map.Height / 2 + 23));
                    Map.Invalidate(true);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
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
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var rangeError = GetViewport().GetTexture().GetImage().SavePng(Path.ChangeExtension(path, ".ranges.png"));
                    if (rangeError != Error.Ok) throw new IOException($"Range screenshot failed: {rangeError}.");
                }
            }
            GD.Print("TERMCITY_GODOT_SMOKE_OK");
            StopMusic();
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            GetTree().Quit();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            GD.PushError($"TermCity Godot smoke failed: {error}");
            GetTree().Quit(1);
        }
    }
}
