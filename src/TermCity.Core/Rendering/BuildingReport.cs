using TermCity.Core.Buildings;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class BuildingReport
{
    public const string Title = "Building Status";

    public static ReportPage Build(CityGame game, Pos position)
    {
        var map = game.Map;
        if (!map.InBounds(position) || map.BuildingAt(position.X, position.Y) is not { } building)
            throw new ArgumentException("No building at the selected position.", nameof(position));
        var footprint = map.BuildingFootprintAt(position.X, position.Y);
        var status = CellInspector.Status(game, position)!;
        var rows = new List<ReportCell[]>();
        ReportCell[] Row(string label, string value, Rgb? color = null) =>
            [new(label, Rgb.Hex(0x70d7ff)), new(value, color ?? Rgb.Hex(0x9fb4c7))];
        rows.Add(Row("Status", status.Text, status.Color));
        rows.Add(Row("Description", building.Description));
        rows.Add(Row("Location", $"{footprint.X}, {footprint.Y}"));
        rows.Add(Row("Footprint", $"{footprint.Width} x {footprint.Height} cells"));
        bool connected = map.BuildingIsServed(map.Index(position.X, position.Y), game.Network.IsServed);
        rows.Add(Row("Road access", connected ? "Connected" : "Disconnected"));
        rows.Add(Row("Upkeep", $"{Fmt.Money(game.Config.FullRules ?
            (int)Math.Round(building.WeeklyUpkeep * game.Budget.Funding(building.Service)) : 0)}/week"));
        if (building.Capacity > 0) rows.Add(Row("Capacity", ChoiceHelpContent.CapacityText(building)));
        if (building.Radius > 0) rows.Add(Row("Coverage", $"Radius {building.Radius} cells; fades to {building.Radius * AreaFalloff.Extent:0.##} cells"));
        if (building.Service == ServiceKind.Granary && game.Config.FullRules)
        {
            rows.Add(Row("Active storage", connected ? $"{building.Capacity:N0} grain measures" : "0 grain measures"));
            rows.Add(Row("City grain reserve", $"{game.GrainWeeks:0.0} weeks"));
            rows.Add(Row("City storage limit", $"{Harvest.StoreCapacity(game):0.0} weeks"));
            rows.Add(Row("Weekly food need", $"{Harvest.NeedPerWeek(game):N0} measures"));
            rows.Add(Row("Food status", game.Hunger >= 0.1 ? "FAMINE" : game.Hunger > 0 ? "Food shortage" : "Fed",
                Rgb.Hex(game.Hunger > 0 ? 0xff7777 : 0x88ee99)));
            rows.Add(Row("Hunger", $"{game.Hunger:P0} unmet need"));
            rows.Add(Row("Buying grain", game.BuyingGrain ? "Yes" : "No"));
            rows.Add(Row("Import capacity", $"{0.4 + 0.6 * Harvest.TradeReach(game):P0} of weekly need"));
            rows.Add(Row("Season", Seasons.Name(game.Season)));
            rows.Add(Row("Harvest quality", $"{game.HarvestQuality:0.00}x"));
            rows.Add(Row("Note", "Granaries store surplus; they do not produce grain. Market Cross coverage improves food imports."));
        }
        return new(building.Name, [new(building.Name.ToUpperInvariant(), rows)]);
    }
}
