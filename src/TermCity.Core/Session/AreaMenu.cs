using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private static readonly IReadOnlyList<TableColumn> AreaMenuColumns = ["COMMAND", "DESCRIPTION"];
    private static readonly IReadOnlyList<TableColumn> ZoneMenuColumns = ["ZONE", "COST", "DESCRIPTION"];

    private static readonly IReadOnlyList<TableColumn> RoadMenuColumns =
        ["ROAD", "ACTION", TableColumn.Right("CELLS"), TableColumn.Right("BLOCKED"), TableColumn.Right("COST")];

    private static readonly IReadOnlyList<TableColumn> BuildingMenuColumns =
        ["BUILDING", TableColumn.Right("CELLS"), TableColumn.Right("COST"), "NEEDS"];

    public void ShowAreaMenu()
    {
        var area = ActiveArea;
        ShowPrompt("Area menu", $"{area.Width}x{area.Height} selected ({area.Area} cells)",
        [
            new("Zone", ShowZoneMenu, ["Mark homes, markets and workshops, or dezone"]),
            new("Roads", ShowRoadMenu, ["Fill the area or draw a straight line"]),
            new("Services", ShowBuildingMenu, ["Fuel, water, fire, law, health and more"]),
            new("Demolish", () => { ClosePrompt(); PreviewDemolish(); }, ["Clear the area, free of charge"]),
            new("Deselect", () => { ClosePrompt(); ClearSelection(); }, ["Drop the selection"]),
            new("City menu", ShowSessionMenu, ["Save, load, budget, guide"]),
            new("Back", ClosePrompt, ["Return to the city"]),
        ], columns: AreaMenuColumns);
    }

    private void ShowZoneMenu()
    {
        var choices = Zones.Placeable.Select(zone => new SessionChoice(
            Zones.Get(zone).Name, () => { ClosePrompt(); Zone(zone); }, ["free", ZoneBlurb(zone)])).ToList();
        choices.Add(new("Dezone", () => { ClosePrompt(); Dezone(); }, ["free", "Buildings leave over 2 to 3 weeks"]));
        choices.Add(new("Back", ShowAreaMenu, ["", "Return to the area menu"]));
        ShowPrompt("Zone selected area", "Zoning is free. Connect roads for growth.", choices, columns: ZoneMenuColumns);
    }

    private static string ZoneBlurb(ZoneType zone) => zone switch
    {
        ZoneType.Residential => "Homes: the source of population",
        ZoneType.Commercial => "Shops: jobs and market tolls",
        ZoneType.Industrial => "Workshops: jobs and guild dues",
        _ => string.Empty,
    };

    private void ShowRoadMenu()
    {
        var choices = new List<SessionChoice>();
        foreach (var road in Game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank))
        {
            var quote = Game.QuoteRoad(ActiveArea, road);
            choices.Add(new(road.Name, () => { ClosePrompt(); PreviewRoad(road); },
                ["Fill area", quote.Cells.ToString(), quote.Skipped.ToString(), Fmt.Money(quote.Cost)]));
            choices.Add(new(road.Name, () => { ClosePrompt(); BeginRoadLine(road); }, ["Draw line", "", "", ""]));
        }

        choices.Add(new("Back", ShowAreaMenu, ["Return to the area menu", "", "", ""]));
        ShowPrompt("Roads", "Choose a road type, then fill the area or draw a straight line.", choices, columns: RoadMenuColumns);
    }

    private void ShowBuildingMenu()
    {
        var choices = Game.Map.Content.Buildings.Where(b => b.PlayerPlaceable).Select(building =>
        {
            var quote = Game.QuoteBuilding(building, ActiveArea);
            string needs = building.MinPopulation > Game.Stats.Population ? $"{building.MinPopulation:N0} souls" : string.Empty;
            return new SessionChoice(building.Name, () => { ClosePrompt(); PreviewBuilding(building); },
                [quote.Cells.ToString(), Fmt.Money(quote.Cost), needs]);
        }).ToList();
        bool empty = choices.Count == 0;
        choices.Add(new("Back", ShowAreaMenu, ["", "", "Return to the area menu"]));
        ShowPrompt("Service buildings", empty ? "No service buildings are registered." : "Choose a building to preview.", choices,
            columns: BuildingMenuColumns);
    }
}
