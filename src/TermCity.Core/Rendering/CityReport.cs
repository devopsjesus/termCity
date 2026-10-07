using TermCity.Core.Buildings;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

/// <summary>Plain-text summaries of how the city is doing, shared by the sidebar, the budget menu and the reports.</summary>
public static class CityReport
{
    public static string ServiceName(ServiceKind kind) => kind switch
    {
        ServiceKind.None => "Roads",
        ServiceKind.Power => "Fuel",
        ServiceKind.Water => "Water",
        ServiceKind.Fire => "Fire watch",
        ServiceKind.Police => "Sheriff",
        ServiceKind.Health => "Physic",
        ServiceKind.Education => "Learning",
        ServiceKind.Recreation => "Commons",
        ServiceKind.Defence => "Garrison",
        ServiceKind.Faith => "Faith",
        ServiceKind.Trade => "Trade",
        ServiceKind.Granary => "Granary",
        _ => kind.ToString(),
    };

    /// <summary>The sidebar block: people, jobs, mood and the biggest complaint. Classic rules show only the basics.</summary>
    public static string Overview(CityGame game)
    {
        var stats = game.Stats;
        string text = $"Souls {stats.Population:N0}\nAdults {stats.Adults}  Children {stats.Children}\n" +
            $"Elders {stats.Seniors}  Hearths {stats.Households}\nTithes and rents {Fmt.Money(stats.WeeklyIncome)}/wk";
        if (!game.Config.FullRules)
        {
            return text;
        }

        var ind = game.Indicators;
        var finance = game.Finance;
        text += $"\nNet {Fmt.Money(finance.Net)}/wk (costs {Fmt.Money(finance.Expenses)})" +
            $"\nMood {ind.Mood} ({ind.Happiness:0})" +
            $"\nWork {ind.Jobs:N0}  Idle {ind.Unemployment:P0}" +
            $"\nFuel {ind.PowerSupplied:P0}  Water {ind.WaterSupplied:P0}";
        if (game.Insolvent)
        {
            text += "\nCOFFERS EMPTY: services run at half strength";
        }

        if (ind.Complaints.Count > 0)
        {
            text += "\nTop complaint: " + ind.Complaints[0].Reason;
        }

        return text;
    }

    /// <summary>Every complaint, coverage figure and finance line, for the city health report.</summary>
    public static string Health(CityGame game)
    {
        if (!game.Config.FullRules)
        {
            return "Classic rules: no services, jobs or budget to report.";
        }

        var ind = game.Indicators;
        var f = game.Finance;
        var lines = new List<string>
        {
            $"{game.CityName}: mood of the people: {ind.Mood} ({ind.Happiness:0}/100), attraction {ind.Attraction:0.00}x, trade climate {ind.BusinessClimate:0.00}",
            $"Work for {ind.Jobs:N0} of {ind.Workers:N0} workers, idle {ind.Unemployment:P0}",
            $"Lawlessness {ind.Crime:0}  Smoke {ind.Pollution:0}  Cart traffic {ind.Congestion:0}  Land value {ind.LandValue:0}",
            $"Fuel {ind.PowerSupplied:P0}  Water {ind.WaterSupplied:P0}",
            "Coverage: " + string.Join("  ", ServiceKinds.Area.Select(k => $"{ServiceName(k)} {ind.CoverageOf(k):0}%")),
            $"Weekly: tithes and rents {Fmt.Money(f.Income)}, services {Fmt.Money(f.ServiceUpkeep)}, roads {Fmt.Money(f.RoadUpkeep)}, stewards and household {Fmt.Money(f.Administration)}, usury {Fmt.Money(f.Interest)}, net {Fmt.Money(f.Net)}",
        };
        if (ind.Complaints.Count > 0)
        {
            lines.Add("Complaints: " + string.Join(", ", ind.Complaints.Select(c => $"{c.Reason} (-{c.Points:0})")));
        }

        foreach (var e in game.Events.TakeLast(5).Reverse())
        {
            lines.Add($"Wk {e.Week}: {e.Message}");
        }

        return string.Join("\n", lines);
    }
}
