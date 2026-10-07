using TermCity.Core.Roads;
using Godot;
using TermCity.Core.Rendering;
using System.Diagnostics;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal static class GodotSmoke
{
    public static async Task RunNativeInput(Main host)
    {
        var session = host.Session;
        async Task Frames()
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        async Task KeyEvent(Key code, bool ctrl = false, uint unicode = 0, bool meta = false, Key physical = Key.None)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = physical,
                Pressed = true, CtrlPressed = ctrl, MetaPressed = meta, Unicode = unicode });
            Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = physical,
                Pressed = false, CtrlPressed = ctrl, MetaPressed = meta, Unicode = unicode });
            await Frames();
        }
        await Frames();
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        Require(host.AdvanceMusicPhrase() && host.MusicPhraseCount == 2,
            "Music did not transition from the fixed theme to a newly synthesized phrase.");
        Require(host.GetNode<AudioStreamPlayer>("CityMusic").Playing,
            "Music player stopped when the phrase changed.");
        IEnumerable<Node> Descendants(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

            var functionKeys = Descendants(host).OfType<Button>()
                .Where(b => b.Text.StartsWith("F", StringComparison.Ordinal))
                .Select(b => int.Parse(b.Text.Split(' ')[0].AsSpan(1))).ToArray();
            Require(functionKeys.SequenceEqual(new[] { 1, 3, 5, 6, 7, 8, 9 }),
                "Header function keys are not in numerical order.");
            Require(!Descendants(host).OfType<Label>().Any(l => l.Text.StartsWith("Arrows: move", StringComparison.Ordinal)),
                "Header still contains the movement guidance row.");
            var zoomIndicator = Descendants(host).OfType<Label>().Single(l => l.Name == "ZoomIndicator");
            Require(zoomIndicator.Text == session.ZoomLabel &&
                zoomIndicator.GetParent().GetChild<Button>(0).Text == "[-]" &&
                zoomIndicator.GetParent().GetChild<Button>(2).Text == "[+]",
                "Minimap zoom indicator must be flanked by its buttons.");
        var headerLabels = Descendants(host).OfType<Label>()
            .Where(l => l.Name == "AppTitle" || l.Name == "CityNameDisplay" || l.Name == "Calendar" ||
                l.Name == "WeekProgress" || l.Name == "ClockStatus" || l.Name == "Population" || l.Name == "Budget")
            .ToArray();
        float headerCenter = headerLabels[0].GetGlobalRect().GetCenter().Y;
        Require(headerLabels.All(l => Math.Abs(l.GetGlobalRect().GetCenter().Y - headerCenter) < 1),
            "Top header text must share one horizontal row.");
        Require(headerLabels.Select(l => l.GetThemeFontSize("font_size")).Distinct().Count() == 1,
            "All top header text must have the same font size.");
        Require(headerLabels.Single(l => l.Name == "Calendar").Text == $"{session.Game.Year} Week {session.Game.WeekOfYear,2}",
            "Time header must spell Week with space padding.");
            int beforeZoom = session.ZoomLevel;
            zoomIndicator.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
            Require(session.ZoomLevel == beforeZoom + 1 && zoomIndicator.Text == session.ZoomLabel,
                "Minimap zoom-in button failed.");
            zoomIndicator.GetParent().GetChild<Button>(0).EmitSignal(Button.SignalName.Pressed);
            Require(session.ZoomLevel == beforeZoom, "Minimap zoom-out button failed.");
            foreach (var zone in Zones.Placeable)
            {
                var letter = Descendants(host).OfType<Label>().Single(l => l.Name == $"Demand{Zones.Get(zone).Letter}");
                var bar = letter.GetParent().GetChild<ProgressBar>(1);
                Require(bar.GetThemeStylebox("fill") is StyleBoxFlat fill &&
                    letter.GetThemeColor("font_color") == fill.BgColor,
                    "Demand letter does not match its bar color.");
            }
            var demandHeading = Descendants(host).OfType<Label>().Single(b => b.Name == "DemandHeading");
            Require(demandHeading.GetThemeFontSize("font_size") > host.Theme.DefaultFontSize &&
                demandHeading.GetThemeColor("font_color") == new Color("#70b7ff"),
                "Sidebar headings must be larger and blue.");
            Require(!Descendants(host).Any(n => n.Name == "CollapseCaret") &&
                demandHeading.GetParent().GetChild<Control>(1).Visible,
                "Sidebar sections must remain open without collapse carets.");
            Require(Descendants(host).OfType<Label>().Any(l => l.Name == "AppTitle" && l.Text == "TermCity"),
                "Left header must retain the TermCity title.");
            var calendarLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "WeekProgress");
            Require(calendarLabel.Text.Contains(Fmt.WeekBar(session.Game.Day, session.Game.Config.DaysPerWeek),
                StringComparison.Ordinal) && !calendarLabel.Text.Contains('%'),
                "Calendar must use the shared day bar, not a week percentage.");
            Require(calendarLabel.HorizontalAlignment == HorizontalAlignment.Center &&
                Descendants(host).OfType<Label>().Single(l => l.Name == "Calendar").HorizontalAlignment == HorizontalAlignment.Left &&
                Descendants(host).OfType<Label>().Single(l => l.Name == "Population").HorizontalAlignment == HorizontalAlignment.Left,
                "Calendar, week bar and population alignment is incorrect.");
            var clockLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "ClockStatus");
            Require(clockLabel.Text == "|| PAUSED" &&
                clockLabel.GetThemeColor("font_color") == new Color("#ffe066"),
                "Paused status must be displayed in yellow.");
            string beforeGlow = SaveGameStore.Serialize(session.Game);
            int beforeGlowRebuilds = host.Map.Grid.Rebuilds;
            host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
            var outline = clockLabel.GetThemeColor("font_outline_color");
            host._Process(0.5);
            var middleOutline = clockLabel.GetThemeColor("font_outline_color");
            host._Process(0.5);
            Require(middleOutline != outline || clockLabel.GetThemeColor("font_outline_color") != outline,
                "Paused status glow did not animate.");
            float minimumGlow = float.MaxValue, maximumGlow = float.MinValue;
            for (int beat = 0; beat < 16; beat++)
            {
                host._Process(0.25);
                minimumGlow = Math.Min(minimumGlow, clockLabel.Modulate.R);
                maximumGlow = Math.Max(maximumGlow, clockLabel.Modulate.R);
            }
            Require(maximumGlow - minimumGlow >= 0.12f && maximumGlow - minimumGlow <= 0.16f,
                "Paused brightness pulse must be gentle.");
            Require(beforeGlow == SaveGameStore.Serialize(session.Game) && beforeGlowRebuilds == host.Map.Grid.Rebuilds,
                "Paused glow changed gameplay or rebuilt the map.");
            host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
            outline = clockLabel.GetThemeColor("font_outline_color");
            host._Process(1);
            Require(clockLabel.GetThemeColor("font_outline_color") == outline,
                "Paused glow continued while unfocused.");
            host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);

        var nameLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "CityNameDisplay");
        var editor = Descendants(host).OfType<LineEdit>().Single(l => l.Name == "CityNameEditor");
        string originalName = session.Game.CityName;
        Require(nameLabel.HorizontalAlignment == HorizontalAlignment.Right &&
            CityGame.IsValidCityName(originalName), "Generated city name must be valid and right-aligned.");
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(!editor.Visible && !Descendants(host).Any(n => n.Name == "NameCursor"),
            "A single click must not show a name cursor or start editing.");
        var titleLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "AppTitle");
        var titleBounds = titleLabel.GetGlobalRect();
        var headerBounds = nameLabel.GetParent().GetParent().GetParent<Control>().GetGlobalRect();
        var font = editor.GetThemeFont("font");
        int nameSize = nameLabel.GetThemeFontSize("font_size");
        float characterWidth = font.GetStringSize("M", fontSize: nameSize).X;
        session.Game.Paused = false;
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true });
        await Frames();
        Require(editor.IsVisibleInTree() && editor.HasFocus() && editor.CaretBlink,
            "Clicking the city name did not activate its blinking-caret editor.");
        Require(session.Game.Paused && titleLabel.GetGlobalRect() == titleBounds &&
            nameLabel.GetParent().GetParent().GetParent<Control>().GetGlobalRect() == headerBounds &&
            editor.Alignment == HorizontalAlignment.Right &&
            Math.Abs(editor.GetGlobalRect().End.X - nameLabel.GetGlobalRect().End.X) < 1 &&
            editor.GetThemeConstant("caret_width") >= characterWidth,
            "Renaming must pause and keep its right-aligned block caret without changing title/header geometry.");
        await KeyEvent(Key.A, ctrl: true);
        int mapVersionBeforeName = session.Game.MapVersion;
        foreach (char character in "Rivertown") await KeyEvent(Key.None, unicode: character);
        Require(editor.Text == "Rivertown" && session.Game.MapVersion == mapVersionBeforeName,
            $"Name typing failed: text='{editor.Text}', map version {mapVersionBeforeName}->{session.Game.MapVersion}, focus={editor.HasFocus()}.");
        int caretBeforeDelete = editor.CaretColumn;
        float editorStart = editor.GlobalPosition.X;
        await KeyEvent(OS.HasFeature("macos") ? Key.Delete : Key.Backspace);
        Require(editor.Text == "Rivertow" && editor.CaretColumn == caretBeforeDelete - 1 &&
            Math.Abs(editor.GlobalPosition.X - editorStart) < 0.01,
            "Backspacing must remove the rightmost character without moving the right-aligned edit region.");
        await KeyEvent(Key.A, ctrl: true);
        foreach (char character in "12345678901234567") await KeyEvent(Key.None, unicode: character);
        Require(editor.Text == "1234567890123456", "City-name editor exceeded 16 characters.");
        await KeyEvent(Key.Enter);
        Require(session.Game.CityName == "1234567890123456" && !editor.Visible &&
            SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game)).CityName == session.Game.CityName,
            "Committed city name did not persist.");
        Require(!session.Game.Paused, "Name commit did not restore the running clock.");
        session.Game.Paused = true;
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true });
        await Frames();
        await KeyEvent(Key.A, ctrl: true);
        await KeyEvent(Key.None, unicode: 'X');
        await KeyEvent(Key.Escape);
        Require(session.Game.CityName == "1234567890123456", "Escape did not cancel name editing.");
        session.Game.RenameCity(originalName);
        Require(!Descendants(host).OfType<Label>().Any(l => l.Text == "CELL / SELECTION"),
            "Sidebar still contains duplicate cell inspection.");
        var originalCursor = session.Cursor;
        session.BeginDrag(new Pos(12, 12));
        session.UpdateDrag(new Pos(14, 12));
        session.EndSelection();
        var status = Descendants(host).OfType<Label>().Single(l => l.Name == "Status");
        Require(status.Text.StartsWith(CellInspector.Summary(session.Game, session.Cursor), StringComparison.Ordinal) &&
            status.Text.Contains("Selection 3x1 (3 cells)", StringComparison.Ordinal),
            "Multi-cell selection details are not beside the cell details in the bottom status line.");
        session.ClearSelection();
        session.PlaceCursor(originalCursor);

        await KeyEvent(Key.F10);
        Require(session.Prompt is null, "F10 must no longer open the Godot city menu.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "City menu", "Native Esc did not open the menu.");
        var cityMenu = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        float largeMenuHeight = cityMenu.Size.Y;
        var menuButtons = Descendants(cityMenu).OfType<Button>().ToArray();
        Require(menuButtons.All(b => b.Alignment == HorizontalAlignment.Left),
            "Menu choices must be left-aligned.");
        float menuCharacter = host.Theme.DefaultFont.GetStringSize("M", fontSize: host.Theme.DefaultFontSize).X;
        var menuTable = TextTable.ForPrompt(session.Prompt!)!.Value;
        float longestMenuLabel = menuTable.Rows.Append(menuTable.Header).Select(row => "00. " + row)
            .Append(session.Prompt.Title)
            .Max(text => host.Theme.DefaultFont.GetStringSize(text, fontSize: host.Theme.DefaultFontSize).X);
        float expectedMenuWidth = Math.Min(longestMenuLabel + menuCharacter * 10 + host.GetThemeStylebox("normal", "Button").GetMargin(Side.Left) * 2, host.Size.X - 64) + 24;
        Require(Math.Abs(cityMenu.Size.X - expectedMenuWidth) <= 16,
            $"Menu width must match its longest label plus 5-character side padding: {cityMenu.Size.X} vs {expectedMenuWidth}.");
        var menuPadding = Descendants(cityMenu).OfType<MarginContainer>()
            .Single(m => m.HasThemeConstantOverride("margin_left") && m.GetThemeConstant("margin_left") == (int)Math.Round(menuCharacter * 5));
        Require(menuPadding.GetThemeConstant("margin_left") == (int)Math.Round(menuCharacter * 5) &&
            menuPadding.GetThemeConstant("margin_right") == (int)Math.Round(menuCharacter * 5),
            "Menu must have five character widths of padding on each side.");
        var verticalPadding = Descendants(cityMenu).OfType<MarginContainer>()
            .Single(m => m.HasThemeConstantOverride("margin_top"));
        Require(verticalPadding.GetThemeConstant("margin_top") == 2 &&
            verticalPadding.GetThemeConstant("margin_bottom") == 2,
            "Menu must have two pixels of inner top/bottom padding.");
        Require(Math.Abs(cityMenu.GetGlobalRect().GetCenter().Y - host.Size.Y / 2) < 1,
            "Menu must remain vertically centered after fitting its content.");
        int musicChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "Music");
        Require(musicChoice >= 0, "City menu is missing the music toggle.");
        for (int item = 0; item < musicChoice; item++) await KeyEvent(Key.Down);
        await KeyEvent(Key.Enter);
        Require(!host.MusicEnabled && host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            "Music toggle did not mute playback.");
        Require(host.GetViewport().GuiGetFocusOwner() is Button { Text: var musicLabel } &&
            musicLabel.Contains("Music", StringComparison.Ordinal) && musicLabel.EndsWith(" OFF", StringComparison.Ordinal),
            "Changing music moved the highlight away from the music menu item.");
        await KeyEvent(Key.Enter);
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        Require(host.MusicEnabled && !host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            $"Music toggle did not resume playback: enabled={host.MusicEnabled}, paused={host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused}, focus={host.GetViewport().GuiGetFocusOwner()}.");
        Require(host.GetViewport().GuiGetFocusOwner() is Button { Text: var enabledLabel } &&
            enabledLabel.Contains("Music", StringComparison.Ordinal) && enabledLabel.EndsWith(" ON", StringComparison.Ordinal),
            "Repeated music changes did not preserve the highlight.");
        var musicButton = (Button)host.GetViewport().GuiGetFocusOwner();
        musicButton.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(!host.MusicEnabled && host.GetViewport().GuiGetFocusOwner() is Button { Text: var clickedLabel } &&
            clickedLabel.Contains("Music", StringComparison.Ordinal) && clickedLabel.EndsWith(" OFF", StringComparison.Ordinal),
            "Clicking a changed music item did not keep its highlight.");
        await KeyEvent(Key.Enter);
        await KeyEvent(Key.Escape);
        await KeyEvent(Key.Escape);
        await Frames();
        await KeyEvent(Key.Down);
        await KeyEvent(Key.Enter);
        Require(session.Prompt is null && !session.HasUnsavedChanges,
            "Native menu keyboard navigation did not select quick-save.");
        session.ShowLoadMenu();
        session.SelectPrompt(4);
        await Frames();
        Require(host.GetViewport().GuiGetFocusOwner() is LineEdit, "Load path field did not receive focus.");
        await KeyEvent(Key.A, ctrl: true);
        foreach (char character in "missing-city.json")
            await KeyEvent(Key.None, unicode: character);
        Require(session.Prompt?.Input == "missing-city.json",
            $"Native text input did not update the load path: '{session.Prompt?.Input}'.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt?.Title == "Load file" && session.MessageKind == MessageKind.Error,
            "Native file submission did not surface a recoverable load error.");
        Require(Descendants(host).OfType<Label>().Any(l => l.IsVisibleInTree() && l.Text.StartsWith("Load failed:", StringComparison.Ordinal)),
            "Load failure is hidden behind the modal.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt is null, "Native Escape did not dismiss the path dialog.");
        string beforeCancel = SaveGameStore.Serialize(session.Game);
        session.PreviewDemolish();
        await Frames();
        await KeyEvent(Key.Tab);
        Require(host.GetViewport().GuiGetFocusOwner() is Button { Text: "Cancel [Esc]" },
            "Preview Tab did not focus the cancel button.");
        await KeyEvent(Key.Enter);
        Require(session.Preview is null, "Preview Enter did not select the focused cancel button.");
        Require(beforeCancel == SaveGameStore.Serialize(session.Game), "Focused cancel changed the city.");
        session.ShowSessionMenu();
        await KeyEvent(Key.Key9);
        Require(session.Prompt?.Title == "Weekly report and milestones", "Numbered menu shortcuts failed.");
        await KeyEvent(Key.Escape);

        var mapCenter = new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2);
        session.PlaceCursor(mapCenter);
        session.CenterOn(mapCenter);
        await Frames();
        var pointer = new Vector2(8 * TerminalGrid.CellWidth, 6 * TerminalGrid.CellHeight);
        async Task Trackpad(Vector2 delta, bool shift = false, bool alt = false, bool ctrl = false, bool meta = false)
        {
            host.GetViewport().PushInput(new InputEventPanGesture
            {
                Position = host.Map.GlobalPosition + pointer, Delta = delta,
                ShiftPressed = shift, AltPressed = alt, CtrlPressed = ctrl, MetaPressed = meta,
            }, true);
            await Frames();
        }
        Pos ExpectedCamera(int dx, int dy)
        {
            var map = session.Game.Map;
            return new Pos(
                Math.Clamp(session.CameraX + dx, 0, Math.Max(0, map.Width - session.VisibleCellsX)) / session.Stride * session.Stride,
                Math.Clamp(session.CameraY + dy, 0, Math.Max(0, map.Height - session.VisibleCellsY)) / session.Stride * session.Stride);
        }
        for (int zoom = GameSession.MinZoom; zoom <= GameSession.MaxZoom; zoom++)
        {
            session.SetZoom(zoom);
            session.CenterOn(mapCenter);
            await Frames();
            var expected = ExpectedCamera(3 * session.Stride, 3 * session.Stride);
            await Trackpad(new Vector2(1, 1));
            Require(expected == new Pos(session.CameraX, session.CameraY),
                $"Native two-finger trackpad scrolling failed at zoom {zoom}.");
            expected = ExpectedCamera(-3 * session.Stride, -3 * session.Stride);
            await Trackpad(new Vector2(-1, -1));
            Require(expected == new Pos(session.CameraX, session.CameraY),
                $"Reverse trackpad scrolling failed at zoom {zoom}.");
        }
        session.SetZoom(0);
        session.CenterOn(mapCenter);
        await Frames();
        var trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(0.125f, 0.125f));
        await Trackpad(new Vector2(0.125f, 0.125f));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Sub-cell trackpad deltas moved too far.");
        await Trackpad(new Vector2(0.125f, 0.125f));
        Require(new Pos(trackpadStart.X + 1, trackpadStart.Y + 1) == new Pos(session.CameraX, session.CameraY),
            "Fractional trackpad deltas were dropped.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(0.25f, 0.25f));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Focus loss did not clear fractional trackpad movement.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        await Trackpad(new Vector2(0, 1), shift: true);
        Require(session.CameraX == trackpadStart.X + 3 && session.CameraY == trackpadStart.Y,
            "Shift+trackpad did not scroll horizontally.");
        await Trackpad(new Vector2(0, -1), alt: true);
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Option+trackpad did not scroll horizontally.");
        var zoomAnchor = session.ScreenToMap(8, 6);
        for (int i = 0; i < 4; i++) await Trackpad(new Vector2(0, -0.25f), ctrl: true);
        Require(host.Map.Grid.TryCell(pointer.X, pointer.Y, out var zoomCell) &&
            session.ZoomLevel == 1 && session.ScreenToMap(zoomCell.X, zoomCell.Y) == zoomAnchor &&
            host.Map.Grid.PixelWidth == TerminalGrid.CellWidth * 2 &&
            host.Map.Grid.PixelHeight == TerminalGrid.CellHeight * 2,
            "Ctrl+trackpad did not accumulate zoom around the pointer.");
        await Trackpad(new Vector2(0, 1), meta: true);
        Require(session.ZoomLevel == 0, "Command+trackpad zoom did not work.");
        await KeyEvent(Key.Equal, meta: true);
        Require(session.ZoomLevel == 1, "Command+plus did not zoom in.");
        await KeyEvent(Key.Minus, ctrl: true);
        Require(session.ZoomLevel == 0, "Ctrl+minus did not zoom out.");
        await KeyEvent(Key.None, unicode: '+');
        Require(session.ZoomLevel == 1, "Unicode plus did not zoom in.");
        await KeyEvent(Key.None, unicode: '-');
        Require(session.ZoomLevel == 0, "Unicode minus did not zoom out.");
        await KeyEvent(Key.None, physical: Key.Equal);
        Require(session.ZoomLevel == 1, "Physical equal key did not zoom in.");
        await KeyEvent(Key.Key0, meta: true);
        Require(session.ZoomLevel == 0, "Command+0 did not reset zoom.");
        async Task Pinch(float factor)
        {
            host.GetViewport().PushInput(new InputEventMagnifyGesture
            {
                Position = host.Map.GlobalPosition + pointer, Factor = factor,
            }, true);
            await Frames();
        }
        var pinchAnchor = session.ScreenToMap(8, 6);
        for (int i = 0; i < 4; i++) await Pinch(1.05f);
        Require(session.ZoomLevel == 1 && host.Map.Grid.TryCell(pointer.X, pointer.Y, out var pinchCell) &&
            session.ScreenToMap(pinchCell.X, pinchCell.Y) == pinchAnchor,
            "Native fractional pinch did not zoom around the pointer.");
        await Pinch(0.82f);
        Require(session.ZoomLevel == 0, "Native contracting pinch did not zoom out.");
        session.ShowReport();
        await Frames();
        await Pinch(1.5f);
        Require(session.ZoomLevel == 0, "Report allowed background pinch zoom.");
        trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(1, 1));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "A report allowed background trackpad scrolling.");
        await KeyEvent(Key.Escape);
        session.PreviewDemolish();
        await Frames();
        await Trackpad(new Vector2(1, 1));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "A confirmation allowed background trackpad scrolling.");
        await KeyEvent(Key.Escape);
        session.CenterOn(mapCenter);
        await Frames();
        var panStart = new Pos(session.CameraX, session.CameraY);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(3 * TerminalGrid.CellWidth, TerminalGrid.CellHeight),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false,
        });
        Require(session.CameraX == panStart.X - 3 && session.CameraY == panStart.Y - 1,
            "Plain left-drag did not pan the map.");
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer, ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = new Vector2((host.Map.Grid.Columns - 1) * TerminalGrid.CellWidth, pointer.Y),
        });
        int edgeCamera = session.CameraX;
        int edgeSelection = session.Selection!.Value.Width;
        host._Process(0.25);
        Require(session.CameraX > edgeCamera && session.Selection!.Value.Width > edgeSelection,
            "Selection drag at the edge did not scroll and extend selection.");
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false,
        });
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "City menu", "Esc must open the menu even while cells are selected.");
        await KeyEvent(Key.Escape);
        session.ClearSelection();
        await KeyEvent(Key.T);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = pointer,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(3 * TerminalGrid.CellWidth, 0),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false,
        });
        Require(session.RoadToolActive && session.Preview?.Area.Width == 4,
            "Road-line preview blocked pointer endpoint dragging.");
        await KeyEvent(Key.Escape);

        session.CenterOn(new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2));
        await Frames();
        var camera = new Pos(session.CameraX, session.CameraY);
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = host.Minimap.Size / 2,
        });
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = host.Minimap.Size });
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false,
        });
        Require(session.CameraX > camera.X && session.CameraY > camera.Y, "Minimap drag did not move to the map edge.");
        session.ShowSessionMenu();
        camera = new Pos(session.CameraX, session.CameraY);
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = Vector2.Zero,
        });
        Require(camera == new Pos(session.CameraX, session.CameraY), "Modal permitted minimap navigation.");
        await KeyEvent(Key.Escape);

        bool headless = DisplayServer.GetName() == "headless";
        var window = host.GetWindow();
        var originalSize = headless ? window.ContentScaleSize : window.Size;
        if (headless) window.ContentScaleSize = new Vector2I(640, 480);
        else window.Size = new Vector2I(640, 480);
        await Frames();
        session.ShowSessionMenu();
        await Frames();
        var finances = Descendants(host).OfType<Label>().Single(l => l.Name == "Budget");
        Require(finances.HorizontalAlignment == HorizontalAlignment.Right &&
            finances.Text.Contains($"TREASURY {Fmt.Money(session.Game.Money)}", StringComparison.Ordinal) &&
            finances.GlobalPosition.X >= host.Size.X / 2 &&
            finances.GlobalPosition.X + finances.Size.X <= host.Size.X,
            "Budget must remain visible on the right side of the HUD at minimum window size.");
        var modal = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        Require(modal.Position.X >= 0 && modal.Position.Y >= 0 &&
            modal.Position.X + modal.Size.X <= host.Size.X && modal.Position.Y + modal.Size.Y <= host.Size.Y,
            "City menu does not fit the minimum window size.");
        Require(host.Map.GlobalPosition.X + host.Map.Size.X <= host.Size.X &&
            host.Map.GlobalPosition.Y + host.Map.Size.Y <= host.Size.Y, "Map does not fit the minimum window size.");
        await KeyEvent(Key.Escape);
        if (headless) window.ContentScaleSize = originalSize;
        else window.Size = originalSize;
        await Frames();
        string cityBeforeFontChange = SaveGameStore.Serialize(session.Game);
        host.SetFontSize(Main.DefaultFontSize);
        await KeyEvent(Key.F3);
        Require(session.Prompt?.Title == "Font size", "F3 did not open the font dialog.");
        await Frames();
        var smallDialog = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        var compactScroll = Descendants(smallDialog).OfType<ScrollContainer>().Single();
        float compactContentHeight = Math.Min(compactScroll.GetChild<Control>(0).GetCombinedMinimumSize().Y,
            host.Size.Y - 96);
        Require(smallDialog.Size.Y < largeMenuHeight &&
            Math.Abs(smallDialog.Size.Y - compactContentHeight - 28) < 1 &&
            Math.Abs(smallDialog.GetGlobalRect().GetCenter().Y - host.Size.Y / 2) < 1,
            $"Compact dialog geometry: size={smallDialog.Size}, center={smallDialog.GetGlobalRect().GetCenter()}, window={host.Size}.");
        var value = Descendants(host).OfType<Label>().Single(l => l.Name == "FontDialogValue");
        var fontMinus = value.GetParent().GetChild<Button>(0);
        var fontPlus = value.GetParent().GetChild<Button>(2);
        fontMinus.GrabFocus();
        await KeyEvent(Key.Right);
        Require(host.FontSize == Main.DefaultFontSize && host.GetViewport().GuiGetFocusOwner() == fontPlus,
            "Font arrows must navigate to + without changing size.");
        await KeyEvent(Key.Left);
        Require(host.FontSize == Main.DefaultFontSize && host.GetViewport().GuiGetFocusOwner() == fontMinus,
            "Font arrows must navigate to - without changing size.");
        value.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize + 2, "Larger-font dialog button failed.");
        value.GetParent().GetChild<Button>(0).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize, "Smaller-font dialog button failed.");
        host.SetFontSize(Main.MaxFontSize);
        value.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MaxFontSize, "Font dialog exceeded the maximum size.");
        session.SelectPrompt(1);
        Require(host.FontSize == Main.DefaultFontSize, "RESET did not restore the default font.");
        session.SelectPrompt(0);
        await KeyEvent(Key.Escape);
        int resizeChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "Resize sidebar");
        Require(resizeChoice >= 0, "City menu is missing keyboard sidebar resizing.");
        session.SelectPrompt(resizeChoice);
        await Frames();
        var split = Descendants(host).OfType<CitySplit>().Single();
        float oldSidebar = split.SidebarWidth;
        var sidebarValue = Descendants(host).OfType<Label>().Single(l => l.Name == "SidebarDialogValue");
        var sidebarMinus = sidebarValue.GetParent().GetChild<Button>(0);
        var sidebarPlus = sidebarValue.GetParent().GetChild<Button>(2);
        sidebarMinus.GrabFocus();
        await KeyEvent(Key.Right);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1 &&
            host.GetViewport().GuiGetFocusOwner() == sidebarPlus,
            "Right arrow must focus + without resizing the sidebar.");
        await KeyEvent(Key.Space);
        Require(Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            "Space must resize exactly one character using the focused + button.");
        await KeyEvent(Key.Left);
        Require(host.GetViewport().GuiGetFocusOwner() == sidebarMinus &&
            Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            "Left arrow must focus - without resizing.");
        await KeyEvent(Key.Enter);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1,
            "Enter must activate the focused - button.");
        split.SetSidebarWidth(float.MaxValue);
        await Frames();
        Require(split.SidebarWidth <= CitySplit.MaximumSidebar + 1 &&
            split.GetChild<Control>(0).Size.X >= 320 - 1, "Sidebar exceeded its maximum or map minimum.");
        split.SetSidebarWidth(0);
        await Frames();
        Require(split.SidebarWidth >= CitySplit.MinimumSidebar - 1, "Sidebar fell below its minimum.");
        session.SelectPrompt(0);
        await Frames();
        var dragger = split.GetDragAreaControls()[0];
        var dragPoint = dragger.GetGlobalRect().GetCenter();
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint, GlobalPosition = dragPoint, ButtonIndex = MouseButton.Left, Pressed = true,
        }, true);
        host.GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = dragPoint - new Vector2(24, 0), GlobalPosition = dragPoint - new Vector2(24, 0),
            Relative = new Vector2(-24, 0), ButtonMask = MouseButtonMask.Left,
        }, true);
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint - new Vector2(24, 0), GlobalPosition = dragPoint - new Vector2(24, 0),
            ButtonIndex = MouseButton.Left, Pressed = false,
        }, true);
        await Frames();
        Require(split.SidebarWidth > CitySplit.MinimumSidebar, "Dragging the shared border did not resize the sidebar.");
        split.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        foreach (int fontSize in new[] { Main.DefaultFontSize, Main.MaxFontSize, Main.MinFontSize })
        {
            host.SetFontSize(fontSize);
            await Frames();
            Require(host.GetWindow().ContentScaleFactor == fontSize / 16f,
                "Font setting did not scale the interface and map together.");
            Require(host.Map.Grid.Columns == (int)(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)(host.Map.Size.Y / TerminalGrid.CellHeight),
                "Font resizing left stale map dimensions.");
            Require(host.Map.Grid.TryCell(TerminalGrid.CellWidth * 2, TerminalGrid.CellHeight * 2, out var fontCell) &&
                fontCell == new Pos(2, 2), "Font resizing broke map hit targets.");
            Require(cityBeforeFontChange == SaveGameStore.Serialize(session.Game),
                "Font resizing changed the city.");
        }
        host.SetFontSize(Main.MaxFontSize);
        if (headless)
        {
            window.ContentScaleSize = new Vector2I(640 * Main.MaxFontSize / 16, 480 * Main.MaxFontSize / 16);
        }
        else host.FitWindowToFont();
        await Frames();
        session.ShowSessionMenu();
        await Frames();
        modal = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        Require(modal.Position.X >= 0 && modal.Position.Y >= 0 &&
            modal.Position.X + modal.Size.X <= host.Size.X && modal.Position.Y + modal.Size.Y <= host.Size.Y,
            "Large-font menu extends outside the window.");
        Require(host.Map.Grid.Columns >= 8 && host.Map.Grid.Rows >= 6,
            $"Large text left too little room for the map: {host.Map.Grid.Columns}x{host.Map.Grid.Rows}.");
        if (!headless)
        {
            var usable = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
            Require(window.Position.X >= usable.Position.X && window.Position.Y >= usable.Position.Y &&
                window.Position.X + window.Size.X <= usable.End.X &&
                window.Position.Y + window.Size.Y <= usable.End.Y,
                "Font resizing moved the window outside the usable desktop.");
        }
        await KeyEvent(Key.Escape);
        host.SetFontSize(Main.DefaultFontSize);
        await Frames();
        await VerifyEffects(host, Frames, KeyEvent);
        if (File.Exists(session.SavePath)) File.Delete(session.SavePath);
    }

    /// <summary>Drives the effect pipeline through the real scene: build, demolish, toggle, drain. Draw checks only apply when the engine draws.</summary>
    private static async Task VerifyEffects(Main host, Func<Task> frames, Func<Key, bool, uint, bool, Key, Task> key)
    {
        var session = host.Session;
        var map = session.Game.Map;
        session.ClosePrompt();
        session.SetZoom(0);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.RefreshCells();
        await frames();
        Require(host.Effects.Settings.Level == TermCity.Core.Effects.EffectLevel.High && host.Map.Effects == host.Effects,
            "Effects are not wired into the map at the default level.");
        void Drain()
        {
            for (int i = 0; i < 40 && host.Effects.ActiveOneShots > 0; i++) host._Process(0.25);
        }
        Drain();
        Pos Visible()
        {
            for (int y = 2; y < host.Map.Grid.Rows - 2; y++)
                for (int x = 2; x < host.Map.Grid.Columns - 2; x++)
                {
                    var at = session.ScreenToMap(x, y);
                    if (map.InBounds(at) && session.Game.CanBuildOn(at.X, at.Y)) return at;
                }
            throw new InvalidOperationException("No visible vacant cell for the effects check.");
        }
        var spot = Visible();
        int spawned = host.Director.TotalSpawned;
        Require(session.Game.BuildRoad(new CellRect(spot.X, spot.Y, 1, 1)).Success, "Effects check could not build a road.");
        await frames();
        Require(host.Director.TotalSpawned > spawned, "Building a road did not start an effect.");
        Drain();
        Require(host.Effects.ActiveOneShots == 0 && host.Effects.CellCount == 0, "Road effect left cell modifiers behind.");
        int draws = host.Map.DrawCount;
        Require(session.Game.Demolish(new CellRect(spot.X, spot.Y, 1, 1)).Success, "Effects check could not demolish.");
        host.Map.RefreshCells();
        host._Process(0.05);
        Require(host.Effects.Effects.OfType<TermCity.Core.Effects.DemolishEffect>().Any() && host.Effects.HasVisuals,
            "Demolishing did not start the shrink-away effect.");
        await frames();
        if (host.Map.DrawCount > draws)
        {
            Require(host.Map.EffectGlyphsDrawn > 0, "The map drew a frame without the running effect glyphs.");
            if (host.Map.Grid.VectorRoads && session.Game.Map.RoadCells.Any())
                Require(host.Map.RoadChunksDrawn > 0, "The map drew a frame without the curved roads.");
        }
        else GD.Print("Godot did not draw during the effects check (headless); effect glyph drawing was not exercised.");
        Drain();
        Require(host.Effects.ActiveOneShots == 0 && host.Effects.CellCount == 0, "Demolish effect did not finish and clean up.");
        string saved = SaveGameStore.Serialize(session.Game);
        host._Process(0.1);
        Require(saved == SaveGameStore.Serialize(session.Game), "Effects mutated gameplay.");

        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.Low && host.Effects.Settings.Intensity < 1, "V did not lower effects.");
        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.Off && !host.Effects.Settings.Active, "V did not turn effects off.");
        spot = Visible();
        spawned = host.Director.TotalSpawned;
        session.Game.BuildRoad(new CellRect(spot.X, spot.Y, 1, 1));
        session.Game.Demolish(new CellRect(spot.X, spot.Y, 1, 1));
        await frames();
        Require(host.Director.TotalSpawned == spawned && host.Effects.ActiveEffects == 0 && !host.Effects.HasVisuals,
            "Effects ran while switched off.");
        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.High && host.Effects.Settings.Active, "V did not restore effects.");
        await frames();
        Require(host.Director.TotalSpawned == spawned, "Re-enabling effects replayed old changes.");
        await key(Key.Escape, false, 0, false, Key.None);
        Require(session.Prompt?.Choices.Any(c => c.Label == "Effects") == true,
            "The city menu has no Effects entry.");
        await key(Key.Escape, false, 0, false, Key.None);
        Drain();
    }

    public static void Run(Main host)
    {
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        var session = host.Session;
        var map = session.Game.Map;
        var music = host.GetNode<AudioStreamPlayer>("CityMusic");
        Require(music.Playing && music.Stream is AudioStreamGenerator
            {
                MixRate: GreensleevesTrack.SampleRate,
            } && host.MusicPhraseCount == 1,
            "Greensleeves must stream its fixed opening, not loop a static WAV.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        Require(music.StreamPaused, "Music continued while the game window was unfocused.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        Require(!music.StreamPaused, "Music did not resume when the game regained focus.");
        Require(host.Map.Grid.Columns >= 8 && host.Map.Grid.Rows >= 6,
            "Smoke viewport must be large enough to exercise map input.");
        Require(host.Map.Size.X <= host.Size.X && host.Map.Size.Y <= host.Size.Y,
            "Map layout extends beyond the window.");
        Require(ProjectSettings.GetSetting("rendering/environment/defaults/default_clear_color").AsColor() == Colors.Black,
            "Desktop background is not black.");
        Require(host.Map.GetParent() is TerminalFrame { MouseFilter: Control.MouseFilterEnum.Ignore },
            "Map must use the shared double frame without intercepting input.");
        Require(host.Size == new Vector2(960, 640), "Window resize did not reach the scene.");
        Require(host.Map.Grid.Columns == (int)(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)(host.Map.Size.Y / TerminalGrid.CellHeight),
            "Window resize did not reach the cell grid.");
        Require(Godot.FileAccess.FileExists("res://Assets/DejaVu-LICENSE.txt"),
            "The bundled font license must also be distributed with exported players.");
        GD.Print($"Godot viewport: {host.Map.Grid.Columns}x{host.Map.Grid.Rows} cells");
        var before = session.Cursor;
        host._UnhandledInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        Require(session.Cursor.X == before.X + 1, "Keyboard movement did not reach the session.");

        var vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.B, Pressed = true });
        Require(session.Preview is not null, "Road preview was not created.");
        int zoned = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == zoned, "Preview allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Preview is null && map.HasRoad(vacant.X, vacant.Y), "Road confirmation failed.");

        vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(map.ZoneAt(vacant.X, vacant.Y) == ZoneType.Residential, "Zoning failed.");
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        Require(loaded.Stats == session.Game.Stats, "Save serialization changed city statistics.");

        host._UnhandledInput(new InputEventKey { Keycode = Key.Key3, Pressed = true });
        double days = session.Game.ElapsedDays;
        for (int i = 0; i < 8; i++)
        {
            session.Update(0.25);
        }
        Require(session.Game.ElapsedDays > days, "Simulation did not advance.");
        session.Game.Paused = true;
        Require(session.Autosave(), "Autosave failed.");
        for (int slot = 1; slot <= GameSession.AutosaveSlots; slot++)
        {
            string path = session.AutosavePath(slot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var watch = Stopwatch.StartNew();
        for (int zoom = GameSession.MinZoom; zoom <= GameSession.MaxZoom; zoom++)
        {
            session.SetZoom(zoom);
            session.ScrollCamera(17, 9);
            host.Map.Grid.Fill(session);
            Require(host.Map.Grid.Columns > 0 && host.Map.Grid.Rows > 0, "Viewport grid was empty.");
        }
        GD.Print($"Godot visible-cell rebuilds at all zooms: {watch.Elapsed.TotalMilliseconds:F1} ms");
        session.SetZoom(0);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.Invalidate(true);

        session.ShowPrompt("Smoke prompt", "Dismiss this prompt.", [new("Close", session.ClosePrompt)]);
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Prompt is null, "Prompt dismissal failed.");
        host._Notification(checked((int)Node.NotificationWMCloseRequest));
        Require(session.Prompt is not null, "Window close did not guard unsaved progress.");
        int homes = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == homes, "Quit prompt allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(session.Prompt is null, "Quit prompt cancellation failed.");
        host.Map.RefreshCells();

        var pointer = new Vector2(5 * TerminalGrid.CellWidth, 5 * TerminalGrid.CellHeight);
        var expected = session.ScreenToMap(5, 5);
        session.SelectCell(expected);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
            ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(2 * TerminalGrid.CellWidth, 0),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.Selection?.Width == 3 && session.Selection.Value.Contains(expected),
            "Mouse drag selection did not reach the session.");
        var farCell = session.ScreenToMap(12, 9);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = new Vector2(12 * TerminalGrid.CellWidth, 9 * TerminalGrid.CellHeight),
            ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.Selection is { } grown && grown.Contains(expected) && grown.Contains(farCell),
            "Shift-click did not extend the selection to the clicked cell.");
        session.ClearSelection();
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelDown,
            Pressed = true,
            CtrlPressed = true,
            Position = pointer,
        });
        Require(session.ZoomLevel == -1, "Mouse wheel zoom failed.");
        session.SetZoom(0);
        var center = new Pos(map.Width / 2, map.Height / 2);
        session.PlaceCursor(center);
        session.CenterOn(center);
        host.Map.RefreshCells();
        VerifyAnimation(host);
        VerifySessionWorkflows(host);
    }

    private static void VerifySessionWorkflows(Main host)
    {
        var session = host.Session;
        void Press(Key code, bool ctrl = false) =>
            host._UnhandledInput(new InputEventKey { Keycode = code, Pressed = true, CtrlPressed = ctrl });
        Pos Vacant() => Enumerable.Range(0, session.Game.Map.Width * session.Game.Map.Height)
            .Select(i => session.Game.Map.PosOf(i)).First(p => session.Game.CanBuildOn(p.X, p.Y));

        foreach (var (key, title) in new[]
        {
            (Key.F1, HelpContent.Title), (Key.F7, "Weekly report and milestones"),
            (Key.F8, "Growth and road access"), (Key.Escape, "City menu"),
        })
        {
            Press(key);
            Require(session.Prompt?.Title == title, $"{key} did not open {title}.");
            Press(Key.R);
            if (key != Key.F1) Require(session.Prompt?.Title == title, "Report allowed a background action.");
            if (session.Prompt is not null) Press(Key.Escape);
        }
        Press(Key.F6);
        Require(session.Prompt is { Title: "TermCity guide", Tabs.Count: 7, ActiveTab: 0 }, "F6 did not open the tabbed guide.");
        Press(Key.Right);
        Require(session.Prompt?.ActiveTab == 1 && session.Prompt.Tabs![1].Title == "Zones", "Right arrow did not switch guide tab.");
        Press(Key.Left);
        Press(Key.Left);
        Require(session.Prompt?.ActiveTab == 6, "Left arrow did not wrap to the last guide tab.");
        Press(Key.Escape);
        Require(session.Prompt is null, "Esc did not close the guide.");
        if (session.GuideVisible)
        {
            Press(Key.F6);
            session.SelectPrompt(1);
            Require(!session.GuideVisible && session.Game.GuideDismissed, "Guide tip was not dismissed.");
        }
        Press(Key.F12);
        Require(session.InputDebug, "Input diagnostics were not enabled.");
        Press(Key.F12);
        Press(Key.E);
        Require(session.EdgeScrollEnabled, "Edge scrolling was not enabled.");
        Press(Key.E);

        session.PlaceCursor(Vacant());
        Press(Key.S);
        Press(Key.Right);
        Press(Key.S);
        Require(session.Selection?.Width == 2 && session.Anchor is null, "Keyboard selection mode failed.");
        Press(Key.Escape);
        Require(session.Prompt?.Title == "City menu", "Esc did not open the menu with an active selection.");
        Press(Key.Escape);
        session.ClearSelection();
        session.PlaceCursor(Vacant());
        var start = session.Cursor;
        Press(Key.T);
        Press(Key.Right);
        Require(session.RoadToolActive && session.Preview?.Area.Width == 2, "Road line endpoint was blocked.");
        Press(Key.R);
        Require(session.RoadToolActive, "Road line allowed a background action.");
        Press(Key.Enter);
        Require(session.Game.Map.HasRoad(start.X, start.Y), "Road line confirmation failed.");
        Press(Key.Z, ctrl: true);
        Require(session.Prompt?.Title == "Undo last action", "Undo was not guarded.");
        Press(Key.Enter);
        Require(!session.Game.Map.HasRoad(start.X, start.Y), "Undo did not restore the city.");

        session.PlaceCursor(Vacant());
        Press(Key.R);
        Press(Key.U);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.None, "Dezone failed.");
        Press(Key.R);
        Press(Key.D);
        Require(session.Preview?.Kind == PlacementKind.Demolish, "Demolition preview was not shown.");
        Press(Key.N);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.Residential,
            "Cancelled demolition changed the map.");
        Press(Key.D);
        Press(Key.Y);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.None, "Demolition failed.");

        Press(Key.M);
        Require(session.Prompt?.Title == "Area menu", "Area menu was not opened.");
        session.SelectPrompt(1);
        Require(session.Prompt?.Title == "Roads", "Road menu was not opened.");
        var avenue = session.Game.Map.Content.Roads.Get(DefaultRoads.CobbledName);
        int avenueChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == DefaultRoads.CobbledName && c.Cells?[0] == "Fill area");
        Require(avenueChoice >= 0, "Cobbled Road was missing from the road menu.");
        session.SelectPrompt(avenueChoice);
        Require(session.Preview?.Road == avenue, "Road menu did not preview the selected type.");
        Press(Key.Escape);
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        Require(session.Prompt?.Title == "Service buildings" && (session.Prompt.Text == "Choose a building to preview." ||
            session.Prompt.Text == "No service buildings are registered."), "Building menu was not shown or explained.");
        Press(Key.Escape);

        string quickSave = session.SavePath;
        Require(session.QuickSave(), "Quick-save failed.");
        string saved = SaveGameStore.Serialize(session.Game);
        Press(Key.R);
        Press(Key.F9);
        Require(session.Prompt?.Title == "Load city", "Quick-load discarded unsaved changes.");
        session.SelectPrompt(1);
        Require(saved == SaveGameStore.Serialize(session.Game), "Quick-load changed saved city data.");
        session.ShowLoadMenu();
        Require(session.Prompt?.Choices.Count == 6, "Load menu omitted autosaves or file path.");
        session.SelectPrompt(4);
        Require(session.Prompt?.Input == quickSave, "Load file field did not show its initial path.");
        session.Prompt!.Input = Path.Combine(Path.GetDirectoryName(quickSave)!, "missing.json");
        session.SelectPrompt(0);
        Require(session.MessageKind == MessageKind.Error && session.Prompt?.Title == "Load file",
            "Load failure did not leave a recoverable error.");
        Press(Key.Escape);

        int seed = session.Game.Config.Seed;
        session.RequestNewCity(restart: true);
        Require(session.Game.Config.Seed == seed && session.Game.Paused &&
            session.Prompt is null &&
            session.GuideVisible == (session.Game.Config.Scenario == CityScenario.Random),
            "Restart did not preserve the seed and scenario-appropriate guide behavior.");
        if (session.Prompt is not null) Press(Key.Escape);
        session.DismissGuide();
        session.CenterOn(new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2));
        host.Map.RefreshCells();
        if (File.Exists(quickSave)) File.Delete(quickSave);
    }

    private static void VerifyAnimation(Main host)
    {
        var session = host.Session;
        var grid = host.Map.Grid;
        var map = session.Game.Map;
        session.Game.Paused = true;
        foreach (var kind in new[] { TerminalGrid.AnimationKind.Hill, TerminalGrid.AnimationKind.Tree, TerminalGrid.AnimationKind.Water })
        {
            if (kind == TerminalGrid.AnimationKind.Hill &&
                !Enumerable.Range(0, map.Width * map.Height)
                    .Any(i => map.TerrainAt(i % map.Width, i / map.Width).Name == "Hill")) continue;
            var position = Enumerable.Range(0, map.Width * map.Height)
                .Select(i => new Pos(i % map.Width, i / map.Width))
                .First(p => TerminalGrid.ShouldAnimate(p) &&
                    (kind != TerminalGrid.AnimationKind.Tree
                        ? map.Content.Terrains.Get(kind.ToString()).Glyphs.Contains(
                            TermCity.Core.Rendering.CellRenderer.Render(session.Game, p.X, p.Y).Glyph)
                        : map.Content.Features.Get("Tree").Glyphs.Contains(
                            TermCity.Core.Rendering.CellRenderer.Render(session.Game, p.X, p.Y).Glyph)));
            session.CenterOn(position);
            host.Map.RefreshCells();
            var screen = Enumerable.Range(0, grid.Columns * grid.Rows)
                .Select(i => new Pos(i % grid.Columns, i / grid.Columns))
                .First(p => session.ScreenToMap(p.X, p.Y) == position);
            Require(grid.AnimationAt(screen.X, screen.Y) == kind, $"{kind} was not marked for animation.");
            var before = grid.OffsetAt(session, screen.X, screen.Y);
            int rebuilds = grid.Rebuilds;
            string saved = SaveGameStore.Serialize(session.Game);
            host._Process(TerminalGrid.BeatSeconds);
            Require(before != grid.OffsetAt(session, screen.X, screen.Y), $"Paused {kind} did not animate.");
            Require(rebuilds == grid.Rebuilds, "Animation rebuilt base cells.");
            Require(saved == SaveGameStore.Serialize(session.Game), "Animation mutated gameplay.");
            var offset = grid.OffsetAt(session, screen.X, screen.Y);
            Require(kind == TerminalGrid.AnimationKind.Hill ? offset.X == 0 : offset.Y == 0,
                $"{kind} moved on the wrong axis.");
        }
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        double seconds = grid.AnimationSeconds;
        host._Process(TerminalGrid.BeatSeconds);
        Require(seconds == grid.AnimationSeconds, "Unfocused animation continued.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.RefreshCells();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
