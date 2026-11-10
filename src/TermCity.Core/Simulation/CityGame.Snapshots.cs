using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Rendering;

namespace TermCity.Core.Simulation;

public sealed partial class CityGame
{
    private PreparedPathScene? _preparedPaths;
    internal int SnapshotRoadVersion => _roadVersion;

    public PreparedPathScene? TakePreparedPaths(CellRect view)
    {
        if (_preparedPaths is not { } paths || paths.View != view ||
            paths.Navigation != Map.NavigationVersion || paths.Paths != Map.FootpathVersion ||
            paths.Terrain != Map.TerrainVersion) return null;
        _preparedPaths = null;
        return paths;
    }

    internal bool TryPublishPreparedPaths(PreparedPathScene paths)
    {
        if (paths.Navigation != Map.NavigationVersion || paths.Paths != Map.FootpathVersion ||
            paths.Terrain != Map.TerrainVersion) return false;
        foreach (var (anchor, yard) in paths.Yards)
        {
            int owner = Map.Index(anchor.X, anchor.Y), cell = Map.Index(yard.Cell.X, yard.Cell.Y);
            if (Map.HouseYardCells.TryGetValue(owner, out int existing) && existing != cell) return false;
            if (Map.HouseYardCells.Any(pair => pair.Key != owner && pair.Value == cell)) return false;
        }
        foreach (var (anchor, yard) in paths.Yards)
            Map.RegisterHouseYard(Map.Index(anchor.X, anchor.Y), Map.Index(yard.Cell.X, yard.Cell.Y));
        _preparedPaths = paths;
        return true;
    }

    internal void PreparePathPresentation(PathSceneRequest request) =>
        _preparedPaths = PreparedPathScene.Create(this, request);

    public Task<PreparedPathScene> PreparePathPresentationAsync(PathSceneRequest request)
    {
        var snapshot = CreateSimulationSnapshot();
        var captured = new PathSceneRequest(request.View, request.Roads.ForkForMap(snapshot.Map));
        return Task.Run(() => PreparedPathScene.Create(snapshot, captured));
    }

    public bool CanSnapshotSimulation => Settlement?.CanSnapshot ?? true;

    public bool DayUpdateDue(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        return elapsedSeconds > 0 && !Paused &&
            _dayProgress + Math.Min(elapsedSeconds, MaxSecondsPerUpdate) >= SecondsPerDay;
    }

    public bool SiegeUpdateDue(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (elapsedSeconds == 0 || Paused || Settlement is not { } settlement) return false;
        if (settlement.WaveNumber == 0) return HighestPopulation >= 100;
        double seconds = Math.Min(elapsedSeconds, MaxSecondsPerUpdate) *
            Config.MediumSecondsPerWeek / Config.SecondsPerWeek(Speed);
        return settlement.Accumulator + seconds + 1e-9 >= SettlementState.StepSeconds;
    }

    public CityGame CreateSimulationSnapshot()
    {
        if (!CanSnapshotSimulation)
            throw new InvalidOperationException("A simulation snapshot cannot capture an active wall transaction.");
        var snapshot = new CityGame(Config, Map.ForkForSimulation(), new GameRandom(Rng.State));
        snapshot.CopySimulationState(this);
        snapshot._preparedPaths = null;
        return snapshot;
    }

    internal void PublishPreparedAnalysis(CityGame source)
    {
        _statsVersion = source._statsVersion;
        _stats = source._stats;
        _serviceVersion = source._serviceVersion;
        _servicesKey = source._servicesKey;
        _servicesInsolvent = source._servicesInsolvent;
        _services = source._services;
        _indicatorsVersion = source._indicatorsVersion;
        _indicators = source._indicators;
        _financeVersion = source._financeVersion;
        _finance = source._finance;
        _profile = source._profile;
        _serviceUnlockDefinitionCount = source._serviceUnlockDefinitionCount;
        Array.Copy(source._serviceUnlocks, _serviceUnlocks, _serviceUnlocks.Length);
        ServiceComputeMilliseconds = source.ServiceComputeMilliseconds;
        if (_network is null || _networkVersion != _roadVersion)
        {
            _network = source._network?.ForkForMap(Map);
            _networkVersion = source._networkVersion;
        }
    }

