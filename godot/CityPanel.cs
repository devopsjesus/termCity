using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class CityPanel : VBoxContainer
{
    private enum PanelSection { Demand, City, Zones }

    public GameSession Session { get; set; } = null!;
    public Action<int> ZoomBy { get; set; } = null!;
    public CityMinimap Minimap { get; private set; } = null!;
    private readonly Dictionary<PanelSection, VBoxContainer> _sections = [];
    private readonly Dictionary<ZoneType, ProgressBar> _demand = [];
    private readonly Dictionary<ZoneType, Label> _zones = [];
    private Label _city = null!;
    private Label _guide = null!;
    private Label _zoom = null!;
    private PanelContainer _guideFrame = null!;
    private static readonly Color HeadingColor = new("#70b7ff");

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(270, 0);
        AddThemeConstantOverride("separation", 0);
        var overview = new VBoxContainer();
        overview.AddChild(Heading("MINIMAP"));
        Minimap = new CityMinimap { Session = Session };
        overview.AddChild(Minimap);
        var zoomControls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var zoomOut = new Button { Text = "[-]", FocusMode = FocusModeEnum.None };
        zoomOut.Pressed += () => { if (!Blocked) ZoomBy(-1); };
        _zoom = new Label { Name = "ZoomIndicator", HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var zoomIn = new Button { Text = "[+]", FocusMode = FocusModeEnum.None };
        zoomIn.Pressed += () => { if (!Blocked) ZoomBy(1); };
        zoomControls.AddChild(zoomOut);
        zoomControls.AddChild(_zoom);
        zoomControls.AddChild(zoomIn);
        overview.AddChild(zoomControls);
        AddChild(Main.Frame(overview));
        foreach (var section in Enum.GetValues<PanelSection>())
        {
            var group = new VBoxContainer();
            var header = Heading(section.ToString().ToUpperInvariant());
            header.Name = $"{section}Heading";
            group.AddChild(header);
            var body = new VBoxContainer();
            group.AddChild(body);
            AddChild(Main.Frame(group));
            _sections[section] = body;
        }
        foreach (var zone in Zones.Placeable)
        {
            var row = new HBoxContainer();
            var letter = new Label { Name = $"Demand{Zones.Get(zone).Letter}", Text = Zones.Get(zone).Letter.ToString() };
            row.AddChild(letter);
            var bar = new ProgressBar { MaxValue = 1, ShowPercentage = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18) };
            var style = new StyleBoxFlat();
            var color = Zones.Get(zone).Foreground;
            style.BgColor = new Color(color.R / 255f, color.G / 255f, color.B / 255f);
            letter.AddThemeColorOverride("font_color", style.BgColor);
            bar.AddThemeStyleboxOverride("fill", style);
            row.AddChild(bar);
            _sections[PanelSection.Demand].AddChild(row);
            _demand[zone] = bar;
            var label = WrappedLabel();
            _sections[PanelSection.Zones].AddChild(label);
            _zones[zone] = label;
        }
        _city = WrappedLabel();
        _sections[PanelSection.City].AddChild(_city);
        _guide = WrappedLabel();
        var guide = new VBoxContainer();
        guide.AddChild(Heading("GUIDE"));
        guide.AddChild(_guide);
        _guideFrame = Main.Frame(guide);
        AddChild(_guideFrame);
        Refresh();
    }

    private bool Blocked => Session.Prompt is not null || Session.Preview is not null;
    private static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeColorOverride("font_color", HeadingColor);
        return label;
    }
    private static Label WrappedLabel() => new()
    {
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    public void Refresh()
    {
        if (_city is null) return;
        var game = Session.Game;
        var stats = game.Stats;
        foreach (var zone in Zones.Placeable)
        {
            _demand[zone].Value = game.Demand.For(zone);
            var count = stats.For(zone);
            _zones[zone].Text = $"{Zones.Get(zone).Letter} {count.Filled}/{count.Zoned}" +
                (count.Zoned > count.Served ? $" ({count.Zoned - count.Served} no road)" : "") +
                (count.AwaitingRemoval > 0 ? $" +{count.AwaitingRemoval} leaving" : "");
        }
        _city.Text = $"Population {stats.Population:N0}\nAdults {stats.Adults}  Kids {stats.Children}\n" +
            $"Seniors {stats.Seniors}  Homes {stats.Households}\nTax income {Fmt.Money(stats.WeeklyIncome)}/wk\n" +
            $"Tax R {game.Taxes.Residential:P0} C {game.Taxes.Commercial:P0} I {game.Taxes.Industrial:P0}";
        _guideFrame.Visible = Session.GuideVisible;
        _guide.Text = Session.GuideText;
        _zoom.Text = Session.ZoomLabel;
        Minimap.QueueRedraw();
    }
}
