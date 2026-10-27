using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private ActionResult ExecuteDebugSiege(Func<ActionResult> action) => DebugSiegeMode
        ? Execute(action) : Complete(ActionResult.Fail("These controls require Debug Siege mode."));

    public ActionResult DebugStartWave(int swarmSize) => ExecuteDebugSiege(() => Game.DebugStartSiegeWave(swarmSize));
    public ActionResult DebugClearGoblins() => ExecuteDebugSiege(Game.DebugRemoveGoblins);
    public ActionResult DebugRepairBuildings() => ExecuteDebugSiege(Game.DebugRepairAllBuildings);

    public void RequestDebugSiegeRestart(int? wallWidth = null, int? wallHeight = null)
    {
        if (!DebugSiegeMode)
        {
            SetMessage("Restarting a debug siege requires Debug Siege mode.", MessageKind.Error);
            return;
        }
        if (Game.Config.Scenario != CityScenario.Random)
        {
            SetMessage("Debug Siege restart requires a random map, not a populated city scenario.", MessageKind.Error);
            return;
        }
        if (wallWidth is null && wallHeight is null && Game.Settlement is { } current)
        {
            wallWidth = current.Bounds.Width;
            wallHeight = current.Bounds.Height;
        }
        if (SettlementDebugSetup.EnclosureSizeError(Game, wallWidth, wallHeight) is { } sizeError)
        {
            SetMessage(sizeError, MessageKind.Error);
            return;
        }
        GuardProgress("Restart debug siege", () =>
        {
            CityGame fresh;
            Pos target;
            try
            {
                fresh = CityGame.New(Game.Config, Game.Map.Content,
                    startingDate: StartAtCurrentWeek ? DateTime.Now : null);
                target = SettlementDebugSetup.Apply(fresh, siege: true,
                    gateSide: Game.Settlement?.GateSide ?? BaileyGateSide.Bottom, wallWidth: wallWidth, wallHeight: wallHeight);
            }
            catch (InvalidOperationException error)
            {
                SetMessage("Debug Siege restart failed: " + error.Message, MessageKind.Error);
                return;
            }
            ReplaceGame(fresh);
            Cursor = target;
            SetZoom(-1);
            CenterOn(target);
            _initialCameraTarget = target;
            HasCursor = false;
            GuideVisible = false;
            SetMessage($"Debug siege restarted: {fresh.Settlement!.Bounds.Width}x{fresh.Settlement.Bounds.Height} bailey, " +
                "100 residents and the first goblin wave.", MessageKind.Success);
            SiegeWaveStarted?.Invoke();
        });
    }
}
