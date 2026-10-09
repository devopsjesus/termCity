using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermCity.Core.Buildings;
using TermCity.Core.Registry;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Persistence;

/// <summary>
/// JSON save files. Layers are stored as deflate-compressed, base64-encoded byte arrays; terrain, feature and building
/// layers store names via a palette, independently of in-memory registry ordering.
/// </summary>
public static class SaveGameStore
{
    public const int CurrentVersion = 2;
    private const byte NoneMarker = 255;
    private const string Deflate = "deflate";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermCity", "quicksave.json");

    public static void Save(CityGame game, string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temp file first so a crash mid-save cannot corrupt the previous save.
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(game));
        File.Move(temp, path, overwrite: true);
    }

    public static CityGame Load(string path, GameContent? content = null) => Deserialize(File.ReadAllText(path), content);

    public static string Serialize(CityGame game)
    {
        var map = game.Map;
        var data = new SaveData
        {
            Version = CurrentVersion,
            Config = game.Config,
            Money = game.Money,
            CityName = game.CityName,
            Week = game.Week,
            Day = game.Day,
            DayProgressSeconds = game.DayProgressSeconds,
            RngState = game.Rng.State,
            Speed = game.Speed,
            Paused = game.Paused,
            Growth = game.GrowthState,
            Tally = game.Tally.State,
            LastReport = game.LastReport,
            HighestMilestone = game.HighestMilestone,
            GuideDismissed = game.GuideDismissed,
            ZoneRemovals = new(map.ZoneRemovals),
            BuildingFootprints = map.BuildingFootprints.Values.ToList(),
            Compression = Deflate,
            Taxes = game.Taxes,
            Funding = game.Budget.Snapshot(),
            Loan = game.Budget.Loan,
            OutbreakWeeksLeft = game.OutbreakWeeksLeft,
            GrainWeeks = game.GrainWeeks,
            HarvestQuality = game.HarvestQuality,
            Hunger = game.Hunger,
            HighestRank = game.HighestRank,
            TributeArrears = game.TributeArrears,
            Terrain = EncodeLayer(map.TerrainLayer, map.Content.Terrains, noneValue: null),
            Features = EncodeLayer(map.FeatureLayer, map.Content.Features, noneValue: 0),
            Buildings = EncodeLayer(map.BuildingLayer, map.Content.Buildings, noneValue: 0),
            Roads = Pack(EncodeFlags(map.RoadLayer)),
            PlayerRoads = Pack(EncodeFlags(map.PlayerRoadLayer)),
            RoadTypes = EncodeLayer(map.RoadTypeLayer, map.Content.Roads, noneValue: 0),
            Zones = Pack(EncodeZones(map)),
            Households = Pack(EncodeHouseholds(map)),
        };
        return JsonSerializer.Serialize(data, Options);
    }

    public static CityGame Deserialize(string json, GameContent? content = null)
    {
        SaveData data;
        try
        {
            data = JsonSerializer.Deserialize<SaveData>(json, Options) ?? throw new InvalidDataException("Save file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Save file is not valid: " + ex.Message, ex);
        }

        if (data.Version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported save version {data.Version} (expected {CurrentVersion}).");
        }

        if (data.Compression != Deflate)
            throw new InvalidDataException($"Unsupported save compression '{data.Compression}'.");

        content ??= new GameContent();
        var config = data.Config ?? throw new InvalidDataException("Save file has no configuration.");
        ValidateState(data, config);
        var map = new GameMap(config.MapWidth, config.MapHeight, content);
        int count = map.Width * map.Height;

        DecodeLayer(data.Terrain, map.TerrainLayer, content.Terrains, "terrain", allowNone: false);
        DecodeLayer(data.Features, map.FeatureLayer, content.Features, "feature");
        DecodeLayer(data.Buildings, map.BuildingLayer, content.Buildings, "building");
        DecodeLayer(data.RoadTypes, map.RoadTypeLayer, content.Roads, "road type");

        byte[] roads = DecodeBytes(data.Roads, count, "roads");
        byte[] playerRoads = DecodeBytes(data.PlayerRoads, count, "player roads");
        byte[] zones = DecodeBytes(data.Zones, count, "zones");
        byte[] households = DecodeBytes(data.Households, count * 3, "households");
        for (int i = 0; i < count; i++)
        {
            if (roads[i] > 1 || playerRoads[i] > 1 || !Enum.IsDefined((ZoneType)zones[i]) ||
                (roads[i] == 0 && (playerRoads[i] != 0 || map.RoadTypeLayer[i] != 0)) ||
                (roads[i] != 0 && map.RoadTypeLayer[i] == 0))
                throw new InvalidDataException("Invalid road or zone cell.");
            map.RoadLayer[i] = roads[i] != 0;
            map.PlayerRoadLayer[i] = playerRoads[i] != 0;
            map.ZoneLayer[i] = (ZoneType)zones[i];
            map.HouseholdLayer[i] = new Household(households[i * 3], households[i * 3 + 1], households[i * 3 + 2]);
        }

        map.RebuildIndexes();
        var occupied = new HashSet<int>();
        foreach (var area in data.BuildingFootprints)
        {
            if (area.Width <= 0 || area.Height <= 0 || !map.InBounds(area.X, area.Y) ||
                area.Width > map.Width - area.X || area.Height > map.Height - area.Y ||
                map.BuildingAt(area.X, area.Y) is not { PlayerPlaceable: true } building ||
                area.Width != building.Width || area.Height != building.Height)
                throw new InvalidDataException("Invalid building footprint.");
            foreach (var p in area.Cells())
            {
                int index = map.Index(p.X, p.Y);
                if (map.BuildingLayer[index] != building.Id || map.RoadLayer[index] ||
                    map.ZoneLayer[index] != ZoneType.None || !occupied.Add(index))
                    throw new InvalidDataException("Invalid or overlapping building footprint.");
            }
            map.RegisterBuildingFootprint(area);
        }
        foreach (int index in map.ServiceCells)
            if (!occupied.Contains(index))
                throw new InvalidDataException("Service building is missing its footprint.");
        foreach (var (i, removal) in data.ZoneRemovals)
        {
            if (i < 0 || i >= count || removal is null || removal.Zone == ZoneType.None ||
                !Enum.IsDefined(removal.Zone) || !double.IsFinite(removal.RemoveAtDay) || removal.RemoveAtDay < 0 ||
                map.ZoneLayer[i] != ZoneType.None || map.BuildingLayer[i] == 0)
            {
                throw new InvalidDataException("Invalid pending zone removal.");
            }

            map.ZoneRemovals.Add(i, removal);
        }

        var game = new CityGame(config, map, new GameRandom(data.RngState))
        {
            Money = data.Money,
            CityName = data.CityName,
            Week = data.Week,
            Day = data.Day,
            DayProgressSeconds = data.DayProgressSeconds,
            Speed = data.Speed,
            Paused = data.Paused,
            LastReport = data.LastReport,
            HighestMilestone = data.HighestMilestone,
            GuideDismissed = data.GuideDismissed,
        };

        game.GrowthState = data.Growth;
        game.Tally.State = data.Tally;
        game.Taxes.Residential = data.Taxes.Residential;
        game.Taxes.Commercial = data.Taxes.Commercial;
        game.Taxes.Industrial = data.Taxes.Industrial;

        game.Budget.Restore(data.Funding);
        game.Budget.Loan = data.Loan;
        game.OutbreakWeeksLeft = data.OutbreakWeeksLeft;
        game.GrainWeeks = data.GrainWeeks;
        game.HarvestQuality = data.HarvestQuality;
        game.Hunger = data.Hunger;
        game.HighestRank = data.HighestRank;
        game.TributeArrears = data.TributeArrears;
        game.Touch();
        return game;
    }

    private static void ValidateState(SaveData data, GameConfig config)
    {
        if (config.MapWidth is < 8 or > MapSize.MaxWidth || config.MapHeight is < 8 or > MapSize.MaxHeight ||
            config.StartingYear is not > 0 || config.DaysPerWeek <= 0 || config.WeeksPerYear <= 0 ||
            config.ResidentialPerCommercial <= 0 || config.ResidentialPerIndustrial <= 0 ||
            config.AdministrationFullAt <= 2_000 ||
            !Positive(config.SlowSecondsPerWeek) || !Positive(config.MediumSecondsPerWeek) ||
            !Positive(config.FastSecondsPerWeek) || !Enum.IsDefined(config.Scenario) || !Enum.IsDefined(config.Rules))
            throw new InvalidDataException("Invalid save configuration.");
        if (!CityGame.IsValidCityName(data.CityName))
            throw new InvalidDataException("Invalid city name: expected 1-16 characters.");
        if (data.Week < 0 || data.Day < 0 || data.Day >= config.DaysPerWeek ||
            !double.IsFinite(data.DayProgressSeconds) || data.DayProgressSeconds < 0 || !Enum.IsDefined(data.Speed))
            throw new InvalidDataException("Invalid saved calendar or speed.");
        if (data.Growth is null || data.Tally is null || data.Taxes is null ||
            data.BuildingFootprints is null || data.ZoneRemovals is null || data.Funding is null)
            throw new InvalidDataException("Save file has missing city state.");
        if (!InRange(data.Taxes.Residential, 0, 0.3) || !InRange(data.Taxes.Commercial, 0, 0.3) ||
            !InRange(data.Taxes.Industrial, 0, 0.3) || data.Funding.Length != ServiceKinds.Count ||
            data.Funding.Any(level => !InRange(level, 0, 1)) || data.Loan < 0 || data.TributeArrears < 0 ||
            data.OutbreakWeeksLeft is < 0 or > 52 || data.HighestRank is < -1 or > 4 ||
            data.HighestMilestone < 0 || !InRange(data.GrainWeeks, 0, 200) ||
            !InRange(data.HarvestQuality, 0.1, 2) || !InRange(data.Hunger, 0, 1) ||
            data.Tally.Births < 0 || data.Tally.Deaths < 0 || data.Tally.MovedIn < 0 ||
            data.Tally.MovedOut < 0 || data.Tally.Events < 0 ||
            data.Growth.Homes < 0 || data.Growth.Shops < 0 || data.Growth.Factories < 0 ||
            data.Growth.WeekHomes < 0 || data.Growth.WeekShops < 0 || data.Growth.WeekFactories < 0)
            throw new InvalidDataException("Invalid saved city state.");

        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    }

    private static LayerData EncodeLayer<T>(byte[] layer, TypeRegistry<T> registry, int? noneValue) where T : RegisteredType
    {
        var palette = new List<string>(registry.Count);
        Span<byte> paletteIndexes = stackalloc byte[256];
        foreach (var type in registry)
        {
            paletteIndexes[type.Id] = (byte)palette.Count;
            palette.Add(type.Name);
        }
        var bytes = new byte[layer.Length];
        for (int i = 0; i < layer.Length; i++)
        {
            if (noneValue is not null && layer[i] == noneValue)
            {
                bytes[i] = NoneMarker;
            }
            else
            {
                bytes[i] = paletteIndexes[registry[layer[i]].Id];
            }
        }

        return new LayerData { Palette = palette, Data = Pack(bytes) };
    }

    private static void DecodeLayer<T>(LayerData? layer, byte[] target, TypeRegistry<T> registry, string label, bool allowNone = true) where T : RegisteredType
    {
        if (layer is null)
        {
            throw new InvalidDataException($"Save file is missing the {label} layer.");
        }

        byte[] bytes = DecodeBytes(layer.Data, target.Length, label);

        // Resolve each palette name once rather than once per cell.
        if (layer.Palette is null || layer.Palette.Count > (allowNone ? 255 : 256))
            throw new InvalidDataException($"Invalid {label} palette.");
        var ids = new byte[layer.Palette.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            string? name = layer.Palette[i];
            ids[i] = name is not null && registry.Find(name) is { } type
                ? type.Id : throw new InvalidDataException($"Unknown {label} type '{name}'.");
        }
        for (int i = 0; i < bytes.Length; i++)
        {
            if (allowNone && bytes[i] == NoneMarker)
            {
                target[i] = 0;
                continue;
            }

            if (bytes[i] >= ids.Length)
            {
                throw new InvalidDataException($"Corrupt {label} layer.");
            }

            target[i] = ids[bytes[i]];
        }
    }

    private static string Pack(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static byte[] Unpack(string base64, int expectedLength)
    {
        byte[] raw = Convert.FromBase64String(base64);
        // Read at most one byte more than expected so a corrupt or malicious file cannot expand without bound.
        using var input = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        var result = new byte[expectedLength + 1];
        int total = 0, read;
        while (total < result.Length && (read = input.Read(result, total, result.Length - total)) > 0)
        {
            total += read;
        }

        return result.AsSpan(0, total).ToArray();
    }

    private static byte[] EncodeFlags(bool[] flags)
    {
        var bytes = new byte[flags.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = flags[i] ? (byte)1 : (byte)0;
        }

        return bytes;
    }

    private static byte[] EncodeZones(GameMap map)
    {
        var bytes = new byte[map.ZoneLayer.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)map.ZoneLayer[i];
        }

        return bytes;
    }

    private static byte[] EncodeHouseholds(GameMap map)
    {
        var bytes = new byte[map.HouseholdLayer.Length * 3];
        for (int i = 0; i < map.HouseholdLayer.Length; i++)
        {
            var h = map.HouseholdLayer[i];
            bytes[i * 3] = h.Adults;
            bytes[i * 3 + 1] = h.Children;
            bytes[i * 3 + 2] = h.Seniors;
        }

        return bytes;
    }
    private static byte[] DecodeBytes(string? base64, int expectedLength, string label)
    {
        if (base64 is null)
        {
            throw new InvalidDataException($"Save file is missing the {label} layer.");
        }

        byte[] bytes;
        try
        {
            bytes = Unpack(base64, expectedLength);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new InvalidDataException($"Corrupt {label} layer.", ex);
        }

        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException($"The {label} layer has the wrong size.");
        }

        return bytes;
    }

    private sealed class SaveData
    {
        public required int Version { get; set; }

        public required GameConfig Config { get; set; }

        public required int Money { get; set; }
        public required string CityName { get; set; }

        public required int Week { get; set; }

        public required int Day { get; set; }

        public required double DayProgressSeconds { get; set; }

        public required ulong RngState { get; set; }

        public required GameSpeed Speed { get; set; }

        public required bool Paused { get; set; }

        public required GrowthState Growth { get; set; }

        public required WeekTallyState Tally { get; set; }

        public required WeekReport? LastReport { get; set; }

        public required int HighestMilestone { get; set; }

        public required bool GuideDismissed { get; set; }

        public required Dictionary<int, ZoneRemoval> ZoneRemovals { get; set; }
        public required List<CellRect> BuildingFootprints { get; set; }

        public required string Compression { get; set; }

        public required TaxRates Taxes { get; set; }

        public required double[] Funding { get; set; }

        public required int Loan { get; set; }

        public required int OutbreakWeeksLeft { get; set; }

        public required double GrainWeeks { get; set; }

        public required double HarvestQuality { get; set; }

        public required double Hunger { get; set; }

        public required int HighestRank { get; set; }

        public required int TributeArrears { get; set; }

        public required LayerData Terrain { get; set; }

        public required LayerData Features { get; set; }

        public required LayerData Buildings { get; set; }

        public required string Roads { get; set; }

        public required string PlayerRoads { get; set; }

        public required LayerData RoadTypes { get; set; }

        public required string Zones { get; set; }

        public required string Households { get; set; }
    }

    private sealed class LayerData
    {
        public required List<string> Palette { get; set; }

        public required string Data { get; set; }
    }
}
