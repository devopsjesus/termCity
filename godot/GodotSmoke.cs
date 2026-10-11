using TermCity.Core.Roads;
using TermCity.Core.Buildings;
using TermCity.Core.Effects;
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
        Require(host.Theme.GetStylebox("panel", "TooltipPanel") is StyleBoxTexture,
            "Hover tooltips must use the shared double-white terminal style.");
        var tooltipStyle = (StyleBoxTexture)host.Theme.GetStylebox("panel", "TooltipPanel");
        using (var tooltipImage = tooltipStyle.Texture.GetImage())
        {
            Require(tooltipImage.GetPixel(0, 16) == Colors.White &&
                tooltipImage.GetPixel(4, 16) == Colors.White &&
                tooltipImage.GetPixel(2, 16) == Colors.Black &&
                tooltipImage.GetPixel(16, 16) == Colors.Black &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side =>
                    tooltipStyle.GetContentMargin(side) == TerminalFrame.Inset &&
                    tooltipStyle.GetTextureMargin(side) == TerminalFrame.Inset),
                "Tooltip borders must be white, separated by black, with the shared content inset.");
        }
        async Task Frames()
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        async Task KeyEvent(Key code, bool ctrl = false, uint unicode = 0, bool meta = false, Key physical = Key.None,
            bool shift = false)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = physical,
                Pressed = true, CtrlPressed = ctrl, MetaPressed = meta, Unicode = unicode, ShiftPressed = shift });
            Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = physical,
                Pressed = false, CtrlPressed = ctrl, MetaPressed = meta, Unicode = unicode, ShiftPressed = shift });
            await Frames();
        }
        await Frames();
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        await VerifyConstructionCatalog(host, Frames, KeyEvent);
        var treasuryLink = Descendants(host).OfType<Label>().Single(label => label.Name == "Budget");
        var populationLink = Descendants(host).OfType<Label>().Single(label => label.Name == "Population");
        foreach (var fraction in new[] { 0.05f, 0.5f, 0.95f })
        {
            float textWidth = Math.Min(treasuryLink.Size.X,
                treasuryLink.GetThemeFont("font").GetStringSize(treasuryLink.Text,
                    fontSize: treasuryLink.GetThemeFontSize("font_size")).X);
            var click = treasuryLink.GetGlobalRect().Position +
                new Vector2(treasuryLink.Size.X - textWidth + textWidth * fraction, treasuryLink.Size.Y / 2);
            foreach (bool pressed in new[] { true, false })
                host.GetViewport().PushInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left, Pressed = pressed, Position = click, GlobalPosition = click,
                }, true);
            await Frames();
            Require(session.Prompt?.Title == (session.Game.Config.FullRules ? "Treasury, tithes and loans" : "Treasury"),
                "Clicking gold text must open the economy menu.");
            populationLink.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(session.Prompt?.Title != "City menu", "Header links must not replace an active dialog.");
            await KeyEvent(Key.Escape);
        }
        float emptyGoldWidth = treasuryLink.Size.X - treasuryLink.GetThemeFont("font").GetStringSize(
            treasuryLink.Text, fontSize: treasuryLink.GetThemeFontSize("font_size")).X;
        Require(emptyGoldWidth > 0, "Smoke must exercise empty space beside gold text.");
        treasuryLink.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(emptyGoldWidth / 2, treasuryLink.Size.Y / 2),
        });
        Require(session.Prompt is null, "Empty space in the gold label must not open the economy dialog.");
        var populationClick = populationLink.GetGlobalRect().GetCenter();
        var cityRows = Descendants(host).OfType<GridContainer>().Single(table => table.Name == "CityTable");
        int sidebarCellCount = cityRows.GetChildCount();
        for (int cellIndex = 0; cellIndex < sidebarCellCount; cellIndex++)
        {
            cityRows = Descendants(host).OfType<GridContainer>().Single(table => table.Name == "CityTable");
            var target = cityRows.GetChild<PanelContainer>(cellIndex);
            var metric = cityRows.GetChild<PanelContainer>(cellIndex - cellIndex % 2).GetChild<Label>(0).Text;
            if (string.IsNullOrEmpty(metric)) continue;
            var clickedControl = cellIndex % 2 == 0 ? (Control)target.GetChild<Label>(0) : target;
            clickedControl.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(session.Prompt?.Title == "Weekly Report" &&
                session.Prompt.ActiveTab == SidebarReport.ReportTabFor(metric),
                $"Clicking sidebar metric {metric} must open the matching report section.");
            var openedReport = session.Prompt;
            clickedControl.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(ReferenceEquals(session.Prompt, openedReport),
                "Sidebar clicks must not replace an already open report.");
            await KeyEvent(Key.Escape);
        }
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = pressed, Position = populationClick,
                GlobalPosition = populationClick,
            }, true);
        await Frames();
        Require(session.Prompt is null, "Clicking population must not open a menu.");
        session.ShowSessionMenu();
        Require(session.Prompt!.Choices.Any(choice => choice.Label == "Report") &&
            session.Prompt.Choices.Any(choice => choice.Label == "Growth") &&
            session.Prompt.Choices.Any(choice => choice.Label == "Health") &&
            !session.Prompt.Choices.Any(choice => choice.Label == "Budget"),
            "City menu must contain city information and reports, with economy in the treasury menu.");
        await KeyEvent(Key.Escape);
        Require(host.MusicVolume == Main.DefaultMusicVolume && host.SoundVolume == Main.DefaultSoundVolume &&
            host.MusicVolume < 100 && host.SoundVolume < 100, "Default audio levels were not reduced.");
        Require(!host.CelebrationsEnabled && !host.Effects.Settings.Celebrations,
            "Celebration bursts must be off by default.");
        int skips = host.MusicSkips;
        // Longer than the entire buffer: the render loop cannot help during this stall.
        Thread.Sleep(1500);
        Require(host.MusicSkips == skips, "Music starved while the main thread was stalled.");
        Require(host.GetNode<AudioStreamPlayer>("CityMusic").Playing,
            "Music player stopped while the main thread was stalled.");

            Require(!Descendants(host).OfType<Button>().Any(b =>
                b.Text.StartsWith("F1 ", StringComparison.Ordinal) || b.Text.StartsWith("F3 ", StringComparison.Ordinal) ||
                b.Text.StartsWith("F5 ", StringComparison.Ordinal) || b.Text.StartsWith("F6 ", StringComparison.Ordinal) ||
                b.Text.StartsWith("F7 ", StringComparison.Ordinal) || b.Text.StartsWith("F8 ", StringComparison.Ordinal) ||
                b.Text.StartsWith("F9 ", StringComparison.Ordinal) || b.Text == "ESC GAME"),
                "The keyboard shortcut toolbar must not be displayed.");
            Require(!Descendants(host).OfType<Label>().Any(l => l.Text.StartsWith("Arrows: move", StringComparison.Ordinal)),
                "Header still contains the movement guidance row.");
            var zoomIndicator = Descendants(host).OfType<Label>().Single(l => l.Name == "ZoomIndicator");
            Require(zoomIndicator.Text == session.ZoomLabel &&
                zoomIndicator.GetParent().GetChild<Button>(0).Text == "[-]" &&
                zoomIndicator.GetParent().GetChild<Button>(2).Text == "[+]",
                "Minimap zoom indicator must be flanked by its buttons.");
        var headerLabels = Descendants(host).OfType<Label>()
            .Where(l => l.Name == "AppTitle" || l.Name == "CityNameDisplay" || l.Name == "Calendar" ||
                l.Name == "WeekProgress" || l.Name == "Population" || l.Name == "ViewsHeader" || l.Name == "GuideHeader" || l.Name == "ReportsHeader" || l.Name == "Budget")
            .ToArray();
        float headerCenter = headerLabels[0].GetGlobalRect().GetCenter().Y;
        Require(headerLabels.All(l => Math.Abs(l.GetGlobalRect().GetCenter().Y - headerCenter) < 1),
            "Top header text must share one horizontal row.");
        foreach (var label in headerLabels)
        {
            Node? ancestor = label.GetParent();
            while (ancestor is not null && ancestor is not TerminalFrame) ancestor = ancestor.GetParent();
            var frame = ancestor as TerminalFrame ??
                throw new InvalidOperationException("Top-bar label is missing its terminal frame.");
            float topPadding = label.GlobalPosition.Y - frame.GlobalPosition.Y - frame.InnerBorderBounds.Position.Y;
            float bottomPadding = frame.GlobalPosition.Y + frame.InnerBorderBounds.End.Y - label.GetGlobalRect().End.Y;
            Require(label.VerticalAlignment == VerticalAlignment.Center &&
                Math.Abs(topPadding - bottomPadding) < 1 &&
                frame.VisibleVerticalContentPadding == 4,
                $"{label.Name} must be centered between visible inner borders: top={topPadding}, bottom={bottomPadding}.");
        }
        Require(headerLabels.Select(l => l.GetThemeFontSize("font_size")).Distinct().Count() == 1,
            "All top header text must have the same font size.");
        Require(headerLabels.Single(l => l.Name == "Calendar").Text ==
            MedievalCalendar.DateLabel(session.Game.Year, session.Game.WeekOfYear, session.Game.Config.WeeksPerYear) &&
            Descendants(host).OfType<CalendarIcon>().Single().Feast ==
                MedievalCalendar.Feast(session.Game.WeekOfYear, session.Game.Config.WeeksPerYear),
            "Time header must use ordinal weeks or the corresponding feast name and icon.");
        var stableHeader = headerLabels.Select(label => (Label: label, Text: label.Text,
            Minimum: label.CustomMinimumSize, Size: label.Size,
            Position: label.GlobalPosition, FontSize: label.GetThemeFontSize("font_size"))).ToArray();
        var dateHeader = headerLabels.Single(label => label.Name == "Calendar");
        var populationHeader = headerLabels.Single(label => label.Name == "Population");
        var budgetHeader = headerLabels.Single(label => label.Name == "Budget");
        foreach (int week in new[] { 1, 2, 17, 25, 44, 52 })
        {
            dateHeader.Text = MedievalCalendar.DateLabel(1289, week);
            headerLabels.Single(label => label.Name == "WeekProgress").Text = week % 2 == 0
                ? "[PAUSED]" : Fmt.WeekBar(0, session.Game.Config.DaysPerWeek);
            populationHeader.Text = week % 2 == 0 ? "POP 9,999,999" : "POP 0";
            budgetHeader.Text = week % 2 == 0 ? "-9,999,999g [-999,999g] | COFFERS EMPTY" : "0g [0g]";
            host.FitHeaderFonts();
            Require(stableHeader.All(slot => slot.Label.CustomMinimumSize == slot.Minimum &&
                slot.Label.GetThemeFontSize("font_size") == slot.FontSize),
                "Changing weeks, feasts, clock state, population or money must not change header slots or font sizing.");
            await Frames();
            Require(stableHeader.All(slot => slot.Label.Size.IsEqualApprox(slot.Size) &&
                slot.Label.GlobalPosition.IsEqualApprox(slot.Position)),
                "Header text must not shift when displayed values change.");
        }
        foreach (var slot in stableHeader) slot.Label.Text = slot.Text;
        host.FitHeaderFonts();
        await Frames();
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
            Require(demandHeading.GetThemeFontSize("font_size") >= 13 &&
                demandHeading.GetThemeColor("font_color") == new Color("#70b7ff"),
                "Sidebar headings must be larger and blue.");
            Require(!Descendants(host).Any(n => n.Name == "CollapseCaret") &&
                demandHeading.GetParent().GetParent().GetChild<Control>(1).Visible,
                "Sidebar sections must remain open without collapse carets.");
            Require(Descendants(host).OfType<Label>().Any(l => l.Name == "AppTitle" && l.Text == "TermCity"),
                "Left header must retain the TermCity title.");
            var calendarLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "WeekProgress");
            Require(calendarLabel.Text == "[PAUSED]" &&
                calendarLabel.GetThemeColor("font_color") == new Color("#ffe066"),
                "PAUSED must replace the week progression bar in yellow.");
            Require(calendarLabel.HorizontalAlignment == HorizontalAlignment.Center &&
                Descendants(host).OfType<Label>().Single(l => l.Name == "Calendar").HorizontalAlignment == HorizontalAlignment.Left &&
                Descendants(host).OfType<Label>().Single(l => l.Name == "Population").HorizontalAlignment == HorizontalAlignment.Left,
                "Calendar, week bar and population alignment is incorrect.");
            Require(!Descendants(host).Any(node => node.Name == "ClockStatus"),
                "The top header must not show a speed indicator.");
            Require(populationHeader.GetParent() is TerminalFrame { Name: var popFrame } &&
                popFrame == "PopulationHeaderFrame" &&
                budgetHeader.GetParent() is TerminalFrame { Name: var fundsFrame } &&
                fundsFrame == "FundsHeaderFrame" && populationHeader.GetParent() != budgetHeader.GetParent() &&
                !budgetHeader.Text.Contains("TREASURY", StringComparison.Ordinal),
                "POP and funds must use separate frames and omit the TREASURY label.");
            populationHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(session.Prompt is null && populationHeader.MouseDefaultCursorShape != Control.CursorShape.PointingHand &&
                !populationHeader.TooltipText.Contains("Open city menu", StringComparison.Ordinal),
                "The POP header must be display-only and not open the city menu.");
            Require(dateHeader.GetParent<HBoxContainer>().GetThemeConstant("separation") == 12,
                "The time section must have increased spacing between its fields.");
            if (session.Game.Config.FullRules)
                Require(budgetHeader.Text.Contains(
                    $" [{(session.Game.Finance.Net > 0 ? "+" : "")}{Fmt.CompactMoney(session.Game.Finance.Net)}]",
                    StringComparison.Ordinal) && !budgetHeader.Text.Contains("/wk", StringComparison.Ordinal),
                    "Weekly gold must use brackets, omit the weekly suffix and show no plus for zero.");
            var reportsHeader = headerLabels.Single(label => label.Name == "ReportsHeader");
            var viewsHeader = headerLabels.Single(label => label.Name == "ViewsHeader");
            viewsHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            var viewsBar = Descendants(host).OfType<TerminalFrame>().Single(frame => frame.Name == "ViewsBar");
            Require(viewsBar.IsVisibleInTree() && !viewsBar.JoinAdjacent &&
                viewsBar.GlobalPosition.IsEqualApprox(host.Map.GlobalPosition) &&
                host.Map.GetGlobalRect().Encloses(viewsBar.GetGlobalRect()) &&
                viewsBar.GetChild<ScrollContainer>(0).GetChild(0) is HBoxContainer,
                "VIEWS must open a horizontal terminal icon bar at the map's top left.");
            foreach (var overlay in Enum.GetValues<MapOverlay>())
            {
                var viewButton = Descendants(viewsBar).OfType<Button>().Single(button => button.Name == $"View{overlay}");
                Require(viewButton.Text.Length == 0 &&
                    viewButton.GetChild<ConstructionIcon>(0) is { Overlay: var renderedOverlay } &&
                    renderedOverlay == overlay,
                    "Every VIEWS option must use rendered artwork for its specific overlay, not a text glyph.");
                viewButton.EmitSignal(Button.SignalName.Pressed);
                await Frames();
                Require(session.Overlay == overlay && viewsBar.IsVisibleInTree(),
                    "Clicking each view icon must select the overlay without closing the bar.");
                Require(host.Map.Grid.VectorRoads,
                    "Selecting an overlay must retain rendered roads instead of terminal road glyphs.");
            }
            await KeyEvent(Key.Escape);
            Require(!viewsBar.IsVisibleInTree() && session.Prompt is null &&
                session.Overlay == MapOverlay.Happiness,
                "Esc must close VIEWS without clearing its overlay or opening Game Menu.");
            viewsHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            await Frames();
            host.GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left,
                Pressed = true, Position = host.Size - Vector2.One, GlobalPosition = host.Size - Vector2.One }, true);
            host.GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left,
                Pressed = false, Position = host.Size - Vector2.One, GlobalPosition = host.Size - Vector2.One }, true);
            await Frames();
            Require(viewsBar.IsVisibleInTree() && session.Overlay == MapOverlay.Happiness && session.Prompt is null,
                "Clicking away must keep VIEWS active.");
            viewsHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(!viewsBar.IsVisibleInTree() && session.Overlay == MapOverlay.Happiness,
                "Toggling VIEWS must close its bar without clearing the overlay.");
            session.SetOverlay(MapOverlay.Off);
            await KeyEvent(Key.O, shift: true);
            Require(session.Overlay == MapOverlay.Happiness,
                "Shift+O must restore the most recently selected overlay.");
            await KeyEvent(Key.O, shift: true);
            Require(session.Overlay == MapOverlay.Off, "Shift+O must switch overlays off.");
            var guideHeader = headerLabels.Single(label => label.Name == "GuideHeader");
            Require(guideHeader.Text == "GUIDE" && guideHeader.GetParent() is TerminalFrame { Name: var guideFrameName } &&
                guideFrameName == "GuideHeaderFrame", "GUIDE must use a separate terminal header frame.");
            guideHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(session.Prompt is { Title: "TermCity guide", Tabs.Count: 9 },
                "Clicking GUIDE must open the complete guide.");
            string beforeGuideDismissal = SaveGameStore.Serialize(session.Game);
            var guideDialog = Descendants(host).OfType<TerminalFrame>()
                .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
            var outsideGuide = Vector2.One;
            Require(!guideDialog.GetGlobalRect().HasPoint(outsideGuide),
                "Guide dismissal test must click outside its frame.");
            foreach (bool pressed in new[] { true, false })
                host.GetViewport().PushInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left, Pressed = pressed,
                    Position = outsideGuide, GlobalPosition = outsideGuide,
                }, true);
            await Frames();
            Require(session.Prompt is null && beforeGuideDismissal == SaveGameStore.Serialize(session.Game),
                "Clicking outside Guide must close it without modifying the city.");
            Require(reportsHeader.Text == "REPORTS" &&
                reportsHeader.GetParent() is TerminalFrame { Name: var reportsFrameName } &&
                reportsFrameName == "ReportsHeaderFrame" &&
                reportsHeader.GetParent().GetIndex() == populationHeader.GetParent().GetIndex() + 2 &&
                guideHeader.GetParent().GetIndex() == reportsHeader.GetParent().GetIndex() + 1 &&
                budgetHeader.GetParent().GetIndex() == guideHeader.GetParent().GetIndex() + 1,
                "GUIDE and REPORTS must have separate frames between population and funds.");
            reportsHeader.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(session.Prompt?.Title == "Weekly Report",
                "Clicking REPORTS must open the weekly report.");
            var reportDialog = Descendants(host).OfType<TerminalFrame>()
                .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
            float reportWidth = Math.Min(720, host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
            Require(Math.Abs(reportDialog.Size.X - reportWidth) < 1,
                $"Reports must use a wider, screen-clamped layout: {reportDialog.Size.X} vs {reportWidth}.");
            Require(!reportDialog.JoinAdjacent &&
                reportDialog.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var reportBackground } &&
                reportBackground == Colors.Black &&
                Descendants(reportDialog).OfType<MarginContainer>().Single(m => m.Name == "DialogContentMargin")
                    .GetThemeConstant("margin_left") == 2,
                "Reports must use shared terminal frames and compact equal padding.");
            VerifyDialogButtonBorders(reportDialog);
            VerifyMapCenteredDialog(host, reportDialog);
            var reportScroll = Descendants(reportDialog).OfType<ScrollContainer>()
                .Single(scroll => scroll.IsVisibleInTree());
            var scrollbar = reportScroll.GetVScrollBar();
            foreach (string scrollbarState in new[] { "scroll", "scroll_focus", "grabber", "grabber_highlight", "grabber_pressed" })
                Require(scrollbar.GetThemeStylebox(scrollbarState) is StyleBoxFlat scrollbarStyle &&
                    scrollbarStyle.CornerRadiusTopLeft == 0 && scrollbarStyle.CornerRadiusTopRight == 0 &&
                    scrollbarStyle.CornerRadiusBottomLeft == 0 && scrollbarStyle.CornerRadiusBottomRight == 0 &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                        .All(side => scrollbarStyle.GetBorderWidth(side) == 1),
                    "Report scrollbars must use square terminal tracks and block thumbs in all states.");
            Require(scrollbar.GetThemeStylebox("grabber_highlight") is StyleBoxFlat { BgColor: var thumbHighlight } &&
                thumbHighlight == Colors.White, "Terminal scrollbar hover must visibly highlight its thumb.");
            reportScroll.ScrollVertical = 80;
            await Frames();
            Require(scrollbar.Visible ? reportScroll.ScrollVertical > 0 : reportScroll.ScrollVertical == 0,
                "Report scrolling must follow whether the current tab overflows.");
            reportScroll.ScrollVertical = 0;
            await Frames();
            var reportTables = Descendants(reportDialog).OfType<GridContainer>()
                .Where(table => table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)).ToArray();
            var reportSections = ReportTables.Build(session);
            var sizeProgress = Descendants(reportDialog).OfType<ProgressBar>().Single(bar => bar.Name == "CitySizeProgress");
            Require(Math.Abs(sizeProgress.Value - CityProgression.SizeProgress(session.Game).Fraction) < 1e-6 &&
                !sizeProgress.ShowPercentage && reportSections.Count == 1 &&
                !reportSections.SelectMany(section => section.Rows).Any(row => row[0].Text == "Unlocks"),
                "Weekly Report must retain city size progress but move milestone unlocks into the guide.");
            var reportHeaders = Descendants(reportDialog).OfType<TerminalFrame>()
                .Where(frame => frame.Name.ToString().StartsWith("ReportSectionHeader", StringComparison.Ordinal)).ToArray();
            Require(reportHeaders.Length == reportSections.Count &&
                reportHeaders.Select((frame, index) => !frame.JoinAdjacent &&
                    frame.GetChild<Label>(0).Text == reportSections[index].Title &&
                    frame.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var background } &&
                    background == Colors.Black).All(valid => valid),
                "Every report subheader must use the shared closed double-white terminal frame.");
            Require(reportTables.Length == reportSections.Count,
                "Reports must render every summary, zone and milestone as a table.");
            for (int section = 0; section < reportTables.Length; section++)
            {
                var table = reportTables[section];
                var values = reportSections[section].Rows.SelectMany(row => row).ToArray();
                Require(table.Columns == 2 && table.GetChildCount() == values.Length &&
                    table.GetThemeConstant("h_separation") == 0 && table.GetThemeConstant("v_separation") == 0,
                    "Report label/value cells must form a connected terminal grid.");
                for (int cellIndex = 0; cellIndex < values.Length; cellIndex++)
                {
                    var cell = table.GetChild<PanelContainer>(cellIndex);
                    var label = cell.GetChild<Label>(0);
                    if (cellIndex % 2 == 0)
                        Require(label.GetThemeFont("font").GetStringSize(label.Text,
                            fontSize: label.GetThemeFontSize("font_size")).X <= label.Size.X + 0.01f,
                            $"Report label '{label.Text}' must fit on one line.");
                    Require(label.Text == values[cellIndex].Text &&
                        label.GetThemeColor("font_color") == new Color(values[cellIndex].Color.R / 255f,
                            values[cellIndex].Color.G / 255f, values[cellIndex].Color.B / 255f) &&
                        cell.GetThemeStylebox("panel") is StyleBoxFlat style && style.BorderColor == Colors.White &&
                        style.BorderWidthLeft == (cellIndex % 2 == 0 ? 1 : 0) &&
                        style.BorderWidthTop == (cellIndex < 2 ? 1 : 0) &&
                        style.BorderWidthRight == 1 && style.BorderWidthBottom == 1,
                        "Reports must preserve their data/colors and share City sidebar grid styling.");
                }
            }
            Require(Descendants(reportDialog).OfType<Button>().Single(button => button is MnemonicButton)
                .GetParent() is HBoxContainer { Alignment: BoxContainer.AlignmentMode.End },
                "Reports Close action must sit at the bottom right.");
            Require(session.Prompt!.Tabs!.Select(tab => tab.Title).SequenceEqual(
                new[] { "Overview", "Population", "Economy", "Services", "Growth", "Food & Seasons", "Events" }),
                "Weekly Report must expose its seven reporting tabs.");
            for (int tab = 1; tab < 7; tab++)
            {
                await KeyEvent(Key.Right);
                Require(session.Prompt?.ActiveTab == tab && session.Prompt.Title == "Weekly Report",
                    "Arrow keys must select each report tab without changing the city.");
                var tables = Descendants(reportDialog).OfType<GridContainer>()
                    .Where(table => table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)).ToArray();
                var groups = ReportTables.Build(session);
                Require(tables.Length == groups.Count, "Every report tab must render its own table groups.");
                for (int group = 0; group < groups.Count; group++)
                    Require(Descendants(tables[group]).OfType<Label>().Select(label => label.Text)
                        .SequenceEqual(groups[group].Rows.SelectMany(row => row).Select(cell => cell.Text)),
                        "Report tab tables must display their actual data.");
                Require(!Descendants(reportDialog).OfType<ProgressBar>().Any(bar => bar.Name == "CitySizeProgress"),
                    "City-size progress belongs only on the Overview tab.");
                VerifyMapCenteredDialog(host, reportDialog);
                VerifyDialogButtonBorders(reportDialog);
                if (tab == 3 && session.Game.Config.FullRules)
                {
                    var serviceScroll = Descendants(reportDialog).OfType<ScrollContainer>()
                        .Single(scroll => scroll.IsVisibleInTree());
                    serviceScroll.ScrollVertical = 80;
                    await Frames();
                    Require(serviceScroll.ScrollVertical > 0, "The detailed Services report must remain scrollable.");
                }
            }
            await KeyEvent(Key.Right);
            Require(session.Prompt?.ActiveTab == 0, "Report tabs must wrap back to Overview.");
            await KeyEvent(Key.Escape);
            Require(!Descendants(host).OfType<PauseDialog>().Any() &&
                !Descendants(host).OfType<PauseStage>().Any(),
                "Paused gameplay must not create an overlay or animation.");
            var pauseHeaderBounds = headerLabels.Select(label => label.GetGlobalRect()).ToArray();
            string beforePause = SaveGameStore.Serialize(session.Game);
            int beforePauseRebuilds = host.Map.Grid.Rebuilds;
            host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
            host._Process(0.5);
            Require(beforePause == SaveGameStore.Serialize(session.Game) && beforePauseRebuilds == host.Map.Grid.Rebuilds,
                "Paused updates must not advance gameplay or rebuild the map.");
            host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
            host._Process(1);
            Require(calendarLabel.Text == "[PAUSED]", "Focus changes must retain the paused header.");
            host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
            await KeyEvent(Key.Space);
            Require(!session.Game.Paused &&
                calendarLabel.Text == Fmt.WeekBar(session.Game.Day, session.Game.Config.DaysPerWeek) &&
                calendarLabel.GetThemeColor("font_color") == new Color("#70d7ff"),
                "Space must resume and restore the week progression bar.");
            Require(headerLabels.Select(label => label.GetGlobalRect()).SequenceEqual(pauseHeaderBounds),
                "Pause/resume must not shift the header layout.");
            foreach (var dialogKey in new[] { Key.F1, Key.F3, Key.F6, Key.F7, Key.F8, Key.Escape })
            {
                await KeyEvent(dialogKey);
                Require(session.Prompt is not null && !session.Game.Paused &&
                    calendarLabel.Text == "[PAUSED]" && calendarLabel.GetThemeColor("font_color") == new Color("#ffe066"),
                    $"{dialogKey} must display the paused indicator while its dialog suspends a running game.");
                double suspendedDay = session.Game.ElapsedDays;
                host._Process(0.25);
                Require(session.Game.ElapsedDays == suspendedDay, "Dialog must not advance the calendar.");
                var pausedPrompt = session.Prompt;
                await KeyEvent(Key.Space);
                Require(!session.ClockSuspended && ReferenceEquals(session.Prompt, pausedPrompt) &&
                    calendarLabel.Text != "[PAUSED]",
                    "Space must explicitly resume time without closing the current dialog.");
                await KeyEvent(Key.Space);
                Require(session.ClockSuspended && session.Game.Paused && ReferenceEquals(session.Prompt, pausedPrompt),
                    "Space must pause from a dialog without activating its focused action.");
                session.ClosePrompt();
                session.TogglePause();
                await Frames();
                Require(!session.Game.Paused &&
                    calendarLabel.Text == Fmt.WeekBar(session.Game.Day, session.Game.Config.DaysPerWeek),
                    "Closing a dialog must restore the running progression indicator.");
            }
            session.PreviewRoad();
            await Frames();
            Require(calendarLabel.Text == "[PAUSED]" && !session.Game.Paused,
                "Placement previews must also show the suspended-clock indicator.");
            await KeyEvent(Key.P);
            Require(!session.ClockSuspended && session.Preview is not null,
                "P must resume while preserving a placement preview.");
            await KeyEvent(Key.P);
            Require(session.ClockSuspended && session.Preview is not null,
                "P must pause again without cancelling placement.");
            session.CancelPreview();
            session.TogglePause();
            await Frames();
            await KeyEvent(Key.P);
            Require(session.Game.Paused && calendarLabel.Text == "[PAUSED]",
                "P must pause without obstructing gameplay.");
            await KeyEvent(Key.Escape);
            Require(session.Game.Paused && session.Prompt?.Title == "GAME MENU",
                "Escape while paused must open Game Menu without unpausing.");
            await KeyEvent(Key.Escape);
            session.ShowSessionMenu();
            await Frames();
            Require(calendarLabel.Text == "[PAUSED]", "Other menus must retain the paused header.");
            host._Process(0.5);
            await KeyEvent(Key.Escape);
            Require(session.Game.Paused && session.Prompt is null &&
                !Descendants(host).OfType<PauseDialog>().Any(),
                "Closing a menu while paused must leave the map unobstructed.");
            session.ShowMilestone(0);
            await Frames();
            var growthDialog = Descendants(host).OfType<TerminalFrame>()
                .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
            float growthWidth = Math.Min(720,
                host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
            Require(Math.Abs(growthDialog.Size.X - growthWidth) < 1,
                "City Grew! must have a wider, screen-clamped layout.");
            VerifyMapCenteredDialog(host, growthDialog);
            VerifyDialogButtonBorders(growthDialog);
            var growthTitle = Descendants(growthDialog).OfType<Label>()
                .Single(label => label.Text == "Your City Grew!");
            Require(growthTitle.HorizontalAlignment == HorizontalAlignment.Center &&
                growthTitle.Modulate == new Color("#ffe066"),
                "Growth title must be centered and yellow.");
            Require(Descendants(growthDialog).OfType<TerminalFrame>().Single(frame => frame.Name == "ReportSectionHeader0")
                .GetChild<Label>(0).Modulate == new Color("#88ee99") &&
                Descendants(growthDialog).OfType<Label>().Single(label => label.Name == "NewServicesHeader").Text == "New Services",
                "New city size must be green, with a New Services header before unlocked buildings.");
            var citySizeHeading = Descendants(growthDialog).OfType<TerminalFrame>()
                .Single(frame => frame.Name == "ReportSectionHeader0").GetChild<Label>(0);
            var newServicesHeading = Descendants(growthDialog).OfType<Label>().Single(label => label.Name == "NewServicesHeader");
            Require(citySizeHeading.HorizontalAlignment == HorizontalAlignment.Center &&
                citySizeHeading.GetThemeFontSize("font_size") == 24 &&
                newServicesHeading.GetThemeFontSize("font_size") == 24,
                "City size and New Services must use matching enlarged 24-point headings.");
            var unlockedBuildings = session.Game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
                building.MinPopulation > 0 && CityProgression.RequiredPopulation(building) == 100).ToArray();
            var milestoneArt = Descendants(growthDialog).OfType<Label>().Where(label => label.Name == "MilestoneBuildingArt").ToArray();
            Require(milestoneArt.Length == unlockedBuildings.Length,
                "Every newly unlocked building must show its artwork.");
            foreach (var building in unlockedBuildings)
            {
                var art = milestoneArt.Single(label => label.TooltipText == building.Name);
                Require(art.Text == (building.FootprintArt.Count > 0
                        ? string.Join("\n", building.FootprintArt) : building.Glyphs[0]) &&
                    art.GetParent().GetParent<HBoxContainer>().GetChild<Control>(0).GetGlobalRect().End.X <= art.GlobalPosition.X,
                    "Building artwork must match its footprint or icon and sit to the right of its feature table.");
            }
            var growthScroll = Descendants(growthDialog).OfType<ScrollContainer>().Single(control => control.IsVisibleInTree());
            Require(growthDialog.ClipContents && growthScroll.ClipContents,
                "Growth dialog and scrolling viewport must clip overflowing frames and text.");
            foreach (string scrollbarState in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
                Require(growthScroll.GetVScrollBar().GetThemeStylebox(scrollbarState) is StyleBoxFlat barStyle &&
                    barStyle.CornerRadiusTopLeft == 0 && barStyle.CornerRadiusTopRight == 0 &&
                    barStyle.BorderWidthLeft == 1 && barStyle.BorderWidthRight == 1,
                    "Growth scrollbar must use the shared square terminal track and thumb styling.");
            Require(session.Prompt!.Title == "Your City Grew!" && session.Game.Paused &&
                Descendants(growthDialog).OfType<GridContainer>().Count() == MilestoneReport.Build(session.Game, 0).Groups.Count,
                "Milestone dialog must pause and render all newly unlocked building feature tables.");
            var previousMilestone = Descendants(growthDialog).OfType<Button>().Single(button => button.Text.StartsWith("Previous", StringComparison.Ordinal));
            Require(previousMilestone.Disabled, "Previous must be disabled at the first milestone.");
            session.SelectPrompt(1);
            await Frames();
            Require(session.Prompt?.ActiveTab == 1 && session.Prompt.Title == "Your City Grew!",
                "Next must browse the next milestone without leaving the announcement.");
            session.SelectPrompt(0);
            await Frames();
            Require(session.Prompt?.ActiveTab == 0, "Previous must return to the newly reached milestone.");
            await KeyEvent(Key.Escape);

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
            editor.GetThemeConstant("caret_width") >= characterWidth &&
            editor.GetThemeStylebox("normal").GetMargin(Side.Right) >= characterWidth &&
            editor.GetThemeStylebox("focus").GetMargin(Side.Right) >= characterWidth,
            "Renaming must pause and keep its right-aligned block caret without changing title/header geometry.");
        if (DisplayServer.GetName() != "headless")
        {
            editor.CaretBlink = false;
            await Frames();
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = host.GetViewport().GetTexture().GetImage();
            var scale = new Vector2(image.GetWidth() / host.Size.X, image.GetHeight() / host.Size.Y);
            float caretWidth = editor.GetThemeConstant("caret_width");
            float caretHeight = font.GetHeight(nameSize);
            var caretCell = new Rect2(editor.GlobalPosition +
                new Vector2(editor.Size.X - caretWidth + 2, (editor.Size.Y - caretHeight) / 2 + 2),
                new Vector2(caretWidth - 4, caretHeight - 4));
            var caretColor = editor.GetThemeColor("caret_color");
            int solid = 0, pixels = 0;
            for (int y = (int)Math.Ceiling(caretCell.Position.Y * scale.Y); y < caretCell.End.Y * scale.Y; y++)
                for (int x = (int)Math.Ceiling(caretCell.Position.X * scale.X); x < caretCell.End.X * scale.X; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    if (Math.Abs(pixel.R - caretColor.R) < 0.02f &&
                        Math.Abs(pixel.G - caretColor.G) < 0.02f && Math.Abs(pixel.B - caretColor.B) < 0.02f) solid++;
                    pixels++;
                }
            Require(pixels > 0 && solid >= pixels * 0.95,
                $"Rename caret is not a solid full-cell block: {solid}/{pixels} pixels.");
            editor.CaretBlink = true;
        }
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
        var petition = Descendants(host).OfType<Label>().Single(label => label.Name == "PeasantStatus");
        string originalStatusText = status.Text, originalPetitionText = petition.Text;
        status.Text = "Cell";
        float petitionCharacterWidth = petition.GetThemeFont("font")
            .GetStringSize("M", fontSize: petition.GetThemeFontSize("font_size")).X;
        petition.Text = new string('M', (int)(host.Size.X * 0.65f / petitionCharacterWidth));
        host.FitStatusRow();
        float petitionTextWidth = petition.GetThemeFont("font")
            .GetStringSize(petition.Text, fontSize: petition.GetThemeFontSize("font_size")).X;
        Require(petition.CustomMinimumSize.X > host.Size.X / 2 &&
            petition.CustomMinimumSize.X >= petitionTextWidth,
            "Petitions must reserve their full text width when available, even beyond half the screen.");
        status.Text = originalStatusText;
        petition.Text = originalPetitionText;
        var savePathLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "QuickSavePath");
        Require(!Descendants(host).Any(node => node.Name == "WeeklyStatus"),
            "Recurring weekly updates must not have a bottom-bar display.");
        void CheckStatusPath()
        {
            if (session.Prompt?.IsSaveDialog != true && session.Prompt?.Title != "GAME MENU")
            {
                Require(!savePathLabel.IsVisibleInTree(), "Quick-save path must be hidden outside game menus and save dialogs.");
                return;
            }
            Require(savePathLabel.IsVisibleInTree() &&
                savePathLabel.Text == $"Quick-save: {session.SaveDisplayPath}" &&
                savePathLabel.TooltipText == Path.GetFullPath(session.SavePath) &&
                savePathLabel.HorizontalAlignment == HorizontalAlignment.Right &&
                savePathLabel.GetParent() == status.GetParent() &&
                Math.Abs(savePathLabel.GlobalPosition.Y - status.GlobalPosition.Y) < 1 &&
                Math.Abs(savePathLabel.GetGlobalRect().End.X - host.Size.X) < 1 &&
                Math.Abs(savePathLabel.GetParent<Control>().GetGlobalRect().End.Y - host.Size.Y) < 1 &&
                savePathLabel.GetParent<Control>().GetGlobalRect().Encloses(savePathLabel.GetGlobalRect()) &&
                status.GetGlobalRect().End.X <= savePathLabel.GlobalPosition.X &&
                savePathLabel.Size.X <= host.Size.X / 2 + 1 &&
                status.Size.X >= host.Size.X / 2 - 8,
                $"Quick-save path must remain at the bottom right beside cell information without overlap or overflow. " +
                $"Window={host.Size}, status={status.GetGlobalRect()}, path={savePathLabel.GetGlobalRect()}, " +
                $"row={savePathLabel.GetParent<Control>().GetGlobalRect()}, text='{savePathLabel.Text}', " +
                $"tooltip='{savePathLabel.TooltipText}'.");
        }
        Require(!savePathLabel.IsVisibleInTree(), "Quick-save path must be hidden during gameplay.");
        Require(status.Text.StartsWith(CellInspector.Summary(session.Game, session.Cursor), StringComparison.Ordinal) &&
            status.Text.Contains("Selection 3x1 (3 cells)", StringComparison.Ordinal),
            "Multi-cell selection details are not beside the cell details in the bottom status line.");
        session.ClearSelection();
        session.PlaceCursor(originalCursor);

        await KeyEvent(Key.F10);
        Require(session.Prompt is null && !host.MusicEnabled && host.SoundEnabled &&
            host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused &&
            host.MusicVolume == Main.DefaultMusicVolume && host.SoundVolume == Main.DefaultSoundVolume,
            "F10 must mute only music without changing volumes or opening a menu.");
        await KeyEvent(Key.F10);
        Require(host.MusicEnabled && !host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            "F10 did not unmute music.");
        OpenGameMenu(host);
        await Frames();
        Require(session.Prompt?.Title == "GAME MENU", "Native Esc while running did not open the game menu.");
        Require(session.Prompt!.Choices.Any(c => c.Label == "FONT SIZE") &&
            session.Prompt.Choices.Any(c => c.Label == "RESIZE SIDEBAR") &&
            !session.Prompt.Choices.Any(c => c.Label is "AUDIO CONTROLS" or "CITY MANAGEMENT") &&
            !session.Prompt.Choices.Any(c => c.Label is "Budget" or "Report" or "Undo"),
            "Esc menu must focus on configuration rather than city actions.");
        CheckStatusPath();
        Require(status.Text == $"Seed {session.Game.Config.Seed} | {session.Game.Map.Width}x{session.Game.Map.Height}" &&
            Math.Abs(status.GlobalPosition.X) < 1,
            "Game menu seed and map dimensions must appear at the bottom left.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Back to game" or "Quit"),
            "Game menu must replace Back to game and Quit rows with footer actions.");
        var menuQuit = Descendants(host).OfType<Button>().Single(button => button.Name == "GameMenuQuit");
        var menuCancel = Descendants(host).OfType<Button>().Single(button => button.Name == "GameMenuCancel");
        Require(menuQuit.GetThemeColor("font_color") == new Color("#ff7777") &&
            menuCancel.Alignment == HorizontalAlignment.Center && menuQuit.Size == menuCancel.Size,
            "Game menu actions must match submenu action sizes, with red Quit text and centered Cancel.");
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel &&
            menuCancel.GetThemeStylebox("focus") is StyleBoxFlat { BgColor: var cancelFocus } &&
            cancelFocus == new Color("#18232c"),
            "Game menu Cancel must start focused with a subtle blue-gray highlight.");
        Require(menuQuit.Text == "QUIT" && menuCancel.Text == "CANCEL" &&
            menuQuit is MnemonicButton { UnderlineIndex: 0 } &&
            menuCancel is MnemonicButton { UnderlineIndex: 0 } &&
            session.Prompt.Choices.All(choice => choice.Label == choice.Label.ToUpperInvariant() && choice.Cells is null) &&
            session.Prompt.Text.Length == 0,
            "Game menu must use uppercase title/actions/options without descriptions.");
        host.GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = Vector2.Zero, GlobalPosition = Vector2.Zero,
        }, true);
        await Frames();
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel,
            "Moving the pointer away must retain Cancel focus.");
        var gameDialog = Descendants(host).OfType<PanelContainer>()
            .Single(panel => panel.Name == "CityDialog" && panel.IsVisibleInTree());
        var menuFocusControls = Descendants(gameDialog).OfType<Control>()
            .Where(control => control.IsVisibleInTree() && control is Button or HSlider).ToArray();
        Require(menuFocusControls.Length == 16, "Game menu must expose all fourteen buttons and two sliders.");
        foreach (var expectedControl in menuFocusControls)
        {
            await KeyEvent(Key.Tab);
            Require(host.GetViewport().GuiGetFocusOwner() == expectedControl,
                $"Tab must focus game menu control {expectedControl.Name} in display order.");
            if (expectedControl is Button focusedButton)
                Require(focusedButton.GetThemeStylebox("focus") is StyleBoxFlat focusedStyle &&
                    focusedStyle.BgColor == new Color("#18232c") &&
                    focusedStyle.BgColor != ((StyleBoxFlat)focusedButton.GetThemeStylebox("normal")).BgColor,
                    "Every focused game menu button must have a visible blue-gray highlight.");
        }
        for (int index = menuFocusControls.Length - 2; index >= 0; index--)
        {
            await KeyEvent(Key.Tab, shift: true);
            Require(host.GetViewport().GuiGetFocusOwner() == menuFocusControls[index],
                "Shift+Tab must traverse game menu controls in reverse.");
        }
        await KeyEvent(Key.Tab, shift: true);
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel,
            "Shift+Tab must wrap to Cancel without leaving the menu.");
        foreach (var expectedControl in menuFocusControls)
        {
            await KeyEvent(Key.Down);
            Require(host.GetViewport().GuiGetFocusOwner() == expectedControl,
                $"Down must navigate to {expectedControl.Name}, including sliders.");
        }
        var menuMusicSlider = menuFocusControls.OfType<HSlider>().Single(slider => slider.Name == "MusicVolume");
        menuMusicSlider.GrabFocus();
        double menuVolume = host.MusicVolume;
        await KeyEvent(Key.Right);
        Require(host.GetViewport().GuiGetFocusOwner() == menuMusicSlider && host.MusicVolume == menuVolume + 1,
            "Right must adjust a focused slider instead of moving focus.");
        await KeyEvent(Key.Left);
        Require(host.MusicVolume == menuVolume, "Left must reverse the slider adjustment.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt?.Title == "GAME MENU" && host.GetViewport().GuiGetFocusOwner() == menuMusicSlider,
            "Enter on a slider must not activate an unrelated menu option.");
        await KeyEvent(Key.Up);
        Require(host.GetViewport().GuiGetFocusOwner()?.Name == "SidebarSizePlus",
            "Up must navigate out of the music slider.");
        menuCancel.GrabFocus();
        var newMap = Descendants(gameDialog).OfType<Button>().Single(button => button.Name == "GameMenuNewMap");
        Require(newMap.Text == "NEW MAP" && newMap.GetThemeColor("font_color") == new Color("#88ee99") &&
            newMap.GetIndex() == 1 && newMap.GlobalPosition.X < menuQuit.GlobalPosition.X &&
            Math.Abs(newMap.GetGlobalRect().End.Y - menuCancel.GetGlobalRect().End.Y) < 1,
            "NEW MAP must stay green to the right of SAVE in the game menu footer.");
        var footerSave = Descendants(gameDialog).OfType<Button>().Single(button => button.Name == "GameMenuSave");
        Require(footerSave.GetIndex() == 0 && footerSave.GlobalPosition.X < menuQuit.GlobalPosition.X &&
            Math.Abs(footerSave.GetGlobalRect().End.Y - menuCancel.GetGlobalRect().End.Y) < 1 &&
            footerSave.GetGlobalRect().End.X <= newMap.GlobalPosition.X,
            "SAVE must occupy the bottom left opposite QUIT and CANCEL.");
        Require(Descendants(gameDialog).OfType<Label>().Single(label => label.Text == "GAME MENU")
            .HorizontalAlignment == HorizontalAlignment.Center, "GAME MENU header must be centered.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt is null, "Game menu Cancel must close the dialog without quitting.");
        Require(!savePathLabel.IsVisibleInTree() &&
            status.Text.StartsWith(session.HasCursor ? CellInspector.Summary(session.Game, session.Cursor) : "Press F1 for HELP",
                StringComparison.Ordinal),
            "Closing the game menu must hide metadata and restore normal gameplay status.");
        OpenGameMenu(host);
        await Frames();
        await KeyEvent(Key.C);
        Require(session.Prompt is null, "C must close the game menu without quitting.");
        OpenGameMenu(host);
        await Frames();
        await KeyEvent(Key.Q);
        await Frames();
        Require(session.Prompt is { Title: "Quit", Footer: null }, "Save confirmation still includes the quick-save path.");
        CheckStatusPath();
        var pathPointer = savePathLabel.GetGlobalRect().GetCenter();
        host.GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = pathPointer, GlobalPosition = pathPointer,
        }, true);
        await Frames();
        var saveDialog = Descendants(host).OfType<PanelContainer>().Single(panel => panel.Name == "CityDialog");
        VerifyMapCenteredDialog(host, saveDialog);
        VerifyDialogButtonBorders(saveDialog);
        var saveActions = Descendants(saveDialog).OfType<Button>().Where(button => button.IsVisibleInTree()).ToArray();
        Require(saveActions.All(button => button.Alignment == HorizontalAlignment.Center) &&
            saveActions.Single(button => button.Text.StartsWith("Save and quit", StringComparison.Ordinal))
                .GetThemeColor("font_color") == new Color("#88ee99") &&
            saveActions.Single(button => button.Text.StartsWith("Quit without saving", StringComparison.Ordinal))
                .GetThemeColor("font_color") == new Color("#ff7777") &&
            !Descendants(saveDialog).OfType<Label>().Any(label => label.Text == session.Prompt!.Text),
            "Save confirmation must omit descriptive text, center actions, and use green save/red discard text.");
        Require(saveDialog is TerminalFrame { JoinLeft: false, JoinRight: false, JoinTop: false, JoinBottom: false } &&
            saveDialog.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var saveBackground } &&
            saveBackground == Colors.Black &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                .All(side => saveDialog.GetThemeStylebox("panel").GetContentMargin(side) == TerminalFrame.Inset),
            "Save confirmation must use the shared black, double-white terminal frame and inset.");
        Require(saveDialog.GetParent<Control>().TooltipText == savePathLabel.TooltipText,
            "The quick-save path tooltip is blocked while a save confirmation is open.");
        await KeyEvent(Key.Escape);
        CheckStatusPath();
        var cityMenu = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        float largeMenuHeight = cityMenu.Size.Y;
        float gameMenuWidth = cityMenu.Size.X;
        var menuButtons = Descendants(cityMenu).OfType<MnemonicButton>()
            .Where(b => b.IsVisibleInTree() && b.Name != "GameMenuQuit" && b.Name != "GameMenuCancel" &&
                b.Name != "GameMenuSave").ToArray();
        VerifyDialogButtonBorders(cityMenu);
        Require(menuButtons.All(b => b.Alignment == HorizontalAlignment.Left),
            "Menu choices must be left-aligned.");
        var menuPrompt = session.Prompt ?? throw new InvalidOperationException("City menu prompt is missing.");
        Require(TextTable.ForPrompt(menuPrompt) is null &&
            !Descendants(cityMenu).OfType<Label>().Any(label => label.Text == "Configure display, sound and music."),
            "Game menu must omit its description and description columns.");
        float expectedMenuWidth = Math.Min(420,
            host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
        Require(Math.Abs(cityMenu.Size.X - expectedMenuWidth) < 1,
            $"Game menu must retain its compact width: {cityMenu.Size.X} vs {expectedMenuWidth}.");
        Require(!Descendants(cityMenu).Any(n => n.Name == "GameMenuHeaders") &&
            Descendants(cityMenu).OfType<TerminalFrame>().All(frame => !frame.JoinAdjacent &&
                !frame.JoinLeft && !frame.JoinRight && !frame.JoinTop && !frame.JoinBottom),
            "Game menu must omit the column-header frame and retain closed, unjoined double borders.");
        var menuMargin = Descendants(cityMenu).OfType<MarginContainer>()
            .Single(m => m.Name == "DialogContentMargin");
        Require(new[] { "left", "top", "right", "bottom" }
            .All(side => menuMargin.GetThemeConstant($"margin_{side}") == 2),
            "Game menu must use matching two-pixel extra padding on every side.");
        var menuScroll = Descendants(menuMargin).OfType<ScrollContainer>().Single(s => s.IsVisibleInTree());
        var menuContent = menuScroll.GetChild<Control>(0).GetChild<Control>(0);
        var menuRect = cityMenu.GetGlobalRect();
        var contentRect = menuContent.GetGlobalRect();
        Require(new[] { contentRect.Position.X - menuRect.Position.X,
            contentRect.Position.Y - menuRect.Position.Y, menuRect.End.X - contentRect.End.X,
            menuRect.End.Y - contentRect.End.Y }.All(padding => Math.Abs(padding - TerminalFrame.Inset - 2) < 1),
            "Game menu content must have equal visible top, bottom, left and right padding.");
        VerifyMapCenteredDialog(host, cityMenu);
        Require(menuButtons.All(button => button is MnemonicButton mnemonic &&
            mnemonic.UnderlineIndex >= 0 && mnemonic.UnderlineIndex < mnemonic.Text.Length &&
            !char.IsDigit(mnemonic.Text[0])), "Menu options must have underlined letters instead of number prefixes.");
        var trappedMenu = session.Prompt;
        var trappedCursor = session.Cursor;
        int trappedZoom = session.ZoomLevel;
        bool trappedDebug = session.InputDebug;
        await KeyEvent(Key.Key9);
        await KeyEvent(Key.F12);
        await KeyEvent(Key.Equal, ctrl: true);
        Require(ReferenceEquals(session.Prompt, trappedMenu) && session.Cursor == trappedCursor &&
            session.ZoomLevel == trappedZoom && session.InputDebug == trappedDebug,
            "Unassigned menu keys leaked into gameplay or activated numbered options.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Music" or "Sound"),
            "Music and Sound rows must be omitted from the game menu.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Effects" or "Celebrations"),
            "Game menu must omit Effects and Celebrations rows.");
        var musicVolume = Descendants(host).OfType<HSlider>().Single(s => s.Name == "MusicVolume");
        var soundVolume = Descendants(host).OfType<HSlider>().Single(s => s.Name == "SoundVolume");
        var musicMute = Descendants(host).OfType<Button>().Single(b => b.Name == "MusicMute");
        var soundMute = Descendants(host).OfType<Button>().Single(b => b.Name == "SoundMute");
        var audioModal = Descendants(host).OfType<PanelContainer>()
            .Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        VerifyDialogButtonBorders(audioModal);
        Require(session.Prompt?.Title == "GAME MENU" && session.Prompt.Text.Length == 0 &&
            Math.Abs(audioModal.Size.X - gameMenuWidth) < 1 &&
            Descendants(audioModal).OfType<Label>().Any(label => label.Text == "MUSIC") &&
            Descendants(audioModal).OfType<Label>().Any(label => label.Text == "SOUND"),
            "Audio controls must appear directly in the game menu without increasing its width.");
        foreach (var frame in Descendants(audioModal).OfType<PanelContainer>().Where(frame =>
            frame.Name == "MusicControls" || frame.Name == "SoundControls"))
            Require(frame.GetThemeStylebox("panel") is StyleBoxFlat style &&
                style.BorderColor == Colors.White &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side =>
                    style.GetBorderWidth(side) == 1 && style.GetContentMargin(side) == 4),
                "Audio channels must have tightly padded single-white borders.");
        Require(audioModal.Position.X >= 0 && audioModal.Position.Y >= 0 &&
            audioModal.Position.X + audioModal.Size.X <= host.Size.X &&
            audioModal.Position.Y + audioModal.Size.Y <= host.Size.Y,
            "Audio controls extend outside the window.");
        Require(audioModal.GetGlobalRect().Encloses(musicMute.GetGlobalRect()) &&
            audioModal.GetGlobalRect().Encloses(soundMute.GetGlobalRect()),
            "Audio mute buttons are clipped.");
        Require(musicVolume.GetParent().GetParent() == musicMute.GetParent() &&
            soundVolume.GetParent().GetParent() == soundMute.GetParent() &&
            musicVolume.GetGlobalRect().End.X <= musicMute.GetGlobalRect().Position.X &&
            soundVolume.GetGlobalRect().End.X <= soundMute.GetGlobalRect().Position.X &&
            !Descendants(audioModal).OfType<Label>().Any(label => label.Text.Contains('%')),
            "Audio sliders must sit beside their mute buttons without percentage displays.");
        foreach (var volumeSlider in new[] { musicVolume, soundVolume })
        {
            var audioRow = volumeSlider.GetParent().GetParent();
            var labelCell = audioRow.GetChild<PanelContainer>(0);
            Require(labelCell.GetChild<Label>(0).Text == (volumeSlider == musicVolume ? "MUSIC" : "SOUND") &&
                labelCell.GetThemeStylebox("panel") is StyleBoxFlat labelStyle &&
                labelStyle.BorderColor == Colors.White &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => labelStyle.GetBorderWidth(side) == 1) &&
                Math.Abs(labelCell.GetGlobalRect().GetCenter().Y - volumeSlider.GetGlobalRect().GetCenter().Y) < 1,
                "Audio labels must have separate single-white cells beside their sliders.");
            var sliderPadding = volumeSlider.GetParent<MarginContainer>();
            Require(sliderPadding.GetThemeConstant("margin_left") == 12 &&
                sliderPadding.GetThemeConstant("margin_right") == 12 &&
                Math.Abs(volumeSlider.Position.X - 12) < 1 &&
                Math.Abs(sliderPadding.Size.X - volumeSlider.Position.X - volumeSlider.Size.X - 12) < 1,
                "Audio sliders must have twelve pixels of breathing room on both sides.");
            Require(volumeSlider.TextureFilter == CanvasItem.TextureFilterEnum.Nearest &&
                volumeSlider.GetThemeIcon("grabber").GetSize() == new Vector2(12, 20),
                "Audio sliders must use crisp rectangular terminal block handles.");
            foreach (string sliderState in new[] { "slider", "grabber_area", "grabber_area_highlight" })
                Require(volumeSlider.GetThemeStylebox(sliderState) is StyleBoxFlat terminalTrack &&
                    terminalTrack.BorderColor == Colors.White &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                        .All(side => terminalTrack.GetBorderWidth(side) == 1) &&
                    terminalTrack.CornerRadiusTopLeft == 0 && terminalTrack.CornerRadiusTopRight == 0 &&
                    terminalTrack.CornerRadiusBottomLeft == 0 && terminalTrack.CornerRadiusBottomRight == 0,
                    "Audio slider tracks must have square corners and single-white terminal outlines.");
        }
        musicVolume.Value = 37;
        soundVolume.Value = 62;
        Require(host.MusicVolume == 37 && host.SoundVolume == 62, "Independent audio sliders did not update volume.");
        musicVolume.GrabFocus();
        await KeyEvent(Key.Left);
        Require(host.MusicVolume == 36 && host.SoundVolume == 62,
            $"Keyboard volume adjustment affected the wrong channel: music={host.MusicVolume}, sound={host.SoundVolume}, focus={host.GetViewport().GuiGetFocusOwner()}.");
        musicMute.EmitSignal(Button.SignalName.Pressed);
        Require(!host.MusicEnabled && host.SoundEnabled && musicMute.Text == "",
            "Music mute also disabled sounds or did not update its button.");
        musicMute.EmitSignal(Button.SignalName.Pressed);
        soundMute.EmitSignal(Button.SignalName.Pressed);
        Require(host.MusicEnabled && !host.SoundEnabled && soundMute.Text == "",
            "Sound mute also disabled music or did not update its button.");
        soundMute.EmitSignal(Button.SignalName.Pressed);
        Require(host.MusicVolume == 36 && host.SoundVolume == 62, "Mute changed the saved volume levels.");
        await KeyEvent(Key.M);
        Require(!host.MusicEnabled && host.SoundEnabled, "Music mute mnemonic affected the wrong channel.");
        await KeyEvent(Key.M);
        await KeyEvent(Key.U);
        Require(host.MusicEnabled && !host.SoundEnabled, "Sound mute mnemonic affected the wrong channel.");
        await KeyEvent(Key.U);
        musicVolume.Value = 0;
        Require(host.MusicEnabled && host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            "Zero music volume must be silent without changing the mute preference.");
        musicVolume.Value = Main.DefaultMusicVolume;
        soundVolume.Value = Main.DefaultSoundVolume;
        Require(!host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused, "Raising zero volume did not resume music.");
        Require(ReferenceEquals(session.Prompt, trappedMenu),
            "Adjusting inline audio controls must not replace the game menu or lose focus.");
        await KeyEvent(Key.F);
        Require(session.Prompt?.Title == "Font size", "F must open font controls from the game menu.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "GAME MENU" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Name: var restoredAction, Text: "CANCEL" } &&
            restoredAction == "GameMenuCancel",
            "Cancelling font controls must restore the parent menu with Cancel focused.");
        var saveMenu = session.Prompt;
        await KeyEvent(Key.S);
        Require(ReferenceEquals(session.Prompt, saveMenu) && !session.HasUnsavedChanges &&
            File.Exists(session.SavePath),
            "Game Menu S must quick-save successfully without closing or replacing the menu.");
        var saveButton = Descendants(host).OfType<MnemonicButton>().Single(button => button.Name == "GameMenuSave");
        Require(saveButton.Text == "SAVE" && saveButton.UnderlineIndex == 0,
            "Game Menu must expose an underlined SAVE action.");
        saveButton.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(ReferenceEquals(session.Prompt, saveMenu) && !session.HasUnsavedChanges,
            "The SAVE button must activate with Enter and preserve the menu.");
        await KeyEvent(Key.Escape);
        session.ShowSessionMenu();
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
        Require(session.Prompt?.Title == "Load city", "Native Escape did not return from the path dialog to the load menu.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt is null, "Native Escape did not close the root load menu.");
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
        int reportChoice = session.Prompt!.Choices.ToList().FindIndex(choice => choice.Label == "Report");
        var reportShortcut = session.Prompt.Shortcuts[reportChoice];
        await KeyEvent((Key)reportShortcut.Letter, shift: reportShortcut.Shift);
        Require(session.Prompt?.Title == "Weekly Report", "Underlined menu shortcut failed.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "City menu" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Text: var restoredReport } &&
            restoredReport.StartsWith("Report", StringComparison.Ordinal),
            "Cancelling the report did not restore its parent and focus.");
        await KeyEvent(Key.Escape);
        int shiftedChoice = -1;
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        await Frames();
        var servicePrompt = session.Prompt!;
        string beforeHelp = SaveGameStore.Serialize(session.Game);
        await KeyEvent(Key.Enter, shift: true);
        Require(ReferenceEquals(servicePrompt, session.Prompt) && session.Preview is null &&
            servicePrompt.HelpIndex == servicePrompt.SelectedIndex &&
            SaveGameStore.Serialize(session.Game) == beforeHelp,
            "Shift+Enter must inspect a choice without activation or simulation changes.");
        var helpPanel = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ChoiceHelpPanel");
        var helpText = Descendants(host).OfType<Label>().Single(n => n.Name == "ChoiceHelpText");
        var focusedChoice = (Button)host.GetViewport().GuiGetFocusOwner();
        Require(helpPanel.IsVisibleInTree() && helpPanel.GlobalPosition.X >=
            focusedChoice.GetGlobalRect().End.X && helpText.Text.Contains("Upkeep:", StringComparison.Ordinal),
            "Contextual help must appear to the right with service details.");
        int clickedIndex = 1;
        var clickedChoice = Descendants(host).OfType<Button>().First(b =>
            b.TooltipText.StartsWith(servicePrompt.Choices[clickedIndex].Label + " (", StringComparison.Ordinal));
        Vector2 helpClick = clickedChoice.GetGlobalRect().GetCenter();
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = pressed, ShiftPressed = true,
                Position = helpClick, GlobalPosition = helpClick,
            }, true);
        await Frames();
        Require(ReferenceEquals(servicePrompt, session.Prompt) && session.Preview is null &&
            servicePrompt.HelpIndex == clickedIndex,
            $"Native Shift+click must open option help without activation: same={ReferenceEquals(servicePrompt, session.Prompt)}, preview={session.Preview?.Name}, help={servicePrompt.HelpIndex}, click={helpClick}, button={clickedChoice.GetGlobalRect()}.");
        bool helpHeadless = DisplayServer.GetName() == "headless";
        var helpWindowSize = helpHeadless ? host.GetWindow().ContentScaleSize : host.GetWindow().Size;
        if (helpHeadless) host.GetWindow().ContentScaleSize = new Vector2I(640, Main.MinimumWindowHeight);
        else host.GetWindow().Size = new Vector2I(640, 480);
        await Frames();
        var helpDialog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        Require(helpDialog.GetGlobalRect().Position.X >= 0 && helpDialog.GetGlobalRect().End.X <= host.Size.X + 1 &&
            helpDialog.GetGlobalRect().Position.Y >= 0 && helpDialog.GetGlobalRect().End.Y <= host.Size.Y + 1 &&
            helpPanel.Size.X > 0 && helpText.AutowrapMode != TextServer.AutowrapMode.Off,
            $"Compact contextual help must fit: dialog={helpDialog.GetGlobalRect()}, window={host.Size}, help={helpPanel.Size}.");
        if (helpHeadless) host.GetWindow().ContentScaleSize = helpWindowSize;
        else host.GetWindow().Size = helpWindowSize;
        await Frames();
        focusedChoice.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(session.Preview?.Building?.Name == servicePrompt.Choices[0].Label,
            "Plain Enter must still activate the focused placement choice after contextual help.");
        await KeyEvent(Key.Escape);
        Require(ReferenceEquals(servicePrompt, session.Prompt), "Help must preserve placement menu history.");
        for (int index = 0; index < session.Prompt!.Shortcuts.Count; index++)
            if (session.Prompt.Shortcuts[index].Shift) { shiftedChoice = index; break; }
        if (shiftedChoice >= 0)
        {
            var shortcut = session.Prompt.Shortcuts[shiftedChoice];
            string buildingName = session.Prompt.Choices[shiftedChoice].Label;
            await KeyEvent((Key)shortcut.Letter, shift: true);
            Require(session.Preview?.Building?.Name == buildingName, "Shift mnemonic selected the wrong service building.");
            await KeyEvent(Key.Escape);
            Require(session.Prompt?.Title == "Service buildings" && session.Prompt.SelectedIndex == shiftedChoice,
                "Preview cancellation did not restore the service menu and focus.");
        }
        session.ClosePrompt();
        session.CancelPreview();

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
        OpenGameMenu(host);
        await Frames();
        Require(session.Prompt?.Title == "GAME MENU", "Esc while running must open the game menu even while cells are selected.");
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
        if (headless) window.ContentScaleSize = new Vector2I(640, Main.MinimumWindowHeight);
        else window.Size = new Vector2I(640, 480);
        await Frames();
        Require(!guideHeader.IsVisibleInTree() &&
            Descendants(host).OfType<TerminalFrame>().Single(frame => frame.Name == "GuideHeaderFrame").Visible == false &&
            !reportsHeader.IsVisibleInTree() &&
            Descendants(host).OfType<Label>().Single(label => label.Name == "ViewsHeader").IsVisibleInTree(),
            "Narrow windows must hide GUIDE and REPORTS while keeping VIEWS visible.");
        int narrowHeaderSize = budgetHeader.GetThemeFontSize("font_size");
        Require(budgetHeader.CustomMinimumSize.X <= budgetHeader.GetThemeFont("font")
            .GetStringSize("99,999g [999g]", fontSize: narrowHeaderSize).X + 1 &&
            narrowHeaderSize > 8 && budgetHeader.TooltipText.Contains(budgetHeader.Text, StringComparison.Ordinal),
            $"Narrow headers must reserve less gold space for larger text and retain the full value tooltip: font={narrowHeaderSize}, width={budgetHeader.CustomMinimumSize.X}, text={budgetHeader.Text}, tooltip={budgetHeader.TooltipText}.");
        await KeyEvent(Key.F6);
        Require(session.Prompt?.Title == "TermCity guide", "F6 must open the guide even when its header is hidden.");
        await KeyEvent(Key.Escape);
        CheckStatusPath();
        session.ShowSessionMenu();
        await Frames();
        var finances = Descendants(host).OfType<Label>().Single(l => l.Name == "Budget");
        Require(finances.HorizontalAlignment == HorizontalAlignment.Right &&
            finances.Text.StartsWith(Fmt.CompactMoney(session.Game.Money), StringComparison.Ordinal) &&
            finances.GlobalPosition.X >= host.Size.X / 2 &&
            finances.GlobalPosition.X + finances.Size.X <= host.Size.X,
            "Budget must remain visible on the right side of the HUD at minimum window size.");
        var modal = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        Require(modal.Position.X >= 0 && modal.Position.Y >= 0 &&
            modal.Position.X + modal.Size.X <= host.Size.X && modal.Position.Y + modal.Size.Y <= host.Size.Y,
            "City menu does not fit the minimum window size.");
        Require(modal.GlobalPosition.X >= host.Map.GetParent<Control>().GlobalPosition.X,
            "Menus must not cover the construction icons at minimum window size.");
        VerifyMapCenteredDialog(host, modal);
        await KeyEvent(Key.Escape);
        Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar")
            .FindChild("Roads", true, false).EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(modal.GlobalPosition.X >= host.Map.GetParent<Control>().GlobalPosition.X &&
            modal.GetGlobalRect().End.X <= host.Size.X &&
            modal.GetGlobalRect().End.Y <= host.Size.Y &&
            Descendants(modal).OfType<BoxContainer>().Single(n => n.Name == "ConstructionDetails").Vertical,
            $"Narrow construction catalogs must fit beside the icons: {modal.GetGlobalRect()}, window={host.Size}; " +
            string.Join(", ", Descendants(modal).OfType<Control>().Where(c =>
                c.Name == "ConstructionTypes" || c.Name == "ConstructionStats" || c.Name == "ConstructionInformation")
                .Select(c => $"{c.Name}={c.GetCombinedMinimumSize()}")));
        await KeyEvent(Key.Escape);
        session.ShowSessionMenu();
        await Frames();
        Require(host.Map.GlobalPosition.X + host.Map.Size.X <= host.Size.X &&
            host.Map.GlobalPosition.Y + host.Map.Size.Y <= host.Size.Y, "Map does not fit the minimum window size.");
        await KeyEvent(Key.Escape);
        var constructionRail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar");
        Require(!constructionRail.GetVScrollBar().Visible &&
            Descendants(constructionRail).OfType<Button>().All(icon =>
                icon.GetParent<Control>().GetGlobalRect().End.Y <= constructionRail.GetGlobalRect().End.Y),
            "All construction icons must fit at minimum window size without scrolling.");
        Descendants(constructionRail).OfType<Button>()
            .Single(b => b.Name == CityReport.ServiceName(TermCity.Core.Buildings.ServiceKind.Faith))
            .EmitSignal(Button.SignalName.Pressed);
        await Frames();
        var narrowCatalog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        Require(narrowCatalog.Position.X >= 0 && narrowCatalog.Position.Y >= 0 &&
            narrowCatalog.GetGlobalRect().End.X <= host.Size.X && narrowCatalog.GetGlobalRect().End.Y <= host.Size.Y,
            $"Construction catalog must fit the minimum window: dialog={narrowCatalog.GetGlobalRect()}, window={host.Size}.");
        await KeyEvent(Key.Escape);
        if (headless) window.ContentScaleSize = originalSize;
        else window.Size = originalSize;
        await Frames();
        Require(guideHeader.IsVisibleInTree(), "GUIDE must reappear when the window becomes wide again.");
        if (!headless && OS.GetName() == "Windows")
        {
            GD.Print("TERMCITY_WINDOWS_RESIZE_BEGIN");
            bool angle = RenderingServer.GetCurrentRenderingDriverName() == "opengl3_angle";
            string beforeResize = SaveGameStore.Serialize(session.Game);
            foreach (var size in new[] { new Vector2I(800, 600), new Vector2I(1920, 1040) })
            {
                var timer = Stopwatch.StartNew();
                window.Size = size;
                await Frames();
                CheckStatusPath();
                Require(!angle || timer.Elapsed.TotalMilliseconds < 1000,
                    $"Window resize stalled for {timer.Elapsed.TotalMilliseconds:F0} ms.");
            }
            var maximize = Stopwatch.StartNew();
            window.Mode = Window.ModeEnum.Maximized;
            await Frames();
            CheckStatusPath();
            Require(!angle || maximize.Elapsed.TotalMilliseconds < 1000,
                $"Maximize stalled for {maximize.Elapsed.TotalMilliseconds:F0} ms.");
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = originalSize;
            await Frames();
            double nativeWorst = await WindowsResizeSmoke.Run(window);
            await Frames();
            CheckStatusPath();
            Require(!angle || nativeWorst < 1000, $"Native edge resize stalled for {nativeWorst:F0} ms.");
            Require(beforeResize == SaveGameStore.Serialize(session.Game), "Resizing changed city data.");
            GD.Print($"TERMCITY_WINDOWS_RESIZE_OK driver={RenderingServer.GetCurrentRenderingDriverName()} native_worst={nativeWorst:F0}ms");
        }
        string cityBeforeFontChange = SaveGameStore.Serialize(session.Game);
        host.SetFontSize(Main.DefaultFontSize);
        await KeyEvent(Key.F3);
        Require(session.Prompt?.Title == "Font size", "F3 did not open the font dialog.");
        await Frames();
        var smallDialog = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        var compactScroll = Descendants(smallDialog).OfType<ScrollContainer>().Single(s => s.IsVisibleInTree());
        float compactContentHeight = Math.Min(compactScroll.GetChild<Control>(0).GetCombinedMinimumSize().Y,
            host.Size.Y - host.Map.GetParent<Control>().GlobalPosition.Y - 64);
        Require(smallDialog.Size.Y < largeMenuHeight &&
            Math.Abs(smallDialog.Size.Y - compactContentHeight - 28) < 1,
            $"Compact dialog geometry: size={smallDialog.Size}, center={smallDialog.GetGlobalRect().GetCenter()}, window={host.Size}.");
        VerifyMapCenteredDialog(host, smallDialog);
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
        OpenGameMenu(host);
        await Frames();
        var inlineMenu = session.Prompt;
        var inlineFontMinus = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizeMinus");
        var inlineFontPlus = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizePlus");
        var inlineSidebarMinus = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizeMinus");
        var inlineSidebarPlus = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizePlus");
        var inlineFontReset = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizeReset");
        var inlineSidebarReset = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizeReset");
        Require(new[] { inlineFontReset, inlineSidebarReset }.All(reset =>
            new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }.All(resetState =>
                reset.GetThemeStylebox(resetState) is StyleBoxFlat resetStyle &&
                resetStyle.ContentMarginLeft == 16 && resetStyle.ContentMarginRight == 16 &&
                resetStyle.ContentMarginTop == 10 && resetStyle.ContentMarginBottom == 10)),
            "RESET buttons must retain increased symmetric padding in every state.");
        Require(new[] { inlineFontMinus, inlineFontPlus, inlineSidebarMinus, inlineSidebarPlus }
            .All(button => button.Size.X >= 48 && button.Size.Y >= 44 &&
                button.GetThemeFontSize("font_size") == 28 && button.Alignment == HorizontalAlignment.Center),
            "Game menu +/- controls must have larger targets and centered 28-point symbols.");
        Require(inlineFontReset.GetIndex() == inlineFontMinus.GetIndex() + 1 &&
            inlineFontPlus.GetIndex() == inlineFontReset.GetIndex() + 1 &&
            inlineSidebarReset.GetIndex() == inlineSidebarMinus.GetIndex() + 1 &&
            inlineSidebarPlus.GetIndex() == inlineSidebarReset.GetIndex() + 1,
            "Inline RESET buttons must appear between - and + in each size row.");
        Require(inlineFontMinus.GetParent().GetChild<Button>(0).GetThemeColor("font_color") == Colors.White &&
            inlineSidebarMinus.GetParent().GetChild<Button>(0).GetThemeColor("font_color") == Colors.White,
            "Game menu display-option labels must be white.");
        inlineFontPlus.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(host.FontSize == Main.DefaultFontSize + 2 &&
            host.GetViewport().GuiGetFocusOwner() == inlineFontPlus && ReferenceEquals(session.Prompt, inlineMenu),
            "Inline font + must adjust by two points without replacing the game menu or losing focus.");
        inlineFontMinus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize, "Inline font - must restore the previous size.");
        host.SetFontSize(Main.MinFontSize);
        inlineFontMinus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MinFontSize, "Inline font - must respect the minimum.");
        host.SetFontSize(Main.MaxFontSize);
        inlineFontPlus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MaxFontSize, "Inline font + must respect the maximum.");
        inlineFontReset.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(host.FontSize == Main.DefaultFontSize && ReferenceEquals(session.Prompt, inlineMenu) &&
            host.GetViewport().GuiGetFocusOwner() == inlineFontReset,
            "Inline font RESET must restore the default without replacing the menu or losing focus.");
        var inlineSplit = Descendants(host).OfType<CitySplit>().Single();
        inlineSplit.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        float inlineWidth = inlineSplit.SidebarWidth;
        inlineSidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - inlineWidth - TerminalGrid.CellWidth) < 1 &&
            ReferenceEquals(session.Prompt, inlineMenu),
            "Inline sidebar + must adjust by one character without replacing the game menu.");
        inlineSidebarMinus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - inlineWidth) < 1,
            "Inline sidebar - must restore the previous width.");
        inlineSidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        inlineSidebarReset.GrabFocus();
        await KeyEvent(Key.Enter);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - CitySplit.DefaultSidebar) < 1 &&
            ReferenceEquals(session.Prompt, inlineMenu) && host.GetViewport().GuiGetFocusOwner() == inlineSidebarReset,
            "Inline sidebar RESET must restore the default without replacing the menu or losing focus.");
        int fontChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "FONT SIZE");
        session.SelectPrompt(fontChoice);
        await Frames();
        Require(session.Prompt?.Title == "Font size", "Font size row must open its adjustment dialog.");
        await KeyEvent(Key.Escape);
        int resizeChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "RESIZE SIDEBAR");
        Require(resizeChoice >= 0, "City menu is missing keyboard sidebar resizing.");
        session.SelectPrompt(resizeChoice);
        await Frames();
        var split = Descendants(host).OfType<CitySplit>().Single();
        split.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        float oldSidebar = split.SidebarWidth;
        var sidebarValue = Descendants(host).OfType<Label>().Single(l => l.Name == "SidebarDialogValue");
        var sidebarMinus = sidebarValue.GetParent().GetChild<Button>(0);
        var sidebarPlus = sidebarValue.GetParent().GetChild<Button>(2);
        Require(session.Prompt?.Title == "Sidebar size", "Sidebar size row must open its adjustment dialog.");
        sidebarMinus.GrabFocus();
        await KeyEvent(Key.Right);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1 &&
            host.GetViewport().GuiGetFocusOwner() == sidebarPlus,
            "Right arrow must focus + without resizing the sidebar.");
        await KeyEvent(Key.Enter);
        Require(Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            $"Sidebar + step: before={oldSidebar}, after={split.SidebarWidth}, split={split.Size}, " +
            $"minimum={split.GetChild<Control>(1).GetCombinedMinimumSize()}.");
        await KeyEvent(Key.Left);
        Require(host.GetViewport().GuiGetFocusOwner() == sidebarMinus &&
            Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            "Left arrow must focus - without resizing.");
        await KeyEvent(Key.Enter);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1,
            "Enter must activate the focused - button.");
        sidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        session.SelectPrompt(1);
        await Frames();
        Require(Math.Abs(split.SidebarWidth - CitySplit.DefaultSidebar) < 1,
            "Sidebar RESET must restore the default width.");
        split.SetSidebarWidth(float.MaxValue);
        await Frames();
        Require(split.SidebarWidth <= CitySplit.MaximumSidebar + 1 &&
            split.GetChild<Control>(0).Size.X >= 320 - 1, "Sidebar exceeded its maximum or map minimum.");
        split.SetSidebarWidth(0);
        await Frames();
        Require(split.SidebarWidth >= CitySplit.MinimumSidebar - 1, "Sidebar fell below its minimum.");
        session.SelectPrompt(0);
        Require(session.Prompt?.Title == "GAME MENU" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Text: "CANCEL" },
            "Sidebar OK must return to the game menu with Cancel focused.");
        await KeyEvent(Key.Escape);
        await Frames();
        var dragger = split.GetDragAreaControls()[0];
        var dragPoint = dragger.GetGlobalRect().GetCenter();
        float beforeDrag = split.SidebarWidth;
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint, GlobalPosition = dragPoint, ButtonIndex = MouseButton.Left, Pressed = true,
        }, true);
        for (int step = 1; step <= 5; step++)
        {
            var position = dragPoint - new Vector2(step, 0);
            host.GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = position, GlobalPosition = position,
                Relative = new Vector2(-1, 0), ButtonMask = MouseButtonMask.Left,
            }, true);
            await Frames();
            Require(Math.Abs(split.SidebarWidth - beforeDrag - step) < 1,
                "Sidebar dragging must advance by pixels, not whole characters.");
            VerifyMapPadding(host);
        }
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint - new Vector2(5, 0), GlobalPosition = dragPoint - new Vector2(5, 0),
            ButtonIndex = MouseButton.Left, Pressed = false,
        }, true);
        await Frames();
        Require(split.SidebarWidth > CitySplit.MinimumSidebar, "Dragging the divider did not resize the sidebar.");
        split.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        var sidebar = Descendants(host).OfType<CityPanel>().Single();
        var cityTable = Descendants(sidebar).OfType<GridContainer>().Single(control => control.Name == "CityTable");
        Require(cityTable.GetGlobalRect().End.Y <= sidebar.GetGlobalRect().End.Y + 1 &&
            cityTable.Size.X <= sidebar.Size.X && !Descendants(sidebar).OfType<ScrollContainer>().Any(),
            "The City table must fit without sidebar scrolling.");
        foreach (int fontSize in new[] { Main.DefaultFontSize, Main.MaxFontSize, Main.MinFontSize })
        {
            host.SetFontSize(fontSize);
            if (headless)
                window.ContentScaleSize = new Vector2I(960 * fontSize / 16, Main.MinimumWindowHeight * fontSize / 16);
            else host.FitWindowToFont();
            await Frames();
            CheckStatusPath();
            Require(host.GetWindow().ContentScaleFactor == fontSize / 16f,
                "Font setting did not scale the interface and map together.");
            Require(host.Map.Grid.Columns == (int)Math.Ceiling(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)Math.Ceiling(host.Map.Size.Y / TerminalGrid.CellHeight),
                "Font resizing left stale map dimensions.");
            VerifyMapPadding(host);
            VerifySidebarTables(host);
            Require(host.Map.Grid.TryCell(TerminalGrid.CellWidth * 2, TerminalGrid.CellHeight * 2, out var fontCell) &&
                fontCell == new Pos(2, 2), "Font resizing broke map hit targets.");
            Require(cityBeforeFontChange == SaveGameStore.Serialize(session.Game),
                "Font resizing changed the city.");
        }
        host.SetFontSize(Main.MaxFontSize);
        if (headless)
        {
            window.ContentScaleSize = new Vector2I(640 * Main.MaxFontSize / 16, Main.MinimumWindowHeight * Main.MaxFontSize / 16);
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
        await VerifyEffects(host, Frames, (code, ctrl, unicode, meta, physical) => KeyEvent(code, ctrl, unicode, meta, physical));
        session.ClosePrompt();
        session.SetZoom(0);
        var green = session.Game.Map.Content.Buildings.Get("Charcoal Burners");
        var areaSpot = Enumerable.Range(0, session.Game.Map.Width * session.Game.Map.Height)
            .Select(session.Game.Map.PosOf)
            .OrderBy(p => Math.Abs(p.X - session.Game.Map.Width / 2) + Math.Abs(p.Y - session.Game.Map.Height / 2))
            .First(p => session.Game.CanPlaceBuilding(green, p.X, p.Y));
        session.PlaceCursor(areaSpot);
        session.CenterOn(areaSpot);
        host.Map.Invalidate(true);
        await Frames();
        var placementCell = new Pos(areaSpot.X - session.CameraX, areaSpot.Y - session.CameraY);
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0xffdc5a),
            "The normal cursor must retain its yellow highlight.");
        session.PreviewBuilding(green);
        session.ClosePrompt();
        await Frames();
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0x2a7849),
            "Valid building placement must show green at the cursor, without a yellow highlight.");
        session.Game.Map.SetBuildingFootprint(green, new(areaSpot.X, areaSpot.Y, green.Width, green.Height));
        session.Game.Touch();
        session.MoveBuildingPlacement(areaSpot);
        host.Map.Invalidate(true);
        await Frames();
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0x8c2d37),
            "Invalid building placement must show red at the cursor, without a yellow highlight.");
        if (!headless) Require(host.Map.AreasDrawn == 0,
            "An invalid placement must not draw preview or underlying-building area effects.");
        session.CancelPreview();
        session.ClosePrompt();
        session.PlaceCursor(areaSpot);
        host.Map.Invalidate(true);
        session.CenterOn(areaSpot);
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn >= 1, "Pollution area circles were not drawn.");
        foreach (var p in session.Game.Map.BuildingFootprintAt(areaSpot.X, areaSpot.Y).Cells())
        {
            var screen = new Pos(p.X - session.CameraX, p.Y - session.CameraY);
            Require(host.Map.Grid.VisualAt(session, screen.X, screen.Y)?.Glyph.Contains('\n') == true,
                "Larger building cells are not filled with ASCII art.");
        }
        session.PlaceCursor(new Pos(areaSpot.X + green.Width, areaSpot.Y));
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn == 0, "Unselected buildings still show range circles.");
        session.PlaceCursor(new Pos(areaSpot.X + green.Width - 1, areaSpot.Y + green.Height - 1));
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn >= 1, "Selecting a non-anchor footprint cell did not show its circles.");
        var inspectionCell = session.Cursor;
        var inspectionPosition = new Vector2((inspectionCell.X - session.CameraX + 0.5f) * TerminalGrid.CellWidth,
            (inspectionCell.Y - session.CameraY + 0.5f) * TerminalGrid.CellHeight);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right, Pressed = true, Position = inspectionPosition,
        });
        await Frames();
        Require(session.Prompt?.Title == BuildingReport.Title &&
            session.Prompt.Text.Contains(green.Name.ToUpperInvariant(), StringComparison.Ordinal),
            "Right-clicking any building footprint cell must open its status dialog.");
        var buildingStatusDialog = Descendants(host).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
        Require(Descendants(buildingStatusDialog).OfType<GridContainer>().Any(table =>
            table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)),
            "Building status must render its details in terminal tables.");
        var buildingActions = Descendants(buildingStatusDialog).OfType<MnemonicButton>().ToArray();
        Require(buildingActions.Length == 2 && buildingActions.All(button => button.Size.X >= 96 &&
            button.Size.Y >= 36 &&
            button.GetThemeFont("font").GetStringSize(button.Text, fontSize: button.GetThemeFontSize("font_size")).X <=
                button.Size.X - button.GetThemeStylebox("normal").GetContentMargin(Side.Left) -
                button.GetThemeStylebox("normal").GetContentMargin(Side.Right)),
            "Building Status footer actions must have readable labels, not collapse to narrow outlines.");
        await KeyEvent(Key.Escape);
        int zoomSkips = host.MusicSkips;
        for (int i = 0; i < 12; i++)
        {
            host.Map.ZoomBy(i % 2 == 0 ? -1 : 1);
            session.ScrollChars(i % 2 == 0 ? 3 : -3, 1);
            await Frames();
            VerifyMapPadding(host);
        }
        Require(host.MusicSkips == zoomSkips, "Music skipped while zooming and scrolling.");
        if (File.Exists(session.SavePath)) File.Delete(session.SavePath);
    }

    /// <summary>Drives the effect pipeline through the real scene: build, demolish, toggle, drain. Draw checks only apply when the engine draws.</summary>
    private static async Task VerifyEffects(Main host, Func<Task> frames, Func<Key, bool, uint, bool, Key, Task> key)
    {
        var session = host.Session;
        var map = session.Game.Map;
        session.ClosePrompt();
        session.SetZoom(0);
        var openCell = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => p.X >= 2 && p.Y >= 2 && p.X < map.Width - 2 && p.Y < map.Height - 2 &&
                session.Game.Network.IsServed(map.Index(p.X, p.Y)) && session.Game.CanPlaceRoad(p.X, p.Y));
        session.CenterOn(openCell);
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
        OpenGameMenu(host);
        await frames();
        Require(session.Prompt?.Title == "GAME MENU" &&
            !session.Prompt.Choices.Any(c => c.Label is "Effects" or "Celebrations"),
            "Effects must remain keyboard-accessible without Effects/Celebrations menu rows.");
        await key(Key.Escape, false, 0, false, Key.None);
        Drain();
    }

    private static void VerifyMapCenteredDialog(Main host, Control dialog)
    {
        var map = host.Map.GetParent<Control>().GetGlobalRect();
        var expected = map.GetCenter() - dialog.Size / 2;
        expected.X = Math.Clamp(expected.X, map.Position.X,
            Math.Max(map.Position.X, host.GetGlobalRect().End.X - dialog.Size.X));
        expected.Y = Math.Clamp(expected.Y, map.Position.Y,
            Math.Max(map.Position.Y, host.GetGlobalRect().End.Y - dialog.Size.Y));
        Require(dialog.GlobalPosition.DistanceTo(expected) < 1,
            $"Regular dialogs must center on the map while remaining on screen: {dialog.GlobalPosition} vs {expected}.");
    }

    private static async Task VerifyConstructionCatalog(Main host, Func<Task> frames,
        Func<Key, bool, uint, bool, Key, bool, Task> key)
    {
        var session = host.Session;
        var rail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar");
        var icons = Descendants(rail).OfType<Button>().ToArray();
        Require(icons.Length == TermCity.Core.Buildings.ServiceKinds.All.Count + 1 &&
            rail.GetGlobalRect().End.X <= host.Map.GlobalPosition.X,
            "Construction rail must show roads and every service category to the left of the map.");
        Require(icons[0].GetChild<ConstructionIcon>(0).Glyph ==
            session.Game.Map.Content.Roads.OrderByDescending(r => r.Rank).First().GlyphFor(5),
            "Road category must use a straight road icon rather than a cross.");
        Require(Descendants(host).OfType<CitySplit>().Single().MouseDefaultCursorShape == Control.CursorShape.Arrow,
            "The split container must not propagate a resize cursor over the construction rail.");
        Require(icons.All(icon => icon.GetParent() is PanelContainer and not TerminalFrame &&
            icon.GetParent<Control>().GetThemeStylebox("panel") is StyleBoxFlat iconBorder &&
            iconBorder.BorderColor == Colors.White &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => iconBorder.GetBorderWidth(side) == 1) &&
            icon.Position == new Vector2(Main.IconPadding, Main.IconPadding)),
            "Every construction icon must have exactly one white outline.");
        Require(icons.All(icon => Math.Abs(icon.GetParent<Control>().Size.X - icon.Size.X -
            icon.Position.X * 2) < 1) &&
            icons.All(icon => Math.Abs(icon.GetParent<Control>().Size.Y - icon.Size.Y -
                icon.Position.Y * 2) < 1),
            "The icon buttons must have compact, equal insets on all sides.");
        var outer = rail.GetParent<TerminalFrame>();
        var firstIcon = icons[0].GetParent<PanelContainer>();
        var innerBorder = new Rect2(outer.GlobalPosition + outer.InnerBorderBounds.Position,
            outer.InnerBorderBounds.Size);
        float leftGap = firstIcon.GlobalPosition.X + 0.5f - innerBorder.Position.X;
        float rightGap = innerBorder.End.X - (firstIcon.GetGlobalRect().End.X - 0.5f);
        float topGap = firstIcon.GlobalPosition.Y + 0.5f - innerBorder.Position.Y;
        float doubleBorderGap = outer.JoinRight ? outer.BorderBounds.End.X + 2.5f - outer.InnerBorderBounds.End.X
            : outer.Size.X - 0.5f - outer.InnerBorderBounds.End.X;
        Require(Math.Abs(leftGap - rightGap) < 0.01 && Math.Abs(leftGap - topGap) < 0.01 &&
            Math.Abs(leftGap - doubleBorderGap) < 0.01 &&
            icons.All(icon => Math.Abs(icon.Size.X - icon.Size.Y) < 0.01),
            $"Rail content must have equal visible border insets and square icon buttons: left={leftGap}, right={rightGap}, top={topGap}.");
        Require(Math.Abs(innerBorder.End.Y - (rail.GetGlobalRect().End.Y - 0.5f) - doubleBorderGap) < 0.01 &&
            icons.Zip(icons.Skip(1)).All(pair =>
                Math.Abs(pair.Second.GetParent<Control>().GlobalPosition.Y + 0.5f -
                    (pair.First.GetParent<Control>().GetGlobalRect().End.Y - 0.5f) - doubleBorderGap) < 0.01),
            "Bottom content inset and gaps between icon outlines must match the double-border line spacing.");
        Require(rail.VerticalScrollMode == ScrollContainer.ScrollMode.Disabled &&
            !rail.GetVScrollBar().Visible &&
            icons.All(icon => icon.GetParent<Control>().GetGlobalRect().End.Y <= rail.GetGlobalRect().End.Y),
            "All construction icons must fit without a vertical scrollbar.");
        Require(Math.Abs(rail.Size.X - icons.Max(icon => icon.GetParent<Control>().Size.X)) < 1 &&
            Math.Abs(innerBorder.Size.X - firstIcon.Size.X + 1 - Main.RailPadding * 2) < 0.01,
            "Construction rail must fit its icons and compact visible border insets without extra horizontal expansion.");
        Require(icons.All(icon => icon.Size.X >= 40 && icon.Size.Y >= 40 &&
            icon.GetThemeFontSize("font_size") == 24),
            "Construction icons must use enlarged buttons and glyphs.");
        Require(icons.Select(icon => icon.GetChild<ConstructionIcon>(0).Kind)
            .SequenceEqual(new[] { ServiceKind.None }.Concat(ServiceKinds.All)),
            "Construction artwork must match its service category.");
        icons[0].EmitSignal(Control.SignalName.MouseEntered);
        Require(ConstructionIcon.WiggleAngleAt(0.25f) < 0 &&
            ConstructionIcon.WiggleAngleAt(0.5f) > 0 &&
            ConstructionIcon.WiggleAngleAt(0.625f) > 0 &&
            ConstructionIcon.WiggleAngleAt(0.625f) < ConstructionIcon.WiggleAngleAt(0.5f) &&
            ConstructionIcon.WiggleAngleAt(0.75f) == 0,
            "Hover must tilt left, right, then return upright in three quarter-second motions.");
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow == 1,
            "Hovering a construction icon must start its color glow.");
        await host.ToSignal(host.GetTree().CreateTimer(0.75), SceneTreeTimer.SignalName.Timeout);
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow < 0.01,
            "Construction icon glow must fade rather than remaining highlighted.");
        await host.ToSignal(host.GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
        Require(icons[0].GetChild<ConstructionIcon>(0).WiggleAngle == 0,
            "Hover animation must stop upright without moving the icon's box.");
        int money = session.Game.Money;
        Button Add() => Descendants(host).OfType<Button>().Single(n => n.Name == "ConstructionAdd");
        Button SelectedBuilding() => Descendants(host).OfType<VBoxContainer>()
            .Single(n => n.Name == "ConstructionTypes").GetChildren().OfType<Button>().Single(b => b.ButtonPressed);
        Button Type(string name) => Descendants(host).OfType<VBoxContainer>()
            .Single(n => n.Name == "ConstructionTypes").GetChildren().OfType<Button>()
            .Single(n => n.Text.EndsWith(name, StringComparison.Ordinal));
        icons[0].EmitSignal(Button.SignalName.Pressed);
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow == 1,
            "Clicking a construction icon must restart its glow.");
        await frames();
        var dialog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        VerifyDialogButtonBorders(dialog);
        var cancelButton = Descendants(dialog).OfType<Button>().Single(n => n.Name == "ConstructionCancel");
        Require(Math.Abs(cancelButton.Size.X - Add().Size.X) < 0.01 &&
            cancelButton.Alignment == HorizontalAlignment.Center,
            "Construction submenu Cancel must be centered and match the Add button's width.");
        var catalogLayout = Descendants(dialog).OfType<HBoxContainer>().Single(n => n.Name == "ConstructionLayout");
        float contentBottom = Descendants(dialog).OfType<Label>()
            .Where(label => label.IsVisibleInTree() && label.GetParent() == catalogLayout.GetParent())
            .Select(label => label.GetGlobalRect().End.Y).Append(catalogLayout.GetGlobalRect().End.Y).Max();
        Require(Math.Abs(Add().GlobalPosition.Y - contentBottom -
            (dialog.GetGlobalRect().End.Y - Add().GetGlobalRect().End.Y)) < 1,
            $"Construction action spacing: top={Add().GlobalPosition.Y - contentBottom}, " +
            $"bottom={dialog.GetGlobalRect().End.Y - Add().GetGlobalRect().End.Y}.");
        Require(dialog.GlobalPosition == host.Map.GetParent<Control>().GlobalPosition &&
            !Descendants(dialog).Any(n => n.Name == "ConstructionHeader") &&
            Descendants(dialog).OfType<Button>().Where(b => b.GetParent().Name == "ConstructionTypes")
                .All(b => b.GetThemeFontSize("font_size") == 22 &&
                    b.GetChild<Label>(0).GetThemeFontSize("font_size") == 30),
            "Catalogs must anchor at the map with larger choices and no duplicate building subheader.");
        Require(dialog is TerminalFrame { JoinAdjacent: false } &&
            Descendants(dialog).OfType<TerminalFrame>().All(frame => !frame.JoinAdjacent &&
                !frame.JoinLeft && !frame.JoinRight && !frame.JoinTop && !frame.JoinBottom),
            "Construction submenu headers must retain closed double-white borders without accidental joins.");
        Require(dialog.Position.X >= 0 && dialog.Position.Y >= 0 &&
            dialog.GetGlobalRect().End.X <= host.Size.X && dialog.GetGlobalRect().End.Y <= host.Size.Y,
            $"Construction catalog must fit the window: dialog={dialog.GetGlobalRect()}, window={host.Size}.");
        Require(session.Preview is null && Type(DefaultRoads.KingsRoadName) is not null &&
            Add().GetThemeStylebox("normal") is StyleBoxFlat { BgColor: var gray } &&
            gray.R == gray.G && gray.G == gray.B &&
            Add().GetThemeColor("font_color").G > Add().GetThemeColor("font_color").R,
            "Road catalog must list road types with green Add text on gray, without starting placement.");
        Require(SelectedBuilding().Text.Contains(DefaultRoads.TrackName, StringComparison.Ordinal),
            "The first road must immediately display its information.");
        var shield = dialog.GetParent<Control>();
        var policeIcon = icons.Single(b => b.Name == CityReport.ServiceName(ServiceKind.Police));
        shield.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            GlobalPosition = policeIcon.GetGlobalRect().GetCenter(),
        });
        Require(policeIcon.GetChild<ConstructionIcon>(0).Glow == 1,
            "Hover highlighting must remain available through the construction modal shield.");
        shield.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true,
            GlobalPosition = policeIcon.GetGlobalRect().GetCenter(),
        });
        await frames();
        Require(session.Prompt?.Title == CityReport.ServiceName(ServiceKind.Police) &&
            SelectedBuilding().Text.Contains(session.Game.Map.Content.Buildings
                .Where(b => b.PlayerPlaceable && b.Service == ServiceKind.Police)
                .OrderBy(CityProgression.RequiredPopulation).ThenBy(b => b.Cost).First().Name,
                StringComparison.Ordinal),
            "Clicking another sidebar icon through the modal shield must switch catalogs.");
        await key(Key.Escape, false, 0, false, Key.None, false);
        Require(session.Prompt is null, "Switching catalogs must not stack previously opened categories.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        policeIcon.EmitSignal(Button.SignalName.Pressed);
        await frames();
        Require(session.Prompt?.Title == CityReport.ServiceName(ServiceKind.Police),
            "Sidebar buttons must allow switching an open construction catalog.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        Type(DefaultRoads.CobbledName).EmitSignal(Button.SignalName.Pressed);
        Require(SelectedBuilding().Text.Contains(DefaultRoads.CobbledName, StringComparison.Ordinal) &&
            Descendants(host).OfType<Label>().Single(n => n.Name == "ConstructionDescription").Text ==
                session.Game.Map.Content.Roads.Single(r => r.Name == DefaultRoads.CobbledName).Description,
            "Catalog selection must update the icon/name header and full description.");
        var details = Descendants(host).OfType<BoxContainer>().Single(n => n.Name == "ConstructionDetails");
        Require(details.Vertical &&
            details.GetChild<Control>(0).GetGlobalRect().End.Y <= details.GetChild<Control>(1).GlobalPosition.Y &&
            Descendants(details.GetChild(0)).OfType<Label>().Any(l => l.Name == "ConstructionDescription") &&
            Descendants(details.GetChild(1)).OfType<Label>().Any(l => l.Text == "Cost") &&
            Math.Abs(details.GetChild<Control>(0).Size.X - details.Size.X) < 1 &&
            Math.Abs(details.GetChild<Control>(1).Size.X - details.Size.X) < 1,
            "Description must precede statistics, with both sections filling the available width.");
        Add().EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is null && session.RoadToolActive &&
            session.Preview?.Road?.Name == DefaultRoads.CobbledName && session.Game.Money == money,
            "Add must enter road placement without charging or building.");
        session.CancelPreview();
        await frames();
        Require(session.Prompt?.Title == "Roads", "Cancelling placement must restore the catalog.");
        Descendants(host).OfType<Button>().Single(b => b.Text == "Cancel").EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is null && session.Preview is null, "Cancel did not close the catalog.");
        foreach (var kind in new[] { ServiceKind.Power, ServiceKind.Water, ServiceKind.Granary })
        {
            icons.Single(button => button.Name == CityReport.ServiceName(kind)).EmitSignal(Button.SignalName.Pressed);
            await frames();
            var capacityChoices = Descendants(host).OfType<VBoxContainer>().Single(node => node.Name == "ConstructionTypes");
            foreach (var building in session.Game.Map.Content.Buildings.Where(type =>
                type.PlayerPlaceable && type.Service == kind && type.Capacity > 0))
            {
                var option = capacityChoices.GetChildren().OfType<Button>().Single(button => button.Text == building.Name);
                var capacityLabel = option.GetChildren().OfType<Label>().Single(label => label.Name == "BuildingCapacity");
                Require(capacityLabel.Text == ChoiceHelpContent.CapacityText(building) &&
                    capacityLabel.Size.X >= capacityLabel.GetThemeFont("font").GetStringSize(capacityLabel.Text,
                        fontSize: capacityLabel.GetThemeFontSize("font_size")).X,
                    "Building capacities and meaningful units must be visible directly in the choice list.");
                option.EmitSignal(Button.SignalName.Pressed);
                await frames();
                var coverageStatus = Descendants(host).OfType<Label>().Single(label => label.Name == "PeasantStatus");
                Require(coverageStatus.IsVisibleInTree() &&
                    coverageStatus.Text == SidebarReport.ServiceStatus(session.Game, kind).Text &&
                    coverageStatus.HorizontalAlignment == HorizontalAlignment.Right &&
                    Math.Abs(coverageStatus.GetGlobalRect().End.X - host.Size.X) < 1,
                    "Building submenus must show relevant service status at the bottom right.");
                var capacityStats = Descendants(host).OfType<GridContainer>().Single(node => node.Name == "ConstructionStats");
                var capacityLabels = capacityStats.GetChildren().OfType<PanelContainer>()
                    .Select(cell => cell.GetChild<Label>(0)).ToArray();
                Require(capacityLabels[4].Text == "Capacity" &&
                    capacityLabels[5].Text == ChoiceHelpContent.CapacityText(building),
                    "Capacity must appear immediately after cost/upkeep in every relevant building's details.");
            }
            Descendants(host).OfType<Button>().Single(button => button.Text == "Cancel").EmitSignal(Button.SignalName.Pressed);
            await frames();
        }
        icons.Single(b => b.Name == CityReport.ServiceName(TermCity.Core.Buildings.ServiceKind.Police))
            .EmitSignal(Button.SignalName.Pressed);
        await frames();
        var types = Descendants(host).OfType<VBoxContainer>().Single(n => n.Name == "ConstructionTypes");
        var typeButtons = types.GetChildren().OfType<Button>().ToArray();
        Require(types.GetParent() is PanelContainer { Name: var typesBoxName } &&
            typesBoxName == "ConstructionTypesBox",
            "All road/building choices must share one enclosing single-white border.");
        Require(typeButtons.All(button => button.GetThemeFontSize("font_size") == 22 &&
            button.Size.X >= button.GetThemeFont("font").GetStringSize(button.Text, fontSize: 22).X + 56),
            "Building names must use the enlarged font and a column wide enough to show every full name.");
        var police = session.Game.Map.Content.Buildings
            .Where(b => b.PlayerPlaceable && b.Service == TermCity.Core.Buildings.ServiceKind.Police)
            .OrderBy(CityProgression.RequiredPopulation).ThenBy(b => b.Cost).ToArray();
        Require(typeButtons.Select(b => b.Text).SequenceEqual(police.Select(b => b.Name)) &&
            typeButtons.Zip(typeButtons.Skip(1)).All(pair => pair.First.GlobalPosition.Y < pair.Second.GlobalPosition.Y) &&
            types.GlobalPosition.X < statsPosition() &&
            SelectedBuilding().Text.Contains(police[0].Name, StringComparison.Ordinal),
            "Buildings must appear vertically on the left in progression/cost order, with the first selected.");
        var stats = Descendants(host).OfType<GridContainer>().Single(n => n.Name == "ConstructionStats");
        float statsPosition() => Descendants(host).OfType<GridContainer>()
            .Single(n => n.Name == "ConstructionStats").GlobalPosition.X;
        var statLabels = stats.GetChildren().OfType<PanelContainer>().Select(cell =>
            cell.GetChild(0) is Label label ? label : Descendants(cell).OfType<Label>().Last()).ToArray();
        var labels = statLabels.Where((_, index) => index % 2 == 0).ToArray();
        int footprintIndex = Array.FindIndex(statLabels, label => label.Text == "Footprint");
        Require(labels[0].Text == "Cost" && labels[1].Text == "Upkeep" &&
            labels.All(label => label.Text is not ("Does" or "Placement" or "Area icons")) &&
            statLabels[1].Text == Fmt.Money(police[0].Cost) &&
            footprintIndex >= 0 &&
            statLabels[footprintIndex + 1].Text == $"{police[0].Width} x {police[0].Height}" &&
            statLabels.Where((_, index) => index % 2 == 1).All(label =>
                !char.IsLetter(label.Text[0]) || char.IsUpper(label.Text[0])),
            "Details must omit redundant rows, simplify cost/footprint and capitalize values.");
        float longestLabel = labels.Max(label => label.GetThemeFont("font").GetStringSize(label.Text,
            fontSize: label.GetThemeFontSize("font_size")).X);
        Require(labels.All(label => label.Size.X >= longestLabel &&
            label.AutowrapMode == TextServer.AutowrapMode.Off),
            "Statistics labels must keep the full width of their longest string without wrapping.");
        Require(statLabels.Where((_, index) => index % 2 == 1)
            .Where(label => label.GetParent() is PanelContainer)
            .All(label => label.Size.X >= 80), "Statistics values must retain a readable minimum column width.");
        Require(statLabels.Where(label => label.GetParent() is PanelContainer).All(label => label.VerticalAlignment == VerticalAlignment.Top &&
            label.SizeFlagsVertical == Control.SizeFlags.Fill &&
            Math.Abs(label.Position.Y - 3) < 0.01),
            "Statistics keys and values must align at the top of their table cells.");
        Require(stats.GetChildren().OfType<PanelContainer>().Select((cell, index) => (cell, index)).All(item =>
            item.cell.GetThemeStylebox("panel") is StyleBoxFlat style && style.BorderColor == Colors.White &&
            style.BorderWidthLeft == 0 &&
            style.BorderWidthTop == (item.index < 2 ? 1 : 0) &&
            style.BorderWidthRight == 1 && style.BorderWidthBottom == 1 && style.ContentMarginTop == 3),
            "Details must draw each shared edge once, with uniform one-pixel white lines.");
        Require(!Descendants(host).Any(n => n.Name == "ConstructionStatsHeader" ||
            n.Name == "ConstructionDescriptionHeader"),
            "Description and statistics/effects headings must be omitted.");
        var descriptionBox = Descendants(host).OfType<PanelContainer>()
            .Single(n => n.Name == "ConstructionDescriptionBox");
        Require(descriptionBox.Size.Y >= typeButtons[0].Size.Y + Main.RailPadding &&
            Descendants(descriptionBox).OfType<Label>().Single(n => n.Name == "ConstructionDescription")
                .GetThemeFontSize("font_size") == 20,
            "Description text must use 20 points and its box must reach at least the first name row's bottom.");
        Require(Descendants(descriptionBox).Any(n => n.Name == "ConstructionDescription") &&
            !Descendants(descriptionBox).OfType<PanelContainer>().Any() &&
            Descendants(descriptionBox).OfType<Label>().Single(n => n.Name == "ConstructionDescription")
                .GetParent() is MarginContainer,
            "The description must have only its outer box, without a header or text-body border.");
        foreach (var panel in Descendants(host).OfType<PanelContainer>().Where(panel =>
            panel.Name == "ConstructionTypesBox" || panel.Name == "ConstructionDescriptionBox" ||
            panel.GetChildren().OfType<Label>().Any(label =>
                label.Name == "ConstructionCategoryHeader")))
            Require(panel.GetThemeStylebox("panel") is StyleBoxFlat style &&
                style.BorderColor == Colors.White &&
                style.BorderWidthLeft == (panel.Name == "ConstructionDescriptionBox" ? 0 : 1) &&
                new[] { Side.Top, Side.Right, Side.Bottom }.All(side => style.GetBorderWidth(side) == 1),
                "Catalog boxes must use single lines, sharing the name list's divider on the left.");
        var typesBox = types.GetParent<Control>();
        Require(Math.Abs(typesBox.GetGlobalRect().End.X - descriptionBox.GlobalPosition.X) < 0.01 &&
            Math.Abs(typesBox.GlobalPosition.Y - descriptionBox.GlobalPosition.Y) < 0.01 &&
            Math.Abs(typesBox.GetGlobalRect().End.Y - stats.GetGlobalRect().End.Y) < 0.01,
            "Name and detail sections must join at one continuous divider rather than sit side by side.");
        foreach (var box in new[] { types.GetParent<Control>(), descriptionBox })
        {
            var child = box.GetChild<Control>(0);
            Require(Math.Abs(child.Position.X - Main.RailPadding) < 0.01 &&
                Math.Abs(child.Position.Y - Main.RailPadding) < 0.01 &&
                Math.Abs(box.Size.X - child.GetRect().End.X - Main.RailPadding) < 0.01 &&
                Math.Abs(box.Size.Y - child.GetRect().End.Y - Main.RailPadding) < 0.01,
                "Catalog enclosing borders must use the same even four-pixel spacing as the icon rail.");
        }
        Require(typeButtons.Zip(typeButtons.Skip(1)).All(pair =>
            Math.Abs(pair.Second.Position.Y - pair.First.GetRect().End.Y - (Main.RailPadding - 1)) < 0.01),
            "Stacked name borders must have four-pixel line-to-line spacing.");
        Type("Gaol").EmitSignal(Button.SignalName.Pressed);
        Require(Add().Disabled && Descendants(host).OfType<Label>().Any(l => l.Text.Contains("Market Town (", StringComparison.Ordinal)) &&
            Type("Gaol").GetChildren().OfType<Control>().Any(control => control.Name == "BuildingLock"),
            "Locked buildings must display their unlock threshold and disable Add.");
        Type("Watch House").EmitSignal(Button.SignalName.Pressed);
        Require(Add().Disabled && Type("Watch House").GetChildren().OfType<Control>()
            .Any(control => control.Name == "BuildingLock"),
            "Starter sheriff buildings must remain locked until Hamlet.");
        Add().EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is not null && !session.BuildingToolActive && session.Game.Money == money,
            "Locked buildings must not start placement even through a forced Add activation.");
        await frames();
        await key(Key.Escape, false, 0, false, Key.None, false);
        Require(session.Prompt is null && session.Preview is null, "Escape did not close the catalog.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        var outside = Vector2.One;
        Require(!Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog")
            .GetGlobalRect().HasPoint(outside), "Outside-click test point is inside the catalog.");
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = pressed, Position = outside, GlobalPosition = outside,
            }, true);
        await frames();
        Require(session.Prompt is null && session.Preview is null && session.Game.Money == money,
            $"Clicking outside the catalog must close it without a background action: prompt={session.Prompt?.Title}, preview={session.Preview?.Name}, gold={session.Game.Money}/{money}.");
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        await frames();
        Require(!Descendants(host).Any(n => n.Name == "ConstructionCatalog") &&
            session.Prompt?.Choices.Any(c => c.Cells?[0] == "Fill area") == true,
            "The existing Roads area menu must remain distinct from the construction catalog.");
        session.ClosePrompt();
        float? sharedNameWidth = null;
        foreach (var category in icons)
        {
            category.EmitSignal(Button.SignalName.Pressed);
            await frames();
            var choices = Descendants(host).OfType<VBoxContainer>().Single(n => n.Name == "ConstructionTypes")
                .GetChildren().OfType<Button>().ToArray();
            sharedNameWidth ??= choices[0].Size.X;
            Require(choices.All(choice => Math.Abs(choice.Size.X - sharedNameWidth.Value) < 0.01),
                "Every building and road name cell must have the same width across all catalogs.");
            foreach (var choice in choices)
            {
                choice.EmitSignal(Button.SignalName.Pressed);
                await frames();
                Require(choice.Size.X >= choice.GetThemeFont("font")
                    .GetStringSize(choice.Text, fontSize: choice.GetThemeFontSize("font_size")).X + 56,
                    $"Construction name must fit without clipping: {choice.Text}.");
                var cells = Descendants(host).OfType<GridContainer>().Single(n => n.Name == "ConstructionStats")
                    .GetChildren().OfType<PanelContainer>().Select(cell => cell.HasMeta("details_value")
                        ? cell.GetMeta("details_value").AsString() : cell.GetChild<Label>(0).Text).ToArray();
                var selectedType = session.Game.Map.Content.Buildings.FirstOrDefault(building => building.Name == choice.Text);
                bool hasAreaEffect = selectedType is null || AreaOfEffect.ForBuilding(selectedType, new Pos(0, 0)).Count > 0;
                Require(!cells.Where((_, index) => index % 2 == 0).Contains("Area of Effect") &&
                    !Descendants(host).Any(node => node.Name == "ConstructionEffectIcon") &&
                    cells.Where((_, index) => index % 2 == 0).Count(text => text == "Effect") == (hasAreaEffect ? 1 : 0),
                    "Buildings must have an Effect row only when they have an actual area effect.");
                int effectIndex = Array.FindIndex(cells, text => text == "Effect");
                if (selectedType is not null)
                    foreach (var area in AreaOfEffect.ForBuilding(selectedType, new Pos(0, 0)))
                    {
                        Require(cells[effectIndex + 1].Contains(
                            $"radius {area.Radius} cells. Area: {area.Radius * 2 + 1} x {area.Radius * 2 + 1} cells",
                            StringComparison.Ordinal),
                            "The single Effect row must include every effect's full cell-width and height.");
                    }
                Require(Descendants(host).OfType<Label>().Single(label => label.Name == "ConstructionDescription").Text ==
                    (selectedType?.Description ?? session.Game.Map.Content.Roads.Single(road => road.Name == choice.Text).Description),
                    "Area effects must not be appended to the description.");
                Require(!cells.Contains("Operation") && cells.All(text => !text.Contains("at current funding", StringComparison.Ordinal)),
                    "Catalog details must omit the Operation row and upkeep funding suffix.");
                Require(cells[0] == "Cost" && cells[2] == "Upkeep" &&
                    cells[1].EndsWith('g') && cells[1][..^1].All(c => char.IsDigit(c) || c == ',') &&
                    cells.Where((_, index) => index % 2 == 0)
                        .All(text => text is not ("Does" or "Placement" or "Area icons")) &&
                    cells.Where((_, index) => index % 2 == 1)
                        .All(text => text.Length == 0 || !char.IsLetter(text[0]) || char.IsUpper(text[0])),
                    $"Every construction details table must use simplified rows and capitalized values: {choice.Text}.");
                for (int i = 0; i < cells.Length; i += 2)
                {
                    if (category == icons[0])
                        Require(cells[i] != "Needs", "Road details must omit the Needs row.");
                    if (cells[i] == "Footprint")
                        Require(!cells[i + 1].Contains("cells", StringComparison.Ordinal),
                            "Footprints must show dimensions without cells.");
                }
            }
            session.ClosePrompt();
        }
        await key(Key.F6, false, 0, false, Key.None, false);
        Require(session.Prompt?.Title == "TermCity guide", "F6 must open the guide.");
        VerifyMapCenteredDialog(host,
            Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog" && n.IsVisibleInTree()));
        await key(Key.Escape, false, 0, false, Key.None, false);
    }

    public static void Run(Main host)
    {
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        var session = host.Session;
        Require(!session.HasCursor && session.Selection is null && session.Anchor is null &&
            Descendants(host).OfType<Label>().Single(l => l.Name == "Status").Text == "Press F1 for HELP",
            "Startup must have no selected cell and show the F1 help hint.");
        host.Map.RefreshCells();
        for (int y = 0; y < host.Map.Grid.Rows; y++)
            for (int x = 0; x < host.Map.Grid.Columns; x++)
                Require(host.Map.Grid.HighlightAt(session, x, y) is null, "Startup painted a cell highlight.");
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
        VerifyTerminalJoins(host);
        VerifyMapPadding(host);
        VerifySidebarTables(host);
        Require(host.Size == new Vector2(960, Main.MinimumWindowHeight), "Window resize did not reach the scene.");
        float minimapAspect = map.Height * 1.5f / map.Width;
        Require(host.Minimap.Size.Y + 0.01 >= host.Minimap.Size.X * minimapAspect,
            $"Minimap must fill sidebar width: minimap={host.Minimap.Size}, panel={host.Minimap.GetParent().GetParent().GetParent<Control>().Size}, " +
            $"window={host.Size}, minimum={Main.MinimumWindowHeight}.");
        Require(host.Map.Grid.Columns == (int)Math.Ceiling(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)Math.Ceiling(host.Map.Size.Y / TerminalGrid.CellHeight),
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
            (Key.F1, HelpContent.Title), (Key.F7, "Weekly Report"),
            (Key.F8, "Growth and road access"), (Key.Escape, "GAME MENU"),
        })
        {
            if (key == Key.Escape) OpenGameMenu(host);
            else Press(key);
            Require(session.Prompt?.Title == title, $"{key} did not open {title}.");
            bool debug = session.InputDebug;
            Press(Key.F12);
            Require(session.Prompt?.Title == title && session.InputDebug == debug, "Dialog allowed a background action.");
            if (session.Prompt is not null) Press(Key.Escape);
        }
        Press(Key.F6);
        Require(session.Prompt is { Title: "TermCity guide", Tabs.Count: 9, ActiveTab: 0 }, "F6 did not open the tabbed guide.");
        Require(session.Prompt!.Tabs!.Single(tab => tab.Title == "Milestones").Text.Contains("Great City", StringComparison.Ordinal),
            "Guide must include the new Milestones tab with every settlement size.");
        Press(Key.Right);
        Require(session.Prompt?.ActiveTab == 1 && session.Prompt.Tabs![1].Title == "Zones", "Right arrow did not switch guide tab.");
        Press(Key.Left);
        Press(Key.Left);
        Require(session.Prompt?.ActiveTab == 8 && session.Prompt.Tabs![8].Title == "Glossary",
            "Left arrow did not wrap to the glossary.");
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
        OpenGameMenu(host);
        Require(session.Prompt?.Title == "GAME MENU", "Esc did not open the game menu with an active selection.");
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
        host._UnhandledInput(new InputEventKey { Keycode = Key.Z, Pressed = true,
            CtrlPressed = !OS.HasFeature("macos"), MetaPressed = OS.HasFeature("macos") });
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
        Require(session.Prompt?.Title == "Service buildings" && (session.Prompt.Text == "Choose a building, then click or press Enter to place it." ||
            session.Prompt.Text == "No service buildings are registered."), "Building menu was not shown or explained.");
        Press(Key.Escape);
        Require(session.Prompt?.Title == "Area menu", "Service cancellation did not restore the area menu.");
        Press(Key.Escape);

        string quickSave = session.SavePath;
        Require(session.QuickSave(), "Quick-save failed.");
        string saved = SaveGameStore.Serialize(session.Game);
        Press(Key.R);
        Press(Key.F9);
        Require(session.Prompt?.Title == "Confirm load city", "Quick-load discarded unsaved changes.");
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
        var road = map.RoadCells.Select(map.PosOf)
            .OrderBy(position => Math.Abs(position.X - map.Width / 2) + Math.Abs(position.Y - map.Height / 2)).First();
        session.CenterOn(road);
        host.Map.RefreshCells();
        session.Game.Paused = false;
        for (int i = 0; i < 120; i++) host.Director.Update(0.25);
        var life = host.Effects.Effects.OfType<AmbientLife>().Single();
        Require(life.Cars > 0 && life.Birds > 0, "Pause check needs active traffic and birds.");
        EffectSprite[] Actors() => host.Effects.Sprites.Where(sprite =>
            sprite.Color == EffectGlyphs.CarColor || sprite.Color == EffectGlyphs.PersonColor ||
            sprite.Color == EffectGlyphs.BirdColor).ToArray();
        var actors = Actors();
        host._UnhandledInput(new InputEventKey { Keycode = Key.P, Pressed = true });
        Require(session.Game.Paused, "P did not pause the game for the ambient check.");
        for (int i = 0; i < 30; i++)
        {
            host._Process(0.1);
            Require(life.TrafficAndBirdsPaused && actors.SequenceEqual(Actors()),
                "Traffic or birds moved, spawned or flapped while the game was paused.");
        }
        host._UnhandledInput(new InputEventKey { Keycode = Key.P, Pressed = true });
        host.Director.Update(0.1);
        Require(!session.Game.Paused && !life.TrafficAndBirdsPaused && !actors.SequenceEqual(Actors()),
            "Traffic and birds did not resume with the game.");
        session.Game.Paused = true;
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        double seconds = grid.AnimationSeconds;
        host._Process(TerminalGrid.BeatSeconds);
        Require(seconds == grid.AnimationSeconds, "Unfocused animation continued.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.RefreshCells();
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void VerifyMapPadding(Main host)
    {
        host.Map.RefreshCells();
        var frame = host.Map.GetParent<TerminalFrame>();
        var inner = frame.InnerBorderBounds;
        var padding = new[]
        {
            host.Map.Position.X - inner.Position.X,
            host.Map.Position.Y - inner.Position.Y,
            inner.End.X - host.Map.Position.X - host.Map.Size.X,
            inner.End.Y - host.Map.Position.Y - host.Map.Size.Y,
        };
        Require(frame.VisibleContentPadding == TerminalFrame.Inset - 4 &&
            padding.All(value => Math.Abs(value - (TerminalFrame.Inset - 4.5f)) < 0.01),
            $"Map padding must be equal relative to the visible inner borders: {string.Join(", ", padding)}.");
        float excessWidth = host.Map.Grid.Columns * host.Map.Grid.PixelWidth - host.Map.Size.X;
        float excessHeight = host.Map.Grid.Rows * host.Map.Grid.PixelHeight - host.Map.Size.Y;
        Require(excessWidth >= 0 && excessWidth < host.Map.Grid.PixelWidth &&
            excessHeight >= 0 && excessHeight < host.Map.Grid.PixelHeight,
            "The map buffer must fill its frame and clip partial edge cells.");
        var sidebar = Descendants(host).OfType<CityPanel>().Single();
        var sidebarFrame = sidebar.GetParent<TerminalFrame>();
        Require(sidebarFrame is TerminalFrame && sidebarFrame.Name == "SidebarFrame",
            "The sidebar must retain its enclosing double-white frame.");
        Require(Math.Abs(sidebarFrame.GetGlobalRect().Position.X - frame.GetGlobalRect().End.X - TerminalFrame.Inset) < 0.01,
            "The map/sidebar gap must match the internal map padding.");
        var sidebarPadding = new[]
        {
            sidebar.Position.X - sidebarFrame.InnerBorderBounds.Position.X,
            sidebar.Position.Y - sidebarFrame.InnerBorderBounds.Position.Y,
            sidebarFrame.InnerBorderBounds.End.X - sidebar.Position.X - sidebar.Size.X,
            sidebarFrame.InnerBorderBounds.End.Y - sidebar.Position.Y - sidebar.Size.Y,
        };
        var railFrame = Descendants(host).OfType<ScrollContainer>()
            .Single(control => control.Name == "ConstructionSidebar").GetParent<TerminalFrame>();
        Require(sidebarFrame.VisibleContentPadding == railFrame.VisibleContentPadding &&
            sidebarPadding.All(value => Math.Abs(value - (Main.RailPadding - 0.5f)) < 0.01),
            $"Right sidebar padding must be equal and match the construction rail: {string.Join(", ", sidebarPadding)}.");
        Require(frame.Size.X <= frame.GetParent<Control>().Size.X + 0.01 &&
            frame.Size.Y <= frame.GetParent<Control>().Size.Y + 0.01,
            "The fitted map frame exceeds its available space.");
    }

    private static void VerifyDialogButtonBorders(Control dialog)
    {
        foreach (var button in Descendants(dialog).OfType<Button>())
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            {
                Require(!button.Flat && button.GetThemeStylebox(state) is StyleBoxFlat style &&
                    style.BorderColor == Colors.White &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => style.GetBorderWidth(side) == 1),
                    $"Dialog button {button.Text} must have one white outline in its {state} state.");
            }
    }

    private static void VerifyTerminalJoins(Main host)
    {
        var map = host.Map.GetParent<TerminalFrame>();
        var rail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar")
            .GetParent<TerminalFrame>();
        var sidebar = Descendants(host).OfType<TerminalFrame>().Single(n => n.Name == "SidebarFrame");
        Require(rail.JoinAdjacent && rail.JoinRight && rail.JoinTop && map.JoinLeft &&
            map.JoinRight && sidebar.JoinLeft && map.JoinTop && sidebar.JoinTop,
            $"Adjacent gameplay frames must share borders: rail={rail.GetGlobalRect()} joins={rail.JoinRight}/{rail.JoinTop}, " +
            $"map={map.GetGlobalRect()} joins={map.JoinLeft}/{map.JoinRight}/{map.JoinTop}, " +
            $"sidebar={sidebar.GetGlobalRect()} joins={sidebar.JoinLeft}/{sidebar.JoinTop}; headers=" +
            string.Join("; ", Descendants(host).OfType<TerminalFrame>()
                .Where(f => f.VisibleVerticalContentPadding is not null).Select(f => f.GetGlobalRect().ToString())));
        Require(Math.Abs(rail.GlobalPosition.X + rail.BorderBounds.End.X -
            map.GlobalPosition.X - map.BorderBounds.Position.X) < 0.01 &&
            Math.Abs(map.GlobalPosition.X + map.BorderBounds.End.X -
            sidebar.GlobalPosition.X - sidebar.BorderBounds.Position.X) < 0.01,
            "Each divider must have one shared double border, not two neighboring border pairs.");
        float top = map.GlobalPosition.Y + map.BorderBounds.Position.Y;
        Require(Math.Abs(rail.GlobalPosition.Y + rail.BorderBounds.Position.Y - top) < 0.01 &&
            Math.Abs(sidebar.GlobalPosition.Y + sidebar.BorderBounds.Position.Y - top) < 0.01,
            "The horizontal border at three-way joins must stay continuous.");
        Require(!rail.JoinLeft && !sidebar.JoinRight &&
            rail.HorizontalEndpoints(rail.JoinTop).Left == rail.InnerBorderBounds.Position.X &&
            sidebar.HorizontalEndpoints(sidebar.JoinTop).Right == sidebar.InnerBorderBounds.End.X,
            "Window-edge junctions must leave the outer vertical line separate from the horizontal branches.");
    }

    private static void VerifySidebarTables(Main host)
    {
        var panel = Descendants(host).OfType<CityPanel>().Single();
        Require(!Descendants(panel).OfType<Label>().Any(label => label.Text is "GUIDE" or "ZONES" or "MINIMAP"),
            "The sidebar still includes a removed section.");
        Require(!Descendants(panel).Any(node => node.Name == "ZonesTable"),
            "The Zones table was not removed.");
        Require(!Descendants(panel).Any(node => node.Name == "MINIMAPHeaderCell") &&
            panel.Minimap.GetIndex() == 1 &&
            Descendants(panel).OfType<Label>().Any(label => label.Name == "ZoomIndicator"),
            "The minimap must appear without a header and retain its zoom controls.");
        var zoomFrame = Descendants(panel).OfType<PanelContainer>().Single(control => control.Name == "MinimapZoomFrame");
        Require(Descendants(zoomFrame).OfType<Label>().Single(label => label.Name == "ZoomIndicator")
            .GetThemeFontSize("font_size") == 20, "Zoom-level text must retain its enlarged 20-point size.");
        Require(zoomFrame is not TerminalFrame &&
            zoomFrame.GetThemeStylebox("panel") is StyleBoxFlat zoomStyle &&
            zoomStyle.BgColor == Colors.Black && zoomStyle.BorderColor == Colors.White &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                .All(side => zoomStyle.GetBorderWidth(side) == 1 && zoomStyle.GetContentMargin(side) == 1),
            "The minimap zoom section must have one white border on black with even padding.");
        Require(Descendants(zoomFrame).OfType<Button>().Count() == 2 &&
            Descendants(zoomFrame).OfType<Button>().All(button => button.CustomMinimumSize == Vector2.Zero &&
                button.GetThemeFontSize("font_size") == 22 &&
                button.Alignment == HorizontalAlignment.Center &&
                Math.Abs(button.Size.Y - button.GetThemeFont("font").GetHeight(22) - 4) < 1 &&
                Math.Abs(button.Size.X - button.GetThemeFont("font").GetStringSize(button.Text, fontSize: 22).X - 4) < 1 &&
                new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }.All(state =>
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                        .All(side => button.GetThemeStylebox(state).GetContentMargin(side) == 2))),
            "Zoom brackets must have tight equal padding on every side without forced extra space.");
        foreach (string title in new[] { "DEMAND", "CITY" })
        {
            var cell = Descendants(panel).OfType<PanelContainer>().Single(control => control.Name == title + "HeaderCell");
            Require(cell is TerminalFrame && cell.GetChild<Label>(0).Text == title &&
                cell.GetChild<Label>(0).GetThemeFontSize("font_size") == 22 &&
                cell.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var background } && background == Colors.Black,
                $"{title} does not have the shared double-white frame on a black background.");
        }
        Verify("CityTable", SidebarReport.CityRows(host.Session.Game));
        var city = Descendants(panel).OfType<GridContainer>().Single(control => control.Name == "CityTable");
        Require(city.GetGlobalRect().End.Y <= panel.GetGlobalRect().End.Y + 1 &&
            city.Size.X <= panel.Size.X && !panel.GetParent()!.GetChildren().OfType<ScrollContainer>().Any(),
            $"Sidebar must fit without scrolling: panel={panel.GetGlobalRect()}, city={city.GetGlobalRect()}, " +
            $"content minimum={panel.GetChild<Control>(0).GetCombinedMinimumSize()}, font={host.FontSize}.");
        foreach (var zone in Zones.Placeable)
        {
            var row = Descendants(panel).OfType<HBoxContainer>().Single(control => control.Name == $"Demand{Zones.Get(zone).Letter}Row");
            var demandLetter = row.GetChild<Label>(0);
            var demandBar = row.GetChild<ProgressBar>(1);
            Require(demandLetter.GetThemeFontSize("font_size") == 18 &&
                demandLetter.VerticalAlignment == VerticalAlignment.Center,
                "Demand letters must retain balanced, vertically centered 18-point text.");
            Require(Math.Abs(demandBar.Size.Y - demandLetter.GetThemeFontSize("font_size")) < 1 &&
                Math.Abs(demandBar.GetGlobalRect().GetCenter().Y - demandLetter.GetGlobalRect().GetCenter().Y) < 1,
                "Demand bars must match the lettering size and share its vertical center.");
            string details = CityReport.ZoneDetails(host.Session.Game, zone);
            foreach (var control in row.GetChildren().OfType<Control>().Prepend(row))
                Require(control.MouseFilter == Control.MouseFilterEnum.Stop && control.TooltipText.Contains(details),
                    $"{Zones.Get(zone).Letter} hover details are missing from its demand row.");
        }

        var frames = Descendants(host).OfType<TerminalFrame>().Where(frame => frame.IsVisibleInTree()).ToArray();
        for (int i = 0; i < frames.Length; i++)
            for (int j = i + 1; j < frames.Length; j++)
                if (!frames[i].IsAncestorOf(frames[j]) && !frames[j].IsAncestorOf(frames[i]))
                    Require(!frames[i].GetGlobalRect().Intersects(frames[j].GetGlobalRect()),
                        "Independent terminal frame borders overlap.");

        void Verify(string name, IReadOnlyList<SidebarCell[]> rows)
        {
            var table = Descendants(panel).OfType<GridContainer>().Single(control => control.Name == name);
            int expectedCells = (rows.Count * 2 + table.Columns - 1) / table.Columns * table.Columns;
            Require(table.Columns is 2 or 4 && table.GetChildCount() == expectedCells &&
                !Descendants(table).OfType<Label>().Any(label => label.Text is "METRIC" or "VALUE"),
                $"{name} has missing table cells.");
            Require(table.GetThemeConstant("h_separation") == 0 && table.GetThemeConstant("v_separation") == 0,
                "City table cells must meet in a continuous terminal grid.");
            for (int index = 0; index < table.GetChildCount(); index++)
            {
                var cell = table.GetChild<PanelContainer>(index);
                Require(cell.GetThemeStylebox("panel") is StyleBoxFlat style &&
                    style.BgColor == (index / table.Columns % 2 != 0 ? new Color("#101010") : Colors.Black) &&
                    style.BorderColor == Colors.White &&
                    style.BorderWidthLeft == (index % table.Columns == 0 ? 1 : 0) &&
                    style.BorderWidthTop == (index < table.Columns ? 1 : 0) &&
                    style.BorderWidthRight == 1 && style.BorderWidthBottom == 1 &&
                    style.CornerRadiusTopLeft == 0 && style.CornerRadiusTopRight == 0 &&
                    style.CornerRadiusBottomLeft == 0 && style.CornerRadiusBottomRight == 0,
                    "City table must use shared single-white edges and neutral terminal shading.");
            }
            for (int row = 0; row < rows.Count; row++)
                for (int column = 0; column < 2; column++)
                {
                    var label = table.GetChild<PanelContainer>(row * 2 + column).GetChild<Label>(0);
                    var expected = rows[row][column];
                    if (column == 1 && rows[row][0].Text is "Fuel" or "Water" or "Grain")
                    {
                        var gauge = label.GetChildren().OfType<ResourceGauge>().Single();
                        var supply = rows[row][0].Text == "Fuel" ? host.Session.Game.Services.Power : host.Session.Game.Services.Water;
                        double level = rows[row][0].Text == "Grain" ? SidebarReport.GrainLevel(host.Session.Game) :
                            SidebarReport.SupplyLevel(supply);
                        Require(Math.Abs(gauge.Level - level) < 1e-6 &&
                            gauge.MouseFilter == Control.MouseFilterEnum.Ignore &&
                            gauge.Size.X >= 8 && gauge.CustomMinimumSize.Y == 18 &&
                            ResourceGauge.BarHeight == 14 && gauge.GetRect().End.X <= label.Size.X -
                                label.GetThemeFont("font").GetStringSize(label.Text,
                                    fontSize: label.GetThemeFontSize("font_size")).X,
                            "Resource gauges must show raw supply levels across the available cell.");
                        Require(ResourceGauge.MinimumLevel == 0.5 && ResourceGauge.MaximumLevel == 1.5 &&
                            Math.Abs(gauge.MarkerPosition - Math.Clamp(gauge.Level - 0.5, 0, 1)) < 1e-6,
                            "Utility gauges must omit the 0-50% segment and remap the level marker to 50-150%.");
                        Require(label.Text.Length == 0 && label.TooltipText.Contains(expected.Text, StringComparison.Ordinal) &&
                            label.GetParent<Control>().TooltipText == label.TooltipText &&
                            Math.Abs(gauge.GetRect().End.X - label.Size.X) < 1,
                            "Resource cells must hide numbers in the cell and show them on hover.");
                    }
                    Require(label.GetThemeFontSize("font_size") >= 13,
                        "City table text must use the enlarged body font, not the old 8-13 range.");
                    if (table.Columns == 2)
                        Require(label.GetThemeFont("font").GetStringSize(label.Text,
                            fontSize: label.GetThemeFontSize("font_size")).X <= label.Size.X + 1,
                            $"City text is truncated: {label.Text}, width={label.Size.X}.");
                    bool gaugeOnly = column == 1 && rows[row][0].Text is "Fuel" or "Water" or "Grain";
                    Require(label.Text == (gaugeOnly ? "" : expected.Text) && label.GetThemeColor("font_color") ==
                        new Color(expected.Color.R / 255f, expected.Color.G / 255f, expected.Color.B / 255f),
                        $"{name} has incorrect values or colors.");
                }
        }
    }

    private static void OpenGameMenu(Main host)
    {
        host._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(host.Session.Prompt?.Title == "GAME MENU", "Escape during gameplay must open the game menu.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
