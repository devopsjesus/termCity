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

public sealed record WeekReport(
    int Week,
    int Income,
    int NewHouseholds,
    int NewCommercial,
    int NewIndustrial,
    int Expenses = 0,
    int Population = 0,
    int Births = 0,
    int Deaths = 0,
    int MovedIn = 0,
    int MovedOut = 0,
    int Events = 0);

/// <summary>What the treasury expects each week: tax in, and what services, roads and the loan cost.</summary>
public sealed record WeeklyFinance(int Income, int ServiceUpkeep, int RoadUpkeep, int Interest, int Administration = 0)
{
    public int Expenses => ServiceUpkeep + RoadUpkeep + Interest + Administration;

    public int Net => Income - Expenses;
}

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
    private int _serviceVersion;
    private int _servicesKey = -1;
    private bool _servicesInsolvent;
    private CityServices? _services;
    private int _indicatorsVersion = -1;
    private CityIndicators? _indicators;
    private int _financeVersion = -1;
    private WeeklyFinance? _finance;
    private CityProfile? _profile;
    private readonly List<CityEvent> _events = [];

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
        RoadSeparation.Apply(map);
        var names = GameRandom.ForStage(config.Seed, "city-name");
        string[] prefixes = ["Oak", "Cedar", "Maple", "Willow", "Pine", "Silver", "Clear", "River"];
        string[] suffixes = ["haven", " Falls", " Ridge", " Creek", "brook", "wood", "view", " Harbor"];
        var game = new CityGame(config, map, GameRandom.ForStage(config.Seed, "simulation"))
        {
            CityName = config.Scenario != CityScenario.Random ? CityScenarioMap.Name(config.Scenario)
                : prefixes[names.Next(prefixes.Length)] + suffixes[names.Next(suffixes.Length)],
        };
        if (config.Scenario != CityScenario.Random && config.FullRules)
        {
            ScenarioSeeder.Seed(game);
        }

        return game;
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
        if (zone == ZoneType.Residential)
        {
            return int.MaxValue;
        }

        // Dense towers support as many shops as the same people spread over small houses would.
        int homes = Config.FullRules
            ? Math.Max(Stats.Households + Stats.Residential.AwaitingRemoval, Stats.Population / 5)
            : Stats.Residential.Occupied;

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

    public Budget Budget { get; } = new();

    public CityProfile Profile => _profile ??= CityProfile.For(Config.Scenario);

    /// <summary>True once the treasury is overdrawn: services run at half strength and nothing can be built.</summary>
    public bool Insolvent => Config.FullRules && Money < 0;

    /// <summary>Weeks left of a disease outbreak (0 when there is none).</summary>
    public int OutbreakWeeksLeft { get; internal set; }

    /// <summary>The season of the farming year (the year starts in midwinter).</summary>
    public Season Season => Seasons.Of(WeekOfYear, Config.WeeksPerYear);

    /// <summary>Weeks of the town's grain in store: the cushion between a poor harvest and starvation.</summary>
    public double GrainWeeks { get; internal set; } = 8;

    /// <summary>This year's crop against an ordinary one (1): below about 0.8 is a poor harvest.</summary>
    public double HarvestQuality { get; internal set; } = 1;

    /// <summary>Share of the town's grain need that went unmet last week, 0-1: above 0 people are going hungry.</summary>
    public double Hunger { get; internal set; }

    internal bool BuyingGrain { get; set; }

    /// <summary>The highest <see cref="TownRank"/> the town has held, so each promotion is announced once (-1 until first assessed).</summary>
    public int HighestRank { get; internal set; } = -1;

    /// <summary>Tribute left unpaid in earlier years, added to the next demand.</summary>
    public int TributeArrears { get; internal set; }

    /// <summary>The town's standing in the realm: see <see cref="Settlement"/>.</summary>
    public TownRank Rank => Config.FullRules ? Settlement.RankOf(this) : TownRank.Hamlet;

    internal WeekTally Tally { get; } = new();

    /// <summary>Coverage of every area service, smog, and power and water supply. Recomputed when the city layout or budget changes.</summary>
    public CityServices Services
    {
        get
        {
            bool insolvent = Insolvent;
            if (_services is null || _servicesKey != _serviceVersion || _servicesInsolvent != insolvent)
            {
                _services = CityServices.Compute(Map, Network, Config, k => Budget.Effective(k, insolvent), Profile.WaterSupply, Profile.PowerDemand);
                _servicesKey = _serviceVersion;
                _servicesInsolvent = insolvent;
            }

            return _services;
        }
    }

    /// <summary>How the city is doing: happiness, jobs, crime, traffic and what is driving people in or out.</summary>
    public CityIndicators Indicators
    {
        get
        {
            if (_indicators is null || _indicatorsVersion != _version)
            {
                _indicators = CityAnalysis.Assess(this);
                _indicatorsVersion = _version;
            }

            return _indicators;
        }
    }

    public WeeklyFinance Finance
    {
        get
        {
            if (_finance is null || _financeVersion != _version)
            {
                _finance = ComputeFinance();
                _financeVersion = _version;
            }

            return _finance;
        }
    }

    /// <summary>The most recent things that happened to the city, oldest first.</summary>
    public IReadOnlyList<CityEvent> Events => _events;

    public event Action<CityEvent>? EventOccurred;

    internal void Report(CityEvent cityEvent)
    {
        _events.Add(cityEvent);
        if (_events.Count > 60)
        {
            _events.RemoveAt(0);
        }

        Tally.Events++;
        EventOccurred?.Invoke(cityEvent);
    }

    private WeeklyFinance ComputeFinance()
    {
        var stats = Stats;
        if (!Config.FullRules)
        {
            return new WeeklyFinance(stats.WeeklyIncome, 0, 0, 0);
        }

        var ind = Indicators;
        double education = 1 + 0.3 * ind.Education / 100;
        double staffing = 0.5 + 0.5 * ind.JobsFilled;
        double income = stats.ResidentialIncome * (0.5 + 0.5 * (1 - ind.Unemployment)) +
            (stats.CommercialIncome + stats.IndustrialIncome) * staffing * education * Settlement.CharterDues(Rank);

        double services = 0;
        foreach (int i in Map.ServiceCells)
        {
            var type = Map.Content.Buildings[Map.BuildingLayer[i]];
            services += type.WeeklyUpkeep * Budget.Funding(type.Service);
        }

        double roads = 0;
        foreach (int i in Map.RoadCells)
        {
            roads += Map.Content.Roads[Map.RoadTypeLayer[i]].WeeklyUpkeep;
        }

        double bureaucracy = Config.AdministrationShare *
            Math.Clamp((stats.Population - 2_000) / (double)(Config.AdministrationFullAt - 2_000), 0, 1);
        return new WeeklyFinance(
            (int)Math.Round(income), (int)Math.Round(services), (int)Math.Round(roads * Budget.Roads),
            (int)Math.Round(Budget.Loan * Budget.LoanInterestPerWeek), (int)Math.Round(income * bureaucracy));
    }

    public void SetTax(ZoneType zone, double rate)
    {
        Taxes.Set(zone, Math.Clamp(double.IsFinite(rate) ? rate : 0.09, 0, 0.3));
        Invalidate();
    }

    /// <summary>Sets how much of its full cost a service is funded (0-1). Lower funding saves money and weakens the service.</summary>
    public void SetFunding(ServiceKind kind, double level)
    {
        if (kind.IsUtility())
        {
            return;
        }

        if (kind == ServiceKind.None)
        {
            Budget.Roads = level;
        }
        else
        {
            Budget.SetFunding(kind, level);
        }

        Notify(roadsChanged: false, mapChanged: false, servicesChanged: true);
    }

    public int MaxLoan => Math.Max(0, Config.FullRules ? Budget.LoanWeeksOfIncome * Finance.Income : 0);

    public ActionResult TakeLoan(int amount)
    {
        if (!Config.FullRules)
        {
            return ActionResult.Fail("Loans are not available in this game.");
        }

        int room = MaxLoan - Budget.Loan;
        if (amount <= 0 || amount > room)
        {
            return ActionResult.Fail(room <= 0
                ? "The bank will lend no more until the city earns more."
                : $"The bank will lend at most {Fmt.Money(room)} more.");
        }

        Budget.Loan += amount;
        Money += amount;
        Notify(roadsChanged: false, mapChanged: false);
        return ActionResult.Ok($"Borrowed {Fmt.Money(amount)}. Interest is {Budget.LoanInterestPerWeek:P1} a week.", amount);
    }

    public ActionResult RepayLoan(int amount)
    {
        amount = Math.Min(amount, Math.Min(Budget.Loan, Math.Max(0, Money)));
        if (amount <= 0)
        {
            return ActionResult.Fail(Budget.Loan == 0 ? "There is no loan to repay." : "There is no spare gold to repay with.");
        }

        Budget.Loan -= amount;
        Money -= amount;
        Notify(roadsChanged: false, mapChanged: false);
        return ActionResult.Ok($"Repaid {Fmt.Money(amount)}.", amount);
    }

    /// <summary>Increases whenever what the map looks like may have changed (not on every clock tick).</summary>
    public int MapVersion { get; private set; }

    /// <summary>
    /// Invalidates every cache and notifies listeners. Call this after changing the map directly;
    /// the player actions below use <see cref="Notify"/> to invalidate only what they affect.
    /// </summary>
    public void Touch() => Notify(roadsChanged: true);

    private void Notify(bool roadsChanged, bool mapChanged = true, bool servicesChanged = false)
    {
        _version++;
        if (roadsChanged)
        {
            _roadVersion++;
        }

        if (roadsChanged || servicesChanged)
        {
            _serviceVersion++;
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
        int homes = Config.FullRules ? MoveIn(Share(_planHomes, day)) : Grow(ZoneType.Residential, Share(_planHomes, day), int.MaxValue);
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

        if (Config.FullRules)
        {
            PopulationEngine.RunWeek(this, Tally);
            Invalidate();
            Settlement.RunWeek(this);
            _serviceVersion++;
        }

        var finance = Finance;
        int income = finance.Income;
        bool wasInsolvent = Insolvent;
        Money += income - finance.Expenses;
        if (Insolvent && !wasInsolvent)
        {
            Report(new CityEvent(Week, EventKind.Finance,
                "The treasury is overdrawn. Services run at half strength and nothing can be built until you are back in credit.", []));
        }

        Week++;
        Day = 0;
        _planned = false;
        LastReport = new WeekReport(
            Week, income, _weekHomes, _weekShops, _weekFactories, finance.Expenses, Stats.Population,
            Tally.Births, Tally.Deaths, Tally.MovedIn, Tally.MovedOut, Tally.Events);
        _weekHomes = _weekShops = _weekFactories = 0;
        Tally.Reset();
        Notify(roadsChanged: false, mapChanged: true, servicesChanged: Config.FullRules);
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
        if (Config.FullRules)
        {
            var ind = Indicators;
            int effective = Math.Max(Stats.Residential.Filled, Stats.Population / 5);
            double families = Config.MaxNewResidentialPerWeek + effective * Config.MigrationRatePerWeek;
            _planHomes = StochasticRound(families * ind.Attraction * Seasons.Travel(Season));
            int allowedC = SupportedCells(ZoneType.Commercial);
            int allowedI = SupportedCells(ZoneType.Industrial);
            _planShops = StochasticRound(WeeklyCap(Config.MaxNewCommercialPerWeek, allowedC) * ind.BusinessClimate);
            _planFactories = StochasticRound(WeeklyCap(Config.MaxNewIndustrialPerWeek, allowedI) * ind.BusinessClimate);
            _planned = true;
            return;
        }

        int filledR = Stats.Residential.Occupied;
        int allowedCells = SupportedCells(ZoneType.Commercial);
        int allowedInd = SupportedCells(ZoneType.Industrial);

        _planHomes = WeeklyCap(Config.MaxNewResidentialPerWeek, filledR);
        _planShops = WeeklyCap(Config.MaxNewCommercialPerWeek, allowedCells);
        _planFactories = WeeklyCap(Config.MaxNewIndustrialPerWeek, allowedInd);
        _planned = true;
    }

    internal int StochasticRound(double value)
    {
        int whole = (int)Math.Floor(Math.Max(0, value));
        return whole + (Rng.Chance(Math.Max(0, value) - whole) ? 1 : 0);
    }

    /// <summary>
    /// Full rules: families move in, first into homes with room (the more desirable the lane, the likelier) and then
    /// into newly built homes. Returns the number of families that arrived.
    /// </summary>
    private int MoveIn(int families)
    {
        if (families <= 0)
        {
            return 0;
        }

        var vacancies = new List<int>();
        foreach (int i in Map.ZoneCells(ZoneType.Residential))
        {
            byte id = Map.BuildingLayer[i];
            if (id != 0 && Map.Content.Buildings[id].Capacity - Map.HouseholdLayer[i].Total >= 3 && CityAnalysis.Operating(this, i))
            {
                vacancies.Add(i);
            }
        }

        vacancies.Sort();
        int moved = 0;
        while (moved < families && vacancies.Count > 0)
        {
            int a = Rng.Next(vacancies.Count), b = Rng.Next(vacancies.Count);
            int pick = CityAnalysis.LandValue(this, vacancies[a]) >= CityAnalysis.LandValue(this, vacancies[b]) ? a : b;
            int cell = vacancies[pick];
            vacancies[pick] = vacancies[^1];
            vacancies.RemoveAt(vacancies.Count - 1);

            var home = Map.HouseholdLayer[cell];
            int room = Map.Content.Buildings[Map.BuildingLayer[cell]].Capacity - home.Total;
            var family = Household.Migrant(Rng);
            if (family.Total > room)
            {
                family = family.Scaled((double)room / family.Total);
            }

            if (family.IsEmpty)
            {
                continue;
            }

            Map.HouseholdLayer[cell] = home.Plus(family);
            Tally.MovedIn += family.Total;
            moved++;
        }

        if (moved < families)
        {
            moved += Grow(ZoneType.Residential, families - moved, int.MaxValue);
        }

        return moved;
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
                var family = Config.FullRules ? Household.Migrant(Rng) : Household.Random(Rng);
                Map.SetHousehold(p.X, p.Y, family);
                Tally.MovedIn += family.Total;
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

    public int BuildingCostAt(BuildingType type, int x, int y) =>
        (int)Math.Round(type.Cost * new CellRect(x, y, type.Width, type.Height).Cells()
            .Average(p => Map.TerrainAt(p.X, p.Y).BuildCostModifier));

    public bool CanBuildOn(int x, int y) =>
        Map.InBounds(x, y) && Map.TerrainAt(x, y).Buildable && !Map.HasRoad(x, y) &&
        Map.ZoneAt(x, y) == ZoneType.None && Map.BuildingAt(x, y) is null;

    /// <summary>A road can go on open, unbuilt ground, or replace a smaller road type (an upgrade); see <see cref="RoadRules"/>.</summary>
    public bool CanPlaceRoad(int x, int y, RoadType? type = null) => PlanRoad([new Pos(x, y)], type).Count == 1;

    /// <summary>The cells of a road stroke that can actually be laid, in order, under the rules of <see cref="RoadRules"/>.</summary>
    public List<Pos> PlanRoad(IEnumerable<Pos> cells, RoadType? type = null) =>
        RoadRules.Plan(Map, cells as IReadOnlyList<Pos> ?? cells.ToList(), (type ?? DefaultRoad).Rank,
            (x, y) => Map.TerrainAt(x, y).Buildable && Map.ZoneAt(x, y) == ZoneType.None && Map.BuildingAt(x, y) is null);

    public Quote QuoteRoad(CellRect area, RoadType? type = null) => QuoteRoad(area.Cells(), type);

    public Quote QuoteRoad(IEnumerable<Pos> cells, RoadType? type = null)
    {
        var area = cells as IReadOnlyCollection<Pos> ?? cells.ToList();
        var plan = PlanRoad(area, type);
        int total = 0;
        foreach (var p in plan)
        {
            total += RoadCostAt(p.X, p.Y, type);
        }

        int inBounds = area.Count(Map.InBounds);
        return new Quote(plan.Count, total, inBounds - plan.Count);
    }

    /// <summary>Open ground, and for a pump, the shore.</summary>
    public bool CanPlaceBuilding(BuildingType type, int x, int y)
    {
        var footprint = new CellRect(x, y, type.Width, type.Height);
        return footprint.Cells().All(p => CanBuildOn(p.X, p.Y)) &&
            (!type.RequiresWaterNearby || footprint.Cells().Any(p => Map.NearWater(p.X, p.Y, 1)));
    }

    public CellRect BuildingPlacementArea(BuildingType type, CellRect area) =>
        new(area.X, area.Y, Math.Max(area.Width, type.Width), Math.Max(area.Height, type.Height));

    public IReadOnlyList<CellRect> PlanBuildings(BuildingType type, CellRect area)
    {
        area = BuildingPlacementArea(type, area);
        var plan = new List<CellRect>();
        for (int y = area.Y; y + type.Height - 1 <= area.Bottom; y += type.Height)
            for (int x = area.X; x + type.Width - 1 <= area.Right; x += type.Width)
                if (CanPlaceBuilding(type, x, y)) plan.Add(new(x, y, type.Width, type.Height));
        return plan;
    }

    public Quote QuoteBuilding(BuildingType type, CellRect area)
    {
        var plan = PlanBuildings(type, area);
        int cells = plan.Sum(p => p.Area);
        return new(cells, plan.Sum(p => BuildingCostAt(type, p.X, p.Y)),
            BuildingPlacementArea(type, area).Cells().Count(Map.InBounds) - cells);
    }

    private Quote QuoteCells(CellRect area, Func<int, int, bool> valid, Func<int, int, int> cost) =>
        QuoteCells(area.Cells(), valid, cost);

    private Quote QuoteCells(IEnumerable<Pos> area, Func<int, int, bool> valid, Func<int, int, int> cost)
    {
        int cells = 0, total = 0, skipped = 0;
        foreach (var p in area)
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

    public ActionResult BuildRoad(CellRect area, RoadType? type = null) => BuildRoad(area.Cells(), type);

    /// <summary>Builds a road over any set of cells, such as a line at an angle.</summary>
    public ActionResult BuildRoad(IEnumerable<Pos> cells, RoadType? type = null)
    {
        var area = cells.ToList();
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

        foreach (var p in PlanRoad(area, type))
        {
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

        if (Stats.Population < type.MinPopulation)
        {
            return ActionResult.Fail($"A {type.Name} needs a town of {type.MinPopulation:N0} souls; you have {Stats.Population:N0}.");
        }

        var quote = QuoteBuilding(type, area);
        var check = CheckSpend(quote, type.Name.ToLowerInvariant());
        if (check is not null)
        {
            return check;
        }

        var plan = PlanBuildings(type, area);
        foreach (var footprint in plan) Map.SetBuildingFootprint(type, footprint);

        Money -= quote.Cost;
        Notify(roadsChanged: false, servicesChanged: true);
        return ActionResult.Ok($"Built {plan.Count} {type.Name}(s), occupying {quote.Cells} cells, for {Fmt.Money(quote.Cost)}." + SkippedNote(quote), quote.Cost, quote.Cells);
    }

    private ActionResult? CheckSpend(Quote quote, string what)
    {
        if (Money <= 0)
        {
            return ActionResult.Fail(Config.FullRules ? "You are out of gold: borrow from the moneylenders or wait for the tithes before building." : "You are out of gold: no more roads or buildings can be placed.");
        }

        if (quote.Cells == 0)
        {
            return ActionResult.Fail($"Nothing to build: no valid cells for a {what} in the selection (water, roads, zones and buildings are skipped).");
        }

        if (quote.Cost > Money)
        {
            return ActionResult.Fail($"Not enough gold: {quote.Cells} {what} cell(s) cost {Fmt.Money(quote.Cost)} but you have {Fmt.Money(Money)}.");
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
    public IReadOnlySet<Pos> DemolitionCells(IEnumerable<Pos> cells)
    {
        var targets = new HashSet<Pos>();
        foreach (var p in cells.Where(Map.InBounds))
            foreach (var cell in Map.BuildingFootprintAt(p.X, p.Y).Cells()) targets.Add(cell);
        return targets;
    }

    public ActionResult Demolish(CellRect area) => Demolish(area.Cells());

    public ActionResult Demolish(IEnumerable<Pos> cells)
    {
        var targets = DemolitionCells(cells);
        int changed = 0;
        bool roadsRemoved = false;
        // Count first: clearing any part of a service building removes its entire footprint.
        foreach (var p in targets)
        {
            bool hadRoad = Map.HasRoad(p.X, p.Y);
            bool something = hadRoad || Map.ZoneAt(p.X, p.Y) != ZoneType.None ||
                             Map.BuildingAt(p.X, p.Y) is not null || Map.FeatureAt(p.X, p.Y) is not null;
            if (something)
            {
                roadsRemoved |= hadRoad;
                changed++;
            }
        }

        if (changed == 0)
        {
            return ActionResult.Fail("Nothing to demolish here.");
        }

        foreach (var p in targets) Map.ClearCell(p.X, p.Y);
        Notify(roadsChanged: roadsRemoved, servicesChanged: true);
        return ActionResult.Ok($"Demolished {changed} cell(s).", 0, changed);
    }

    // ---- Statistics -------------------------------------------------------------------------------------------

    /// <summary>
    /// What a lord and a market add to the tithe a cell pays: land under a castle's garrison pays up to a sixth more, and
    /// market stalls and workshops near a market cross or guildhall pay tolls and dues on a bigger trade.
    /// </summary>
    private double TitheFactor(CityServices services, ZoneType zone, int i)
    {
        if (!Config.FullRules)
        {
            return 1;
        }

        double factor = 1 + 0.15 * services.Coverage(ServiceKind.Defence, i) / 100.0;
        return zone == ZoneType.Residential ? factor : factor * (1 + 0.25 * services.Coverage(ServiceKind.Trade, i) / 100.0);
    }

    private CityStats ComputeStats()
    {
        var network = Network;
        var services = Services;
        int adults = 0, children = 0, seniors = 0, households = 0, vacant = 0;
        Span<int> zoned = stackalloc int[4];
        Span<int> filled = stackalloc int[4];
        Span<int> served = stackalloc int[4];
        Span<int> awaitingRemoval = stackalloc int[4];
        Span<double> value = stackalloc double[4];

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

                byte id = Map.BuildingLayer[i];
                if (id != 0)
                {
                    filled[z]++;
                    if (services.IsPowered(Map, i) && services.IsWatered(Map, i))
                    {
                        value[z] += Map.Content.Buildings[id].ValueMultiplier * TitheFactor(services, zone, i);
                    }

                    if (zone == ZoneType.Residential)
                    {
                        var h = Map.HouseholdLayer[i];
                        adults += h.Adults;
                        children += h.Children;
                        seniors += h.Seniors;
                        if (h.IsEmpty)
                        {
                            vacant++;
                        }
                        else
                        {
                            households++;
                        }
                    }
                }
            }
        }

        foreach (var (i, removal) in Map.ZoneRemovals)
        {
            awaitingRemoval[(int)removal.Zone]++;
            value[(int)removal.Zone] += Map.Content.Buildings[Map.BuildingLayer[i]].ValueMultiplier;
            if (removal.Zone == ZoneType.Residential)
            {
                var h = Map.HouseholdLayer[i];
                adults += h.Adults;
                children += h.Children;
                seniors += h.Seniors;
                households++;
            }
        }

        Span<double> income = stackalloc double[4];
        double total = 0;
        foreach (var zone in Zones.Placeable)
        {
            income[(int)zone] = value[(int)zone] * Zones.Get(zone).WeeklyValue * Taxes.Get(zone);
            total += income[(int)zone];
        }

        return new CityStats(
            adults + children + seniors, adults, children, seniors, households,
            Count(ZoneType.Residential, zoned, filled, served, awaitingRemoval),
            Count(ZoneType.Commercial, zoned, filled, served, awaitingRemoval),
            Count(ZoneType.Industrial, zoned, filled, served, awaitingRemoval),
            Map.RoadCount, network.ConnectedRoadCount, (int)Math.Round(total, MidpointRounding.AwayFromZero),
            vacant, income[(int)ZoneType.Residential], income[(int)ZoneType.Commercial], income[(int)ZoneType.Industrial]);

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
