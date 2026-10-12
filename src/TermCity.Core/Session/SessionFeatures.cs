using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public enum PlacementKind
{
    Road,
    Building,
    Demolish,
    RoadTemplate,
}

/// <summary>
/// What a placement would cover. <see cref="Area"/> is the rectangle; for a road line at an angle, <see cref="Cells"/>
/// holds just the cells on the line and <see cref="Area"/> their bounding box.
/// </summary>
public sealed record PlacementPreview(
    PlacementKind Kind, CellRect Area, Quote Quote, string Name, RoadType? Road = null, BuildingType? Building = null,
    IReadOnlySet<Pos>? Cells = null, IReadOnlySet<Pos>? ValidCells = null, RoadTemplate? Template = null,
    IReadOnlyList<Pos>? RoadStroke = null, int Rotation = 0)
{
    /// <summary>Whether the placement covers any cell of <paramref name="block"/>.</summary>
    public bool Touches(CellRect block) => Cells is null
        ? Area.X <= block.Right && Area.Right >= block.X && Area.Y <= block.Bottom && Area.Bottom >= block.Y
        : block.Cells().Any(Cells.Contains);

    public bool IsValid(CityGame game, int x, int y) => Kind switch
    {
        PlacementKind.Road => ValidCells?.Contains(new(x, y)) ?? game.CanPlaceRoad(x, y, Road),
        PlacementKind.Building => ValidCells?.Contains(new(x, y)) == true,
        PlacementKind.RoadTemplate => ValidCells?.Contains(new(x, y)) == true,
        _ => game.Map.HasRoad(x, y) || game.Map.ZoneAt(x, y) != ZoneType.None ||
             game.Map.BuildingAt(x, y) is not null || game.Map.FeatureAt(x, y) is not null ||
             game.Map.PathWearAt(x, y) > 0,
    };

    public string Summary => $"{Name}: {Quote.Cells} valid, {Quote.Skipped} blocked, {Fmt.Money(Quote.Cost)}";
}

/// <summary>One selectable row of a prompt. With table columns, <see cref="Label"/> fills the first and <see cref="Cells"/> the rest.</summary>
public sealed record SessionChoice(string Label, Action Select, IReadOnlyList<string>? Cells = null, ChoiceHelp? Help = null);

public sealed record ChoiceHelp(string Icons, string Description, string Details);

/// <summary>One page of a tabbed prompt such as the guide.</summary>
public sealed record PromptTab(string Title, string Text);

public sealed class SessionPrompt(
    string title, string text, IReadOnlyList<SessionChoice> choices, string? input = null, string? footer = null,
    IReadOnlyList<TableColumn>? columns = null, IReadOnlyList<PromptTab>? tabs = null, int activeTab = 0)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public IReadOnlyList<SessionChoice> Choices { get; } = choices;
    public string? Input { get; set; } = input;
    public string? Footer { get; } = footer;
    public bool IsSaveDialog { get; init; }

    /// <summary>When set, the choices are drawn as a table with these column headers (the first heads the choice label).</summary>
    public IReadOnlyList<TableColumn>? Columns { get; } = columns;

    /// <summary>When set, the text is one page of several, switched with tabs along the top of the dialog.</summary>
    public IReadOnlyList<PromptTab>? Tabs { get; } = tabs;

    public int ActiveTab { get; } = activeTab;
    public int SelectedIndex { get; set; }
    public int? HelpIndex { get; set; }
    public ChoiceHelp? ActiveHelp => HelpIndex is { } index && index >= 0 && index < Choices.Count
        ? Choices[index].Help ?? new ChoiceHelp("?", Choices[index].Label,
            string.Join("\n", Choices[index].Cells ?? []) + "\n" + Text +
            "\n\nPlain Enter activates this option. Esc returns to the previous menu.")
        : null;
    public IReadOnlyList<MenuShortcut> Shortcuts { get; } = MenuShortcuts.Assign(choices);
}

public sealed partial class GameSession
{
    public const int AutosaveIntervalSeconds = 60;
    public const int AutosaveSlots = 3;
    public static readonly IReadOnlyList<int> Milestones = Array.AsReadOnly(CityProgression.Milestones.Select(m => m.Population).ToArray());

