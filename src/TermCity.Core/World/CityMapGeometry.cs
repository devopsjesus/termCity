using TermCity.Core.Roads;
using TermCity.Core.Util;

namespace TermCity.Core.World;

internal static class CityMapGeometry
{
    public static bool Ellipse(double x, double y, double cx, double cy, double rx, double ry) =>
        Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2) <= 1;

    public static bool Contains((double X, double Y)[] polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Lays a road through the given points (percent of the map), over water too, as one stroke under <see cref="RoadRules"/>.</summary>
    public static void Road(GameMap map, RoadType road, params (int X, int Y)[] points)
    {
        var cells = new List<Pos>();
        for (int segment = 1; segment < points.Length; segment++)
        {
            var a = points[segment - 1];
            var b = points[segment];
            int x = a.X * (map.Width - 1) / 100, y = a.Y * (map.Height - 1) / 100;
            int endX = b.X * (map.Width - 1) / 100, endY = b.Y * (map.Height - 1) / 100;
            int dx = Math.Abs(endX - x), dy = Math.Abs(endY - y);
            int error = dx - dy;
            while (true)
            {
                cells.Add(new Pos(x, y));
                if (x == endX && y == endY) break;
                int twice = error * 2;
                if (twice > -dy)
                {
                    error -= dy;
                    x += Math.Sign(endX - x);
                    cells.Add(new Pos(x, y));
                }
                if (twice < dx)
                {
                    error += dx;
                    y += Math.Sign(endY - y);
                }
            }
        }

        foreach (var p in RoadRules.Plan(map, cells, road.Rank, (_, _) => true))
        {
            map.SetFeature(p.X, p.Y, null);
            map.SetRoad(p.X, p.Y, road);
        }
    }

    /// <summary>
    /// Lays the street grid: a street every five cells each way, an avenue every twenty, each run drawn as a stroke so
    /// streets stop short of the roads already there instead of running alongside them.
    /// </summary>
    public static void Grid(GameMap map, Func<int, int, bool> cell, RoadType street, RoadType avenue)
    {
        RoadType Type(int x, int y) => x % 20 == 0 || y % 20 == 0 ? avenue : street;
        void Run(List<Pos> run)
        {
            if (run.Count == 0) return;
            foreach (var p in RoadRules.Plan(map, run, street.Rank, (_, _) => true))
            {
                map.SetRoad(p.X, p.Y, Type(p.X, p.Y));
            }

            foreach (var p in run)
            {
                if (map.RoadTypeAt(p.X, p.Y) is { } existing && existing.Rank < Type(p.X, p.Y).Rank)
                    map.SetRoad(p.X, p.Y, Type(p.X, p.Y));
            }

            run.Clear();
        }

        var cells = new List<Pos>();
        for (int y = 0; y < map.Height; y += 5)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (cell(x, y)) cells.Add(new Pos(x, y)); else Run(cells);
            }
            Run(cells);
        }
        for (int x = 0; x < map.Width; x += 5)
        {
            for (int y = 0; y < map.Height; y++)
            {
                if (cell(x, y)) cells.Add(new Pos(x, y)); else Run(cells);
            }
            Run(cells);
        }
    }
}
