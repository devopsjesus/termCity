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
}

public sealed record GrowthDiagnostic(GrowthStatus Status, string Message, int EligibleVacancies, bool Paused);

public static class GrowthDiagnostics
{
    public static GrowthDiagnostic ForZone(CityGame game, ZoneType zone)
    {
        int eligible = game.Map.ZoneCells(zone).Count(i => game.Map.BuildingLayer[i] == 0 && game.Network.IsServed(i));
        var count = game.Stats.For(zone);
        if (count.Filled >= count.Zoned)
        {
            return Result(GrowthStatus.NoVacancies, "Zone more land.", eligible, game);
        }

        if (eligible == 0)
        {
            return NoRoad(game, eligible);
        }

        return Capacity(game, zone, eligible);
    }

    public static GrowthDiagnostic ForCell(CityGame game, int x, int y)
    {
        var zone = game.Map.ZoneAt(x, y);
        if (zone == ZoneType.None || game.Map.BuildingAt(x, y) is not null)
        {
            return Result(GrowthStatus.NoVacancies, "No vacant zone here.", 0, game);
        }

        return game.Network.IsServed(game.Map, x, y) ? Capacity(game, zone, 1) : NoRoad(game, 0);
    }

    private static GrowthDiagnostic NoRoad(CityGame game, int eligible) =>
        Result(GrowthStatus.NoRoadAccess, $"Connect a road to the map edge within {game.Config.RoadServiceReach} cells.", eligible, game);

    private static GrowthDiagnostic Capacity(CityGame game, ZoneType zone, int eligible)
    {
        if (game.Map.Content.Buildings.ForZone(zone) is null)
        {
            return Result(GrowthStatus.MissingBuilding, "No growth building registered for this zone.", eligible, game);
        }

        int homes = game.Stats.Residential.Occupied;
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

        return Result(GrowthStatus.Ready, "Ready for growth; arrivals are spread across the week.", eligible, game);
    }

    private static GrowthDiagnostic Result(GrowthStatus status, string message, int eligible, CityGame game) =>
        new(status, message, eligible, game.Paused);
}
