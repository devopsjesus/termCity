using TermCity.Core.Registry;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Buildings;

/// <summary>
/// A structure on the building layer. Growth buildings appear automatically in a zone and come in density
/// <see cref="Level"/>s (house, apartments, tower); <see cref="PlayerPlaceable"/> civic buildings (power, water, fire,
/// police, health, schools, parks) are placed by the player, cost money to build and a little every week to run.
/// </summary>
public sealed class BuildingType : RegisteredType
{
    public required IReadOnlyList<string> Glyphs { get; init; }

    public required Rgb Foreground { get; init; }

    /// <summary>Zone this building grows in, or <see cref="ZoneType.None"/> for stand-alone buildings.</summary>
    public ZoneType Zone { get; init; } = ZoneType.None;

    /// <summary>Density level of a growth building (1 = smallest). Higher levels hold more people or jobs.</summary>
    public int Level { get; init; } = 1;

    /// <summary>
    /// Residential: residents the building holds. Commercial/industrial: jobs. Power/water: units of supply.
    /// Other services: unused.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>Cost per cell when placed by the player.</summary>
    public int Cost { get; init; }

    public bool PlayerPlaceable { get; init; }

    public string Description { get; init; } = string.Empty;

    public ServiceKind Service { get; init; } = ServiceKind.None;

    /// <summary>Reach, in cells, of an area service.</summary>
    public int Radius { get; init; }

    /// <summary>Strength (0-100) of an area service at its centre; strength fades towards the edge of the radius.</summary>
    public int Strength { get; init; } = 100;

    /// <summary>Weekly running cost at full funding.</summary>
    public int WeeklyUpkeep { get; init; }

    /// <summary>Pollution emitted (cells of reach scale with it). Heavy industry and coal plants pollute; parks clean.</summary>
    public int Pollution { get; init; }

    /// <summary>Wells and pumps must be placed next to open water.</summary>
    public bool RequiresWaterNearby { get; init; }

    /// <summary>Power units a working building draws each week.</summary>
    public int PowerUse { get; init; }

    /// <summary>Water units a working building draws each week.</summary>
    public int WaterUse { get; init; }

    public bool IsService => Service != ServiceKind.None;

    public string GlyphAt(int x, int y) => Glyphs[CellHash.Pick(x, y, Glyphs.Count)];
}

public sealed class BuildingRegistry : TypeRegistry<BuildingType>
{
    /// <summary>Id 0 is reserved for "no building".</summary>
    public BuildingRegistry() : base(1)
    {
    }

    /// <summary>The smallest growth building of a zone.</summary>
    public BuildingType? ForZone(ZoneType zone) => ForZone(zone, 1);

    public BuildingType? ForZone(ZoneType zone, int level) =>
        this.FirstOrDefault(b => b.Zone == zone && b.Level == level);

    /// <summary>The next denser building of the same zone, or null when this one is already the densest.</summary>
    public BuildingType? Upgrade(BuildingType building) =>
        building.Zone == ZoneType.None ? null : ForZone(building.Zone, building.Level + 1);

    public int MaxLevel(ZoneType zone) => this.Where(b => b.Zone == zone).Select(b => b.Level).DefaultIfEmpty(1).Max();

    public IEnumerable<BuildingType> ServiceBuildings(ServiceKind kind) => this.Where(b => b.Service == kind);