    internal void PublishSimulationSnapshot(CityGame snapshot, IReadOnlyList<CityEvent> events,
        bool waveStarted = false, bool siegeChanged = false, bool copyMap = true,
        bool? paused = null, GameSpeed? speed = null, PreparedPathScene? preparedPaths = null)
    {
        if (!CanSnapshotSimulation || !snapshot.CanSnapshotSimulation)
            throw new InvalidOperationException("A simulation snapshot cannot publish during an active wall transaction.");
        if (ReferenceEquals(this, snapshot) || Config != snapshot.Config)
            throw new ArgumentException("Publication requires a separate snapshot of the same game.", nameof(snapshot));
        bool notify = copyMap || snapshot.AnalysisVersion != AnalysisVersion || waveStarted || events.Count > 0;
        if (copyMap) Map.CopySnapshot(snapshot.Map);
        CopySimulationState(snapshot, copyNavigation: copyMap);
        if (preparedPaths is not null) _preparedPaths = preparedPaths;
        if (paused is { } retainedPause) Paused = retainedPause;
        if (speed is { } retainedSpeed) Speed = retainedSpeed;
        if (waveStarted) SiegeWaveStarted?.Invoke();
        if (siegeChanged) SiegeChanged?.Invoke();
        foreach (var occurrence in events) EventOccurred?.Invoke(occurrence);
        if (notify) Changed?.Invoke();
    }

    private void CopySimulationState(CityGame source, bool copyNavigation = true)
    {
        bool retainNetwork = _network is not null && _networkVersion == _roadVersion &&
            _roadVersion == source._roadVersion;
        Money = source.Money;
        CityName = source.CityName;
        Week = source.Week;
        Day = source.Day;
        _dayProgress = source._dayProgress;
        Speed = source.Speed;
        Paused = source.Paused;
        ProfileUpdates = source.ProfileUpdates;
        LastReport = source.LastReport;
        HighestMilestone = source.HighestMilestone;
        HighestPopulation = source.HighestPopulation;
        GuideDismissed = source.GuideDismissed;
        LoadedFromSave = source.LoadedFromSave;
        SimulationStarted = source.SimulationStarted;
        OutbreakWeeksLeft = source.OutbreakWeeksLeft;
        GrainWeeks = source.GrainWeeks;
        HarvestQuality = source.HarvestQuality;
        _hunger = source._hunger;
        BuyingGrain = source.BuyingGrain;
        HighestRank = source.HighestRank;
        TributeArrears = source.TributeArrears;
        _planned = source._planned;
        _planHomes = source._planHomes;
        _planShops = source._planShops;
        _planFactories = source._planFactories;
        _weekHomes = source._weekHomes;
        _weekShops = source._weekShops;
        _weekFactories = source._weekFactories;
        _version = source._version;
        _roadVersion = source._roadVersion;
        _networkVersion = source._networkVersion;
        if (!retainNetwork) _network = source._network?.ForkForMap(Map);
        _statsVersion = source._statsVersion;
        _stats = source._stats;
        _serviceVersion = source._serviceVersion;
        _servicesKey = source._servicesKey;
        _servicesInsolvent = source._servicesInsolvent;
        _services = source._services;
        _indicatorsVersion = source._indicatorsVersion;
        _indicators = source._indicators;
        _financeVersion = source._financeVersion;
        _finance = source._finance;
        _profile = source._profile;
        if (copyNavigation || source._preparedPaths is not null) _preparedPaths = source._preparedPaths;
        _serviceUnlockDefinitionCount = source._serviceUnlockDefinitionCount;
        MapVersion = source.MapVersion;
        Rng.State = source.Rng.State;
        Taxes.Residential = source.Taxes.Residential;
        Taxes.Commercial = source.Taxes.Commercial;
        Taxes.Industrial = source.Taxes.Industrial;
        Budget.Restore(source.Budget.Snapshot());
        Budget.Loan = source.Budget.Loan;
        Tally.State = source.Tally.State;
        _events.Clear();
        _events.AddRange(source._events);
        Array.Copy(source._serviceUnlocks, _serviceUnlocks, _serviceUnlocks.Length);
        ServiceComputeMilliseconds = source.ServiceComputeMilliseconds;
        LastSiegeUpdateMilliseconds = source.LastSiegeUpdateMilliseconds;
        LastDayUpdateMilliseconds = source.LastDayUpdateMilliseconds;
        LastPatrolAssignmentMilliseconds = source.LastPatrolAssignmentMilliseconds;
        LastPathWearMilliseconds = source.LastPathWearMilliseconds;
        LastPopulationWeekMilliseconds = source.LastPopulationWeekMilliseconds;
        LastWeekFinishMilliseconds = source.LastWeekFinishMilliseconds;
        if (source.Settlement is { } settlement)
        {
            if (_settlement is null) _settlement = settlement.ForkForSimulation(this);
            else if (copyNavigation) _settlement.CopySnapshot(this, settlement);
            else _settlement.CopyCombatState(this, settlement);
        }
        else _settlement = null;
        Map.BindSnapshotFortifications(_settlement);
    }
}
