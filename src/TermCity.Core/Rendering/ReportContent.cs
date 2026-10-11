using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public sealed record ReportCell(string Text, Rgb Color);
public sealed record ReportGroup(string Title, IReadOnlyList<ReportCell[]> Rows);
public sealed record ReportPage(string Title, IReadOnlyList<ReportGroup> Groups)
{
    public string Text => string.Join("\n\n", Groups.Select(group => group.Title + "\n" +
        string.Join("\n", group.Rows.Select(row => $"{row[0].Text}: {row[1].Text}"))));
}

public static class ReportContent
{
    private static readonly Rgb Label = Rgb.Hex(0x9fb4c7), People = Rgb.Hex(0x70d7ff),
        Good = Rgb.Hex(0x88ee99), Bad = Rgb.Hex(0xff7777), None = Rgb.Hex(0x888888);

    public static IReadOnlyList<ReportPage> Build(CityGame game)
    {
        var stats = game.Stats;
        var size = CityProgression.SizeProgress(game);
        var overview = new List<ReportCell[]>
        {
            Row("City", game.CityName), Row("Population", $"{stats.Population:N0}"),
            Row("Current size", size.CurrentName),
            Row("Next size", size.Target is { } target ? $"{size.NextName}: {target:N0} people" : "Largest city size reached."),
            Row("Date", MedievalCalendar.DateLabel(game.Year, game.WeekOfYear, game.Config.WeeksPerYear).Trim()),
            Row("Season", Seasons.Name(game.Season)), Row("Seed", game.Config.Seed.ToString()),
            Row("Map dimensions", $"{game.Map.Width} x {game.Map.Height}"),
            Row("Scenario", game.Config.Scenario.ToString()), Row("Rules", game.Config.Rules.ToString()),
            Row("Road cells", $"{stats.RoadCells:N0}"),
            Row("Connected roads", $"{stats.ConnectedRoadCells:N0}"),
        };
        var last = new List<ReportCell[]>();
        if (game.LastReport is { } report)
        {
            last.AddRange([
                Row("Completed week", report.Week.ToString()),
                Row("Population", $"{report.Population:N0}"), Row("Births", $"{report.Births:N0}", Good),
                Row("Deaths", $"{report.Deaths:N0}", Bad), Row("Moved in", $"{report.MovedIn:N0}", Good),
                Row("Moved out", $"{report.MovedOut:N0}", Bad), Row("New homes", $"{report.NewHouseholds:N0}"),
                Row("New stalls", $"{report.NewCommercial:N0}"), Row("New workshops", $"{report.NewIndustrial:N0}"),
                Row("Income", Fmt.Money(report.Income), Good), Row("Expenses", Fmt.Money(report.Expenses), Bad),
                Row("Net", Signed(report.Income - report.Expenses), report.Income >= report.Expenses ? Good : Bad),
                Row("Events", report.Events.ToString()),
            ]);
        }
        else last.Add(Row("Completed week", "No completed week yet.", None));
        var population = new List<ReportCell[]>
        {
            Row("Population", $"{stats.Population:N0}"), Row("Adults", $"{stats.Adults:N0}"),
            Row("Children", $"{stats.Children:N0}"), Row("Elders", $"{stats.Seniors:N0}"),
            Row("Households", $"{stats.Households:N0}"), Row("Vacant homes", $"{stats.VacantHomes:N0}"),
        };
        if (game.Config.FullRules)
        {
            var ind = game.Indicators;
            population.AddRange([
                Row("Workers", $"{ind.Workers:N0}"), Row("Jobs", $"{ind.Jobs:N0}"),
                Row("Unemployment", $"{ind.Unemployment:P0}"), Row("Jobs filled", $"{ind.JobsFilled:P0}"),
                Row("Mood", ind.Mood), Row("Happiness", $"{ind.Happiness:0}/100"),
                Row("Attraction", $"{ind.Attraction:0.00}x"), Row("Business climate", $"{ind.BusinessClimate:0.00}x"),
            ]);
        }
        var finance = game.Finance;
        var economy = new List<ReportCell[]>
        {
            Row("Gold", Fmt.Money(game.Money), game.Money < 0 ? Bad : Good),
            Row("Income", Fmt.Money(finance.Income), Good),
            Row("Expenses", Fmt.Money(finance.Expenses), Bad),
            Row("Net", Signed(finance.Net), finance.Net >= 0 ? Good : Bad),
            Row("Services", Fmt.Money(finance.ServiceUpkeep)), Row("Road upkeep", Fmt.Money(finance.RoadUpkeep)),
            Row("Administration", Fmt.Money(finance.Administration)), Row("Loan interest", Fmt.Money(finance.Interest)),
            Row("Loan", Fmt.Money(game.Budget.Loan)), Row("Road funding", $"{game.Budget.Roads:P0}"),
        };
        foreach (var zone in Zones.Placeable)
            economy.Add(Row($"{Zones.Get(zone).Name} tax", $"{game.Taxes.Get(zone):P0}"));
        if (game.Config.FullRules)
            economy.AddRange([
                Row("Grain", $"{game.GrainWeeks:0.#} weeks"), Row("Harvest quality", $"{game.HarvestQuality:0.00}x"),
                Row("Hunger", $"{game.Hunger:P0}", game.Hunger > 0 ? Bad : Good),
                Row("Tribute arrears", Fmt.Money(game.TributeArrears), game.TributeArrears > 0 ? Bad : Good),
                Row("Insolvent", game.Insolvent ? "Yes" : "No", game.Insolvent ? Bad : Good),
            ]);
        var services = new List<ReportGroup>();
        if (game.Config.FullRules)
        {
            var ind = game.Indicators;
            services.Add(new("CITY CONDITIONS",
            [
                Row("Fuel supplied", $"{ind.PowerSupplied:P0}"), Row("Water supplied", $"{ind.WaterSupplied:P0}"),
                Row("Crime", $"{ind.Crime:0}/100"), Row("Pollution", $"{ind.Pollution:0}/100"),
                Row("Traffic", $"{ind.Congestion:0}/100"), Row("Education", $"{ind.Education:0}/100"),
                Row("Land value", $"{ind.LandValue:0}"), Row("Outbreak", $"{game.OutbreakWeeksLeft} weeks remaining"),
            ]));
            foreach (var kind in ServiceKinds.All)
                services.Add(new(CityReport.ServiceName(kind).ToUpperInvariant(),
                [
                    Row("Buildings", game.Services.BuildingCounts[(int)kind].ToString()),
                    Row("Active", game.Services.ActiveCounts[(int)kind].ToString()),
                    Row("Coverage", $"{ind.CoverageOf(kind):0}%"),
                    Row("Funding", $"{game.Budget.Funding(kind):P0}"),
                ]));
            services.Add(new("COMPLAINTS", ind.Complaints.Count == 0
                ? [Row("Status", "No complaints.", Good)]
                : ind.Complaints.Select(c => Row(c.Reason, $"-{c.Points:0.#} happiness", Bad)).ToArray()));
        }
        else services.Add(new("SERVICES", [Row("Rules", "Classic rules do not simulate services or health.", None)]));
        var growth = new List<ReportGroup>();
        foreach (var zone in Zones.Placeable)
        {
            var info = Zones.Get(zone);
            var count = stats.For(zone);
            var diagnostic = GrowthDiagnostics.ForZone(game, zone);
            growth.Add(new($"{info.Letter} {info.Name.ToUpperInvariant()}",
            [
                Row("Demand", $"{game.Demand.For(zone):P0}", info.Foreground),
                Row("Built", $"{count.Filled:N0} cells", info.Foreground),
                Row("Zoned", $"{count.Zoned:N0} lots", info.Foreground),
                Row("Road-served", $"{count.Served:N0} lots", info.Foreground),
                Row("Without road", $"{count.Zoned - count.Served:N0} lots", info.Foreground),
                Row("Awaiting removal", $"{count.AwaitingRemoval:N0} buildings", info.Foreground),
                Row("Eligible vacancies", $"{diagnostic.EligibleVacancies:N0}", info.Foreground),
                Row("Growth status", diagnostic.Message, info.Foreground),
            ]));
        }
        var events = game.Events.TakeLast(20).Reverse().Select(e =>
            Row($"Week {e.Week}: {e.Kind}", e.Message, e.Bad ? Bad : Good)).ToArray();
        var food = new List<ReportGroup>();
        if (game.Config.FullRules)
        {
            food.Add(new("FOOD AND GRAIN",
            [
                Row("Food status", game.Hunger >= 0.1 ? "FAMINE" : game.Hunger > 0 ? "Food shortage" :
                    game.GrainWeeks < 2 ? "Low grain reserves" : "Fed",
                    game.Hunger > 0 ? Bad : game.GrainWeeks < 2 ? Rgb.Hex(0xffe066) : Good),
                Row("Grain reserve", $"{game.GrainWeeks:0.0} weeks"),
                Row("Store capacity", $"{Harvest.StoreCapacity(game):0.0} weeks"),
                Row("Grain consumption", $"{Harvest.NeedPerWeek(game):N0} measures/week"),
                Row("Granary capacity", $"{game.Services.GranaryCapacity:N0} measures"),
                Row("Hunger", $"{game.Hunger:P0} unmet food need", game.Hunger > 0 ? Bad : Good),
                Row("Buying grain", game.BuyingGrain ? "Yes" : "No"),
                Row("Grain price", $"{Harvest.Price(game):0.##}g per measure"),
                Row("Harvest quality", $"{game.HarvestQuality:0.00}x ordinary harvest"),
                Row("Current yield", $"{Seasons.FieldYield(game.Season) * game.HarvestQuality:0.00} weeks of food per week"),
            ]));
        }
        else food.Add(new("FOOD AND GRAIN", [Row("Rules", "Classic rules do not simulate grain, hunger or famine.", None)]));
        food.Add(new("CURRENT SEASON",
        [
            Row("Season", Seasons.Name(game.Season)),
            Row("Calendar", MedievalCalendar.DateLabel(game.Year, game.WeekOfYear, game.Config.WeeksPerYear).Trim()),
            Row("Seasonal rules", game.Config.FullRules ? "Seasonal yields, travel and disaster risks apply." :
                "Calendar only; seasonal food and disaster effects are not simulated.", None),
        ]));
        if (game.Config.FullRules)
        {
            foreach (var season in Enum.GetValues<Season>())
                food.Add(new(Seasons.Name(season).ToUpperInvariant(),
                [
                    Row("Grain yield", $"{Seasons.FieldYield(season):0.00}x"),
                    Row("Settler travel", $"{Seasons.Travel(season):0.00}x"),
                    Row("Fire risk", $"{Seasons.FireFactor(season):0.00}x"),
                    Row("Plague risk", $"{Seasons.PlagueFactor(season):0.00}x"),
                ]));
            var foodEvents = game.Events.Where(e => e.Kind is EventKind.Famine or EventKind.Harvest)
                .TakeLast(10).Reverse().Select(e => Row($"Week {e.Week}", e.Message, e.Bad ? Bad : Good)).ToArray();
            food.Add(new("RECENT FOOD EVENTS", foodEvents.Length > 0 ? foodEvents :
                [Row("Status", "No harvest or famine events recorded.", None)]));
        }
        return [
            new("Overview", [new("CITY OVERVIEW", overview)]),
            new("Population", [new("CURRENT POPULATION", population), new("LAST COMPLETED WEEK", last)]),
            new("Economy", [new("PROJECTED WEEKLY BUDGET", economy), new("LAST COMPLETED WEEK", last)]),
            new("Services", services), new("Growth", growth),
            new("Food & Seasons", food),
            new("Events", [new("RECENT EVENTS", events.Length == 0 ? [Row("Status", "No events recorded.", None)] : events)]),
        ];
    }

    private static string Signed(int value) => (value > 0 ? "+" : "") + Fmt.Money(value);
    private static ReportCell[] Row(string label, string value, Rgb? color = null) =>
        [new(label, Label), new(value, color ?? People)];
}
