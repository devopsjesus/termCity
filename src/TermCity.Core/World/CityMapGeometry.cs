using TermCity.Core.Roads;

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

    public static void Road(GameMap map, RoadType road, params (int X, int Y)[] points)
    {
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
                map.SetFeature(x, y, null);
                map.SetRoad(x, y, road);
                if (x == endX && y == endY) break;
                int twice = error * 2;
                if (twice > -dy)
                {
                    error -= dy;
                    x += Math.Sign(endX - x);
                    map.SetFeature(x, y, null);
                    map.SetRoad(x, y, road);
                }
                if (twice < dx)
                {
                    error += dx;
                    y += Math.Sign(endY - y);
                }
            }
        }
    }
}
