using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Core.Session;

internal sealed record SimulationPresentationInput(CityGame Snapshot, PathSceneRequest Request);

public sealed partial class GameSession
{
    private sealed record PresentationWork(CityGame Source, SimulationPresentationInput Input, Task<PreparedPathScene> Task);
    private PresentationWork? _presentationWork;
    private (CityGame Source, SimulationPresentationInput Input)? _pendingPresentation;
    public bool BackgroundPresentationPending => _presentationWork is not null || _pendingPresentation is not null;
    public int DiscardedPresentationUpdates { get; private set; }
    public event Action? PresentationChanged;

    private void QueueSimulationPresentation(CityGame source, SimulationPresentationInput input)
    {
        if (_pendingPresentation is not null) DiscardedPresentationUpdates++;
        _pendingPresentation = (source, input);
        PollSimulationPresentation();
    }

    private void PollSimulationPresentation()
    {
        if (_presentationWork is { Task.IsCompleted: true } work)
        {
            _presentationWork = null;
            PreparedPathScene result;
            try { result = work.Task.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                Game.Paused = true;
                throw new BackgroundSimulationException(error);
            }
            if (ReferenceEquals(work.Source, Game) && Game.TryPublishPreparedPaths(result))
            {
                SimulationPathPreparations++;
                PresentationChanged?.Invoke();
            }
            else DiscardedPresentationUpdates++;
        }
        if (_presentationWork is null && _pendingPresentation is { } pending)
        {
            _pendingPresentation = null;
            if (!ReferenceEquals(pending.Source, Game)) { DiscardedPresentationUpdates++; return; }
            var input = pending.Input;
            _presentationWork = new(pending.Source, input, Task.Run(() =>
                PreparedPathScene.Create(input.Snapshot, input.Request)));
        }
    }

    public async Task WaitForPresentationAsync()
    {
        while (BackgroundPresentationPending)
        {
            if (_presentationWork is { } work)
                await Task.WhenAny(work.Task);
            PollSimulationPresentation();
        }
    }

    private async Task StopPresentationForQuitAsync()
    {
        _pendingPresentation = null;
        var work = _presentationWork;
        _presentationWork = null;
        if (work is null) return;
        try { await work.Task; }
        catch (Exception error) { throw new BackgroundSimulationException(error); }
    }
}
