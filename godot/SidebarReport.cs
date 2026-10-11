using TermCity.Core.Simulation;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public readonly record struct SidebarCell(string Text, Rgb Color);

public static class SidebarReport
{
    public static double CoverageRefreshDays(GameConfig config, GameSpeed speed) =>
        3 * config.FastSecondsPerWeek / config.SecondsPerWeek(speed) * config.DaysPerWeek;

    public static SidebarCell ServiceStatus(CityGame game, ServiceKind kind)
    {
        if (!game.Config.FullRules)
            return new("Service coverage is not simulated in classic rules.", NoneColor);
        if (kind is ServiceKind.Power or ServiceKind.Water)
        {
            var supply = kind == ServiceKind.Power ? game.Services.Power : game.Services.Water;
            double level = SupplyLevel(supply);
            return new($"{CityReport.ServiceName(kind)} supply",
                SupplyLevelColor(level));
        }
        if (kind == ServiceKind.Granary)
            return new("Grain storage and reserves",
                game.Hunger >= 0.1 ? BadColor : game.GrainWeeks < 2 ? WarningColor : GoodColor);
        double coverage = game.Indicators.CoverageOf(kind);
        return new($"{CityReport.ServiceName(kind)} coverage",
            coverage < 70 ? BadColor : coverage < 95 ? WarningColor : GoodColor);
    }

    public static string ServiceStatusDetails(CityGame game, ServiceKind kind)
    {
        if (!game.Config.FullRules) return ServiceStatus(game, kind).Text;
        if (kind is ServiceKind.Power or ServiceKind.Water)
        {
            var supply = kind == ServiceKind.Power ? game.Services.Power : game.Services.Water;
            return $"{CityReport.ServiceName(kind)}: {supply.Supply:N0} / {supply.Demand:N0} units ({SupplyLevel(supply):P0})";
        }
        return kind == ServiceKind.Granary
            ? $"Grain storage: {game.Services.GranaryCapacity:N0} measures | Reserve: {game.GrainWeeks:0.#} weeks"
            : $"{CityReport.ServiceName(kind)} coverage: {game.Indicators.CoverageOf(kind):0.#}% of residents";
    }

    public const double GrainReserveTargetWeeks = 2;
    public static double GrainLevel(CityGame game) => game.GrainWeeks / GrainReserveTargetWeeks;

    public static double SupplyLevel(ServiceSupply supply) =>
        supply.Demand <= 0 ? 1 : supply.Supply / (double)supply.Demand;

    public static Rgb SupplyLevelColor(double level) =>
        level < 0.95 ? BadColor : level < 1.05 ? WarningColor : GoodColor;

    public static int ReportTabFor(string metric) => metric switch
    {
        "Adults" or "Children" or "Elders" or "Hearths" or "Mood" => 1,
        "Fuel" or "Water" or "Services" or "Complaint" => 3,
        "Season" or "Grain" => 5,
        _ => throw new ArgumentException($"No report section for sidebar metric '{metric}'.", nameof(metric)),
    };

    public static readonly Rgb LabelColor = Rgb.Hex(0x9fb4c7);
    public static readonly Rgb PeopleColor = Rgb.Hex(0x70d7ff);
    public static readonly Rgb GoodColor = Rgb.Hex(0x88ee99);
    public static readonly Rgb WarningColor = Rgb.Hex(0xffe066);
    public static readonly Rgb BadColor = Rgb.Hex(0xff7777);
    public static readonly Rgb NoneColor = Rgb.Hex(0x888888);

    public static IReadOnlyList<SidebarCell[]> CityRows(CityGame game)
    {
        var stats = game.Stats;
        var rows = new List<SidebarCell[]>();
        Add("Adults", $"{stats.Adults:N0}", PeopleColor);
        Add("Children", $"{stats.Children:N0}", PeopleColor);
        Add("Elders", $"{stats.Seniors:N0}", PeopleColor);
        Add("Hearths", $"{stats.Households:N0}", PeopleColor);
        if (game.Config.FullRules)
        {
            var indicators = game.Indicators;
            Add("Season", Seasons.Name(game.Season), PeopleColor);
            Add("Grain", $"{game.GrainWeeks:0.#} weeks" + (game.Hunger >= 0.1 ? " FAMINE" : ""),
                game.Hunger >= 0.1 ? BadColor : game.GrainWeeks < 2 ? WarningColor : GoodColor);
            Add("Mood", indicators.Mood,
                indicators.Happiness < 50 ? BadColor : indicators.Happiness < 65 ? WarningColor : GoodColor);
            double fuel = SupplyLevel(game.Services.Power), water = SupplyLevel(game.Services.Water);
            Add("Fuel", $"{fuel:P0}", SupplyLevelColor(fuel));
            Add("Water", $"{water:P0}", SupplyLevelColor(water));
        }
        return rows;

        void Add(string label, string value, Rgb color) =>
            rows.Add([new(label, LabelColor), new(value, color)]);
    }

    private static Rgb SupplyColor(double ratio) => ratio >= 1 ? GoodColor : ratio >= 0.8 ? WarningColor : BadColor;
}
