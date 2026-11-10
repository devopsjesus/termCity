using Godot;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public partial class OrcMotionStudy : Control
{
    private const string AssetPath = "res://Assets/OrcMotion/hammer.orcmotion";
    private OrcMotionLibrary _library = null!;
    private OrcMotionTransform[] _pose = [];
    private readonly Dictionary<int, (MultiMesh Mesh, int[] Parts)> _batches = [];
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly HSlider _scrubber = new() { Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Button _play = new() { Text = "Play", ToggleMode = true };
    private readonly SubViewport _viewport = new() { OwnWorld3D = true };
    private readonly SubViewportContainer _picture = new() { Stretch = true, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly Camera3D _camera = new() { Fov = 43, Near = .05f, Far = 30 };
    private readonly List<Button> _clipButtons = [];
    private readonly GridContainer _clipGrid = new() { Columns = 3 };
    private readonly Basis _facing = new(Vector3.Up, Mathf.Pi);
    private int _clip = 1;
    private double _time;
    private float _yaw = .58f, _pitch = .32f, _distance = 6.9f;
    private bool _running, _updating, _dragging, _smokeStarted;
    private Vector2 _pointer;
    private string _picked = "No part selected";
    private OrcMotionClip Clip => _library.Clips[_clip];

    public override void _Ready()
    {
#if !DEBUG
        GD.PushError("The orc motion preview is available only in Debug builds.");
        GetTree().Quit(1);
        return;
#endif
        using var asset = new MemoryStream(Godot.FileAccess.GetFileAsBytes(AssetPath), writable: false);
        _library = OrcMotionLibrary.Load(asset);
        _pose = new OrcMotionTransform[_library.Parts.Count];
        GetWindow().MinSize = new(390, 640);
        Theme = new()
        {
            DefaultFont = GD.Load<Font>("res://Assets/DejaVuSansMono.ttf"), DefaultFontSize = 14,
        };
        Theme.SetStylebox("panel", "TooltipPanel", TerminalFrame.CreateTooltipStyle());
        Theme.SetColor("font_color", "TooltipLabel", Colors.White);
        var plain = new StyleBoxFlat { BgColor = Colors.Black };
        plain.SetContentMarginAll(4);
        Theme.SetStylebox("normal", "Button", plain);
        var active = new StyleBoxFlat { BgColor = new("#151515"), BorderColor = Colors.White };
        active.SetBorderWidthAll(1); active.SetContentMarginAll(4);
        foreach (string state in new[] { "hover", "pressed", "focus" }) Theme.SetStylebox(state, "Button", active);
        Theme.SetColor("font_color", "Button", Colors.White);
        Theme.SetColor("font_pressed_color", "Button", new("#70d7ff"));
        var background = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background); background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 0);
        AddChild(layout); layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var header = new TerminalFrame();
        layout.AddChild(header);
        var title = new Label { Text = "ORC / NATIVE MOTION PREVIEW", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeColorOverride("font_color", new("#70d7ff")); header.AddChild(title);
        var mapFrame = new TerminalFrame { SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddChild(mapFrame);
        _picture.CustomMinimumSize = new(0, 240);
        mapFrame.AddChild(_picture);
        _viewport.Msaa3D = Viewport.Msaa.Msaa4X;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _picture.AddChild(_viewport);
        _viewport.AddChild(_camera);
        _picture.GuiInput += ViewInput;
        var controls = new TerminalFrame();
        layout.AddChild(controls);
        var tools = new VBoxContainer();
        tools.AddThemeConstantOverride("separation", 4); controls.AddChild(tools);
        tools.AddChild(_scrubber);
        _scrubber.ValueChanged += value =>
        {
            if (_updating) return;
            SetRunning(false); _time = value / 30; DrawPose();
        };
        tools.AddChild(_status);
        var playback = new HBoxContainer(); tools.AddChild(playback);
        playback.AddChild(_play);
        _play.Pressed += () => SetRunning(!_running);
        AddButton(playback, "Restart", () => { _time = 0; DrawPose(); });
        AddButton(playback, "Front", () => { _yaw = 0; _pitch = .18f; UpdateCamera(); });
        AddButton(playback, "Side", () => { _yaw = -Mathf.Pi / 2; _pitch = .18f; UpdateCamera(); });
        tools.AddChild(_clipGrid);
        for (int i = 0; i < _library.Clips.Count; i++)
        {
            int index = i;
            var button = AddButton(_clipGrid, _library.Clips[i].Name.Replace("Approved pose ", "Pose "), () => SelectClip(index));
            button.ToggleMode = true; _clipButtons.Add(button);
        }
        tools.AddChild(new Label
        {
            Text = "Drag: orbit | wheel: zoom | click: inspect\nVisual preview only; combat rules are unchanged.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        BuildModel();
        Resized += () => _clipGrid.Columns = Size.X < 600 ? 2 : 3;
        _clipGrid.Columns = Size.X < 600 ? 2 : 3;
        SelectClip(1);
        GD.Print($"ORC MOTION PREVIEW READY parts={_pose.Length} clips={_library.Clips.Count} rate={_library.SampleRate}");
    }

    private static Button AddButton(Node parent, string text, Action action)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        parent.AddChild(button); button.Pressed += action;
        return button;
    }

    private void BuildModel()
    {
        var cube = new BoxMesh { Size = Vector3.One };
        foreach (var group in _library.Parts.Select((part, index) => (part, index)).GroupBy(p => p.part.Color))
        {
            var ids = group.Select(p => p.index).ToArray();
            var mesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = cube, InstanceCount = ids.Length,
            };
            _viewport.AddChild(new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = PixelMaterial(group.Key) });
            _batches.Add(group.Key, (mesh, ids));
        }
        var ground = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = cube, InstanceCount = 64,
        };
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++)
        {
            int index = x * 8 + z;
            ground.SetInstanceTransform(index, new(Basis.Identity.Scaled(new(.59f, .04f, .59f)),
                new((x - 3.5f) * .6f, -.02f, (z - 3.5f) * .6f)));
            ground.SetInstanceColor(index, new((x + z) % 2 == 0 ? "#30412a" : "#253323"));
        }
        _viewport.AddChild(new MultiMeshInstance3D
        {
            Multimesh = ground,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true },
        });
        UpdateCamera();
    }

    private static ShaderMaterial PixelMaterial(int rgb)
    {
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded;
                uniform vec4 tint : source_color;
                varying vec3 world_normal;
                void vertex() { world_normal = MODEL_NORMAL_MATRIX * NORMAL; }
                void fragment() {
                    float shade = .64 + .36 * max(0., dot(normalize(world_normal), normalize(vec3(.4,.85,-.7))));
                    ALBEDO = tint.rgb * shade;
                }
                """,
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("tint", new Color(((rgb >> 16) & 255) / 255f,
            ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f));
        return material;
    }

    private void SelectClip(int index)
    {
        _clip = index; _time = 0; SetRunning(false);
        for (int i = 0; i < _clipButtons.Count; i++) _clipButtons[i].ButtonPressed = i == index;
        _play.Disabled = Clip.Duration == 0;
        DrawPose();
    }

    private void SetRunning(bool running)
    {
        _running = running && Clip.Duration > 0;
        _play.ButtonPressed = _running; _play.Text = _running ? "Pause" : "Play";
    }

    public override void _Process(double delta)
    {
        if (_library is null) return;
        if (_dragging && !Input.IsMouseButtonPressed(MouseButton.Left)) _dragging = false;
        if (_running && GetWindow().Mode != Window.ModeEnum.Minimized)
        {
            _time = (_time + Math.Min(delta, .1)) % Clip.Duration;
            DrawPose();
        }
        if (!_smokeStarted && OS.GetCmdlineUserArgs().Contains("--orc-preview-smoke"))
        {
            _smokeStarted = true;
            RunSmoke();
        }
    }

    private Transform3D Transform(OrcMotionTransform pose)
    {
        var q = pose.Rotation;
        var rotation = new Basis(new Quaternion(q.X, q.Y, q.Z, q.W));
        var scale = pose.Scale;
        var p = pose.Position;
        var local = new Basis(rotation.X * scale.X, rotation.Y * scale.Y, rotation.Z * scale.Z);
        return new(_facing * local,
            _facing * new Vector3(p.X, p.Y, p.Z));
    }

    private void DrawPose()
    {
        Clip.Sample(_time, _pose);
        foreach (var (mesh, ids) in _batches.Values)
            for (int i = 0; i < ids.Length; i++) mesh.SetInstanceTransform(i, Transform(_pose[ids[i]]));
        _updating = true;
        _scrubber.MaxValue = Math.Round(Clip.Duration * 30);
        _scrubber.Value = Math.Round(_time * 30);
        _scrubber.Editable = Clip.Duration > 0;
        _updating = false;
        _status.Text = $"{Clip.Name} | frame {_scrubber.Value:0}/{_scrubber.MaxValue:0}\n" +
            $"Right wrist {Clip.SampleArm(_time, true).WristAngleDegrees:0.0} deg | {_picked}";
    }

    private void UpdateCamera()
    {
        var target = new Vector3(0, 1.70f, 0);
        _camera.Position = target + new Vector3(-Mathf.Sin(_yaw) * Mathf.Cos(_pitch),
            Mathf.Sin(_pitch), -Mathf.Cos(_yaw) * Mathf.Cos(_pitch)) * _distance;
        _camera.LookAt(target);
    }

    private void ViewInput(InputEvent input)
    {
        if (input is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.WheelUp || mouse.ButtonIndex == MouseButton.WheelDown)
            {
                if (!mouse.Pressed) return;
                _distance = Math.Clamp(_distance + (mouse.ButtonIndex == MouseButton.WheelUp ? -.25f : .25f), 4.4f, 9);
                UpdateCamera(); AcceptEvent();
            }
            if (mouse.ButtonIndex == MouseButton.Left)
            {
                if (mouse.Pressed) { _dragging = true; _pointer = mouse.Position; }
                else
                {
                    if (_dragging && mouse.Position.DistanceTo(_pointer) < 4) Pick(mouse.Position);
                    _dragging = false;
                }
            }
        }
        if (input is InputEventMouseMotion motion && _dragging)
        {
            _yaw -= motion.Relative.X * .009f;
            _pitch = Math.Clamp(_pitch + motion.Relative.Y * .007f, .10f, 1.15f);
            UpdateCamera();
        }
    }

    private void Pick(Vector2 point)
    {
        var origin = _camera.ProjectRayOrigin(point);
        var direction = _camera.ProjectRayNormal(point);
        float nearest = float.PositiveInfinity;
        int picked = -1;
        for (int i = 0; i < _pose.Length; i++)
        {
            var inverse = Transform(_pose[i]).AffineInverse();
            var localOrigin = inverse * origin;
            var localDirection = inverse.Basis * direction;
            float enter = 0, exit = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(localDirection[axis]) < .000001f)
                {
                    if (Math.Abs(localOrigin[axis]) > .5f) exit = -1;
                    continue;
                }
                float a = (-.5f - localOrigin[axis]) / localDirection[axis];
                float b = (.5f - localOrigin[axis]) / localDirection[axis];
                enter = Math.Max(enter, Math.Min(a, b)); exit = Math.Min(exit, Math.Max(a, b));
            }
            if (exit >= enter && enter < nearest) { nearest = enter; picked = i; }
        }
        _picked = picked < 0 ? "No part selected" : _library.Parts[picked].Owner + "/" + _library.Parts[picked].Tag;
        DrawPose();
    }

    private async void RunSmoke()
    {
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (int clip in Enumerable.Range(0, _library.Clips.Count))
            {
                SelectClip(clip);
                foreach (double fraction in new[] { 0d, .25, .5, .75, 1 })
                {
                    _time = Clip.Duration * fraction; DrawPose();
                }
            }
            SelectClip(1); _time = 79 / 30d; DrawPose();
            var point = new Vector3(.25f, -.25f, .5f);
            foreach (var pose in _pose)
            {
                var expected = System.Numerics.Vector3.Transform(new(point.X, point.Y, point.Z), pose.Matrix);
                var actual = Transform(pose) * point;
                if (actual.DistanceTo(_facing * new Vector3(expected.X, expected.Y, expected.Z)) > .00001f)
                    throw new InvalidOperationException("Native transform/handedness conversion failed.");
            }
            var part = Transform(_pose[0]);
            Pick(_camera.UnprojectPosition(part.Origin));
            if (_picked == "No part selected") throw new InvalidOperationException("Native box picking failed.");
            int hammer = Enumerable.Range(0, _pose.Length).First(i => _library.Parts[i].Tag == "weapon-head");
            Pick(_camera.UnprojectPosition(Transform(_pose[hammer]).Origin));
            if (!_picked.StartsWith("weapon/", StringComparison.Ordinal))
                throw new InvalidOperationException("Visible hammer head picking failed.");
            foreach (float yaw in new[] { 0, -Mathf.Pi / 2, Mathf.Pi / 2 })
            {
                _yaw = yaw; UpdateCamera();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            GetWindow().Size = new(390, 780);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            float width = GetViewportRect().Size.X;
            if (_picture.GetGlobalRect().End.X > width + .5f ||
                _scrubber.GetGlobalRect().End.X > width + .5f ||
                _clipButtons.Any(button => button.GetGlobalRect().End.X > width + .5f))
                throw new InvalidOperationException("Narrow preview overflow.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string? capture = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--orc-preview-capture="));
            if (capture is not null)
                SaveCapture(capture["--orc-preview-capture=".Length..] + ".narrow.png");
            GetWindow().Size = new(1000, 900); _yaw = .58f; _pitch = .32f; UpdateCamera();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (capture is not null)
                SaveCapture(capture["--orc-preview-capture=".Length..]);
            GD.Print($"ORC PREVIEW SMOKE PASS parts={_pose.Length} clips={_library.Clips.Count} native-msaa={_viewport.Msaa3D}");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"ORC PREVIEW SMOKE FAILED: {error}");
            GetTree().Quit(1);
        }
    }

    private void SaveCapture(string path)
    {
        var result = GetViewport().GetTexture().GetImage().SavePng(path);
        if (result != Error.Ok) throw new IOException($"Preview capture failed: {result}");
    }
}
