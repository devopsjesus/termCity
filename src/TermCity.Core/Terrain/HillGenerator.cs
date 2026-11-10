using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Terrain;

/// <summary>Places coherent hill regions on dry land, including an optional budgeted river canyon.</summary>
public sealed class HillGenerator : ITerrainGenerator
{
    internal static void AddRiverCanyon(GenerationContext context)
    {
        if (context.RegionalHillsComplete) return;
        var map = context.Map;
        var rng = context.CreateRandom("river-canyons");
        if (!rng.Chance(0.3) || map.Content.Terrains.Find(DefaultTerrains.HillName) is not { } hill) return;
        int radius = Math.Clamp(Math.Min(map.Width, map.Height) / 7, 10, 24);
        var candidates = new List<Pos>();
        for (int y = radius + 2; y < map.Height - radius - 2; y++)
            for (int x = radius + 2; x < map.Width - radius - 2; x++)
                if (map.IsRiver(x, y) && !map.IsSea(x, y)) candidates.Add(new(x, y));
        if (candidates.Count == 0) return;
        var center = candidates[rng.Next(candidates.Count)];
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
            for (int x = center.X - radius; x <= center.X + radius; x++)
            {
                double dx = x - center.X, dy = y - center.Y;
                if (dx * dx + dy * dy <= radius * radius && map.TerrainAt(x, y).Buildable)
                    map.SetTerrain(x, y, hill);
            }
    }

    public bool GeographicRegions { get; init; }

    public int Order => GeographicRegions ? 21 : 10;

    public double Coverage { get; init; } = 0.17;

    public double Frequency { get; init; } = 0.055;

    public void Generate(GenerationContext context, TerrainType terrain)
    {
        var map = context.Map;
        if (!GeographicRegions)
        {
            var noise = new PerlinNoise(context.CreateRandom("hills"));
            var values = new double[map.Width * map.Height];
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    values[map.Index(x, y)] = noise.Fractal(x * Frequency * .5, y * Frequency, 3);
            double threshold = PerlinNoise.ThresholdForCoverage(values, Coverage);
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    if (values[map.Index(x, y)] >= threshold) map.SetTerrain(x, y, terrain);
            return;
        }
        var regions = LandscapeRegions.Create(context, "hills", Coverage, Frequency, baseTerrainOnly: true);
        var random = context.CreateRandom("river-canyons");
        int radius = Math.Min(Math.Clamp(Math.Min(map.Width, map.Height) / 7, 10, 24),
            (int)Math.Sqrt(regions.TargetCells * .4 / Math.PI));
        if (radius >= 4 && random.Chance(.3))
        {
            var rivers = new List<Pos>();
            for (int y = radius + 2; y < map.Height - radius - 2; y++)
                for (int x = radius + 2; x < map.Width - radius - 2; x++)
                    if (map.IsRiver(x, y) && !map.IsSea(x, y)) rivers.Add(new(x, y));
            if (rivers.Count > 0)
            {
                var center = rivers[random.Next(rivers.Count)];
                var canyon = new List<int>();
                for (int y = center.Y - radius; y <= center.Y + radius; y++)
                    for (int x = center.X - radius; x <= center.X + radius; x++)
                        if ((x - center.X) * (x - center.X) + (y - center.Y) * (y - center.Y) <= radius * radius &&
                            map.TerrainAt(x, y) == map.Content.Terrains.Base) canyon.Add(map.Index(x, y));
                regions.Reserve(canyon);
            }
        }
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
                if (regions.Mask[map.Index(x, y)]) map.SetTerrain(x, y, terrain);
        context.RegionalHillsComplete = true;
    }
}
