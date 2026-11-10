using System.Diagnostics;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class DepthComparison : Control
{
    internal bool SmokeTest { get; init; }
    internal string? CapturePath { get; init; }
    internal bool TerrainStudy { get; init; }
    private static readonly string[] BuildingTreatments = ["1 Original", "2 Lighting", "3 Relief", "4 Combined"];
    private static readonly string[] BuildingSubjects =
        ["Timber house", "Stone house", "Stone wall / tower / gate", "Wood wall / tower / gate", "Timber palisade"];
    private static readonly string[] TerrainTreatments =
        ["1 Original / 1x hills", "2 Lighting", "3 Balanced / 1x hills", "4 Balanced / 2x hills", "5 Selected / 3x hills"];
    private static readonly string[] TerrainSubjects =
        ["Terrain / river", "Village / infrastructure", "Woodland / outcrops", "Structures / props"];
    private string[] Treatments => TerrainStudy ? TerrainTreatments : BuildingTreatments;
    private string[] Subjects => TerrainStudy ? TerrainSubjects : BuildingSubjects;
    private int _terrainTreatment = 4;
    private int ActiveTreatment => TerrainStudy ? _terrainTreatment : (int)_treatment;
    private readonly TextureRect _picture = new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
        SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 240),
    };
    private readonly Label _heading = new();
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _description = new()
        { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(0, 40) };
    private readonly List<Button> _treatmentButtons = [];
    private readonly List<Button> _subjectButtons = [];
    private readonly List<Button> _controls = [];
    private PerspectiveCity _renderer = null!;
    private GameSession _session = null!;
    private DepthTreatment _treatment = DepthStudyArtwork.DefaultTreatment;
    private int _subject;
    private float _azimuth = PerspectiveCity.InitialAzimuth, _elevation = 35;
    private bool _capturing, _rotating;
    private bool _panning;
    private Vector3 _panAnchor;
    private Vector2 _panOffset;
    private string CapturePrefix => CapturePath ?? ProjectSettings.GlobalizePath(TerrainStudy
        ? "user://terrain-comparison/study.png" : "user://depth-comparison/study.png");

    public override void _Ready()
    {
        Theme = new Theme
        {
            DefaultFont = GD.Load<Font>("res://Assets/DejaVuSansMono.ttf"), DefaultFontSize = 14,
        };
        Theme.SetStylebox("panel", "TooltipPanel", TerminalFrame.CreateTooltipStyle());
        Theme.SetColor("font_color", "TooltipLabel", Colors.White);
        var background = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 0);
        AddChild(layout);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var header = new TerminalFrame();
        layout.AddChild(header);
        var tools = new VBoxContainer();
        tools.AddThemeConstantOverride("separation", 4);
        header.AddChild(tools);
        _heading.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        tools.AddChild(_heading);
        var treatments = new GridContainer { Columns = 2 };
        tools.AddChild(treatments);
        for (int i = 0; i < Treatments.Length; i++)
        {
            int treatment = i;
            _treatmentButtons.Add(AddButton(treatments, Treatments[i], () => SetTreatmentIndex(treatment)));
        }
        var subjects = new GridContainer { Columns = 2 };
        tools.AddChild(subjects);
        for (int i = 0; i < Subjects.Length; i++)
        {
            int subject = i;
            _subjectButtons.Add(AddButton(subjects, Subjects[i], () => SetSubject(subject)));
        }
        var actions = new GridContainer { Columns = 4 };
        tools.AddChild(actions);
        AddButton(actions, "Q Rotate left", () => Rotate(-30));
        AddButton(actions, "E Rotate right", () => Rotate(30));
        AddButton(actions, "T Tilt", Tilt);
        AddButton(actions, "R Reset", ResetCamera);
        AddButton(actions, "- Zoom out", () => Zoom(-1));
        AddButton(actions, "+ Zoom in", () => Zoom(1));
        AddButton(actions, $"C Capture {Treatments.Length}", () => _ = CaptureSafely());
        AddButton(actions, "Esc Exit", () => GetTree().Quit());
        if (TerrainStudy) AddButton(actions, "O Overhead", ToggleOverhead);
        tools.AddChild(_description);
        var mapFrame = new TerminalFrame { SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddChild(mapFrame);
        mapFrame.AddChild(_picture);
        var footer = new TerminalFrame();
        layout.AddChild(footer);
        footer.AddChild(_status);
        _picture.GuiInput += HandlePictureInput;
        BuildFixture();
        ReplaceRenderer();
        _status.Text = TerrainStudy ? "Private comparison; 3x hills selected for new maps. Middle-drag pans; right-drag rotates."
            : "Combined is the game default. Right-drag rotates; wheel zooms; click inspects.";
        GD.Print($"TERMCITY_DEPTH_COMPARISON_READY subjects={Subjects.Length} treatments={Treatments.Length} live-viewports=1");
        if (SmokeTest) Callable.From(RunSmoke).CallDeferred();
    }

    private Button AddButton(Node parent, string text, Action action)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var normal = new StyleBoxFlat { BgColor = Colors.Black };
        normal.SetContentMarginAll(3);
        var focus = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = Colors.White };
        focus.SetBorderWidthAll(1);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", normal);
        button.AddThemeStyleboxOverride("pressed", normal);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeStyleboxOverride("focus", focus);
        button.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color("#70d7ff"));
        button.Pressed += action;
        parent.AddChild(button);
        _controls.Add(button);
        return button;
    }

    private void BuildFixture()
    {
        if (TerrainStudy)
        {
            var fixture = TerrainStudyFixture.Create(_subject, (TerrainStudyTreatment)_terrainTreatment);
            _session = new GameSession(fixture.Game, showGuide: false);
            _session.SetViewport(8, 8);
            _session.SetZoom(_subject == 3 ? -2 : -1);
            _session.CenterOn(fixture.Focus);
            _session.ClearSelection();
            return;
        }
        var game = CityGame.New(new GameConfig
        {
            MapWidth = 48, MapHeight = 48, Seed = 42, Scenario = CityScenario.Random,
        });
        game.Paused = true;
        var meadow = game.Map.Content.Terrains.Get(DefaultTerrains.GrassName);
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, meadow);
            }
        if (_subject < 2)
        {
            var building = game.Map.Content.Buildings.Get(_subject == 0 ? "Timber Home" : "Stone Home");
            var area = new CellRect(24 - building.Width / 2, 24 - building.Height / 2, building.Width, building.Height);
            foreach (var p in area.Cells()) game.Map.SetZone(p.X, p.Y, ZoneType.Residential);
            game.Map.SetBuildingFootprint(building, area);
        }
        game.Touch();
        _session = new GameSession(game, showGuide: false);
        _session.SetViewport(8, 8);
        _session.CenterOn(new Pos(24, 24));
        _session.ClearSelection();
        _session.SetZoom(_subject >= 2 ? -1 : 0);
    }

    private static IReadOnlyList<WallComponent> Walls(WallMaterial material)
    {
        var tower = WallDamageLayout.PreviewTower(new(18, 24), material);
        float edge = 18 + WallDamageLayout.Width(tower) / 2;
        var wall = WallDamageLayout.Preview(new(edge, 24), new(24, 24), material);
        var gate = WallDamageLayout.Preview(new(24, 24), new(27, 24), material, gate: true);
        return [tower, wall, gate];
    }

    private void ReplaceRenderer()
    {
        if (TerrainStudy && _session.Game.Map.PreviewElevationProfile !=
            TerrainStudyFixture.Profile((TerrainStudyTreatment)_terrainTreatment))
        {
            double zoom = _session.ZoomLevel;
            BuildFixture();
            _session.SetZoom(zoom);
            _session.CenterOn(new(29, _subject == 1 ? 30 : 27));
        }
        if (_renderer is not null)
        {
            _picture.Texture = null;
            RemoveChild(_renderer);
            _renderer.Free();
        }
        _renderer = new PerspectiveCity
        {
            DepthTreatment = _treatment, WaterMotionEnabled = false, SiegeAnimationEnabled = false,
            TerrainTreatment = TerrainStudy ? (TerrainStudyTreatment)_terrainTreatment : TerrainStudyTreatment.Current,
            DepthStudyWalls = TerrainStudy ? TerrainStudyFixture.ReferenceWalls(_subject) :
                _subject is 2 or 3 ? Walls(_subject == 3 ? WallMaterial.Wood : WallMaterial.Stone) : [],
            DepthStudyPalisade = !TerrainStudy && _subject == 4,
            DepthStudyFocusHeight = TerrainStudy ? _subject == 1 ? 4.5f : 3 :
                _subject is 2 or 3 ? 3 : 1.5f,
        };
        AddChild(_renderer);
        _renderer.SetDepthStudyCamera(_azimuth, _elevation);
        _heading.Text = $"{(TerrainStudy ? "TERRAIN" : "DEPTH")} STUDY | {Subjects[_subject]} | {Treatments[ActiveTreatment]}";
        _description.Text = TerrainStudy ? _terrainTreatment switch
        {
            0 => "Original terrain rendering with 6-foot hills. Current building and natural-feature artwork.",
            1 => "Current hill heights; correctly lit roads, bridge decks, cliffs and closed banks.",
            2 => "Lit surfaces, broad grass grouping and recessed earthy strata. Existing hill heights.",
            3 => "Balanced treatment with 12-foot maximum hills. River and sea levels are unchanged.",
            _ => "Selected: 18-foot maximum hills for new random maps. River and sea levels are unchanged.",
        } : _treatment switch
        {
            DepthTreatment.Baseline => "Original models, strong ambient fill, no cast shadows.",
            DepthTreatment.Lighting => "Unchanged models; reduced ambient fill and directional cast shadows.",
            DepthTreatment.Relief => "Original light; recessed openings, deeper eaves, projecting stonework.",
            _ => "Game default: recessed buildings, log-built timber defenses, stone relief and cast shadows.",
        };
        for (int i = 0; i < _treatmentButtons.Count; i++)
            _treatmentButtons[i].Text = (ActiveTreatment == i ? "> " : "  ") + Treatments[i];
        for (int i = 0; i < _subjectButtons.Count; i++)
            _subjectButtons[i].Text = (_subject == i ? "> " : "  ") + Subjects[i];
        Sync();
        if (_panOffset != Vector2.Zero)
        {
            _renderer.SetCameraOffset(_session, _panOffset);
            Sync();
        }
    }

    private void Sync()
    {
        if (_picture.Size.X < 1 || _picture.Size.Y < 1) return;
        _renderer.Sync(_session, _picture.Size);
        _picture.Texture = _renderer.Texture;
    }

    public override void _Process(double delta)
    {
        if (_renderer is not null) Sync();
    }

    private void SetTreatment(DepthTreatment treatment) => SetTreatmentIndex((int)treatment);

    private void SetTreatmentIndex(int treatment)
    {
        if (_capturing) return;
        ApplyTreatmentIndex(treatment);
        ReplaceRenderer();
    }

    private void ApplyTreatmentIndex(int index)
    {
        if (index < 0 || index >= Treatments.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (TerrainStudy) _terrainTreatment = index;
        else _treatment = (DepthTreatment)index;
    }

    private void ToggleOverhead()
    {
        _elevation = _elevation == 90 ? 35 : 90;
        _renderer.SetDepthStudyCamera(_azimuth, _elevation);
        Sync();
    }

    private void SetSubject(int subject)
    {
        if (_capturing) return;
        _subject = subject;
        _panOffset = Vector2.Zero;
        _panning = _rotating = false;
        BuildFixture();
        ReplaceRenderer();
    }

    private void Rotate(float degrees)
    {
        _azimuth = Mathf.PosMod(_azimuth + degrees, 360);
        _renderer.SetDepthStudyCamera(_azimuth, _elevation);
        Sync();
    }

    private void Tilt()
    {
        _elevation = _elevation == 20 ? 35 : _elevation == 35 ? 50 : 20;
        _renderer.SetDepthStudyCamera(_azimuth, _elevation);
        Sync();
    }

    private void Zoom(int direction)
    {
        _session.ZoomBy(direction);
        _renderer.SetCameraOffset(_session, _panOffset);
        Sync();
    }

    private void ResetCamera()
    {
        _azimuth = PerspectiveCity.InitialAzimuth;
        _elevation = 35;
        _panOffset = Vector2.Zero;
        _renderer.ResetCameraOffset();
        _session.SetZoom(TerrainStudy ? _subject == 3 ? -2 : -1 : _subject >= 2 ? -1 : 0);
        _renderer.SetDepthStudyCamera(_azimuth, _elevation);
        Sync();
    }

    private void HandlePictureInput(InputEvent input)
    {
        if (_capturing) return;
        if (input is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Right) _rotating = button.Pressed;
            if (button.ButtonIndex == MouseButton.Middle)
            {
                _panning = button.Pressed;
                if (_panning) _panAnchor = _renderer.GroundPlanePoint(button.Position);
            }
            if (!button.Pressed) return;
            if (button.ButtonIndex == MouseButton.WheelUp) Zoom(1);
            if (button.ButtonIndex == MouseButton.WheelDown) Zoom(-1);
            if (button.ButtonIndex == MouseButton.Left)
            {
                _status.Text = _renderer.PickBuilding(button.Position, out var owner)
                    ? $"Model hit at {owner.X},{owner.Y} (prototype geometry; no gameplay edits)."
                    : "Terrain / background (no model hit).";
                if (TerrainStudy && !_renderer.PickBuilding(button.Position, out _))
                {
                    var cell = _renderer.GroundCell(button.Position);
                    if (_renderer.PickNaturalFeature(button.Position, out var feature)) cell = feature;
                    _status.Text = _session.Game.Map.InBounds(cell)
                        ? $"{_session.Game.Map.FeatureAt(cell.X, cell.Y)?.Name ?? _session.Game.Map.TerrainAt(cell.X, cell.Y).Name} {cell.X},{cell.Y} | height " +
                            $"{TerrainSprite.Elevation(_session.Game.Map, cell.X, cell.Y):0.00} | prototype only"
                        : "Outside the staging map.";
                }
            }
        }
        if (input is InputEventMouseMotion motion && _rotating &&
            (motion.ButtonMask & MouseButtonMask.Right) != 0) Rotate(motion.Relative.X * .35f);
        if (input is InputEventMouseMotion pan && _panning &&
            (pan.ButtonMask & MouseButtonMask.Middle) != 0)
        {
            var delta = _panAnchor - _renderer.GroundPlanePoint(pan.Position);
            _panOffset += new Vector2(delta.X, delta.Z);
            _renderer.SetCameraOffset(_session, _panOffset);
            Sync();
        }
    }

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (_capturing || input is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Key1: SetTreatment(DepthTreatment.Baseline); break;
            case Key.Key2: SetTreatment(DepthTreatment.Lighting); break;
            case Key.Key3: SetTreatment(DepthTreatment.Relief); break;
            case Key.Key4: SetTreatment(DepthTreatment.Combined); break;
            case Key.Key5 when TerrainStudy: SetTreatmentIndex(4); break;
            case Key.O when TerrainStudy: ToggleOverhead(); break;
            case Key.Q: Rotate(-30); break;
            case Key.E: Rotate(30); break;
            case Key.T: Tilt(); break;
            case Key.R: ResetCamera(); break;
            case Key.Minus: Zoom(-1); break;
            case Key.Equal: case Key.Plus: Zoom(1); break;
            case Key.Bracketleft: SetSubject((_subject + Subjects.Length - 1) % Subjects.Length); break;
            case Key.Bracketright: SetSubject((_subject + 1) % Subjects.Length); break;
            case Key.C: _ = CaptureSafely(); break;
            case Key.Escape: GetTree().Quit(); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    private async Task Frames(int count = 4)
    {
        for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task CaptureSafely()
    {
        try { await CaptureTreatments(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            GD.PushError($"Depth comparison capture failed: {error.Message}");
            _status.Text = $"Capture failed: {error.Message}";
        }
    }

    private async Task CaptureTreatments(string? variant = null)
    {
        if (DisplayServer.GetName() == "headless")
            throw new InvalidOperationException("Depth comparison captures require a graphical renderer.");
        _capturing = true;
        foreach (var button in _controls) button.Disabled = true;
        int original = ActiveTreatment;
        try
        {
            string prefix = Path.GetFullPath(CapturePrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
            prefix = Path.Combine(Path.GetDirectoryName(prefix)!, Path.GetFileNameWithoutExtension(prefix));
            if (variant is not null) prefix += "-" + variant;
            for (int i = 0; i < Treatments.Length; i++)
            {
                ApplyTreatmentIndex(i);
                ReplaceRenderer();
                _status.Text = $"Matched capture | subject {_subject + 1}/{Subjects.Length} | treatment {i + 1}/{Treatments.Length}";
                await Frames();
                var drawn = ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                _renderer.RequestRender();
                RenderingServer.ForceDraw();
                await drawn;
                using var image = GetViewport().GetTexture().GetImage();
                string label = TerrainStudy ? ((TerrainStudyTreatment)i).ToString() : _treatment.ToString();
                string path = $"{prefix}-subject-{_subject + 1}-{label.ToLowerInvariant()}.png";
                var error = image.SavePng(path);
                if (error != Error.Ok) throw new IOException($"Could not save {path}: {error}.");
                GD.Print($"TERMCITY_DEPTH_CAPTURE {path}");
            }
            _status.Text = $"Saved {Treatments.Length} matched captures: {prefix}-subject-{_subject + 1}-*.png";
        }
        finally
        {
            ApplyTreatmentIndex(original);
            ReplaceRenderer();
            _capturing = false;
            foreach (var button in _controls) button.Disabled = false;
        }
    }

    private async void RunSmoke()
    {
        try
        {
            await Frames();
            if (TerrainStudy)
            {
                await RunTerrainSmoke();
                return;
            }
            Require(!_session.Game.Map.BuildingFootprints.Any(pair =>
                !_session.Game.Map.InBounds(pair.Value.X, pair.Value.Y)), "Fixture footprints must stay on map.");
            foreach (int subject in Enumerable.Range(0, Subjects.Length))
            {
                SetSubject(subject);
                await Frames();
                foreach (DepthTreatment treatment in Enum.GetValues<DepthTreatment>())
                {
                    SetTreatment(treatment);
                    await Frames();
                    Require(_renderer.DepthTreatment == treatment && _renderer.GeometryBuilds > 0,
                        "Each explicit treatment must render.");
                    Require(_renderer.StablePixelMaterials, "Treatments must preserve filtered pixel materials.");
                    var timings = new List<double>();
                    foreach (int zoom in new[] { -1, 0, 1 })
                    {
                        _session.SetZoom(zoom);
                        foreach (float tilt in new[] { 20f, 35f, 50f })
                            foreach (float rotation in new[] { 0f, 90f, 180f, 270f })
                            {
                                _renderer.SetDepthStudyCamera(PerspectiveCity.InitialAzimuth + rotation, tilt);
                                Sync();
                                await Frames(2);
                                Require(HasPickableModel(),
                                    $"Subject {subject}, {treatment}, zoom {zoom}, tilt {tilt}, rotation {rotation} must be clickable.");
                            }
                    }
                    ResetCamera();
                    await Frames();
                    if (treatment == DepthTreatment.Combined && DisplayServer.GetName() != "headless")
                        await VerifyGroundShadows();
                    int builds = _renderer.GeometryBuilds;
                    if (subject < 2)
                    {
                        var footprint = _session.Game.Map.BuildingFootprints.Single().Value;
                        var anchor = new Pos(footprint.X, footprint.Y);
                        var roof = _renderer.BuildingVolumes(anchor).MaxBy(volume => volume.End.Y);
                        var point = _renderer.ScreenPoint(new(roof.GetCenter().X, roof.End.Y, roof.GetCenter().Z));
                        Require(_renderer.PickBuilding(point, out var owner) && owner == anchor,
                            "Raised roofs and overhangs must retain their model owner.");
                        var building = _session.Game.Map.BuildingAt(anchor.X, anchor.Y)!;
                        _session.PlaceCursor(new(32, 32));
                        _session.PreviewBuilding(building);
                        Sync();
                        Require(_renderer.BuildingsDrawn > 1, "The same relief must render in placement ghosts.");
                        Require(!_session.Game.CanPlaceBuilding(building, -1, -1),
                            "Study treatments must not make off-map placement valid.");
                        _session.CancelPreview();
                        _session.ClosePrompt();
                        _session.ClearSelection();
                        Sync();
                        Require(_renderer.GeometryBuilds == builds, "Selection and placement indicators must reuse city geometry.");
                    }
                    for (int i = 0; i < 12; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        Rotate(1);
                        await Frames(1);
                        timings.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    Require(_renderer.GeometryBuilds == builds, "Small rotations must preserve cached geometry.");
                    timings.Sort();
                    GD.Print($"TERMCITY_DEPTH_SAMPLE subject={subject} treatment={treatment} " +
                        $"frame-p90={timings[(int)(timings.Count * .9)]:0.0}ms objects={_renderer.SceneObjectCount}");
                    ResetCamera();
                }
                if (DisplayServer.GetName() != "headless")
                {
                    SetSubject(subject);
                    await CaptureTreatments();
                    if (subject == 3)
                    {
                        _session.SetZoom(0);
                        await CaptureTreatments("close");
                    }
                }
            }
            GetWindow().Size = new(640, 780);
            await Frames();
            Require(_controls.All(button => button.GetGlobalRect().End.X <= Size.X + 1 &&
                button.GetGlobalRect().End.Y <= Size.Y + 1),
                "Comparison controls must fit a narrow window.");
            if (DisplayServer.GetName() != "headless")
            {
                var drawn = ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                _renderer.RequestRender();
                RenderingServer.ForceDraw();
                await drawn;
                using var image = GetViewport().GetTexture().GetImage();
                string path = Path.ChangeExtension(CapturePrefix, null) + "-narrow.png";
                Require(image.SavePng(path) == Error.Ok, "Narrow comparison capture must save.");
            }
            GD.Print("TERMCITY_DEPTH_COMPARISON_SMOKE_OK");
            GetTree().Quit();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            GD.PushError($"Depth comparison smoke failed: {error}");
            GetTree().Quit(1);
        }
    }

    private bool HasPickableModel()
    {
        for (int y = 8; y < _picture.Size.Y; y += 16)
            for (int x = 8; x < _picture.Size.X; x += 16)
                if (_renderer.PickBuilding(new(x, y), out _)) return true;
        return false;
    }

    private async Task<Image> RenderImage()
    {
        _renderer.RequestRender();
        await Frames(2);
        var drawn = ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        _renderer.RequestRender();
        RenderingServer.ForceDraw();
        await drawn;
        return _renderer.Texture.GetImage();
    }

    private async Task VerifyGroundShadows()
    {
        using var shadowed = await RenderImage();
        _renderer.SetDepthStudyShadows(false);
        using var unshadowed = await RenderImage();
        _renderer.SetDepthStudyShadows(true);
        int darkerGroundSamples = 0;
        for (int y = 8; y < _picture.Size.Y; y += 8)
            for (int x = 8; x < _picture.Size.X; x += 8)
            {
                if (_renderer.PickBuilding(new(x, y), out _)) continue;
                var ground = _renderer.GroundPlanePoint(new(x, y));
                if (!_session.Game.Map.InBounds((int)MathF.Floor(ground.X), (int)MathF.Floor(ground.Z))) continue;
                int px = (int)(x * shadowed.GetWidth() / _picture.Size.X);
                int py = (int)(y * shadowed.GetHeight() / _picture.Size.Y);
                var a = shadowed.GetPixel(px, py);
                var b = unshadowed.GetPixel(px, py);
                if (b.R + b.G + b.B - a.R - a.G - a.B > .04f) darkerGroundSamples++;
            }
        Require(darkerGroundSamples >= 8,
            $"Subject {_subject} must cast a visible ground shadow, found {darkerGroundSamples} darkened samples.");
        GD.Print($"TERMCITY_DEPTH_SHADOW_OK subject={_subject} ground-samples={darkerGroundSamples}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
