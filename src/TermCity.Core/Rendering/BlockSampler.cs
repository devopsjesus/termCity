using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>
/// Picks what to draw for one character when the map is zoomed out and a character stands for a block of cells.
/// The most important thing in the block wins: buildings, then zones, then roads (bigger roads first), then
/// water, hills, natural features and finally plain ground.
/// </summary>
public sealed class BlockSampler
{
    private readonly CityGame _game;
    private readonly GameMap _map;
    private readonly int[] _terrainPriority = new int[256];

    public BlockSampler(CityGame game)
    {
        _game = game;
        _map = game.Map;
        foreach (var terrain in _map.Content.Terrains)
        {
            _terrainPriority[terrain.Id] = !terrain.Buildable ? 30 : terrain.BuildCostModifier > 1 ? 20 : 0;
        }
    }

    /// <summary>The visual for the block whose top-left cell is (x0, y0) and whose side is <paramref name="size"/> cells.</summary>
    public CellVisual Sample(int x0, int y0, int size)
    {
        int bestX = x0, bestY = y0, best = -1;
        int right = Math.Min(_map.Width, x0 + size), bottom = Math.Min(_map.Height, y0 + size);

        for (int y = y0; y < bottom; y++)
        {
            for (int x = x0; x < right; x++)
            {
                int i = y * _map.Width + x;
                int priority = _terrainPriority[_map.TerrainLayer[i]];

                if (_map.FeatureLayer[i] != 0)
                {
                    priority = Math.Max(priority, 10);
                }

                if (_map.RoadLayer[i])
                {
                    priority = Math.Max(priority, 40 + _map.RoadTypeLayer[i]);
                }

                if (_map.ZoneLayer[i] != ZoneType.None)
                {
                    priority = Math.Max(priority, 50);
                }

                if (_map.BuildingLayer[i] != 0)
                {
                    priority = Math.Max(priority, 60);
                }

                if (priority > best)
                {
                    best = priority;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return CellRenderer.Render(_game, bestX, bestY);
    }
}
