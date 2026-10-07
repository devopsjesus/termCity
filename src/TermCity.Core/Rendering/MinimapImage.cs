using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>
/// Low-resolution colour image of the whole map for the minimap. It is deliberately simple: water, hills and open
/// land in muted colours, roads as faint lines, and the player's zones in bright colours, so the city stands out.
/// The terrain layer is sampled once per map and size; roads and zones are re-applied only when the map changes,
/// from the sparse road/zone indexes, so a redraw never walks every cell of a large map.
/// </summary>
public sealed class MinimapImage
{
    private static readonly Rgb Water = Rgb.Hex(0x3c86dc);
    private static readonly Rgb HillTint = Rgb.Hex(0x7a6840);
    private static readonly Rgb Land = Rgb.Hex(0x2b5233);
    private static readonly Rgb RoadTint = Rgb.Hex(0xbdbdbd);

    /// <summary>How much of the road colour is mixed into the land: enough to read as a line, not enough to dominate.</summary>
    private const double RoadStrength = 0.28;

    private CityGame? _game;
    private int _width;
    private int _height;
    private int _overlayVersion = -1;
    private Rgb[] _terrain = [];
    private Rgb[] _pixels = [];
    private int[] _roadCounts = [];
    private bool[] _filled = [];

    public int Width => _width;

    public int Height => _height;

    /// <summary>Brings the image up to date for the given game and pixel dimensions.</summary>
    public void Update(CityGame game, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Minimap dimensions must be positive.");
        }

        if (!ReferenceEquals(game, _game) || width != _width || height != _height)
        {
            _game = game;
            _width = width;
            _height = height;
            BuildTerrain(game.Map);
            _overlayVersion = -1;
        }

        if (_overlayVersion != game.MapVersion)
        {
            BuildOverlay(game.Map);
            _overlayVersion = game.MapVersion;
        }
    }

    public Rgb this[int x, int y] => _pixels[y * _width + x];

    private void BuildTerrain(GameMap map)
    {
        int count = _width * _height;
        var total = new int[count];
        var water = new int[count];
        var hill = new int[count];

        var px = new int[map.Width];
        for (int x = 0; x < map.Width; x++)
        {
            px[x] = (int)((long)x * _width / map.Width);
        }

        for (int y = 0; y < map.Height; y++)
        {
            int row = (int)((long)y * _height / map.Height) * _width;
            for (int x = 0; x < map.Width; x++)
            {
                int p = row + px[x];
                total[p]++;
                var terrain = map.TerrainAt(x, y);
                if (!terrain.Buildable)
                {
                    water[p]++;
                }
                else if (terrain.BuildCostModifier > 1)
                {
                    hill[p]++;
                }

            }
        }

        _terrain = new Rgb[count];
        for (int i = 0; i < count; i++)
        {
            int n = Math.Max(1, total[i]);
            // Rivers matter more than anything else on the overview, and they are narrow: a pixel is water if even an
            // eighth of it is. Hills are only a faint shade, and only where they cover most of a pixel.
            _terrain[i] = water[i] * 8 >= n ? Water
                : hill[i] * 5 >= n * 3 ? Rgb.Blend(Land, HillTint, 0.45)
                : Land;
        }

        _pixels = new Rgb[count];
        _roadCounts = new int[count];
        _filled = new bool[count];
    }

    private void BuildOverlay(GameMap map)
    {
        Array.Copy(_terrain, _pixels, _terrain.Length);
        Array.Clear(_roadCounts);
        Array.Clear(_filled);

        // A pixel counts as road only if enough road cells fall in it for a line to be running through, so a stray
        // corner or stub does not smear into the image.
        foreach (int index in map.RoadCells)
        {
            _roadCounts[PixelOf(map, index)]++;
        }

        int cellsX = (map.Width + _width - 1) / _width, cellsY = (map.Height + _height - 1) / _height;
        int needed = Math.Max(1, (int)Math.Round(Math.Min(cellsX, cellsY) * 0.6));
        for (int p = 0; p < _roadCounts.Length; p++)
        {
            if (_roadCounts[p] >= needed)
            {
                _pixels[p] = Rgb.Blend(_terrain[p], RoadTint, RoadStrength);
            }
        }
        // Zones win over roads; a pixel counts as built-up if any zone cell in it is filled.
        foreach (var zone in Zones.Placeable)
        {
            var info = Zones.Get(zone);
            foreach (int index in map.ZoneCells(zone))
            {
                int p = PixelOf(map, index);
                var cell = map.PosOf(index);
                bool built = map.IsFilled(cell.X, cell.Y);
                if (built || !_filled[p])
                {
                    _pixels[p] = built ? info.Foreground : info.Foreground.Scale(0.55);
                    _filled[p] |= built;
                }
            }
        }
    }

    private int PixelOf(GameMap map, int index)
    {
        int x = index % map.Width, y = index / map.Width;
        return (int)((long)y * _height / map.Height) * _width + (int)((long)x * _width / map.Width);
    }
}
