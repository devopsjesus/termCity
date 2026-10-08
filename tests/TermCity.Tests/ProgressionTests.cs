using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ProgressionTests
{
    private static void SetPopulation(CityGame game, int population)
    {
        var cottage = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        int cells = Math.Max(game.Stats.Residential.Zoned, Math.Max(100, (population + 4) / 5));
        for (int i = 0; i < cells; i++)
        {
            int x = i % 100, y = 19 + i / 100 * 2;
            game.Map.SetZone(x, y, ZoneType.Residential);
            game.Map.SetBuilding(x, y, cottage);
            int people = Math.Clamp(population - i * 5, 0, 5);
            game.Map.SetHousehold(x, y, new Household((byte)people, 0, 0));
        }
        game.Touch();
    }

    [Fact]
    public void GatesAreAtomicInQuotesPreviewsAndActionsAndRemainOpenAfterDeclineAndLoad()
    {
        var game = TestCity.Flat(config: new GameConfig { StartingMoney = 200_000 }, rules: CityRules.Full);
        var keep = game.Map.Content.Buildings.Get("Stone Keep");
        var area = new CellRect(100, 18, 1, 1);
        SetPopulation(game, 499);
        string before = SaveGameStore.Serialize(game);
        Assert.False(game.CanPlaceBuilding(keep, area.X, area.Y));
        Assert.Equal(0, game.QuoteBuilding(keep, area).Cells);
        Assert.False(game.PlaceBuilding(keep, area).Success);
        Assert.Equal(before, SaveGameStore.Serialize(game));
        var session = new GameSession(game);
        session.PreviewBuilding(keep);
        Assert.Null(session.Preview);
        Assert.Equal(MessageKind.Error, session.MessageKind);
        SetPopulation(game, 500);
        Assert.Contains("Stone Keep", session.Prompt!.Text);
        SetPopulation(game, 0);
        game = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(500, game.HighestMilestone);
        Assert.True(game.PlaceBuilding(keep, area).Success);
    }

    [Fact]
    public void GuideCanBeDisabledButEveryCrossedMilestoneStillAnnouncesAndUnlocks()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game, showGuide: true);
        session.DismissGuide();
        SetPopulation(game, 500);
        Assert.False(session.GuideVisible);
        Assert.True(game.GuideDismissed);
        Assert.Equal("City Grew!", session.Prompt!.Title);
        Assert.Contains("Hamlet", session.Prompt.Text);
        Assert.Contains("Village", session.Prompt.Text);
        Assert.Contains("Tavern", session.Prompt.Text);
        Assert.Contains("Stone Keep", session.Prompt.Text);
        double days = game.ElapsedDays;
        session.Update(0.5);
        Assert.Equal(days, game.ElapsedDays);
        session.ClosePrompt();
        game.Touch();
        Assert.Null(session.Prompt);
        var loaded = new GameSession(SaveGameStore.Deserialize(SaveGameStore.Serialize(game)), showGuide: true);
        Assert.False(loaded.GuideVisible);
        loaded.Game.Touch();
        Assert.Null(loaded.Prompt);
        loaded.ShowGuide();
        loaded.SelectPrompt(1);
        Assert.True(loaded.GuideVisible);
        Assert.False(loaded.Game.GuideDismissed);
    }

    [Fact]
    public void StarterGuideRequestsSuppliesBeforeResumingAndLeavesAUsableTreasury()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.Paused = true;
        game.Designate(new CellRect(10, 18, 30, 1), ZoneType.Residential);
        Assert.Contains("Woodlot", CityProgression.NextStep(game));
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new CellRect(45, 18, 1, 1)).Success);
        Assert.Contains("Town Well", CityProgression.NextStep(game));
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new CellRect(48, 19, 1, 1)).Success);
        Assert.Equal(38_000, game.Money);
        Assert.Equal(60, game.Finance.ServiceUpkeep);
        Assert.Contains("Resume", CityProgression.NextStep(game));
        TestCity.Advance(game, 10);
        Assert.True(game.Stats.Population > 0);
        Assert.True(game.Money > 30_000);
        Assert.Equal(1, game.Services.Power.Ratio);
        Assert.Equal(1, game.Services.Water.Ratio);
    }

    [Fact]
    public void EveryDefaultGateMatchesAnAnnouncedMilestone()
    {
        var game = TestCity.Flat();
        foreach (var building in game.Map.Content.Buildings.Where(b => b.PlayerPlaceable && b.MinPopulation > 0))
        {
            var milestone = Assert.Single(CityProgression.Milestones, m =>
                m.Population == CityProgression.RequiredPopulation(building));
            Assert.Contains(building.Name, CityProgression.Unlocks(game, milestone));
        }
    }

    [Fact]
    public void LockedMenuShowsRealCostAndKeepsTheMenuOpenWhenSelected()
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        var menu = session.Prompt!;
        int index = menu.Choices.ToList().FindIndex(c => c.Label == "Stone Keep");
        Assert.Contains("LOCKED 500", menu.Choices[index].Cells!.Last());
        Assert.Equal("38,000g", menu.Choices[index].Cells![1]);
        session.SelectPrompt(index);
        Assert.Same(menu, session.Prompt);
        Assert.Null(session.Preview);
        Assert.Equal(MessageKind.Error, session.MessageKind);
    }

    [Fact]
    public void DezonedResidentsDoNotLoseOrRepeatTheirMilestone()
    {
        var game = TestCity.Flat(config: new GameConfig { MaxNewResidentialPerWeek = 0 }, rules: CityRules.Full);
        SetPopulation(game, 100);
        Assert.True(game.Dezone(new CellRect(0, 19, 20, 1)).Success);
        Assert.Equal(100, game.Stats.Population);
        Assert.Equal(100, game.HighestMilestone);
        game.AdvanceWeek();
        Assert.Equal(100, game.HighestMilestone);
        Assert.Single(game.Events, e => e.Kind == EventKind.Milestone && e.Message.StartsWith("City Grew!"));
    }

    [Fact]
    public void FinalDayArrivalsEarnTheirMilestoneBeforeWeeklyDeaths()
    {
        var game = TestCity.Flat(config: new GameConfig { PlagueDeathPerWeek = 1 }, rules: CityRules.Full);
        SetPopulation(game, 99);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new CellRect(130, 18, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new CellRect(134, 19, 1, 1)).Success);
        Assert.Equal(0, game.HighestMilestone);
        game.OutbreakWeeksLeft = 2;
        game.Day = 6;
        game.GrowthState = new GrowthState(true, 1, 0, 0, 0, 0, 0);
        game.AdvanceDay();
        Assert.Equal(100, game.HighestMilestone);
        Assert.Equal(0, game.Stats.Population);
        Assert.Contains(game.Events, e => e.Message.StartsWith("City Grew! Hamlet"));
    }

    public static IEnumerable<object[]> GatedBuildings() => new GameContent().Buildings
        .Where(b => b.PlayerPlaceable && b.MinPopulation > 0).Select(b => new object[] { b.Name });

    [Theory]
    [MemberData(nameof(GatedBuildings))]
    public void EveryBuildingUnlocksAtItsExactMilestone(string name)
    {
        var game = TestCity.Flat(config: new GameConfig { StartingMoney = 1_000_000 }, rules: CityRules.Full);
        var type = game.Map.Content.Buildings.Get(name);
        int threshold = CityProgression.RequiredPopulation(type);
        var area = new CellRect(110, 21, 1, 1);
        SetPopulation(game, threshold - 1);
        Assert.False(game.PlaceBuilding(type, area).Success);
        Assert.Equal(0, game.QuoteBuilding(type, area).Cells);
        SetPopulation(game, threshold);
        Assert.True(game.QuoteBuilding(type, area).Cells > 0);
        Assert.True(game.PlaceBuilding(type, area).Success);
    }
}
