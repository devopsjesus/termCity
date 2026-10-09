using Godot;
using TermCity.Core.Session;

namespace TermCity.GodotApp;

public partial class Main
{
    private Label? _fontSizeLabel;

    private const string FontDialogTitle = "Font size";

    private const string SidebarDialogTitle = "Sidebar size";

    private Label? _sidebarSizeLabel;

    private PanelContainer _modal = null!;

    private Control _modalShield = null!;

    private VBoxContainer _linePreview = null!;

    private Label _lineSummary = null!;

    private Label _modalError = null!;

    private ScrollContainer? _modalScroll;

    private HBoxContainer? _modalColumns;

    private ScrollContainer? _choiceHelpScroll;

    private Label? _choiceHelpText;

    private float _modalContentWidth;

    private readonly List<Button> _promptButtons = [];

    private readonly List<Button> _dialogButtons = [];

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

    private void UpdateModal()
    {
        _linePreview.Visible = Session.RoadToolActive && Session.Prompt is null;
        _lineSummary.Text = Session.Preview?.Summary + "\nArrows or drag set the endpoint. Enter confirms; Esc cancels.";
        object? state = Session.Prompt ?? (Session.RoadToolActive || Session.BuildingToolActive ? null : (object?)Session.Preview);
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
        _choiceHelpText = new Label
        {
            Name = "ChoiceHelpText", AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        helpContent.AddChild(_choiceHelpText);
        helpScroll.AddChild(helpContent);
        columns.AddChild(helpScroll);
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
                    TooltipText = $"{prompt.Choices[i].Label} ({(shortcut.Shift ? "Shift+" : "")}{shortcut.Letter})\nShift+click or Shift+Enter: help; Enter: activate." };
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
        if (Session.Prompt?.HelpIndex is { } helpIndex) ShowChoiceHelp(helpIndex, focusChoice: false);
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
        _choiceHelpText.Text = $"{help.Icons}\n\n{prompt.Choices[index].Label}\n\n{help.Description}\n\n{help.Details}";
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
            float availableWidth = Math.Max(1, Size.X - 64);
            bool helpVisible = _choiceHelpScroll?.Visible == true;
            float helpWidth = helpVisible ? Math.Min(360, availableWidth * 0.4f) : 0;
            float gap = helpVisible ? _modalColumns!.GetThemeConstant("separation") : 0;
            var content = _modalScroll.GetChild<Control>(0);
            _modalScroll.CustomMinimumSize = new Vector2(Math.Max(1, Math.Min(_modalContentWidth, availableWidth - helpWidth - gap)),
                Math.Max(1, Math.Min(content.GetCombinedMinimumSize().Y, Size.Y - 96)));
            if (helpVisible)
                _choiceHelpScroll!.CustomMinimumSize = new Vector2(helpWidth, _modalScroll.CustomMinimumSize.Y);
            _modal.Size = _modal.GetCombinedMinimumSize();
        }
        _modal.Position = (Size - _modal.Size) / 2;
    }
}
