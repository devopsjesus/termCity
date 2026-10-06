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

public sealed record PlacementPreview(PlacementKind Kind, CellRect Area, Quote Quote, string Name, RoadType? Road = null, BuildingType? Building = null)
{
    public bool IsValid(CityGame game, int x, int y) => Kind switch
    {
        PlacementKind.Road => game.CanPlaceRoad(x, y, Road),
        PlacementKind.Building => game.CanBuildOn(x, y),
        _ => game.Map.HasRoad(x, y) || game.Map.ZoneAt(x, y) != ZoneType.None ||
             game.Map.BuildingAt(x, y) is not null || game.Map.FeatureAt(x, y) is not null,
    };

    public string Summary => $"{Name}: {Quote.Cells} valid, {Quote.Skipped} blocked, {Fmt.Money(Quote.Cost)}";
}

public sealed record SessionChoice(string Label, Action Select);

public sealed class SessionPrompt(
    string title, string text, IReadOnlyList<SessionChoice> choices, string? input = null, string? footer = null)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public IReadOnlyList<SessionChoice> Choices { get; } = choices;
    public string? Input { get; set; } = input;
    public string? Footer { get; } = footer;
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
        ? "First city: connect a street (T), zone nearby homes (R), then resume (P). F6 dismisses the guide."
        : Game.Stats.Residential.Occupied >= Game.Config.MinResidentialCells
            ? "Shops and factories unlocked: zone with C and I. F6 dismisses the guide."
        : Game.Paused
            ? "Homes zoned. Check road access, then press P to resume. Shops and factories unlock at 10 occupied homes."
            : $"Grow to {Game.Config.MinResidentialCells} occupied homes to unlock shops and factories. F6 dismisses the guide.";

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
        road ??= Game.DefaultRoad;
        Preview = new(PlacementKind.Road, ActiveArea, Game.QuoteRoad(ActiveArea, road), road.Name, Road: road);
        Changed?.Invoke();
    }

    public void PreviewBuilding(BuildingType building)
    {
        Preview = new(PlacementKind.Building, ActiveArea, Game.QuoteBuilding(building, ActiveArea), building.Name, Building: building);
        Changed?.Invoke();
    }

    public void PreviewDemolish()
    {
        var preview = new PlacementPreview(PlacementKind.Demolish, ActiveArea, new(0, 0, 0), "Demolish");
        int cells = ActiveArea.Cells().Count(p => Game.Map.InBounds(p) && preview.IsValid(Game, p.X, p.Y));
        Preview = preview with { Quote = new(cells, 0, ActiveArea.Area - cells) };
        Changed?.Invoke();
    }

    public void BeginRoadLine(RoadType? road = null)
    {
        RoadToolActive = true;
        _lineRoad = road ?? Game.DefaultRoad;
        _roadAnchor = Cursor;
        Anchor = null;
        Selection = null;
        RefreshRoadLine();
    }

    private void RefreshRoadLine()
    {
        var end = Math.Abs(Cursor.X - _roadAnchor.X) >= Math.Abs(Cursor.Y - _roadAnchor.Y)
            ? new Pos(Cursor.X, _roadAnchor.Y) : new Pos(_roadAnchor.X, Cursor.Y);
        var area = CellRect.FromCorners(_roadAnchor, end);
        var road = _lineRoad ?? Game.DefaultRoad;
        var quote = Game.QuoteRoad(area, road);
        bool hasGap = area.Cells().Any(p => !Game.CanPlaceRoad(p.X, p.Y, road) && !Game.Map.HasRoad(p.X, p.Y));
        Preview = new(PlacementKind.Road, area, quote,
            hasGap ? road.Name + " line (gaps)" : road.Name + " line", Road: road);
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
            PlacementKind.Road => Game.BuildRoad(preview.Area, preview.Road),
            PlacementKind.Building when preview.Building is { } building => Game.PlaceBuilding(building, preview.Area),
            PlacementKind.Demolish => Game.Demolish(preview.Area),
            _ => ActionResult.Fail("No building type selected."),
        });
        if (result.Success)
        {
            CancelPreview();
        }

        return result;
    }

    public void CancelPreview()
    {
        if (Preview is null && !RoadToolActive)
        {
            return;
        }

        Preview = null;
        RoadToolActive = false;
        _lineRoad = null;
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
        CancelPreview();
        ShowPrompt("Undo last action",
            "Undo restores the entire city to immediately before the last successful action, including money, residents and time. The game will remain paused.",
            [new("Undo", Undo), new("Cancel", ClosePrompt)]);
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
        string title, string text, IReadOnlyList<SessionChoice> choices, string? input = null, string? footer = null)
    {
        Prompt = new(title, text, choices, input, footer);
        Changed?.Invoke();
    }

    public void ClosePrompt()
    {
        Prompt = null;
        Changed?.Invoke();
    }

    public void SelectPrompt(int index)
    {
        if (Prompt is { } prompt && index >= 0 && index < prompt.Choices.Count)
        {
            prompt.Choices[index].Select();
        }
    }

    public void RequestQuit() => GuardProgress("Quit", () => QuitRequested?.Invoke());

    public void RequestNewCity(bool restart) => GuardProgress(restart ? "Restart this seed" : "New city", () =>
        NewGame(Game.Config with { Seed = restart ? Game.Config.Seed : Random.Shared.Next() }));

    public void RequestLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetMessage("Load failed: enter a save-file path.", MessageKind.Error);
            return;
        }

        path = path.Trim().Trim('"');
        GuardProgress("Load city", () =>
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
                    ClosePrompt();
                    action();
                }
            }),
            new(quitting ? "Quit without saving" : "Continue without saving", () => { ClosePrompt(); action(); }),
            new("Cancel", ClosePrompt),
        ],
        footer: $"Quick-save file: {DisplayPath(SavePath)}");
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

    public void ShowSessionMenu()
    {
        CancelPreview();
        ShowPrompt("City menu", $"Seed {Game.Config.Seed} | {Game.Map.Width}x{Game.Map.Height}",
        [
            new("Back to city", ClosePrompt),
            new("Save quick-save", () => { if (QuickSave()) ClosePrompt(); }),
            new("Load city", ShowLoadMenu),
            new("New city (same map size)", () => RequestNewCity(restart: false)),
            new("Restart this seed", () => RequestNewCity(restart: true)),
            new("Undo last action", RequestUndo),
            new("Weekly report / milestones", ShowReport),
            new("First-city guide", ShowGuide),
            new("Quit", RequestQuit),
        ]);
    }

    public void ShowLoadMenu()
    {
        var choices = new List<SessionChoice> { new("Quick-save", () => RequestLoad(SavePath)) };
        for (int i = 1; i <= AutosaveSlots; i++)
        {
            int slot = i;
            choices.Add(new($"Autosave {slot} ({(slot == 1 ? "newest" : "backup")})", () => RequestLoad(AutosavePath(slot))));
        }

        choices.Add(new("Enter a file path", () =>
            ShowPrompt("Load file", OperatingSystem.IsMacOS()
                    ? "Enter a save-file path. Control+A clears the field; Return selects the highlighted button."
                    : "Enter a save-file path. Ctrl+A clears the field; Enter selects the highlighted button.",
                [new("Load", () => RequestLoad(Prompt!.Input!)), new("Cancel", ClosePrompt)], SavePath)));
        choices.Add(new("Cancel", ClosePrompt));
        ShowPrompt("Load city", "Choose a quick-save, autosave, or file path.", choices);
    }

    public void ShowGuide()
    {
        GuideVisible = true;
        ShowPrompt("Your first city",
            $"1. Connect a street to the highways (T draws a line).\n2. Zone nearby homes with R.\n3. Press P to resume. At {Game.Config.MinResidentialCells} occupied homes, shops and factories unlock.",
        [
            new("Start building / keep guide", ClosePrompt),
            new("Dismiss guide", DismissGuide),
        ]);
    }

    public void DismissGuide()
    {
        GuideVisible = false;
        Game.GuideDismissed = true;
        Game.Touch();
        ClosePrompt();
    }

    public void ShowReport()
    {
        var report = Game.LastReport;
        string text = report is null ? "No completed week yet." :
            $"Week {report.Week}: income {Fmt.Money(report.Income)}\nNew homes {report.NewHouseholds}, shops {report.NewCommercial}, factories {report.NewIndustrial}";
        text += $"\nPopulation: {Game.Stats.Population:N0}\nHighest milestone: {Game.HighestMilestone:N0}";
        text += NextMilestone is { } next ? $"\nNext milestone: {next:N0} people" : "\nAll population milestones reached.";
        ShowPrompt("Weekly report and milestones", text, [new("Close", ClosePrompt)]);
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

        ShowPrompt("Growth and road access", string.Join("\n", lines), [new("Close", ClosePrompt)]);
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
            SetMessage($"{Game.Config.MinResidentialCells} occupied homes: shops and factories unlocked. Zone with C and I.", MessageKind.Success);
        }
        else if (_observedWeek != Game.Week && Game.LastReport is { } report && !MessageVisible)
        {
            SetMessage($"Week {report.Week}: +{report.NewHouseholds} homes, +{report.NewCommercial} shops, +{report.NewIndustrial} factories, {Fmt.Money(report.Income)} tax. F7 report.");
        }

        _observedHomes = homes;
        _observedWeek = Game.Week;
    }
}
