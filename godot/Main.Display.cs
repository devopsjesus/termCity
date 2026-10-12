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
    public static int MinimumWindowHeight => CityGame.DebugToolsAvailable ? 800 : 780;

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
    private CalendarIcon _calendarIcon = null!;

    private Label _hudWeek = null!;
    private Label _hudSpeed = null!;

    private Label _hudPopulation = null!;
    private Label _hudReports = null!;
    private Label _hudGuide = null!;



    private Label _status = null!;

    private Label _placementStatus = null!;
    private Label _peasantStatus = null!;
    private CityGame? _coverageGame;
    private TermCity.Core.Buildings.ServiceKind? _coverageService;
    private double _coverageUpdatedAt;
    internal Action FitStatusRow = null!;
    private string? _peasantReason;
    private int _peasantVariation;
    private int? _peasantStartWeek;
    private CityGame? _peasantGame;

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
        UpdateMenuSizeControls();
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
        if (window.Mode == Window.ModeEnum.Maximized) return;
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
        Theme.SetStylebox("panel", "TooltipPanel", TerminalFrame.CreateTooltipStyle());
        Theme.SetColor("font_color", "TooltipLabel", Colors.White);
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
        void LinkHeader(Label label, Action action, string tooltip, bool textOnly = false)
        {
            label.MouseFilter = MouseFilterEnum.Stop;
            label.MouseDefaultCursorShape = CursorShape.PointingHand;
            label.TooltipText = tooltip;
            label.GuiInput += input =>
            {
                var font = label.GetThemeFont("font");
                int fontSize = label.GetThemeFontSize("font_size");
                float textWidth = Math.Min(label.Size.X, font.GetStringSize(label.Text, fontSize: fontSize).X);
                float textHeight = Math.Min(label.Size.Y, font.GetHeight(fontSize));
                var textRect = new Rect2(new Vector2(label.Size.X - textWidth, (label.Size.Y - textHeight) / 2),
                    new Vector2(textWidth, textHeight));
                if (textOnly && input is InputEventMouseMotion motion)
                    label.MouseDefaultCursorShape = textRect.HasPoint(motion.Position) ? CursorShape.PointingHand : CursorShape.Arrow;
                if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click ||
                    _editingName || Session.Prompt is not null || Session.Preview is not null) return;
                if (textOnly && !textRect.HasPoint(click.Position)) return;
                action();
                label.AcceptEvent();
            };
        }
        LinkHeader(_hudStats, Session.ShowBudgetMenu, "Open economy / budget", textOnly: true);
        _hudGuide = new Label { Name = "GuideHeader", Text = "GUIDE",
            HorizontalAlignment = HorizontalAlignment.Center };
        _hudGuide.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        LinkHeader(_hudGuide, () => Session.ShowGuide(), "Open guide");
        _hudReports = new Label { Name = "ReportsHeader", Text = "REPORTS",
            HorizontalAlignment = HorizontalAlignment.Center };
        _hudReports.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        LinkHeader(_hudReports, Session.ShowReport, "Open weekly report");
        _hudViews = new Label { Name = "ViewsHeader", Text = "VIEWS", HorizontalAlignment = HorizontalAlignment.Center };
        _hudViews.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        LinkHeader(_hudViews, ToggleViews, "Choose an overlay");
        _hudCalendar = new Label { Name = "Calendar", HorizontalAlignment = HorizontalAlignment.Left };
        _hudCalendar.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        _hudWeek = new Label { Name = "WeekProgress", HorizontalAlignment = HorizontalAlignment.Center };
        _hudWeek.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        _hudCalendar.VerticalAlignment = _hudWeek.VerticalAlignment = VerticalAlignment.Center;
        var calendar = new HBoxContainer { Name = "TimeHeader" };
        calendar.AddThemeConstantOverride("separation", 12);
        _calendarIcon = new CalendarIcon { Name = "CalendarFeastIcon" };
        calendar.AddChild(_calendarIcon);
        _hudWeek.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        calendar.AddChild(_hudCalendar);
        calendar.AddChild(_hudWeek);
        _hudSpeed = new Label { Name = "TimeSpeed", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        _hudSpeed.AddThemeColorOverride("font_color", new Color("#70d7ff"));
        calendar.AddChild(_hudSpeed);
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
        var titleFrame = new TerminalFrame { VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        titleFrame.AddChild(names);
        _hud.AddThemeFontSizeOverride("font_size", 20);
        hud.AddChild(titleFrame);
        var calendarFrame = new TerminalFrame { Name = "TimeHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        calendarFrame.AddChild(calendar);
        hud.AddChild(calendarFrame);
        var populationFrame = new TerminalFrame { Name = "PopulationHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        populationFrame.AddChild(_hudPopulation);
        hud.AddChild(populationFrame);
        var viewsFrame = new TerminalFrame { Name = "ViewsHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        viewsFrame.AddChild(_hudViews);
        hud.AddChild(viewsFrame);
        var guideFrame = new TerminalFrame { Name = "GuideHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        guideFrame.AddChild(_hudGuide);
        var reportsFrame = new TerminalFrame { Name = "ReportsHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        reportsFrame.AddChild(_hudReports);
        hud.AddChild(reportsFrame);
        hud.AddChild(guideFrame);
        var financeFrame = new TerminalFrame { Name = "FundsHeaderFrame",
            VisibleVerticalContentPadding = 4, SizeFlagsVertical = SizeFlags.Fill };
        financeFrame.AddChild(_hudStats);
        hud.AddChild(financeFrame);
        layout.AddChild(hud);
        _headerLabels = [_brand, _hud, _hudCalendar, _hudWeek, _hudSpeed, _hudPopulation, _hudViews, _hudReports, _hudGuide, _hudStats];
        foreach (var label in _headerLabels)
        {
            label.VerticalAlignment = VerticalAlignment.Center;
            label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        }
        _nameSlot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var body = new CitySplit { Name = "CityDivider", SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _split = body;
        var workspace = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        workspace.AddChild(CreateConstructionSidebar());
        body.AddChild(workspace);
        var city = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        workspace.AddChild(city);
        var mapFrame = new TerminalFrame { VisibleContentPadding = TerminalFrame.Inset - 4 };
        mapFrame.AddChild(Map);
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
            TiltMap = Map.TiltMap, RotateMap = Map.RotateMap,
            CameraOrientation = () => Map.PerspectiveEnabled && Map.Perspective is { } camera
                ? (camera.Azimuth, camera.Elevation) : null,
            CameraFootprint = () => Map.PerspectiveEnabled && Map.Perspective is { } camera
                ? camera.GroundViewCorners() : null,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        var sidebar = new TerminalFrame { VisibleContentPadding = RailPadding };
        sidebar.AddChild(_panel);
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
        _peasantStatus = new Label { Name = "PeasantStatus", HorizontalAlignment = HorizontalAlignment.Right,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop, Visible = false };
        statusRow.AddChild(_peasantStatus);
        statusRow.AddChild(_savePathLabel);
        FitStatusRow = () =>
        {
            statusRow.CustomMinimumSize = new Vector2(Size.X, 28);
            _savePathLabel.CustomMinimumSize = new Vector2(
                Math.Min(_savePathLabel.GetThemeFont("font").GetStringSize(_savePathLabel.Text,
                    fontSize: _savePathLabel.GetThemeFontSize("font_size")).X, Size.X / 2), 0);
            _placementStatus.CustomMinimumSize = new Vector2(
                Math.Min(_placementStatus.GetThemeFont("font").GetStringSize(_placementStatus.Text,
                    fontSize: _placementStatus.GetThemeFontSize("font_size")).X, Size.X / 2), 0);
            float leftWidth = Math.Min(_status.GetThemeFont("font").GetStringSize(_status.Text,
                fontSize: _status.GetThemeFontSize("font_size")).X, Size.X * 0.4f);
            float otherWidth = new[] { _placementStatus, _savePathLabel }.Where(label => label.Visible)
                .Sum(label => label.CustomMinimumSize.X + statusRow.GetThemeConstant("separation"));
            float available = Math.Max(0, Size.X - leftWidth - otherWidth - statusRow.GetThemeConstant("separation"));
            _peasantStatus.CustomMinimumSize = new Vector2(Math.Min(
                _peasantStatus.GetThemeFont("font").GetStringSize(_peasantStatus.Text,
                    fontSize: _peasantStatus.GetThemeFontSize("font_size")).X, available), 0);
        };
        Resized += FitStatusRow;
        _placementStatus.MinimumSizeChanged += FitStatusRow;
        _peasantStatus.MinimumSizeChanged += FitStatusRow;
        _status.MinimumSizeChanged += FitStatusRow;
        FitStatusRow();
        layout.AddChild(statusRow);
        CreateViewsBar();
        _modalShield = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modalShield.GuiInput += input =>
        {
            if (Session.Prompt?.Title == "TermCity guide" &&
                input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } guideClick &&
                !_modal.GetGlobalRect().HasPoint(guideClick.GlobalPosition))
            {
                Session.CancelPrompt();
                _modalShield.AcceptEvent();
                return;
            }
            if (ConstructionMenuVisible &&
                input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click &&
                !_modal.GetGlobalRect().HasPoint(click.GlobalPosition))
            {
                var icon = _constructionIcons.FirstOrDefault(button =>
                    button.IsVisibleInTree() && button.GetGlobalRect().HasPoint(click.GlobalPosition));
                if (icon is not null) icon.EmitSignal(Button.SignalName.Pressed);
                else Session.CancelPrompt();
                _modalShield.AcceptEvent();
            }
            if (input is InputEventMouseMotion motion)
            {
                var icon = ConstructionMenuVisible ? _constructionIcons.FirstOrDefault(button =>
                    button.IsVisibleInTree() && button.GetGlobalRect().HasPoint(motion.GlobalPosition)) : null;
                if (icon != _shieldHoveredIcon)
                {
                    _shieldHoveredIcon = icon;
                    icon?.GetChild<ConstructionIcon>(0).Hover();
                }
                _modalShield.TooltipText = _savePathLabel.Visible &&
                    _savePathLabel.GetGlobalRect().HasPoint(motion.GlobalPosition)
                    ? _savePathLabel.TooltipText : "";
            }
        };
        _modalShield.MouseExited += () => _shieldHoveredIcon = null;
        _modalShield.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_modalShield);
        _modal = new TerminalFrame
        {
            Name = "CityDialog",
            ClipContents = true,
            JoinAdjacent = false,
            Theme = CreateDialogButtonTheme(),
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsVertical = SizeFlags.Fill,
        };
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

    private void UpdateUi()
    {
        var game = Session.Game;
        _hud.Text = Session.Game.CityName;
        _hud.AddThemeColorOverride("font_color", new Color("#70b7ff"));
        _hudCalendar.Text = MedievalCalendar.DateLabel(game.Year, game.WeekOfYear, game.Config.WeeksPerYear);
        _calendarIcon.Feast = MedievalCalendar.Feast(game.WeekOfYear, game.Config.WeeksPerYear);
        _calendarIcon.QueueRedraw();
        bool clockPaused = Session.ClockSuspended;
        _hudWeek.Text = clockPaused ? "[PAUSED]" : Fmt.WeekBar(game.Day, game.Config.DaysPerWeek);
        _hudWeek.AddThemeColorOverride("font_color", new Color(clockPaused ? "#ffe066" : "#70d7ff"));
        _hudSpeed.Text = Fmt.SpeedLabel(game.Speed);
        _hudSpeed.TooltipText = $"{game.Speed} speed ({_hudSpeed.Text}); 1/2/3 change speed";
        _hudPopulation.Text = $"POP {game.Stats.Population:N0}";
        _hudStats.Text = Fmt.CompactMoney(game.Money) +
            (game.Config.FullRules ? $" [{(game.Finance.Net > 0 ? "+" : "")}{Fmt.CompactMoney(game.Finance.Net)}]" : "") +
            (game.Money <= 0 ? " | COFFERS EMPTY" : "");
        _hudStats.Modulate = game.Money <= 0 ? new Color("#ff7777") : new Color("#88ee99");
        FitHeaderFonts();
        UpdateMenuSizeControls();
        string cellInfo = Session.HasCursor ? CellInspector.Summary(game, Session.Cursor) : "Press F1 for HELP";
        bool gameMenu = Session.Prompt?.Title == GameDialogTitle;
        if (Session.Selection is { Area: > 1 } selection)
            cellInfo += $" | Selection {selection.Width}x{selection.Height} ({selection.Area} cells)";
        _status.Text = gameMenu ? $"Seed {game.Config.Seed} | {game.Map.Width}x{game.Map.Height}" : Session.InputDebug
            ? $"{_lastInput} | loop {Session.LoopGapMs} ms, worst {Session.LoopWorstGapMs} ms"
            : Session.MessageVisible ? (Session.HasCursor ? $"{cellInfo} | {Session.Message}" : Session.Message) : cellInfo;
        _status.TooltipText = _status.Text;
        var buildingStatus = Session.HasCursor ? CellInspector.Status(game, Session.Cursor) : null;
        _status.Modulate = gameMenu ? Colors.White : Session.MessageVisible && Session.MessageKind is MessageKind.Error or MessageKind.Warning
            ? Session.MessageKind switch
        {
            MessageKind.Error => new Color("#ff7777"),
            MessageKind.Warning => new Color("#ffe066"),
            _ => Colors.White,
        } : buildingStatus is { } status ? ToColor(status.Color) : Colors.White;
        string? placementError = Session.FixedPlacementError;
        _placementStatus.Text = placementError ?? Session.FixedPlacementWarning ?? "";
        _placementStatus.Modulate = placementError is not null ? new Color("#ff7777") : new Color("#ffe066");
        _placementStatus.TooltipText = _placementStatus.Text;
        _placementStatus.Visible = Session.FixedPlacementToolActive && _placementStatus.Text.Length > 0;
        _savePathLabel.Visible = gameMenu || Session.Prompt?.IsSaveDialog == true;
        if (!ReferenceEquals(_peasantGame, game))
        {
            _peasantGame = game;
            _peasantStartWeek = null;
            _peasantReason = null;
        }
        if (_peasantStartWeek is null || game.Week < _peasantStartWeek) _peasantStartWeek = game.Week;
        int complaintIndex = game.Week - _peasantStartWeek.Value;
        var need = game.Config.FullRules ? PeasantNeeds.Describe(game, _peasantVariation, complaintIndex) : null;
        if (need?.Reason != _peasantReason)
        {
            _peasantReason = need?.Reason;
            _peasantVariation = Random.Shared.Next(4);
            need = game.Config.FullRules ? PeasantNeeds.Describe(game, _peasantVariation, complaintIndex) : null;
        }
        if (need is null) _peasantStartWeek = null;
        if (!ConstructionMenuVisible || _constructionChoices.Length == 0 ||
            _constructionChoices[_constructionIndex].Building is null)
        {
            _peasantStatus.Text = need?.Text ?? "";
            _peasantStatus.TooltipText = need is null ? "" : $"{need.Text}\n\n{need.Hint}";
            _peasantStatus.Modulate = need is null ? Colors.White : ToColor(need.Color);
            _peasantStatus.Visible = need is not null && !_savePathLabel.Visible &&
                !_placementStatus.Visible && !Session.InputDebug && Session.Prompt is null;
        }
        if (!_savePathLabel.Visible) _modalShield.TooltipText = "";
        UpdateModal();
        UpdateConstructionPurchaseState();
        UpdateConstructionCoverage();
        FitStatusRow();
        UpdateViewsBar();
        _panel.Refresh();
        if (_modal.Visible)
        {
            _modalError.Text = Session.MessageVisible && Session.MessageKind == MessageKind.Error ? Session.Message : "";
            _modalError.Visible = _modalError.Text.Length > 0;
        }
    }

    private void UpdateConstructionCoverage(bool force = false)
    {
        if (!ConstructionMenuVisible || _constructionChoices.Length == 0 ||
            _constructionChoices[_constructionIndex].Building is not { } building) return;
        var game = Session.Game;
        if (force || !ReferenceEquals(_coverageGame, game) || _coverageService != building.Service ||
            game.ElapsedDays < _coverageUpdatedAt ||
            game.ElapsedDays - _coverageUpdatedAt >= SidebarReport.CoverageRefreshDays(game.Config, game.Speed))
        {
            _coverageGame = game;
            _coverageService = building.Service;
            _coverageUpdatedAt = game.ElapsedDays;
            var status = building.IsMonument
                ? new SidebarCell(CityGame.DebugToolsAvailable ? "Debug monument: ready for placement" : "Debug-only monument",
                    SidebarReport.PeopleColor)
                : SidebarReport.ServiceStatus(game, building.Service);
            if (_peasantStatus.Text != status.Text) _peasantStatus.Text = status.Text;
            _peasantStatus.TooltipText = building.IsMonument ? "Monument prototype: normal gate 100,000 souls; Debug launch bypasses it." :
                SidebarReport.ServiceStatusDetails(game, building.Service);
            var color = ToColor(status.Color);
            if (_peasantStatus.Modulate != color) _peasantStatus.Modulate = color;
        }
        _peasantStatus.Visible = !Session.InputDebug && !_savePathLabel.Visible && !_placementStatus.Visible;
    }

    internal void FitHeaderFonts()
    {
        if (_editingName) return;
        const int headerSize = 24;
        bool showGuide = Size.X >= 900;
        _hudGuide.GetParent<Control>().Visible = showGuide;
        bool showReports = Size.X >= 900;
        _hudReports.GetParent<Control>().Visible = showReports;
        var slots = new (Label Label, string Sample)[]
        {
            (_brand, "TermCity"),
            (_hud, new string('M', CityGame.MaxCityNameLength + 1)),
            (_hudCalendar, MedievalCalendar.DateLabel(9999, Math.Min(22, Session.Game.Config.WeeksPerYear),
                Session.Game.Config.WeeksPerYear)),
            (_hudWeek, Session.Game.Config.DaysPerWeek + 2 >= "[PAUSED]".Length
                ? Fmt.WeekBar(0, Session.Game.Config.DaysPerWeek) : "[PAUSED]"),
            (_hudSpeed, "3x"),
            (_hudPopulation, "POP 9,999,999"),
            (_hudViews, "VIEWS"),
            (_hudGuide, "GUIDE"),
            (_hudReports, "REPORTS"),
            (_hudStats, !showGuide ? "99,999g [999g]" : Session.Game.Config.FullRules
                ? "-9,999,999g [-999,999g] | COFFERS EMPTY"
                : "-9,999,999g | COFFERS EMPTY"),
        }.Where(slot => (slot.Label != _hudGuide || showGuide) &&
            (slot.Label != _hudReports || showReports)).ToArray();
        var header = _hudCalendar.GetParent().GetParent().GetParent<BoxContainer>();
        var names = _nameSlot.GetParent<BoxContainer>();
        var calendar = _hudCalendar.GetParent<BoxContainer>();
        var visibleFrames = header.GetChildren().OfType<TerminalFrame>().Where(frame => frame.Visible).ToArray();
        float chromeWidth = 18 + header.GetThemeConstant("separation") * (visibleFrames.Length - 1) +
            names.GetThemeConstant("separation") * (names.GetChildCount() - 1) +
            calendar.GetThemeConstant("separation") * (calendar.GetChildCount() - 1) +
            visibleFrames.Sum(frame =>
                frame.GetThemeStylebox("panel").GetContentMargin(Side.Left) +
                frame.GetThemeStylebox("panel").GetContentMargin(Side.Right));
        float width = slots.Sum(slot => Theme.DefaultFont.GetStringSize(slot.Sample, fontSize: headerSize).X);
        float scale = Math.Min(1, Math.Max(1, Size.X - chromeWidth) / Math.Max(1, width));
        int size = Math.Max(showGuide ? 8 : 10, (int)(headerSize * scale));
        float fittedWidth = slots.Sum(slot => Theme.DefaultFont.GetStringSize(slot.Sample, fontSize: size).X);
        float slotScale = Math.Min(1, Math.Max(1, Size.X - chromeWidth) / Math.Max(1, fittedWidth));
        foreach (var (label, sample) in slots)
        {
            label.AddThemeFontSizeOverride("font_size", size);
            label.ClipText = true;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            if (label == _hudCalendar || label == _hudWeek)
                label.TooltipText = label.Text;
            else if (label == _hudPopulation) label.TooltipText = label.Text;
            else if (label == _hudStats) label.TooltipText = $"{label.Text}\n{Fmt.Money(Session.Game.Money)}" +
                (Session.Game.Config.FullRules ? $" [{(Session.Game.Finance.Net > 0 ? "+" : "")}{Fmt.Money(Session.Game.Finance.Net)}]" : "") +
                "\nOpen economy / budget";
            if (label == _hud) continue;
            label.CustomMinimumSize = new Vector2(Theme.DefaultFont.GetStringSize(sample, fontSize: size).X * slotScale, 0);
        }
        _calendarIcon.CustomMinimumSize = new Vector2(18, _hudCalendar.GetMinimumSize().Y);
        _nameSlot.CustomMinimumSize = new Vector2(
            Theme.DefaultFont.GetStringSize(new string('M', CityGame.MaxCityNameLength + 1), fontSize: size).X * slotScale,
            Theme.DefaultFont.GetHeight(size));
    }

}
