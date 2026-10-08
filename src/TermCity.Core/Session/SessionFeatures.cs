using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
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
}

/// <summary>
/// What a placement would cover. <see cref="Area"/> is the rectangle; for a road line at an angle, <see cref="Cells"/>
/// holds just the cells on the line and <see cref="Area"/> their bounding box.
/// </summary>
public sealed record PlacementPreview(
    PlacementKind Kind, CellRect Area, Quote Quote, string Name, RoadType? Road = null, BuildingType? Building = null,
    IReadOnlySet<Pos>? Cells = null, IReadOnlySet<Pos>? ValidCells = null)
{
    /// <summary>Whether the placement covers any cell of <paramref name="block"/>.</summary>
    public bool Touches(CellRect block) => Cells is null
        ? Area.X <= block.Right && Area.Right >= block.X && Area.Y <= block.Bottom && Area.Bottom >= block.Y
        : block.Cells().Any(Cells.Contains);

    public bool IsValid(CityGame game, int x, int y) => Kind switch
    {
        PlacementKind.Road => game.CanPlaceRoad(x, y, Road),
        PlacementKind.Building => ValidCells?.Contains(new(x, y)) == true,
        _ => game.Map.HasRoad(x, y) || game.Map.ZoneAt(x, y) != ZoneType.None ||
             game.Map.BuildingAt(x, y) is not null || game.Map.FeatureAt(x, y) is not null,
    };

    public string Summary => $"{Name}: {Quote.Cells} valid, {Quote.Skipped} blocked, {Fmt.Money(Quote.Cost)}";
}

/// <summary>One selectable row of a prompt. With table columns, <see cref="Label"/> fills the first and <see cref="Cells"/> the rest.</summary>
public sealed record SessionChoice(string Label, Action Select, IReadOnlyList<string>? Cells = null);

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

    /// <summary>When set, the choices are drawn as a table with these column headers (the first heads the choice label).</summary>
    public IReadOnlyList<TableColumn>? Columns { get; } = columns;

    /// <summary>When set, the text is one page of several, switched with tabs along the top of the dialog.</summary>
    public IReadOnlyList<PromptTab>? Tabs { get; } = tabs;

    public int ActiveTab { get; } = activeTab;
    public int SelectedIndex { get; set; }
    public IReadOnlyList<MenuShortcut> Shortcuts { get; } = MenuShortcuts.Assign(choices);
}

public sealed partial class GameSession
{
    public const int AutosaveIntervalSeconds = 60;
    public const int AutosaveSlots = 3;
    public static readonly IReadOnlyList<int> Milestones = Array.AsReadOnly(new[] { 100, 500, 1_000, 5_000, 10_000 });

    private string? _undoSnapshot;
    private double _undoAt;
    private int _revision, _savedRevision = -1, _autosaveRevision = -1;
    private double _savedAt, _autosaveAt, _autosaveElapsed;
    private int _observedHomes, _observedWeek;
    private Pos _roadAnchor;
    private RoadType? _lineRoad;
    private readonly Stack<SessionPrompt> _promptHistory = [];
    private sealed record MenuReturn(SessionPrompt Prompt, SessionPrompt[] History);
    private MenuReturn? _selectedMenu, _previewMenu;

    public PlacementPreview? Preview { get; private set; }
    public bool RoadToolActive { get; private set; }
    public SessionPrompt? Prompt { get; private set; }
    public bool GuideVisible { get; private set; }
    public bool HasUnsavedChanges => _revision != _savedRevision || Game.ElapsedDays != _savedAt;
    public bool CanUndo => _undoSnapshot is not null && (Game.Paused || Game.ElapsedDays - _undoAt <= 7);
    public event Action? QuitRequested;

    public int? NextMilestone
    {
        get
        {
            int next = Milestones.FirstOrDefault(m => m > Game.HighestMilestone);
            return next == 0 ? null : next;
        }
    }

    public string GuideText => Game.Stats.Residential.Zoned == 0
        ? "First city: cut a track (T), mark out homesteads (R), then resume (P). F6 opens the guide."
        : Game.Stats.Residential.Occupied >= Game.Config.MinResidentialCells
            ? "Markets and workshops unlocked: zone with C and I. F6 opens the guide."
        : Game.Paused
            ? "Homes zoned. Check road access, then press P to resume. Markets and workshops unlock at 10 occupied homes."
            : $"Grow to {Game.Config.MinResidentialCells} occupied homes to unlock markets and workshops. F6 opens the guide.";

