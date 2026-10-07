using TermCity.Core.Buildings;
using System.Text;
using TermCity.Core.Roads;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed record ActionResult(bool Success, string Message, int Cost = 0, int Cells = 0)
{
    public static ActionResult Ok(string message, int cost = 0, int cells = 0) => new(true, message, cost, cells);

    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>What a placement would do: how many cells it affects, what it costs, and how many were skipped as invalid.</summary>
public sealed record Quote(int Cells, int Cost, int Skipped);

public sealed record WeekReport(int Week, int Income, int NewHouseholds, int NewCommercial, int NewIndustrial);

public sealed record GrowthState(bool Planned, int Homes, int Shops, int Factories, int WeekHomes, int WeekShops, int WeekFactories);

public sealed class TaxRates
{
    public double Residential { get; set; } = 0.05;

    public double Commercial { get; set; } = 0.05;

    public double Industrial { get; set; } = 0.05;

    public double Get(ZoneType zone) => zone switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => 0,
    };

    public void Set(ZoneType zone, double rate)
    {
        switch (zone)
        {
            case ZoneType.Residential: Residential = rate; break;
            case ZoneType.Commercial: Commercial = rate; break;
            case ZoneType.Industrial: Industrial = rate; break;
        }
    }
}

/// <summary>
/// The whole simulation: money, calendar, player actions and weekly growth. Has no knowledge of any user interface.
/// Drive it with <see cref="Update"/> (real time) or <see cref="AdvanceWeek"/> (deterministic tests).
/// </summary>
public sealed class CityGame
{
    // Real time is never allowed to jump the simulation forward by more than this in one update, so a stall (the
    // window in the background, a slow frame) does not fast-forward the game when it comes back.
    private const double MaxSecondsPerUpdate = 0.5;

    private double _dayProgress;
    private bool _planned;
    private int _planHomes, _planShops, _planFactories;
    private int _weekHomes, _weekShops, _weekFactories;
    private int _version;
    private int _roadVersion;
    private int _networkVersion = -1;
    private int _statsVersion = -1;
    private RoadNetwork? _network;
    private CityStats? _stats;

    internal CityGame(GameConfig config, GameMap map, GameRandom rng)
    {
        Config = config;
        Map = map;
        Rng = rng;
        Money = config.StartingMoney;
        Taxes = new TaxRates
        {
            Residential = config.DefaultTaxRate,
            Commercial = config.DefaultTaxRate,
            Industrial = config.DefaultTaxRate,
        };
    }

    public static CityGame New(GameConfig config, GameContent? content = null)
    {
        config = config with { StartingYear = config.StartingYear ?? DateTime.Now.Year };
        if (config.Scenario != CityScenario.Random)
            config = config with { MapWidth = MapSize.MaxWidth, MapHeight = MapSize.MaxHeight };
        content ??= new GameContent();
        var map = config.Scenario != CityScenario.Random ? CityScenarioMap.Generate(config, content)
            : MapGenerator.Generate(config.MapWidth, config.MapHeight, config.Seed, content);
        var names = GameRandom.ForStage(config.Seed, "city-name");
        string[] prefixes = ["Oak", "Cedar", "Maple", "Willow", "Pine", "Silver", "Clear", "River"];
        string[] suffixes = ["haven", " Falls", " Ridge", " Creek", "brook", "wood", "view", " Harbor"];
        return new CityGame(config, map, GameRandom.ForStage(config.Seed, "simulation"))
        {
            CityName = config.Scenario != CityScenario.Random ? CityScenarioMap.Name(config.Scenario)
                : prefixes[names.Next(prefixes.Length)] + suffixes[names.Next(suffixes.Length)],
        };
    }

    public GameConfig Config { get; }

    public GameMap Map { get; }

    public GameRandom Rng { get; }

    public int Money { get; internal set; }
    public const int MaxCityNameLength = 16;
    public string CityName { get; internal set; } = "New City";

