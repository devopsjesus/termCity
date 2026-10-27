using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Buildings;

namespace TermCity.GodotApp;

public partial class Main
{
    private Label _hudViews = null!;
    private TerminalFrame _viewsBar = null!;
    private readonly List<Button> _viewButtons = [];
    private Button _viewAreas = null!;

    private void CreateViewsBar()
    {
        _viewsBar = new TerminalFrame { Name = "ViewsBar", JoinAdjacent = false,
            Visible = false, MouseFilter = MouseFilterEnum.Stop, Theme = CreateDialogButtonTheme() };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var icons = new HBoxContainer();
        icons.AddThemeConstantOverride("separation", 3);
        _viewAreas = new Button { Name = "ViewAreas", Text = "AREAS", ToggleMode = true,
            CustomMinimumSize = new Vector2(60, 44),
            TooltipText = "Toggle vacant zoning and service-range guides" };
        _viewAreas.Pressed += () => Session.ToggleAreaOverlays();
        icons.AddChild(_viewAreas);
        foreach (var overlay in Enum.GetValues<MapOverlay>())
        {
            var kind = overlay switch
            {
                MapOverlay.Power => ServiceKind.Power, MapOverlay.Water => ServiceKind.Water,
                MapOverlay.Fire => ServiceKind.Fire, MapOverlay.Police => ServiceKind.Police,
                MapOverlay.Health => ServiceKind.Health, MapOverlay.Education => ServiceKind.Education,
                MapOverlay.Parks => ServiceKind.Recreation, MapOverlay.Defence => ServiceKind.Defence,
                MapOverlay.Faith => ServiceKind.Faith, MapOverlay.Trade => ServiceKind.Trade,
                _ => ServiceKind.None,
            };
            var color = new Color(overlay switch
            {
                MapOverlay.Off => "#ff7777", MapOverlay.Power or MapOverlay.Happiness => "#ffe066",
                MapOverlay.Water => "#66c7ff", MapOverlay.Fire => "#ff6b4a",
                MapOverlay.Police => "#8fa8ff", MapOverlay.Health => "#ff7aa8",
                MapOverlay.Education => "#c59bff", MapOverlay.Parks => "#4ddf6b",
                MapOverlay.Defence => "#d9b48f", MapOverlay.Faith => "#f5dfaa",
                MapOverlay.Trade or MapOverlay.LandValue => "#ffd27f",
                _ => "#b99980",
            });
            var button = new Button { Name = $"View{overlay}",
                CustomMinimumSize = new Vector2(44, 44), ToggleMode = true,
                TooltipText = overlay == MapOverlay.Off ? "Clear tint and hide area overlays" : MapOverlays.Label(overlay) };
            var artwork = new ConstructionIcon { Name = "ViewArtwork", Kind = kind,
                Overlay = overlay, MainColor = color, MouseFilter = MouseFilterEnum.Ignore };
            artwork.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            button.AddChild(artwork);
            button.MouseEntered += artwork.Hover;
            button.Pressed += () =>
            {
                artwork.Pulse();
                Session.SetOverlay(overlay);
                if (overlay == MapOverlay.Off) Session.SetAreaOverlays(false);
            };
            icons.AddChild(button);
            _viewButtons.Add(button);
        }
        scroll.AddChild(icons);
        _viewsBar.AddChild(scroll);
        Map.AddChild(_viewsBar);
    }

    private void ToggleViews()
    {
        _viewsBar.Visible = !_viewsBar.Visible;
        StopPointerGesture();
        UpdateViewsBar();
    }

    private void UpdateViewsBar()
    {
        if (!_viewsBar.Visible) return;
        var scroll = _viewsBar.GetChild<ScrollContainer>(0);
        scroll.CustomMinimumSize = new Vector2(Math.Max(1, Map.Size.X - TerminalFrame.Inset * 2), 44);
        _viewsBar.Size = _viewsBar.GetCombinedMinimumSize();
        _viewsBar.Position = Vector2.Zero;
        _viewAreas.SetPressedNoSignal(Session.AreaOverlaysVisible);
        foreach (var (button, index) in _viewButtons.Select((button, index) => (button, index)))
            button.SetPressedNoSignal((int)Session.Overlay == index);
    }

}
