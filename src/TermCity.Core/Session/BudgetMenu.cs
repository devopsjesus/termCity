using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private static readonly double[] FundingSteps = [1.0, 0.75, 0.5, 0.25, 0.0];

    public void ShowBudgetMenu()
    {
        if (!Game.Config.FullRules)
        {
            ShowPrompt("Treasury", "Classic rules have one flat tithe and no running costs.", [new("Back", ShowSessionMenu)]);
            return;
        }

        var finance = Game.Finance;
        var choices = new List<SessionChoice>();
        foreach (var kind in ServiceKinds.Area.Prepend(ServiceKind.None))
        {
            var service = kind;
            double level = service == ServiceKind.None ? Game.Budget.Roads : Game.Budget.Funding(service);
            choices.Add(new($"{CityReport.ServiceName(service)} funding {level:P0} (select to change)", () =>
            {
                Game.SetFunding(service, NextFunding(level));
                ShowBudgetMenu();
            }));
        }

        foreach (var zone in Zones.Placeable)
        {
            var z = zone;
            choices.Add(new($"{TaxName(z)} {Game.Taxes.Get(z):P0} (select to raise, wraps at 20%)", () =>
            {
                double next = Game.Taxes.Get(z) + 0.01;
                Game.SetTax(z, next > 0.2001 ? 0.01 : next);
                ShowBudgetMenu();
            }));
        }

        choices.Add(new($"Borrow from the moneylenders {Fmt.Money(10_000)} (owed {Fmt.Money(Game.Budget.Loan)}, limit {Fmt.Money(Game.MaxLoan)})",
            () => { SetMessage(Game.TakeLoan(10_000).Message); ShowBudgetMenu(); }));
        choices.Add(new($"Repay {Fmt.Money(10_000)}", () => { SetMessage(Game.RepayLoan(10_000).Message); ShowBudgetMenu(); }));
        choices.Add(new("Back", ShowSessionMenu));
        ShowPrompt("Treasury, tithes and loans",
            $"Tithes and rents {Fmt.Money(finance.Income)}/wk, services {Fmt.Money(finance.ServiceUpkeep)}, roads {Fmt.Money(finance.RoadUpkeep)}, " +
            $"usury {Fmt.Money(finance.Interest)}, net {Fmt.Money(finance.Net)}/wk. Fuel and water are always paid in full.",
            choices);
    }

    /// <summary>What the levy on each kind of plot is called: the rent and tithe of homes, market tolls and guild dues.</summary>
    public static string TaxName(ZoneType zone) => zone switch
    {
        ZoneType.Residential => "Hearth tithe and rents",
        ZoneType.Commercial => "Market tolls",
        ZoneType.Industrial => "Guild dues",
        _ => Zones.Get(zone).Name,
    };

    private static double NextFunding(double current)
    {
        foreach (double step in FundingSteps)
        {
            if (step < current - 0.001)
            {
                return step;
            }
        }

        return 1.0;
    }

    public MapOverlay Overlay { get; private set; }

    public void CycleOverlay()
    {
        Overlay = Game.Config.FullRules ? MapOverlays.Next(Overlay) : MapOverlay.Off;
        SetMessage(Game.Config.FullRules ? MapOverlays.Label(Overlay) : "Overlays need the full rules.");
        Changed?.Invoke();
    }

    public void ShowHealthReport() =>
        ShowPrompt("City health", CityReport.Health(Game), [new("Close", ClosePrompt)]);
}
