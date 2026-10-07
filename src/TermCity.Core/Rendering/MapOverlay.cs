using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public enum MapOverlay
{
    Off,
    Power,
    Water,
    Fire,
    Police,
    Health,
    Education,
    Parks,
    Pollution,
    LandValue,
    Happiness,
}

/// <summary>Colours the map background by a city statistic so the player can see what each service reaches.</summary>
public static class MapOverlays
{
    private static readonly Rgb Good = Rgb.Hex(0x1f8f4a);
    private static readonly Rgb Bad = Rgb.Hex(0xa8322e);
    private static readonly Rgb Cold = Rgb.Hex(0x1a2433);
    private static readonly Rgb Smog = Rgb.Hex(0x9a5a1e);
    private const double Strength = 0.7;

    public static MapOverlay Next(MapOverlay overlay) => (MapOverlay)(((int)overlay + 1) % Enum.GetValues<MapOverlay>().Length);

    public static string Label(MapOverlay overlay) => overlay switch
    {
        MapOverlay.Off => "Overlay off",
        MapOverlay.Power => "Overlay: fuel",
        MapOverlay.Fire => "Overlay: fire watch",
        MapOverlay.Police => "Overlay: sheriff's reach",
        MapOverlay.Health => "Overlay: physic",
        MapOverlay.Education => "Overlay: learning",
        MapOverlay.Parks => "Overlay: commons",
        MapOverlay.Pollution => "Overlay: smoke",
        MapOverlay.LandValue => "Overlay: land value",
        MapOverlay.Happiness => "Overlay: contentment",
        _ => "Overlay: " + overlay.ToString().ToLowerInvariant(),
    };

    /// <summary>The background to draw instead of <paramref name="background"/>, or null when the overlay says nothing here.</summary>
    public static Rgb? Tint(CityGame game, MapOverlay overlay, int x, int y, Rgb background)
    {
        if (overlay == MapOverlay.Off || !game.Config.FullRules || !game.Map.TerrainAt(x, y).Buildable)
        {
            return null;
        }

        var map = game.Map;
        int i = map.Index(x, y);
        var services = game.Services;
        switch (overlay)
        {
            case MapOverlay.Power or MapOverlay.Water:
                if (map.BuildingLayer[i] == 0)
                {
                    return null;
                }

                bool on = overlay == MapOverlay.Power ? services.IsPowered(map, i) : services.IsWatered(map, i);
                return Rgb.Blend(background, on ? Good : Bad, Strength);
            case MapOverlay.Fire: return Ramp(background, services.Coverage(ServiceKind.Fire, i));
            case MapOverlay.Police: return Ramp(background, services.Coverage(ServiceKind.Police, i));
            case MapOverlay.Health: return Ramp(background, services.Coverage(ServiceKind.Health, i));
            case MapOverlay.Education: return Ramp(background, services.Coverage(ServiceKind.Education, i));
            case MapOverlay.Parks: return Ramp(background, services.Coverage(ServiceKind.Recreation, i));
            case MapOverlay.Pollution:
                return Rgb.Blend(background, Smog, Math.Clamp(services.Pollution(i) / 60.0, 0, 1) * Strength);
            case MapOverlay.LandValue: return Ramp(background, CityAnalysis.LandValue(game, i));
            case MapOverlay.Happiness:
                return map.ZoneAt(x, y) == ZoneType.Residential && map.BuildingLayer[i] != 0 && !map.HouseholdLayer[i].IsEmpty
                    ? Ramp(background, CityAnalysis.CellHappiness(game, i), low: Bad, high: Good)
                    : null;
            default: return null;
        }
    }

    private static Rgb Ramp(Rgb background, double value, Rgb? low = null, Rgb? high = null) =>
        Rgb.Blend(background, Rgb.Blend(low ?? Cold, high ?? Good, Math.Clamp(value / 100.0, 0, 1)), Strength);
}
