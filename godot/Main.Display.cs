using Godot;
using TermCity.Core.Effects;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class Main
{
    public CityMinimap Minimap => _panel.Minimap;

    public const int DefaultFontSize = 20, MinFontSize = 16, MaxFontSize = 28;
    public const int MinimumWindowHeight = 780;

    public int FontSize { get; private set; } = DefaultFontSize;

    private CitySplit _split = null!;

    private Label _hud = null!;

    private Label _brand = null!;

    private Label[] _headerLabels = [];

    private Control _nameSlot = null!;

    private LineEdit _nameEditor = null!;

    private bool _editingName;

    private bool _pausedBeforeRename;

    private Label _hudStats = null!;

    private Label _hudCalendar = null!;

    private Label _hudWeek = null!;

    private Label _hudPopulation = null!;

    private Label _hudClock = null!;

    private double _pauseGlowSeconds;

    private Label _status = null!;

    private Label _placementStatus = null!;

    private Label _savePathLabel = null!;

    private CityPanel _panel = null!;

    private const string DisplaySettingsPath = "user://display.cfg";

    private void LoadPreferences()
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
        SavePreferences();
    }

    private void SavePreferences()
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
        var minimum = new Vector2I((int)Math.Ceiling(640 * scale), (int)Math.Ceiling(MinimumWindowHeight * scale));
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
        layout.AddThemeConstantOverride("separation", 4);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(layout);
        var hud = new HBoxContainer();
        hud.AddThemeConstantOverride("separation", 4);
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
        var mapFrame = Frame(Map);
        mapFrame.Name = "MapFrame";
        mapFrame.SizeFlagsVertical = SizeFlags.ExpandFill;
        city.AddChild(mapFrame);
        _linePreview = new VBoxContainer { Visible = false };
        _lineSummary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _linePreview.AddChild(_lineSummary);
        var lineButtons = new HBoxContainer();
        AddButton(lineButtons, "Confirm [Enter]", () => Session.ConfirmPreview(), underline: 0);
        AddButton(lineButtons, "Cancel [Esc]", Session.CancelPreview, underline: 1);
        _linePreview.AddChild(lineButtons);
        city.AddChild(_linePreview);
        _panel = new CityPanel { Session = Session, ZoomBy = delta => Map.ZoomBy(delta),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        var sidebar = Frame(_panel);
        sidebar.Name = "SidebarFrame";
        sidebar.SizeFlagsHorizontal = SizeFlags.Fill;
        sidebar.CustomMinimumSize = new Vector2(CitySplit.MinimumSidebar, 0);
        sidebar.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(sidebar);
        layout.AddChild(body);
        Map.GuiInput += OnMapInput;
        Map.MouseExited += () => _hover = false;
        var statusRow = new HBoxContainer { Name = "StatusRow", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _status = new Label { Name = "Status", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop };
        _placementStatus = new Label
        {
            Name = "PlacementStatus",
            HorizontalAlignment = HorizontalAlignment.Right,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop,
            Modulate = new Color("#ff7777"),
        };
        _savePathLabel = new Label { Name = "QuickSavePath", Text = $"Quick-save: {Session.SaveDisplayPath}", Visible = false,
            HorizontalAlignment = HorizontalAlignment.Right, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = Path.GetFullPath(Session.SavePath), MouseFilter = MouseFilterEnum.Stop };
        statusRow.AddChild(_status);
        statusRow.AddChild(_placementStatus);
        statusRow.AddChild(_savePathLabel);
        void FitStatusRow()
        {
            statusRow.CustomMinimumSize = new Vector2(Size.X, 28);
            _savePathLabel.CustomMinimumSize = new Vector2(
                Math.Min(_savePathLabel.GetThemeFont("font").GetStringSize(_savePathLabel.Text,
                    fontSize: _savePathLabel.GetThemeFontSize("font_size")).X, Size.X / 2), 0);
            _placementStatus.CustomMinimumSize = new Vector2(
                Math.Min(_placementStatus.GetThemeFont("font").GetStringSize(_placementStatus.Text,
                    fontSize: _placementStatus.GetThemeFontSize("font_size")).X, Size.X / 2), 0);
        }
        Resized += FitStatusRow;
        _placementStatus.MinimumSizeChanged += FitStatusRow;
        FitStatusRow();
        layout.AddChild(statusRow);
        _modalShield = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modalShield.GuiInput += input =>
        {
            if (input is InputEventMouseMotion motion)
                _modalShield.TooltipText = _savePathLabel.Visible &&
                    _savePathLabel.GetGlobalRect().HasPoint(motion.GlobalPosition)
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
        _status.Modulate = !Session.MessageVisible ? Colors.White : Session.MessageKind switch
        {
            MessageKind.Error => new Color("#ff7777"),
            MessageKind.Warning => new Color("#ffe066"),
            _ => Colors.White,
        };
        string? placementError = Session.BuildingPlacementError;
        _placementStatus.Text = placementError ?? Session.BuildingPlacementWarning ?? "";
        _placementStatus.Modulate = placementError is not null ? new Color("#ff7777") : new Color("#ffe066");
        _placementStatus.TooltipText = _placementStatus.Text;
        _placementStatus.Visible = Session.BuildingToolActive && _placementStatus.Text.Length > 0;
        _savePathLabel.Visible = Session.Prompt?.IsSaveDialog == true;
        if (!_savePathLabel.Visible) _modalShield.TooltipText = "";
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
}
