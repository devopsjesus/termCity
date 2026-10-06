using TermCity.Core.Registry;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Buildings;

/// <summary>
/// A structure on the building layer. Growth buildings appear automatically when residents move into a zone;
/// <see cref="PlayerPlaceable"/> buildings (fire stations, police, ...) are placed by the player and cost money.
/// </summary>
public sealed class BuildingType : RegisteredType
{
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    /// <summary>Zone this building grows in, or <see cref="ZoneType.None"/> for stand-alone buildings.</summary>
    public ZoneType Zone { get; init; } = ZoneType.None;

    /// <summary>Cost per cell when placed by the player.</summary>
    public int Cost { get; init; }

    public bool PlayerPlaceable { get; init; }

    public string Description { get; init; } = string.Empty;

    public string GlyphAt(int x, int y) => Glyphs[CellHash.Pick(x, y, Glyphs.Count)];
}

public sealed class BuildingRegistry : TypeRegistry<BuildingType>
{
    /// <summary>Id 0 is reserved for "no building".</summary>
    public BuildingRegistry() : base(1)
    {
    }

    public BuildingType? ForZone(ZoneType zone) => this.FirstOrDefault(b => b.Zone == zone);

    public static BuildingRegistry CreateDefault()
    {
        var registry = new BuildingRegistry();
        registry.Register(new BuildingType
        {
            Name = "House",
            Glyphs = ["⌂", "⌂", "⌂", "▟"],
            Foreground = Rgb.Hex(0x9dff9d),
            Zone = ZoneType.Residential,
            Description = "A family home",
        });
        registry.Register(new BuildingType
        {
            Name = "Shop",
            Glyphs = ["▣", "▦", "▣"],
            Foreground = Rgb.Hex(0x9fd0ff),
            Zone = ZoneType.Commercial,
            Description = "Stores and offices",
        });
        registry.Register(new BuildingType
        {
            Name = "Factory",
            Glyphs = ["▤", "▩", "▤"],
            Foreground = Rgb.Hex(0xffe08a),
            Zone = ZoneType.Industrial,
            Description = "Workshops and plants",
        });
        return registry;
    }
}
