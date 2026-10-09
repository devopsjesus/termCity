using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public readonly record struct SidebarCell(string Text, Rgb Color);

public static class SidebarReport
{
    public static readonly Rgb LabelColor = Rgb.Hex(0x9fb4c7);
    public static readonly Rgb PeopleColor = Rgb.Hex(0x70d7ff);
    public static readonly Rgb GoodColor = Rgb.Hex(0x88ee99);
    public static readonly Rgb WarningColor = Rgb.Hex(0xffe066);
    public static readonly Rgb BadColor = Rgb.Hex(0xff7777);

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
            Add("Grain", $"{game.GrainWeeks:0.#} wk" + (game.Hunger >= 0.1 ? " FAMINE" : ""),
                game.Hunger >= 0.1 ? BadColor : game.GrainWeeks < 2 ? WarningColor : GoodColor);
            Add("Mood", $"{indicators.Mood} ({indicators.Happiness:0})",
                indicators.Happiness < 50 ? BadColor : indicators.Happiness < 65 ? WarningColor : GoodColor);
            Add("Fuel", $"{indicators.PowerSupplied:P0}", SupplyColor(indicators.PowerSupplied));
            Add("Water", $"{indicators.WaterSupplied:P0}", SupplyColor(indicators.WaterSupplied));
            Add("Services", game.Insolvent ? "Half strength" : "Full strength", game.Insolvent ? BadColor : GoodColor);
            Add("Complaint", indicators.Complaints.Count > 0 ? indicators.Complaints[0].Reason : "None",
                indicators.Complaints.Count > 0 ? WarningColor : GoodColor);
        }
        return rows;

        void Add(string label, string value, Rgb color) =>
            rows.Add([new(label, LabelColor), new(value, color)]);
    }

    private static Rgb SupplyColor(double ratio) => ratio >= 1 ? GoodColor : ratio >= 0.8 ? WarningColor : BadColor;
}
