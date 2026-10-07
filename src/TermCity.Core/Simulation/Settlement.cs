using TermCity.Core.Buildings;

namespace TermCity.Core.Simulation;

/// <summary>How the realm sees the place, from a clutch of huts to a chartered city.</summary>
public enum TownRank
{
    Hamlet,
    Village,
    MarketTown,
    Borough,
    City,
}

/// <summary>
/// Standing and tribute. A settlement earns a rank (and with it a charter's freedoms) by growing, by holding a market, and
/// by having a lord's seat and a church worthy of the name. Rank trims the crown's yearly demand and lifts what traders
/// pay in dues; a failure to pay what is owed is remembered and added to next year's bill.
/// </summary>
public static class Settlement
{
    /// <summary>The week of the year the crown's reeve comes for the tribute (Michaelmas, the end of the harvest).</summary>
    public static int TributeWeek(int weeksPerYear) => weeksPerYear * 3 / 4;

    public static string Name(TownRank rank) => rank switch
    {
        TownRank.Hamlet => "Hamlet",
        TownRank.Village => "Village",
        TownRank.MarketTown => "Market Town",
        TownRank.Borough => "Borough",
        _ => "City",
    };

    /// <summary>What the charter of this rank allows, for the report.</summary>
    public static string Privilege(TownRank rank) => rank switch
    {
        TownRank.Hamlet => "No charter yet: the lord's reeve takes what he likes",
        TownRank.Village => "A village moot and its own reeve",
        TownRank.MarketTown => "A weekly market and tolls; a little relief from the crown's tribute",
        TownRank.Borough => "A borough charter: guild dues and burgesses' votes",
        _ => "A city charter: free from most of the crown's exactions",
    };

    /// <summary>The rank the city holds right now, from its size, its market, its seat and its faith.</summary>
    public static TownRank RankOf(CityGame game)
    {
        int people = game.Stats.Population;
        var ind = game.Indicators;
        double trade = ind.CoverageOf(ServiceKind.Trade);
        double faith = ind.CoverageOf(ServiceKind.Faith);
        int seat = game.Services.SeatRank;
        if (people >= 25_000 && trade >= 25 && seat >= 2 && faith >= 20)
        {
            return TownRank.City;
        }

        if (people >= 5_000 && trade >= 25 && seat >= 1)
        {
            return TownRank.Borough;
        }

        if (people >= 800 && trade >= 15)
        {
            return TownRank.MarketTown;
        }

        return people >= 120 ? TownRank.Village : TownRank.Hamlet;
    }

    /// <summary>Extra dues on the trade and workshops of a chartered town: 3 percent for each rank.</summary>
    public static double CharterDues(TownRank rank) => 1 + 0.03 * (int)rank;

    /// <summary>
    /// The crown's tribute for the year: a couple of gold a soul, trimmed by the strength of the lord's seat and the town's
    /// charter, plus any arrears. A hamlet is beneath the crown's notice.
    /// </summary>
    public static int TributeDue(CityGame game)
    {
        if (!game.Config.FullRules || game.Stats.Population < game.Config.TributeMinPopulation)
        {
            return 0;
        }

        double relief = (1 - 0.12 * game.Services.SeatRank) * (1 - 0.08 * (int)RankOf(game));
        double due = game.Stats.Population * game.Config.TributePerSoul * Math.Max(0.3, relief);
        return (int)Math.Round(due) + game.TributeArrears;
    }

    internal static void RunWeek(CityGame game)
    {
        var rank = RankOf(game);
        if ((int)rank > game.HighestRank)
        {
            if (game.HighestRank >= 0)
            {
                game.Report(new CityEvent(game.Week, EventKind.Milestone,
                    $"The town has been granted standing as a {Name(rank)}. {Privilege(rank)}.", [], Bad: false));
            }

            game.HighestRank = (int)rank;
        }

        if (game.WeekOfYear != TributeWeek(game.Config.WeeksPerYear))
        {
            return;
        }

        int due = TributeDue(game);
        if (due <= 0)
        {
            return;
        }

        if (game.Money >= due)
        {
            game.Money -= due;
            game.TributeArrears = 0;
            game.Report(new CityEvent(game.Week, EventKind.Finance,
                $"The crown's reeve took the Michaelmas tribute: {Fmt.Money(due)}.", [], Bad: false));
            return;
        }

        int paid = Math.Max(0, game.Money);
        game.Money -= paid;
        game.TributeArrears = due - paid;
        game.Report(new CityEvent(game.Week, EventKind.Finance,
            $"The treasury could not meet the tribute: {Fmt.Money(paid)} was paid and {Fmt.Money(game.TributeArrears)} remains owing, to be added to next year's demand.", []));
    }
}
