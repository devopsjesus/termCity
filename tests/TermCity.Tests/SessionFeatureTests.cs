using TermCity.Core.Persistence;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SessionFeatureTests
{
    private static GameSession Session() => new(TestCity.Flat(),
        Path.Combine(Path.GetTempPath(), "termcity-session-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public void PreviewDoesNotSpendOrAdvanceAndConfirmationCanBeUndone()
    {
        var session = Session();
        session.BeginDrag(new Pos(10, 18));
        session.UpdateDrag(new Pos(12, 18));
        session.EndSelection();
        string before = SaveGameStore.Serialize(session.Game);
        session.PreviewRoad();
        Assert.Equal(3, session.Preview!.Quote.Cells);
        Assert.Equal(1_500, session.Preview.Quote.Cost);
        session.Update(0.5);
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(48_500, session.Game.Money);
        Assert.Null(session.Preview);
        session.RequestUndo();
        Assert.True(session.Game.Paused);
        Assert.Contains("entire city", session.Prompt!.Text);
        session.SelectPrompt(0);
        session.Game.Paused = false;
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void PreviewMarksWaterAndZonesAsBlockedAndCanBeCancelled()
    {
        var session = Session();
        var game = session.Game;
        game.Map.SetTerrain(11, 18, game.Map.Content.Terrains.Get("Water"));
        game.Designate(CellRect.Single(new Pos(12, 18)), ZoneType.Residential);
        session.BeginDrag(new Pos(10, 18));
        session.UpdateDrag(new Pos(13, 18));
        session.EndSelection();
        session.PreviewRoad();
        var preview = session.Preview!;
        Assert.Equal(new Quote(2, 1_000, 2), preview.Quote);
        Assert.True(preview.IsValid(game, 10, 18));
        Assert.False(preview.IsValid(game, 11, 18));
        Assert.False(preview.IsValid(game, 12, 18));
        session.CancelPreview();
        Assert.Null(session.Preview);
        Assert.Equal(50_000, game.Money);
    }

    [Fact]
    public void FailedConfirmationKeepsPreviewAndSurfacesError()
    {
        var session = Session();
        session.Game.Money = 1;
        session.PlaceCursor(new Pos(10, 18));
        session.PreviewRoad();
        Assert.False(session.ConfirmPreview().Success);
        Assert.NotNull(session.Preview);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Contains("Not enough money", session.Message);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void CustomBuildingPreviewQuotesTerrainAndSupportsUndo()
    {
        var session = Session();
        var game = session.Game;
        var building = game.Map.Content.Buildings.Register(new BuildingType
        {
            Name = "Clinic",
            Glyphs = ["X"],
            Foreground = Rgb.Hex(0xffffff),
            Cost = 1_000,
            PlayerPlaceable = true,
        });
        game.Map.SetTerrain(11, 18, game.Map.Content.Terrains.Get("Hill"));
        game.Touch();
        session.BeginDrag(new Pos(10, 18));
        session.UpdateDrag(new Pos(11, 18));
        session.EndSelection();
        string before = SaveGameStore.Serialize(game);
        session.PreviewBuilding(building);
        Assert.Equal(new Quote(2, 2_500, 0), session.Preview!.Quote);
        Assert.Equal(50_000, game.Money);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(47_500, game.Money);
        session.RequestUndo();
        session.SelectPrompt(0);
        session.Game.Paused = false;
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(1)]
    public void RoadToolDrawsOneCellWideOnDominantAxisAtEveryZoom(int zoom)
    {
        var session = Session();
        session.SetZoom(zoom);
        session.PlaceCursor(new Pos(10, 18));
        session.BeginRoadLine();
        session.UpdateDrag(new Pos(15, 19));
        Assert.Equal(new CellRect(10, 18, 6, 1), session.Preview!.Area);
        session.UpdateDrag(new Pos(11, 13));
        Assert.Equal(new CellRect(10, 13, 1, 6), session.Preview.Area);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(6, session.Game.Map.RoadCells.Count(i => i % session.Game.Map.Width == 10 && i / session.Game.Map.Width < 20));
        Assert.False(session.RoadToolActive);
    }

    [Fact]
    public void RoadLineClearlyIdentifiesGapsAndQuotesUpgrades()
    {
        var session = Session();
        var game = session.Game;
        game.Map.SetTerrain(11, 18, game.Map.Content.Terrains.Get("Water"));
        game.Map.SetRoad(12, 18, game.DefaultRoad);
        game.Touch();
        session.PlaceCursor(new Pos(10, 18));
        session.BeginRoadLine(game.Map.Content.Roads.Get("Avenue"));
        session.UpdateDrag(new Pos(12, 18));
        Assert.Equal(new Quote(2, 1_300, 1), session.Preview!.Quote);
        Assert.Contains("gaps", session.Preview.Name);
    }

    [Fact]
    public void ExistingRoadCellsAreSkippedButAreNotGapsInALine()
    {
        var session = Session();
        session.Game.Map.SetRoad(11, 18, session.Game.DefaultRoad);
        session.Game.Touch();
        session.PlaceCursor(new Pos(10, 18));
        session.BeginRoadLine();
        session.UpdateDrag(new Pos(12, 18));
        Assert.Equal(new Quote(2, 1_000, 1), session.Preview!.Quote);
        Assert.DoesNotContain("gaps", session.Preview.Name);
    }

    [Fact]
    public void DemolitionPreviewAndUndoRestoreHighwaysFeaturesAndHouseholds()
    {
        var session = Session();
        var game = session.Game;
        FillHomes(game, 1);
        game.Map.SetFeature(1, 21, game.Map.Content.Features.Get("Tree"));
        game.Touch();
        session.BeginDrag(new Pos(0, 20));
        session.UpdateDrag(new Pos(1, 21));
        session.EndSelection();
        string before = SaveGameStore.Serialize(game);
        session.PreviewDemolish();
        Assert.Equal(4, session.Preview!.Quote.Cells);
        Assert.True(session.ConfirmPreview().Success);
        session.RequestUndo();
        session.SelectPrompt(0);
        session.Game.Paused = false;
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
    }

    [Fact]
    public void UndoAllowsExactlySevenDaysWhileRunningAndOlderActionsWhilePaused()
    {
        var session = Session();
        session.PlaceCursor(new Pos(10, 18));
        Assert.True(session.BuildRoad().Success);
        for (int i = 0; i < 7; i++) session.Game.AdvanceDay();
        Assert.True(session.CanUndo);
        session.Game.Update(0.1);
        Assert.False(session.CanUndo);
        session.RequestUndo();
        Assert.Null(session.Prompt);
        Assert.False(session.Game.Paused);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        session.TogglePause();
        Assert.True(session.CanUndo);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(0, session.Game.Week);
        Assert.Equal(0, session.Game.Day);
        Assert.True(session.Game.Paused);
        Assert.False(session.Game.Map.HasRoad(10, 18));
    }

    [Fact]
    public void CancellingUndoKeepsCityIntactAndPaused()
    {
        var session = Session();
        session.PlaceCursor(new Pos(10, 18));
        session.BuildRoad();
        session.RequestUndo();
        session.SelectPrompt(1);
        Assert.True(session.Game.Paused);
        Assert.True(session.Game.Map.HasRoad(10, 18));
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void FailedActionsDoNotReplaceTheUndoSnapshot()
    {
        var session = Session();
        session.PlaceCursor(new Pos(10, 18));
        session.BuildRoad();
        Assert.False(session.BuildRoad().Success);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(50_000, session.Game.Money);
        Assert.False(session.Game.Map.HasRoad(10, 18));
    }

    [Fact]
    public void MidweekRestorePreservesPlansReportsRandomnessAndCustomTiming()
    {
        var session = new GameSession(TestCity.Flat(config: new GameConfig { SlowSecondsPerWeek = 14 }));
        session.Game.Designate(new CellRect(0, 21, 30, 1), ZoneType.Residential);
        session.Game.AdvanceWeek();
        for (int i = 0; i < 3; i++) session.Game.AdvanceDay();
        session.Game.Update(0.1);
        string before = SaveGameStore.Serialize(session.Game);
        var control = SaveGameStore.Deserialize(before, preserveTimings: true);
        session.PlaceCursor(new Pos(50, 18));
        session.BuildRoad();
        session.Game.AdvanceWeek();
        session.RequestUndo();
        session.SelectPrompt(0);
        session.Game.Paused = false;
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
        session.Game.AdvanceWeek();
        control.AdvanceWeek();
        Assert.Equal(SaveGameStore.Serialize(control), SaveGameStore.Serialize(session.Game));
    }

    [Fact]
    public void RotatingAutosavesRetainThreeStatesWithoutOverwritingQuickSave()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        Assert.True(session.QuickSave());
        string manual = File.ReadAllText(session.SavePath);
        for (int i = 1; i <= 4; i++)
        {
            session.Game.Money = i;
            session.Game.Touch();
            Assert.True(session.Autosave());
        }

        Assert.Equal(4, SaveGameStore.Load(session.AutosavePath(1)).Money);
        Assert.Equal(3, SaveGameStore.Load(session.AutosavePath(2)).Money);
        Assert.Equal(2, SaveGameStore.Load(session.AutosavePath(3)).Money);
        Assert.Equal(manual, File.ReadAllText(session.SavePath));
        Assert.True(session.LoadFrom(session.AutosavePath(2)));
        Assert.True(session.Game.Paused);
        Assert.Equal(3, session.Game.Money);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AutosaveRunsAfterSixtySecondsAndSkipsUnchangedPausedCity()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        session.Game.Paused = true;
        session.Update(59);
        Assert.False(File.Exists(session.AutosavePath(1)));
        session.Update(1);
        Assert.True(File.Exists(session.AutosavePath(1)));
        session.Update(60);
        Assert.False(File.Exists(session.AutosavePath(2)));
        session.Game.Money--;
        session.Game.Touch();
        session.Update(60);
        Assert.True(File.Exists(session.AutosavePath(2)));
    }

    [Fact]
    public void SaveFailuresPreventContinuationAndAreVisible()
    {
        using var files = new SaveFiles();
        string blocker = Path.Combine(files.DirectoryPath, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var session = new GameSession(TestCity.Flat(), Path.Combine(blocker, "quicksave.json"));
        bool quit = false;
        session.QuitRequested += () => quit = true;
        session.RequestQuit();
        session.SelectPrompt(0);
        Assert.False(quit);
        Assert.NotNull(session.Prompt);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        Assert.Contains("Save failed", session.Message);
        Assert.False(session.Autosave());
        Assert.Contains("Autosave failed", session.Message);
    }

    [Fact]
    public void RotationFailureDoesNotDestroyTheNewestAutosave()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        session.Autosave();
        session.Game.Money = 42;
        session.Autosave();
        string latest = File.ReadAllText(session.AutosavePath(1));
        string blocker = session.AutosavePath(3);
        Directory.CreateDirectory(blocker);
        try
        {
            session.Game.Money = 43;
            Assert.False(session.Autosave());
            Assert.Equal(latest, File.ReadAllText(session.AutosavePath(1)));
            Assert.Equal(MessageKind.Error, session.MessageKind);
        }
        finally
        {
            Directory.Delete(blocker);
        }
    }

    [Fact]
    public void QuitPromptSupportsSavingDiscardingAndCancelling()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        bool quit = false;
        session.QuitRequested += () => quit = true;
        session.RequestQuit();
        session.SelectPrompt(2);
        Assert.False(quit);
        session.RequestQuit();
        session.SelectPrompt(0);
        Assert.True(quit);
        Assert.True(File.Exists(session.SavePath));
        Assert.False(session.HasUnsavedChanges);
        quit = false;
        session.Game.AdvanceDay();
        Assert.True(session.HasUnsavedChanges);
        session.RequestQuit();
        session.SelectPrompt(1);
        Assert.True(quit);
    }

    [Fact]
    public void RestartAndLoadGuardUnsavedProgressAndResetUndo()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        session.PlaceCursor(new Pos(10, 18));
        session.BuildRoad();
        int seed = session.Game.Config.Seed;
        session.RequestNewCity(restart: true);
        session.SelectPrompt(2);
        Assert.True(session.Game.Map.HasRoad(10, 18));
        session.RequestNewCity(restart: true);
        session.SelectPrompt(1);
        Assert.Equal(seed, session.Game.Config.Seed);
        Assert.Equal(50_000, session.Game.Money);
        Assert.True(session.Game.Paused);
        Assert.True(session.GuideVisible);
        Assert.False(session.CanUndo);
        Assert.Contains("first city", session.Prompt!.Title);
        session.ClosePrompt();
        session.QuickSave();
        session.PlaceCursor(new Pos(20, 18));
        session.Zone(ZoneType.Residential);
        session.RequestLoad(session.SavePath);
        session.SelectPrompt(1);
        Assert.Equal(0, session.Game.Stats.Residential.Zoned);
        Assert.True(session.Game.Paused);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void PromptsFreezeSimulationAndFreshCityGuideCanBeDismissedAndSaved()
    {
        var session = new GameSession(TestCity.Flat(), showGuide: true);
        Assert.True(session.Game.Paused);
        Assert.True(session.GuideVisible);
        Assert.NotNull(session.Prompt);
        session.SelectPrompt(0);
        session.SetSpeed(GameSpeed.Fast);
        session.ShowReport();
        double before = session.Game.ElapsedDays;
        session.Update(0.5);
        Assert.Equal(before, session.Game.ElapsedDays);
        session.ClosePrompt();
        session.DismissGuide();
        Assert.False(session.GuideVisible);
        Assert.True(SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game)).GuideDismissed);
    }

    [Fact]
    public void MilestonesAreInclusivePersistedAndNotRepeatedAfterLoad()
    {
        using var files = new SaveFiles();
        var session = files.Session();
        FillHomes(session.Game, 20, people: 5);
        Assert.Equal(100, session.Game.HighestMilestone);
        Assert.Contains("milestone: 100", session.Message);
        session.QuickSave();
        Assert.True(session.LoadFrom(session.SavePath));
        session.SetMessage("");
        session.Game.Touch();
        Assert.DoesNotContain("milestone", session.Message);
        FillHomes(session.Game, 100, people: 5);
        Assert.Equal(500, session.Game.HighestMilestone);
        Assert.Equal(1_000, session.NextMilestone);
        session.ShowReport();
        Assert.Contains("Highest milestone: 500", session.Prompt!.Text);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public void EveryMilestoneTriggersAtItsExactPopulation(int threshold)
    {
        var session = Session();
        var game = session.Game;
        Pos last = default;
        for (int i = 0; i < threshold / 5; i++)
        {
            last = game.Map.PosOf(game.Map.Width * 30 + i);
            game.Map.SetZone(last.X, last.Y, ZoneType.Residential);
            game.Map.SetBuilding(last.X, last.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
            game.Map.SetHousehold(last.X, last.Y, new Household(3, 2, 0));
        }

        game.Map.SetHousehold(last.X, last.Y, new Household(2, 2, 0));
        game.Touch();
        Assert.Equal(threshold - 1, game.Stats.Population);
        Assert.True(game.HighestMilestone < threshold);
        game.Map.SetHousehold(last.X, last.Y, new Household(3, 2, 0));
        game.Touch();
        Assert.Equal(threshold, game.HighestMilestone);
    }

    [Fact]
    public void TenHomesUnlockMessageAndWeeklyReportReflectActualSimulation()
    {
        var session = Session();
        FillHomes(session.Game, 9, people: 2);
        session.SetMessage("");
        FillHomes(session.Game, 10, people: 2);
        Assert.Contains("shops and factories unlocked", session.Message);
        session.Game.Designate(new CellRect(10, 21, 20, 1), ZoneType.Residential);
        session.Game.AdvanceWeek();
        session.ShowReport();
        Assert.Contains($"New homes {session.Game.LastReport!.NewHouseholds}", session.Prompt!.Text);
        Assert.Contains(Fmt.Money(session.Game.LastReport.Income), session.Prompt.Text);
    }

    [Theory]
    [InlineData(ZoneType.Commercial, 20, 21)]
    [InlineData(ZoneType.Industrial, 10, 11)]
    public void GrowthDiagnosticsUseExactUnlockAndCapacityThresholds(ZoneType zone, int homes, int next)
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(0, 19, 2, 1), zone);
        FillHomes(game, 9);
        Assert.Equal(GrowthStatus.NeedsHomes, GrowthDiagnostics.ForCell(game, 1, 19).Status);
        FillHomes(game, 10);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForCell(game, 1, 19).Status);
        game.Map.SetBuilding(0, 19, game.Map.Content.Buildings.ForZone(zone)!);
        FillHomes(game, homes);
        var diagnostic = GrowthDiagnostics.ForCell(game, 1, 19);
        Assert.Equal(GrowthStatus.CapacityReached, diagnostic.Status);
        Assert.Contains($"at {next} occupied homes", diagnostic.Message);
        FillHomes(game, next);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForCell(game, 1, 19).Status);
    }

    [Fact]
    public void GrowthDiagnosticsAreReadOnlyAndPauseDoesNotHideStructuralBlockers()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(10, 18, 2, 1), ZoneType.Residential);
        game.Map.SetTerrain(10, 19, game.Map.Content.Terrains.Get("Water"));
        game.Map.SetTerrain(11, 19, game.Map.Content.Terrains.Get("Water"));
        game.Paused = true;
        game.Touch();
        string before = SaveGameStore.Serialize(game);
        Assert.Equal(GrowthStatus.NoRoadAccess, GrowthDiagnostics.ForCell(game, 10, 18).Status);
        Assert.True(GrowthDiagnostics.ForCell(game, 10, 18).Paused);
        Assert.Contains("Paused", CellInspector.Summary(game, new Pos(10, 18)));
        var diagnostic = GrowthDiagnostics.ForZone(game, ZoneType.Residential);
        Assert.Equal(0, diagnostic.EligibleVacancies);
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void GrowthSummaryCountsServedVacanciesNotOccupiedCells()
    {
        var game = TestCity.Flat();
        FillHomes(game, 10);
        game.Designate(new CellRect(10, 21, 2, 1), ZoneType.Residential);
        game.Designate(new CellRect(0, 30, 5, 1), ZoneType.Residential);
        Assert.Equal(2, GrowthDiagnostics.ForZone(game, ZoneType.Residential).EligibleVacancies);
        game.Designate(new CellRect(0, 19, 2, 1), ZoneType.Commercial);
        Assert.Equal(0, game.Demand.Commercial);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
    }

    [Fact]
    public void DisconnectedRoadsDoNotMakeVacantCellsEligible()
    {
        var game = TestCity.Flat();
        game.Map.SetRoad(0, 20, false);
        game.Map.SetRoad(game.Map.Width - 1, 20, false);
        game.Touch();
        game.Designate(new CellRect(10, 21, 2, 1), ZoneType.Residential);
        var diagnostic = GrowthDiagnostics.ForZone(game, ZoneType.Residential);
        Assert.Equal(GrowthStatus.NoRoadAccess, diagnostic.Status);
        Assert.Equal(0, diagnostic.EligibleVacancies);
        Assert.Contains("map edge", diagnostic.Message);
    }

    private static void FillHomes(CityGame game, int count, byte people = 2)
    {
        for (int x = 0; x < count; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
            game.Map.SetHousehold(x, 21, new Household(people, 0, 0));
        }

        game.Touch();
    }

    private sealed class SaveFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "termcity-session-files-" + Guid.NewGuid().ToString("N"));
        public SaveFiles() => Directory.CreateDirectory(DirectoryPath);
        public GameSession Session() => new(TestCity.Flat(), Path.Combine(DirectoryPath, "quicksave.json"));
        public void Dispose()
        {
            foreach (string file in Directory.GetFiles(DirectoryPath)) File.Delete(file);
            Directory.Delete(DirectoryPath);
        }
    }
}
