using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class CityPanel : Control
{
    public GameSession Session { get; set; } = null!;
    public Action<int> ZoomBy { get; set; } = null!;
    public Action TiltMap { get; set; } = null!;
    public Action RotateMap { get; set; } = null!;
    public Func<(float Azimuth, float Elevation)?> CameraOrientation { get; set; } = () => null;
    public Func<Vector2[]?> CameraFootprint { get; set; } = () => null;
    public CityMinimap Minimap { get; private set; } = null!;
    private readonly Dictionary<ZoneType, ProgressBar> _demand = [];
    private readonly Dictionary<ZoneType, Control[]> _demandHover = [];
    private (CityGame Game, CityStats Stats, CityIndicators Indicators, bool Paused, Season Season)? _demandState;
    private readonly List<Label> _cityCells = [];
    private GridContainer _cityTable = null!;
    private Label _zoom = null!;
    private VBoxContainer _content = null!;
    private GridContainer _top = null!;
    private readonly List<Label> _headings = [];
    private readonly List<Button> _zoomButtons = [];
    private readonly List<VBoxContainer> _groups = [];
    private bool _fitting;
    private float _fittedHeight = -1;
    private float _fittedWidth = -1;
    private int _fittedCells = -1;
    private static readonly Color HeadingColor = new("#70b7ff");
    private const int TableFontSize = 13;
    private const int CityFontSize = 18;
    private const int DemandSize = 18;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(CitySplit.MinimumSidebar - TerminalFrame.Inset * 2, 0);
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 4);
        AddChild(_content);
        _top = new GridContainer { Columns = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _top.AddThemeConstantOverride("h_separation", 4);
        _top.AddThemeConstantOverride("v_separation", 4);
        _content.AddChild(_top);
        var overview = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Minimap = new CityMinimap { Session = Session, CameraOrientation = CameraOrientation, CameraFootprint = CameraFootprint };
        Minimap.SetHeight(120);
        var cameraControls = new HBoxContainer
        {
            Name = "MinimapCameraControls",
            Alignment = BoxContainer.AlignmentMode.Center
        };
        var tilt = new Button
        {
            Name = "MinimapTilt",
            Text = "TILT",
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Cycle map tilt: 20° / 35° / 50°"
        };
        var rotate = new Button
        {
            Name = "MinimapRotate",
            Text = "ROTATE",
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Rotate map 90° clockwise; Shift+[ / Shift+] rotate the map during building placement"
        };
        tilt.Pressed += () => { if (Session.MapNavigationAllowed) TiltMap(); };
        rotate.Pressed += () => { if (Session.MapNavigationAllowed) RotateMap(); };
        cameraControls.AddChild(tilt);
        cameraControls.AddChild(rotate);
        foreach (var button in new[] { tilt, rotate })
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            {
                var style = new StyleBoxFlat { BgColor = Colors.Black, BorderColor = Colors.White };
                style.SetBorderWidthAll(1);
                style.SetContentMarginAll(4);
                button.AddThemeStyleboxOverride(state, style);
            }
        overview.AddChild(cameraControls);
        var zoomControls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var zoomOut = new Button { Name = "MinimapZoomOut", Text = "[-]", FocusMode = FocusModeEnum.None };
        zoomOut.Pressed += () => { if (Session.MapNavigationAllowed) ZoomBy(-1); };
        _zoom = new Label
        {
            Name = "ZoomIndicator",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _zoom.AddThemeFontSizeOverride("font_size", 20);
        var zoomIn = new Button { Name = "MinimapZoomIn", Text = "[+]", FocusMode = FocusModeEnum.None };
        zoomIn.Pressed += () => { if (Session.MapNavigationAllowed) ZoomBy(1); };
        zoomControls.AddChild(zoomOut);
        zoomControls.AddChild(_zoom);
        zoomControls.AddChild(zoomIn);
        _zoomButtons.AddRange([zoomOut, zoomIn]);
        foreach (var button in _zoomButtons)
        {
            button.Alignment = HorizontalAlignment.Center;
            button.AddThemeFontSizeOverride("font_size", 22);
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            {
                var style = (StyleBox)button.GetThemeStylebox(state).Duplicate();
                style.SetContentMarginAll(2);
                button.AddThemeStyleboxOverride(state, style);
            }
        }
        var zoomFrame = new PanelContainer { Name = "MinimapZoomFrame" };
        var zoomStyle = new StyleBoxFlat { BgColor = Colors.Black, BorderColor = Colors.White };
        zoomStyle.SetBorderWidthAll(1);
        zoomStyle.SetContentMarginAll(1);
        zoomFrame.AddThemeStyleboxOverride("panel", zoomStyle);
        zoomFrame.AddChild(zoomControls);
        overview.AddChild(zoomFrame);
        overview.AddChild(Minimap);
        _top.AddChild(overview);
        var demand = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        demand.AddChild(Heading("DEMAND"));
        foreach (var zone in Zones.Placeable)
        {
            var row = new HBoxContainer { Name = $"Demand{Zones.Get(zone).Letter}Row", MouseFilter = MouseFilterEnum.Stop };
            var letter = new Label
            {
                Name = $"Demand{Zones.Get(zone).Letter}",
                Text = Zones.Get(zone).Letter.ToString(),
                MouseFilter = MouseFilterEnum.Stop,
                VerticalAlignment = VerticalAlignment.Center,
            };
            letter.AddThemeFontSizeOverride("font_size", DemandSize);
            row.AddChild(letter);
            var bar = new ProgressBar
            {
                MaxValue = 1,
                ShowPercentage = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(0, DemandSize),
                MouseFilter = MouseFilterEnum.Stop
            };
            var style = new StyleBoxFlat();
            var color = Zones.Get(zone).Foreground;
            style.BgColor = new Color(color.R / 255f, color.G / 255f, color.B / 255f);
            letter.AddThemeColorOverride("font_color", style.BgColor);
            bar.AddThemeStyleboxOverride("fill", style);
            row.AddChild(bar);
            demand.AddChild(row);
            _demand[zone] = bar;
            _demandHover[zone] = [row, letter, bar];
        }
        _top.AddChild(demand);
        _cityTable = CreateTable("CityTable", 2);
        var city = new VBoxContainer();
        city.AddChild(Heading("CITY"));
        city.AddChild(_cityTable);
        _content.AddChild(city);
        _groups.AddRange([overview, demand, city]);
        Resized += FitContents;
        Refresh();
        FitContents();
    }

    private bool Blocked => Session.Prompt is not null || Session.Preview is not null;
    private PanelContainer Heading(string text)
    {
        var label = new Label { Name = text[0] + text[1..].ToLowerInvariant() + "Heading", Text = text };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", HeadingColor);
        var cell = Main.Frame(label);
        var style = cell.GetThemeStylebox("panel");
        style.ContentMarginTop = style.ContentMarginBottom = 5;
        cell.Name = text + "HeaderCell";
        _headings.Add(label);
        return cell;
    }

    internal static GridContainer CreateTable(string name, int columns)
    {
        var table = new GridContainer { Name = name, Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeConstantOverride("h_separation", 0);
        table.AddThemeConstantOverride("v_separation", 0);
        return table;
    }

    internal static PanelContainer CellStyle(int index, int columns)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = index / columns % 2 != 0 ? new Color("#101010") : Colors.Black,
            BorderColor = Colors.White,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        style.SetBorderWidthAll(1);
        style.BorderWidthLeft = index % columns == 0 ? 1 : 0;
        style.BorderWidthTop = index < columns ? 1 : 0;
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private void RefreshTable(GridContainer table, IReadOnlyList<SidebarCell[]> rows, List<Label> cells)
    {
        int count = (rows.Count * 2 + table.Columns - 1) / table.Columns * table.Columns;
        if (cells.Count != count)
        {
            foreach (var child in table.GetChildren())
            {
                table.RemoveChild(child);
                child.QueueFree();
            }
            cells.Clear();
            for (int index = 0; index < count; index++)
                cells.Add(AddCell(index));
        }
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < 2; column++)
            {
                var value = rows[row][column];
                var label = cells[row * 2 + column];
                label.Text = value.Text;
                label.TooltipText = value.Text;
                label.AddThemeColorOverride("font_color", ToColor(value.Color));
                string metric = rows[row][0].Text;
                bool showGauge = column == 1 && (metric is "Fuel" or "Water" || metric == "Grain" && Session.Game.GrainRequired);
                if (label.GetChildren().OfType<ResourceGauge>().SingleOrDefault() is { } existingGauge)
                    existingGauge.Visible = showGauge;
                if (showGauge)
                {
                    var gauge = label.GetChildren().OfType<ResourceGauge>().SingleOrDefault();
                    if (gauge is null)
                    {
                        gauge = new ResourceGauge { Name = metric + "Gauge" };
                        gauge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                        label.AddChild(gauge);
                    }
                    var supply = metric == "Fuel" ? Session.Game.Services.Power : Session.Game.Services.Water;
                    gauge.SetLevel(metric == "Grain" ? SidebarReport.GrainLevel(Session.Game) : SidebarReport.SupplyLevel(supply));
                    label.TooltipText = metric == "Grain"
                        ? $"Grain: {value.Text}\nReserve target: {SidebarReport.GrainReserveTargetWeeks} weeks ({SidebarReport.GrainLevel(Session.Game):P0})\nHunger: {Session.Game.Hunger:P0} unmet food need"
                        : $"{metric}: {supply.Supply:N0} supplied / {supply.Demand:N0} needed ({SidebarReport.SupplyLevel(supply):P0})";
                    label.Text = "";
                    label.GetParent<Control>().TooltipText = label.TooltipText;
                    FitGauge(label);
                }
            }

        Label AddCell(int index)
        {
            int column = index % 2;
            var label = new Label
            {
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(column == 0 ? 90 : 145, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Stop,
            };
            label.AddThemeFontSizeOverride("font_size", CityFontSize);
            label.Resized += () => FitGauge(label);
            var cell = CellStyle(index, table.Columns);
            cell.AddChild(label);
            table.AddChild(cell);
            void OpenReport(Control control, InputEvent input)
            {
                if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } ||
                    Blocked || index / 2 >= rows.Count) return;
                Session.ShowReport(SidebarReport.ReportTabFor(cells[index - index % 2].Text));
                control.AcceptEvent();
            }
            label.MouseDefaultCursorShape = cell.MouseDefaultCursorShape = CursorShape.PointingHand;
            cell.MouseFilter = MouseFilterEnum.Stop;
            label.GuiInput += input => OpenReport(label, input);
            cell.GuiInput += input => OpenReport(cell, input);
            return label;
        }
    }

    private static void FitGauge(Label label)
    {
        var gauge = label.GetChildren().OfType<ResourceGauge>().SingleOrDefault();
        if (gauge is null) return;
        gauge.OffsetRight = label.Text.Length == 0 ? 0 : -label.GetThemeFont("font").GetStringSize(label.Text,
            fontSize: label.GetThemeFontSize("font_size")).X - 6;
    }

    private static Color ToColor(Rgb color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    private void FitContents()
    {
        if (_content is null || Minimap is null || Size.Y <= 0 || _fitting) return;
        if (Math.Abs(_fittedHeight - Size.Y) < 0.01 && Math.Abs(_fittedWidth - Size.X) < 0.01 &&
            _fittedCells == _cityCells.Count)
        {
            _content.Size = Size;
            return;
        }
        _fitting = true;
        try
        {
            float mapHeight = Size.X;
            bool compact = Size.Y < mapHeight + 300;
            _top.Columns = 1;
            int columns = compact ? 4 : 2;
            if (_cityTable.Columns != columns)
            {
                _cityTable.Columns = columns;
                _cityCells.Clear();
                RefreshTable(_cityTable, SidebarReport.CityRows(Session.Game), _cityCells);
            }
            for (int index = 0; index < _cityCells.Count; index++)
            {
                _cityCells[index].CustomMinimumSize = new Vector2(compact
                    ? index % 2 == 0 ? 45 : 65
                    : index % 2 == 0 ? 90 : 145, 0);
                var cellStyle = _cityCells[index].GetParent<Control>().GetThemeStylebox("panel");
                cellStyle.ContentMarginTop = cellStyle.ContentMarginBottom = compact ? 1 : 2;
            }
            float fixedHeight = 0;
            for (int fontSize = TableFontSize; fontSize >= 8; fontSize--)
            {
                foreach (var label in _cityCells)
                {
                    SetFont(label, fontSize + CityFontSize - TableFontSize);
                    FitGauge(label);
                }
                foreach (var label in _headings) SetFont(label, 22);
                foreach (var group in _groups) group.AddThemeConstantOverride("separation", Math.Max(2, fontSize / 3));
                fixedHeight = _content.GetCombinedMinimumSize().Y - Minimap.CustomMinimumSize.Y;
                if (fixedHeight + mapHeight <= Size.Y) break;
            }
            Minimap.SetHeight(Math.Min(mapHeight, Math.Max(0, Size.Y - fixedHeight)));
            _content.Size = Size;
            _fittedHeight = Size.Y;
            _fittedWidth = Size.X;
            _fittedCells = _cityCells.Count;
        }
        finally { _fitting = false; }

        static void SetFont(Control control, int size)
        {
            if (control.GetThemeFontSize("font_size") != size) control.AddThemeFontSizeOverride("font_size", size);
        }
    }

    public void Refresh()
    {
        if (_cityTable is null) return;
        var game = Session.Game;
        var demand = game.Demand;
        var demandState = (game, game.Stats, game.Indicators, game.Paused, game.Season);
        foreach (var zone in Zones.Placeable)
        {
            _demand[zone].Value = demand.For(zone);
            if (_demandState != demandState)
            {
                string details = CityReport.ZoneDetails(game, zone) + "\nF8: growth and road access report.";
                foreach (var control in _demandHover[zone]) control.TooltipText = details;
            }
        }
        _demandState = demandState;
        int previousCells = _cityCells.Count;
        RefreshTable(_cityTable, SidebarReport.CityRows(game), _cityCells);
        _zoom.Text = Session.ZoomLabel;
        Minimap.QueueRedraw();
        if (previousCells != _cityCells.Count) FitContents();
    }
}
