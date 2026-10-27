using System.Globalization;
using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class Main
{
    private TerminalFrame? _debugSiegeControls;
    private LineEdit? _debugSwarmSize;
    private LineEdit? _debugWallWidth;
    private LineEdit? _debugWallHeight;
    private Label? _debugSiegeStatus;
    private Button? _debugSiegeRestart;
    private readonly List<Button> _debugSiegeButtons = [];

    private bool DebugSiegeControlHasFocus => _debugSiegeControls is not null &&
        GetViewport().GuiGetFocusOwner() is { } focus && _debugSiegeControls.IsAncestorOf(focus);

    private void ClearDebugSiegeFocus()
    {
        if (DebugSiegeControlHasFocus) GetViewport().GuiGetFocusOwner()!.ReleaseFocus();
    }

    private void CreateDebugSiegeControls()
    {
        if (!Session.DebugSiegeMode) return;
        var edge = new MarginContainer
        {
            Name = "DebugSiegeMapEdge", Position = new Vector2(8, 8), MouseFilter = MouseFilterEnum.Ignore,
        };
        var theme = CreateDialogButtonTheme();
        theme.DefaultFont = Theme.DefaultFont;
        theme.DefaultFontSize = 14;
        _debugSiegeControls = new TerminalFrame
        {
            Name = "DebugSiegeControls", JoinAdjacent = false, Theme = theme,
            MouseFilter = MouseFilterEnum.Stop,
        };
        _debugSiegeControls.MouseEntered += () => { StopPointerGesture(); _hover = false; };
        _debugSiegeControls.GuiInput += _ => _debugSiegeControls.AcceptEvent();
        var row = new HFlowContainer { Name = "DebugSiegeActions", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 4);
        var heading = new Label { Text = "DEBUG SIEGE", VerticalAlignment = VerticalAlignment.Center };
        heading.AddThemeColorOverride("font_color", new Color("#70b7ff"));
        row.AddChild(heading);
        var size = new HBoxContainer();
        size.AddChild(new Label { Text = "Swarm", VerticalAlignment = VerticalAlignment.Center });
        _debugSwarmSize = CreateDebugSiegeNumberInput("DebugSwarmSize", 24,
            $"Size of the next manual swarm: 1-{SettlementState.MaximumGoblinSwarm}. Enter starts a replacement wave.",
            StartDebugSiegeWave);
        size.AddChild(_debugSwarmSize);
        row.AddChild(size);
        var walls = new HBoxContainer { Name = "DebugWallDimensions" };
        walls.AddChild(new Label { Text = "Walls", VerticalAlignment = VerticalAlignment.Center });
        string wallTip = $"Starting wall dimensions in cells ({SettlementState.MinimumEnclosureSize}-{SettlementState.MaximumEnclosureSize}, " +
            "map size and dry land permitting). Restart Game applies the selected width and height; Enter requests restart.";
        _debugWallWidth = CreateDebugSiegeNumberInput("DebugWallWidth", Session.Game.Settlement?.Bounds.Width ?? 24,
            "Wall width. " + wallTip, RestartDebugSiegeWithSize);
        _debugWallHeight = CreateDebugSiegeNumberInput("DebugWallHeight", Session.Game.Settlement?.Bounds.Height ?? 24,
            "Wall height. " + wallTip, RestartDebugSiegeWithSize);
        walls.AddChild(_debugWallWidth);
        walls.AddChild(new Label { Text = "x", VerticalAlignment = VerticalAlignment.Center });
        walls.AddChild(_debugWallHeight);
        row.AddChild(walls);
        Button Action(string name, string text, string tooltip, System.Action action)
        {
            var button = new Button { Name = name, Text = text, TooltipText = tooltip };
            button.Pressed += action;
            row.AddChild(button);
            _debugSiegeButtons.Add(button);
            return button;
        }
        Action("DebugStartWave", "START WAVE",
            "Replace the active attackers with a new wave of the selected size. Reset the next-wave countdown.",
            StartDebugSiegeWave);
        Action("DebugClearGoblins", "REMOVE GOBLINS",
            "Remove every goblin and projectile, then restart the next-wave countdown.",
            () => RunDebugSiegeAction(() => Session.DebugClearGoblins()));
        Action("DebugRepairBuildings", "REPAIR ALL",
            "Free repair of all damage-bearing structures: walls, gates, towers and mounted defenses. Town buildings have no siege hit points.",
            () => RunDebugSiegeAction(() => Session.DebugRepairBuildings()));
        _debugSiegeRestart = Action("DebugRestartGame", "RESTART GAME",
            "Restart this seed using the selected wall width/height, 100 residents and the first wave. Offers save/continue/cancel.",
            RestartDebugSiegeWithSize);
        _debugSiegeStatus = new Label { Name = "DebugSiegeStatus", VerticalAlignment = VerticalAlignment.Center };
        _debugSiegeStatus.AddThemeColorOverride("font_color", new Color("#ffe066"));
        row.AddChild(_debugSiegeStatus);
        _debugSiegeControls.AddChild(row);
        edge.AddChild(_debugSiegeControls);
        Map.AddChild(edge);
        void FitEdge()
        {
            edge.Size = new Vector2(Math.Max(1, Map.Size.X - 16), edge.GetCombinedMinimumSize().Y);
        }
        Map.Resized += FitEdge;
        edge.MinimumSizeChanged += () => Callable.From(FitEdge).CallDeferred();
        FitEdge();
    }

    private LineEdit CreateDebugSiegeNumberInput(string name, int value, string tooltip, System.Action submitted)
    {
        var input = new LineEdit
        {
            Name = name, Text = value.ToString(CultureInfo.InvariantCulture), MaxLength = 3,
            CustomMinimumSize = new Vector2(56, 0), Alignment = HorizontalAlignment.Center, TooltipText = tooltip,
        };
        foreach (string state in new[] { "normal", "read_only", "focus" })
        {
            var style = new StyleBoxFlat
            {
                BgColor = Colors.Black, BorderColor = state == "focus" ? new Color("#ffe066") : Colors.White,
            };
            style.SetBorderWidthAll(1);
            style.SetContentMarginAll(4);
            input.AddThemeStyleboxOverride(state, style);
        }
        input.FocusEntered += () => { StopPointerGesture(); _hover = false; };
        input.TextSubmitted += _ => submitted();
        return input;
    }

    private bool ReadDebugSiegeNumber(LineEdit input, string name, int minimum, int maximum, out int value)
    {
        if (int.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum) return true;
        Session.SetMessage($"{name} must be a whole number from {minimum} to {maximum}.", MessageKind.Error);
        input.GrabFocus();
        input.SelectAll();
        return false;
    }

    private void StartDebugSiegeWave()
    {
        if (!ReadDebugSiegeNumber(_debugSwarmSize!, "Swarm size", 1, SettlementState.MaximumGoblinSwarm, out int size))
            return;
        RunDebugSiegeAction(() => Session.DebugStartWave(size));
    }

    private void RestartDebugSiegeWithSize()
    {
        int maxWidth = Math.Min(SettlementState.MaximumEnclosureSize, Session.Game.Map.Width - 2);
        int maxHeight = Math.Min(SettlementState.MaximumEnclosureSize, Session.Game.Map.Height - 2);
        if (!ReadDebugSiegeNumber(_debugWallWidth!, "Wall width", SettlementState.MinimumEnclosureSize, maxWidth, out int width) ||
            !ReadDebugSiegeNumber(_debugWallHeight!, "Wall height", SettlementState.MinimumEnclosureSize, maxHeight, out int height))
            return;
        ClearDebugSiegeFocus();
        Session.RequestDebugSiegeRestart(width, height);
    }

    private void RunDebugSiegeAction(Func<ActionResult> action)
    {
        ClearDebugSiegeFocus();
        if (!Session.MapNavigationAllowed || _editingName || Session.Preview is not null)
        {
            Session.SetMessage("Finish the current dialog or placement before using siege debug controls.", MessageKind.Error);
            return;
        }
        action();
    }

    private void UpdateDebugSiegeControls()
    {
        if (_debugSiegeControls is null) return;
        bool blocked = Session.Prompt is not null || Session.Preview is not null || _editingName;
        foreach (var button in _debugSiegeButtons)
            button.Disabled = blocked || button != _debugSiegeRestart && Session.Game.Settlement is null;
        _debugSwarmSize!.Editable = !blocked && Session.Game.Settlement is not null;
        _debugWallWidth!.Editable = _debugWallHeight!.Editable = !blocked;
        _debugSiegeStatus!.Text = Session.Game.Settlement is { } siege
            ? $"{siege.Bounds.Width}x{siege.Bounds.Height} | {siege.Goblins.Count} goblins" : "No bailey";
    }
}
