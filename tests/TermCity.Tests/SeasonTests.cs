using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

/// <summary>The farming year, grain and famine, plague, raids and pilgrim feasts.</summary>
public class SeasonTests
{
    /// <summary>A hamlet with fuel and water, a gap in the middle of the houses for a service building, and some weeks of life.</summary>
    private static CityGame Village(string? service = null, GameConfig? config = null, int weeks = 30, string? second = null)
    {
        var game = TestCity.Flat(1, config ?? new GameConfig { StartingMoney = 2_000_000, DefaultTaxRate = 0.09 }, CityRules.Full);
        foreach (int y in new[] { 18, 21 })
        {
            Assert.True(game.Designate(new CellRect(10, y, 20, 2), ZoneType.Residential).Success);
            Assert.True(game.Designate(new CellRect(31, y, 20, 2), ZoneType.Residential).Success);
        }

        Assert.True(game.Designate(new CellRect(60, 18, 10, 2), ZoneType.Commercial).Success);
        Assert.True(game.Designate(new CellRect(60, 21, 10, 2), ZoneType.Industrial).Success);
        foreach (var (name, x) in new[] { ("Charcoal Burners", 80), ("Town Well", 82), ("Town Well", 84) })
        {
            Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get(name), new CellRect(x, 19, 1, 1)).Success);
        }

        if (service is not null)
        {
            Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get(service), new CellRect(30, 19, 1, 1)).Success);
        }

        if (second is not null)
        {
            Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get(second), new CellRect(30, 21, 1, 1)).Success);
        }

        TestCity.Advance(game, weeks);
        Assert.True(game.Stats.Population > 40);
        return game;
    }

    private static CityGame Copy(CityGame game) => SaveGameStore.Deserialize(SaveGameStore.Serialize(game));

    [Fact]
    public void TheYearRunsFromMidwinterThroughFourSeasons()
    {
        Assert.Equal(Season.Winter, Seasons.Of(1, 52));
        Assert.Equal(Season.Winter, Seasons.Of(9, 52));
        Assert.Equal(Season.Spring, Seasons.Of(10, 52));
        Assert.Equal(Season.Summer, Seasons.Of(23, 52));
        Assert.Equal(Season.Autumn, Seasons.Of(36, 52));
        Assert.Equal(Season.Autumn, Seasons.Of(48, 52));
        Assert.Equal(Season.Winter, Seasons.Of(49, 52));
        Assert.Equal(Season.Winter, Seasons.Of(52, 52));
        Assert.Equal("Autumn", Seasons.Name(Season.Autumn));
    }

    [Fact]
    public void AnOrdinaryYearsFieldsJustFeedTheTownAndSummerIsTheDangerousSeasonForPlagueAndFire()
    {
        var all = new[] { Season.Winter, Season.Spring, Season.Summer, Season.Autumn };
        Assert.Equal(1.0, all.Average(Seasons.FieldYield), 6);
        Assert.True(Seasons.FieldYield(Season.Autumn) > Seasons.FieldYield(Season.Winter));
        Assert.Equal(Season.Summer, all.MaxBy(Seasons.PlagueFactor));
        Assert.Equal(Season.Summer, all.MaxBy(Seasons.FireFactor));
        Assert.Equal(Season.Winter, all.MinBy(Seasons.Travel));
    }

    [Fact]
    public void SeasonsSetTheFeastDays()
    {
        Assert.Equal("Lady Day", Feasts.FeastOf(13, 52));
        Assert.Equal("Midsummer", Feasts.FeastOf(26, 52));
        Assert.Equal("Michaelmas", Feasts.FeastOf(39, 52));
        Assert.Equal("Christmas", Feasts.FeastOf(52, 52));
        Assert.Null(Feasts.FeastOf(14, 52));
    }

    [Fact]
    public void AFullStoreFeedsTheTownThroughAThinWinterWeek()
    {
        var game = Village();
        game.Week = 0;
        game.GrainWeeks = 5;
        game.HarvestQuality = 1;
        game.Money = 0;
        Harvest.RunWeek(game, new WeekTally());
        Assert.Equal(0, game.Hunger);
        Assert.InRange(game.GrainWeeks, 4.5, 4.7);
    }

    [Fact]
    public void TheBarnFillsAtHarvestButNeverPastItsCapacity()
    {
        var game = Village();
        game.Week = 40;
        game.GrainWeeks = 9.9;
        game.HarvestQuality = 1.5;
        Harvest.RunWeek(game, new WeekTally());
        Assert.True(game.GrainWeeks <= Harvest.StoreCapacity(game) + 1e-9);
        Assert.Equal(game.Config.HouseholdStoreWeeks, game.GrainWeeks, 6);
    }

    [Fact]
    public void AnEmptyStoreAndAFullTreasuryBuysGrainAndNobodyStarves()
    {
        var game = Village();
        game.Week = 0;
        game.GrainWeeks = 0;
        game.HarvestQuality = 0.4;
        game.Money = 1_000_000;
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        Harvest.RunWeek(game, new WeekTally());
        Assert.True(game.Money < 1_000_000);
        Assert.True(game.BuyingGrain);
        Assert.Contains(events, e => e.Kind == EventKind.Harvest);
        Assert.True(game.Hunger < 0.8);
    }

    [Fact]
    public void WithoutGoldFamineSetsInAndHungerCostsHappiness()
    {
        var game = Village();
        game.Week = 0;
        game.GrainWeeks = 0;
        game.HarvestQuality = 0.4;
        game.Money = 0;
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        game.Touch();
        double content = game.Indicators.Happiness;
        Harvest.RunWeek(game, new WeekTally());
        Assert.True(game.Hunger > 0.3);
        Assert.Contains(events, e => e.Kind == EventKind.Famine && e.Bad);
        game.Touch();
        Assert.True(game.Indicators.Happiness < content - 5);
    }

    [Fact]
    public void AMarketBringsInMoreGrainAtALowerPrice()
    {
        var plain = Village(weeks: 12);
        var market = Village("Market Cross", weeks: 12);
        Assert.True(Harvest.TradeReach(market) > Harvest.TradeReach(plain));
        Assert.True(Harvest.Price(market) < Harvest.Price(plain));
    }

    [Fact]
    public void AGranaryRaisesHowMuchGrainTheTownCanKeep()
    {
        var plain = Village(weeks: 12);
        var barn = Village("Granary", weeks: 12);
        Assert.True(Harvest.StoreCapacity(barn) > Harvest.StoreCapacity(plain));
        Assert.True(Harvest.StoreCapacity(barn) <= barn.Config.MaxStoreWeeks);
    }

    [Fact]
    public void HarvestsVaryButTheFirstYearIsOrdinary()
    {
        var game = Village();
        var qualities = Enumerable.Range(0, 200).Select(_ => Harvest.RollQuality(game)).ToList();
        Assert.All(qualities, q => Assert.InRange(q, 0.3, 1.5));
        Assert.True(qualities.Min() < 0.8);
        Assert.True(qualities.Max() > 1.2);
        Assert.InRange(qualities.Average(), 0.9, 1.1);

        var fresh = Village(weeks: 12);
        Assert.Equal(1, fresh.HarvestQuality);
    }

    [Fact]
    public void PlagueKillsAndPhysicBlunts()
    {
        // A big seeded city, so the deaths are a signal rather than noise: same people, same weather, three ways.
        var seeded = CityGame.New(new GameConfig { Scenario = CityScenario.Lubeck, MapWidth = 640, MapHeight = 384, Seed = 5 });
        Assert.True(seeded.Indicators.CoverageOf(ServiceKind.Health) > 20);

        static int Deaths(CityGame g, int outbreak, double physic)
        {
            g.SetFunding(ServiceKind.Health, physic);
            g.Touch();
            g.OutbreakWeeksLeft = outbreak;
            g.GrainWeeks = 12;
            int dead = 0;
            for (int w = 0; w < 3; w++)
            {
                g.AdvanceWeek();
                dead += g.LastReport!.Deaths;
            }

            return dead;
        }

        int ordinary = Deaths(Copy(seeded), 0, 1);
        int plague = Deaths(Copy(seeded), 14, 1);
        int plagueUnpaid = Deaths(Copy(seeded), 14, 0);
        Assert.True(plague > ordinary * 1.2, $"plague {plague} vs ordinary {ordinary}");
        Assert.True(plagueUnpaid > plague * 1.2, $"unfunded physic {plagueUnpaid} vs funded {plague} vs ordinary {ordinary}");
    }

    [Fact]
    public void ThePlagueLastsWeeksAndEnds()
    {
        var game = Village();
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        game.OutbreakWeeksLeft = 6;
        game.GrainWeeks = 12;
        for (int w = 0; w < 7; w++)
        {
            game.AdvanceWeek();
        }

        Assert.Equal(0, game.OutbreakWeeksLeft);
        Assert.Contains(events, e => e.Kind == EventKind.Outbreak && !e.Bad);
    }

    [Fact]
    public void RaidsStrikeAnUnguardedTownAndTheGarrisonTurnsThemAway()
    {
        var config = new GameConfig { StartingMoney = 2_000_000, DefaultTaxRate = 0.09, RaidChancePerWeek = 1, RaidMinPopulation = 0 };
        var bare = Village(config: config, weeks: 12);
        var guarded = Village("Motte and Bailey", config, weeks: 12);

        static (int Sacked, int Repelled) Run(CityGame g)
        {
            int sacked = 0, repelled = 0;
            g.EventOccurred += e =>
            {
                if (e.Kind != EventKind.Raid) return;
                if (e.Bad) sacked++; else repelled++;
            };
            g.GrainWeeks = 12;
            for (int w = 0; w < 12; w++)
            {
                g.GrainWeeks = 12;
                g.AdvanceWeek();
            }

            return (sacked, repelled);
        }

        var open = Run(bare);
        var held = Run(guarded);
        Assert.True(open.Sacked > 0);
        Assert.Equal(0, open.Repelled);
        Assert.True(held.Repelled > 0);
        Assert.True(held.Repelled > open.Repelled);
    }

    [Fact]
    public void ASheriffAlsoTurnsRaidersBack()
    {
        var config = new GameConfig { StartingMoney = 2_000_000, DefaultTaxRate = 0.09, RaidChancePerWeek = 1, RaidMinPopulation = 0 };
        var game = Village("Sheriff's Hall", config, weeks: 12);
        int repelled = 0;
        game.EventOccurred += e => repelled += e.Kind == EventKind.Raid && !e.Bad ? 1 : 0;
        for (int w = 0; w < 12; w++)
        {
            game.GrainWeeks = 12;
            game.AdvanceWeek();
        }

        Assert.True(repelled > 0);
    }

    [Fact]
    public void NoRaidsComeToATownBelowTheMinimumSize()
    {
        var config = new GameConfig { StartingMoney = 2_000_000, DefaultTaxRate = 0.09, RaidChancePerWeek = 1, RaidMinPopulation = 100_000 };
        var game = Village(config: config, weeks: 12);
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        for (int w = 0; w < 10; w++)
        {
            game.GrainWeeks = 12;
            game.AdvanceWeek();
        }

        Assert.DoesNotContain(events, e => e.Kind == EventKind.Raid);
    }

    [Fact]
    public void PilgrimsKeepTheFeastAndSpendGoldAtAShrine()
    {
        var game = Village("Chapel", weeks: 12);
        game.Week = 12;
        game.Hunger = 0;
        long before = game.Money;
        var events = new List<CityEvent>();
        game.EventOccurred += events.Add;
        Feasts.RunWeek(game);
        Assert.True(game.Money > before);
        Assert.Contains(events, e => e.Kind == EventKind.Feast);
    }

    [Fact]
    public void NoPilgrimsComeInAFamineAndAMarketMakesTheFeastRicher()
    {
        var plain = Village("Chapel", weeks: 12);
        plain.Week = 12;
        plain.Hunger = 1;
        long before = plain.Money;
        Feasts.RunWeek(plain);
        Assert.True(plain.Money - before < 10);

        plain.Week = 13;
        long ordinary = plain.Money;
        plain.Hunger = 0;
        plain.Week = 12;
        Feasts.RunWeek(plain);
        long plainGold = plain.Money - ordinary;

        var market = Village("Chapel", weeks: 12, second: "Market Cross");
        market.Week = 12;
        market.Hunger = 0;
        long start = market.Money;
        Feasts.RunWeek(market);
        Assert.True(market.Money - start > plainGold);
    }

    [Fact]
    public void NoFeastOnAnOrdinaryWeek()
    {
        var game = Village("Chapel", weeks: 12);
        game.Week = 5;
        long before = game.Money;
        Feasts.RunWeek(game);
        Assert.Equal(before, game.Money);
    }

    [Fact]
    public void GrainAndHarvestSurviveASaveAndOldSavesLoadWithDefaults()
    {
        var game = Village(weeks: 12);
        game.GrainWeeks = 3.5;
        game.HarvestQuality = 0.7;
        game.Hunger = 0.2;
        var loaded = Copy(game);
        Assert.Equal(3.5, loaded.GrainWeeks, 6);
        Assert.Equal(0.7, loaded.HarvestQuality, 6);
        Assert.Equal(0.2, loaded.Hunger, 6);

        var node = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        foreach (var key in new[] { "GrainWeeks", "HarvestQuality", "Hunger" })
        {
            foreach (var prop in node.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                node.Remove(prop);
            }
        }

        var old = SaveGameStore.Deserialize(node.ToJsonString());
        Assert.Equal(0, old.Hunger);
        Assert.Equal(1, old.HarvestQuality);
        Assert.True(old.GrainWeeks > 0);
    }

    [Fact]
    public void MedievalLivesAreShortAndChildrenWork()
    {
        var config = new GameConfig();
        Assert.True(config.ChildLabour > 0.05);
        Assert.True(config.SeniorParticipation >= 0.2);
        Assert.True(config.PlagueDeathPerWeek > 0.005);
        Assert.True(config.FireIgnitionPerBuildingWeek > 0.00006);

        var game = Village(weeks: 20);
        game.Touch();
        int working = game.Indicators.Workers;
        int adultsOnly = (int)Math.Round(game.Stats.Adults * config.AdultParticipation + game.Stats.Seniors * config.SeniorParticipation);
        Assert.True(working > adultsOnly);
    }
}
