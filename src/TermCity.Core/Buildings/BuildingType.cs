using TermCity.Core.Registry;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Buildings;

/// <summary>
/// A structure on the building layer. Growth buildings appear automatically in a zone and come in density
/// <see cref="Level"/>s (cottage, burgage house, tenement); <see cref="PlayerPlaceable"/> civic buildings (fuel, water, fire watch,
/// sheriff, apothecary, schools, commons) are placed by the player, cost gold to build and a little every week to run.
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
    /// Residential: residents the building holds. Commercial/industrial: jobs. Fuel/water: units of supply.
    /// Other services: unused.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>What a filled cell of this building is worth in tax, relative to the smallest building of its zone.</summary>
    public double ValueMultiplier { get; init; } = 1;

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

    /// <summary>Smoke emitted (cells of reach scale with it). Forges and charcoal burners foul the air; village greens clean it.</summary>
    public int Pollution { get; init; }

    /// <summary>Aqueducts must be placed next to open water.</summary>
    public bool RequiresWaterNearby { get; init; }

    /// <summary>Fuel units (firewood, charcoal) a working building draws each week.</summary>
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
            Name = "Cottage",
            PowerUse = 2,
            WaterUse = 2,
            Glyphs = ["⌂", "⌂", "⌂", "▟"],
            Foreground = Rgb.Hex(0x9dff9d),
            Zone = ZoneType.Residential,
            Capacity = 6,
            Description = "A thatched family cottage",
        });
        registry.Register(new BuildingType
        {
            Name = "Market Stall",
            PowerUse = 3,
            WaterUse = 2,
            Glyphs = ["▣", "▦", "▣"],
            Foreground = Rgb.Hex(0x9fd0ff),
            Zone = ZoneType.Commercial,
            Capacity = 14,
            Description = "A trader's stall and storeroom",
        });
        registry.Register(new BuildingType
        {
            Name = "Workshop",
            PowerUse = 6,
            WaterUse = 4,
            Glyphs = ["▤", "▩", "▤"],
            Foreground = Rgb.Hex(0xffe08a),
            Zone = ZoneType.Industrial,
            Capacity = 14,
            Pollution = 2,
            Description = "Smiths, tanners, weavers and coopers",
        });

        registry.Register(new BuildingType
        {
            Name = "Burgage House",
            PowerUse = 6,
            WaterUse = 6,
            Glyphs = ["▥", "▥", "▧"],
            Foreground = Rgb.Hex(0x7df59a),
            Zone = ZoneType.Residential,
            Level = 2,
            Capacity = 18,
            ValueMultiplier = 2.5,
            Description = "A timber-framed townhouse on a narrow burgage plot",
        });
        registry.Register(new BuildingType
        {
            Name = "Merchant House",
            PowerUse = 9,
            WaterUse = 5,
            Glyphs = ["▥", "▧", "▥"],
            Foreground = Rgb.Hex(0x7fbcff),
            Zone = ZoneType.Commercial,
            Level = 2,
            Capacity = 40,
            ValueMultiplier = 2.6,
            Description = "A merchant's shop, counting room and warehouse",
        });
        registry.Register(new BuildingType
        {
            Name = "Mill",
            PowerUse = 14,
            WaterUse = 10,
            Glyphs = ["▥", "▨", "▥"],
            Foreground = Rgb.Hex(0xffd060),
            Zone = ZoneType.Industrial,
            Level = 2,
            Capacity = 40,
            ValueMultiplier = 2.6,
            Pollution = 4,
            Description = "A watermill, fulling mill or brewery",
        });

        registry.Register(new BuildingType
        {
            Name = "Tenement",
            PowerUse = 14,
            WaterUse = 14,
            Glyphs = ["█", "▐", "█"],
            Foreground = Rgb.Hex(0xb8ffc8),
            Zone = ZoneType.Residential,
            Level = 3,
            Capacity = 48,
            ValueMultiplier = 5.5,
            Description = "A tall jettied tenement packed with families",
        });
        registry.Register(new BuildingType
        {
            Name = "Market Hall",
            PowerUse = 28,
            WaterUse = 12,
            Glyphs = ["█", "▐", "▌"],
            Foreground = Rgb.Hex(0xc4e2ff),
            Zone = ZoneType.Commercial,
            Level = 3,
            Capacity = 110,
            ValueMultiplier = 6.0,
            Description = "A great market hall with chambers above",
        });
        registry.Register(new BuildingType
        {
            Name = "Great Forge",
            PowerUse = 30,
            WaterUse = 20,
            Glyphs = ["▓", "▒", "▓"],
            Foreground = Rgb.Hex(0xffe9a0),
            Zone = ZoneType.Industrial,
            Level = 3,
            Capacity = 80,
            ValueMultiplier = 6.0,
            Pollution = 6,
            Description = "Foundry, kilns and forges; smoky and dear",
        });

        RegisterServices(registry);
        RegisterOldNames(registry);
        return registry;
    }

    // Saves from before the medieval setting used these names.
    private static void RegisterOldNames(BuildingRegistry registry)
    {
        foreach (var (old, current) in new[]
        {
            ("House", "Cottage"), ("Shop", "Market Stall"), ("Factory", "Workshop"), ("Apartments", "Burgage House"),
            ("Office", "Merchant House"), ("Plant", "Mill"), ("Tower", "Tenement"), ("Skyscraper", "Market Hall"),
            ("Complex", "Great Forge"), ("Coal Plant", "Charcoal Burners"), ("Solar Farm", "Woodlot"),
            ("Water Pump", "Aqueduct"), ("Water Tower", "Town Well"), ("Fire Station", "Fire Watch"),
            ("Police Station", "Sheriff's Hall"), ("Clinic", "Apothecary"), ("Hospital", "Infirmary"),
            ("School", "Chantry School"), ("University", "Monastery"), ("Park", "Village Green"),
        })
        {
            registry.Alias(old, current);
        }
    }

    private static void RegisterServices(BuildingRegistry registry)
    {
        registry.Register(Civic("Charcoal Burners", ServiceKind.Power, "Ψ", 0xff8c5a, cost: 60_000, upkeep: 400,
            capacity: 1_200, pollution: 8, text: "Cheap, plentiful fuel for hearths and forges; the smoke carries a long way"));
        registry.Register(Civic("Woodlot", ServiceKind.Power, "☼", 0xffe066, cost: 45_000, upkeep: 90,
            capacity: 300, text: "Coppiced wood: clean fuel, but each lot supplies far less"));
        registry.Register(Civic("Aqueduct", ServiceKind.Water, "◍", 0x66c7ff, cost: 22_000, upkeep: 120,
            capacity: 900, water: true, text: "Channels water from a river, mere or the sea; must stand on the shore"));
        registry.Register(Civic("Town Well", ServiceKind.Water, "♜", 0x8fd8ff, cost: 12_000, upkeep: 60,
            capacity: 260, text: "A stone well and trough; works anywhere, supplies little"));

        registry.Register(Civic("Fire Watch", ServiceKind.Fire, "♨", 0xff6b4a, cost: 9_000, upkeep: 160,
            radius: 14, text: "A bucket brigade and watchman: fires are rarer and burn less; needs road access"));
        registry.Register(Civic("Sheriff's Hall", ServiceKind.Police, "★", 0x6d8dff, cost: 9_000, upkeep: 160,
            radius: 16, text: "The sheriff, his men and a lock-up: deters crime around it; needs road access"));
        registry.Register(Civic("Apothecary", ServiceKind.Health, "✚", 0xff7aa8, cost: 8_000, upkeep: 130,
            radius: 12, strength: 70, text: "Herbs and leeches: lowers mortality and the risk of plague"));
        registry.Register(Civic("Infirmary", ServiceKind.Health, "⊕", 0xff4d8d, cost: 40_000, upkeep: 520,
            radius: 24, text: "A monastic infirmary with wide, strong care; expensive to run"));
        registry.Register(Civic("Chantry School", ServiceKind.Education, "✎", 0xc59bff, cost: 8_000, upkeep: 120,
            radius: 12, strength: 70, text: "A priest teaches letters and sums; lettered workers earn and build more"));
        registry.Register(Civic("Monastery", ServiceKind.Education, "⌘", 0xa56cff, cost: 45_000, upkeep: 560,
            radius: 28, text: "A scriptorium and cloister: learning with a wide reach"));
        registry.Register(Civic("Village Green", ServiceKind.Recreation, "♣", 0x4ddf6b, cost: 2_500, upkeep: 25,
            radius: 8, strength: 80, pollution: -2, text: "Common land and a maypole: land value, happiness, less crime and smoke"));
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
