using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class CityPanel : Control
{
    public GameSession Session { get; set; } = null!;
    public Action<int> ZoomBy { get; set; } = null!;
    public CityMinimap Minimap { get; private set; } = null!;
    private readonly Dictionary<ZoneType, ProgressBar> _demand = [];
    private readonly Dictionary<ZoneType, Control[]> _demandHover = [];
    private readonly List<Label> _cityCells = [];
    private GridContainer _cityTable = null!;
    private Label _zoom = null!;
    private VBoxContainer _content = null!;
    private GridContainer _top = null!;
    private readonly List<Label> _headings = [];
    private readonly List<Label> _demandLetters = [];
    private readonly List<Button> _zoomButtons = [];
    private readonly List<VBoxContainer> _groups = [];
    private bool _fitting;
    private float _fittedHeight = -1;
    private float _fittedWidth = -1;
    private int _fittedCells = -1;
    private static readonly Color HeadingColor = new("#70b7ff");
    private const int TableFontSize = 13;
    private const int CityFontSize = 16;

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
        overview.AddChild(Heading("MINIMAP"));
        Minimap = new CityMinimap { Session = Session };
        Minimap.SetHeight(120);
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
        _zoomButtons.AddRange([zoomOut, zoomIn]);
        overview.AddChild(zoomControls);
        _top.AddChild(overview);
        var demand = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        demand.AddChild(Heading("DEMAND"));
        foreach (var zone in Zones.Placeable)
        {
            var row = new HBoxContainer { Name = $"Demand{Zones.Get(zone).Letter}Row", MouseFilter = MouseFilterEnum.Stop };
            var letter = new Label
            {
                Name = $"Demand{Zones.Get(zone).Letter}", Text = Zones.Get(zone).Letter.ToString(),
                MouseFilter = MouseFilterEnum.Stop,
            };
            row.AddChild(letter);
            var bar = new ProgressBar { MaxValue = 1, ShowPercentage = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18),
                MouseFilter = MouseFilterEnum.Stop };
            var style = new StyleBoxFlat();
            var color = Zones.Get(zone).Foreground;
            style.BgColor = new Color(color.R / 255f, color.G / 255f, color.B / 255f);
            letter.AddThemeColorOverride("font_color", style.BgColor);
            bar.AddThemeStyleboxOverride("fill", style);
            row.AddChild(bar);
            demand.AddChild(row);
            _demand[zone] = bar;
            _demandHover[zone] = [row, letter, bar];
            _demandLetters.Add(letter);
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
        label.AddThemeFontSizeOverride("font_size", 18);
        label.AddThemeColorOverride("font_color", HeadingColor);
        var cell = Main.Frame(label);
        var style = cell.GetThemeStylebox("panel");
        style.ContentMarginTop = style.ContentMarginBottom = 5;
        cell.Name = text + "HeaderCell";
        _headings.Add(label);
        return cell;
    }

    private static GridContainer CreateTable(string name, int columns)
    {
        var table = new GridContainer { Name = name, Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeConstantOverride("h_separation", 1);
        table.AddThemeConstantOverride("v_separation", 1);
        return table;
    }

    private static PanelContainer CellStyle(bool alternate)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(alternate ? "#0c1c29" : "#08121b"),
            BorderColor = new Color("#2c4965"),
            ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2,
        };
        style.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static void RefreshTable(GridContainer table, IReadOnlyList<SidebarCell[]> rows, List<Label> cells)
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
                cells.Add(AddCell(index % 2, alternate: index / table.Columns % 2 != 0));
        }
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < 2; column++)
            {
                var value = rows[row][column];
                var label = cells[row * 2 + column];
                label.Text = value.Text;
                label.TooltipText = value.Text;
                label.AddThemeColorOverride("font_color", ToColor(value.Color));
            }

        Label AddCell(int column, bool alternate)
        {
            var label = new Label
            {
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(column == 0 ? 90 : 145, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Stop,
            };
            label.AddThemeFontSizeOverride("font_size", CityFontSize);
            var cell = CellStyle(alternate);
            cell.AddChild(label);
            table.AddChild(cell);
            return label;
        }
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
            float mapHeight = Size.X * Session.Game.Map.Height * 1.5f / Session.Game.Map.Width;
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
                foreach (var label in _cityCells) SetFont(label, fontSize + CityFontSize - TableFontSize);
                foreach (var label in _headings) SetFont(label, TableFontSize + 5);
                foreach (var label in _demandLetters) SetFont(label, fontSize + 1);
                SetFont(_zoom, fontSize + 1);
                foreach (var button in _zoomButtons) SetFont(button, fontSize + 1);
                foreach (var bar in _demand.Values) bar.CustomMinimumSize = new Vector2(0, fontSize + 3);
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
        foreach (var zone in Zones.Placeable)
        {
            _demand[zone].Value = game.Demand.For(zone);
            string details = CityReport.ZoneDetails(game, zone) + "\nF8: growth and road access report.";
            foreach (var control in _demandHover[zone]) control.TooltipText = details;
        }
        int previousCells = _cityCells.Count;
        RefreshTable(_cityTable, SidebarReport.CityRows(game), _cityCells);
        _zoom.Text = Session.ZoomLabel;
        Minimap.QueueRedraw();
        if (previousCells != _cityCells.Count) FitContents();
    }
}
