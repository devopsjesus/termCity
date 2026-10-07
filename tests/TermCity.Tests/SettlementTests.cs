using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

/// <summary>Town rank, charters and the crown's tribute.</summary>
public class SettlementTests
{
    private static CityGame Hamlet(List<CityEvent>? events = null, int weeks = 30)
    {
        var game = TestCity.Flat(1, new GameConfig { StartingMoney = 2_000_000, DefaultTaxRate = 0.09 }, CityRules.Full);
        if (events is not null)
        {
            game.EventOccurred += events.Add;
        }

        foreach (int y in new[] { 18, 21 })
        {
            Assert.True(game.Designate(new CellRect(10, y, 50, 2), ZoneType.Residential).Success);
        }

        Assert.True(game.Designate(new CellRect(60, 18, 10, 2), ZoneType.Commercial).Success);
        Assert.True(game.Designate(new CellRect(60, 21, 10, 2), ZoneType.Industrial).Success);
        foreach (var (name, x) in new[] { ("Charcoal Burners", 80), ("Town Well", 82), ("Town Well", 84) })
        {
            Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get(name), new CellRect(x, 19, 1, 1)).Success);
        }

        TestCity.Advance(game, weeks);
        return game;
    }

    private static CityGame Seeded() =>
        CityGame.New(new GameConfig { Scenario = CityScenario.Lubeck, MapWidth = 640, MapHeight = 384, Seed = 5 });

    [Fact]
    public void RanksHaveNamesAndPrivileges()
    {
        foreach (var rank in Enum.GetValues<TownRank>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Settlement.Name(rank)));
            Assert.False(string.IsNullOrWhiteSpace(Settlement.Privilege(rank)));
        }

        Assert.Equal("Market Town", Settlement.Name(TownRank.MarketTown));
        Assert.Equal(39, Settlement.TributeWeek(52));
    }

    [Fact]
    public void ANewHamletHasNoStandingAndOwesNothing()
    {
        var game = TestCity.Flat(1, new GameConfig(), CityRules.Full);
        Assert.Equal(TownRank.Hamlet, game.Rank);
        Assert.Equal(0, Settlement.TributeDue(game));
    }

    [Fact]
    public void ClassicRulesHaveNoRankOrTribute()
    {
        var game = TestCity.Flat(1, new GameConfig(), CityRules.Classic);
        Assert.Equal(TownRank.Hamlet, game.Rank);
        Assert.Equal(0, Settlement.TributeDue(game));
    }

    [Fact]
    public void AGrowingHamletBecomesAVillageAndIsToldOnce()
    {
        var events = new List<CityEvent>();
        var game = Hamlet(events);
        Assert.True(game.Stats.Population >= 120);
        Assert.Equal(TownRank.Village, game.Rank);
        Assert.Equal(1, game.HighestRank);
        var promotions = events.Where(e => e.Kind == EventKind.Milestone && e.Message.Contains("Village")).ToList();
        Assert.Single(promotions);
        Assert.False(promotions[0].Bad);
    }

    [Fact]
    public void ABigSeededTownWithAMarketAKeepAndAChurchIsAChartedCity()
    {
        var game = Seeded();
        Assert.True(game.Stats.Population > 25_000);
        Assert.True(game.Services.SeatRank >= 2);
        Assert.Equal(TownRank.City, game.Rank);
    }

    [Fact]
    public void WithoutAMarketABigTownIsOnlyAVillage()
    {
        var game = Seeded();
        game.SetFunding(ServiceKind.Trade, 0);
        game.Touch();
        Assert.True(game.Indicators.CoverageOf(ServiceKind.Trade) < 15);
        Assert.Equal(TownRank.Village, game.Rank);
    }

    [Fact]
    public void ChartersRaiseTradeDuesByThreePercentARank()
    {
        Assert.Equal(1.0, Settlement.CharterDues(TownRank.Hamlet));
        Assert.Equal(1.03, Settlement.CharterDues(TownRank.Village), 6);
        Assert.Equal(1.12, Settlement.CharterDues(TownRank.City), 6);

        var chartered = Seeded();
        var uncharted = Seeded();
        uncharted.SetFunding(ServiceKind.Trade, 0);
        uncharted.Touch();
        // Same city; one has lost its market, so its traders pay less (the market toll and the charter both).
        Assert.True(chartered.Finance.Income > uncharted.Finance.Income);
    }

    [Fact]
    public void TheCrownsTributeIsTrimmedByTheSeatAndTheCharter()
    {
        var game = Seeded();
        int due = Settlement.TributeDue(game);
        double bare = game.Stats.Population * game.Config.TributePerSoul;
        Assert.InRange(due, 1, (int)(bare * 0.8));

        var weaker = Seeded();
        weaker.SetFunding(ServiceKind.Trade, 0);
        weaker.Touch();
        Assert.True(Settlement.TributeDue(weaker) > due);
    }

    [Fact]
    public void TributeIsCollectedAtMichaelmas()
    {
        var game = Seeded();
        game.Money = 50_000_000;
        game.Week = Settlement.TributeWeek(52) - 1;
        Assert.Equal(Settlement.TributeWeek(52), game.WeekOfYear);
        int due = Settlement.TributeDue(game);
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        Settlement.RunWeek(game);
        Assert.Equal(50_000_000 - due, game.Money);
        Assert.Equal(0, game.TributeArrears);
        Assert.Contains(events, e => e.Kind == EventKind.Finance && e.Message.Contains("tribute") && !e.Bad);
    }

    [Fact]
    public void NothingIsCollectedOnAnOrdinaryWeek()
    {
        var game = Seeded();
        long before = game.Money;
        game.Week = 5;
        Settlement.RunWeek(game);
        Assert.Equal(before, game.Money);
    }

    [Fact]
    public void ATownThatCannotPayOwesTheRestNextYear()
    {
        var game = Seeded();
        game.Money = 100;
        game.Week = Settlement.TributeWeek(52) - 1;
        int due = Settlement.TributeDue(game);
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        Settlement.RunWeek(game);
        Assert.Equal(0, game.Money);
        Assert.Equal(due - 100, game.TributeArrears);
        Assert.Contains(events, e => e.Kind == EventKind.Finance && e.Bad && e.Message.Contains("owing"));
        Assert.True(Settlement.TributeDue(game) > due);

        // Paid up the next year, the debt is gone.
        game.Money = 500_000_000;
        game.Week += 52;
        Settlement.RunWeek(game);
        Assert.Equal(0, game.TributeArrears);
    }

    [Fact]
    public void RankAndArrearsSurviveASaveAndOldSavesLoadWithoutThem()
    {
        var game = Hamlet();
        game.TributeArrears = 1234;
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(1234, loaded.TributeArrears);
        Assert.Equal(game.HighestRank, loaded.HighestRank);

        var node = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        foreach (var key in node.Select(p => p.Key).Where(k => k.Equals("HighestRank", StringComparison.OrdinalIgnoreCase) ||
                                                                k.Equals("TributeArrears", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
        }

        var old = SaveGameStore.Deserialize(node.ToJsonString());
        Assert.Equal(-1, old.HighestRank);
        Assert.Equal(0, old.TributeArrears);
    }

    [Fact]
    public void TheReportsShowStandingSeasonAndTribute()
    {
        var game = Seeded();
        string overview = CityReport.Overview(game);
        Assert.Contains("Standing City", overview);
        Assert.Contains("Grain", overview);
        string health = CityReport.Health(game);
        Assert.Contains("tribute", health);
        Assert.Contains("grain store", health);
    }
}
