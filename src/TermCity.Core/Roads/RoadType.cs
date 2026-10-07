using TermCity.Core.Registry;
using TermCity.Core.Util;

namespace TermCity.Core.Roads;

/// <summary>
/// A kind of road. Roads share one layer on the map; each cell remembers which type it is.
/// To add a type (boulevard, dirt track, rail...) register another <see cref="RoadType"/>.
/// </summary>
public sealed class RoadType : RegisteredType
{
    /// <summary>
    /// Sixteen glyphs indexed by which neighbours are also roads: bit 1 = north, 2 = east, 4 = south, 8 = west.
    /// </summary>
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    public required Rgb Background { get; init; }

    /// <summary>Multiplier on <see cref="Simulation.GameConfig.RoadCostPerCell"/>.</summary>
    public double CostMultiplier { get; init; } = 1.0;

    /// <summary>Bigger roads can replace smaller ones (an upgrade, paying only the difference).</summary>
    public int Rank { get; init; }

    /// <summary>How many trips a cell of this road carries each week before the city starts to jam.</summary>
    public int TrafficCapacity { get; init; } = 6;

    /// <summary>Weekly maintenance for one cell of this road at full funding.</summary>
    public int WeeklyUpkeep { get; init; } = 1;

    public bool PlayerPlaceable { get; init; } = true;

    public string Description { get; init; } = string.Empty;

    public string GlyphFor(int neighbourMask) => Glyphs[neighbourMask & 15];
}

public sealed class RoadRegistry : TypeRegistry<RoadType>
{
    /// <summary>Id 0 is reserved for "no road".</summary>
    public RoadRegistry() : base(1)
    {
    }

    /// <summary>The type used when none is specified: the smallest, cheapest road.</summary>
    public RoadType Default => this.OrderBy(r => r.Rank).First();

    public static RoadRegistry CreateDefault()
    {
        var registry = new RoadRegistry();
        registry.Register(DefaultRoads.Street());
        registry.Register(DefaultRoads.Avenue());
        registry.Register(DefaultRoads.Highway());
        return registry;
    }
}

public static class DefaultRoads
{
    public const string StreetName = "Street";
    public const string AvenueName = "Avenue";
    public const string HighwayName = "Highway";

    // Index = neighbour mask (N=1, E=2, S=4, W=8). Dead ends and lone cells use the straight piece.
    private static readonly string[] Light =
        ["•", "│", "─", "└", "│", "│", "┌", "├", "─", "┘", "─", "┴", "┐", "┤", "┬", "┼"];

    private static readonly string[] Heavy =
        ["•", "┃", "━", "┗", "┃", "┃", "┏", "┣", "━", "┛", "━", "┻", "┓", "┫", "┳", "╋"];

    private static readonly string[] Double =
        ["•", "║", "═", "╚", "║", "║", "╔", "╠", "═", "╝", "═", "╩", "╗", "╣", "╦", "╬"];

    public static RoadType Street() => new()
    {
        Name = StreetName,
        Glyphs = Light,
        Foreground = Rgb.Hex(0xd6d6d6),
        Background = Rgb.Hex(0x2b2b30),
        CostMultiplier = 1.0,
        Rank = 1,
        TrafficCapacity = 6,
        WeeklyUpkeep = 1,
        Description = "Local street",
    };

    public static RoadType Avenue() => new()
    {
        Name = AvenueName,
        Glyphs = Heavy,
        Foreground = Rgb.Hex(0xa9d4ff),
        Background = Rgb.Hex(0x2b2f3a),
        CostMultiplier = 1.8,
        Rank = 2,
        TrafficCapacity = 20,
        WeeklyUpkeep = 2,
        Description = "Wide avenue",
    };

    public static RoadType Highway() => new()
    {
        Name = HighwayName,
        Glyphs = Double,
        Foreground = Rgb.Hex(0xffd166),
        Background = Rgb.Hex(0x33302a),
        CostMultiplier = 3.0,
        Rank = 3,
        TrafficCapacity = 60,
        WeeklyUpkeep = 4,
        Description = "Highway",
    };
}
