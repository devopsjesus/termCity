using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Terrain;

internal sealed record LandscapeRegions(bool[] Mask, int[] RankedCells, int TargetCells)
{
    internal static LandscapeRegions Create(GenerationContext context, string stage, double coverage,
        double frequency = .065, bool baseTerrainOnly = false)
    {
        if (!double.IsFinite(coverage) || coverage < 0 || coverage > 1)
            throw new ArgumentOutOfRangeException(nameof(coverage));
        if (!double.IsFinite(frequency) || frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        var map = context.Map;
        var cells = Enumerable.Range(0, map.Width * map.Height)
            .Where(cell => map.TerrainAt(cell % map.Width, cell / map.Width).Buildable &&
                (!baseTerrainOnly || map.TerrainAt(cell % map.Width, cell / map.Width) == map.Content.Terrains.Base)).ToArray();
        int target = (int)Math.Round(cells.Length * coverage);
        var mask = new bool[map.Width * map.Height];
        if (target == 0) return new(mask, cells, 0);
        var random = context.CreateRandom("regions:" + stage);
        var noise = new PerlinNoise(context.CreateRandom("region-edges:" + stage));
        int count = Math.Min(cells.Length, Math.Clamp((int)Math.Round(cells.Length / 12000d), 2, 64));
        var centers = new List<Pos>();
        var radii = new List<double>();
        double radius = Math.Sqrt(target / (Math.PI * count));
        for (int region = 0; region < count; region++)
        {
            Pos center = default;
            double best = -1;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                var candidate = map.PosOf(cells[random.Next(cells.Length)]);
                double separation = centers.Count == 0 ? 1 : centers.Min(previous =>
                    (candidate.X - previous.X) * (double)(candidate.X - previous.X) +
                    (candidate.Y - previous.Y) * (double)(candidate.Y - previous.Y));
                if (separation > best) { center = candidate; best = separation; }
            }
            centers.Add(center);
            radii.Add(Math.Max(1, radius * (.85 + random.NextDouble() * .3)));
        }
        var scores = new double[mask.Length];
        foreach (int cell in cells)
        {
            var point = map.PosOf(cell);
            double edge = .85 + noise.Fractal(point.X * frequency, point.Y * frequency, 2) * .3;
            double score = double.NegativeInfinity;
            for (int region = 0; region < centers.Count; region++)
            {
                double dx = point.X - centers[region].X, dy = point.Y - centers[region].Y;
                double size = radii[region] * edge;
                score = Math.Max(score, 1 - (dx * dx + dy * dy) / (size * size));
            }
            scores[cell] = score;
        }
        Array.Sort(cells, (first, last) =>
        {
            int order = scores[last].CompareTo(scores[first]);
            return order != 0 ? order : first.CompareTo(last);
        });
        for (int i = 0; i < target; i++) mask[cells[i]] = true;
        return new(mask, cells, target);
    }

    internal void Reserve(IReadOnlyCollection<int> cells)
    {
        if (cells.Count > TargetCells)
            throw new ArgumentException("Reserved hill terrain exceeds its landscape budget.", nameof(cells));
        Array.Clear(Mask);
        foreach (int cell in cells) Mask[cell] = true;
        int remaining = TargetCells - cells.Count;
        foreach (int cell in RankedCells)
        {
            if (remaining == 0) break;
            if (Mask[cell]) continue;
            Mask[cell] = true;
            remaining--;
        }
    }
}
