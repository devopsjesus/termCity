namespace TermCity.Core.World;

/// <summary>
/// Roads may cross and branch but never run side by side. This clears generated roads that sit in a 2x2 block of road,
/// dropping the humblest road of each block, and only where the cells around it stay connected.
/// </summary>
public static class RoadSeparation
{
    private static readonly (int X, int Y)[] Ring = [(-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0)];

    public static int Apply(GameMap map)
    {
        int removed = 0;
        for (int pass = 0; pass < 8; pass++)
        {
            int before = removed;
            for (int y = 0; y < map.Height - 1; y++)
            {
                for (int x = 0; x < map.Width - 1; x++)
                {
                    if (map.HasRoad(x, y) && map.HasRoad(x + 1, y) && map.HasRoad(x, y + 1) && map.HasRoad(x + 1, y + 1) &&
                        TryBreak(map, x, y))
                    {
                        removed++;
                    }
                }
            }

            if (removed == before)
            {
                break;
            }
        }

        return removed;
    }

    /// <summary>Removes little detached scraps of road, the leftovers where a street was cut off from the rest.</summary>
    public static int RemoveFragments(GameMap map, int minSize)
    {
        var seen = new HashSet<int>();
        int removed = 0;
        foreach (int start in map.RoadCells.ToList())
        {
            if (!seen.Add(start))
            {
                continue;
            }

            var component = new List<int> { start };
            for (int i = 0; i < component.Count; i++)
            {
                int x = component[i] % map.Width, y = component[i] / map.Width;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (map.HasRoad(nx, ny) && seen.Add(ny * map.Width + nx))
                    {
                        component.Add(ny * map.Width + nx);
                    }
                }
            }

            if (component.Count < minSize)
            {
                foreach (int cell in component)
                {
                    map.SetRoad(cell % map.Width, cell / map.Width, false);
                }

                removed += component.Count;
            }
        }

        return removed;
    }

    private static bool TryBreak(GameMap map, int x, int y)
    {
        var cells = new[] { (x, y), (x + 1, y), (x, y + 1), (x + 1, y + 1) };
        foreach (var (cx, cy) in cells.OrderBy(c => map.RoadTypeAt(c.Item1, c.Item2)!.Rank).ThenBy(c => c.Item2).ThenBy(c => c.Item1))
        {
            if (map.ZoneAt(cx, cy) == ZoneType.None && KeepsNeighboursConnected(map, cx, cy))
            {
                map.SetRoad(cx, cy, false);
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the road cells touching this one stay joined through the cells around it once it is gone.</summary>
    private static bool KeepsNeighboursConnected(GameMap map, int x, int y)
    {
        var road = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            road[i] = map.HasRoad(x + Ring[i].X, y + Ring[i].Y);
        }

        var seen = new bool[8];
        int components = 0;
        for (int i = 1; i < 8; i += 2)
        {
            if (!road[i] || seen[i])
            {
                continue;
            }

            components++;
            var stack = new Stack<int>();
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                foreach (int n in new[] { (c + 1) % 8, (c + 7) % 8 })
                {
                    // Ring cells that are corners join only through the side cells they sit between.
                    if (road[n] && !seen[n] && (c % 2 == 1 || n % 2 == 1))
                    {
                        seen[n] = true;
                        stack.Push(n);
                    }
                }
            }
        }

        return components <= 1;
    }
}