    public const int UndoLimit = 10;
    private readonly List<string> _undoHistory = [];
    private int _revision, _savedRevision = -1, _autosaveRevision = -1;
    private double _savedAt, _autosaveAt, _autosaveElapsed;
    private int _observedHomes, _observedMilestone;
    private Pos _roadAnchor;
    private RoadType? _lineRoad;
    private readonly Stack<SessionPrompt> _promptHistory = [];
    private sealed record MenuReturn(SessionPrompt Prompt, SessionPrompt[] History);
    private MenuReturn? _selectedMenu, _previewMenu;

    public PlacementPreview? Preview { get; private set; }
    public bool RoadToolActive { get; private set; }
    public bool BuildingToolActive => Preview?.Kind == PlacementKind.Building;
    public bool FixedPlacementToolActive => BuildingToolActive || Preview?.Kind == PlacementKind.RoadTemplate;
    public string? FixedPlacementError
    {
        get
        {
            if (Preview?.Template is not { } template) return BuildingPlacementError;
            if (Game.RoadTemplatePlacementError(template, new(Preview.Area.X, Preview.Area.Y)) is { } error) return error;
            return Preview.Quote.Cost > Game.Money
                ? $"Not enough gold: {template.Name} costs {Fmt.Money(Preview.Quote.Cost)}; you have {Fmt.Money(Game.Money)}."
                : null;
        }
    }
    public string? FixedPlacementWarning => Preview?.Template is not null ? Preview.Summary + "; click or Enter to place."
        : BuildingPlacementWarning;
    public string? BuildingPlacementError => Preview?.Building is { } building
        ? Game.BuildingPlacementError(building, Preview.Area.X, Preview.Area.Y, Preview.Rotation)
        : null;
    public string? BuildingPlacementWarning => Preview?.Building is { } building
        ? Game.BuildingPlacementWarning(building, Preview.Area)
        : null;
    public SessionPrompt? Prompt { get; private set; }
    public bool GuideVisible { get; private set; }
    public bool HasUnsavedChanges => _revision != _savedRevision || Game.ElapsedDays != _savedAt;
    public int UndoCount => _undoHistory.Count;
    public bool CanUndo => Game.Paused && UndoCount > 0;
    public event Action? QuitRequested;

    public int? NextMilestone
    {
        get
        {
            int next = Milestones.FirstOrDefault(m => m > Game.HighestMilestone);
            return next == 0 ? null : next;
        }
    }

    public string GuideText => CityProgression.NextStep(Game);

