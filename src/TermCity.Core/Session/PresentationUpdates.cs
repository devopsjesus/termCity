using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    public SiegePresentation CombatPresentation { get; } = new();
    public SettlementState? PresentedSiege
    {
        get
        {
            if (!CombatPresentation.IsCurrent(Game)) return Game.Settlement;
            if (CombatPresentation.Current?.Revision != Game.Settlement?.Revision)
                CombatPresentation.Advance(Game, 0, !ClockSuspended);
            return CombatPresentation.Current;
        }
    }

    public void AdvancePresentation(double elapsedSeconds, bool focused) =>
        CombatPresentation.Advance(Game, elapsedSeconds, focused && !ClockSuspended);
}
