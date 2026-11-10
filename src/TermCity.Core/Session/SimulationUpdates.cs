using TermCity.Core.Simulation;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public sealed class BackgroundSimulationException(Exception error)
    : InvalidOperationException("Background simulation update failed; the game has been paused.", error);

public sealed partial class GameSession
{
    private readonly record struct SimulationEpoch(int Analysis, int Navigation, int Paths, int Terrain, ulong Random,
        int Week, int Day, double Progress, int Money, int Yards, int Siege);
    private sealed record SimulationWork(CityGame Source, SimulationEpoch Epoch, Task<SimulationResult> Task);
    private readonly Queue<SimulationSlice> _simulationSlices = [];
    private SimulationWork? _simulationWork;
    private SimulationWorker? _simulationWorker;
    private CityGame? _responsiveGame;
    private SimulationEpoch? _publishedEpoch;
    private bool _simulationFailure;
    private bool _stoppingForQuit;
    private (GameMap Map, RoadMeshLayout Roads, CellRect View, int Navigation, int Terrain)? _presentationKey;
    private PathSceneRequest? _capturedPresentation;

    public bool BackgroundSimulationPending => _simulationWork is not null;
    public double PendingSimulationSeconds { get; private set; }
    public double LastBackgroundSimulationMilliseconds { get; private set; }
    public int RetriedSimulationUpdates { get; private set; }
    public int SimulationStateCaptures { get; private set; }
    public int CombatOnlyPublications { get; private set; }
    public int SimulationPathPreparations { get; private set; }
    public double SimulationPublicationMilliseconds { get; private set; }
    public double SimulationCaptureMilliseconds { get; private set; }
    public bool SimulationMapPublished { get; private set; }
    public bool SimulationCompleted { get; private set; }
    public SimulationTimings LastSimulationTimings { get; private set; }

    public void UpdateResponsive(double elapsedSeconds, PathSceneRequest? presentation = null)
    {
        ValidateUpdateSeconds(elapsedSeconds);
        if (_stoppingForQuit) return;
        SimulationPublicationMilliseconds = SimulationCaptureMilliseconds = 0;
        SimulationMapPublished = SimulationCompleted = false;
        PollSimulationPresentation();
        if (!ReferenceEquals(_responsiveGame, Game))
        {
            _simulationSlices.Clear();
            PendingSimulationSeconds = 0;
            _responsiveGame = Game;
            _publishedEpoch = null;
            _presentationKey = null;
            _capturedPresentation = null;
            _simulationFailure = false;
        }
        if (_simulationFailure && Game.Paused)
        {
            FinishUpdate(elapsedSeconds);
            return;
        }
        _simulationFailure = false;
        if (!ClockSuspended && elapsedSeconds > 0)
        {
            double seconds = Math.Min(elapsedSeconds, CityGame.MaxSecondsPerUpdate);
            _simulationSlices.Enqueue(new(seconds, Game.Speed));
            PendingSimulationSeconds += seconds;
        }
        if (_simulationWork is { Task.IsCompleted: true } work)
        {
            _simulationWork = null;
            if (work.Task.IsFaulted)
            {
                _simulationFailure = true;
                _publishedEpoch = null;
                _simulationWorker = null;
                Game.Paused = true;
                throw new BackgroundSimulationException(work.Task.Exception!);
            }
            var result = work.Task.GetAwaiter().GetResult();
            SimulationCompleted = true;
            LastBackgroundSimulationMilliseconds = result.Milliseconds;
            LastSimulationTimings = result.Timings;
            if (ReferenceEquals(work.Source, Game))
            {
                if (work.Epoch == Epoch(Game))
                {
                    bool paused = Game.Paused;
                    var speed = Game.Speed;
                    for (int i = 0; i < result.Processed; i++)
                        PendingSimulationSeconds = Math.Max(0, PendingSimulationSeconds - _simulationSlices.Dequeue().Seconds);
                    if (_simulationSlices.Count == 0) PendingSimulationSeconds = 0;
                    if (result.Processed == 0) Game.PublishPreparedAnalysis(result.State);
                    else
                    {
                        long started = Game.ProfileUpdates ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        Game.PublishSimulationSnapshot(result.State, result.Events, result.WaveStarted, result.SiegeChanged,
                            copyMap: result.MapChanged, paused: paused, speed: result.WaveStarted ? null : speed,
                            preparedPaths: null);
                        if (Game.ProfileUpdates)
                            SimulationPublicationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        SimulationMapPublished = result.MapChanged;
                        if (!result.MapChanged) CombatOnlyPublications++;
                    }
                    if (result.Presentation is { } input) QueueSimulationPresentation(Game, input);
                    _publishedEpoch = Epoch(Game);
                }
                else
                {
                    RetriedSimulationUpdates++;
                    _publishedEpoch = null;
                }
            }
        }
        if (_simulationWork is null && Game.CanSnapshotSimulation &&
            (_simulationSlices.Count > 0 || _publishedEpoch != Epoch(Game)))
        {
            var epoch = Epoch(Game);
            CityGame? reset = null;
            if (_publishedEpoch != epoch)
            {
                long started = Game.ProfileUpdates ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                reset = Game.CreateSimulationSnapshot();
                if (Game.ProfileUpdates)
                    SimulationCaptureMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                SimulationStateCaptures++;
            }
            _simulationWorker ??= new();
            // Preserve every Update boundary and speed; batch delivery, never simulation time.
            var slices = _simulationSlices.Take(16).ToArray();
            var request = CapturePresentationRequest(presentation);
            _simulationWork = new(Game, epoch, _simulationWorker.Advance(reset, slices, request));
        }
        FinishUpdate(elapsedSeconds);
    }