    public string AutosavePath(int slot)
    {
        if (slot is < 1 or > AutosaveSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath))!,
            Path.GetFileNameWithoutExtension(SavePath) + $".autosave{slot}.json");
    }

    private bool _resumeWhileInteracting;
    public bool ClockSuspended => Game.Paused ||
        !_resumeWhileInteracting && (Prompt is not null || Preview is not null);

    public void Update(double elapsedSeconds)
    {
        if (elapsedSeconds < 0 || !double.IsFinite(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        if (!ClockSuspended)
        {
            Game.Update(elapsedSeconds);
        }
        if (Prompt is null && Preview is null) _resumeWhileInteracting = false;

        _autosaveElapsed += elapsedSeconds;
        if (_autosaveElapsed >= AutosaveIntervalSeconds)
        {
            _autosaveElapsed = 0;
            if (_autosaveRevision != _revision || _autosaveAt != Game.ElapsedDays)
            {
                Autosave();
            }
        }
    }

    public bool Autosave()
    {
        string pending = AutosavePath(1) + ".pending";
        try
        {
            SaveGameStore.Save(Game, pending, new SavedView(CameraX, CameraY, ZoomLevel)
            { CameraEdgePadding = CameraEdgePadding });
            for (int slot = AutosaveSlots; slot > 1; slot--)
            {
                if (File.Exists(AutosavePath(slot - 1)))
                {
                    File.Copy(AutosavePath(slot - 1), AutosavePath(slot), overwrite: true);
                }
            }

            File.Move(pending, AutosavePath(1), overwrite: true);
            _autosaveRevision = _revision;
            _autosaveAt = Game.ElapsedDays;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage("Autosave failed: " + ex.Message, MessageKind.Error);
            return false;
        }
    }

    public void PreviewRoad(RoadType? road = null)
    {
        HasCursor = true;
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        road ??= Game.DefaultRoad;
        var stroke = SelectedWaterCrossing();
        Preview = stroke is null
            ? new(PlacementKind.Road, ActiveArea, Game.QuoteRoad(ActiveArea, road), road.Name, Road: road)
            : new(PlacementKind.Road, ActiveArea, Game.QuoteRoad(stroke, road), road.Name + " crossing", Road: road,
                Cells: stroke.ToHashSet(), ValidCells: Game.PlanRoad(stroke, road).ToHashSet(), RoadStroke: stroke);
        Changed?.Invoke();
    }

    private IReadOnlyList<Pos>? SelectedWaterCrossing()
    {
        if (ActiveArea.Area <= 1 || !ActiveArea.Cells().Any(p => !Game.Map.TerrainAt(p.X, p.Y).Buildable)) return null;
        return CellLines.Between(Anchor ?? _selectionOrigin ?? new(ActiveArea.X, ActiveArea.Y), Cursor);
    }

    public void PreviewRoadTemplate(RoadTemplate template)
    {
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        HasCursor = true;
        Anchor = null;
        Selection = null;
        RefreshRoadTemplate(template);
    }

    public void MoveFixedPlacement(Pos position)
    {
        if (Preview?.Template is { } template)
        {
            Cursor = Clamp(position);
            RefreshRoadTemplate(template);
            FollowCursor();
        }
        else MoveBuildingPlacement(position);
    }

    private void RefreshRoadTemplate(RoadTemplate template)
    {
        var cells = template.Cells(Cursor).ToHashSet();
        var area = new CellRect(Cursor.X, Cursor.Y, template.Width, template.Height);
        var quote = Game.QuoteRoadTemplate(template, Cursor);
        Preview = new(PlacementKind.RoadTemplate, area, quote, $"{template.Name} ({template.Width}x{template.Height})",
            Road: Game.Map.Content.Roads.Get(template.RoadName), Cells: cells,
            ValidCells: Game.RoadTemplatePlacementError(template, Cursor) is null ? cells : [], Template: template);
        Changed?.Invoke();
    }

    public string? BuildingPurchaseError(BuildingType building)
    {
        if (!CityProgression.IsUnlocked(Game, building))
            return $"{building.Name} is locked until City Grew! at {CityProgression.RequiredPopulation(building):N0} souls.";
        return building.Cost > Game.Money
            ? $"Not enough gold: {building.Name} costs {Fmt.Money(building.Cost)}; you have {Fmt.Money(Game.Money)}."
            : null;
    }

    public void PreviewBuilding(BuildingType building)
    {
        if (BuildingPurchaseError(building) is { } error)
        {
            SetMessage(error, MessageKind.Error);
            return;
        }

        _previewMenu = CaptureMenu() ?? _selectedMenu;
        HasCursor = true;
        Anchor = null;
        Selection = null;
        RefreshBuildingPlacement(building, 0);
    }

    public void MoveBuildingPlacement(Pos position)
    {
        if (Preview?.Building is not { } building)
        {
            return;
        }

        Cursor = Clamp(position);
        RefreshBuildingPlacement(building, Preview.Rotation);
        FollowCursor();
    }

    public void RotateBuildingPlacement(int quarterTurns)
    {
        if (Preview?.Building is not { } building) return;
        int rotation = ((Preview.Rotation + quarterTurns) % 4 + 4) % 4;
        RefreshBuildingPlacement(building, rotation);
    }

    private void RefreshBuildingPlacement(BuildingType building, int rotation)
    {
        var area = building.FootprintAt(Cursor.X, Cursor.Y, rotation);
        var validCells = Game.BuildingPlacementError(building, Cursor.X, Cursor.Y, rotation) is null
            ? area.Cells().ToHashSet()
            : [];
        Preview = new(PlacementKind.Building, area, Game.QuoteBuilding(building, area, rotation),
            $"{building.Name} ({area.Width}x{area.Height})", Building: building, ValidCells: validCells, Rotation: rotation);
        Changed?.Invoke();
    }

    public void PreviewDemolish()
    {
        HasCursor = true;
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        var targets = Game.DemolitionCells(ActiveArea.Cells());
        var area = targets.Aggregate(ActiveArea, (bounds, p) => bounds.Union(CellRect.Single(p)));
        var preview = new PlacementPreview(PlacementKind.Demolish, area, new(0, 0, 0), "Demolish", Cells: targets);
        int cells = targets.Count(p => preview.IsValid(Game, p.X, p.Y));
        Preview = preview with { Quote = new(cells, 0, targets.Count - cells) };
        Changed?.Invoke();
    }

    public void BeginRoadLine(RoadType? road = null)
    {
        HasCursor = true;
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        RoadToolActive = true;
        _lineRoad = road ?? Game.DefaultRoad;
        _roadAnchor = Cursor;
        Anchor = null;
        Selection = null;
        RefreshRoadLine();
    }

    private void RefreshRoadLine()
    {
        var line = CellLines.Between(_roadAnchor, Cursor);
        var area = CellRect.FromCorners(_roadAnchor, Cursor);
        var road = _lineRoad ?? Game.DefaultRoad;
        var quote = Game.QuoteRoad(line, road);
        var laid = Game.PlanRoad(line, road).ToHashSet();
        bool hasGap = line.Any(p => !laid.Contains(p) && !Game.Map.HasRoad(p.X, p.Y));
        Preview = new(PlacementKind.Road, area, quote,
            hasGap ? road.Name + " line (gaps)" : road.Name + " line", Road: road, Cells: line.ToHashSet(),
            ValidCells: laid, RoadStroke: line);
        Changed?.Invoke();
    }

    public ActionResult ConfirmPreview()
    {
        if (Preview is not { } preview)
        {
            return Complete(ActionResult.Fail("There is no placement to confirm."));
        }

        var result = Execute(() => preview.Kind switch
        {
            PlacementKind.Road when preview.RoadStroke is { } stroke => Game.BuildRoad(stroke, preview.Road),
            PlacementKind.Road => preview.Cells is { } line
                ? Game.BuildRoad(line, preview.Road) : Game.BuildRoad(preview.Area, preview.Road),
            PlacementKind.Building when preview.Building is { } building => Game.PlaceBuilding(building, preview.Area, preview.Rotation),
            PlacementKind.RoadTemplate when preview.Template is { } template =>
                Game.BuildRoadTemplate(template, new(preview.Area.X, preview.Area.Y)),
            PlacementKind.Demolish => Game.Demolish(preview.Cells ?? preview.Area.Cells()),
            _ => ActionResult.Fail("No building type selected."),
        }, preview.Building is { } type ? Game.BuildingPlacementWarning(type, preview.Area) : null);
        if (result.Success)
        {
            ClearPreview();
        }

        return result;
    }

    public void CancelPreview() => FinishPreview(returnToMenu: true);

    private void ClearPreview() => FinishPreview(returnToMenu: false);

    private void FinishPreview(bool returnToMenu)
    {
        if (Preview is null && !RoadToolActive)
        {
            return;
        }

        Preview = null;
        RoadToolActive = false;
        _lineRoad = null;
        var menu = _previewMenu;
        _previewMenu = null;
        if (returnToMenu && menu is not null) RestoreMenu(menu);
        Changed?.Invoke();
    }

    public void RequestUndo()
    {
        if (!CanUndo)
        {
            SetMessage(UndoCount == 0 ? "Nothing to undo." : "Pause the game to undo.", MessageKind.Error);
            return;
        }

        Game.Paused = true;
        ClearPreview();
        ShowPrompt("Undo last action",
            $"Undo restores the entire city to immediately before the last successful action, including gold, residents and time. The game will remain paused. {UndoCount} action(s) available.",
            [new("Undo", Undo), new("Cancel", CancelPrompt)]);
    }

    private void Undo()
    {
        if (!CanUndo)
        {
            SetMessage(UndoCount == 0 ? "Nothing to undo." : "Pause the game to undo.", MessageKind.Error);
            return;
        }

        var restored = SaveGameStore.Deserialize(_undoHistory[^1], Game.Map.Content);
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        ReplaceGame(restored, clearUndo: false);
        Game.Paused = true;
        SetMessage($"Last action undone; city restored and paused. {UndoCount} action(s) remaining.", MessageKind.Success);
    }

    public void ShowPrompt(
        string title, string text, IReadOnlyList<SessionChoice> choices, string? input = null, string? footer = null,
        IReadOnlyList<TableColumn>? columns = null)
    {
        SetPrompt(new(title, text, choices, input, footer, columns));
    }

    public void ShowPrompt(SessionPrompt prompt) => SetPrompt(prompt);

    /// <summary>Shows a prompt whose text is one of several pages, with a tab for each along the top.</summary>
    public void ShowTabbedPrompt(
        string title, IReadOnlyList<PromptTab> tabs, int activeTab, IReadOnlyList<SessionChoice> choices,
        IReadOnlyList<TableColumn>? columns = null)
    {
        activeTab = Math.Clamp(activeTab, 0, tabs.Count - 1);
        SetPrompt(new(title, tabs[activeTab].Text, choices, null, null, columns, tabs, activeTab));
    }

    private void SetPrompt(SessionPrompt prompt)
    {
        if (Prompt is null) _resumeWhileInteracting = false;
        if (Prompt is { } previous)
        {
            if (previous.Title == prompt.Title) prompt.SelectedIndex = previous.SelectedIndex;
            else if (_promptHistory.Any(parent => parent.Title == prompt.Title))
            {
                SessionPrompt parent;
                do { parent = _promptHistory.Pop(); } while (parent.Title != prompt.Title);
                prompt.SelectedIndex = parent.SelectedIndex;
            }
            else _promptHistory.Push(previous);
        }
        Prompt = prompt;
        Changed?.Invoke();
    }

    /// <summary>Switches a tabbed prompt by <paramref name="delta"/> tabs, wrapping around.</summary>
    public void CycleTab(int delta)
    {
        if (Prompt is { Tabs: { Count: > 0 } tabs } prompt)
        {
            SelectTab((prompt.ActiveTab + delta % tabs.Count + tabs.Count) % tabs.Count);
        }
    }

    public void SelectTab(int index)
    {
        if (Prompt is { Tabs: { Count: > 0 } tabs } prompt && index >= 0 && index < tabs.Count && index != prompt.ActiveTab)
        {
            ShowTabbedPrompt(prompt.Title, tabs, index, prompt.Choices, prompt.Columns);
        }
    }

    public void ClosePrompt()
    {
        Prompt = null;
        _promptHistory.Clear();
        Changed?.Invoke();
    }

    public void CancelPrompt()
    {
        Prompt = _promptHistory.TryPop(out var parent) ? parent : null;
        Changed?.Invoke();
    }

    private MenuReturn? CaptureMenu() => Prompt is { } prompt ? new(prompt, _promptHistory.ToArray()) : null;

    private void RestoreMenu(MenuReturn menu)
    {
        _promptHistory.Clear();
        foreach (var parent in menu.History.Reverse()) _promptHistory.Push(parent);
        Prompt = menu.Prompt;
    }

    public void SelectPrompt(int index)
    {
        if (Prompt is { } prompt && index >= 0 && index < prompt.Choices.Count)
        {
            prompt.SelectedIndex = index;
            var previous = _selectedMenu;
            _selectedMenu = CaptureMenu();
            try { prompt.Choices[index].Select(); }
            finally { _selectedMenu = previous; }
        }
    }

    public bool SelectShortcut(char letter, bool shift = false)
    {
        if (Prompt is not { } prompt) return false;
        for (int index = 0; index < prompt.Shortcuts.Count; index++)
            if (prompt.Shortcuts[index].Letter == char.ToUpperInvariant(letter) && prompt.Shortcuts[index].Shift == shift)
            {
                SelectPrompt(index);
                return true;
            }
        return false;
    }

    public bool ConfirmQuit { get; init; } = true;

    public void RequestQuit()
    {
        if (!ConfirmQuit) QuitRequested?.Invoke();
        else GuardProgress("Quit", () => QuitRequested?.Invoke());
    }

    public void RequestNewCity(bool restart) => GuardProgress(restart ? "Restart this seed" : "New city", () =>
        NewGame(Game.Config with { Seed = restart ? Game.Config.Seed : Random.Shared.Next(), StartingYear = null }));

    public void RequestLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetMessage("Load failed: enter a save-file path.", MessageKind.Error);
            return;
        }

        path = path.Trim().Trim('"');
        GuardProgress("Confirm load city", () =>
        {
            if (LoadFrom(path))
            {
                ClosePrompt();
            }
        });
    }

    private void GuardProgress(string title, Action action)
    {
        if (!HasUnsavedChanges)
        {
            action();
            return;
        }

        bool quitting = title == "Quit";
        SetPrompt(new(title,
            quitting
                ? "Save your city before quitting? Autosaves are separate from your quick-save."
                : "Save your city before continuing? Autosaves are separate from your quick-save.",
        [
            new(quitting ? "Save and quit" : "Save and continue", () =>
            {
                if (QuickSave())
                {
                    CancelPrompt();
                    action();
                }
            }),
            new(quitting ? "Quit without saving" : "Continue without saving", () => { CancelPrompt(); action(); }),
            new("Cancel", CancelPrompt),
        ])
        { IsSaveDialog = true });
    }

    private static string DisplayPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) &&
            fullPath.StartsWith(home + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return "~" + fullPath[home.Length..];
        }

        return fullPath;
    }

    public static readonly IReadOnlyList<TableColumn> CityMenuColumns = ["COMMAND", "DESCRIPTION", "STATE"];

    public void ShowSessionMenu()
    {
        ClearPreview();
        ShowPrompt("City menu", $"Seed {Game.Config.Seed} | {Game.Map.Width}x{Game.Map.Height}",
        [
            new("Back to city", ClosePrompt, ["Close this menu"]),
            new("Save", () => { if (QuickSave()) ClosePrompt(); }, ["Write the quick-save file"]),
            new("Load", ShowLoadMenu, ["Quick-save, autosaves or a file"]),
            new("New city", () => RequestNewCity(restart: false), ["A fresh random map of the same size"]),
            new("Restart", () => RequestNewCity(restart: true), ["Replay this seed from the start"]),
            new("Undo", RequestUndo, [$"While paused: {UndoCount} of {UndoLimit} actions available"]),
            new("Health", ShowHealthReport, ["Indicators, complaints and events"]),
            new("Report", ShowReport, ["Weekly report and milestones"]),
            new("Growth", ShowGrowthReport, ["Population, demand and road access"]),
            new("Guide", () => ShowGuide(), ["How to play, tab by tab"]),
            new("Quit", RequestQuit, ["Leave TermCity"]),
        ], columns: CityMenuColumns);
    }

    public static readonly IReadOnlyList<TableColumn> LoadMenuColumns = ["SOURCE", "NOTE"];

    public void ShowLoadMenu()
    {
        var choices = new List<SessionChoice> { new("Quick-save", () => RequestLoad(SavePath), ["Your own save (F5)"]) };
        for (int i = 1; i <= AutosaveSlots; i++)
        {
            int slot = i;
            choices.Add(new($"Autosave {slot}", () => RequestLoad(AutosavePath(slot)), [slot == 1 ? "Newest" : "Older backup"]));
        }

        choices.Add(new("File path", () =>
            ShowPrompt("Load file", OperatingSystem.IsMacOS()
                    ? "Enter a save-file path. Control+A clears the field; Return selects the highlighted button."
                    : "Enter a save-file path. Ctrl+A clears the field; Enter selects the highlighted button.",
                [new("Load", () => RequestLoad(Prompt!.Input!)), new("Cancel", CancelPrompt)], SavePath), ["Type where a save lives"]));
        choices.Add(new("Cancel", CancelPrompt, ["Return to the previous menu"]));
        ShowPrompt("Load city", "Choose a quick-save, autosave, or file path.", choices, columns: LoadMenuColumns);
    }

    public static readonly IReadOnlyList<TableColumn> GuideChoiceColumns = ["ACTION", "DESCRIPTION"];

    public void ShowGuide(int tab = 0)
    {
        var choices = new List<SessionChoice> { new("Close", CancelPrompt, ["Return to the previous menu"]) };
        if (GuideVisible)
        {
            choices.Add(new("Dismiss tip", DismissGuide, ["Hide next-step advice on the Start tab"]));
        }
        else
        {
            choices.Add(new("Enable guide", EnableGuide, ["Show next-step advice on the Start tab"]));
        }

        ShowTabbedPrompt("TermCity guide", GuideContent.Tabs(Game, GuideVisible), tab, choices, GuideChoiceColumns);
    }

    public void DismissGuide()
    {
        GuideVisible = false;
        Game.GuideDismissed = true;
        Game.NotifyStateChanged();
        CancelPrompt();
    }

    public void EnableGuide()
    {
        GuideVisible = true;
        Game.GuideDismissed = false;
        Game.NotifyStateChanged();
        CancelPrompt();
    }

    public void ShowBuildingStatus(Pos position)
    {
        if (!Game.Map.InBounds(position) || Game.Map.BuildingAt(position.X, position.Y) is null)
        {
            SetMessage("No building at the selected position.", MessageKind.Error);
            return;
        }
        PlaceCursor(position);
        var report = BuildingReport.Build(Game, position);
        ShowPrompt(BuildingReport.Title, report.Text,
            [new("Area menu", ShowAreaMenu), new("Close", CancelPrompt)]);
    }

    public void ShowReport() => ShowReport(0);

    public void ShowReport(int tab)
    {
        var tabs = ReportContent.Build(Game).Select(page => new PromptTab(page.Title, page.Text)).ToArray();
        ShowTabbedPrompt("Weekly Report", tabs, tab, [new("Close", CancelPrompt)]);
    }

    public void ShowGrowthReport()
    {
        var lines = new List<string>();
        foreach (var zone in Zones.Placeable)
        {
            var diagnostic = GrowthDiagnostics.ForZone(Game, zone);
            lines.Add(CityReport.ZoneDetails(Game, zone));
            lines.Add($"{Zones.Get(zone).Name}: {Game.VacantPlots(zone).Count} complete vacant plots");
            lines.Add(diagnostic.Message);
            lines.Add("");
        }

        if (Game.Paused)
        {
            lines.Add("Paused - press P after closing this report to resume.");
        }

        ShowPrompt("Growth and road access", string.Join("\n", lines), [new("Close", CancelPrompt)]);
    }

    private void InitializeFeedback()
    {
        _observedHomes = Game.Stats.Residential.Occupied;
        _observedMilestone = Game.HighestMilestone;
    }

    public void ShowMilestone(int index)
    {
        index = Math.Clamp(index, 0, CityProgression.Milestones.Count - 1);
        Game.Paused = true;
        var tabs = Enumerable.Range(0, CityProgression.Milestones.Count)
            .Select(i => MilestoneReport.Build(Game, i))
            .Select(page => new PromptTab(page.Title, page.Text)).ToArray();
        ShowTabbedPrompt(MilestoneReport.Title, tabs, index,
        [
            new("Previous", () => ShowMilestone(index - 1)),
            new("Next", () => ShowMilestone(index + 1)),
            new("Continue", CancelPrompt),
        ]);
    }

    private void UpdateFeedback()
    {
        int homes = Game.Stats.Residential.Occupied;
        if (Game.HighestMilestone > _observedMilestone)
        {
            var reached = CityProgression.Milestones.Where(m =>
                m.Population > _observedMilestone && m.Population <= Game.HighestMilestone).ToArray();
            _observedMilestone = Game.HighestMilestone;
            SetMessage($"Your City Grew! {reached[^1].Name}: {Game.HighestMilestone:N0} souls.", MessageKind.Success);
            ShowMilestone(CityProgression.Milestones.ToList().FindIndex(m => m.Population == Game.HighestMilestone));
        }
        else if (_observedHomes < Game.Config.MinResidentialCells && homes >= Game.Config.MinResidentialCells)
        {
            SetMessage($"{Game.Config.MinResidentialCells} occupied homes: markets and workshops unlocked. Zone with C and I.", MessageKind.Success);
        }

        _observedHomes = homes;
    }
}
