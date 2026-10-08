using Godot;
using System.Buffers.Binary;
using System.Text;
using TermCity.Core.Effects;
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
    public EffectSystem Effects { get; private set; } = null!;
    public EffectDirector Director { get; private set; } = null!;
    public EffectLevel EffectsLevel { get; private set; } = EffectLevel.High;
    private double _effectRedrawClock;
    private const double AmbientRedrawSeconds = 1.0 / 15;
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
    private Label _savePathLabel = null!;
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
    private WindowsResizeGuard? _resizeGuard;
    internal WindowsResizeGuard? ResizeGuard => _resizeGuard;
    public bool MusicEnabled { get; private set; } = true;
    public bool SoundEnabled { get; private set; } = true;
    public const double DefaultMusicVolume = 70, DefaultSoundVolume = 60;
    public double MusicVolume { get; private set; } = DefaultMusicVolume;
    public double SoundVolume { get; private set; } = DefaultSoundVolume;
    public bool CelebrationsEnabled { get; private set; }
    private const string AudioDialogTitle = "Audio controls";
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
    private readonly CancellationTokenSource _musicCancellation = new();
    private Task? _musicPump;
    private int _musicPhraseCount;
    public int MusicPhraseCount => Volatile.Read(ref _musicPhraseCount);
    internal int MusicSkips => _musicPlayback.GetSkips();

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
                        text.Append(CellRenderer.Render(Session.Game, x, y, buildingArt: false).Glyph);
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
            CreateEffects();
            CreateLayout(font);
            if (OS.GetName() == "Windows" && DisplayServer.GetName() != "headless")
                _resizeGuard = new WindowsResizeGuard(GetWindow());
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
        var sound = settings.GetValue("audio", "sound_enabled", MusicEnabled);
        if (sound.VariantType != Variant.Type.Bool)
            throw new InvalidOperationException("Saved sound preference must be a boolean.");
        SoundEnabled = sound.AsBool();
        MusicVolume = LoadVolume(settings, "music_volume", DefaultMusicVolume);
        SoundVolume = LoadVolume(settings, "sound_volume", DefaultSoundVolume);
        var celebrations = settings.GetValue("display", "celebrations", false);
        if (celebrations.VariantType != Variant.Type.Bool)
            throw new InvalidOperationException("Saved celebrations preference must be a boolean.");
        CelebrationsEnabled = celebrations.AsBool();
        var effects = settings.GetValue("display", "effects", "high");
        if (effects.VariantType != Variant.Type.String ||
            !Enum.TryParse(effects.AsString(), ignoreCase: true, out EffectLevel level) || !Enum.IsDefined(level))
            throw new InvalidOperationException("Saved effects preference must be off, low or high.");
        EffectsLevel = level;
    }

    private static double LoadVolume(ConfigFile settings, string key, double defaultValue)
    {
        var value = settings.GetValue("audio", key, defaultValue);
        if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) ||
            !double.IsFinite(value.AsDouble()) || value.AsDouble() is < 0 or > 100)
            throw new InvalidOperationException($"Saved {key} must be between 0 and 100.");
        return value.AsDouble();
    }

    private void CreateEffects()
    {
        Effects = new EffectSystem(Session.Game.Config.Seed ^ 0x5eed1e5,
            new EffectSettings { ReducedMotion = _options.ReducedMotion, Celebrations = CelebrationsEnabled });
        Effects.Settings.Level = EffectsLevel;
        Director = new EffectDirector(Effects);
        Director.Attach(Session);
        Map.Effects = Effects;
    }

    public void SetEffectsLevel(EffectLevel level)
    {
        EffectsLevel = level;
        Effects.Settings.Level = level;
        Map.Invalidate(false);
        SaveDisplaySettings();
    }

    private void CycleEffects()
    {
        if (Effects.Settings.ReducedMotion)
        {
            Session.SetMessage("Effects are disabled by --reduced-motion.", MessageKind.Info);
            return;
        }
        SetEffectsLevel(EffectsLevel switch
        {
            EffectLevel.High => EffectLevel.Low,
            EffectLevel.Low => EffectLevel.Off,
            _ => EffectLevel.High,
        });
        Session.SetMessage($"Effects: {EffectsLabel()}.", MessageKind.Info);
        RefreshMenuState(CycleEffects, EffectsLabel());
    }

    private void RefreshMenuState(Action target, string state)
    {
        if (Session.Prompt is not { } prompt) return;
        var choices = prompt.Choices.Select(choice => choice.Select == target
            ? choice with { Cells = [choice.Cells?[0] ?? string.Empty, state] }
            : choice).ToArray();
        Session.ShowPrompt(prompt.Title, prompt.Text, choices, prompt.Input, prompt.Footer, prompt.Columns);
    }

    private string EffectsLabel() => Effects.Settings.ReducedMotion ? "OFF (reduced motion)" : EffectsLevel.ToString().ToUpperInvariant();

    private void ToggleCelebrations()
    {
        CelebrationsEnabled = !CelebrationsEnabled;
        Effects.Settings.Celebrations = CelebrationsEnabled;
        if (!CelebrationsEnabled) Effects.ClearCelebrations();
        Map.Invalidate(false);
        SaveDisplaySettings();
        RefreshMenuState(ToggleCelebrations, CelebrationsEnabled ? "ON" : "OFF");
    }

    private void UpdateEffects(double delta)
    {
        if (_focused) Director.Update(delta);
        if (!Effects.NeedsRedraw) return;
        _effectRedrawClock += delta;
        // One-shot effects and shakes redraw every frame; ambient life alone redraws at a calmer rate to save CPU.
        if (Effects.ActiveOneShots > 0 || Effects.Shake != default || !Effects.HasVisuals ||
            _effectRedrawClock >= AmbientRedrawSeconds)
        {
            _effectRedrawClock = 0;
            Map.QueueRedraw();
        }
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
        settings.SetValue("audio", "sound_enabled", SoundEnabled);
        settings.SetValue("audio", "music_volume", MusicVolume);
        settings.SetValue("audio", "sound_volume", SoundVolume);
        settings.SetValue("display", "effects", EffectsLevel.ToString().ToLowerInvariant());
        settings.SetValue("display", "celebrations", CelebrationsEnabled);
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
        _musicPhraseCount = 1;
        _nextMusicPhrase = Task.Run(() => _musicSequence.Next());
        _musicTrack = new AudioStreamGenerator
        {
            MixRate = GreensleevesTrack.SampleRate,
            BufferLength = 1f,
        };
        _music = new AudioStreamPlayer
        {
            Name = "CityMusic", Stream = _musicTrack,
            VolumeDb = VolumeDb(MusicVolume, -24),
        };
        AddChild(_music);
        _music.Play();
        _musicPlayback = (AudioStreamGeneratorPlayback)_music.GetStreamPlayback();
        PumpMusic();
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
        // Only the playback resource is accessed here, never nodes or the scene tree. Map rendering must not
        // be responsible for keeping the audio ring buffer full.
        _musicPump = Task.Run(async () =>
        {
            while (!_musicCancellation.IsCancellationRequested)
            {
                PumpMusic();
                await Task.Delay(10, _musicCancellation.Token);
            }
        });
    }

    private float VolumeDb(double volume, float nominal) =>
        _options.SmokeTest || volume == 0 ? -80 : nominal + (float)(20 * Math.Log10(volume / 100));

    private AudioStreamWav[] _clicks = [];
    private AudioStreamPlayer _clickPlayer = null!;
    private readonly Random _clickRandom = new();

    private void CreateClicks()
    {
        _clicks = Enumerable.Range(0, PlacementClick.Variants).Select(i => new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = PlacementClick.SampleRate,
            Stereo = false,
            Data = PlacementClick.Render(i),
        }).ToArray();
        _clickPlayer = new AudioStreamPlayer { Name = "PlacementClick", VolumeDb = VolumeDb(SoundVolume, -12) };
        AddChild(_clickPlayer);
    }

    private void PlayPlacementClick()
    {
        if (!_focused || !SoundEnabled || SoundVolume == 0 || _clicks.Length == 0) return;
        _clickPlayer.Stream = _clicks[_clickRandom.Next(_clicks.Length)];
        _clickPlayer.PitchScale = (float)(0.92 + _clickRandom.NextDouble() * 0.16);
        _clickPlayer.Play();
    }

    private bool AdvanceMusicPhrase()
    {
        if (_nextMusicPhrase is null || !_nextMusicPhrase.IsCompleted) return false;
        if (_nextMusicPhrase.IsFaulted)
        {
            throw new InvalidOperationException("Music synthesis failed.", _nextMusicPhrase.Exception);
        }
        _musicPhrase = _nextMusicPhrase.GetAwaiter().GetResult();
        _musicSample = 0;
        Interlocked.Increment(ref _musicPhraseCount);
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
                    return;
                }
                float sample = BinaryPrimitives.ReadInt16LittleEndian(
                    _musicPhrase.Pcm.AsSpan(_musicSample, sizeof(short))) / 32768f;
                _musicFrames[_musicFrameCount++] = new Vector2(sample, sample);
                _musicSample += sizeof(short);
            }
            if (!_musicPlayback.PushBuffer(_musicFrames))
            {
                throw new InvalidOperationException("Could not queue synthesized music frames.");
            }
            _musicFrameCount = 0;
        }
    }

    private void ToggleMusic()
    {
        MusicEnabled = !MusicEnabled;
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
        SaveDisplaySettings();
        RefreshMenuState(ToggleMusic, MusicEnabled ? "ON" : "OFF");
    }

    private void ToggleSound()
    {
        SoundEnabled = !SoundEnabled;
        if (!SoundEnabled) _clickPlayer.Stop();
        SaveDisplaySettings();
        RefreshMenuState(ToggleSound, SoundEnabled ? "ON" : "OFF");
    }

    private void ShowAudioDialog() => Session.ShowPrompt(AudioDialogTitle,
        "Music and placement sounds have independent volume and mute controls.",
        [new("Close", Session.CancelPrompt)]);

    private void AddAudioControls(Container content, bool music)
    {
        var channel = new VBoxContainer();
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = music ? "Music" : "Sound", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var slider = new HSlider
        {
            Name = music ? "MusicVolume" : "SoundVolume",
            MinValue = 0, MaxValue = 100, Step = 1,
            Value = music ? MusicVolume : SoundVolume,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(200, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        var value = new Label { Text = $"{slider.Value:0}%", CustomMinimumSize = new Vector2(56, 0) };
        string muteText = (music ? MusicEnabled : SoundEnabled) ? "Mute" : "Unmute";
        var mute = new MnemonicButton { Name = music ? "MusicMute" : "SoundMute",
            Text = muteText, UnderlineIndex = muteText.IndexOf(music ? 'm' : 'u', StringComparison.OrdinalIgnoreCase),
            TooltipText = music ? "Mute/unmute music (M)" : "Mute/unmute sound (U)" };
        slider.ValueChanged += volume =>
        {
            if (music) MusicVolume = volume; else SoundVolume = volume;
            value.Text = $"{volume:0}%";
            (music ? _music : _clickPlayer).VolumeDb = VolumeDb(volume, music ? -24 : -12);
            if (music) _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
            else if (volume == 0) _clickPlayer.Stop();
            SaveDisplaySettings();
        };
        mute.Pressed += () =>
        {
            if (music)
            {
                MusicEnabled = !MusicEnabled;
                _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
            }
            else
            {
                SoundEnabled = !SoundEnabled;
                if (!SoundEnabled) _clickPlayer.Stop();
            }
            mute.Text = (music ? MusicEnabled : SoundEnabled) ? "Mute" : "Unmute";
            mute.UnderlineIndex = mute.Text.IndexOf(music ? 'm' : 'u', StringComparison.OrdinalIgnoreCase);
            SaveDisplaySettings();
        };
        row.AddChild(value);
        row.AddChild(mute);
        _dialogButtons.Add(mute);
        channel.AddChild(row);
        channel.AddChild(slider);
        content.AddChild(channel);
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
        var caretSpace = new StyleBoxEmpty { ContentMarginRight = (float)Math.Ceiling(character) };
        _nameEditor.AddThemeStyleboxOverride("normal", caretSpace);
        _nameEditor.AddThemeStyleboxOverride("focus", caretSpace);
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
        AddToolbarButton(functions, "F6 GUIDE", () => Session.ShowGuide());
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
        AddButton(lineButtons, "Confirm [Enter]", () => Session.ConfirmPreview(), underline: 0);
        AddButton(lineButtons, "Cancel [Esc]", Session.CancelPreview, underline: 1);
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
        var statusRow = new HBoxContainer { Name = "StatusRow", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _status = new Label { Name = "Status", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop };
        _savePathLabel = new Label { Name = "QuickSavePath", Text = $"Quick-save: {Session.SaveDisplayPath}",
            HorizontalAlignment = HorizontalAlignment.Right, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = Path.GetFullPath(Session.SavePath), MouseFilter = MouseFilterEnum.Stop };
        statusRow.AddChild(_status);
        statusRow.AddChild(_savePathLabel);
        void FitStatusRow()
        {
            statusRow.CustomMinimumSize = new Vector2(Size.X, 28);
            _savePathLabel.CustomMinimumSize = new Vector2(
                Math.Min(_savePathLabel.GetThemeFont("font").GetStringSize(_savePathLabel.Text,
                    fontSize: _savePathLabel.GetThemeFontSize("font_size")).X, Size.X / 2), 0);
        }
        Resized += FitStatusRow;
        FitStatusRow();
        layout.AddChild(statusRow);
        _modalShield = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modalShield.GuiInput += input =>
        {
            if (input is InputEventMouseMotion motion)
                _modalShield.TooltipText = _savePathLabel.GetGlobalRect().HasPoint(motion.GlobalPosition)
                    ? _savePathLabel.TooltipText : "";
        };
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
        if (_musicPump is { IsFaulted: true } && !_musicStopped)
        {
            GD.PushError($"Music playback failed: {_musicPump.Exception}");
            Session.SetMessage("Music playback failed; playback stopped.", MessageKind.Error);
            StopMusic();
        }
        if (_focused && !_editingName)
        {
            Session.Update(delta);
            EdgeScroll(delta);
        }
        UpdateEffects(delta);
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
        _hudStats.Text = $"TREASURY {Fmt.Money(game.Money)}" +
            (game.Config.FullRules ? $" ({(game.Finance.Net >= 0 ? "+" : "-")}{Fmt.Money(Math.Abs(game.Finance.Net))}/wk)" : "") +
            (game.Money <= 0 ? " | COFFERS EMPTY" : "");
        _hudStats.Modulate = game.Money <= 0 ? new Color("#ff7777") : new Color("#88ee99");
        FitHeaderFonts();
        if (_sidebarSizeLabel is not null) _sidebarSizeLabel.Text = $"{_split.SidebarWidth:0} px";
        string cellInfo = CellInspector.Summary(game, Session.Cursor);
        if (Session.Selection is { Area: > 1 } selection)
            cellInfo += $" | Selection {selection.Width}x{selection.Height} ({selection.Area} cells)";
        _status.Text = Session.InputDebug
            ? $"{_lastInput} | loop {Session.LoopGapMs} ms, worst {Session.LoopWorstGapMs} ms"
            : Session.MessageVisible ? $"{cellInfo} | {Session.Message}" : cellInfo;
        _status.TooltipText = _status.Text;
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
        new("OK", Session.CancelPrompt),
        new("RESET", () => SetFontSize(DefaultFontSize)),
    ]);

    private void ShowCityMenu()
    {
        Session.ShowSessionMenu();
        var prompt = Session.Prompt!;
        Session.ShowPrompt(prompt.Title, prompt.Text,
            prompt.Choices.Concat(new[]
            {
                new SessionChoice("Resize sidebar", ShowSidebarDialog, ["Step the sidebar width by a character"]),
                new SessionChoice("Music", ToggleMusic, ["Evolving Greensleeves phrases", MusicEnabled ? "ON" : "OFF"]),
                new SessionChoice("Sound", ToggleSound, ["Placement click-clack", SoundEnabled ? "ON" : "OFF"]),
                new SessionChoice("Audio controls", ShowAudioDialog, ["Independent music and sound volume / mute"]),
                new SessionChoice("Effects", CycleEffects, ["Growth, fire, flood, traffic, birds", EffectsLabel()]),
                new SessionChoice("Celebrations", ToggleCelebrations, ["Population milestone confetti", CelebrationsEnabled ? "ON" : "OFF"]),
            }).ToArray(),
            footer: prompt.Footer, columns: prompt.Columns);
    }

    private void ShowSidebarDialog() => Session.ShowPrompt(SidebarDialogTitle,
        "Arrows select controls; Space/Enter activates [-] or [+] to resize one character at a time.",
    [
        new("OK", Session.CancelPrompt),
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
        int selectedIndex = state is SessionPrompt next ? next.SelectedIndex : 0;
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
        var table = Session.Prompt is { } tablePrompt ? TextTable.ForPrompt(tablePrompt) : null;
        IEnumerable<string> labels = Session.Prompt is { } sizingPrompt
            ? (table is { } t
                ? t.Rows.Append(t.Header)
                : sizingPrompt.Choices.Select((choice, index) => sizingPrompt.Shortcuts[index].DisplayLabel(choice.Label)))
                .Append(sizingPrompt.Title)
            : new[] { "Confirm placement", "Confirm [Enter]", "Cancel [Esc]" };
        float characterWidth = Theme.DefaultFont.GetStringSize("M", fontSize: Theme.DefaultFontSize).X;
        float TextWidth(string text) => Theme.DefaultFont.GetStringSize(text, fontSize: Theme.DefaultFontSize).X;
        _modalContentWidth = labels.Max(TextWidth) + characterWidth * 10 + ButtonTextInset() * 2;
        int fixedTextLines = 0;
        if (Session.Prompt is { } fitPrompt && (fitPrompt.Tabs is not null || _helpVisible))
        {
            var pages = fitPrompt.Tabs?.Select(tab => tab.Text) ?? [fitPrompt.Text];
            foreach (string page in pages)
            {
                string[] pageLines = page.Split('\n');
                fixedTextLines = Math.Max(fixedTextLines, pageLines.Length);
                _modalContentWidth = Math.Max(_modalContentWidth, pageLines.Max(TextWidth) + characterWidth * 12);
            }
        }
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
        if (Session.Prompt is { Tabs: { } tabs } tabbed)
        {
            content.AddChild(BuildTabBar(tabs, tabbed.ActiveTab));
        }
        var textLabel = new Label
        {
            Text = Session.Prompt?.Text ?? Session.Preview!.Summary,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        if (fixedTextLines > 0 && Session.Prompt?.Tabs is not null)
        {
            float lineHeight = Theme.DefaultFont.GetHeight(Theme.DefaultFontSize) + textLabel.GetThemeConstant("line_spacing");
            textLabel.CustomMinimumSize = new Vector2(0, lineHeight * fixedTextLines);
        }
        content.AddChild(textLabel);
        _modalError = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color("#ff7777") };
        content.AddChild(_modalError);
        var buttons = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 8);
        LineEdit? inputField = null;
        if (Session.Prompt is { } prompt)
        {
            if (prompt.Title == AudioDialogTitle)
            {
                AddAudioControls(content, music: true);
                AddAudioControls(content, music: false);
            }
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
                var shortcut = prompt.Shortcuts[i];
                string rowText = table is { } rows ? rows.Rows[i] : shortcut.DisplayLabel(prompt.Choices[i].Label);
                int underline = shortcut.UnderlineIndex < 0 ? prompt.Choices[i].Label.Length + 2 : shortcut.UnderlineIndex;
                var button = new MnemonicButton { Text = rowText, UnderlineIndex = underline,
                    Alignment = HorizontalAlignment.Left, ClipText = true,
                    TooltipText = $"{prompt.Choices[i].Label} ({(shortcut.Shift ? "Shift+" : "")}{shortcut.Letter})" };
                button.Pressed += () => SelectPromptChoice(index);
                button.FocusEntered += () => { _promptIndex = index; prompt.SelectedIndex = index; };
                buttons.AddChild(button);
                _promptButtons.Add(button);
                _dialogButtons.Add(button);
            }
            if (prompt.Footer is { } footer)
                content.AddChild(new Label { Text = footer, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            if (table is { } header)
            {
                var headerBox = new MarginContainer();
                headerBox.AddThemeConstantOverride("margin_left", (int)ButtonTextInset());
                headerBox.AddChild(new Label { Text = header.Header + "\n" + header.Rule, ClipText = true });
                content.AddChild(headerBox);
            }
            if (prompt.Tabs is not null)
            {
                content.AddChild(new Label { Text = "Left/Right: change tab. Underlined letter or Enter: pick an action. Esc: back." });
            }
        }
        else
        {
            AddButton(buttons, "Confirm [Enter]", () => Session.ConfirmPreview(), underline: 0);
            AddButton(buttons, "Cancel [Esc]", Session.CancelPreview, underline: 1);
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

    private float ButtonTextInset() => GetThemeStylebox("normal", "Button")?.GetMargin(Side.Left) ?? 0;

    private HFlowContainer BuildTabBar(IReadOnlyList<PromptTab> tabs, int active)
    {
        var bar = new HFlowContainer { Name = "GuideTabs" };
        bar.AddThemeConstantOverride("h_separation", 0);
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i;
            bool selected = i == active;
            var tab = new Button
            {
                Text = selected ? $"[{tabs[i].Title}]" : $" {tabs[i].Title} ",
                Flat = true,
                FocusMode = FocusModeEnum.None,
                TooltipText = tabs[i].Title,
            };
            tab.AddThemeColorOverride("font_color", selected ? new Color("#ffd75e") : new Color("#9fb4c7"));
            tab.AddThemeColorOverride("font_hover_color", new Color("#ffffff"));
            tab.Pressed += () => Session.SelectTab(index);
            bar.AddChild(tab);
        }

        return bar;
    }

    private void AddButton(Container buttons, string text, Action action, int underline = -1)
    {
        Button button = underline < 0 ? new Button() : new MnemonicButton { UnderlineIndex = underline };
        button.Text = text;
        button.Alignment = HorizontalAlignment.Left;
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
        if (input is InputEventKey dialogKey && Session.Prompt is not null && HandleDialogKey(dialogKey))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMagnifyGesture magnify)
        {
            if (Session.Prompt is null && !_editingName)
            {
                var pointer = magnify.Position - Map.GlobalPosition;
                if (!new Rect2(Vector2.Zero, Map.Size).HasPoint(pointer)) pointer = Map.Size / 2;
                ApplyPinchZoom(magnify.Factor, pointer);
            }
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
            Session.CancelPrompt();
            GetViewport().SetInputAsHandled();
        }
    }

    private bool HandleDialogKey(InputEventKey key)
    {
        if (Session.Prompt is not { } prompt) return false;
        var code = key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode;
        var focus = GetViewport().GuiGetFocusOwner();
        if (!key.Pressed) return focus is not LineEdit;
        if (code == Key.Escape)
        {
            _helpVisible = false;
            Session.CancelPrompt();
            return true;
        }
        if (focus is LineEdit || code == Key.Tab) return false;
        if (focus is Slider && code is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            return false;
        if (code is Key.Enter or Key.KpEnter or Key.Space)
        {
            if (!key.Echo)
            {
                if (focus is Button button && _dialogButtons.Contains(button))
                {
                    if (!button.Disabled) button.EmitSignal(Button.SignalName.Pressed);
                }
                else SelectPromptChoice(prompt.SelectedIndex);
            }
            return true;
        }
        if (code is Key.Left or Key.Right && prompt.Tabs is not null)
        {
            Session.CycleTab(code == Key.Left ? -1 : 1);
            return true;
        }
        if (code is Key.Left or Key.Right or Key.Up or Key.Down && _dialogButtons.Count > 0)
        {
            int index = _dialogButtons.FindIndex(button => button == focus);
            int direction = code is Key.Left or Key.Up ? -1 : 1;
            index = (Math.Max(0, index) + direction + _dialogButtons.Count) % _dialogButtons.Count;
            _dialogButtons[index].GrabFocus();
            return true;
        }
        if (!key.Echo && !key.CtrlPressed && !key.MetaPressed && !key.AltPressed)
        {
            char letter = (int)code is >= (int)Key.A and <= (int)Key.Z ? (char)code
                : key.Unicode is >= 'A' and <= 'z' ? char.ToUpperInvariant((char)key.Unicode) : '\0';
            if (prompt.Title == AudioDialogTitle && !key.ShiftPressed && letter is 'M' or 'U')
            {
                var mute = _dialogButtons.First(button => button.Name == (letter == 'M' ? "MusicMute" : "SoundMute"));
                mute.EmitSignal(Button.SignalName.Pressed);
                return true;
            }
            for (int index = 0; index < prompt.Shortcuts.Count; index++)
                if (prompt.Shortcuts[index].Letter == letter && prompt.Shortcuts[index].Shift == key.ShiftPressed)
                {
                    SelectPromptChoice(index);
                    break;
                }
        }
        return true;
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
        if (Session.Prompt is not null)
        {
            HandleDialogKey(key);
            return true;
        }
        if (Session.Preview is not null)
        {
            if (!key.CtrlPressed && !key.MetaPressed && !key.AltPressed && code is Key.Enter or Key.Y or Key.C)
            {
                Session.ConfirmPreview();
            }
            else if (code is Key.Escape or Key.N or Key.A)
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
        if ((key.CtrlPressed || key.MetaPressed) && code == Key.Z)
        {
            Session.RequestUndo();
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
            case Key.O: Session.CycleOverlay(); break;
            case Key.V: CycleEffects(); break;
            case Key.S: Session.ToggleSelectionMode(); break;
            case Key.E: Session.ToggleEdgeScroll(); break;
            case Key.Enter or Key.M: Session.ShowAreaMenu(); break;
            case Key.F1 or Key.Question: ShowHelp(); break;
            case Key.F3: ShowFontDialog(); break;
            case Key.F5: Session.QuickSave(); break;
            case Key.F6: Session.ShowGuide(); break;
            case Key.F7: Session.ShowReport(); break;
            case Key.F8: Session.ShowGrowthReport(); break;
            case Key.F9: Session.RequestLoad(Session.SavePath); break;
            case Key.F10: if (!key.Echo) ToggleMusic(); break;
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
                    if (_selecting) Session.BeginDrag(position, extend: button.ShiftPressed && !button.CtrlPressed && !button.AltPressed && !button.MetaPressed);
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
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0;
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
        _clickPlayer.Stop();
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
        Session.ShowPrompt(HelpContent.Title, HelpContent.Text(),
            [new("Close", () => { _helpVisible = false; Session.CancelPrompt(); })], footer: HelpContent.Footer);
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
        _musicCancellation.Cancel();
        if (_musicPump is not null)
        {
            try { _musicPump.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (_musicCancellation.IsCancellationRequested) { }
            catch (InvalidOperationException error)
            {
                GD.PushError($"Music playback stopped: {error.Message}");
            }
        }
        _musicStopped = true;
        _music.Stop();
        _music.Stream = null;
        _musicPlayback.Dispose();
        _musicTrack?.Dispose();
        _musicTrack = null;
        _musicCancellation.Dispose();
    }

    private void Quit()
    {
        StopMusic();
        GetTree().CreateTimer(0.1).Timeout += () => GetTree().Quit();
    }

    public override void _ExitTree()
    {
        _resizeGuard?.Dispose();
        _resizeGuard = null;
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
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            GD.PushError($"TermCity Godot smoke failed: {error}");
            GetTree().Quit(1);
        }
    }
}
