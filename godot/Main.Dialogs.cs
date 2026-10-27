using Godot;
using TermCity.Core.Session;

namespace TermCity.GodotApp;

public partial class Main
{
    private Label? _fontSizeLabel;

    private const string FontDialogTitle = "Font size";

    private const string GameDialogTitle = "GAME MENU";
    private const string ReportDialogTitle = "Weekly Report";

    private Label? _sidebarSizeLabel;

    private TerminalFrame _modal = null!;

    private Control _modalShield = null!;

    private VBoxContainer _linePreview = null!;

    private Label _lineSummary = null!;

    private Label _modalError = null!;

    private ScrollContainer? _modalScroll;

    private HBoxContainer? _modalColumns;

    private ScrollContainer? _choiceHelpScroll;

    private Label? _choiceHelpText;
    private VBoxContainer? _choiceHelpPreviews;

    private float _modalContentWidth;

    private readonly List<Button> _promptButtons = [];

    private readonly List<Control> _dialogControls = [];

    private int _promptIndex;

    private bool _helpVisible;

    private object? _shownModal;

    private void RefreshMenuState(Action target, string state)
    {
        if (Session.Prompt is not { } prompt) return;
        var choices = prompt.Choices.Select(choice => choice.Select == target
            ? choice with { Cells = [choice.Cells?[0] ?? string.Empty, state] }
            : choice).ToArray();
        Session.ShowPrompt(prompt.Title, prompt.Text, choices, prompt.Input, prompt.Footer, prompt.Columns);
    }

    private void ShowFontDialog() => Session.ShowPrompt(FontDialogTitle, "Adjust the display font size (16-28).",
    [
        new("OK", Session.CancelPrompt),
        new("RESET", () => SetFontSize(DefaultFontSize)),
    ]);

    private void ShowGameMenu()
    {
        Session.ShowPrompt(GameDialogTitle, "",
        [
            new("FONT SIZE", ShowFontDialog),
            new("RESIZE SIDEBAR", ShowSidebarDialog),
        ]);
    }

    private void ShowSidebarDialog() => Session.ShowPrompt("Sidebar size",
        "Adjust the sidebar width.",
        [new("OK", Session.CancelPrompt), new("RESET", () => _split.SetSidebarWidth(CitySplit.DefaultSidebar))]);

