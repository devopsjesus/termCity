using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.App;

/// <summary>Builds the right-click / Enter context menu for the currently selected area.</summary>
internal static class ContextMenuBuilder
{
    public static PopoverMenu Build(GameSession session)
    {
        var game = session.Game;
        var area = session.ActiveArea;
        string header = area.Area == 1 ? $"Cell {area}" : $"{area.Width}x{area.Height} selected ({area.Area} cells)";

        var areaItems = new List<View>();
        foreach (var zone in Zones.Placeable)
        {
            var info = Zones.Get(zone);
            var captured = zone;
            areaItems.Add(new MenuItem($"_{info.Name}", "Free", () => session.Zone(captured), Key.Empty));
        }
        areaItems.Add(new MenuItem("_Unzone / dezone", "Free; buildings leave in 2-3 weeks", () => session.Dezone(), Key.Empty));

        var roadItems = new List<View>();
        foreach (var road in game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank))
        {
            var captured = road;
            var quote = game.QuoteRoad(area, road);
            roadItems.Add(new MenuItem(road.Name, quote.Cells == 0 ? "no valid cells" : Fmt.Money(quote.Cost), () => session.PreviewRoad(captured), Key.Empty));
        }

        foreach (var road in game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank))
        {
            var captured = road;
            roadItems.Add(new MenuItem("Draw " + road.Name + " line", "Preview", () => session.BeginRoadLine(captured), Key.Empty));
        }

        var buildingItems = new List<View>();
        foreach (var building in game.Map.Content.Buildings.Where(b => b.PlayerPlaceable))
        {
            var captured = building;
            var quote = game.QuoteBuilding(building, area);
            buildingItems.Add(new MenuItem($"{building.Name}", Fmt.Money(quote.Cost), () => session.PreviewBuilding(captured), Key.Empty));
        }

        if (buildingItems.Count == 0)
        {
            buildingItems.Add(new MenuItem("(no service buildings)", string.Empty, () => { }, Key.Empty) { Enabled = false });
        }

        var items = new List<View>
        {
            new MenuItem(header, string.Empty, () => { }, Key.Empty) { Enabled = false },
            new Line(),
            new MenuItem("_Area", string.Empty, new Menu(areaItems)),
            new MenuItem("_Road", string.Empty, new Menu(roadItems)),
            new MenuItem("_Buildings", string.Empty, new Menu(buildingItems)),
            new Line(),
            new MenuItem("_Demolish", "Free", session.PreviewDemolish, Key.Empty),
            new Line(),
            new MenuItem("_Cancel selection", string.Empty, () => session.ClearSelection(), Key.Empty),
            new MenuItem("City menu (F10)", string.Empty, session.ShowSessionMenu, Key.Empty),
        };

        return new PopoverMenu(items);
    }
}
