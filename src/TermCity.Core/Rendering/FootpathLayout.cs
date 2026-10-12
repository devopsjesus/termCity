using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public static class FootpathLayout
{
    /// <summary>North = 1, east = 2, south = 4, west = 8; paths join neighboring paths, roads and buildings.</summary>
    public static int Connections(GameMap map, int x, int y)
    {
        bool Joins(int cx, int cy) => map.InBounds(cx, cy) &&
            (map.PathWearAt(cx, cy) > 0 || map.HasRoad(cx, cy) || map.BuildingAt(cx, cy) is not null);
        return (Joins(x, y - 1) ? 1 : 0) | (Joins(x + 1, y) ? 2 : 0) |
            (Joins(x, y + 1) ? 4 : 0) | (Joins(x - 1, y) ? 8 : 0);
    }

    public static string Glyph(int connections) => connections switch
    {
        1 or 4 or 5 => "│",
        2 or 8 or 10 => "─",
        3 => "└", 6 => "┌", 9 => "┘", 12 => "┐",
        7 => "├", 11 => "┴", 13 => "┤", 14 => "┬", 15 => "┼",
        _ => "·",
    };
}
