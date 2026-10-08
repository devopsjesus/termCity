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

    /// <summary>Cost per complete building when placed by the player.</summary>
    public int Cost { get; init; }

    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;

    /// <summary>ASCII art at two columns and two rows per occupied map cell.</summary>
    public IReadOnlyList<string> FootprintArt { get; init; } = [];

    public bool PlayerPlaceable { get; init; }

    /// <summary>Souls the town needs before the player may raise this (great works are not built for a hamlet).</summary>
    public int MinPopulation { get; init; }

    /// <summary>
    /// Rank of a castle, 0 for everything else: 1 motte and bailey, 2 stone keep, 3 castle. The highest working rank is the
    /// town's seat of power (see <see cref="TermCity.Core.Simulation.CityServices.SeatRank"/>).
    /// </summary>
    public int SeatRank { get; init; }

    /// <summary>Pilgrims this holy place draws to each feast (the quarter days); 0 for buildings that draw none.</summary>
    public int Pilgrims { get; init; }

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

    public string FootprintGlyphAt(CellRect footprint, int x, int y)
    {
        if (footprint.Area == 1 || FootprintArt.Count == 0) return GlyphAt(x, y);
        int column = (x - footprint.X) * 2, row = (y - footprint.Y) * 2;
        return FootprintArt[row].Substring(column, 2) + "\n" + FootprintArt[row + 1].Substring(column, 2);
    }
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
        registry.Register(Civic("Charcoal Burners", ServiceKind.Power, "Ψ", 0xff8c5a, cost: 34_000, upkeep: 100,
            capacity: 1_200, pollution: 8, text: "Cheap, plentiful fuel for hearths and forges; the smoke carries a long way"));
        registry.Register(Civic("Woodlot", ServiceKind.Power, "☼", 0xffe066, cost: 8_000, upkeep: 40,
            capacity: 300, text: "Coppiced wood: clean fuel, but each lot supplies far less"));
        registry.Register(Civic("Aqueduct", ServiceKind.Water, "◍", 0x66c7ff, cost: 14_000, upkeep: 60,
            capacity: 900, water: true, text: "Channels water from a river, mere or the sea; must stand on the shore"));
        registry.Register(Civic("Town Well", ServiceKind.Water, "◎", 0x8fd8ff, cost: 4_000, upkeep: 20,
            capacity: 260, text: "A stone well and trough; works anywhere, supplies little"));

        registry.Register(Civic("Fire Watch", ServiceKind.Fire, "♨", 0xff6b4a, cost: 9_000, upkeep: 160,
            radius: 14, text: "A bucket brigade and watchman: fires are rarer and burn less; needs road access"));
        registry.Register(Civic("Watch House", ServiceKind.Police, "⚑", 0x8fa8ff, cost: 3_500, upkeep: 60,
            radius: 10, strength: 60, text: "A constable and the night watch: a cheap deterrent for a small quarter"));
        registry.Register(Civic("Sheriff's Hall", ServiceKind.Police, "★", 0x6d8dff, cost: 9_000, upkeep: 160,
            radius: 16, text: "The sheriff, his men and a lock-up: deters crime around it; needs road access"));
        registry.Register(Civic("Gaol", ServiceKind.Police, "▦", 0x4f6bd8, cost: 26_000, upkeep: 180,
            radius: 24, minPopulation: 900, text: "Cells, stocks and a gallows: a wide reach against crime, for a large town"));
        registry.Register(Civic("Apothecary", ServiceKind.Health, "✚", 0xff7aa8, cost: 8_000, upkeep: 130,
            radius: 12, strength: 70, text: "Herbs and leeches: lowers mortality and the risk of plague"));
        registry.Register(Civic("Infirmary", ServiceKind.Health, "⊕", 0xff4d8d, cost: 40_000, upkeep: 520,
            radius: 24, minPopulation: 1_000, text: "A monastic infirmary with wide, strong care; expensive to run"));
        registry.Register(Civic("Hospice", ServiceKind.Health, "♡", 0xff93b8, cost: 16_000, upkeep: 210,
            radius: 15, strength: 85, minPopulation: 500, text: "Almshouse beds run by a brotherhood: a middling reach and a gentle price"));
        registry.Register(Civic("Chantry School", ServiceKind.Education, "✎", 0xc59bff, cost: 8_000, upkeep: 120,
            radius: 12, strength: 70, text: "A priest teaches letters and sums; lettered workers earn and build more"));
        registry.Register(Civic("Monastery", ServiceKind.Education, "⌘", 0xa56cff, cost: 45_000, upkeep: 560,
            radius: 28, minPopulation: 1_000, pilgrims: 60, text: "A scriptorium and cloister: learning with a wide reach, and a shrine for pilgrims"));
        registry.Register(Civic("Village Green", ServiceKind.Recreation, "♣", 0x4ddf6b, cost: 2_500, upkeep: 25,
            radius: 8, strength: 80, pollution: -2, text: "Common land and a maypole: land value, happiness, less crime and smoke"));
        registry.Register(Civic("Tavern", ServiceKind.Recreation, "◒", 0xffb347, cost: 6_000, upkeep: 35,
            radius: 11, strength: 90, minPopulation: 100, text: "Ale, songs and gossip: cheers a wide quarter"));

        // The lord's seat: a castle anchors a settlement. It garrisons the surrounding land (defence), lifts land value and
        // the tithe, draws settlers, and a town cannot grow tall without one.
        registry.Register(Civic("Motte and Bailey", ServiceKind.Defence, "♙", 0xd9b48f, cost: 14_000, upkeep: 150,
            radius: 20, strength: 70, seat: 1, text: "An earth mound, palisade and hall: the lord's first seat; a hamlet becomes a manor"));
        registry.Register(Civic("Stone Keep", ServiceKind.Defence, "♜", 0xc8c8d0, cost: 38_000, upkeep: 200,
            radius: 28, strength: 85, seat: 2, minPopulation: 400, text: "A tower of stone and a walled bailey: burghers dare to build taller and richer"));
        registry.Register(Civic("Castle", ServiceKind.Defence, "♚", 0xf2f2f8, cost: 85_000, upkeep: 280,
            radius: 38, strength: 100, seat: 3, minPopulation: 2_500, text: "Curtain walls, towers and a great hall: a royal seat that anchors a whole town"));

        registry.Register(Civic("Chapel", ServiceKind.Faith, "†", 0xfff2b3, cost: 5_000, upkeep: 70,
            radius: 10, strength: 70, pilgrims: 12, text: "A priest and a bell: solace for the faithful and a gentler temper"));
        registry.Register(Civic("Parish Church", ServiceKind.Faith, "‡", 0xffe680, cost: 26_000, upkeep: 130,
            radius: 18, strength: 90, minPopulation: 300, pilgrims: 80, text: "A stone church with a tower: a wide reach for the faith, tithes and holy days"));
        registry.Register(Civic("Cathedral", ServiceKind.Faith, "✙", 0xfff7d6, cost: 110_000, upkeep: 280,
            radius: 34, strength: 100, minPopulation: 4_000, pilgrims: 700, text: "A soaring cathedral that draws pilgrims and wonder; a work of generations"));

        registry.Register(Civic("Market Cross", ServiceKind.Trade, "¤", 0xffd27f, cost: 6_000, upkeep: 60,
            radius: 12, strength: 80, text: "A market place and a toll: traders and shoppers raise land value and market tolls"));
        registry.Register(Civic("Guildhall", ServiceKind.Trade, "§", 0xe0a55c, cost: 28_000, upkeep: 160,
            radius: 22, minPopulation: 800, text: "The guilds' hall: master craftsmen, fair weights and a rich trade across the town"));

        registry.Register(Civic("Granary", ServiceKind.Granary, "▨", 0xe6c97a, cost: 10_000, upkeep: 80,
            capacity: 4_000, text: "A raised store for grain: a reserve against a bad harvest or a long winter"));
    }

    private static BuildingType Civic(
        string name, ServiceKind kind, string glyph, int color, int cost, int upkeep, int radius = 0, int capacity = 0,
        int strength = 100, int pollution = 0, bool water = false, string text = "", int minPopulation = 0, int seat = 0, int pilgrims = 0) => new()
    {
        MinPopulation = minPopulation,
        SeatRank = seat,
        Pilgrims = pilgrims,
        Width = name switch
        {
            "Castle" => 4,
            "Stone Keep" or "Cathedral" or "Monastery" or "Infirmary" => 3,
            "Charcoal Burners" or "Woodlot" or "Aqueduct" or "Fire Watch" or "Sheriff's Hall" or
                "Gaol" or "Hospice" or "Chantry School" or "Village Green" or "Motte and Bailey" or
                "Parish Church" or "Guildhall" or "Granary" => 2,
            _ => 1,
        },
        Height = name switch
        {
            "Castle" or "Cathedral" or "Monastery" => 3,
            "Stone Keep" or "Infirmary" or "Woodlot" or "Gaol" or "Village Green" or "Parish Church" => 2,
            _ => 1,
        },
        Name = name,
        Glyphs = [glyph],
        FootprintArt = name switch
        {
            "Charcoal Burners" => ["~^^~", "[##]"],
            "Woodlot" => ["/\\/\\", "||||", "/\\/\\", "||||"],
            "Aqueduct" => ["====", "()()"],
            "Fire Watch" => ["/^^\\", "[F|]"],
            "Sheriff's Hall" => ["/^^\\", "[S#]"],
            "Gaol" => ["+--+", "|##|", "|##|", "+--+"],
            "Infirmary" => [" /++\\ ", "/____\\", "|+[]+|", "|__A_|"],
            "Hospice" => ["/++\\", "[H|]"],
            "Chantry School" => ["/^^\\", "[=A]"],
            "Monastery" => ["  /\\  ", " /++\\ ", "/____\\", "|[][]|", "|_AA_|", "======"],
            "Village Green" => ["T..T", ".++.", ".++.", "T..T"],
            "Motte and Bailey" => ["/^^\\", "[||]"],
            "Stone Keep" => ["[][][]", "|####|", "|#[]#|", "|_AA_|"],
            "Castle" => ["[] [] []", "|######|", "|#[] []|", "|# /\\ #|", "[##||##]", "========"],
            "Parish Church" => [" /+\\", "/__\\", "|[]|", "|_A|"],
            "Cathedral" => [" /++\\ ", "/|++|\\", "||[]||", "||AA||", "|/AA\\|", "======"],
            "Guildhall" => ["/^^\\", "[G|]"],
            "Granary" => ["/^^\\", "[==]"],
            _ => [],
        },
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
