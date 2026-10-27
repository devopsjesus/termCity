using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public enum GrowthStatus
{
    Ready,
    NoVacancies,
    NoRoadAccess,
    NeedsHomes,
    CapacityReached,
    MissingBuilding,
    RoadOverlap,
    NeedsSpace,
    LowAttraction,
    LowBusinessClimate,
}

public sealed record GrowthDiagnostic(GrowthStatus Status, string Message, int EligibleVacancies, bool Paused);

public static class GrowthDiagnostics
{
    public static GrowthDiagnostic ForZone(CityGame game, ZoneType zone)
    {
        if (game.Settlement is not null)
        {
            int sites = game.VacantPlots(zone).Count;
            var forecast = Capacity(game, zone, sites);
            if (forecast.Status != GrowthStatus.Ready) return forecast;
            return sites > 0 ? forecast with { Message = "Settlers choose individual sites inside the walls; no zoning needed." } :
                Result(GrowthStatus.NeedsSpace, "Interior sites are crowded; low-value homes can stack upward. Keep paths and gate access clear.", 0, game);
        }
        int eligible = game.Map.ZoneCells(zone).Count(i => game.Map.BuildingLayer[i] == 0 &&
            game.Network.IsServed(i) && !game.Network.RoadOverlapsCell(i));
        if (eligible == 0 && !game.Map.ZoneCells(zone).Any(i => game.Map.BuildingLayer[i] == 0))
        {
            return Result(GrowthStatus.NoVacancies, "Zone more land.", eligible, game);
        }

        if (eligible == 0)
        {
            if (game.Map.ZoneCells(zone).Any(i => game.Map.BuildingLayer[i] == 0 && game.Network.RoadOverlapsCell(i)))
                return RoadOverlap(game);
            return NoRoad(game, eligible);
        }

        var capacity = Capacity(game, zone, eligible);
        if (capacity.Status != GrowthStatus.Ready) return capacity;
        var building = game.Map.Content.Buildings.ForZone(zone);
        var size = game.MinimumGrowthSize(zone);
        if (building is not null && game.VacantPlots(zone).Count == 0)
            return Result(GrowthStatus.NeedsSpace, $"Needs a clear {size.Width}x{size.Height} zoned plot.", eligible, game);
        return capacity;
    }

    public static GrowthDiagnostic ForCell(CityGame game, int x, int y)
    {
        var zone = game.Map.ZoneAt(x, y);
        if (zone == ZoneType.None || game.Map.BuildingAt(x, y) is not null)
        {
            return Result(GrowthStatus.NoVacancies, "No vacant zone here.", 0, game);
        }

        int index = game.Map.Index(x, y);
        if (game.Network.RoadOverlapsCell(index)) return RoadOverlap(game);
        if (!game.Network.IsServed(game.Map, x, y)) return NoRoad(game, 0);
        var capacity = Capacity(game, zone, 1);
        if (capacity.Status != GrowthStatus.Ready) return capacity;
        var type = game.Map.Content.Buildings.ForZone(zone);
        var size = game.MinimumGrowthSize(zone);
        if (type is not null && !game.VacantPlots(zone).Any(area => area.Contains(x, y)))
            return Result(GrowthStatus.NeedsSpace, $"Needs a clear {size.Width}x{size.Height} zoned plot.", 0, game);
        return capacity;
    }

    private static GrowthDiagnostic NoRoad(CityGame game, int eligible) =>
        Result(GrowthStatus.NoRoadAccess, "No walkable land: water crossings need a bridge.", eligible, game);

    private static GrowthDiagnostic RoadOverlap(CityGame game) =>
        Result(GrowthStatus.RoadOverlap, "An angled road crosses this land; buildings need whole, clear cells.", 0, game);

    private static GrowthDiagnostic Capacity(CityGame game, ZoneType zone, int eligible)
    {
        if (game.Map.Content.Buildings.ForZone(zone) is null)
        {
            return Result(GrowthStatus.MissingBuilding, "No growth building registered for this zone.", eligible, game);
        }

        int homes = Demand.SupportingHomes(game.Stats, game.Config);
        if (zone != ZoneType.Residential && homes < game.Config.MinResidentialCells)
        {
            return Result(GrowthStatus.NeedsHomes, $"Needs {game.Config.MinResidentialCells} occupied homes; you have {homes}.", eligible, game);
        }

        int supported = game.SupportedCells(zone);
        int filled = game.Stats.For(zone).Occupied;
        if (zone != ZoneType.Residential && filled >= supported)
        {
            int ratio = zone == ZoneType.Commercial ? game.Config.ResidentialPerCommercial : game.Config.ResidentialPerIndustrial;
            int next = Math.Max(game.Config.MinResidentialCells, filled * ratio + 1);
            return Result(GrowthStatus.CapacityReached, $"{filled}/{supported} supported. Next slot at {next} occupied homes.", eligible, game);
        }

        if (zone == ZoneType.Residential && game.Config.FullRules && game.Stats.Population > 0 &&
            game.Indicators.Attraction <= 0)
        {
            string supply = game.Services.BuildingCounts[(int)Buildings.ServiceKind.Power] == 0
                ? "Dispatch fuel: build a Woodlot near homes."
                : game.Services.BuildingCounts[(int)Buildings.ServiceKind.Water] == 0
                    ? "Dispatch water: build a Town Well near homes."
                    : "Check delivery times, jobs and service workloads.";
            return Result(GrowthStatus.LowAttraction,
                $"Settlers are waiting: contentment {game.Indicators.Happiness:0}/100 is too low. {supply}", eligible, game);
        }

        if (zone != ZoneType.Residential && game.Config.FullRules && game.Indicators.BusinessClimate <= 0)
            return Result(GrowthStatus.LowBusinessClimate,
                "Businesses are not opening: trade climate is too low. Check fuel and water deliveries, taxes and available workers.",
                eligible, game);
        double arrivals = game.ExpectedGrowthArrivals(zone);
        if (arrivals <= 0)
            return Result(zone == ZoneType.Residential ? GrowthStatus.LowAttraction : GrowthStatus.LowBusinessClimate,
                "No arrivals are expected under the current growth conditions.", eligible, game);
        if (arrivals < 1)
            return Result(GrowthStatus.Ready,
                $"Slow arrivals: about {arrivals:0.00} {(zone == ZoneType.Residential ? "families" : "businesses")} per week; some weeks have none.",
                eligible, game);
        return Result(GrowthStatus.Ready, "Ready for growth; walking arrivals are slower without roads.", eligible, game);
    }

    private static GrowthDiagnostic Result(GrowthStatus status, string message, int eligible, CityGame game) =>
        new(status, message, eligible, game.Paused);
}