    private PathSceneRequest? CapturePresentationRequest(PathSceneRequest? presentation)
    {
        if (presentation is null) return null;
        var key = (Game.Map, presentation.Roads, presentation.View, Game.Map.NavigationVersion, Game.Map.TerrainVersion);
        if (_presentationKey != key)
        {
            _capturedPresentation = new(presentation.View, presentation.Roads.ForkForMap(Game.Map));
            _presentationKey = key;
        }
        return _capturedPresentation;
    }

    private static SimulationEpoch Epoch(CityGame game) => new(game.AnalysisVersion,
        game.Map.NavigationVersion, game.Map.FootpathVersion, game.Map.TerrainVersion, game.Rng.State,
        game.Week, game.Day, game.DayProgressSeconds, game.Money, game.Map.HouseYardCells.Count,
        game.Settlement?.Revision ?? -1);

    private void SynchronizePendingWaveSpeed()
    {
        if (_simulationSlices.Count == 0) return;
        var pending = _simulationSlices.ToArray();
        _simulationSlices.Clear();
        foreach (var slice in pending) _simulationSlices.Enqueue(slice with { Speed = Game.Speed });
    }

    public async ValueTask StopSimulationAsync()
    {
        _publishedEpoch = null;
        if (_simulationWorker is { } worker)
        {
            _simulationWorker = null;
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void RequestSimulationStop() => _simulationWorker?.Complete();

    public async ValueTask StopForQuitAsync()
    {
        _stoppingForQuit = true;
        Game.Paused = true;
        var worker = _simulationWorker;
        var work = _simulationWork;
        _simulationWorker = null;
        _simulationWork = null;
        _simulationSlices.Clear();
        PendingSimulationSeconds = 0;
        _publishedEpoch = null;
        worker?.StopAfterCurrentSlice();
        try
        {
            await WaitForAutosaveAsync();
            if (worker is not null) await worker.DisposeAsync();
            if (work is not null)
            {
                try { await work.Task; }
                catch (Exception error) { throw new BackgroundSimulationException(error); }
            }
            await StopPresentationForQuitAsync();
        }
        finally { _stoppingForQuit = false; }
    }

    private void SynchronizeSimulation()
    {
        if (!Game.CanSnapshotSimulation)
            throw new InvalidOperationException("Simulation cannot be synchronized during a wall transaction.");
        bool paused = Game.Paused;
        Game.Paused = true;
        try
        {
            while (BackgroundSimulationPending || PendingSimulationSeconds > 0)
            {
                if (_simulationWork is { } work) Task.WaitAny(work.Task);
                UpdateResponsive(0);
            }
        }
        finally
        {
            if (!_simulationFailure) Game.Paused = paused;
        }
    }
}
