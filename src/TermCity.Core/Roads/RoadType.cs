using TermCity.Core.Registry;
using TermCity.Core.Util;

namespace TermCity.Core.Roads;

/// <summary>
/// A kind of road. Roads share one layer on the map; each cell remembers which type it is.
/// To add a type (a paved causeway, a drove road, a canal towpath...) register another <see cref="RoadType"/>.
/// </summary>
public sealed class RoadType : RegisteredType
{
    /// <summary>
    /// Sixteen glyphs indexed by which neighbours are also roads: bit 1 = north, 2 = east, 4 = south, 8 = west. They are
    /// drawn where roads are not shown as smooth curves (under a map overlay, and on the minimap). Every road type is
    /// otherwise drawn as a continuous curve in its colours, which merges smoothly with the roads it meets.
    /// </summary>
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    public required Rgb Background { get; init; }
    public Rgb SurfaceColor { get; init; } = Rgb.Hex(0x897c5e);

    /// <summary>Multiplier on <see cref="Simulation.GameConfig.RoadCostPerCell"/>.</summary>
    public double CostMultiplier { get; init; } = 1.0;

    /// <summary>Bigger roads can replace smaller ones (an upgrade, paying only the difference).</summary>
    public int Rank { get; init; }

    private float _widthCells = 1;
    public float WidthCells { get => _widthCells; init => _widthCells = value; }

    private double _bendRadiusCells;
    public double BendRadiusCells { get => _bendRadiusCells; init => _bendRadiusCells = value; }

    /// <summary>How many trips a cell of this road carries each week before the city starts to jam.</summary>
    public int TrafficCapacity { get; init; } = 6;

    /// <summary>Weekly maintenance for one cell of this road at full funding.</summary>
    public int WeeklyUpkeep { get; init; } = 1;

    public bool PlayerPlaceable { get; init; } = true;

    private string _description = string.Empty;
    public string Description { get => _description; init => _description = value; }

    public string GlyphFor(int neighbourMask) => Glyphs[neighbourMask & 15];

    internal RoadType WithLegacyLayout(bool legacy)
    {
        var copy = (RoadType)MemberwiseClone();
        var dimensions = Name switch
        {
            DefaultRoads.CobbledName => DefaultRoads.CobbledRoad(legacy),
            DefaultRoads.KingsRoadName => DefaultRoads.KingsRoad(legacy),
            _ => null,
        };
        if (dimensions is not null)
        {
            copy._widthCells = dimensions.WidthCells;
            copy._bendRadiusCells = dimensions.BendRadiusCells;
            copy._description = dimensions.Description;
        }
        return copy;
    }
}

public sealed class RoadRegistry : TypeRegistry<RoadType>
{
    /// <summary>Id 0 is reserved for "no road".</summary>
    public RoadRegistry() : base(1)
    {
    }

    /// <summary>The type used when none is specified: the smallest, cheapest road.</summary>
    public RoadType Default => this.OrderBy(r => r.Rank).First();

    public static RoadRegistry CreateDefault(bool retainLegacyLayout = false)
    {
        var registry = new RoadRegistry();
        registry.Register(DefaultRoads.Track());
        registry.Register(DefaultRoads.CobbledRoad(retainLegacyLayout));
        registry.Register(DefaultRoads.KingsRoad(retainLegacyLayout));
        return registry;
    }
}

public static class DefaultRoads
{
    public const string TrackName = "Dirt Track";
    public const string CobbledName = "Cobbled Road";
    public const string KingsRoadName = "King's Road";

    // Index = neighbour mask (N=1, E=2, S=4, W=8). Dead ends and lone cells use the straight piece.
    private static readonly string[] Light =
        ["•", "│", "─", "└", "│", "│", "┌", "├", "─", "┘", "─", "┴", "┐", "┤", "┬", "┼"];

    private static readonly string[] Heavy =
        ["•", "┃", "━", "┗", "┃", "┃", "┏", "┣", "━", "┛", "━", "┻", "┓", "┫", "┳", "╋"];

    private static readonly string[] Double =
        ["•", "║", "═", "╚", "║", "║", "╔", "╠", "═", "╝", "═", "╩", "╗", "╣", "╦", "╬"];

    public static RoadType Track() => new()
    {
        Name = TrackName,
        Glyphs = Light,
        Foreground = Rgb.Hex(0xd6d6d6),
        Background = Rgb.Hex(0x2b2b30),
        CostMultiplier = 1.0,
        Rank = 1,
        BendRadiusCells = 1,
        TrafficCapacity = 6,
        WeeklyUpkeep = 1,
        Description = "One-cell dirt track between homes and fields; a narrow, rutted surface for light traffic",
    };

    public static RoadType CobbledRoad(bool retainLegacyLayout = false) => new()
    {
        Name = CobbledName,
        WidthCells = retainLegacyLayout ? 1 : 2,
        SurfaceColor = Rgb.Hex(0x707477),
        Glyphs = Heavy,
        Foreground = Rgb.Hex(0xa9d4ff),
        Background = Rgb.Hex(0x2b2f3a),
        CostMultiplier = 1.8,
        Rank = 2,
        BendRadiusCells = 1,
        TrafficCapacity = 20,
        WeeklyUpkeep = 2,
        Description = retainLegacyLayout ? "One-cell cobbled market road for carts and market traffic" :
            "Two-cell cobbled market road, about nine feet wide; firm surfacing gives carts more room than a dirt track",
    };

    public static RoadType KingsRoad(bool retainLegacyLayout = false) => new()
    {
        Name = KingsRoadName,
        SurfaceColor = Rgb.Hex(0x565757),
        Glyphs = Double,
        Foreground = Rgb.Hex(0xffd166),
        Background = Rgb.Hex(0x33302a),
        CostMultiplier = 3.0,
        Rank = 3,
        WidthCells = retainLegacyLayout ? 2 : 4,
        BendRadiusCells = retainLegacyLayout ? 6 : 8,
        TrafficCapacity = 60,
        WeeklyUpkeep = 3,
        Description = retainLegacyLayout ? "Two-cell King's Road: a paved highway with broad bends for long-distance travel" :
            "Four-cell King's Road, about eighteen feet wide: a paved, patrolled highway with broad bends for long-distance travel",
    };
}
