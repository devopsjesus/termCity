using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

internal static class TestCity
{
    /// <summary>A flat, empty map with a single road running edge to edge along row 20.</summary>
    public static CityGame Flat(int seed = 1, GameConfig? config = null)
    {
        var game = CityGame.New((config ?? new GameConfig()) with { Seed = seed });
        var map = game.Map;
        var grass = map.Content.Terrains.Base;
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, grass);
            }
        }

        for (int x = 0; x < map.Width; x++)
        {
            map.SetRoad(x, 20, true);
        }

        game.Touch();
        return game;
    }

    public static void Advance(CityGame game, int weeks)
    {
        for (int i = 0; i < weeks; i++)
        {
            game.AdvanceWeek();
        }
    }
}
