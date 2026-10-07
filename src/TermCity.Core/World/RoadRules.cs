using TermCity.Core.Util;

namespace TermCity.Core.World;

/// <summary>
/// How roads may meet. Roads cross and branch but never run side by side, and junctions keep their distance, so every
/// road reads as a clean line: a new road cell may touch another road only where the stroke itself runs into it or at
/// the stroke's two ends, never completes a 2x2 block of road, and never makes a new junction right next to another.
/// </summary>
public static class RoadRules
{
    /// <summary>Junctions must be at least this many cells apart (Chebyshev distance).</summary>
    public const int JunctionSpacing = 2;

    private static readonly (int X, int Y)[] Around = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>
    /// The cells of a road stroke that can be laid, in order, each judged against the roads already there and the cells
    /// laid before it. <paramref name="canUse"/> says whether a cell is free ground; a cell that already has a road is
    /// passed through, and is included only when <paramref name="rank"/> upgrades it.
    /// </summary>
    public static List<Pos> Plan(GameMap map, IReadOnlyList<Pos> stroke, int rank, Func<int, int, bool> canUse)
    {
        var plan = new List<Pos>();
        HashSet<Pos>? pending = null;
        for (int i = 0; i < stroke.Count; i++)
        {
            var p = stroke[i];
            if (!map.InBounds(p) || pending?.Contains(p) == true)
            {
                continue;
            }

            if (map.RoadTypeAt(p.X, p.Y) is { } existing)
            {
                if (existing.Rank < rank && canUse(p.X, p.Y))
                {
                    plan.Add(p);
                }

                continue;
            }

            if (!canUse(p.X, p.Y))
            {
                continue;
            }

            var previous = i > 0 ? stroke[i - 1] : p;
            var next = i < stroke.Count - 1 ? stroke[i + 1] : p;
            bool end = i == 0 || i == stroke.Count - 1;
            if (!Fits(map, p, previous, next, end, pending))
            {
                continue;
            }

            (pending ??= []).Add(p);
            plan.Add(p);
        }

        return plan;
    }

    /// <summary>True when a single road cell may be laid at <paramref name="p"/>, as a stroke of one cell.</summary>
    public static bool CanLay(GameMap map, Pos p) => Fits(map, p, p, p, true, null);

    public static bool CompletesBlock(GameMap map, int x, int y, ISet<Pos>? pending)
    {
        for (int ax = x - 1; ax <= x; ax++)
        {
            for (int ay = y - 1; ay <= y; ay++)
            {
                if (BlockHas(map, pending, x, y, ax, ay))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Fits(GameMap map, Pos p, Pos previous, Pos next, bool end, ISet<Pos>? pending)
    {
        foreach (var (dx, dy) in Around)
        {
            var n = new Pos(p.X + dx, p.Y + dy);
            if (!Road(map, pending, n.X, n.Y))
            {
                continue;
            }

            if (!end && n != previous && n != next)
            {
                return false;
            }

            if (Neighbours(map, pending, n.X, n.Y) == 2 && JunctionNear(map, pending, n))
            {
                return false;
            }
        }

        return !CompletesBlock(map, p.X, p.Y, pending);
    }

    private static bool JunctionNear(GameMap map, ISet<Pos>? pending, Pos at)
    {
        // A cell with two neighbours becomes a junction when a third road joins it.
        for (int y = at.Y - JunctionSpacing + 1; y <= at.Y + JunctionSpacing - 1; y++)
        {
            for (int x = at.X - JunctionSpacing + 1; x <= at.X + JunctionSpacing - 1; x++)
            {
                if ((x != at.X || y != at.Y) && Road(map, pending, x, y) && Neighbours(map, pending, x, y) >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Neighbours(GameMap map, ISet<Pos>? pending, int x, int y)
    {
        int count = 0;
        foreach (var (dx, dy) in Around)
        {
            if (Road(map, pending, x + dx, y + dy))
            {
                count++;
            }
        }

        return count;
    }

    private static bool Road(GameMap map, ISet<Pos>? pending, int x, int y) =>
        map.HasRoad(x, y) || pending?.Contains(new Pos(x, y)) == true;

    private static bool BlockHas(GameMap map, ISet<Pos>? pending, int x, int y, int ax, int ay)
    {
        for (int cx = ax; cx <= ax + 1; cx++)
        {
            for (int cy = ay; cy <= ay + 1; cy++)
            {
                if ((cx != x || cy != y) && !Road(map, pending, cx, cy))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
