using TermCity.Core.Buildings;

namespace TermCity.Core.Simulation;

public enum Season
{
    Winter,
    Spring,
    Summer,
    Autumn,
}

/// <summary>
/// The farming year. A medieval town lives by it: the fields give little in the hungry weeks of late winter and spring
/// and a great deal at harvest, travellers stay home when the roads are mud and snow, and summer dries the thatch.
/// The year starts in midwinter, so with 52 weeks winter is weeks 49-9, spring 10-22, summer 23-35 and autumn 36-48.
/// </summary>
public static class Seasons
{
    public static Season Of(int weekOfYear, int weeksPerYear)
    {
        double f = (Math.Clamp(weekOfYear, 1, Math.Max(1, weeksPerYear)) - 1) / (double)Math.Max(1, weeksPerYear);
        return f switch
        {
            < 0.17 => Season.Winter,
            < 0.42 => Season.Spring,
            < 0.67 => Season.Summer,
            < 0.92 => Season.Autumn,
            _ => Season.Winter,
        };
    }

    public static string Name(Season season) => season switch
    {
        Season.Winter => "Winter",
        Season.Spring => "Spring",
        Season.Summer => "Summer",
        _ => "Autumn",
    };

    /// <summary>
    /// What the fields give in a week of this season, in weeks of the town's grain, in an average year. The four seasons
    /// are equal in length, so they average to 1: enough, if the store carries the town through the thin months.
    /// </summary>
    public static double FieldYield(Season season) => season switch
    {
        Season.Winter => 0.6,
        Season.Spring => 0.7,
        Season.Summer => 1.2,
        _ => 1.5,
    };

    /// <summary>How readily settlers and traders travel: the roads are mud in spring and snow in winter.</summary>
    public static double Travel(Season season) => season switch
    {
        Season.Winter => 0.6,
        Season.Spring => 0.9,
        Season.Summer => 1.3,
        _ => 1.1,
    };

    /// <summary>Hearths in winter and dry thatch in summer make fires likelier.</summary>
    public static double FireFactor(Season season) => season switch
    {
        Season.Winter => 1.15,
        Season.Spring => 0.8,
        Season.Summer => 1.35,
        _ => 0.95,
    };

    /// <summary>Plague loves the heat and the crowded summer markets.</summary>
    public static double PlagueFactor(Season season) => season switch
    {
        Season.Winter => 0.6,
        Season.Spring => 0.9,
        Season.Summer => 1.5,
        _ => 1.0,
    };
}

/// <summary>
/// Grain, the harvest and hunger. The town keeps a store measured in weeks of what its people eat; the fields fill or
/// drain it by season, a granary raises the most it can hold, and when it runs dry the reeve buys grain from merchants
/// for gold (cheaper near a market) until the treasury is empty, after which the people starve.
/// </summary>
internal static class Harvest
{
    public static double NeedPerWeek(CityGame game) => game.Stats.Population * game.Config.GrainPerPersonWeek;

    /// <summary>The most weeks of grain the town can store: household bins plus whatever the working granaries hold.</summary>
    public static double StoreCapacity(CityGame game)
    {
        double need = Math.Max(1, NeedPerWeek(game));
        double extra = game.Services.GranaryCapacity / need;
        return Math.Min(game.Config.MaxStoreWeeks, game.Config.HouseholdStoreWeeks + extra);
    }

    /// <summary>Gold per measure of grain bought in a shortage: dear in a remote town, fairer beside a market.</summary>
    public static double Price(CityGame game) =>
        game.Config.GrainPrice * (1.7 - 0.7 * TradeReach(game)) * (1 + Math.Max(0, 1 - game.HarvestQuality));

    /// <summary>How much of a week's need merchants can bring in: 0.4 to a town with no market, all of it to a well-served one.</summary>
    public static double TradeReach(CityGame game) => Math.Clamp(game.Indicators.CoverageOf(ServiceKind.Trade) / 100, 0, 1);

    /// <summary>The year's weather: sometimes a bumper crop, sometimes a failure. The first year is always an ordinary one.</summary>
    internal static double RollQuality(CityGame game)
    {
        double sum = game.Rng.NextDouble() + game.Rng.NextDouble() + game.Rng.NextDouble() - 1.5;
        return Math.Clamp(1 + 0.5 * sum * game.Profile.HarvestVolatility, 0.3, 1.5);
    }