    public string AutosavePath(int slot)
    {
        if (slot is < 1 or > AutosaveSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SavePath))!,
            Path.GetFileNameWithoutExtension(SavePath) + $".autosave{slot}.json");
    }

    public void Update(double elapsedSeconds)
    {
        if (elapsedSeconds < 0 || !double.IsFinite(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        if (Prompt is null && Preview is null)
        {
            Game.Update(elapsedSeconds);
        }

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
            SaveGameStore.Save(Game, pending);
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
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        road ??= Game.DefaultRoad;
        Preview = new(PlacementKind.Road, ActiveArea, Game.QuoteRoad(ActiveArea, road), road.Name, Road: road);
        Changed?.Invoke();
    }

    public void PreviewBuilding(BuildingType building)
    {
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        var area = Game.BuildingPlacementArea(building, ActiveArea);
        Preview = new(PlacementKind.Building, area, Game.QuoteBuilding(building, area),
            $"{building.Name} ({building.Width}x{building.Height})", Building: building,
            ValidCells: Game.PlanBuildings(building, area).SelectMany(p => p.Cells()).ToHashSet());
        Changed?.Invoke();
    }

    public void PreviewDemolish()
    {
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
            hasGap ? road.Name + " line (gaps)" : road.Name + " line", Road: road, Cells: line.ToHashSet());
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
            PlacementKind.Road => preview.Cells is { } line
                ? Game.BuildRoad(line, preview.Road) : Game.BuildRoad(preview.Area, preview.Road),
            PlacementKind.Building when preview.Building is { } building => Game.PlaceBuilding(building, preview.Area),
            PlacementKind.Demolish => Game.Demolish(preview.Cells ?? preview.Area.Cells()),
            _ => ActionResult.Fail("No building type selected."),
        });
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
            SetMessage(_undoSnapshot is null ? "Nothing to undo." : "Undo expired: more than 7 game days have passed. Pause to allow undo.", MessageKind.Error);
            return;
        }

        Game.Paused = true;
        ClearPreview();
        ShowPrompt("Undo last action",
            "Undo restores the entire city to immediately before the last successful action, including gold, residents and time. The game will remain paused.",
            [new("Undo", Undo), new("Cancel", CancelPrompt)]);
    }

    private void Undo()
    {
        if (_undoSnapshot is not { } snapshot)
        {
            SetMessage("Nothing to undo.", MessageKind.Error);
            return;
        }

        var restored = SaveGameStore.Deserialize(snapshot, Game.Map.Content, preserveTimings: true);
        ReplaceGame(restored);
        Game.Paused = true;
        SetMessage("Last action undone; city restored and paused.", MessageKind.Success);
    }

    public void ShowPrompt(
        string title, string text, IReadOnlyList<SessionChoice> choices, string? input = null, string? footer = null,
        IReadOnlyList<TableColumn>? columns = null)
    {
        SetPrompt(new(title, text, choices, input, footer, columns));
    }

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

    public void RequestQuit() => GuardProgress("Quit", () => QuitRequested?.Invoke());

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
        ShowPrompt(title,
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
        ]);
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
            new("Undo", RequestUndo, ["Roll back the last action"]),
            new("Budget", ShowBudgetMenu, ["Funding, taxes and loans"]),
            new("Health", ShowHealthReport, ["Indicators, complaints and events"]),
            new("Report", ShowReport, ["Weekly report and milestones"]),
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
            choices.Add(new("Dismiss tip", DismissGuide, ["Hide the sidebar tip for good"]));
        }

        ShowTabbedPrompt("TermCity guide", GuideContent.Tabs(Game), tab, choices, GuideChoiceColumns);
    }

    public void DismissGuide()
    {
        GuideVisible = false;
        Game.GuideDismissed = true;
        Game.Touch();
        CancelPrompt();
    }

    public void ShowReport()
    {
        var report = Game.LastReport;
        string text = report is null ? "No completed week yet." :
            $"Week {report.Week}: income {Fmt.Money(report.Income)}\nNew homes {report.NewHouseholds}, stalls {report.NewCommercial}, workshops {report.NewIndustrial}";
        text += $"\nPopulation: {Game.Stats.Population:N0}\nHighest milestone: {Game.HighestMilestone:N0}";
        text += NextMilestone is { } next ? $"\nNext milestone: {next:N0} people" : "\nAll population milestones reached.";
        ShowPrompt("Weekly report and milestones", text, [new("Close", CancelPrompt)]);
    }

    public void ShowGrowthReport()
    {
        var lines = new List<string>();
        foreach (var zone in Zones.Placeable)
        {
            var diagnostic = GrowthDiagnostics.ForZone(Game, zone);
            lines.Add($"{Zones.Get(zone).Name}: {diagnostic.EligibleVacancies} road-served vacancies");
            lines.Add(diagnostic.Message);
            int leaving = Game.Stats.For(zone).AwaitingRemoval;
            if (leaving > 0)
            {
                lines.Add($"{leaving} unzoned building(s) awaiting removal.");
            }
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
        _observedWeek = Game.Week;
    }

    private void UpdateFeedback()
    {
        int homes = Game.Stats.Residential.Occupied;
        int reached = Milestones.LastOrDefault(m => m <= Game.Stats.Population);
        if (reached > Game.HighestMilestone)
        {
            Game.HighestMilestone = reached;
            SetMessage($"Population milestone: {reached:N0}! F7 shows your progress.", MessageKind.Success);
        }
        else if (_observedHomes < Game.Config.MinResidentialCells && homes >= Game.Config.MinResidentialCells)
        {
            SetMessage($"{Game.Config.MinResidentialCells} occupied homes: markets and workshops unlocked. Zone with C and I.", MessageKind.Success);
        }
        else if (_observedWeek != Game.Week && Game.LastReport is { } report && !MessageVisible)
        {
            SetMessage($"Week {report.Week}: +{report.NewHouseholds} homes, +{report.NewCommercial} shops, +{report.NewIndustrial} factories, {Fmt.Money(report.Income)} tax. F7 report.");
        }

        _observedHomes = homes;
        _observedWeek = Game.Week;
    }
}
