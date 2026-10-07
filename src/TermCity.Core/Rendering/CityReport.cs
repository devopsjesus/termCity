using TermCity.Core.Buildings;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

/// <summary>Plain-text summaries of how the city is doing, shared by the sidebar, the budget menu and the reports.</summary>
public static class CityReport
{
    public static string ServiceName(ServiceKind kind) => kind switch
    {
        ServiceKind.None => "Roads",
        ServiceKind.Recreation => "Parks",
        _ => kind.ToString(),
    };

    /// <summary>The sidebar block: people, jobs, mood and the biggest complaint. Classic rules show only the basics.</summary>
    public static string Overview(CityGame game)
    {
        var stats = game.Stats;
        string text = $"Population {stats.Population:N0}\nAdults {stats.Adults}  Kids {stats.Children}\n" +
            $"Seniors {stats.Seniors}  Homes {stats.Households}\nTax income {Fmt.Money(stats.WeeklyIncome)}/wk";
        if (!game.Config.FullRules)
        {
            return text;
        }

        var ind = game.Indicators;
        var finance = game.Finance;
        text += $"\nNet {Fmt.Money(finance.Net)}/wk (costs {Fmt.Money(finance.Expenses)})" +
            $"\nMood {ind.Mood} ({ind.Happiness:0})" +
            $"\nJobs {ind.Jobs:N0}  Unemployed {ind.Unemployment:P0}" +
            $"\nPower {ind.PowerSupplied:P0}  Water {ind.WaterSupplied:P0}";
        if (game.Insolvent)
        {
            text += "\nBROKE: services run at half strength";
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
            $"{game.CityName}: mood {ind.Mood} ({ind.Happiness:0}/100), attraction {ind.Attraction:0.00}x, business climate {ind.BusinessClimate:0.00}",
            $"Jobs {ind.Jobs:N0} for {ind.Workers:N0} workers, unemployment {ind.Unemployment:P0}",
            $"Crime {ind.Crime:0}  Pollution {ind.Pollution:0}  Traffic {ind.Congestion:0}  Land value {ind.LandValue:0}",
            $"Power {ind.PowerSupplied:P0}  Water {ind.WaterSupplied:P0}",
            "Coverage: " + string.Join("  ", ServiceKinds.Area.Select(k => $"{ServiceName(k)} {ind.CoverageOf(k):0}%")),
            $"Weekly: tax {Fmt.Money(f.Income)}, services {Fmt.Money(f.ServiceUpkeep)}, roads {Fmt.Money(f.RoadUpkeep)}, interest {Fmt.Money(f.Interest)}, net {Fmt.Money(f.Net)}",
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
