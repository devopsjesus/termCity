using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public readonly record struct CellVisual(string Glyph, Rgb Foreground, Rgb Background);

/// <summary>Resolves what a map cell looks like by composing its layers: terrain, feature, zone, building, road.</summary>
public static class CellRenderer
{
    private static readonly Rgb BuildingBackground = Rgb.Hex(0x2b2b30);
    public static readonly Rgb DisconnectedRoad = Rgb.Hex(0xff7777);

    /// <param name="vectorRoads">
    /// When the caller draws roads as curves (see <see cref="RoadVectorLayer"/>), their cells are left as bare ground
    /// so the glyph does not show through.
    /// </param>
    public static CellVisual Render(CityGame game, int x, int y, MapOverlay overlay = MapOverlay.Off,
        bool vectorRoads = false, bool buildingArt = true, bool buildingSprites = false, bool showAreas = true)
    {
        var map = game.Map;
        var terrain = map.TerrainAt(x, y);
        string glyph = terrain.GlyphAt(x, y);
        Rgb fg = terrain.Foreground;
        Rgb bg = terrain.Background;

        var zone = map.ZoneAt(x, y);
        var building = map.BuildingAt(x, y);

        if (showAreas && zone != ZoneType.None)
        {
            var visual = RenderZone(game, x, y);
            bg = visual.Background;
            fg = visual.Foreground;
            glyph = visual.Glyph;
        }
        else if (map.FeatureAt(x, y) is { } feature && TerrainSprite.NaturalFeatureVisible(game, x, y))
        {
            glyph = feature.GlyphAt(x, y);
            fg = feature.Foreground;
        }

        if (building is not null)
        {
            var footprint = map.BuildingFootprintAt(x, y);
            glyph = buildingSprites ? " " :
                buildingArt ? building.FootprintGlyphAt(footprint, x, y) : building.GlyphAt(x, y);
            fg = building.Foreground;
            if (zone == ZoneType.None && !buildingSprites)
            {
                bg = BuildingBackground;
            }

        }

        if (building is null && map.PathWearAt(x, y) > 0 && !map.HasRoad(x, y) && map.FootpathsAllowedAt(x, y) &&
            !map.IsHouseYardCell(map.Index(x, y)) && !TravelRoutes.IsFootpathBridgeCell(map, map.Index(x, y)) &&
            !TravelRoutes.IsFootpathHillCell(map, map.Index(x, y)))
        {
            glyph = FootpathLayout.Glyph(FootpathLayout.Connections(map, x, y));
            fg = Rgb.Hex(FootpathArtwork.ColorForWear(map.PathWearAt(x, y)));
        }

        if (vectorRoads && map.HasRoad(x, y))
        {
            glyph = " ";
            bg = terrain.Background;
        }
        else if (map.RoadTypeAt(x, y) is { } road)
        {
            // A road over water is a bridge: same glyphs, but drawn on the water.
            glyph = road.GlyphFor(RoadMask(map, x, y));
            fg = road.Foreground;
            bg = terrain.Buildable ? road.Background : terrain.Background;
        }

        if (overlay != MapOverlay.Off && MapOverlays.Tint(game, overlay, x, y, bg) is { } tint)
        {
            bg = tint;
        }

        return new CellVisual(glyph, fg, bg);
    }

    public static CellVisual RenderZone(CityGame game, int x, int y, MapOverlay overlay = MapOverlay.Off)
    {
        var info = Zones.Get(game.Map.ZoneAt(x, y));
        var foreground = game.Network.IsServed(game.Map, x, y) ? info.Foreground : info.Foreground.Scale(0.5);
        var background = overlay == MapOverlay.Off ? info.Background :
            MapOverlays.Tint(game, overlay, x, y, info.Background) ?? info.Background;
        return new(info.EmptyGlyph, foreground, background);
    }

    /// <summary>Which of the four neighbours are roads: north = 1, east = 2, south = 4, west = 8.</summary>
    public static int RoadMask(GameMap map, int x, int y) =>
        (map.HasRoad(x, y - 1) ? 1 : 0) | (map.HasRoad(x + 1, y) ? 2 : 0) |
        (map.HasRoad(x, y + 1) ? 4 : 0) | (map.HasRoad(x - 1, y) ? 8 : 0);
}
