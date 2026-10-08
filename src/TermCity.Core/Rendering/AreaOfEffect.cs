using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public sealed record EffectArea(Pos Center, int Radius, IReadOnlyList<string> Glyphs);

public static class AreaOfEffect
{
    public const float RingAlpha = 0.38f, IconAlpha = 1f;

    public static IReadOnlyList<EffectArea> ForBuilding(BuildingType type, Pos center)
    {
        var areas = new Dictionary<int, List<string>>();
        void Add(int radius, string glyph)
        {
            if (radius <= 0) return;
            if (!areas.TryGetValue(radius, out var glyphs)) areas.Add(radius, glyphs = []);
            glyphs.Add(glyph);
        }
        if (type.Radius > 0 && ServiceKinds.Area.Contains(type.Service)) Add(type.Radius, type.Glyphs[0]);
        if (type.Pollution != 0)
            Add(CityServices.PollutionRadius(type.Pollution), type.Pollution < 0 ? "♧" : "Ψ");
        return areas.OrderBy(pair => pair.Key)
            .Select(pair => new EffectArea(center, pair.Key, pair.Value)).ToArray();
    }

    public static IEnumerable<EffectArea> InView(CityGame game, CellRect view, CellRect selection)
    {
        var map = game.Map;
        foreach (int index in map.ServiceCells.Concat(map.ZoneCells(World.ZoneType.Industrial)))
        {
            var p = map.PosOf(index);
            if (map.BuildingAt(p.X, p.Y) is not { } type) continue;
            var footprint = map.BuildingFootprintAt(p.X, p.Y);
            if (footprint.Right < selection.X || footprint.X > selection.Right ||
                footprint.Bottom < selection.Y || footprint.Y > selection.Bottom) continue;
            int radius = Math.Max(type.Radius, CityServices.PollutionRadius(type.Pollution));
            if (radius <= 0 || p.X + radius < view.X || p.X - radius > view.Right ||
                p.Y + radius < view.Y || p.Y - radius > view.Bottom) continue;
            foreach (var area in ForBuilding(type, p))
                if (p.X + area.Radius >= view.X && p.X - area.Radius <= view.Right &&
                    p.Y + area.Radius >= view.Y && p.Y - area.Radius <= view.Bottom)
                    yield return area;
        }
    }

    /// <summary>Pixelated ellipse matching a circular radius in map-cell space, including non-square terminal cells.</summary>
    public static IReadOnlySet<Pos> Outline(int radius, int cellWidth, int cellHeight, int stride, int pixel)
    {
        if (radius <= 0 || cellWidth <= 0 || cellHeight <= 0 || stride <= 0 || pixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        double rx = radius * cellWidth / (double)(stride * pixel);
        double ry = radius * cellHeight / (double)(stride * pixel);
        int steps = Math.Max(16, (int)Math.Ceiling(Math.Tau * Math.Max(rx, ry) * 2));
        var points = new HashSet<Pos>();
        for (int i = 0; i < steps; i++)
        {
            double angle = Math.Tau * i / steps;
            points.Add(new((int)Math.Round(rx * Math.Cos(angle)) * pixel,
                (int)Math.Round(ry * Math.Sin(angle)) * pixel));
        }
        return points;
    }
}
