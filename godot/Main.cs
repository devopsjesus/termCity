using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class Main : Control
{
    public GameSession Session { get; private set; } = null!;
    public TerminalMap Map { get; private set; } = null!;
    private Label _hud = null!;
    private Label _status = null!;
    private PanelContainer _modal = null!;
    private PrototypeOptions _options = null!;
    private object? _shownModal;
    private bool _focused = true;
    private bool _selecting;
    private bool _panning;
    private Pos _panAnchor;
    private double _hudElapsed;
    private bool _started;

    public override void _Ready()
    {
        try
        {
            _options = PrototypeOptions.Parse(OS.GetCmdlineUserArgs());
            if (_options.SmokeTest && DisplayServer.GetName() == "headless")
            {
                GetWindow().ContentScaleSize = new Vector2I(1200, 720);
                GetWindow().ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            }
            var game = CityGame.New(_options.Config);
            game.Paused = true;
            string savePath = ProjectSettings.GlobalizePath(
                _options.SmokeTest ? "user://smoke/quicksave.json" : "user://quicksave.json");
            Session = new GameSession(game, savePath);
            Session.SetMessage("Godot prototype: build roads, zone nearby homes, then press P to resume.");
            var font = GD.Load<FontFile>("res://Assets/DejaVuSansMono.ttf")
                ?? throw new InvalidOperationException("Could not load the bundled map font.");
            Map = new TerminalMap { Session = Session, CellFont = font };
            Map.VerifyGlyphs(game.Map.Content);
            CreateLayout(font);
            Session.Changed += OnChanged;
            Session.CameraChanged += OnCameraChanged;
            Session.SelectionChanged += OnSelectionChanged;
            Session.QuitRequested += Quit;
            GetTree().AutoAcceptQuit = false;
            GetWindow().FocusEntered += OnFocusEntered;
            GetWindow().FocusExited += OnFocusExited;
            _started = true;
            UpdateUi();
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

    private void CreateLayout(Font font)
    {
        Theme = new Theme { DefaultFont = font, DefaultFontSize = 16 };
        var layout = new VBoxContainer();
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(layout);
        _hud = new Label { CustomMinimumSize = new Vector2(0, 28), ClipText = true };
        layout.AddChild(_hud);
        var controls = new Label
        {
            Text = "Arrows: move | Shift: select | Click/drag: select | Middle drag/wheel: pan | +/-: zoom\nP/Space: pause | 1/2/3: speed | R/C/I: zone | B: road preview | Enter: confirm | Esc: cancel | Q: quit",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        controls.AddThemeFontSizeOverride("font_size", 13);
        layout.AddChild(controls);
        layout.AddChild(Map);
        Map.GuiInput += OnMapInput;
        _status = new Label { CustomMinimumSize = new Vector2(0, 28), ClipText = true };
        layout.AddChild(_status);
        _modal = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modal.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _modal.Resized += CenterModal;
        AddChild(_modal);
    }

    public override void _Process(double delta)
    {
        if (!_started)
        {
            return;
        }
        if (_focused)
        {
            Session.Update(delta);
        }
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
        Map.Invalidate(true);
        UpdateUi();
    }

    private void OnSelectionChanged()
    {
        Map.Invalidate(false);
        UpdateUi();
    }

    private void UpdateUi()
    {
        var game = Session.Game;
        _hud.Text = $" TERMCITY GODOT | ${game.Money:N0} | Y{game.Year} W{game.WeekOfYear:00} D{game.Day + 1}" +
            $" | POP {game.Stats.Population:N0} | {(game.Paused ? "PAUSED" : game.Speed.ToString().ToUpperInvariant())} | {Session.ZoomLabel}";
        _status.Text = Session.MessageVisible ? Session.Message : CellInspector.Summary(game, Session.Cursor);
        _status.Modulate = Session.MessageVisible && Session.MessageKind == MessageKind.Error
            ? new Color("#ff7777") : Colors.White;
        UpdateModal();
    }

    private void UpdateModal()
    {
        object? state = Session.Prompt ?? (object?)Session.Preview;
        if (ReferenceEquals(state, _shownModal))
        {
            return;
        }
        _shownModal = state;
        foreach (var child in _modal.GetChildren())
        {
            _modal.RemoveChild(child);
            child.QueueFree();
        }
        _modal.Visible = state is not null;
        if (state is null)
        {
            return;
        }
        StopPointerGesture();
        var content = new VBoxContainer();
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        }
        margin.AddChild(content);
        _modal.AddChild(margin);
        _modal.Size = new Vector2(480, 180);
        _modal.Position = (Size - _modal.Size) / 2;
        content.AddChild(new Label
        {
            Text = Session.Prompt?.Title ?? "Confirm placement",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.AddChild(new Label
        {
            Text = Session.Prompt?.Text ?? Session.Preview!.Summary,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(440, 0),
        });
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        if (Session.Prompt is { } prompt)
        {
            if (prompt.Input is not null)
            {
                var input = new LineEdit { Text = prompt.Input };
                input.TextChanged += text => prompt.Input = text;
                content.AddChild(input);
            }
            for (int i = 0; i < prompt.Choices.Count; i++)
            {
                int index = i;
                AddButton(buttons, prompt.Choices[i].Label, () => Session.SelectPrompt(index));
            }
        }
        else
        {
            AddButton(buttons, "Confirm [Enter]", () => Session.ConfirmPreview());
            AddButton(buttons, "Cancel [Esc]", Session.CancelPreview);
        }
        content.AddChild(buttons);
        if (buttons.GetChildCount() > 0)
        {
            buttons.GetChild<Button>(0).GrabFocus();
        }
    }

    private static void AddButton(HBoxContainer buttons, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        buttons.AddChild(button);
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!_started || input is not InputEventKey { Pressed: true } key)
        {
            return;
        }
        if (HandleKey(key))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private bool HandleKey(InputEventKey key)
    {
        var code = key.Keycode;
        if (Session.Prompt is { } prompt)
        {
            if (code == Key.Escape)
            {
                Session.ClosePrompt();
            }
            else if (code == Key.Enter && prompt.Choices.Count == 1)
            {
                Session.SelectPrompt(0);
            }
            return true;
        }
        if (Session.Preview is not null)
        {
            if (code is Key.Enter or Key.Y)
            {
                Session.ConfirmPreview();
            }
            else if (code is Key.Escape or Key.N)
            {
                Session.CancelPreview();
            }
            return true;
        }
        int dx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
        int dy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
        if (dx != 0 || dy != 0)
        {
            if (key.CtrlPressed)
            {
                Session.JumpCursor(dx, dy, key.ShiftPressed);
            }
            else
            {
                Session.MoveCursor(dx, dy, key.ShiftPressed);
            }
            return true;
        }
        if (key.Echo)
        {
            return true;
        }
        switch (code)
        {
            case Key.P or Key.Space: Session.TogglePause(); break;
            case Key.Key1: Session.SetSpeed(GameSpeed.Slow); break;
            case Key.Key2: Session.SetSpeed(GameSpeed.Medium); break;
            case Key.Key3: Session.SetSpeed(GameSpeed.Fast); break;
            case Key.R: Session.Zone(ZoneType.Residential); break;
            case Key.C: Session.Zone(ZoneType.Commercial); break;
            case Key.I: Session.Zone(ZoneType.Industrial); break;
            case Key.B: Session.PreviewRoad(); break;
            case Key.Plus or Key.Equal or Key.KpAdd: Session.ZoomBy(1); break;
            case Key.Minus or Key.KpSubtract: Session.ZoomBy(-1); break;
            case Key.Key0: Session.SetZoom(0); break;
            case Key.Escape: Session.ClearSelection(); break;
            case Key.Q: Session.RequestQuit(); break;
            default: return false;
        }
        return true;
    }

    private void OnMapInput(InputEvent input)
    {
        if (Session.Prompt is not null || Session.Preview is not null)
        {
            StopPointerGesture();
            return;
        }
        if (input is InputEventMouseButton button)
        {
            if (!button.Pressed)
            {
                if (button.ButtonIndex is MouseButton.Left or MouseButton.Middle)
                {
                    StopPointerGesture();
                }
                return;
            }
            if (!Map.Grid.TryCell(button.Position.X, button.Position.Y, out var cell))
            {
                return;
            }
            var position = Session.ScreenToMap(cell.X, cell.Y);
            if (!Session.Game.Map.InBounds(position))
            {
                return;
            }
            switch (button.ButtonIndex)
            {
                case MouseButton.Left:
                    _selecting = true;
                    Session.BeginDrag(position);
                    break;
                case MouseButton.Middle:
                    _panning = true;
                    _panAnchor = position;
                    break;
                case MouseButton.WheelUp:
                case MouseButton.WheelDown:
                    int direction = button.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
                    if (button.CtrlPressed)
                    {
                        Session.ZoomBy(-direction, cell.X, cell.Y);
                    }
                    else
                    {
                        Session.ScrollChars(button.ShiftPressed ? direction * 3 : 0, button.ShiftPressed ? 0 : direction * 3);
                    }
                    break;
            }
            Map.AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion &&
                 Map.Grid.TryCell(motion.Position.X, motion.Position.Y, out var cell))
        {
            if (_selecting)
            {
                Session.UpdateDrag(Session.ScreenToMap(cell.X, cell.Y));
            }
            else if (_panning)
            {
                Session.PanCamera(_panAnchor, cell.X, cell.Y);
            }
            Map.AcceptEvent();
        }
    }

    private void StopPointerGesture()
    {
        bool selecting = _selecting;
        _selecting = false;
        _panning = false;
        if (selecting)
        {
            Session.EndSelection();
        }
    }

    private void OnFocusEntered() => _focused = true;
    private void OnFocusExited()
    {
        _focused = false;
        StopPointerGesture();
    }

    private void CenterModal() => _modal.Position = (Size - _modal.Size) / 2;

    public override void _Notification(int what)
    {
        if (_started && what == NotificationWMCloseRequest)
        {
            Session.RequestQuit();
        }
        if (_started && what == NotificationResized && _modal.Visible)
        {
            CenterModal();
        }
    }

    private void Quit() => GetTree().Quit();

    public override void _ExitTree()
    {
        if (!_started)
        {
            return;
        }
        Session.Changed -= OnChanged;
        Session.CameraChanged -= OnCameraChanged;
        Session.SelectionChanged -= OnSelectionChanged;
        Session.QuitRequested -= Quit;
        GetWindow().FocusEntered -= OnFocusEntered;
        GetWindow().FocusExited -= OnFocusExited;
    }

    private async void RunSmokeTest()
    {
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            bool headless = DisplayServer.GetName() == "headless";
            var window = GetWindow();
            var initialSize = headless ? window.ContentScaleSize : window.Size;
            if (headless)
            {
                window.ContentScaleSize = new Vector2I(960, 640);
            }
            else
            {
                window.Size = new Vector2I(960, 640);
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            PrototypeSmoke.Run(this);
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
            }
            GD.Print("TERMCITY_GODOT_SMOKE_OK");
            GetTree().Quit();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            GD.PushError($"TermCity Godot smoke failed: {error}");
            GetTree().Quit(1);
        }
    }
}
