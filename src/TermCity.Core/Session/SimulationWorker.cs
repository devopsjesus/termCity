using System.Diagnostics;
using System.Threading.Channels;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Core.Session;

internal readonly record struct SimulationSlice(double Seconds, GameSpeed Speed);
public readonly record struct SimulationTimings(double Siege, double Day, double Analysis,
    double Navigation, double Capture, double PathWear, double Patrol,
    double BuildingJourneys, double RoadJourneys, double PathLinks, double Population, double WeekFinish,
    double Footbridges, double TravelRepair);
internal sealed record SimulationResult(CityGame State, IReadOnlyList<CityEvent> Events,
    int Processed, double Milliseconds, bool WaveStarted, bool SiegeChanged, bool MapChanged,
    SimulationPresentationInput? Presentation, SimulationTimings Timings);

internal sealed class SimulationWorker : IAsyncDisposable
{
    private sealed record Command(CityGame? Reset, SimulationSlice[] Slices, PathSceneRequest? Presentation,
        TaskCompletionSource<SimulationResult> Completion);
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _loop;
    private int _stopping;

    internal SimulationWorker() => _loop = Task.Run(Run);

    internal Task<SimulationResult> Advance(CityGame? reset, SimulationSlice[] slices, PathSceneRequest? presentation)
    {
        var completion = new TaskCompletionSource<SimulationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new(reset, slices, presentation, completion)))
            throw new InvalidOperationException("The simulation worker has stopped.");
        return completion.Task;
    }

    private async Task Run()
    {
        CityGame? state = null;
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            try
            {
                state = command.Reset ?? state ??
                    throw new InvalidOperationException("The simulation worker requires an initial state.");
                long started = Stopwatch.GetTimestamp();
                int analysis = state.AnalysisVersion, map = state.MapVersion, navigation = state.Map.NavigationVersion;
                int paths = state.Map.FootpathVersion, terrain = state.Map.TerrainVersion, roads = state.SnapshotRoadVersion;
                int week = state.Week, day = state.Day;
                var events = new List<CityEvent>();
                bool waveStarted = false, siegeChanged = false;
                double siegeCost = 0, dayCost = 0, pathCost = 0, patrolCost = 0;
                double buildingJourneys = 0, roadJourneys = 0, pathLinks = 0, population = 0, weekFinish = 0;
                double footbridges = 0, travelRepair = 0;
                Action onWave = () => waveStarted = true;
                Action onSiege = () => siegeChanged = true;
                state.EventOccurred += events.Add;
                state.SiegeWaveStarted += onWave;
                state.SiegeChanged += onSiege;
                int processed = 0;
                SimulationResult result;
                try
                {
                    foreach (var slice in command.Slices)
                    {
                        if (Volatile.Read(ref _stopping) != 0) break;
                        state.Paused = false;
                        state.Speed = slice.Speed;
                        state.Update(slice.Seconds);
                        if (state.ProfileUpdates)
                        {
                            siegeCost += state.LastSiegeUpdateMilliseconds;
                            dayCost += state.LastDayUpdateMilliseconds;
                            pathCost += state.LastPathWearMilliseconds;
                            patrolCost += state.LastPatrolAssignmentMilliseconds;
                            population += state.LastPopulationWeekMilliseconds;
                            weekFinish += state.LastWeekFinishMilliseconds;
                            if (state.LastDayUpdateMilliseconds > 0)
                            {
                                buildingJourneys += state.Map.LastBuildingJourneyMilliseconds;
                                roadJourneys += state.Map.LastRoadJourneyMilliseconds;
                                pathLinks += state.Map.LastPathLinkMilliseconds;
                                footbridges += state.Map.LastFootbridgeMilliseconds;
                                travelRepair += state.Map.LastTravelRepairMilliseconds;
                            }
                        }
                        processed++;
                        if (waveStarted || siegeChanged) break;
                    }
                    bool dayChanged = state.Week != week || state.Day != day;
                    long phase = Stopwatch.GetTimestamp();
                    if (Volatile.Read(ref _stopping) == 0 &&
                        (command.Reset is not null || dayChanged || state.AnalysisVersion != analysis))
                    {
                        _ = state.Stats;
                        _ = state.Services;
                        _ = state.Indicators;
                        _ = state.Finance;
                    }
                    double analysisCost = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    phase = Stopwatch.GetTimestamp();
                    bool navigationChanged = state.Map.NavigationVersion != navigation;
                    if (navigationChanged && Volatile.Read(ref _stopping) == 0) state.Settlement?.PrepareGarrisonNavigation(state);
                    double navigationCost = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    bool pathsChanged = state.MapVersion != map || navigationChanged ||
                        state.Map.FootpathVersion != paths || state.Map.TerrainVersion != terrain;
                    SimulationPresentationInput? prepared = null;
                    phase = Stopwatch.GetTimestamp();
                    if (Volatile.Read(ref _stopping) == 0 && pathsChanged &&
                        command.Presentation is { } request && state.SnapshotRoadVersion == roads)
                        prepared = new(state.CreateSimulationSnapshot(), request);
                    double captureCost = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                    result = new(state, events, processed,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, waveStarted, siegeChanged,
                        pathsChanged || dayChanged, prepared,
                        new(siegeCost, dayCost, analysisCost, navigationCost, captureCost, pathCost, patrolCost,
                            buildingJourneys, roadJourneys, pathLinks, population, weekFinish, footbridges, travelRepair));
                }
                finally
                {
                    state.EventOccurred -= events.Add;
                    state.SiegeWaveStarted -= onWave;
                    state.SiegeChanged -= onSiege;
                }
                command.Completion.SetResult(result);
            }
            catch (Exception error)
            {
                command.Completion.TrySetException(error);
                _commands.Writer.TryComplete(error);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        await _loop.ConfigureAwait(false);
    }

    internal void Complete() => _commands.Writer.TryComplete();
    internal void StopAfterCurrentSlice()
    {
        Interlocked.Exchange(ref _stopping, 1);
        Complete();
    }
}
