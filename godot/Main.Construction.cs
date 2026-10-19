using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class Main
{
    internal const int IconPadding = 6;
    internal const int RailPadding = 4;
    private sealed record ConstructionChoice(string Name, Color Color, ChoiceHelp Help,
        BuildingType? Building = null, RoadType? Road = null, RoadTemplate? Template = null);

    private SessionPrompt? _constructionPrompt;
    private readonly List<Button> _constructionIcons = [];
    private Button? _shieldHoveredIcon;
    private ConstructionChoice[] _constructionChoices = [];
    private int _constructionIndex;
    private Label _constructionDescription = null!;
    private GridContainer _constructionStats = null!;
    private Button _constructionAdd = null!;
    private RenderedModelPreview _constructionPreview = null!;
    private RenderedModelPreviewCache? _modelPreviewCache;

    private RenderedModelPreview CreateModelPreview(string name, BuildingType? building = null,
        RoadType? road = null, RoadTemplate? template = null)
    {
        if (_modelPreviewCache is null)
        {
            _modelPreviewCache = new RenderedModelPreviewCache { Name = "ModelPreviewCache" };
            AddChild(_modelPreviewCache);
        }
        return new RenderedModelPreview
        {
            Name = name, Cache = _modelPreviewCache,
            Subject = new ModelPreviewSubject(Session.Game.Map.Content, building, road, template),
            TooltipText = building?.Name ?? template?.Name ?? road!.Name,
        };
    }

    private bool ConstructionMenuVisible =>
        Session.Prompt is not null && ReferenceEquals(Session.Prompt, _constructionPrompt);

    private Control CreateConstructionSidebar()
    {
        var scroll = new ScrollContainer
        {
            Name = "ConstructionSidebar",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var icons = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        icons.AddThemeConstantOverride("separation", RailPadding - 1);
        scroll.AddChild(icons);
        void AddIcon(string name, string icon, Color color, Action action, ServiceKind kind = ServiceKind.None)
        {
            var button = new Button
            {
                Name = name, TooltipText = name,
                CustomMinimumSize = new Vector2(40, 40), FocusMode = FocusModeEnum.None,
            };
            button.AddThemeFontSizeOverride("font_size", 24);
            button.AddThemeColorOverride("font_color", color);
            var artwork = new ConstructionIcon
            {
                Name = "IconArtwork", Glyph = icon, Kind = kind, MainColor = color,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            artwork.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            button.AddChild(artwork);
            button.MouseEntered += artwork.Hover;
            foreach (string state in new[] { "normal", "hover", "pressed" })
                button.AddThemeStyleboxOverride(state, new StyleBoxFlat
                {
                    BgColor = new Color(state == "hover" ? "#18232c" : state == "pressed" ? "#243847" : "#101010"),
                });
            button.Pressed += () =>
            {
                artwork.Pulse();
                if (!_editingName && (Session.Prompt is null || ConstructionMenuVisible) &&
                    Session.Preview is null) action();
            };
            _constructionIcons.Add(button);
            var iconFrame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            iconFrame.AddChild(button);
            var iconBorder = new StyleBoxFlat { BgColor = Colors.Black, BorderColor = Colors.White };
            iconBorder.SetBorderWidthAll(1);
            iconBorder.SetContentMarginAll(IconPadding);
            iconFrame.AddThemeStyleboxOverride("panel", iconBorder);
            iconFrame.Name = $"{name}IconFrame";
            icons.AddChild(iconFrame);
        }
        string roadIcon = Session.Game.Map.Content.Roads.OrderByDescending(r => r.Rank).First().GlyphFor(5);
        AddIcon("Roads", roadIcon, new Color("#b99a70"), () => ShowConstructionMenu(null));
        foreach (var service in ServiceKinds.All)
        {
            var building = Session.Game.Map.Content.Buildings
                .FirstOrDefault(b => b.PlayerPlaceable && b.Service == service);
            if (building is null) continue;
            AddIcon(CityReport.ServiceName(service), building.Glyphs[0], ToColor(building.Foreground),
                () => ShowConstructionMenu(service), service);
        }
        if (CityGame.DebugToolsAvailable)
            AddIcon("Monuments", "♙", new Color("#d0c6a6"), () => ShowConstructionMenu(null, monuments: true), ServiceKind.Defence);
        var frame = new TerminalFrame { VisibleContentPadding = RailPadding };
        frame.AddChild(scroll);
        frame.SizeFlagsHorizontal = SizeFlags.Fill;
        return frame;
    }

    private static Color ToColor(TermCity.Core.Util.Rgb color) =>
        Color.Color8(color.R, color.G, color.B);

    private void ShowConstructionMenu(ServiceKind? service, bool monuments = false)
    {
        if (ConstructionMenuVisible) Session.ClosePrompt();
        var game = Session.Game;
        _constructionChoices = monuments
            ? game.Map.Content.Buildings.Where(b => b.PlayerPlaceable && b.IsMonument &&
                (!b.DebugOnly || CityGame.DebugToolsAvailable))
                .Select(b => new ConstructionChoice(b.CatalogName,
                    ToColor(b.Foreground),
                    ChoiceHelpContent.Building(game, b), Building: b)).ToArray()
            : service is { } kind
            ? game.Map.Content.Buildings.Where(b => b.PlayerPlaceable && b.Service == kind)
                .OrderBy(CityProgression.RequiredPopulation).ThenBy(b => b.Cost)
                .Select(b => new ConstructionChoice(b.Name, ToColor(b.Foreground),
                    ChoiceHelpContent.Building(game, b), Building: b)).ToArray()
            : game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank)
                .Select(r => new ConstructionChoice(r.Name, ToColor(r.Foreground),
                    ChoiceHelpContent.Road(game, r, line: true), Road: r))
                .Append(new ConstructionChoice("Traffic Circle",
                    ToColor(game.Map.Content.Roads.Get(DefaultRoads.CobbledName).Foreground),
                    ChoiceHelpContent.RoadTemplate(game, RoadTemplate.TrafficCircle),
                    Template: RoadTemplate.TrafficCircle)).ToArray();
        string title = monuments ? "Monuments (Debug)" : service is { } category ? CityReport.ServiceName(category) : "Roads";
        _constructionIndex = 0;
        var choices = _constructionChoices.Select((choice, index) =>
            new SessionChoice(choice.Name, () => SelectConstruction(index))).ToArray();
        _constructionPrompt = new SessionPrompt(title, "Choose a type, then Add to start placement.", choices);
        Session.ShowPrompt(_constructionPrompt);
    }

    private void BuildConstructionMenu(PanelContainer body)
    {
        var padding = new MarginContainer();
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            padding.AddThemeConstantOverride($"margin_{side}", side == "top" ? 0 : 12);
        var scroll = new ScrollContainer
        {
            Name = "ConstructionCatalog", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        _modalScroll = scroll;
        _modalContentWidth = 1040;
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.MinimumSizeChanged += () => Callable.From(CenterModal).CallDeferred();
        content.AddThemeConstantOverride("separation", RailPadding - 1);
        Label Heading(string text, string name, string color)
        {
            var heading = new Label
            {
                Text = text, Name = name, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            heading.AddThemeColorOverride("font_color", new Color(color));
            heading.AddThemeFontSizeOverride("font_size", 24);
            return heading;
        }
        content.AddChild(ConstructionTableCell(Heading(_constructionPrompt!.Title, "ConstructionCategoryHeader", "#70d7ff")));
        var catalog = new HBoxContainer { Name = "ConstructionLayout", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        catalog.AddThemeConstantOverride("separation", 0);
        var types = new VBoxContainer { Name = "ConstructionTypes" };
        types.AddThemeConstantOverride("separation", RailPadding - 1);
        const int nameFontSize = 22;
        var names = Session.Game.Map.Content.Buildings.Where(building => building.PlayerPlaceable && !building.IsMonument)
            .Select(building => building.Name)
            .Concat(Session.Game.Map.Content.Roads.Where(road => road.PlayerPlaceable).Select(road => road.Name))
            .Concat(_constructionChoices.Select(choice => choice.Name));
        float typeWidth = names.Max(name =>
            Theme.DefaultFont.GetStringSize(name, fontSize: nameFontSize).X) + 56;
        var capacities = Session.Game.Map.Content.Buildings
            .Where(building => building.PlayerPlaceable && building.Capacity > 0)
            .Select(ChoiceHelpContent.CapacityText);
        typeWidth = Math.Max(typeWidth, capacities.Select(capacity =>
            Theme.DefaultFont.GetStringSize(capacity, fontSize: 14).X + 56).DefaultIfEmpty(0).Max());
        types.CustomMinimumSize = new Vector2(typeWidth, 0);
        for (int i = 0; i < _constructionChoices.Length; i++)
        {
            int index = i;
            var choice = _constructionChoices[i];
            bool locked = choice.Building is { } gated && !CityProgression.IsUnlocked(Session.Game, gated);
            string? capacity = choice.Building is { Capacity: > 0 } building
                ? ChoiceHelpContent.CapacityText(building) : null;
            var button = new Button { Text = choice.Name, ClipText = true,
                TooltipText = capacity is null ? choice.Name : $"{choice.Name}\nCapacity: {capacity}",
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, capacity is null ? 44 : 64) };
            button.AddThemeFontSizeOverride("font_size", nameFontSize);
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            {
                var style = (StyleBox)_modal.GetThemeStylebox(state, "Button").Duplicate();
                style.ContentMarginLeft = 44;
                if (locked) style.ContentMarginRight = 28;
                style.ContentMarginTop = 6;
                style.ContentMarginBottom = capacity is null ? 6 : 26;
                button.AddThemeStyleboxOverride(state, style);
            }
            var icon = CreateModelPreview("ConstructionTypePreview", choice.Building, choice.Road, choice.Template);
            icon.AnchorBottom = capacity is null ? 1 : 0;
            icon.OffsetBottom = capacity is null ? 0 : 44;
            icon.OffsetRight = 40;
            button.AddChild(icon);
            if (locked)
            {
                var padlock = new Control { Name = "BuildingLock", MouseFilter = MouseFilterEnum.Ignore };
                padlock.AnchorLeft = padlock.AnchorRight = 1;
                padlock.OffsetLeft = -26;
                padlock.OffsetRight = -6;
                padlock.OffsetTop = 12;
                padlock.OffsetBottom = 32;
                padlock.Draw += () =>
                {
                    var red = new Color("#ff7777");
                    padlock.DrawRect(new Rect2(4, 9, 12, 10), red);
                    padlock.DrawRect(new Rect2(7, 2, 6, 9), red, filled: false, width: 2);
                    padlock.DrawRect(new Rect2(9, 12, 2, 4), Colors.Black);
                };
                button.AddChild(padlock);
            }
            if (capacity is not null)
            {
                var capacityLabel = new Label { Name = "BuildingCapacity", Text = capacity,
                    MouseFilter = MouseFilterEnum.Ignore, ClipText = true };
                capacityLabel.AddThemeFontSizeOverride("font_size", 14);
                capacityLabel.AddThemeColorOverride("font_color", new Color("#9fb4c7"));
                capacityLabel.AnchorTop = capacityLabel.AnchorBottom = 1;
                capacityLabel.AnchorRight = 1;
                capacityLabel.OffsetLeft = 44;
                capacityLabel.OffsetRight = -6;
                capacityLabel.OffsetTop = -24;
                capacityLabel.OffsetBottom = -4;
                button.AddChild(capacityLabel);
            }
            button.AddThemeColorOverride("font_color", choice.Color);
            button.Pressed += () => SelectPromptChoice(index);
            button.FocusEntered += () => Session.Prompt!.SelectedIndex = index;
            types.AddChild(button);
            _promptButtons.Add(button);
            _dialogControls.Add(button);
        }
        var typesBox = ConstructionTableCell(types);
        typesBox.Name = "ConstructionTypesBox";
        typesBox.SizeFlagsHorizontal = SizeFlags.Fill;
        typesBox.SizeFlagsVertical = SizeFlags.Fill;
        typesBox.GetThemeStylebox("panel").SetContentMarginAll(RailPadding);
        catalog.AddChild(typesBox);
        var information = new VBoxContainer { Name = "ConstructionInformation",
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        information.AddThemeConstantOverride("separation", 12);
        var columns = new BoxContainer { Name = "ConstructionDetails", Vertical = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", RailPadding - 1);
        var stats = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
        _constructionStats = new GridContainer { Name = "ConstructionStats", Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _constructionStats.AddThemeConstantOverride("h_separation", 0);
        _constructionStats.AddThemeConstantOverride("v_separation", 0);
        stats.AddChild(_constructionStats);
        var description = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
        description.AddThemeConstantOverride("separation", 0);
        var previewSlot = new VBoxContainer { Name = "ConstructionPreviewSlot",
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        description.AddChild(previewSlot);
        _constructionDescription = new Label
        {
            Name = "ConstructionDescription", AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _constructionDescription.AddThemeFontSizeOverride("font_size", 20);
        var descriptionPadding = new MarginContainer();
        descriptionPadding.AddThemeConstantOverride("margin_left", 6);
        descriptionPadding.AddThemeConstantOverride("margin_right", 6);
        descriptionPadding.AddThemeConstantOverride("margin_top", 3);
        descriptionPadding.AddThemeConstantOverride("margin_bottom", 3);
        descriptionPadding.AddChild(_constructionDescription);
        description.AddChild(descriptionPadding);
        var descriptionBox = ConstructionTableCell(description, left: false);
        descriptionBox.Name = "ConstructionDescriptionBox";
        descriptionBox.CustomMinimumSize = new Vector2(0,
            _promptButtons[0].GetCombinedMinimumSize().Y + RailPadding);
        descriptionBox.GetThemeStylebox("panel").SetContentMarginAll(RailPadding);
        columns.AddChild(descriptionBox);
        columns.AddChild(stats);
        information.AddChild(columns);
        catalog.AddChild(information);
        content.AddChild(catalog);
        _modalError = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color("#ff7777") };
        content.AddChild(_modalError);
        var actions = CreateCatalogActions("ConstructionAdd", "Add", AddConstruction, new Color("#88ee99"),
            "ConstructionCancel", Session.CancelPrompt);
        _constructionAdd = actions.GetChild<Button>(0);
        var actionPadding = new MarginContainer { Name = "ConstructionActionPadding" };
        actionPadding.AddThemeConstantOverride("margin_top", TerminalFrame.Inset + 12);
        actionPadding.AddChild(actions);
        scroll.AddChild(content);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 0);
        layout.AddChild(scroll);
        layout.AddChild(actionPadding);
        padding.AddChild(layout);
        body.AddChild(padding);
        SelectConstruction(_constructionIndex);
        _promptButtons[_constructionIndex].GrabFocus();
        CenterModal();
        Callable.From(CenterModal).CallDeferred();
    }

    private void SelectConstruction(int index)
    {
        _constructionIndex = index;
        var choice = _constructionChoices[index];
        var previewSlot = _constructionDescription.GetParent().GetParent().GetNode<VBoxContainer>("ConstructionPreviewSlot");
        foreach (var oldPreview in previewSlot.GetChildren())
        {
            previewSlot.RemoveChild(oldPreview);
            oldPreview.QueueFree();
        }
        _constructionPreview = CreateModelPreview("ConstructionModelPreview", choice.Building, choice.Road, choice.Template);
        _constructionPreview.CustomMinimumSize = new Vector2(0, 144);
        _constructionPreview.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        previewSlot.AddChild(_constructionPreview);
        _constructionDescription.Text = choice.Help.Description;
        string effectDescription = "";
        if (choice.Building is { } describedBuilding)
        {
            var effects = AreaOfEffect.ForBuilding(describedBuilding, new Pos(0, 0))
                .SelectMany(area => area.Glyphs.Select(glyph =>
                    $"{(glyph == "♧" ? "Clean air: reduces nearby pollution" :
                        glyph == "Ψ" ? "Pollution: produces nearby smoke pollution" :
                        $"{CityReport.ServiceName(describedBuilding.Service)} timely response")}: " +
                    (glyph is "♧" or "Ψ" ? $"radius {area.Radius} cells. " +
                    $"Area: {area.Radius * 2 + 1} x {area.Radius * 2 + 1} cells. " +
                    $"Fades beyond radius to {area.Radius * AreaFalloff.Extent:0.##} cells." :
                    $"target {describedBuilding.ResponseTargetDays:0.00} days; " +
                    "roads speed arrival, workload adds delay. No distance cutoff.")));
            effectDescription = string.Join("\n", effects);
        }
        foreach (var child in _constructionStats.GetChildren())
        {
            _constructionStats.RemoveChild(child);
            child.QueueFree();
        }
        var rows = choice.Help.Details.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(detail =>
        {
            int separator = detail.IndexOf(':');
            return (Name: separator >= 0 ? detail[..separator] : "Effect",
                Value: separator >= 0 ? detail[(separator + 1)..].Trim() : detail);
        }).Where(row => row.Name is not ("Does" or "Placement" or "Area icons") &&
            !((choice.Road is not null || choice.Template is not null) && row.Name == "Needs"))
            .OrderBy(row => row.Name switch { "Cost" => 0, "Upkeep" => 1, "Capacity" => 2, _ => 3 })
            .Select(row =>
            {
                string value = row.Name switch
                {
                    "Cost" when choice.Template is { } template => Fmt.Money(Session.Game.QuoteRoadTemplate(template, Session.Cursor).Cost) +
                        " (current plot)",
                    "Cost" => Fmt.Money(choice.Building?.Cost ?? (int)Math.Round(
                        Session.Game.Config.RoadCostPerCell * choice.Road!.CostMultiplier)),
                    "Footprint" => row.Value.Replace(" cells", "", StringComparison.Ordinal),
                    _ => row.Value,
                };
                if (value.Length > 0 && char.IsLetter(value[0]))
                    value = char.ToUpperInvariant(value[0]) + value[1..];
                return (Name: row.Name, Value: value);
            }).ToArray();
        var effectValues = rows.Where(row => row.Name == "Effect").Select(row => row.Value)
            .Concat(effectDescription.Length > 0 ? [effectDescription] : Array.Empty<string>()).ToArray();
        if (choice.Building is not null && effectDescription.Length == 0) effectValues = [];
        if (choice.Template is not null) effectValues = [];
        rows = rows.Where(row => row.Name != "Effect")
            .Concat(effectValues.Length > 0
                ? new[] { (Name: "Effect", Value: string.Join("\n\n", effectValues)) }
                : Array.Empty<(string Name, string Value)>()).ToArray();
        float labelWidth = rows.Max(row =>
            Theme.DefaultFont.GetStringSize(row.Name, fontSize: Theme.DefaultFontSize).X);
        float namesWidth = _promptButtons[0].GetParent<Control>().GetCombinedMinimumSize().X;
        float availableKeyWidth = Math.Max(40, Size.X - (Map.GetParent<Control>().GlobalPosition.X - GlobalPosition.X) -
            namesWidth - 112 - RailPadding * 2 - 92);
        bool wrapKeys = labelWidth > availableKeyWidth;
        if (wrapKeys) labelWidth = availableKeyWidth;
        _constructionDescription.AutowrapMode = wrapKeys
            ? TextServer.AutowrapMode.Arbitrary : TextServer.AutowrapMode.WordSmart;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var label = new Label { Text = row.Name, CustomMinimumSize = new Vector2(labelWidth, 0),
                VerticalAlignment = VerticalAlignment.Top,
                AutowrapMode = wrapKeys ? TextServer.AutowrapMode.Arbitrary : TextServer.AutowrapMode.Off };
            label.AddThemeColorOverride("font_color", new Color("#70d7ff"));
            var keyCell = ConstructionTableCell(label, left: false, top: i == 0);
            keyCell.SizeFlagsHorizontal = SizeFlags.Fill;
            _constructionStats.AddChild(keyCell);
            Control valueContent = new Label
            {
                Text = row.Value,
                VerticalAlignment = VerticalAlignment.Top,
                AutowrapMode = wrapKeys ? TextServer.AutowrapMode.Arbitrary : TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(80, 0),
            };
            var valueCell = ConstructionTableCell(valueContent, left: false, top: i == 0);
            valueCell.SetMeta("details_value", row.Value);
            _constructionStats.AddChild(valueCell);
        }

        UpdateConstructionPurchaseState();
        for (int i = 0; i < _promptButtons.Count; i++)
        {
            _promptButtons[i].ToggleMode = true;
            _promptButtons[i].SetPressedNoSignal(i == index);
        }
        UpdateConstructionCoverage(force: true);
        Callable.From(CenterModal).CallDeferred();
    }

    private void UpdateConstructionPurchaseState()
    {
        if (!ConstructionMenuVisible || _constructionChoices.Length == 0) return;
        var choice = _constructionChoices[_constructionIndex];
        string? error = choice.Building is { } building ? Session.BuildingPurchaseError(building) : null;
        _constructionAdd.Disabled = error is not null;
        _constructionAdd.TooltipText = error ?? "Start placement on the map.";
        _constructionAdd.AddThemeColorOverride("font_disabled_color", new Color("#888888"));
    }

    private static PanelContainer ConstructionTableCell(Control content, bool left = true, bool top = true)
    {
        content.SizeFlagsVertical = SizeFlags.Fill;
        var cell = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat { BgColor = Colors.Black, BorderColor = Colors.White };
        style.SetBorderWidthAll(1);
        style.BorderWidthLeft = left ? 1 : 0;
        style.BorderWidthTop = top ? 1 : 0;
        style.ContentMarginLeft = style.ContentMarginRight = 6;
        style.ContentMarginTop = style.ContentMarginBottom = 3;
        cell.AddThemeStyleboxOverride("panel", style);
        cell.AddChild(content);
        return cell;
    }

    private void AddConstruction()
    {
        var choice = _constructionChoices[_constructionIndex];
        if (choice.Building is { } building)
        {
            Session.PreviewBuilding(building);
            if (!Session.BuildingToolActive) return;
        }
        else if (choice.Template is { } template) Session.PreviewRoadTemplate(template);
        else if (choice.Road is { } road) Session.BeginRoadLine(road);
        Session.ClosePrompt();
    }
}
