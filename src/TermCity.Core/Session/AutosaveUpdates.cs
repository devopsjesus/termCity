using System.Diagnostics;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private sealed record AutosaveResult(double Milliseconds, string? Error);
    private sealed record AutosaveWork(CityGame Source, int Revision, double Days, Task<AutosaveResult> Task);
    private AutosaveWork? _autosaveWork;
    public bool BackgroundAutosavePending => _autosaveWork is not null;
    public double LastAutosaveMilliseconds { get; private set; }
    public double LastAutosaveCaptureMilliseconds { get; private set; }
    public int AutosaveStateCaptures { get; private set; }

    private void QueueAutosave()
    {
        if (_autosaveWork is not null || !Game.CanSnapshotSimulation) return;
        long captured = Game.ProfileUpdates ? Stopwatch.GetTimestamp() : 0;
        var snapshot = Game.CreateSimulationSnapshot();
        LastAutosaveCaptureMilliseconds = Game.ProfileUpdates ? Stopwatch.GetElapsedTime(captured).TotalMilliseconds : 0;
        AutosaveStateCaptures++;
        var paths = Enumerable.Range(1, AutosaveSlots).Select(AutosavePath).ToArray();
        var view = new SavedView(CameraX, CameraY, ZoomLevel) { CameraEdgePadding = CameraEdgePadding };
        var task = Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                WriteAutosave(snapshot, paths, view);
                return new AutosaveResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new AutosaveResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds, error.Message);
            }
        });
        _autosaveWork = new(Game, _revision, Game.ElapsedDays, task);
        _autosaveElapsed = 0;
    }

    private bool PollAutosave(bool wait = false)
    {
        if (_autosaveWork is not { } work || !wait && !work.Task.IsCompleted) return true;
        _autosaveWork = null;
        var result = work.Task.GetAwaiter().GetResult();
        LastAutosaveMilliseconds = result.Milliseconds;
        if (result.Error is { } error)
        {
            SetMessage("Autosave failed: " + error, MessageKind.Error);
            return false;
        }
        if (ReferenceEquals(work.Source, Game))
        {
            _autosaveRevision = work.Revision;
            _autosaveAt = work.Days;
        }
        return true;
    }

    public async Task WaitForAutosaveAsync()
    {
        if (_autosaveWork is { } work) await work.Task;
        PollAutosave();
    }

    private static void WriteAutosave(CityGame game, string[] paths, SavedView view)
    {
        string pending = paths[0] + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            SaveGameStore.Save(game, pending, view);
            for (int slot = paths.Length - 1; slot > 0; slot--)
                if (File.Exists(paths[slot - 1])) File.Copy(paths[slot - 1], paths[slot], overwrite: true);
            File.Move(pending, paths[0], overwrite: true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
            if (File.Exists(pending + ".tmp")) File.Delete(pending + ".tmp");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await WaitForAutosaveAsync();
        await StopSimulationAsync();
        await WaitForPresentationAsync();
    }
}