    public static void RunWeek(CityGame game, WeekTally tally)
    {
        int population = game.Stats.Population;
        if (population == 0)
        {
            game.Hunger = 0;
            return;
        }

        int weeksPerYear = game.Config.WeeksPerYear;
        int week = game.WeekOfYear;
        var season = Seasons.Of(week, weeksPerYear);
        if (week == 1 && game.Week >= weeksPerYear)
        {
            game.HarvestQuality = RollQuality(game);
        }

        if (season == Season.Autumn && Seasons.Of(week - 1, weeksPerYear) != Season.Autumn)
        {
            AnnounceHarvest(game);
        }

        double need = NeedPerWeek(game);
        double store = game.GrainWeeks + Seasons.FieldYield(season) * game.HarvestQuality - 1;
        double unmet = 0;
        if (store < 0)
        {
            unmet = -store;
            store = 0;
        }

        store = Math.Min(store, StoreCapacity(game));

        if (unmet > 0 && game.Money > 0)
        {
            double wanted = Math.Min(unmet, 0.4 + 0.6 * TradeReach(game));
            double cost = wanted * need * Price(game);
            double afford = Math.Min(1, game.Money / Math.Max(1, cost));
            double bought = wanted * afford;
            int paid = (int)Math.Ceiling(cost * afford);
            game.Money -= paid;
            unmet -= bought;
            if (!game.BuyingGrain)
            {
                game.Report(new CityEvent(game.Week, EventKind.Harvest,
                    $"The reeve is buying grain from merchants to feed the town: {Fmt.Money(paid)} this week.", [], Bad: unmet > 0.05));
            }

            game.BuyingGrain = true;
        }
        else
        {
            game.BuyingGrain = false;
        }

        double before = game.Hunger;
        game.Hunger = Math.Clamp(unmet, 0, 1);
        game.GrainWeeks = store;
        if (game.Hunger >= 0.1 && before < 0.1)
        {
            game.Report(new CityEvent(game.Week, EventKind.Famine,
                "Famine: the stores are empty and there is no gold for grain. The people are starving.", []));
        }
        else if (game.Hunger == 0 && before >= 0.1)
        {
            game.Report(new CityEvent(game.Week, EventKind.Famine, "The famine has eased; bread is back in the market.", [], Bad: false));
        }
    }

    private static void AnnounceHarvest(CityGame game)
    {
        double q = game.HarvestQuality;
        if (q >= 1.2)
        {
            game.Report(new CityEvent(game.Week, EventKind.Harvest, "A bumper harvest: the barns are full.", [], Bad: false));
        }
        else if (q <= 0.5)
        {
            game.Report(new CityEvent(game.Week, EventKind.Harvest,
                "The harvest has failed. Fill the granaries, keep the treasury full and pray for a mild winter.", []));
        }
        else if (q <= 0.8)
        {
            game.Report(new CityEvent(game.Week, EventKind.Harvest, "A poor harvest: grain will be short before spring.", []));
        }
    }
}

/// <summary>
/// Pilgrimage. Four times a year (the quarter days) pilgrims throng the shrines, churches and abbeys; they spend gold in
/// the market, more where a market cross or guildhall stands near the shrine, but stay home in a famine or a plague.
/// </summary>
internal static class Feasts
{
    private static readonly string[] Names = ["Lady Day", "Midsummer", "Michaelmas", "Christmas"];

    /// <summary>The feast kept this week, or null.</summary>
    public static string? FeastOf(int weekOfYear, int weeksPerYear)
    {
        int quarter = weeksPerYear / 4;
        if (quarter <= 0 || weekOfYear % quarter != 0 || weekOfYear / quarter is < 1 or > 4)
        {
            return null;
        }

        return Names[weekOfYear / quarter - 1];
    }

    public static void RunWeek(CityGame game)
    {
        string? feast = FeastOf(game.WeekOfYear, game.Config.WeeksPerYear);
        if (feast is null)
        {
            return;
        }

        var map = game.Map;
        var services = game.Services;
        double weather = (1 - Math.Clamp(game.Hunger, 0, 1)) * (game.OutbreakWeeksLeft > 0 ? 0.3 : 1);
        double pilgrims = 0, gold = 0;
        foreach (int i in map.ServiceCells.Order())
        {
            var type = map.Content.Buildings[map.BuildingLayer[i]];
            if (type.Pilgrims <= 0 || !game.Network.IsServed(i))
            {
                continue;
            }

            double here = type.Pilgrims * Math.Max(0.25, game.Budget.Funding(type.Service)) * weather;
            pilgrims += here;
            gold += here * game.Config.PilgrimGold * (1 + 1.5 * services.Coverage(ServiceKind.Trade, i) / 100.0);
        }

        int spent = (int)Math.Round(gold);
        if (spent <= 0)
        {
            return;
        }

        game.Money += spent;
        game.Report(new CityEvent(game.Week, EventKind.Feast,
            $"Pilgrims kept {feast} in the town: about {(int)Math.Round(pilgrims):N0} came and spent {Fmt.Money(spent)}.", [], Bad: false));
    }
}
