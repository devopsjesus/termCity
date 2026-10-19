using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Terrain;

/// <summary>Scatters noise-driven blobs of hills across the map.</summary>
public sealed class HillGenerator : ITerrainGenerator
{
    internal static void AddRiverCanyon(GenerationContext context)
    {
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

    public int Order => 10;

    public double Coverage { get; init; } = 0.17;

    public double Frequency { get; init; } = 0.055;

    public void Generate(GenerationContext context, TerrainType terrain)
    {
        var map = context.Map;
        var noise = new PerlinNoise(context.CreateRandom("hills"));
        var values = new double[map.Width * map.Height];

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                // Display cells are about twice as tall as wide, so squash x to keep blobs round.
                values[y * map.Width + x] = noise.Fractal(x * Frequency * 0.5, y * Frequency, 3);
            }
        }

        double threshold = PerlinNoise.ThresholdForCoverage(values, Coverage);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (values[y * map.Width + x] >= threshold)
                {
                    map.SetTerrain(x, y, terrain);
                }
            }
        }
    }
}