    public static bool IsValidCityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        int length = 0;
        var remaining = name.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != System.Buffers.OperationStatus.Done ||
                Rune.IsControl(rune) || ++length > MaxCityNameLength) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    public ActionResult RenameCity(string name)
    {
        if (!IsValidCityName(name))
            return ActionResult.Fail("City name must contain 1-16 characters without control characters.");
        name = name.Trim();
        if (CityName != name)
        {
            CityName = name;
            Notify(roadsChanged: false, mapChanged: false);
        }
        return ActionResult.Ok($"City named {CityName}.");
    }

    /// <summary>Weeks elapsed since the game began.</summary>
    public int Week { get; internal set; }

    public GameSpeed Speed { get; set; } = GameSpeed.Medium;

    public bool Paused { get; set; }

    public TaxRates Taxes { get; }

    public WeekReport? LastReport { get; internal set; }

    public int HighestMilestone { get; internal set; }

    public bool GuideDismissed { get; internal set; }

    internal GrowthState GrowthState
    {
        get => new(_planned, _planHomes, _planShops, _planFactories, _weekHomes, _weekShops, _weekFactories);
        set
        {
            _planned = value.Planned;
            _planHomes = value.Homes;
            _planShops = value.Shops;
            _planFactories = value.Factories;
            _weekHomes = value.WeekHomes;
            _weekShops = value.WeekShops;
            _weekFactories = value.WeekFactories;
        }
    }

    /// <summary>The day of the current week, counting from 0.</summary>
    public int Day { get; internal set; }

    /// <summary>Real seconds elapsed in the current day.</summary>
    internal double DayProgressSeconds
    {
        get => _dayProgress;
        set => _dayProgress = value;
    }

    private double SecondsPerDay => Config.SecondsPerWeek(Speed) / Config.DaysPerWeek;

    public double ElapsedDays => (long)Week * Config.DaysPerWeek + Day + Math.Clamp(_dayProgress / SecondsPerDay, 0, 1);

    public int SupportedCells(ZoneType zone)
    {
        int homes = Stats.Residential.Occupied;
        if (zone == ZoneType.Residential)
        {
            return int.MaxValue;
        }

        if (homes < Config.MinResidentialCells)
        {
            return 0;
        }

        return zone switch
        {
            ZoneType.Commercial => CeilDiv(homes, Config.ResidentialPerCommercial),
            ZoneType.Industrial => CeilDiv(homes, Config.ResidentialPerIndustrial),
            _ => 0,
        };
    }

    /// <summary>Fraction (0-1) of the current week that has elapsed.</summary>
    public double WeekProgress => Math.Clamp((Day + Math.Clamp(_dayProgress / SecondsPerDay, 0, 1)) / Config.DaysPerWeek, 0, 1);

    public int Year => Week / Config.WeeksPerYear + (Config.StartingYear ?? 1);

    public int WeekOfYear => Week % Config.WeeksPerYear + 1;

    /// <summary>Raised whenever anything the UI displays may have changed.</summary>
    public event Action? Changed;

    public RoadNetwork Network
    {
        get
        {
            // Connectivity only depends on roads and terrain, so weekly growth never recomputes it.
            if (_network is null || _networkVersion != _roadVersion)
            {
                _network = RoadNetwork.Compute(Map, Config.RoadServiceReach);
                _networkVersion = _roadVersion;
            }

            return _network;
        }
    }

    public CityStats Stats
    {
        get
        {
            if (_stats is null || _statsVersion != _version)
            {
                _stats = ComputeStats();
                _statsVersion = _version;
            }

            return _stats;
        }
    }

    public Demand Demand => Demand.Compute(Stats, Config);

    /// <summary>Increases whenever what the map looks like may have changed (not on every clock tick).</summary>
    public int MapVersion { get; private set; }

    /// <summary>
    /// Invalidates every cache and notifies listeners. Call this after changing the map directly;
    /// the player actions below use <see cref="Notify"/> to invalidate only what they affect.
    /// </summary>
    public void Touch() => Notify(roadsChanged: true);

    private void Notify(bool roadsChanged, bool mapChanged = true)
    {
        _version++;
        if (roadsChanged)
        {
            _roadVersion++;
        }

        if (mapChanged)
        {
            MapVersion++;
        }

        Changed?.Invoke();
    }

    // ---- Time -------------------------------------------------------------------------------------------------

    /// <summary>Advances real time. Days complete according to the current speed; does nothing while paused.</summary>
    public void Update(double elapsedSeconds)
    {
        if (Paused || elapsedSeconds <= 0)
        {
            return;
        }

        _dayProgress += Math.Min(elapsedSeconds, MaxSecondsPerUpdate);
        double length = SecondsPerDay;
        while (_dayProgress >= length)
        {
            _dayProgress -= length;
            AdvanceDay();
        }

        if (RemoveDezonedBuildings(ElapsedDays) > 0)
        {
            Notify(roadsChanged: false);
        }
    }

    /// <summary>Runs the rest of the current week (all seven days, from a fresh week): people move in, then taxes are collected.</summary>
    public WeekReport AdvanceWeek()
    {
        do
        {
            AdvanceDay();
        }
        while (Day != 0);

        return LastReport!;
    }

    /// <summary>
    /// Runs one day. The week's growth is decided on its first day and spread over its days, so the city fills in a
    /// little at a time; on the last day taxes are collected and the week counter moves on.
    /// </summary>
    public void AdvanceDay()
    {
        if (!_planned)
        {
            PlanWeek();
        }

        int day = Day;
        int homes = Grow(ZoneType.Residential, Share(_planHomes, day), int.MaxValue);
        Invalidate();

        int allowedC = SupportedCells(ZoneType.Commercial);
        int shops = Grow(ZoneType.Commercial, Share(_planShops, day), allowedC - Stats.Commercial.Occupied);
        Invalidate();

        int allowedI = SupportedCells(ZoneType.Industrial);
        int factories = Grow(ZoneType.Industrial, Share(_planFactories, day), allowedI - Stats.Industrial.Occupied);
        Invalidate();

        _weekHomes += homes;
        _weekShops += shops;
        _weekFactories += factories;
        Day++;
        int removed = RemoveDezonedBuildings((long)Week * Config.DaysPerWeek + Day);

        if (Day < Config.DaysPerWeek)
        {
            // A day on which nothing moved in changes nothing anyone can see, so it does not wake the user interface
            // (the week bar has its own, cheaper, redraw).
            if (homes + shops + factories + removed > 0)
            {
                Notify(roadsChanged: false);
            }

            return;
        }

        int income = Stats.WeeklyIncome;
        Money += income;
        Week++;
        Day = 0;
        _planned = false;
        LastReport = new WeekReport(Week, income, _weekHomes, _weekShops, _weekFactories);
        _weekHomes = _weekShops = _weekFactories = 0;
        Notify(roadsChanged: false, mapChanged: homes + shops + factories + removed > 0 ||
            LastReport.NewHouseholds + LastReport.NewCommercial + LastReport.NewIndustrial > 0);
    }

    private int RemoveDezonedBuildings(double elapsedDays)
    {
        List<int>? due = null;
        foreach (var (index, removal) in Map.ZoneRemovals)
        {
            if (removal.RemoveAtDay <= elapsedDays)
            {
                (due ??= []).Add(index);
            }
        }

        if (due is null)
        {
            return 0;
        }

        foreach (int i in due)
        {
            var p = Map.PosOf(i);
            Map.SetBuilding(p.X, p.Y, null);
            Map.SetHousehold(p.X, p.Y, default);
        }

        Invalidate();
        return due.Count;
    }

    /// <summary>Decides how many cells may fill this week, from the size of the city as the week begins.</summary>
    private void PlanWeek()
    {
        int filledR = Stats.Residential.Occupied;
        int allowedC = SupportedCells(ZoneType.Commercial);
        int allowedI = SupportedCells(ZoneType.Industrial);

        _planHomes = WeeklyCap(Config.MaxNewResidentialPerWeek, filledR);
        _planShops = WeeklyCap(Config.MaxNewCommercialPerWeek, allowedC);
        _planFactories = WeeklyCap(Config.MaxNewIndustrialPerWeek, allowedI);
        _planned = true;
    }

    /// <summary>The part of a week''s total that falls on one day: the shares of all the days add up to the total.</summary>
    private int Share(int total, int day)
    {
        int days = Config.DaysPerWeek;
        return (int)((long)total * (day + 1) / days - (long)total * day / days);
    }
    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    /// <summary>The most cells that may fill this week: the base cap plus a share of what is already there.</summary>
    internal int WeeklyCap(int baseCap, int existing) =>
        baseCap + (int)Math.Ceiling(Math.Max(0, existing) * Math.Max(0, Config.GrowthRatePerWeek));

    private void Invalidate() => _version++;

    /// <summary>Fills up to <paramref name="limit"/> (and <paramref name="room"/>) served, empty cells of a zone type.</summary>
    private int Grow(ZoneType zone, int limit, int room)
    {
        int count = Math.Min(limit, room);
        var building = Map.Content.Buildings.ForZone(zone);
        if (count <= 0 || building is null)
        {
            return 0;
        }

        var network = Network;
        var candidates = new List<int>();
        foreach (int i in Map.ZoneCells(zone))
        {
            if (Map.BuildingLayer[i] == 0 && network.IsServed(i))
            {
                candidates.Add(i);
            }
        }

        // The zone index has no meaningful order; sorting keeps results identical for a given seed,
        // including after a save is loaded and the index is rebuilt.
        candidates.Sort();

        int filled = 0;
        while (filled < count && candidates.Count > 0)
        {
            int pick = Rng.Next(candidates.Count);
            var p = Map.PosOf(candidates[pick]);
            candidates[pick] = candidates[^1];
            candidates.RemoveAt(candidates.Count - 1);

            Map.SetBuilding(p.X, p.Y, building);
            if (zone == ZoneType.Residential)
            {
                Map.SetHousehold(p.X, p.Y, Household.Random(Rng));
            }

            filled++;
        }

        return filled;
    }

    // ---- Player actions ---------------------------------------------------------------------------------------

    /// <summary>The road type used when none is specified (the smallest and cheapest).</summary>
    public RoadType DefaultRoad => Map.Content.Roads.Default;

    /// <summary>
    /// What it costs to lay a road of the given type on a cell. Upgrading an existing smaller road costs only the
    /// difference between the two types.
    /// </summary>
    public int RoadCostAt(int x, int y, RoadType? type = null)
    {
        type ??= DefaultRoad;
        double terrain = Map.TerrainAt(x, y).BuildCostModifier;
        double existing = Map.RoadTypeAt(x, y)?.CostMultiplier ?? 0;
        return (int)Math.Round(Config.RoadCostPerCell * Math.Max(0, type.CostMultiplier - existing) * terrain);
    }

    public int BuildingCostAt(BuildingType type, int x, int y) => (int)Math.Round(type.Cost * Map.TerrainAt(x, y).BuildCostModifier);

    public bool CanBuildOn(int x, int y) =>
        Map.InBounds(x, y) && Map.TerrainAt(x, y).Buildable && !Map.HasRoad(x, y) &&
        Map.ZoneAt(x, y) == ZoneType.None && Map.BuildingAt(x, y) is null;

    /// <summary>A road can go on open, unbuilt ground, or replace a smaller road type (an upgrade).</summary>
    public bool CanPlaceRoad(int x, int y, RoadType? type = null)
    {
        type ??= DefaultRoad;
        if (!Map.InBounds(x, y) || !Map.TerrainAt(x, y).Buildable || Map.ZoneAt(x, y) != ZoneType.None || Map.BuildingAt(x, y) is not null)
        {
            return false;
        }

        return Map.RoadTypeAt(x, y) is not { } existing || existing.Rank < type.Rank;
    }

    public Quote QuoteRoad(CellRect area, RoadType? type = null) =>
        QuoteCells(area, (x, y) => CanPlaceRoad(x, y, type), (x, y) => RoadCostAt(x, y, type));

    public Quote QuoteBuilding(BuildingType type, CellRect area) =>
        QuoteCells(area, CanBuildOn, (x, y) => BuildingCostAt(type, x, y));

    private Quote QuoteCells(CellRect area, Func<int, int, bool> valid, Func<int, int, int> cost)
    {
        int cells = 0, total = 0, skipped = 0;
        foreach (var p in area.Cells())
        {
            if (!Map.InBounds(p))
            {
                continue;
            }

            if (valid(p.X, p.Y))
            {
                cells++;
                total += cost(p.X, p.Y);
            }
            else
            {
                skipped++;
            }
        }

        return new Quote(cells, total, skipped);
    }

    public ActionResult BuildRoad(CellRect area, RoadType? type = null)
    {
        type ??= DefaultRoad;
        if (!type.PlayerPlaceable)
        {
            return ActionResult.Fail($"{type.Name} cannot be built by the player.");
        }

        var quote = QuoteRoad(area, type);
        var check = CheckSpend(quote, type.Name.ToLowerInvariant());
        if (check is not null)
        {
            return check;
        }

        foreach (var p in area.Cells())
        {
            if (!CanPlaceRoad(p.X, p.Y, type))
            {
                continue;
            }

            Map.SetFeature(p.X, p.Y, null);
            Map.SetRoad(p.X, p.Y, type);
        }

        Money -= quote.Cost;
        Notify(roadsChanged: true);
        return ActionResult.Ok($"Built {quote.Cells} {type.Name.ToLowerInvariant()} cell(s) for {Fmt.Money(quote.Cost)}." + SkippedNote(quote), quote.Cost, quote.Cells);
    }

    public ActionResult PlaceBuilding(BuildingType type, CellRect area)
    {
        if (!type.PlayerPlaceable)
        {
            return ActionResult.Fail($"{type.Name} cannot be placed by the player.");
        }

        var quote = QuoteBuilding(type, area);
        var check = CheckSpend(quote, type.Name.ToLowerInvariant());
        if (check is not null)
        {
            return check;
        }

        foreach (var p in area.Cells())
        {
            if (!CanBuildOn(p.X, p.Y))
            {
                continue;
            }

            Map.SetFeature(p.X, p.Y, null);
            Map.SetBuilding(p.X, p.Y, type);
        }

        Money -= quote.Cost;
        Notify(roadsChanged: false);
        return ActionResult.Ok($"Built {quote.Cells} {type.Name}(s) for {Fmt.Money(quote.Cost)}." + SkippedNote(quote), quote.Cost, quote.Cells);
    }

    private ActionResult? CheckSpend(Quote quote, string what)
    {
        if (Money <= 0)
        {
            return ActionResult.Fail("You are out of money: no more roads or buildings can be placed.");
        }

        if (quote.Cells == 0)
        {
            return ActionResult.Fail($"Nothing to build: no valid cells for a {what} in the selection (water, roads, zones and buildings are skipped).");
        }

        if (quote.Cost > Money)
        {
            return ActionResult.Fail($"Not enough money: {quote.Cells} {what} cell(s) cost {Fmt.Money(quote.Cost)} but you have {Fmt.Money(Money)}.");
        }

        return null;
    }

    private static string SkippedNote(Quote quote) => quote.Skipped > 0 ? $" ({quote.Skipped} blocked cell(s) skipped.)" : string.Empty;

    /// <summary>Designates cells for a zone type. Free. Occupied cells of a different zone type must be demolished first.</summary>
    public ActionResult Designate(CellRect area, ZoneType zone)
    {
        if (zone == ZoneType.None)
        {
            return Dezone(area);
        }

        var info = Zones.Get(zone);
        int changed = 0, skipped = 0;
        foreach (var p in area.Cells())
        {
            if (!Map.InBounds(p))
            {
                continue;
            }

            if (Map.ZoneAt(p.X, p.Y) == zone)
            {
                continue;
            }

            bool restoring = Map.ZoneRemovalAt(p.X, p.Y) is { } removal && removal.Zone == zone;
            bool allowed = Map.TerrainAt(p.X, p.Y).Buildable && !Map.HasRoad(p.X, p.Y) &&
                (Map.BuildingAt(p.X, p.Y) is null || restoring);
            if (!allowed)
            {
                skipped++;
            }
            else
            {
                Map.SetFeature(p.X, p.Y, null);
                Map.SetZone(p.X, p.Y, zone);
                changed++;
            }
        }

        if (changed == 0)
        {
            return ActionResult.Fail($"No cells could be designated {info.Name}: water, roads and buildings are skipped.");
        }

        Notify(roadsChanged: false);
        string note = skipped > 0 ? $" ({skipped} blocked cell(s) skipped.)" : string.Empty;
        return ActionResult.Ok($"Designated {changed} {info.Name} cell(s).{note}", 0, changed);
    }

    /// <summary>Removes zone designations immediately; occupied buildings remain for two to three game weeks.</summary>
    public ActionResult Dezone(CellRect area)
    {
        int changed = 0, scheduled = 0;
        foreach (var p in area.Cells())
        {
            if (!Map.InBounds(p) || Map.ZoneAt(p.X, p.Y) == ZoneType.None)
            {
                continue;
            }

            var zone = Map.ZoneAt(p.X, p.Y);
            Map.SetZone(p.X, p.Y, ZoneType.None);
            if (Map.BuildingAt(p.X, p.Y) is not null)
            {
                int delay = Rng.Next(2 * Config.DaysPerWeek, 3 * Config.DaysPerWeek + 1);
                Map.ZoneRemovals[Map.Index(p.X, p.Y)] = new(zone, ElapsedDays + delay);
                scheduled++;
            }

            changed++;
        }

        if (changed == 0)
        {
            return ActionResult.Fail("No zones to remove here.");
        }

        Notify(roadsChanged: false);
        string note = scheduled > 0 ? $" {scheduled} building(s) will be removed in 2-3 game weeks unless their original zone is restored." : "";
        return ActionResult.Ok($"Dezoned {changed} cell(s).{note}", 0, changed);
    }

    /// <summary>Clears roads, zones, buildings and natural features. Free, no refunds.</summary>
    public ActionResult Demolish(CellRect area)
    {
        int changed = 0;
        bool roadsRemoved = false;
        foreach (var p in area.Cells())
        {
            if (!Map.InBounds(p))
            {
                continue;
            }

            bool hadRoad = Map.HasRoad(p.X, p.Y);
            bool something = hadRoad || Map.ZoneAt(p.X, p.Y) != ZoneType.None ||
                             Map.BuildingAt(p.X, p.Y) is not null || Map.FeatureAt(p.X, p.Y) is not null;
            if (something)
            {
                Map.ClearCell(p.X, p.Y);
                roadsRemoved |= hadRoad;
                changed++;
            }
        }

        if (changed == 0)
        {
            return ActionResult.Fail("Nothing to demolish here.");
        }

        Notify(roadsChanged: roadsRemoved);
        return ActionResult.Ok($"Demolished {changed} cell(s).", 0, changed);
    }

    // ---- Statistics -------------------------------------------------------------------------------------------

    private CityStats ComputeStats()
    {
        var network = Network;
        int adults = 0, children = 0, seniors = 0, households = 0;
        Span<int> zoned = stackalloc int[4];
        Span<int> filled = stackalloc int[4];
        Span<int> served = stackalloc int[4];
        Span<int> awaitingRemoval = stackalloc int[4];

        // Visit only zone indexes and the sparse removal queue, not the whole map.
        foreach (var zone in Zones.Placeable)
        {
            int z = (int)zone;
            foreach (int i in Map.ZoneCells(zone))
            {
                zoned[z]++;
                if (network.IsServed(i))
                {
                    served[z]++;
                }

                if (Map.BuildingLayer[i] != 0)
                {
                    filled[z]++;
                    if (zone == ZoneType.Residential)
                    {
                        var h = Map.HouseholdLayer[i];
                        adults += h.Adults;
                        children += h.Children;
                        seniors += h.Seniors;
                        households++;
                    }
                }
            }
        }

        foreach (var (i, removal) in Map.ZoneRemovals)
        {
            awaitingRemoval[(int)removal.Zone]++;
            if (removal.Zone == ZoneType.Residential)
            {
                var h = Map.HouseholdLayer[i];
                adults += h.Adults;
                children += h.Children;
                seniors += h.Seniors;
                households++;
            }
        }

        double income = 0;
        foreach (var zone in Zones.Placeable)
        {
            income += (filled[(int)zone] + awaitingRemoval[(int)zone]) * Zones.Get(zone).WeeklyValue * Taxes.Get(zone);
        }

        return new CityStats(
            adults + children + seniors, adults, children, seniors, households,
            Count(ZoneType.Residential, zoned, filled, served, awaitingRemoval),
            Count(ZoneType.Commercial, zoned, filled, served, awaitingRemoval),
            Count(ZoneType.Industrial, zoned, filled, served, awaitingRemoval),
            Map.RoadCount, network.ConnectedRoadCount, (int)Math.Round(income, MidpointRounding.AwayFromZero));

        static ZoneCount Count(
            ZoneType zone,
            ReadOnlySpan<int> zoned,
            ReadOnlySpan<int> filled,
            ReadOnlySpan<int> served,
            ReadOnlySpan<int> awaitingRemoval)
        {
            int index = (int)zone;
            return new ZoneCount(zoned[index], filled[index], served[index], awaitingRemoval[index]);
        }
    }
}