    public static BuildingRegistry CreateDefault()
    {
        var registry = new BuildingRegistry();
        registry.Register(new BuildingType
        {
            Name = "House",
            PowerUse = 2,
            WaterUse = 2,
            Glyphs = ["⌂", "⌂", "⌂", "▟"],
            Foreground = Rgb.Hex(0x9dff9d),
            Zone = ZoneType.Residential,
            Capacity = 6,
            Description = "A family home",
        });
        registry.Register(new BuildingType
        {
            Name = "Shop",
            PowerUse = 3,
            WaterUse = 2,
            Glyphs = ["▣", "▦", "▣"],
            Foreground = Rgb.Hex(0x9fd0ff),
            Zone = ZoneType.Commercial,
            Capacity = 6,
            Description = "Stores and offices",
        });
        registry.Register(new BuildingType
        {
            Name = "Factory",
            PowerUse = 6,
            WaterUse = 4,
            Glyphs = ["▤", "▩", "▤"],
            Foreground = Rgb.Hex(0xffe08a),
            Zone = ZoneType.Industrial,
            Capacity = 9,
            Pollution = 2,
            Description = "Workshops and plants",
        });

        registry.Register(new BuildingType
        {
            Name = "Apartments",
            PowerUse = 6,
            WaterUse = 6,
            Glyphs = ["▥", "▥", "▧"],
            Foreground = Rgb.Hex(0x7df59a),
            Zone = ZoneType.Residential,
            Level = 2,
            Capacity = 18,
            Description = "A mid-rise block of flats",
        });
        registry.Register(new BuildingType
        {
            Name = "Office",
            PowerUse = 9,
            WaterUse = 5,
            Glyphs = ["▥", "▧", "▥"],
            Foreground = Rgb.Hex(0x7fbcff),
            Zone = ZoneType.Commercial,
            Level = 2,
            Capacity = 24,
            Description = "Offices and department stores",
        });
        registry.Register(new BuildingType
        {
            Name = "Plant",
            PowerUse = 14,
            WaterUse = 10,
            Glyphs = ["▥", "▨", "▥"],
            Foreground = Rgb.Hex(0xffd060),
            Zone = ZoneType.Industrial,
            Level = 2,
            Capacity = 28,
            Pollution = 4,
            Description = "A large manufacturing plant",
        });

        registry.Register(new BuildingType
        {
            Name = "Tower",
            PowerUse = 14,
            WaterUse = 14,
            Glyphs = ["█", "▐", "█"],
            Foreground = Rgb.Hex(0xb8ffc8),
            Zone = ZoneType.Residential,
            Level = 3,
            Capacity = 48,
            Description = "A high-rise residential tower",
        });
        registry.Register(new BuildingType
        {
            Name = "Skyscraper",
            PowerUse = 28,
            WaterUse = 12,
            Glyphs = ["█", "▐", "▌"],
            Foreground = Rgb.Hex(0xc4e2ff),
            Zone = ZoneType.Commercial,
            Level = 3,
            Capacity = 90,
            Description = "A downtown skyscraper",
        });
        registry.Register(new BuildingType
        {
            Name = "Complex",
            PowerUse = 30,
            WaterUse = 20,
            Glyphs = ["▓", "▒", "▓"],
            Foreground = Rgb.Hex(0xffe9a0),
            Zone = ZoneType.Industrial,
            Level = 3,
            Capacity = 60,
            Pollution = 6,
            Description = "A heavy industrial complex",
        });

        RegisterServices(registry);
        return registry;
    }

    private static void RegisterServices(BuildingRegistry registry)
    {
        registry.Register(Civic("Coal Plant", ServiceKind.Power, "Ψ", 0xff8c5a, cost: 60_000, upkeep: 400,
            capacity: 1_200, pollution: 8, text: "Cheap, plentiful power; fouls the air for a long way around"));
        registry.Register(Civic("Solar Farm", ServiceKind.Power, "☼", 0xffe066, cost: 45_000, upkeep: 90,
            capacity: 300, text: "Clean power, but each farm supplies far less"));
        registry.Register(Civic("Water Pump", ServiceKind.Water, "◍", 0x66c7ff, cost: 22_000, upkeep: 120,
            capacity: 900, water: true, text: "Draws from a lake, river or sea; must stand on the shore"));
        registry.Register(Civic("Water Tower", ServiceKind.Water, "♜", 0x8fd8ff, cost: 12_000, upkeep: 60,
            capacity: 260, text: "A well and tank; works anywhere, supplies little"));

        registry.Register(Civic("Fire Station", ServiceKind.Fire, "♨", 0xff6b4a, cost: 9_000, upkeep: 160,
            radius: 14, text: "Puts out fires and makes them rare; needs road access"));
        registry.Register(Civic("Police Station", ServiceKind.Police, "★", 0x6d8dff, cost: 9_000, upkeep: 160,
            radius: 16, text: "Deters crime around it; needs road access"));
        registry.Register(Civic("Clinic", ServiceKind.Health, "✚", 0xff7aa8, cost: 8_000, upkeep: 130,
            radius: 12, strength: 70, text: "Basic care: lowers mortality and the risk of outbreaks"));
        registry.Register(Civic("Hospital", ServiceKind.Health, "⊕", 0xff4d8d, cost: 40_000, upkeep: 520,
            radius: 24, text: "Wide, strong medical cover; expensive to run"));
        registry.Register(Civic("School", ServiceKind.Education, "✎", 0xc59bff, cost: 8_000, upkeep: 120,
            radius: 12, strength: 70, text: "Educates children; educated workers earn and build more"));
        registry.Register(Civic("University", ServiceKind.Education, "⌘", 0xa56cff, cost: 45_000, upkeep: 560,
            radius: 28, text: "Higher learning with a wide reach"));
        registry.Register(Civic("Park", ServiceKind.Recreation, "♣", 0x4ddf6b, cost: 2_500, upkeep: 25,
            radius: 8, strength: 80, pollution: -2, text: "Green space: land value, happiness, less crime and smog"));
    }

    private static BuildingType Civic(
        string name, ServiceKind kind, string glyph, int color, int cost, int upkeep, int radius = 0, int capacity = 0,
        int strength = 100, int pollution = 0, bool water = false, string text = "") => new()
    {
        Name = name,
        Glyphs = [glyph],
        Foreground = Rgb.Hex(color),
        Cost = cost,
        PlayerPlaceable = true,
        Service = kind,
        Radius = radius,
        Capacity = capacity,
        Strength = strength,
        WeeklyUpkeep = upkeep,
        Pollution = pollution,
        RequiresWaterNearby = water,
        Description = text,
    };
}
