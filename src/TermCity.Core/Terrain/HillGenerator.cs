using TermCity.Core.Util;

namespace TermCity.Core.Terrain;

/// <summary>Scatters noise-driven blobs of hills across the map.</summary>
public sealed class HillGenerator : ITerrainGenerator
{
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
                // Terminal cells are about twice as tall as wide, so squash x to keep blobs round.
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
