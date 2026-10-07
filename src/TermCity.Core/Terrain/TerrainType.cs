using TermCity.Core.Registry;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Terrain;

/// <summary>
/// Describes one kind of ground. To add a terrain (swamp, desert, snow...) create a <see cref="TerrainType"/>,
/// optionally give it an <see cref="ITerrainGenerator"/>, and register it in the <see cref="TerrainRegistry"/>.
/// </summary>
public sealed class TerrainType : RegisteredType
{
    /// <summary>One or more single-cell glyphs. A glyph is picked per cell for visual variety.</summary>
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    public required Rgb Background { get; init; }

    /// <summary>Whether roads, zones and buildings can be placed here.</summary>
    public bool Buildable { get; init; } = true;

    /// <summary>Multiplier applied to road and building costs on this terrain.</summary>
    public double BuildCostModifier { get; init; } = 1.0;

    /// <summary>Whether natural features such as trees and rocks may spawn here.</summary>
    public bool AllowsFeatures { get; init; } = true;

    /// <summary>Per-feature spawn density multipliers keyed by feature name (missing = 1.0).</summary>
    public IReadOnlyDictionary<string, double>? FeatureDensity { get; init; }

    /// <summary>Paints this terrain onto a new map. Null for terrain that is only ever the base layer.</summary>
    public ITerrainGenerator? Generator { get; init; }

    public string Description { get; init; } = string.Empty;

    public string GlyphAt(int x, int y) => Glyphs[CellHash.Pick(x, y, Glyphs.Count)];

    public double DensityFor(string featureName) =>
        FeatureDensity is not null && FeatureDensity.TryGetValue(featureName, out double d) ? d : 1.0;
}

public interface ITerrainGenerator
{
    /// <summary>Lower orders run first, so later generators paint over earlier ones.</summary>
    int Order { get; }

    void Generate(GenerationContext context, TerrainType terrain);
}

/// <summary>Shared inputs for every generation stage.</summary>
public sealed class GenerationContext(GameMap map, int seed)
{
    public GameMap Map { get; } = map;

    public int Seed { get; } = seed;

    public GameRandom CreateRandom(string stage) => GameRandom.ForStage(Seed, stage);
}

public sealed class TerrainRegistry : TypeRegistry<TerrainType>
{
    public TerrainRegistry() : base(0)
    {
    }

    /// <summary>The first registered terrain fills the map before generators run.</summary>
    public TerrainType Base => this[0];

    public static TerrainRegistry CreateDefault()
    {
        var registry = new TerrainRegistry();
        registry.Register(DefaultTerrains.Grass());
        registry.Register(DefaultTerrains.Hill());
        registry.Register(DefaultTerrains.Water());
        registry.Alias("Grass", DefaultTerrains.GrassName);
        return registry;
    }
}

public static class DefaultTerrains
{
    public const string GrassName = "Meadow";
    public const string HillName = "Hill";
    public const string WaterName = "Water";

    public static TerrainType Grass() => new()
    {
        Name = GrassName,
        // Mostly plain dots keep the ground texture quiet beside roads, buildings and animated features.
        Glyphs = [.. Enumerable.Repeat("·", 33), ",", "'", "\""],
        Foreground = Rgb.Hex(0x4f8f4a),
        Background = Rgb.Hex(0x16301a),
        Description = "Open meadow and common pasture",
    };

    public static TerrainType Hill() => new()
    {
        Name = HillName,
        Glyphs = [.. Enumerable.Repeat("∩", 7), "⌒"],
        Foreground = Rgb.Hex(0xc9a468),
        Background = Rgb.Hex(0x3d3320),
        BuildCostModifier = 1.5,
        FeatureDensity = new Dictionary<string, double> { ["Tree"] = 0.5, ["Rock"] = 3.0 },
        Generator = new HillGenerator(),
        Description = "Rolling downs (building costs 1.5x)",
    };

    public static TerrainType Water() => new()
    {
        Name = WaterName,
        Glyphs = [.. Enumerable.Repeat("≈", 5), "~"],
        Foreground = Rgb.Hex(0x7fc4ff),
        Background = Rgb.Hex(0x0f3a66),
        Buildable = false,
        AllowsFeatures = false,
        Generator = new WaterGenerator(),
        Description = "Rivers, meres and the sea (cannot be built on; existing roads cross by ford and bridge)",
    };
}
