using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermCity.Core.Registry;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Persistence;

/// <summary>
/// JSON save files. Layers are stored as deflate-compressed, base64-encoded byte arrays; terrain, feature and building
/// layers store names via a palette, so adding or reordering registered types never invalidates old saves.
/// </summary>
public static class SaveGameStore
{
    public const int CurrentVersion = 1;
    private const byte NoneMarker = 255;
    private const string Deflate = "deflate";

    // Saves from before the city rules existed carry no marker and keep playing by the classic rules.
    private const string EngineMarker = "city-rules-2";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new CityScenarioConverter(), new JsonStringEnumConverter() },
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
            LastReport = game.LastReport,
            HighestMilestone = game.HighestMilestone,
            GuideDismissed = game.GuideDismissed,
            ZoneRemovals = new(map.ZoneRemovals),
            BuildingFootprints = map.BuildingFootprints.Values.ToList(),
            Compression = Deflate,
            Taxes = game.Taxes,
            Engine = EngineMarker,
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
            Roads = Pack(EncodeRoads(map)),
            RoadTypes = EncodeLayer(map.RoadTypeLayer, map.Content.Roads, noneValue: 0),
            Zones = Pack(EncodeZones(map)),
            Households = Pack(EncodeHouseholds(map)),
        };
        return JsonSerializer.Serialize(data, Options);
    }

    public static CityGame Deserialize(string json, GameContent? content = null, bool preserveTimings = false)
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

        content ??= new GameContent();
        var config = data.Config ?? throw new InvalidDataException("Save file has no configuration.");
        config = config with { StartingYear = config.StartingYear ?? 1 };
        if (data.Engine is null)
        {
            config = config with { Rules = CityRules.Classic };
        }

        // Game speeds are a property of the game, not of the saved city: a save made with older, faster speeds
        // plays at the current ones.
        var current = new GameConfig();
        if (!preserveTimings)
        {
            config = config with
            {
                SlowSecondsPerWeek = current.SlowSecondsPerWeek,
                MediumSecondsPerWeek = current.MediumSecondsPerWeek,
                FastSecondsPerWeek = current.FastSecondsPerWeek,
                DaysPerWeek = current.DaysPerWeek,
            };
        }
        var map = new GameMap(config.MapWidth, config.MapHeight, content);
        int count = map.Width * map.Height;

        bool layersPacked = data.Compression == Deflate;
        DecodeLayer(data.Terrain, map.TerrainLayer, content.Terrains, "terrain", layersPacked);
        DecodeLayer(data.Features, map.FeatureLayer, content.Features, "feature", layersPacked);
        DecodeLayer(data.Buildings, map.BuildingLayer, content.Buildings, "building", layersPacked);

        bool packed = data.Compression == Deflate;
        byte[] roads = DecodeBytes(data.Roads, count, "roads", packed);
        byte[] zones = DecodeBytes(data.Zones, count, "zones", packed);
        byte[] households = DecodeBytes(data.Households, count * 3, "households", packed);
        for (int i = 0; i < count; i++)
        {
            map.RoadLayer[i] = roads[i] != 0;
            map.ZoneLayer[i] = Enum.IsDefined((ZoneType)zones[i]) ? (ZoneType)zones[i] : ZoneType.None;
            map.HouseholdLayer[i] = new Household(households[i * 3], households[i * 3 + 1], households[i * 3 + 2]);
        }

        DecodeRoadTypes(data.RoadTypes, map, layersPacked);
        map.RebuildIndexes();
        if (data.BuildingFootprints is not null)
        {
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
        }
        if (data.ZoneRemovals is not null)
        {
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
        }

        if (data.CityName is not null && !CityGame.IsValidCityName(data.CityName))
            throw new InvalidDataException("Invalid city name: expected 1-16 characters.");
        var game = new CityGame(config, map, new GameRandom(data.RngState))
        {
            Money = data.Money,
            CityName = data.CityName ?? "New City",
            Week = data.Week,
            Day = Math.Clamp(data.Day, 0, Math.Max(0, config.DaysPerWeek - 1)),
            DayProgressSeconds = data.DayProgressSeconds,
            Speed = data.Speed,
            Paused = data.Paused,
            LastReport = data.LastReport,
            HighestMilestone = data.HighestMilestone,
            GuideDismissed = data.GuideDismissed,
        };

        if (data.Growth is not null)
        {
            game.GrowthState = data.Growth;
        }

        if (data.Taxes is not null)
        {
            game.Taxes.Residential = data.Taxes.Residential;
            game.Taxes.Commercial = data.Taxes.Commercial;
            game.Taxes.Industrial = data.Taxes.Industrial;
        }

        game.Budget.Restore(data.Funding);
        game.Budget.Loan = Math.Max(0, data.Loan);
        game.OutbreakWeeksLeft = Math.Clamp(data.OutbreakWeeksLeft, 0, 52);
        game.GrainWeeks = Math.Clamp(data.GrainWeeks ?? 8, 0, 200);
        game.HarvestQuality = Math.Clamp(data.HarvestQuality ?? 1, 0.1, 2);
        game.Hunger = Math.Clamp(data.Hunger ?? 0, 0, 1);
        game.HighestRank = Math.Clamp(data.HighestRank ?? -1, -1, 4);
        game.TributeArrears = Math.Max(0, data.TributeArrears ?? 0);
        game.Touch();
        return game;
    }

    private static LayerData EncodeLayer<T>(byte[] layer, TypeRegistry<T> registry, int? noneValue) where T : RegisteredType
    {
        var palette = registry.Select(t => t.Name).ToList();
        var bytes = new byte[layer.Length];
        for (int i = 0; i < layer.Length; i++)
        {
            if (noneValue is not null && layer[i] == noneValue)
            {
                bytes[i] = NoneMarker;
            }
            else
            {
                bytes[i] = (byte)palette.IndexOf(registry[layer[i]].Name);
            }
        }

        return new LayerData { Palette = palette, Data = Pack(bytes) };
    }

    private static void DecodeLayer<T>(LayerData? layer, byte[] target, TypeRegistry<T> registry, string label, bool packed) where T : RegisteredType
    {
        if (layer is null)
        {
            throw new InvalidDataException($"Save file is missing the {label} layer.");
        }

        byte[] bytes = DecodeBytes(layer.Data, target.Length, label, packed);

        // Resolve each palette name once rather than once per cell.
        var ids = layer.Palette.Select(name => registry.Find(name)?.Id ?? 0).ToArray();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == NoneMarker)
            {
                target[i] = 0;
                continue;
            }

            if (bytes[i] >= ids.Length)
            {
                throw new InvalidDataException($"Corrupt {label} layer.");
            }

            // Unknown terrain falls back to the base terrain (id 0); unknown features/buildings simply disappear.
            target[i] = ids[bytes[i]];
        }
    }

    /// <summary>Saves from before road types existed have no such layer: every road is then a street.</summary>
    private static void DecodeRoadTypes(LayerData? layer, GameMap map, bool packed)
    {
        if (layer is not null)
        {
            DecodeLayer(layer, map.RoadTypeLayer, map.Content.Roads, "road type", packed);
        }

        byte fallback = map.Content.Roads.Default.Id;
        for (int i = 0; i < map.RoadLayer.Length; i++)
        {
            if (!map.RoadLayer[i])
            {
                map.RoadTypeLayer[i] = 0;
            }
            else if (map.RoadTypeLayer[i] == 0)
            {
                map.RoadTypeLayer[i] = fallback;
            }
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

    private static byte[] Unpack(string base64, bool packed, int expectedLength)
    {
        byte[] raw = Convert.FromBase64String(base64);
        if (!packed)
        {
            return raw;
        }

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

    private static byte[] EncodeRoads(GameMap map)
    {
        var bytes = new byte[map.RoadLayer.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = map.RoadLayer[i] ? (byte)1 : (byte)0;
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
    private static byte[] DecodeBytes(string? base64, int expectedLength, string label, bool packed)
    {
        if (base64 is null)
        {
            throw new InvalidDataException($"Save file is missing the {label} layer.");
        }

        byte[] bytes;
        try
        {
            bytes = Unpack(base64, packed, expectedLength);
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
        public int Version { get; set; }

        public GameConfig? Config { get; set; }

        public int Money { get; set; }
        public string? CityName { get; set; }

        public int Week { get; set; }

        public int Day { get; set; }

        public double DayProgressSeconds { get; set; }

        public ulong RngState { get; set; }

        public GameSpeed Speed { get; set; }

        public bool Paused { get; set; }

        public GrowthState? Growth { get; set; }

        public WeekReport? LastReport { get; set; }

        public int HighestMilestone { get; set; }

        public bool GuideDismissed { get; set; }

        public Dictionary<int, ZoneRemoval>? ZoneRemovals { get; set; }
        public List<CellRect>? BuildingFootprints { get; set; }

        /// <summary>Absent in the earliest saves, which stored raw base64 layers.</summary>
        public string? Compression { get; set; }

        public TaxRates? Taxes { get; set; }

        /// <summary>Absent in saves made before the full city rules.</summary>
        public string? Engine { get; set; }

        public double[]? Funding { get; set; }

        public int Loan { get; set; }

        public int OutbreakWeeksLeft { get; set; }

        /// <summary>Absent in saves made before grain and harvests.</summary>
        public double? GrainWeeks { get; set; }

        public double? HarvestQuality { get; set; }

        public double? Hunger { get; set; }

        public int? HighestRank { get; set; }

        public int? TributeArrears { get; set; }

        public LayerData? Terrain { get; set; }

        public LayerData? Features { get; set; }

        public LayerData? Buildings { get; set; }

        public string? Roads { get; set; }

        public LayerData? RoadTypes { get; set; }

        public string? Zones { get; set; }

        public string? Households { get; set; }
    }

    private sealed class LayerData
    {
        public List<string> Palette { get; set; } = [];

        public string? Data { get; set; }
    }
}
