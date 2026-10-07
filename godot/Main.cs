using Godot;
using System.Buffers.Binary;
using System.Text;
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
    public CityMinimap Minimap => _panel.Minimap;
    public const int DefaultFontSize = 20, MinFontSize = 16, MaxFontSize = 28;
    public int FontSize { get; private set; } = DefaultFontSize;
    private Label? _fontSizeLabel;
    private const string FontDialogTitle = "Font size";
    private const string SidebarDialogTitle = "Sidebar size";
    private CitySplit _split = null!;
    private Label? _sidebarSizeLabel;
    private Label _hud = null!;
    private Label _brand = null!;
    private Label[] _headerLabels = [];
    private Control _nameSlot = null!;
    private LineEdit _nameEditor = null!;
    private bool _editingName;
    private bool _pausedBeforeRename;
    private double _pinchZoom;
    private Label _hudStats = null!;
    private Label _hudCalendar = null!;
    private Label _hudWeek = null!;
    private Label _hudPopulation = null!;
    private Label _hudClock = null!;
    private double _pauseGlowSeconds;
    private Label _status = null!;
    private PanelContainer _modal = null!;
    private Control _modalShield = null!;
    private CityPanel _panel = null!;
    private VBoxContainer _linePreview = null!;
    private Label _lineSummary = null!;
    private Label _modalError = null!;
    private ScrollContainer? _modalScroll;
    private float _modalContentWidth;
    private readonly List<Button> _promptButtons = [];
    private readonly List<Button> _dialogButtons = [];
    private int _promptIndex;
    private bool _helpVisible;
    private readonly EdgeScroller _scroller = new();
    private const int ScrollStep = 3;
    private double _trackpadX, _trackpadY, _trackpadZoom;
    private int _trackpadZoomLevel = int.MinValue;
    private Vector2 _pointer;
    private bool _hover;
    private string _lastInput = "";
    private GodotOptions _options = null!;
    private object? _shownModal;
    private bool _focused = true;
    private bool _selecting;
    private bool _panning;
    private Pos _panAnchor;
    private double _hudElapsed;
    private bool _started;
    public bool MusicEnabled { get; private set; } = true;
    private AudioStreamPlayer _music = null!;
    private AudioStreamGenerator? _musicTrack;
    private AudioStreamGeneratorPlayback _musicPlayback = null!;
    private readonly Vector2[] _musicFrames = new Vector2[1024];
    private GreensleevesSequence _musicSequence = null!;
    private MusicPhrase _musicPhrase = null!;
    private Task<MusicPhrase>? _nextMusicPhrase;
    private int _musicSample;
    private int _musicFrameCount;
    private bool _musicStopped;
    private bool _musicBufferWarned;
    public int MusicPhraseCount { get; private set; }

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
            var game = CityGame.New(_options.Config);
            game.Paused = true;
            string savePath = ProjectSettings.GlobalizePath(
                _options.SmokeTest ? "user://smoke/quicksave.json" : "user://quicksave.json");
            Session = new GameSession(game, savePath,
                showGuide: !_options.Load && !_options.SmokeTest && !_options.DumpMap);
            if (_options.Load && !Session.LoadFrom(_options.LoadPath ?? savePath))
            {
                throw new IOException(Session.Message);
            }
            if (_options.DumpMap)
            {
                var text = new StringBuilder($"Seed {Session.Game.Config.Seed}, {Session.Game.Map.Width}x{Session.Game.Map.Height}\n");
                for (int y = 0; y < Session.Game.Map.Height; y++)
                {
                    for (int x = 0; x < Session.Game.Map.Width; x++)
                        text.Append(CellRenderer.Render(Session.Game, x, y).Glyph);
                    text.AppendLine();
                }
                GD.Print(text.ToString());
                GetTree().Quit();
                return;
            }
            LoadFontSize();
            ApplyFontSize();
            var font = LoadBundledFont();
            Map = new TerminalMap { Session = Session, CellFont = font };
            Map.VerifyGlyphs(Session.Game.Map.Content);
            CreateLayout(font);
            CreateMusic();
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

    private const string DisplaySettingsPath = "user://display.cfg";

    private void LoadFontSize()
    {
        if (_options.SmokeTest) return;
        using var settings = new ConfigFile();
        var error = settings.Load(DisplaySettingsPath);
        if (error == Error.FileNotFound) return;
        if (error != Error.Ok)
        {
            throw new IOException($"Could not load display settings: {error}.");
        }
        var value = settings.GetValue("display", "font_size", DefaultFontSize);
        if (value.VariantType != Variant.Type.Int || value.AsInt64() is < MinFontSize or > MaxFontSize)
        {
            throw new InvalidOperationException("Saved font size must be an integer from 16 to 28.");
        }
        FontSize = value.AsInt32();
        var music = settings.GetValue("audio", "music_enabled", true);
        if (music.VariantType != Variant.Type.Bool)
            throw new InvalidOperationException("Saved music preference must be a boolean.");
        MusicEnabled = music.AsBool();
    }

    public void SetFontSize(int size)
    {
        if (size is < MinFontSize or > MaxFontSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Font size must be from 16 to 28.");
        }
        FontSize = size;
        ApplyFontSize();
        if (_fontSizeLabel is not null) _fontSizeLabel.Text = FontSize.ToString();
        StopPointerGesture();
        ResetTrackpadScroll();
        SaveDisplaySettings();
    }

    private void SaveDisplaySettings()
    {
        if (_options.SmokeTest) return;
        using var settings = new ConfigFile();
        settings.SetValue("display", "font_size", FontSize);
        settings.SetValue("audio", "music_enabled", MusicEnabled);
        var error = settings.Save(DisplaySettingsPath);
        if (error != Error.Ok)
        {
            GD.PushError($"Could not save display settings: {error}.");
            Session.SetMessage($"Could not save display/audio preferences: {error}.", MessageKind.Error);
        }
    }

    private void CreateMusic()
    {
        _musicSequence = new GreensleevesSequence(_options.SmokeTest ? 42 : null);
        _musicPhrase = _musicSequence.Next();
        MusicPhraseCount = 1;
        _nextMusicPhrase = Task.Run(() => _musicSequence.Next());
        _musicTrack = new AudioStreamGenerator
        {
            MixRate = GreensleevesTrack.SampleRate,
            BufferLength = 0.25f,
        };
        _music = new AudioStreamPlayer
        {
            Name = "CityMusic", Stream = _musicTrack,
            VolumeDb = _options.SmokeTest ? -80 : -24,
        };
        AddChild(_music);
        _music.Play();
        _musicPlayback = (AudioStreamGeneratorPlayback)_music.GetStreamPlayback();
        PumpMusic();
        _music.StreamPaused = !MusicEnabled || !_focused;
    }

    internal bool AdvanceMusicPhrase()
    {
        if (_nextMusicPhrase is null || !_nextMusicPhrase.IsCompleted) return false;
        if (_nextMusicPhrase.IsFaulted)
        {
            GD.PushError($"Music synthesis failed: {_nextMusicPhrase.Exception}");
            Session.SetMessage("Music synthesis failed; playback stopped.", MessageKind.Error);
            StopMusic();
            return false;
        }
        _musicPhrase = _nextMusicPhrase.GetAwaiter().GetResult();
        _musicSample = 0;
        MusicPhraseCount++;
        _nextMusicPhrase = Task.Run(() => _musicSequence.Next());
        return true;
    }

    private void PumpMusic()
    {
        if (_musicStopped) return;
        while (_musicPlayback.GetFramesAvailable() >= _musicFrames.Length)
        {
            while (_musicFrameCount < _musicFrames.Length)
            {
                if (_musicSample >= _musicPhrase.Pcm.Length && !AdvanceMusicPhrase())
                {
                    if (!_musicBufferWarned && !_musicStopped)
                    {
                        GD.PushWarning("Music phrase was not ready before the audio buffer ran out.");
                        _musicBufferWarned = true;
                    }
                    return;
                }
                float sample = BinaryPrimitives.ReadInt16LittleEndian(
                    _musicPhrase.Pcm.AsSpan(_musicSample, sizeof(short))) / 32768f;
                _musicFrames[_musicFrameCount++] = new Vector2(sample, sample);
                _musicSample += sizeof(short);
            }
            if (!_musicPlayback.PushBuffer(_musicFrames))
            {
                GD.PushError("Could not queue synthesized music frames.");
                StopMusic();
                return;
            }
            _musicFrameCount = 0;
        }
    }

    private void ToggleMusic()
    {
        MusicEnabled = !MusicEnabled;
        _music.StreamPaused = !MusicEnabled || !_focused;
        SaveDisplaySettings();
        if (Session.Prompt is { } prompt)
        {
            var choices = prompt.Choices.Select(choice => choice.Select == (Action)ToggleMusic
                ? choice with { Label = $"Music: {(MusicEnabled ? "ON" : "OFF")}" }
                : choice).ToArray();
            Session.ShowPrompt(prompt.Title, prompt.Text, choices, prompt.Input, prompt.Footer);
        }
    }

    private void ApplyFontSize()
    {
        var window = GetWindow();
        float scale = FontSize / 16f;
        window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        if (DisplayServer.GetName() != "headless")
        {
            window.ContentScaleSize = Vector2I.Zero;
        }
        window.ContentScaleFactor = scale;
        if (_options.SmokeTest) return;
        FitWindowToFont();
    }

    internal void FitWindowToFont()
    {
        var window = GetWindow();
        float scale = FontSize / 16f;
        var screen = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
        var usable = screen.Size;
        var minimum = new Vector2I((int)Math.Ceiling(640 * scale), (int)Math.Ceiling(480 * scale));
        window.MinSize = new Vector2I(Math.Min(minimum.X, usable.X), Math.Min(minimum.Y, usable.Y));
        var desired = new Vector2I((int)Math.Ceiling(1152 * scale), (int)Math.Ceiling(720 * scale));
        window.Size = new Vector2I(
            Math.Min(usable.X, Math.Max(window.Size.X, desired.X)),
            Math.Min(usable.Y, Math.Max(window.Size.Y, desired.Y)));
        window.Position = new Vector2I(
            Math.Clamp(window.Position.X, screen.Position.X, screen.End.X - window.Size.X),
            Math.Clamp(window.Position.Y, screen.Position.Y, screen.End.Y - window.Size.Y));
    }

    private void BeginRename()
    {
        if (_editingName || Session.Prompt is not null || Session.Preview is not null) return;
        StopPointerGesture();
        _editingName = true;
        _pausedBeforeRename = Session.Game.Paused;
        Session.Game.Paused = true;
        _nameEditor.Text = Session.Game.CityName;
        int fontSize = _hud.GetThemeFontSize("font_size");
        _nameEditor.AddThemeFontSizeOverride("font_size", fontSize);
        var font = _nameEditor.GetThemeFont("font");
        float character = font.GetStringSize("M", fontSize: fontSize).X;
        _nameEditor.Position = Vector2.Zero;
        _nameEditor.Size = _nameSlot.Size;
        _nameEditor.AddThemeConstantOverride("caret_width", (int)Math.Ceiling(character));
        _hud.Visible = false;
        _nameEditor.Visible = true;
        _nameEditor.GrabFocus();
        _nameEditor.CaretColumn = _nameEditor.Text.EnumerateRunes().Count();
        UpdateUi();
    }

    private bool FinishRename(bool commit)
    {
        if (!_editingName) return true;
        if (commit && !CityGame.IsValidCityName(_nameEditor.Text))
        {
            Session.SetMessage("City name must contain 1-16 characters.", MessageKind.Error);
            Callable.From(() => _nameEditor.GrabFocus()).CallDeferred();
            return false;
        }
        _editingName = false;
        Session.Game.Paused = _pausedBeforeRename;
        if (commit)
        {
            var result = Session.Game.RenameCity(_nameEditor.Text);
            Session.SetMessage(result.Message, result.Success ? MessageKind.Success : MessageKind.Error);
        }
        _nameEditor.ReleaseFocus();
        _nameEditor.Visible = false;
        _hud.Visible = true;
        UpdateUi();
        return true;
    }

    private static FontFile LoadBundledFont()
    {
        const string path = "res://Assets/DejaVuSansMono.ttf";
        byte[] data = Godot.FileAccess.GetFileAsBytes(path);
        if (data.Length == 0)
        {
            throw new IOException($"Could not read the bundled map font at {path}: {Godot.FileAccess.GetOpenError()}.");
        }

        return new FontFile { Data = data };
    }

    private void CreateLayout(Font font)
    {
        Theme = new Theme { DefaultFont = font, DefaultFontSize = 16 };
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 0);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(layout);
        var hud = new HBoxContainer();
        hud.AddThemeConstantOverride("separation", 0);
        _hud = new Label
        {
            Name = "CityNameDisplay",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hudStats = new Label
        {
            Name = "Budget",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hudPopulation = new Label { Name = "Population", HorizontalAlignment = HorizontalAlignment.Left };
        _hudPopulation.AddThemeColorOverride("font_color", new Color("#88ee99"));
        _hudPopulation.VerticalAlignment = VerticalAlignment.Center;
        var finances = new HBoxContainer();
        finances.AddChild(_hudPopulation);
        finances.AddChild(_hudStats);
        _hudCalendar = new Label { Name = "Calendar", HorizontalAlignment = HorizontalAlignment.Left };
        _hudCalendar.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        _hudClock = new Label { Name = "ClockStatus", HorizontalAlignment = HorizontalAlignment.Right };
        _hudWeek = new Label { Name = "WeekProgress", HorizontalAlignment = HorizontalAlignment.Center };
        _hudWeek.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        _hudCalendar.VerticalAlignment = _hudWeek.VerticalAlignment = _hudClock.VerticalAlignment = VerticalAlignment.Center;
        var calendar = new HBoxContainer();
        _hudWeek.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        calendar.AddChild(_hudCalendar);
        calendar.AddChild(_hudWeek);
        calendar.AddChild(_hudClock);
        var names = new HBoxContainer { Name = "CityNameHeader", Alignment = BoxContainer.AlignmentMode.End };
        _brand = new Label { Name = "AppTitle", Text = "TermCity", VerticalAlignment = VerticalAlignment.Center };
        _brand.AddThemeColorOverride("font_color", new Color("#70b7ff"));
        names.AddChild(_brand);
        _hud.HorizontalAlignment = HorizontalAlignment.Right;
        _hud.AutowrapMode = TextServer.AutowrapMode.Off;
        _hud.MouseFilter = MouseFilterEnum.Stop;
        _nameEditor = new LineEdit { Name = "CityNameEditor", Visible = false, MaxLength = CityGame.MaxCityNameLength,
            CaretBlink = true, CaretBlinkInterval = 0.5f, Alignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        _nameEditor.AddThemeFontSizeOverride("font_size", 20);
        _nameEditor.AddThemeColorOverride("font_color", new Color("#70b7ff"));
        _nameEditor.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        _nameEditor.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _nameEditor.FocusExited += () => FinishRename(true);
        void NameInput(InputEvent input)
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
            {
                BeginRename();
                GetViewport().SetInputAsHandled();
            }
        }
        _hud.GuiInput += NameInput;
        _nameSlot = new Control { Name = "CityNameSlot", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true };
        _hud.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _nameSlot.AddChild(_hud);
        _nameEditor.AddThemeConstantOverride("minimum_character_width", 0);
        _nameEditor.AddThemeColorOverride("caret_color", new Color("#70b7ff"));
        _nameSlot.AddChild(_nameEditor);
        names.AddChild(_nameSlot);
        var titleFrame = Frame(names);
        _hud.AddThemeFontSizeOverride("font_size", 20);
        hud.AddChild(titleFrame);
        hud.AddChild(Frame(calendar));
        hud.AddChild(Frame(finances));
        layout.AddChild(hud);
        _headerLabels = [_brand, _hud, _hudCalendar, _hudWeek, _hudClock, _hudPopulation, _hudStats];
        var functions = new HFlowContainer();
        AddToolbarButton(functions, "F1 HELP", ShowHelp);
        AddToolbarButton(functions, "F3 FONT", ShowFontDialog);
        AddToolbarButton(functions, "F5 SAVE", () => Session.QuickSave());
        AddToolbarButton(functions, "F6 GUIDE", Session.ShowGuide);
        AddToolbarButton(functions, "F7 REPORT", Session.ShowReport);
        AddToolbarButton(functions, "F8 GROWTH", Session.ShowGrowthReport);
        AddToolbarButton(functions, "F9 LOAD", () => Session.RequestLoad(Session.SavePath));
        AddToolbarButton(functions, "ESC MENU", ShowCityMenu);
        layout.AddChild(Frame(functions));
        var body = new CitySplit { Name = "CityDivider", SizeFlagsVertical = SizeFlags.ExpandFill };
        _split = body;
        var city = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(city);
        var outerBorder = Frame(Map);
        outerBorder.SizeFlagsVertical = SizeFlags.ExpandFill;
        city.AddChild(outerBorder);
        _linePreview = new VBoxContainer { Visible = false };
        _lineSummary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _linePreview.AddChild(_lineSummary);
        var lineButtons = new HBoxContainer();
        AddButton(lineButtons, "Confirm [Enter]", () => Session.ConfirmPreview());
        AddButton(lineButtons, "Cancel [Esc]", Session.CancelPreview);
        _linePreview.AddChild(lineButtons);
        city.AddChild(_linePreview);
        var sidebar = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        var sideScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _panel = new CityPanel { Session = Session, ZoomBy = delta => Map.ZoomBy(delta),
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sideScroll.AddChild(_panel);
        sidebar.AddChild(sideScroll);
        body.AddChild(sidebar);
        layout.AddChild(body);
        Map.GuiInput += OnMapInput;
        Map.MouseExited += () => _hover = false;
        _status = new Label { Name = "Status", CustomMinimumSize = new Vector2(0, 28),
            AutowrapMode = TextServer.AutowrapMode.WordSmart };
        layout.AddChild(_status);
        _modalShield = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modalShield.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_modalShield);
        _modal = CreateMapBorder(false);
        _modal.Name = "CityDialog";
        _modal.Visible = false;
        _modal.MouseFilter = MouseFilterEnum.Stop;
        var dialogBody = new PanelContainer();
        dialogBody.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _modal.AddChild(dialogBody);
        _modal.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _modal.Resized += CenterModal;
        _modalShield.AddChild(_modal);
    }

    internal static PanelContainer Frame(Control content)
    {
        var outer = CreateMapBorder(false);
        outer.AddChild(content);
        return outer;
    }

    private static PanelContainer CreateMapBorder(bool expandVertical = true)
    {
        var border = new TerminalFrame
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = expandVertical ? SizeFlags.ExpandFill : SizeFlags.Fill,
        };
        return border;
    }

    private void AddToolbarButton(Container toolbar, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None };
        button.Pressed += () =>
        {
            if (Session.Prompt is null && Session.Preview is null) action();
        };
        toolbar.AddChild(button);
    }

    public override void _Process(double delta)
    {
        if (!_started)
        {
            return;
        }
        if (_focused && MusicEnabled) PumpMusic();
        if (_focused && !_editingName)
        {
            Session.Update(delta);
            EdgeScroll(delta);
        }
        Map.AdvanceAnimation(delta, _focused);
        if (_focused && Session.Game.Paused)
        {
            _pauseGlowSeconds = (_pauseGlowSeconds + delta) % 4;
            UpdatePauseGlow();
        }
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
        _hud.Text = Session.Game.CityName;
        _hud.AddThemeColorOverride("font_color", new Color("#70b7ff"));
        _hudCalendar.Text = $"{game.Year} Week {game.WeekOfYear,2}";
        _hudWeek.Text = Fmt.WeekBar(game.Day, game.Config.DaysPerWeek);
        _hudClock.Text = game.Paused ? "|| PAUSED" : game.Speed switch
        {
            GameSpeed.Slow => "> SLOW",
            GameSpeed.Medium => ">> MEDIUM",
            _ => ">>> FAST",
        };
        _hudClock.AddThemeColorOverride("font_color", game.Paused ? new Color("#ffe066") : new Color("#70d7ff"));
        _hudClock.AddThemeConstantOverride("outline_size", game.Paused ? 2 : 0);
        _hudClock.AddThemeConstantOverride("shadow_outline_size", game.Paused ? 4 : 0);
        UpdatePauseGlow();
        _hudPopulation.Text = $"POP {game.Stats.Population:N0}";
        _hudStats.Text = $"BUDGET ${game.Money:N0}" +
            (game.Config.FullRules ? $" ({(game.Finance.Net >= 0 ? "+" : "-")}${Math.Abs(game.Finance.Net):N0}/wk)" : "") +
            (game.Money <= 0 ? " | OUT OF MONEY" : "");
        _hudStats.Modulate = game.Money <= 0 ? new Color("#ff7777") : new Color("#88ee99");
        FitHeaderFonts();
        if (_sidebarSizeLabel is not null) _sidebarSizeLabel.Text = $"{_split.SidebarWidth:0} px";
        string cellInfo = CellInspector.Summary(game, Session.Cursor);
        if (Session.Selection is { Area: > 1 } selection)
            cellInfo += $" | Selection {selection.Width}x{selection.Height} ({selection.Area} cells)";
        _status.Text = Session.InputDebug
            ? $"{_lastInput} | loop {Session.LoopGapMs} ms, worst {Session.LoopWorstGapMs} ms"
            : Session.MessageVisible ? $"{cellInfo} | {Session.Message}" : cellInfo;
        _status.Modulate = Session.MessageVisible && Session.MessageKind == MessageKind.Error
            ? new Color("#ff7777") : Colors.White;
        UpdateModal();
        _panel.Refresh();
        if (_modal.Visible)
        {
            _modalError.Text = Session.MessageVisible && Session.MessageKind == MessageKind.Error ? Session.Message : "";
            _modalError.Visible = _modalError.Text.Length > 0;
        }
    }

    private void FitHeaderFonts()
    {
        if (_editingName) return;
        const int headerSize = 24;
        float width = 0;
        foreach (var label in _headerLabels)
        {
            if (label == _hudCalendar || label == _hudClock) continue;
            string text = label == _hud && label.Text.EnumerateRunes().Count() <= CityGame.MaxCityNameLength
                ? new string('M', CityGame.MaxCityNameLength + 1) : label.Text;
            width += Theme.DefaultFont.GetStringSize(text, fontSize: headerSize).X;
        }
        width += 2 * Math.Max(Theme.DefaultFont.GetStringSize(_hudCalendar.Text, fontSize: headerSize).X,
            Theme.DefaultFont.GetStringSize(_hudClock.Text, fontSize: headerSize).X);
        float scale = Math.Min(1, Math.Max(1, Size.X - 160) / Math.Max(1, width));
        int size = Math.Max(8, (int)(headerSize * scale));
        foreach (var label in _headerLabels) label.AddThemeFontSizeOverride("font_size", size);
        _nameSlot.CustomMinimumSize = new Vector2(
            Theme.DefaultFont.GetStringSize(new string('M', CityGame.MaxCityNameLength + 1), fontSize: size).X,
            Theme.DefaultFont.GetHeight(size) + 8);
        float calendarSlot = Math.Max(_hudCalendar.GetMinimumSize().X, _hudClock.GetMinimumSize().X);
        _hudCalendar.CustomMinimumSize = _hudClock.CustomMinimumSize = new Vector2(calendarSlot, 0);
    }

    private void ShowFontDialog() => Session.ShowPrompt(FontDialogTitle, "Adjust the display font size (16-28).",
    [
        new("OK", Session.ClosePrompt),
        new("RESET", () => SetFontSize(DefaultFontSize)),
    ]);

    private void ShowCityMenu()
    {
        Session.ShowSessionMenu();
        var prompt = Session.Prompt!;
        Session.ShowPrompt(prompt.Title, prompt.Text,
            prompt.Choices.Concat(new[]
            {
                new SessionChoice("Resize sidebar", ShowSidebarDialog),
                new SessionChoice($"Music: {(MusicEnabled ? "ON" : "OFF")}", ToggleMusic),
            }).ToArray(),
            footer: prompt.Footer);
    }

    private void ShowSidebarDialog() => Session.ShowPrompt(SidebarDialogTitle,
        "Arrows select controls; Space/Enter activates [-] or [+] to resize one character at a time.",
    [
        new("OK", Session.ClosePrompt),
        new("RESET", () => _split.SetSidebarWidth(CitySplit.DefaultSidebar)),
    ]);

    private void UpdatePauseGlow()
    {
        float strength = (float)((1 - Math.Cos(_pauseGlowSeconds * Math.Tau / 4)) / 2);
        _hudClock.AddThemeColorOverride("font_outline_color",
            new Color(1, 0.78f, 0.15f, Session.Game.Paused ? 0.1f + strength * 0.35f : 0));
        _hudClock.AddThemeColorOverride("font_shadow_color",
            new Color(1, 0.65f, 0.05f, Session.Game.Paused ? 0.05f + strength * 0.3f : 0));
        float brightness = Session.Game.Paused ? 0.85f + strength * 0.15f : 1;
        _hudClock.Modulate = new Color(brightness, brightness, brightness);
    }

    private void UpdateModal()
    {
        _linePreview.Visible = Session.RoadToolActive && Session.Prompt is null;
        _lineSummary.Text = Session.Preview?.Summary + "\nArrows or drag set the endpoint. Enter confirms; Esc cancels.";
        object? state = Session.Prompt ?? (Session.RoadToolActive ? null : (object?)Session.Preview);
        if (ReferenceEquals(state, _shownModal))
        {
            return;
        }
        int selectedIndex = _shownModal is SessionPrompt previous && state is SessionPrompt next &&
            previous.Title == next.Title && previous.Choices.Count == next.Choices.Count
            ? _promptIndex : 0;
        _shownModal = state;
        _fontSizeLabel = null;
        _sidebarSizeLabel = null;
        _modalScroll = null;
        _promptButtons.Clear();
        _dialogButtons.Clear();
        _promptIndex = selectedIndex;
        var modalBody = _modal.GetChild<PanelContainer>(0);
        foreach (var child in modalBody.GetChildren())
        {
            modalBody.RemoveChild(child);
            child.QueueFree();
        }
        _modal.Visible = state is not null;
        _modalShield.Visible = _modal.Visible;
        if (state is null)
        {
            return;
        }
        StopPointerGesture();
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.MinimumSizeChanged += () => Callable.From(CenterModal).CallDeferred();
        IEnumerable<string> labels = Session.Prompt is { } sizingPrompt
            ? sizingPrompt.Choices.Select((choice, index) => $"{index + 1}. {choice.Label}")
                .Append(sizingPrompt.Title)
            : new[] { "Confirm placement", "Confirm [Enter]", "Cancel [Esc]" };
        float characterWidth = Theme.DefaultFont.GetStringSize("M", fontSize: Theme.DefaultFontSize).X;
        _modalContentWidth = labels.Max(text =>
            Theme.DefaultFont.GetStringSize(text, fontSize: Theme.DefaultFontSize).X) + characterWidth * 10;
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", side is "left" or "right" ? 0 : 2);
        }
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(Math.Min(_modalContentWidth, Size.X - 64), 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        _modalScroll = scroll;
        var padding = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        int sidePadding = (int)Math.Round(characterWidth * 5);
        padding.AddThemeConstantOverride("margin_left", sidePadding);
        padding.AddThemeConstantOverride("margin_right", sidePadding);
        padding.AddChild(content);
        scroll.AddChild(padding);
        margin.AddChild(scroll);
        modalBody.AddChild(margin);
        content.AddChild(new Label
        {
            Text = Session.Prompt?.Title ?? "Confirm placement",
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        content.AddChild(new Label
        {
            Text = Session.Prompt?.Text ?? Session.Preview!.Summary,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _modalError = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color("#ff7777") };
        content.AddChild(_modalError);
        var buttons = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 8);
        LineEdit? inputField = null;
        if (Session.Prompt is { } prompt)
        {
            if (prompt.Title == FontDialogTitle)
            {
                var fontRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                AddButton(fontRow, "[-]", () => SetFontSize(Math.Max(MinFontSize, FontSize - 2)));
                _fontSizeLabel = new Label { Name = "FontDialogValue", Text = FontSize.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(64, 0) };
                fontRow.AddChild(_fontSizeLabel);
                AddButton(fontRow, "[+]", () => SetFontSize(Math.Min(MaxFontSize, FontSize + 2)));
                content.AddChild(fontRow);
            }
            if (prompt.Title == SidebarDialogTitle)
            {
                var sizes = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                AddButton(sizes, "[-]", () => _split.SetSidebarWidth(_split.SidebarWidth - TerminalGrid.CellWidth));
                _sidebarSizeLabel = new Label { Name = "SidebarDialogValue",
                    Text = $"{_split.SidebarWidth:0} px", CustomMinimumSize = new Vector2(120, 0),
                    HorizontalAlignment = HorizontalAlignment.Center };
                sizes.AddChild(_sidebarSizeLabel);
                AddButton(sizes, "[+]", () => _split.SetSidebarWidth(_split.SidebarWidth + TerminalGrid.CellWidth));
                content.AddChild(sizes);
            }
            if (prompt.Input is not null)
            {
                var input = new LineEdit { Text = prompt.Input };
                input.TextChanged += text => prompt.Input = text;
                input.TextSubmitted += _ => Session.SelectPrompt(0);
                input.GuiInput += inputEvent =>
                {
                    if (inputEvent is InputEventKey { Pressed: true, Keycode: Key.A } selectAll &&
                        (selectAll.CtrlPressed || selectAll.MetaPressed))
                    {
                        input.SelectAll();
                        input.AcceptEvent();
                    }
                };
                content.AddChild(input);
                inputField = input;
            }
            for (int i = 0; i < prompt.Choices.Count; i++)
            {
                int index = i;
                var button = new Button { Text = $"{i + 1}. {prompt.Choices[i].Label}", Alignment = HorizontalAlignment.Left,
                    ClipText = true, TooltipText = prompt.Choices[i].Label };
                button.Pressed += () => SelectPromptChoice(index);
                button.FocusEntered += () => _promptIndex = index;
                buttons.AddChild(button);
                _promptButtons.Add(button);
                _dialogButtons.Add(button);
            }
            if (prompt.Footer is { } footer)
                content.AddChild(new Label { Text = footer, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }
        else
        {
            AddButton(buttons, "Confirm [Enter]", () => Session.ConfirmPreview());
            AddButton(buttons, "Cancel [Esc]", Session.CancelPreview);
        }
        content.AddChild(buttons);
        if (buttons.GetChildCount() > 0)
        {
            if (inputField is not null) inputField.GrabFocus();
            else buttons.GetChild<Button>(Math.Clamp(_promptIndex, 0, buttons.GetChildCount() - 1)).GrabFocus();
        }
        CenterModal();
        Callable.From(CenterModal).CallDeferred();
    }

    private void AddButton(Container buttons, string text, Action action)
    {
        var button = new Button { Text = text, Alignment = HorizontalAlignment.Left };
        button.Pressed += action;
        buttons.AddChild(button);
        _dialogButtons.Add(button);
    }

    private void SelectPromptChoice(int index)
    {
        if (index >= 0 && index < _promptButtons.Count)
        {
            _promptIndex = index;
            _promptButtons[index].GrabFocus();
        }
        Session.SelectPrompt(index);
    }

    public override void _Input(InputEvent input)
    {
        if (!_started) return;
        if (input is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter or Key.Space } activate &&
            Session.Prompt is { Input: null } && GetViewport().GuiGetFocusOwner() is Button focused &&
            _dialogButtons.Contains(focused))
        {
            if (!activate.Echo && !focused.Disabled) focused.EmitSignal(Button.SignalName.Pressed);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventKey { Pressed: true, Keycode: Key.Left or Key.Right or Key.Up or Key.Down } navigate &&
            Session.Prompt is { Input: null } && _dialogButtons.Count > 0)
        {
            int index = _dialogButtons.FindIndex(button => button == GetViewport().GuiGetFocusOwner());
            int direction = navigate.Keycode is Key.Left or Key.Up ? -1 : 1;
            index = (Math.Max(0, index) + direction + _dialogButtons.Count) % _dialogButtons.Count;
            _dialogButtons[index].GrabFocus();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMagnifyGesture magnify)
        {
            var pointer = magnify.Position - Map.GlobalPosition;
            if (!new Rect2(Vector2.Zero, Map.Size).HasPoint(pointer)) pointer = Map.Size / 2;
            ApplyPinchZoom(magnify.Factor, pointer);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_editingName)
        {
            if (input is InputEventKey { Pressed: true } nameKey)
            {
                if (nameKey.Keycode == Key.A && (nameKey.CtrlPressed || nameKey.MetaPressed))
                {
                    _nameEditor.SelectAll();
                    GetViewport().SetInputAsHandled();
                }
                else if (nameKey.Keycode is Key.Enter or Key.KpEnter or Key.Escape)
                {
                    FinishRename(nameKey.Keycode != Key.Escape);
                    GetViewport().SetInputAsHandled();
                }
                else if (OS.HasFeature("macos") && nameKey.Keycode == Key.Delete)
                {
                    int end = _nameEditor.HasSelection() ? _nameEditor.GetSelectionToColumn() : _nameEditor.CaretColumn;
                    int start = _nameEditor.HasSelection() ? _nameEditor.GetSelectionFromColumn() : Math.Max(0, end - 1);
                    _nameEditor.DeleteText(start, end);
                    _nameEditor.CaretColumn = start;
                    GetViewport().SetInputAsHandled();
                }
                return;
            }
            if (input is InputEventMouseButton { Pressed: true } mouse &&
                !_nameEditor.GetGlobalRect().HasPoint(mouse.Position) && !FinishRename(true))
            {
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (_helpVisible && (input is InputEventKey { Pressed: true } ||
            input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right }))
        {
            _helpVisible = false;
            Session.ClosePrompt();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left or MouseButton.Middle })
        {
            StopPointerGesture();
        }
        if (input is InputEventKey { Pressed: true } && Session.Prompt is null)
        {
            if (Session.Preview is not null && input is InputEventKey previewKey)
            {
                var focus = GetViewport().GuiGetFocusOwner();
                bool confirmationFocused = focus is Button &&
                    (_modal.IsAncestorOf(focus) || _linePreview.IsAncestorOf(focus));
                if (confirmationFocused && previewKey.Keycode is Key.Enter or Key.KpEnter ||
                    !Session.RoadToolActive && previewKey.Keycode is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
                {
                    return;
                }
            }
            _UnhandledInput(input);
        }
        else if (input is InputEventKey { Pressed: true, Keycode: Key.Escape } && Session.Prompt is not null)
        {
            _helpVisible = false;
            Session.ClosePrompt();
            GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventKey { Pressed: true, Echo: false } number &&
            Session.Prompt is { Input: null } && !number.CtrlPressed && !number.MetaPressed && !number.AltPressed &&
            (int)number.Keycode >= (int)Key.Key1 && (int)number.Keycode <= (int)Key.Key9)
        {
            SelectPromptChoice((int)number.Keycode - (int)Key.Key1);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!_started || _editingName || input is not InputEventKey { Pressed: true } key)
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
        var code = key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode;
        _hover = false;
        ResetTrackpadScroll();
        _lastInput = $"Key {code}";
        if (_helpVisible)
        {
            _helpVisible = false;
            Session.ClosePrompt();
            return true;
        }
        if (Session.Prompt is { } prompt)
        {
            if (code == Key.Escape)
            {
                Session.ClosePrompt();
            }
            else if (code is Key.Enter or Key.KpEnter)
            {
                SelectPromptChoice(_promptIndex);
            }
            else if (code is Key.Up or Key.Down && _promptButtons.Count > 0)
            {
                _promptIndex = (_promptIndex + (code == Key.Up ? -1 : 1) + prompt.Choices.Count) % prompt.Choices.Count;
                _promptButtons[_promptIndex].GrabFocus();
            }
            else if (prompt.Input is null && (int)code >= (int)Key.Key1 && (int)code <= (int)Key.Key9)
            {
                SelectPromptChoice((int)code - (int)Key.Key1);
            }
            return true;
        }
        if ((key.CtrlPressed || key.MetaPressed) && code == Key.Z)
        {
            Session.RequestUndo();
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
            else if (Session.RoadToolActive && code is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int lineDx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
                int lineDy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
                if (key.CtrlPressed) Session.JumpCursor(lineDx, lineDy);
                else Session.MoveCursor(lineDx, lineDy);
            }
            return true;
        }
        int dx = code == Key.Left ? -1 : code == Key.Right ? 1 : 0;
        int dy = code == Key.Up ? -1 : code == Key.Down ? 1 : 0;
        if (code is Key.Pageup or Key.Pagedown or Key.Home or Key.End)
        {
            Session.JumpCursor(code == Key.Home ? -1 : code == Key.End ? 1 : 0,
                code == Key.Pageup ? -1 : code == Key.Pagedown ? 1 : 0, key.ShiftPressed);
            return true;
        }
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
        if (key.Unicode is '+' or '=' || code is Key.Plus or Key.Equal or Key.KpAdd)
        {
            Map.ZoomBy(1);
            return true;
        }
        if (key.Unicode is '-' or '_' || code is Key.Minus or Key.KpSubtract)
        {
            Map.ZoomBy(-1);
            return true;
        }
        if (code == Key.Key0 || key.Unicode == '0')
        {
            Map.ZoomBy(-Session.ZoomLevel);
            return true;
        }
        if (key.CtrlPressed || key.MetaPressed || key.AltPressed)
        {
            if (code == Key.Q && (key.CtrlPressed || key.MetaPressed)) Session.RequestQuit();
            else return false;
            return true;
        }
        if (key.Unicode == '?')
        {
            ShowHelp();
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
            case Key.T: Session.BeginRoadLine(); break;
            case Key.U: Session.Dezone(); break;
            case Key.D or Key.Delete: Session.PreviewDemolish(); break;
            case Key.S: Session.ToggleSelectionMode(); break;
            case Key.E: Session.ToggleEdgeScroll(); break;
            case Key.Enter or Key.M: Session.ShowAreaMenu(); break;
            case Key.F1 or Key.Question: ShowHelp(); break;
            case Key.F3: ShowFontDialog(); break;
            case Key.F5: Session.QuickSave(); break;
            case Key.F6:
                if (Session.GuideVisible) Session.DismissGuide();
                else Session.ShowGuide();
                break;
            case Key.F7: Session.ShowReport(); break;
            case Key.F8: Session.ShowGrowthReport(); break;
            case Key.F9: Session.RequestLoad(Session.SavePath); break;
            case Key.F12: Session.ToggleInputDebug(); break;
            case Key.Escape: ShowCityMenu(); break;
            case Key.Q: Session.RequestQuit(); break;
            default: return false;
        }
        return true;
    }

    private void ApplyPinchZoom(float factor, Vector2 pointer)
    {
        if (_editingName || Session.Prompt is not null || Session.Preview is not null) return;
        if (!float.IsFinite(factor) || factor <= 0)
        {
            GD.PushWarning("Ignored invalid pinch magnification factor.");
            return;
        }
        if (_trackpadZoomLevel != Session.ZoomLevel) ResetTrackpadScroll();
        _pinchZoom += Math.Log(factor) / Math.Log(1.2);
        int steps = (int)Math.Truncate(_pinchZoom);
        _pinchZoom -= steps;
        if (steps != 0)
        {
            Map.ZoomBy(steps, pointer);
            _trackpadZoomLevel = Session.ZoomLevel;
        }
        _lastInput = $"Pinch {factor:F3}";
    }

    private void OnMapInput(InputEvent input)
    {
        Map.RefreshCells();
        if (_editingName || Session.Prompt is not null || (Session.Preview is not null && !Session.RoadToolActive))
        {
            StopPointerGesture();
            return;
        }
        if (input is InputEventMagnifyGesture magnify)
        {
            ApplyPinchZoom(magnify.Factor, magnify.Position);
            Map.AcceptEvent();
        }
        else if (input is InputEventPanGesture pan)
        {
            if (!Map.Grid.TryCell(pan.Position.X, pan.Position.Y, out var cell))
            {
                return;
            }
            if (_trackpadZoomLevel != Session.ZoomLevel)
            {
                ResetTrackpadScroll();
            }
            if (pan.CtrlPressed || pan.MetaPressed)
            {
                _trackpadX = _trackpadY = 0;
                _trackpadZoom -= pan.Delta.Y;
                int zoom = (int)Math.Truncate(_trackpadZoom);
                _trackpadZoom -= zoom;
                if (zoom != 0)
                {
                    Map.ZoomBy(zoom, pan.Position);
                    _trackpadZoomLevel = Session.ZoomLevel;
                }
            }
            else
            {
                _trackpadZoom = 0;
                bool horizontal = pan.ShiftPressed || pan.AltPressed;
                _trackpadX += (pan.Delta.X + (horizontal ? pan.Delta.Y : 0)) * ScrollStep;
                _trackpadY += (horizontal ? 0 : pan.Delta.Y) * ScrollStep;
                int columns = (int)Math.Truncate(_trackpadX);
                int rows = (int)Math.Truncate(_trackpadY);
                _trackpadX -= columns;
                _trackpadY -= rows;
                Session.ScrollCamera(columns * Session.Stride, rows * Session.Stride);
            }
            _lastInput = $"Trackpad {pan.Delta} {cell}";
            Map.AcceptEvent();
        }
        else if (input is InputEventMouseButton button)
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
            if (!Session.Game.Map.InBounds(position) &&
                button.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right)
            {
                return;
            }
            switch (button.ButtonIndex)
            {
                case MouseButton.Left:
                    _pointer = button.Position;
                    _selecting = Session.RoadToolActive || button.ShiftPressed || button.CtrlPressed || button.AltPressed || button.MetaPressed;
                    _panning = !_selecting;
                    if (_selecting) Session.BeginDrag(position);
                    else { Session.SelectCell(position); _panAnchor = position; }
                    break;
                case MouseButton.Middle:
                    _panning = true;
                    _panAnchor = position;
                    break;
                case MouseButton.Right:
                    Session.PrepareContextMenuAt(position);
                    Session.ShowAreaMenu();
                    break;
                case MouseButton.WheelUp:
                case MouseButton.WheelDown:
                    int direction = button.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
                    if (button.CtrlPressed || button.MetaPressed)
                    {
                        Map.ZoomBy(-direction, button.Position);
                    }
                    else
                    {
                        bool horizontal = button.ShiftPressed || button.AltPressed;
                        Session.ScrollChars(horizontal ? direction * ScrollStep : 0, horizontal ? 0 : direction * ScrollStep);
                    }
                    break;
                case MouseButton.WheelLeft: Session.ScrollChars(-ScrollStep, 0); break;
                case MouseButton.WheelRight: Session.ScrollChars(ScrollStep, 0); break;
            }
            _lastInput = $"Mouse {button.ButtonIndex} {cell}";
            Map.AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion)
        {
            _pointer = motion.Position;
            _hover = new Rect2(Vector2.Zero, Map.Size).HasPoint(motion.Position);
            var cell = PointerCell(motion.Position);
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
        _scroller.Reset();
        ResetTrackpadScroll();
        if (selecting)
        {
            Session.EndSelection();
        }
    }

    private void OnFocusEntered()
    {
        _focused = true;
        _music.StreamPaused = !MusicEnabled;
    }

    private void ResetTrackpadScroll()
    {
        _trackpadX = _trackpadY = _trackpadZoom = 0;
        _pinchZoom = 0;
        _trackpadZoomLevel = Session.ZoomLevel;
    }
    private void OnFocusExited()
    {
        _focused = false;
        _music.StreamPaused = true;
        StopPointerGesture();
        _hover = false;
        _panel.Minimap.StopDrag();
    }

    private Pos PointerCell(Vector2 pointer) => new(
        Math.Clamp((int)(pointer.X / Map.Grid.PixelWidth), 0, Map.Grid.Columns - 1),
        Math.Clamp((int)(pointer.Y / Map.Grid.PixelHeight), 0, Map.Grid.Rows - 1));

    private void EdgeScroll(double delta)
    {
        Session.RecordLoopGap((long)(delta * 1000));
        if (Session.Prompt is not null || Session.Preview is not null && !Session.RoadToolActive ||
            _panning || !_selecting && (!Session.EdgeScrollEnabled || !_hover))
        {
            _scroller.Reset();
            return;
        }
        var cell = PointerCell(_pointer);
        var (dx, dy) = _scroller.Step(cell.X, cell.Y, Map.Grid.Columns, Map.Grid.Rows, Math.Min(delta, 0.25));
        if (dx == 0 && dy == 0) return;
        Session.ScrollChars(dx, dy);
        if (_selecting) Session.UpdateDrag(Session.ScreenToMap(cell.X, cell.Y));
    }

    private void ShowHelp()
    {
        _helpVisible = true;
        Session.ShowPrompt("TermCity - Help", """
            Move: arrows; Ctrl+arrows or Home/End/PageUp/PageDown jump a screen.
            Pan: left-drag, middle-drag, wheel, two-finger trackpad scroll, or minimap click/drag.
            Select: Shift/Ctrl/Alt+left-drag; Shift+arrows; S, arrows, S.
            Area menu: right-click or Enter/M. Esc cancels selection.
            Zone: R homes, C shops, I factories; U dezone.
            Roads: B street preview; T straight-line tool. Menus offer every road type.
            Demolish: D/Delete (free, no refund). Enter/Y confirms; Esc/N cancels.
            Undo: Ctrl+Z (Command+Z also works); full-city rollback with confirmation.
            Clock: Space/P pause; 1 slow, 2 medium, 3 fast.
            Zoom: +/- (Ctrl/Command also work), pinch, or Ctrl/Command+wheel; 0 normal.
            Shift/Alt+wheel pans sideways. Close dialogs or finish text editing first.
            Rename: double-click the city name (16 chars). Enter saves; Esc cancels.
            Font: F3 opens the [-] size [+] dialog; OK closes it, RESET restores the default.
            Sidebar: drag the divider, or Esc > Resize sidebar; arrows select, Space/Enter activates.
            Music: Esc > Music toggles evolving Greensleeves phrases in related keys.
            Name editing: Delete/Backspace removes text; Ctrl/Command+A selects all.
            Sidebar sections stay open. E toggles edge scrolling.
            Game: Esc city menu; F5 save; F9 quick-load; Q/Ctrl+Q quit.
            Reports: F7 weekly report/milestones; F8 growth/road access.
            Guide: F6 shows/dismisses. F12 input and loop diagnostics.
            Dezoned buildings leave in 2-3 weeks; restore their zone to keep them.
            Zones are free. Connect roads to the map edge for growth.
            """, [new("Close", () => { _helpVisible = false; Session.ClosePrompt(); })]);
    }

    private void CenterModal()
    {
        if (!_modal.Visible) return;
        if (_modalScroll is not null)
        {
            var content = _modalScroll.GetChild<Control>(0);
            _modalScroll.CustomMinimumSize = new Vector2(Math.Max(1, Math.Min(_modalContentWidth, Size.X - 64)),
                Math.Max(1, Math.Min(content.GetCombinedMinimumSize().Y, Size.Y - 96)));
            _modal.Size = _modal.GetCombinedMinimumSize();
        }
        _modal.Position = (Size - _modal.Size) / 2;
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

    private void StopMusic()
    {
        if (_musicStopped) return;
        _musicStopped = true;
        _music.Stop();
        _music.Stream = null;
        _musicPlayback.Dispose();
        _musicTrack?.Dispose();
        _musicTrack = null;
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
        Session.Changed -= OnChanged;
        Session.CameraChanged -= OnCameraChanged;
        Session.SelectionChanged -= OnSelectionChanged;
        Session.QuitRequested -= Quit;
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
                window.ContentScaleSize = new Vector2I(960, 640);
            }
            else
            {
                window.Size = new Vector2I(960, 640);
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GodotSmoke.Run(this);
            await GodotSmoke.RunNativeInput(this);
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
            Session.PlaceCursor(center);
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
