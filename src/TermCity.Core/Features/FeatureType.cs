using TermCity.Core.Registry;
using TermCity.Core.Rendering;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Features;

/// <summary>A natural object sitting on top of terrain (trees, rocks, ...). Cleared automatically when built over.</summary>
public sealed class FeatureType : RegisteredType
{
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    public IFeatureGenerator? Generator { get; init; }

    public string Description { get; init; } = string.Empty;

    public string GlyphAt(int x, int y) => Glyphs[CellHash.Pick(x, y, Glyphs.Count)];
}

public interface IFeatureGenerator
{
    int Order { get; }

    void Generate(GenerationContext context, FeatureType feature);
}

/// <summary>Randomly scatters a feature, optionally concentrated into noise-driven clusters (forests).</summary>
public sealed class ScatterFeatureGenerator : IFeatureGenerator
{
    public int Order { get; init; } = 10;

    /// <summary>Chance per eligible cell outside clusters.</summary>
    public double BaseDensity { get; init; }

    /// <summary>Fraction of the map that is considered "clustered" (0 disables clustering).</summary>
    public double ClusterCoverage { get; init; }

    /// <summary>Chance per eligible cell inside clusters.</summary>
    public double ClusterDensity { get; init; }

    public double ClusterFrequency { get; init; } = 0.07;
    public bool GeographicRegions { get; init; }

    /// <summary>Minimum center-to-center distance in map cells; zero keeps unrestricted scatter.</summary>
    public double MinimumSpacing { get; init; }

    public void Generate(GenerationContext context, FeatureType feature)
    {
        if (!double.IsFinite(MinimumSpacing) || MinimumSpacing < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumSpacing), "Feature spacing must be finite and nonnegative.");
        var map = context.Map;
        var rng = context.CreateRandom("feature:" + feature.Name);
        double[]? cluster = null;
        double threshold = 0;
        List<Pos>? candidates = MinimumSpacing > 0 ? [] : null;
        var regions = GeographicRegions
            ? LandscapeRegions.Create(context, "forest:" + feature.Name, ClusterCoverage, ClusterFrequency).Mask : null;

        if (ClusterCoverage > 0 && regions is null)
        {
            var noise = new PerlinNoise(context.CreateRandom("cluster:" + feature.Name));
            cluster = new double[map.Width * map.Height];
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    cluster[y * map.Width + x] = noise.Fractal(x * ClusterFrequency * 0.5, y * ClusterFrequency, 3);
                }
            }

            threshold = PerlinNoise.ThresholdForCoverage(cluster, ClusterCoverage);
        }

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                var terrain = map.TerrainAt(x, y);
                if (!terrain.AllowsFeatures || map.HasRoad(x, y) || map.FeatureAt(x, y) is not null)
                {
                    continue;
                }

                bool clustered = regions is not null ? regions[map.Index(x, y)] :
                    cluster is not null && cluster[y * map.Width + x] >= threshold;
                double density = clustered ? ClusterDensity : BaseDensity;
                if (rng.Chance(density * terrain.DensityFor(feature.Name)) && ShorelineLayout.NaturalFeatureFits(map, x, y))
                {
                    if (candidates is null) map.SetFeature(x, y, feature);
                    else candidates.Add(new(x, y));
                }
            }
        }
        if (candidates is null) return;
        int radius = (int)Math.Min(Math.Ceiling(MinimumSpacing), Math.Max(map.Width, map.Height));
        double spacingSquared = MinimumSpacing * MinimumSpacing;
        foreach (var candidate in candidates.OrderBy(p => CellHash.Pick(p.X ^ context.Seed, p.Y, int.MaxValue))
            .ThenBy(p => p.Y).ThenBy(p => p.X))
        {
            bool crowded = false;
            for (int y = Math.Max(0, candidate.Y - radius); y <= Math.Min(map.Height - 1, candidate.Y + radius) && !crowded; y++)
                for (int x = Math.Max(0, candidate.X - radius); x <= Math.Min(map.Width - 1, candidate.X + radius); x++)
                    if (map.FeatureAt(x, y) == feature &&
                        (x - candidate.X) * (double)(x - candidate.X) + (y - candidate.Y) * (double)(y - candidate.Y) < spacingSquared)
                    {
                        crowded = true;
                        break;
                    }
            if (!crowded) map.SetFeature(candidate.X, candidate.Y, feature);
        }
    }
}

public sealed class FeatureRegistry : TypeRegistry<FeatureType>
{
    /// <summary>Id 0 is reserved for "no feature".</summary>
    public FeatureRegistry() : base(1)
    {
    }

    public static FeatureRegistry CreateDefault()
    {
        var registry = new FeatureRegistry();
        registry.Register(DefaultFeatures.Tree());
        registry.Register(DefaultFeatures.Rock());
        return registry;
    }
}

public static class DefaultFeatures
{
    public const string TreeName = "Tree";
    public const string RockName = "Rock";

    public static FeatureType Tree() => new()
    {
        Name = TreeName,
        Glyphs = [.. Enumerable.Repeat("♣", 5), "♠"],
        Foreground = Rgb.Hex(0x3fbf4f),
        Generator = new ScatterFeatureGenerator
        {
            Order = 10,
            BaseDensity = 0.001,
            ClusterCoverage = 0.125,
            ClusterDensity = 0.55,
            GeographicRegions = true,
            MinimumSpacing = WorldScale.NaturalTreeSpacing,
        },
        Description = "Woodland (cleared when built over)",
    };

    public static FeatureType Rock() => new()
    {
        Name = RockName,
        Glyphs = [.. Enumerable.Repeat("●", 5), "◦"],
        Foreground = Rgb.Hex(0xa8a8a8),
        Generator = new ScatterFeatureGenerator
        {
            Order = 20,
            BaseDensity = 0.012,
        },
        Description = "Boulders and standing stones (cleared when built over)",
    };
}
