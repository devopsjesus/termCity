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
        var homes = game.Map.BuildingFootprints.Values
            .Where(area => game.Map.BuildingAt(area.X, area.Y)!.Name == cottage.Name).ToList();
        int count = Math.Max(homes.Count, Math.Max(100, (population + 4) / 5));
        for (int y = 60; homes.Count < count && y + cottage.Height <= game.Map.Height; y += cottage.Height)
            for (int x = 0; homes.Count < count && x + cottage.Width <= game.Map.Width; x += cottage.Width)
            {
                var area = cottage.FootprintAt(x, y);
                if (area.Cells().Any(p => game.Map.BuildingAt(p.X, p.Y) is not null ||
                    game.Map.HasRoad(p.X, p.Y) || game.Settlement is { } siege && siege.Bounds.Contains(p))) continue;
                homes.Add(TestCity.Stamp(game, cottage.Name, new(x, y)));
            }
        Assert.Equal(count, homes.Count);
        for (int i = 0; i < homes.Count; i++)
        {
            int people = Math.Clamp(population - i * 5, 0, 5);
            game.Map.SetHousehold(homes[i].X, homes[i].Y, new Household((byte)people, 0, 0));
        }
        game.Touch();
    }

    [Fact]
    public void GrowingHamletUnlocksCareAtExactly250AndKeepsItAfterSaveAndDecline()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        SetPopulation(game, 249);
        Assert.Equal(100, game.HighestMilestone);
        string[] names = ["Fire Watch", "Watch House", "Sheriff's Hall", "Apothecary", "Chantry School"];
        Assert.All(names, name => Assert.False(CityProgression.IsUnlocked(game, game.Map.Content.Buildings.Get(name))));
        int money = game.Money;
        SetPopulation(game, 250);
        Assert.Equal(250, game.HighestMilestone);
        Assert.Equal(money + CityProgression.GoldBonus(game, CityProgression.Milestones.Single(m => m.Population == 250)), game.Money);
        Assert.Contains(game.Events, e => e.Message.Contains("Growing Hamlet") && e.Message.Contains("Fire Watch"));
        SetPopulation(game, 20);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.All(names, name => Assert.True(CityProgression.IsUnlocked(loaded, loaded.Map.Content.Buildings.Get(name))));
        Assert.False(loaded.GrainRequired);
        Assert.False(CityProgression.IsUnlocked(loaded, loaded.Map.Content.Buildings.Get("Granary")));
    }

    [Fact]
    public void GatesAreAtomicInQuotesPreviewsAndActionsAndRemainOpenAfterDeclineAndLoad()
    {
        var game = TestCity.Flat(config: new GameConfig { StartingMoney = 200_000 }, rules: CityRules.Full);
        var keep = game.Map.Content.Buildings.Get("Stone Keep");
        var area = new CellRect(110, 40, 1, 1);
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
        Assert.Contains("STONE KEEP", session.Prompt!.Text);
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
        Assert.Equal("Your City Grew!", session.Prompt!.Title);
        Assert.Equal(2, session.Prompt.ActiveTab);
        Assert.Null(session.Prompt.Tabs);
        Assert.Equal("Continue", Assert.Single(session.Prompt.Choices).Label);
        Assert.Contains("VILLAGE GREEN", session.Prompt.Text);
        Assert.DoesNotContain("Tavern", session.Prompt.Text);
        Assert.Contains("STONE KEEP", session.Prompt.Text);
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
        game.Designate(new CellRect(10, 10, 50, 10), ZoneType.Residential);
        Assert.Contains("Woodlot", CityProgression.NextStep(game));
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new CellRect(65, 10, 1, 1)).Success);
        Assert.Contains("Town Well", CityProgression.NextStep(game));
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new CellRect(77, 10, 1, 1)).Success);
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
        foreach (var building in game.Map.Content.Buildings.Where(b => b.PlayerPlaceable && !b.DebugOnly && b.MinPopulation > 0))
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
        Assert.True(game.Dezone(new CellRect(0, 60, 100, 5)).Success);
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
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new CellRect(130, 45, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new CellRect(142, 45, 1, 1)).Success);
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
        .Where(b => b.PlayerPlaceable && !b.DebugOnly && b.MinPopulation > 0).Select(b => new object[] { b.Name });

    [Theory]
    [MemberData(nameof(GatedBuildings))]
    public void EveryBuildingUnlocksAtItsExactMilestone(string name)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            StartingMoney = 1_000_000, MapWidth = 320, MapHeight = 192,
        }, rules: CityRules.Full);
        var type = game.Map.Content.Buildings.Get(name);
        int threshold = CityProgression.RequiredPopulation(type);
        var area = new CellRect(110, 21, 1, 1);
        if (SettlementState.RosterCapacity(type) > 0)
        {
            Assert.True(game.FoundSettlement(new(106, 25, 48, 48)).Success);
            area = new(110, 29, 1, 1);
        }
        SetPopulation(game, threshold - 1);
        Assert.False(game.PlaceBuilding(type, area).Success);
        Assert.Equal(0, game.QuoteBuilding(type, area).Cells);
        SetPopulation(game, threshold);
        Assert.True(game.QuoteBuilding(type, area).Cells > 0);
        Assert.True(game.PlaceBuilding(type, area).Success);
    }
}