    private void UpdateModal()
    {
        if (Session.Prompt?.Title == "Garrison" && !ConstructionMenuVisible)
        {
            ShowConstructionMenu(TermCity.Core.Buildings.ServiceKind.Defence, fromContextMenu: ConstructionContextCell is not null);
            return;
        }
        _linePreview.Visible = Session.RoadToolActive && Session.Prompt is null;
        bool nextStroke = Session.Preview is { RoadStroke.Count: 1, Quote.Cells: 0, Road: { } type } preview &&
            Session.Game.Map.RoadTypeAt(preview.RoadStroke[0].X, preview.RoadStroke[0].Y)?.Rank >= type.Rank;
        _lineSummary.Text = (nextStroke ? Session.Preview!.Road!.Name + ": ready for the next stroke." : Session.Preview?.Summary) +
            "\nClick an endpoint to build now. Drag to extend; release or Enter builds. Esc exits.";
        object? state = Session.Prompt ?? (Session.RoadToolActive || Session.FixedPlacementToolActive ? null : (object?)Session.Preview);
        if (ReferenceEquals(state, _shownModal))
        {
            return;
        }
        int selectedIndex = state is SessionPrompt next ? next.SelectedIndex : 0;
        _shownModal = state;
        _fontSizeLabel = null;
        _sidebarSizeLabel = null;
        _modalScroll = null;
        _modalColumns = null;
        _choiceHelpScroll = null;
        _choiceHelpText = null;
        _choiceHelpPreviews = null;
        _promptButtons.Clear();
        _dialogControls.Clear();
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
            ClearConstructionContext();
            return;
        }
        StopPointerGesture();
        if (ConstructionMenuVisible)
        {
            BuildConstructionMenu(modalBody);
            return;
        }
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
        bool reportDialog = Session.Prompt?.Title == ReportDialogTitle;
        bool milestoneDialog = Session.Prompt?.Title == TermCity.Core.Rendering.MilestoneReport.Title;
        bool milestonesReport = reportDialog && Session.Prompt is { Tabs: { } reportTabs } reportPrompt &&
            reportTabs[reportPrompt.ActiveTab].Title == TermCity.Core.Rendering.MilestoneReport.ReportTabTitle;
        bool buildingDialog = Session.Prompt?.Title == TermCity.Core.Rendering.BuildingReport.Title;
        bool tableDialog = reportDialog || milestoneDialog || buildingDialog;
        if (reportDialog) _modalContentWidth = Math.Max(_modalContentWidth, 720);
        if (milestoneDialog) _modalContentWidth = Math.Max(_modalContentWidth, 720);
        if (buildingDialog) _modalContentWidth = Math.Max(_modalContentWidth, 640);
        if (Session.Prompt?.Title == "City Grew!") _modalContentWidth = Math.Max(_modalContentWidth, 640);
        int fixedTextLines = 0;
        if (!tableDialog && Session.Prompt is { } fitPrompt && (fitPrompt.Tabs is not null || _helpVisible))
        {
            var pages = fitPrompt.Tabs?.Select(tab => tab.Text) ?? [fitPrompt.Text];
            foreach (string page in pages)
            {
                string[] pageLines = page.Split('\n');
                fixedTextLines = Math.Max(fixedTextLines, pageLines.Length);
                _modalContentWidth = Math.Max(_modalContentWidth, pageLines.Max(TextWidth) + characterWidth * 12);
            }
        }
        bool gameMenu = Session.Prompt?.Title == GameDialogTitle;
        var margin = new MarginContainer { Name = "DialogContentMargin" };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", gameMenu || tableDialog || side is "top" or "bottom" ? 2 : 0);
        }
        var scroll = new ScrollContainer
        {
            ClipContents = true,
            CustomMinimumSize = new Vector2(Math.Min(_modalContentWidth, Size.X - 64), 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true,
        };
        _modalScroll = scroll;
        var padding = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        int sidePadding = gameMenu || tableDialog ? 0 : 12;
        padding.AddThemeConstantOverride("margin_left", sidePadding);
        padding.AddThemeConstantOverride("margin_right", sidePadding);
        padding.AddChild(content);
        scroll.AddChild(padding);
        var columns = new HBoxContainer();
        _modalColumns = columns;
        columns.AddChild(scroll);
        margin.AddChild(columns);
        modalBody.AddChild(margin);
        var helpScroll = new ScrollContainer
        {
            Name = "ChoiceHelpPanel", Visible = false, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _choiceHelpScroll = helpScroll;
        var helpContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var closeHelp = new Button { Text = "Close help", FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left, ClipText = true };
        closeHelp.Pressed += () =>
        {
            if (Session.Prompt is { } current) current.HelpIndex = null;
            helpScroll.Visible = false;
            CenterModal();
        };
        helpContent.AddChild(closeHelp);
        _choiceHelpPreviews = new VBoxContainer { Name = "ChoiceHelpPreviewSlot",
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        helpContent.AddChild(_choiceHelpPreviews);
        _choiceHelpText = new Label
        {
            Name = "ChoiceHelpText", AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        helpContent.AddChild(_choiceHelpText);
        helpScroll.AddChild(helpContent);
        columns.AddChild(helpScroll);
        var title = new Label
        {
            Text = Session.Prompt?.Title ?? "Confirm placement",
            HorizontalAlignment = milestoneDialog || gameMenu ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Modulate = new Color(milestoneDialog ? "#ffe066" : "#70d7ff"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(PopupFrame(title));
        if (!milestoneDialog && Session.Prompt is { Tabs: { } tabs } tabbed)
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
        if (tableDialog)
        {
            int sectionIndex = 0;
            var detailPage = milestoneDialog
                ? TermCity.Core.Rendering.MilestoneReport.Build(Session.Game, Session.Prompt!.ActiveTab)
                : buildingDialog ? TermCity.Core.Rendering.BuildingReport.Build(Session.Game, Session.Cursor) : null;
            var sections = detailPage is not null
                ? detailPage.Groups
                    .Select(group => new ReportSection(group.Title, group.Rows.Select(row =>
                        row.Select(cell => new SidebarCell(cell.Text, cell.Color)).ToArray()).ToArray())).ToArray()
                : ReportTables.Build(Session);
            foreach (var section in sections)
            {
                var buildingArt = milestoneDialog || milestonesReport ? Session.Game.Map.Content.Buildings.FirstOrDefault(building =>
                    building.PlayerPlaceable && building.Name.ToUpperInvariant() == section.Title) : null;
                if (milestoneDialog && sectionIndex == 1 && buildingArt is not null)
                {
                    var servicesHeader = new Label { Name = "NewServicesHeader", Text = "New Services",
                        Modulate = new Color("#70d7ff"), HorizontalAlignment = HorizontalAlignment.Center };
                    servicesHeader.AddThemeFontSizeOverride("font_size", 24);
                    content.AddChild(PopupFrame(servicesHeader));
                }
                var heading = new Label { Text = section.Title,
                    HorizontalAlignment = (milestoneDialog || milestonesReport) && sectionIndex == 0
                        ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    Modulate = new Color((milestoneDialog || milestonesReport) && sectionIndex == 0 ? "#88ee99" : "#70b7ff") };
                heading.AddThemeFontSizeOverride("font_size", (milestoneDialog || milestonesReport) && sectionIndex == 0 ? 24 : 18);
                var sectionHeader = PopupFrame(heading);
                sectionHeader.Name = $"ReportSectionHeader{sectionIndex}";
                content.AddChild(sectionHeader);
                var reportTable = CityPanel.CreateTable($"ReportTable{sectionIndex++}", 2);
                float labelWidth = section.Rows.Max(row =>
                    Theme.DefaultFont.GetStringSize(row[0].Text, fontSize: 16).X);
                int cellIndex = 0;
                foreach (var row in section.Rows)
                    foreach (var value in row)
                    {
                        bool labelColumn = cellIndex % 2 == 0;
                        var label = new Label { Text = value.Text,
                            AutowrapMode = TextServer.AutowrapMode.WordSmart,
                            HorizontalAlignment = labelColumn ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                            CustomMinimumSize = new Vector2(labelColumn ? labelWidth : 0, 0),
                            SizeFlagsVertical = SizeFlags.ShrinkCenter,
                            SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        label.AddThemeFontSizeOverride("font_size", 16);
                        label.AddThemeColorOverride("font_color", ToColor(value.Color));
                        var cell = CityPanel.CellStyle(cellIndex++, 2);
                        cell.SizeFlagsHorizontal = labelColumn ? SizeFlags.Fill : SizeFlags.ExpandFill;
                        cell.AddChild(label);
                        reportTable.AddChild(cell);
                    }
                if (buildingArt is not null)
                {
                    var buildingSection = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    buildingSection.AddThemeConstantOverride("separation", 12);
                    buildingSection.AddChild(reportTable);
                    var artwork = CreateModelPreview("MilestoneBuildingPreview", building: buildingArt);
                    artwork.CustomMinimumSize = new Vector2(80, 72);
                    buildingSection.AddChild(ConstructionTableCell(artwork));
                    buildingSection.GetChild<Control>(1).SizeFlagsHorizontal = SizeFlags.Fill;
                    content.AddChild(buildingSection);
                }
                else content.AddChild(reportTable);
                if (milestonesReport && sectionIndex == 1)
                {
                    var size = TermCity.Core.Simulation.CityProgression.SizeProgress(Session.Game);
                    var progress = new ProgressBar { Name = "CitySizeProgress", MinValue = 0, MaxValue = 1,
                        Value = size.Fraction, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 20),
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        TooltipText = size.Target is { } target
                            ? $"{size.Population:N0} / {target:N0} people toward {size.NextName}" : "Largest city size reached." };
                    var track = new StyleBoxFlat { BgColor = Colors.Black, BorderColor = Colors.White };
                    track.SetBorderWidthAll(1);
                    progress.AddThemeStyleboxOverride("background", track);
                    progress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("#88ee99") });
                    content.AddChild(progress);
                }
            }
            textLabel.QueueFree();
        }
        else if (Session.Prompt?.Title != GameDialogTitle &&
            Session.Prompt?.IsSaveDialog != true) content.AddChild(textLabel);
        else textLabel.QueueFree();
        _modalError = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color("#ff7777") };
        content.AddChild(_modalError);
        BoxContainer buttons = tableDialog
            ? new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End } : new VBoxContainer();
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
            if (prompt.Title == "Sidebar size")
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
                    Alignment = tableDialog || prompt.IsSaveDialog || prompt.Choices[i].Label.StartsWith("Cancel", StringComparison.Ordinal)
                        ? HorizontalAlignment.Center : HorizontalAlignment.Left, ClipText = true,
                    TooltipText = $"{prompt.Choices[i].Label} ({(shortcut.Shift ? "Shift+" : "")}{shortcut.Letter})\nShift+click or Shift+Enter: help; Enter: activate." };
                if (tableDialog)
                    button.CustomMinimumSize = new Vector2(Math.Max(96, TextWidth(rowText) +
                        _modal.GetThemeStylebox("normal", "Button").GetContentMargin(Side.Left) +
                        _modal.GetThemeStylebox("normal", "Button").GetContentMargin(Side.Right)), 36);
                if (prompt.IsSaveDialog && prompt.Choices[i].Label is "Save and quit" or "Quit without saving")
                {
                    var color = new Color(prompt.Choices[i].Label == "Save and quit" ? "#88ee99" : "#ff7777");
                    foreach (string colorState in new[] { "font_color", "font_hover_color", "font_pressed_color",
                        "font_hover_pressed_color", "font_focus_color" })
                        button.AddThemeColorOverride(colorState, color);
                }
                _dialogControls.Add(button);
                if (prompt.Title == GameDialogTitle)
                {
                    foreach (string colorState in new[] { "font_color", "font_hover_color", "font_pressed_color",
                        "font_hover_pressed_color", "font_focus_color" })
                        button.AddThemeColorOverride(colorState, Colors.White);
                    button.CustomMinimumSize = new Vector2(0, 32);
                }
                button.GuiInput += inputEvent =>
                {
                    if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, ShiftPressed: true } mouse)
                    {
                        if (mouse.Pressed) ShowChoiceHelp(index);
                        button.AcceptEvent();
                    }
                };
                button.Pressed += () => SelectPromptChoice(index);
                button.FocusEntered += () => { _promptIndex = index; prompt.SelectedIndex = index; };
                if (prompt.Title == GameDialogTitle)
                {
                    bool font = prompt.Choices[i].Select == ShowFontDialog;
                    var row = new HBoxContainer();
                    button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    row.AddChild(button);
                    AddButton(row, "-", () =>
                    {
                        if (font) SetFontSize(Math.Max(MinFontSize, FontSize - 2));
                        else _split.SetSidebarWidth(_split.SidebarWidth - TerminalGrid.CellWidth);
                    });
                    row.GetChild<Button>(1).Name = font ? "FontSizeMinus" : "SidebarSizeMinus";
                    AddButton(row, "RESET", () =>
                    {
                        if (font) SetFontSize(DefaultFontSize);
                        else _split.SetSidebarWidth(CitySplit.DefaultSidebar);
                    });
                    row.GetChild<Button>(2).Name = font ? "FontSizeReset" : "SidebarSizeReset";
                    var reset = row.GetChild<Button>(2);
                    reset.Alignment = HorizontalAlignment.Center;
                    foreach (string resetState in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
                    {
                        var resetStyle = (StyleBoxFlat)_modal.GetThemeStylebox(resetState, "Button").Duplicate();
                        resetStyle.ContentMarginLeft = resetStyle.ContentMarginRight = 16;
                        resetStyle.ContentMarginTop = resetStyle.ContentMarginBottom = 10;
                        reset.AddThemeStyleboxOverride(resetState, resetStyle);
                    }
                    AddButton(row, "+", () =>
                    {
                        if (font) SetFontSize(Math.Min(MaxFontSize, FontSize + 2));
                        else _split.SetSidebarWidth(_split.SidebarWidth + TerminalGrid.CellWidth);
                    });
                    row.GetChild<Button>(3).Name = font ? "FontSizePlus" : "SidebarSizePlus";
                    foreach (var stepButton in new[] { row.GetChild<Button>(1), row.GetChild<Button>(3) })
                    {
                        stepButton.CustomMinimumSize = new Vector2(48, 44);
                        stepButton.AddThemeFontSizeOverride("font_size", 28);
                        stepButton.Alignment = HorizontalAlignment.Center;
                    }
                    buttons.AddChild(row);
                }
                else buttons.AddChild(button);
                _promptButtons.Add(button);
            }
            if (prompt.Footer is { } footer)
                content.AddChild(new Label { Text = footer, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            if (prompt.Title == GameDialogTitle)
            {
                _modalContentWidth = 420;
            }
            else if (table is { } header)
            {
                var headerBox = new MarginContainer();
                headerBox.AddThemeConstantOverride("margin_left", (int)ButtonTextInset());
                headerBox.AddChild(new Label { Text = header.Header + "\n" + header.Rule, ClipText = true });
                content.AddChild(headerBox);
            }
            if (prompt.Tabs is not null && !milestoneDialog)
            {
                content.AddChild(new Label
                {
                    Text = reportDialog ? "Left/Right: change tab. Esc: close." :
                        "Left/Right: change tab. Underlined letter or Enter: pick an action. Esc: back.",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                });
            }
        }
        else
        {
            AddButton(buttons, "Confirm [Enter]", () => Session.ConfirmPreview(), underline: 0);
            AddButton(buttons, "Cancel [Esc]", Session.CancelPreview, underline: 1);
        }
        content.AddChild(buttons);
        if (Session.Prompt?.Title == GameDialogTitle)
        {
            AddAudioControls(content, music: true);
            AddAudioControls(content, music: false);
            var newMapActions = new HBoxContainer();
            AddButton(newMapActions, "NEW MAP", () => Session.RequestNewCity(restart: false));
            var newMap = newMapActions.GetChild<Button>(0);
            newMap.Name = "GameMenuNewMap";
            newMap.Alignment = HorizontalAlignment.Center;
            newMap.CustomMinimumSize = new Vector2(96, 36);
            foreach (string colorState in new[] { "font_color", "font_hover_color", "font_pressed_color",
                "font_hover_pressed_color", "font_focus_color" })
                newMap.AddThemeColorOverride(colorState, new Color("#88ee99"));
            var footerActions = new HBoxContainer();
            AddButton(footerActions, "SAVE", () => Session.QuickSave(), underline: 0);
            var save = footerActions.GetChild<Button>(0);
            save.Name = "GameMenuSave";
            save.Alignment = HorizontalAlignment.Center;
            save.CustomMinimumSize = new Vector2(96, 36);
            save.TooltipText = $"Quick-save to {Session.SaveDisplayPath} (S)";
            newMapActions.RemoveChild(newMap);
            newMapActions.QueueFree();
            footerActions.AddChild(newMap);
            _dialogControls.Remove(newMap);
            _dialogControls.Insert(_dialogControls.IndexOf(save) + 1, newMap);
            footerActions.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var actions = CreateCatalogActions("GameMenuQuit", "QUIT", Session.RequestQuit, new Color("#ff7777"),
                "GameMenuCancel", Session.ClosePrompt, underline: 0);
            footerActions.AddChild(actions);
            var cancel = actions.GetChild<Button>(1);
            cancel.Text = "CANCEL";
            var actionPadding = new MarginContainer();
            actionPadding.AddThemeConstantOverride("margin_top", 16);
            actionPadding.AddChild(footerActions);
            content.AddChild(actionPadding);
            cancel.GrabFocus();
        }
        else if (buttons.GetChildCount() > 0)
        {
            if (inputField is not null) inputField.GrabFocus();
            else buttons.GetChild<Button>(Math.Clamp(_promptIndex, 0, buttons.GetChildCount() - 1)).GrabFocus();
        }
        if (Session.Prompt?.HelpIndex is { } helpIndex) ShowChoiceHelp(helpIndex, focusChoice: false);
        CenterModal();
        Callable.From(CenterModal).CallDeferred();
        if (milestonesReport)
        {
            var shownPrompt = Session.Prompt;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(scroll) && ReferenceEquals(Session.Prompt, shownPrompt))
                    scroll.ScrollVertical = 0;
            }).CallDeferred();
        }
    }

    private float ButtonTextInset() => GetThemeStylebox("normal", "Button")?.GetMargin(Side.Left) ?? 0;

    private HBoxContainer CreateCatalogActions(string primaryName, string primaryText, Action primaryAction,
        Color color, string cancelName, Action cancelAction, int underline = -1)
    {
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        Button primary = underline < 0 ? new Button() : new MnemonicButton { UnderlineIndex = underline };
        primary.Name = primaryName;
        primary.Text = primaryText;
        primary.CustomMinimumSize = new Vector2(96, 36);
        foreach (string state in new[] { "normal", "hover", "pressed" })
        {
            var style = new StyleBoxFlat
            {
                BgColor = new Color(state == "hover" ? "#444444" : state == "pressed" ? "#222222" : "#333333"),
                ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 6, ContentMarginBottom = 6,
                BorderColor = Colors.White,
            };
            style.SetBorderWidthAll(1);
            primary.AddThemeStyleboxOverride(state, style);
        }
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
            primary.AddThemeColorOverride(state, color);
        primary.Pressed += primaryAction;
        actions.AddChild(primary);
        _dialogControls.Add(primary);
        AddButton(actions, "Cancel", cancelAction, underline);
        var cancel = actions.GetChild<Button>(1);
        cancel.Name = cancelName;
        cancel.Alignment = HorizontalAlignment.Center;
        cancel.CustomMinimumSize = primary.CustomMinimumSize;
        float width = Math.Max(primary.GetCombinedMinimumSize().X, cancel.GetCombinedMinimumSize().X);
        primary.CustomMinimumSize = new Vector2(width, 36);
        cancel.CustomMinimumSize = primary.CustomMinimumSize;
        return actions;
    }

    private void UpdateMenuSizeControls()
    {
        if (_fontSizeLabel is not null) _fontSizeLabel.Text = FontSize.ToString();
        if (_sidebarSizeLabel is not null) _sidebarSizeLabel.Text = $"{_split.SidebarWidth:0} px";
    }

    private Theme CreateDialogButtonTheme()
    {
        var theme = new Theme();
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
        {
            var existing = GetThemeStylebox(state, "Button");
            var style = new StyleBoxFlat
            {
                BgColor = state switch
                {
                    "hover" => new Color("#18232c"),
                    "pressed" or "hover_pressed" => new Color("#243847"),
                    "focus" => new Color("#18232c"),
                    _ => new Color("#101010"),
                },
                BorderColor = Colors.White,
            };
            style.SetBorderWidthAll(1);
            foreach (var side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
                style.SetContentMargin(side, existing.GetContentMargin(side));
            theme.SetStylebox(state, "Button", style);
        }
        foreach (string type in new[] { "VScrollBar", "HScrollBar" })
        {
            foreach (string state in new[] { "scroll", "scroll_focus", "grabber", "grabber_highlight", "grabber_pressed" })
            {
                var style = new StyleBoxFlat
                {
                    BgColor = state switch
                    {
                        "grabber" => new Color("#888888"),
                        "grabber_highlight" => Colors.White,
                        "grabber_pressed" => new Color("#ffe066"),
                        _ => Colors.Black,
                    },
                    BorderColor = state == "scroll_focus" ? new Color("#ffe066") : Colors.White,
                    ContentMarginLeft = type == "VScrollBar" ? 6 : 8,
                    ContentMarginRight = type == "VScrollBar" ? 6 : 8,
                    ContentMarginTop = type == "VScrollBar" ? 8 : 6,
                    ContentMarginBottom = type == "VScrollBar" ? 8 : 6,
                };
                style.SetBorderWidthAll(1);
                theme.SetStylebox(state, type, style);
            }
            using var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            image.Fill(Colors.Transparent);
            var empty = ImageTexture.CreateFromImage(image);
            foreach (string icon in new[] { "increment", "increment_highlight", "increment_pressed",
                "decrement", "decrement_highlight", "decrement_pressed" })
                theme.SetIcon(icon, type, empty);
        }
        return theme;
    }

    private static TerminalFrame PopupFrame(Control content)
    {
        var frame = new TerminalFrame { JoinAdjacent = false, SizeFlagsVertical = SizeFlags.Fill };
        frame.GetThemeStylebox("panel").ContentMarginTop = 6;
        frame.GetThemeStylebox("panel").ContentMarginBottom = 6;
        frame.AddChild(content);
        return frame;
    }

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
                Flat = false,
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
        button.Alignment = text.StartsWith("Cancel", StringComparison.Ordinal)
            ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        button.Pressed += action;
        buttons.AddChild(button);
        _dialogControls.Add(button);
    }

    private void SelectPromptChoice(int index)
    {
        var context = ConstructionMenuVisible ? null : ConstructionContextCell;
        if (index >= 0 && index < _promptButtons.Count)
        {
            _promptIndex = index;
            _promptButtons[index].GrabFocus();
        }
        Session.SelectPrompt(index);
        ConfirmContextConstruction(context);
    }

    private void ShowChoiceHelp(int index, bool focusChoice = true)
    {
        if (Session.Prompt is not { } prompt || index < 0 || index >= prompt.Choices.Count ||
            _choiceHelpScroll is null || _choiceHelpText is null) return;
        prompt.HelpIndex = index;
        if (focusChoice)
        {
            prompt.SelectedIndex = index;
            _promptIndex = index;
            _promptButtons[index].GrabFocus();
        }
        var help = prompt.ActiveHelp!;
        string choiceName = prompt.Choices[index].Label;
        var building = Session.Game.Map.Content.Buildings.FirstOrDefault(type =>
            type.Name == choiceName || type.CatalogName == choiceName);
        var road = Session.Game.Map.Content.Roads.FirstOrDefault(type => type.Name == choiceName);
        var template = choiceName == TermCity.Core.Roads.RoadTemplate.TrafficCircle.Name
            ? TermCity.Core.Roads.RoadTemplate.TrafficCircle : null;
        foreach (var child in _choiceHelpPreviews!.GetChildren())
        {
            _choiceHelpPreviews.RemoveChild(child);
            child.QueueFree();
        }
        bool modelHelp = building is not null || road is not null || template is not null;
        if (modelHelp)
        {
            var artwork = CreateModelPreview("ChoiceHelpModelPreview", building, road, template);
            artwork.CustomMinimumSize = new Vector2(0, 144);
            artwork.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _choiceHelpPreviews.AddChild(artwork);
        }
        string details = modelHelp ? help.Details.Replace(
            "Roads appear as smooth curves; these glyphs show connections on overlays and the minimap.\n\n",
            "", StringComparison.Ordinal) : help.Details;
        _choiceHelpText.Text = $"{(modelHelp ? "" : help.Icons + "\n\n")}{choiceName}\n\n{help.Description}\n\n{details}";
        _choiceHelpScroll.Visible = true;
        _choiceHelpScroll.ScrollVertical = 0;
        CenterModal();
        Callable.From(CenterModal).CallDeferred();
    }

    private bool HandleDialogKey(InputEventKey key)
    {
        if (Session.Prompt is not { } prompt) return false;
        var code = key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode;
        var focus = GetViewport().GuiGetFocusOwner();
        if (!key.Pressed) return focus is not LineEdit;
        if (key.ShiftPressed && code is Key.Enter or Key.KpEnter)
        {
            if (!key.Echo)
            {
                int index = focus is Button choice ? _promptButtons.IndexOf(choice) : prompt.SelectedIndex;
                if (index >= 0) ShowChoiceHelp(index);
            }
            return true;
        }
        if (code == Key.Escape)
        {
            _helpVisible = false;
            Session.CancelPrompt();
            return true;
        }
        if (code == Key.Tab && prompt.Title == GameDialogTitle)
        {
            int index = _dialogControls.FindIndex(control => control == focus);
            int direction = key.ShiftPressed ? -1 : 1;
            index = (index + direction + _dialogControls.Count) % _dialogControls.Count;
            _dialogControls[index].GrabFocus();
            return true;
        }
        if (focus is LineEdit || code == Key.Tab) return false;
        if (focus is Slider && code is Key.Left or Key.Right or Key.Home or Key.End)
            return false;
        if (ConstructionMenuVisible && (focus is not Button focusedButton ||
            _promptButtons.Contains(focusedButton) || focusedButton == _constructionAdd) &&
            code is Key.Enter or Key.KpEnter)
        {
            if (!key.Echo && !_constructionAdd.Disabled) _constructionAdd.EmitSignal(Button.SignalName.Pressed);
            return true;
        }
        if (code is Key.Enter or Key.KpEnter or Key.Space)
        {
            if (!key.Echo)
            {
                if (focus is Button button && _dialogControls.Contains(button))
                {
                    if (!button.Disabled) button.EmitSignal(Button.SignalName.Pressed);
                }
                else if (focus is not Slider) SelectPromptChoice(prompt.SelectedIndex);
            }
            return true;
        }
        if (code is Key.Left or Key.Right && prompt.Tabs is not null)
        {
            Session.CycleTab(code == Key.Left ? -1 : 1);
            return true;
        }
        if (code is Key.Left or Key.Right or Key.Up or Key.Down && _dialogControls.Count > 0)
        {
            int index = _dialogControls.FindIndex(control => control == focus);
            int direction = code is Key.Left or Key.Up ? -1 : 1;
            index = (index + direction + _dialogControls.Count) % _dialogControls.Count;
            _dialogControls[index].GrabFocus();
            return true;
        }
        if (!key.Echo && !key.CtrlPressed && !key.MetaPressed && !key.AltPressed)
        {
            char letter = (int)code is >= (int)Key.A and <= (int)Key.Z ? (char)code
                : key.Unicode is >= 'A' and <= 'z' ? char.ToUpperInvariant((char)key.Unicode) : '\0';
            if (prompt.Title == GameDialogTitle && !key.ShiftPressed && letter is 'Q' or 'C' or 'M' or 'U' or 'S')
            {
                string actionName = letter switch
                {
                    'Q' => "GameMenuQuit", 'C' => "GameMenuCancel",
                    'M' => "MusicMute", 'S' => "GameMenuSave", _ => "SoundMute",
                };
                _dialogControls.First(control => control.Name == actionName).EmitSignal(Button.SignalName.Pressed);
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

    private void ShowHelp()
    {
        _helpVisible = true;
        Session.ShowPrompt(HelpContent.Title, HelpContent.Text(OS.HasFeature("macos")),
            [new("Close", () => { _helpVisible = false; Session.CancelPrompt(); })], footer: HelpContent.Footer);
    }

    private void CenterModal()
    {
        if (!_modal.Visible) return;
        float leftEdge = Map.GetParent<Control>().GlobalPosition.X - GlobalPosition.X;
        float topEdge = Map.GetParent<Control>().GlobalPosition.Y - GlobalPosition.Y;
        float usableWidth = Math.Max(1, Size.X - leftEdge);
        if (_modalScroll is not null)
        {
            float availableWidth = Math.Max(1, usableWidth - 64);
            bool helpVisible = _choiceHelpScroll?.Visible == true;
            float helpWidth = helpVisible ? Math.Min(360, availableWidth * 0.4f) : 0;
            float gap = helpVisible ? _modalColumns!.GetThemeConstant("separation") : 0;
            var content = _modalScroll.GetChild<Control>(0);
            float outsideScrollHeight = _modal.GetCombinedMinimumSize().Y - _modalScroll.GetCombinedMinimumSize().Y;
            _modalScroll.CustomMinimumSize = new Vector2(Math.Max(1, Math.Min(_modalContentWidth, availableWidth - helpWidth - gap)),
                Math.Max(1, Math.Min(content.GetCombinedMinimumSize().Y,
                    Size.Y - topEdge - Math.Max(64, outsideScrollHeight + 28))));
            if (helpVisible)
                _choiceHelpScroll!.CustomMinimumSize = new Vector2(helpWidth, _modalScroll.CustomMinimumSize.Y);
            _modal.Size = _modal.GetCombinedMinimumSize();
        }
        var position = new Vector2(leftEdge, topEdge);
        if (!ConstructionMenuVisible)
        {
            var mapFrame = Map.GetParent<Control>();
            position += (mapFrame.Size - _modal.Size) / 2;
            position.X = Math.Clamp(position.X, leftEdge, Math.Max(leftEdge, Size.X - _modal.Size.X));
            position.Y = Math.Clamp(position.Y, topEdge, Math.Max(topEdge, Size.Y - _modal.Size.Y));
        }
        _modal.Position = position;
    }
}
