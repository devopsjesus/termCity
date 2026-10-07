using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public readonly record struct CellVisual(string Glyph, Rgb Foreground, Rgb Background);

/// <summary>Resolves what a map cell looks like by composing its layers: terrain, feature, zone, building, road.</summary>
public static class CellRenderer
{
    private static readonly Rgb BuildingBackground = Rgb.Hex(0x2b2b30);
    private static readonly Rgb DisconnectedRoad = Rgb.Hex(0xe8a33d);

    public static CellVisual Render(CityGame game, int x, int y, MapOverlay overlay = MapOverlay.Off)
    {
        var map = game.Map;
        var terrain = map.TerrainAt(x, y);
        string glyph = terrain.GlyphAt(x, y);
        Rgb fg = terrain.Foreground;
        Rgb bg = terrain.Background;

        var zone = map.ZoneAt(x, y);
        var building = map.BuildingAt(x, y);

        if (zone != ZoneType.None)
        {
            var info = Zones.Get(zone);
            bg = info.Background;
            fg = game.Network.IsServed(map, x, y) ? info.Foreground : info.Foreground.Scale(0.5);
            glyph = info.EmptyGlyph;
        }
        else if (map.FeatureAt(x, y) is { } feature)
        {
            glyph = feature.GlyphAt(x, y);
            fg = feature.Foreground;
        }

        if (building is not null)
        {
            glyph = building.GlyphAt(x, y);
            fg = building.Foreground;
            if (zone == ZoneType.None)
            {
                bg = BuildingBackground;
            }
        }

        if (map.RoadTypeAt(x, y) is { } road)
        {
            // A road over water is a bridge: same glyphs, but drawn on the water.
            glyph = road.GlyphFor(RoadMask(map, x, y));
            fg = game.Network.IsConnected(map, x, y) ? road.Foreground : DisconnectedRoad;
            bg = terrain.Buildable ? road.Background : terrain.Background;
        }

        if (overlay != MapOverlay.Off && MapOverlays.Tint(game, overlay, x, y, bg) is { } tint)
        {
            bg = tint;
        }

        return new CellVisual(glyph, fg, bg);
    }

    /// <summary>Which of the four neighbours are roads: north = 1, east = 2, south = 4, west = 8.</summary>
    public static int RoadMask(GameMap map, int x, int y) =>
        (map.HasRoad(x, y - 1) ? 1 : 0) | (map.HasRoad(x + 1, y) ? 2 : 0) |
        (map.HasRoad(x, y + 1) ? 4 : 0) | (map.HasRoad(x - 1, y) ? 8 : 0);
}