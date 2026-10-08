using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Effects;

/// <summary>
/// A cheap copy of the map's layers, kept by <see cref="EffectDirector"/> so it can tell what changed since the last
/// frame and recover what a removed cell used to look like. The simulation mutates the map before it notifies anyone
/// and action results carry no cell lists, so diffing is the only way to learn "which cells" without touching the
/// simulation. Copying is a handful of array copies; diffing is limited to the visible area by the caller.
/// </summary>
public sealed class MapSnapshot
{
    private byte[] _terrain = [];
    private byte[] _feature = [];
    private bool[] _road = [];
    private byte[] _roadType = [];
    private ZoneType[] _zone = [];
    private byte[] _building = [];
    private readonly Dictionary<int, CellRect> _footprints = [];
    private int _width, _height;

    public int Width => _width;

    public int Height => _height;

    public bool Matches(GameMap map) => map.Width == _width && map.Height == _height;

    public void Capture(GameMap map)
    {
        if (!Matches(map))
        {
            _width = map.Width;
            _height = map.Height;
            int n = _width * _height;
            _terrain = new byte[n];
            _feature = new byte[n];
            _road = new bool[n];
            _roadType = new byte[n];
            _zone = new ZoneType[n];
            _building = new byte[n];
        }

        Array.Copy(map.TerrainLayer, _terrain, _terrain.Length);
        Array.Copy(map.FeatureLayer, _feature, _feature.Length);
        Array.Copy(map.RoadLayer, _road, _road.Length);
        Array.Copy(map.RoadTypeLayer, _roadType, _roadType.Length);
        Array.Copy(map.ZoneLayer, _zone, _zone.Length);
        Array.Copy(map.BuildingLayer, _building, _building.Length);
        _footprints.Clear();
        foreach (var footprint in map.BuildingFootprints.Values)
            foreach (var p in footprint.Cells()) _footprints.Add(map.Index(p.X, p.Y), footprint);
    }

    /// <summary>Compares the snapshot with the live map for one cell.</summary>
    public CellChange Compare(GameMap map, int x, int y)
    {
        int i = y * _width + x;
        var change = CellChange.None;
        if (_building[i] != 0 && map.BuildingLayer[i] == 0 ||
            _road[i] && !map.RoadLayer[i] ||
            _zone[i] != ZoneType.None && map.ZoneLayer[i] == ZoneType.None ||
            _feature[i] != 0 && map.FeatureLayer[i] == 0)
        {
            change |= CellChange.Lost;
        }

        if (_building[i] == 0 && map.BuildingLayer[i] != 0)
        {
            change |= CellChange.BuildingGained;
        }
        else if (_building[i] != 0 && map.BuildingLayer[i] != 0 && _building[i] != map.BuildingLayer[i])
        {
            change |= CellChange.BuildingChanged;
        }

        if (!_road[i] && map.RoadLayer[i])
        {
            change |= CellChange.RoadGained;
        }

        if (_zone[i] != map.ZoneLayer[i] && map.ZoneLayer[i] != ZoneType.None)
        {
            change |= CellChange.Zoned;
        }

        if (_terrain[i] != map.TerrainLayer[i])
        {
            change |= CellChange.Terrain;
        }

        return change;
    }

    /// <summary>What the cell looked like when captured (roads, buildings, zones, features, terrain; no service tinting).</summary>
    public CellVisual Describe(GameMap map, int x, int y)
    {
        int i = y * _width + x;
        var content = map.Content;
        var terrain = content.Terrains[_terrain[i]];
        string glyph = terrain.GlyphAt(x, y);
        Rgb fg = terrain.Foreground;
        Rgb bg = terrain.Background;
        var zone = _zone[i];
        if (zone != ZoneType.None)
        {
            var info = Zones.Get(zone);
            glyph = info.EmptyGlyph;
            fg = info.Foreground;
            bg = info.Background;
        }
        else if (_feature[i] != 0)
        {
            var feature = content.Features[_feature[i]];
            glyph = feature.GlyphAt(x, y);
            fg = feature.Foreground;
        }

        if (_building[i] != 0)
        {
            var building = content.Buildings[_building[i]];
            glyph = building.FootprintGlyphAt(_footprints.GetValueOrDefault(i, new CellRect(x, y, 1, 1)), x, y);
            fg = building.Foreground;
            if (zone == ZoneType.None)
            {
                bg = Rgb.Hex(0x2b2b30);
            }
        }

        if (_road[i])
        {
            var road = content.Roads[_roadType[i] == 0 ? content.Roads.Default.Id : _roadType[i]];
            int mask = (RoadAt(x, y - 1) ? 1 : 0) | (RoadAt(x + 1, y) ? 2 : 0) | (RoadAt(x, y + 1) ? 4 : 0) | (RoadAt(x - 1, y) ? 8 : 0);
            glyph = road.GlyphFor(mask);
            fg = road.Foreground;
            bg = terrain.Buildable ? road.Background : terrain.Background;
        }

        return new CellVisual(glyph, fg, bg);
    }

    private bool RoadAt(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height && _road[y * _width + x];
}

[Flags]
public enum CellChange
{
    None = 0,

    /// <summary>A building, road, zone or natural feature that was there is gone.</summary>
    Lost = 1,

    BuildingGained = 2,
    BuildingChanged = 4,
    RoadGained = 8,
    Zoned = 16,
    Terrain = 32,
}
