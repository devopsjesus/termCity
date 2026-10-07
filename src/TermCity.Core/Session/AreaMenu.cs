using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    public void ShowAreaMenu()
    {
        var area = ActiveArea;
        ShowPrompt("Area menu", $"{area.Width}x{area.Height} selected ({area.Area} cells)",
        [
            new("Zone / dezone", ShowZoneMenu),
            new("Roads / straight-line tools", ShowRoadMenu),
            new("Service buildings", ShowBuildingMenu),
            new("Demolish (free)", () => { ClosePrompt(); PreviewDemolish(); }),
            new("Cancel selection", () => { ClosePrompt(); ClearSelection(); }),
            new("City menu", ShowSessionMenu),
            new("Back to city", ClosePrompt),
        ]);
    }

    private void ShowZoneMenu()
    {
        var choices = Zones.Placeable.Select(zone => new SessionChoice(
            Zones.Get(zone).Name + " (free)", () => { ClosePrompt(); Zone(zone); })).ToList();
        choices.Add(new("Unzone / dezone (buildings leave in 2-3 weeks)", () => { ClosePrompt(); Dezone(); }));
        choices.Add(new("Back", ShowAreaMenu));
        ShowPrompt("Zone selected area", "Zoning is free. Connect roads for growth.", choices);
    }

    private void ShowRoadMenu()
    {
        var choices = new List<SessionChoice>();
        foreach (var road in Game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank))
        {
            var quote = Game.QuoteRoad(ActiveArea, road);
            choices.Add(new($"{road.Name}: {quote.Cells} valid, {quote.Skipped} blocked, {Fmt.Money(quote.Cost)}",
                () => { ClosePrompt(); PreviewRoad(road); }));
            choices.Add(new("Draw " + road.Name + " line", () => { ClosePrompt(); BeginRoadLine(road); }));
        }
        choices.Add(new("Back", ShowAreaMenu));
        ShowPrompt("Roads", "Choose a road type or draw a straight line.", choices);
    }

    private void ShowBuildingMenu()
    {
        var choices = Game.Map.Content.Buildings.Where(b => b.PlayerPlaceable).Select(building =>
        {
            var quote = Game.QuoteBuilding(building, ActiveArea);
            string locked = building.MinPopulation > Game.Stats.Population ? $" (needs {building.MinPopulation:N0} souls)" : string.Empty;
            return new SessionChoice($"{building.Name}: {quote.Cells} valid, {Fmt.Money(quote.Cost)}{locked}",
                () => { ClosePrompt(); PreviewBuilding(building); });
        }).ToList();
        bool empty = choices.Count == 0;
        choices.Add(new("Back", ShowAreaMenu));
        ShowPrompt("Service buildings", empty ? "No service buildings are registered." : "Choose a building to preview.", choices);
    }
}
