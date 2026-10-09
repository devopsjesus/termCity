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

    /// <summary>Lays a road through percentage points; water is crossed only between two land approaches.</summary>
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

        var bridges = new HashSet<Pos>();
        for (int start = 0; start < cells.Count; start++)
        {
            if (map.TerrainAt(cells[start].X, cells[start].Y).Buildable) continue;
            int end = start;
            while (end + 1 < cells.Count && !map.TerrainAt(cells[end + 1].X, cells[end + 1].Y).Buildable) end++;
            if (start > 0 && end + 1 < cells.Count)
                for (int index = start; index <= end; index++) bridges.Add(cells[index]);
            start = end;
        }
        foreach (var p in RoadRules.Plan(map, cells, road.Rank,
            (x, y) => map.TerrainAt(x, y).Buildable || bridges.Contains(new(x, y))))
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
        bool Open(int x, int y)
        {
            if (!map.TerrainAt(x, y).Buildable) return false;
            if (map.HasRoad(x, y)) return true;
            foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
                if (map.HasRoad(x + dx, y + dy) && !map.TerrainAt(x + dx, y + dy).Buildable) return false;
            return true;
        }
        void Run(List<Pos> run)
        {
            if (run.Count == 0) return;
            foreach (var p in RoadRules.Plan(map, run, street.Rank, Open))
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
                if (cell(x, y) && Open(x, y)) cells.Add(new Pos(x, y)); else Run(cells);
            }
            Run(cells);
        }
        for (int x = 0; x < map.Width; x += 5)
        {
            for (int y = 0; y < map.Height; y++)
            {
                if (cell(x, y) && Open(x, y)) cells.Add(new Pos(x, y)); else Run(cells);
            }
            Run(cells);
        }
    }
}
