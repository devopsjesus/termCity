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
            choices.Add(new(CityReport.ServiceName(service), () =>
            {
                Game.SetFunding(service, NextFunding(level));
                ShowBudgetMenu();
            }, [$"{level:P0}", "Funding: select to step down by a quarter"]));
        }

        foreach (var zone in Zones.Placeable)
        {
            var z = zone;
            choices.Add(new(TaxName(z), () =>
            {
                double next = Game.Taxes.Get(z) + 0.01;
                Game.SetTax(z, next > 0.2001 ? 0.01 : next);
                ShowBudgetMenu();
            }, [$"{Game.Taxes.Get(z):P0}", "Tax: select to raise a point, wraps at 20%"]));
        }

        choices.Add(new("Borrow", () => { SetMessage(Game.TakeLoan(10_000).Message); ShowBudgetMenu(); },
            [Fmt.Money(10_000), $"From the moneylenders: owed {Fmt.Money(Game.Budget.Loan)}, limit {Fmt.Money(Game.MaxLoan)}"]));
        choices.Add(new("Repay", () => { SetMessage(Game.RepayLoan(10_000).Message); ShowBudgetMenu(); },
            [Fmt.Money(10_000), "Pay back part of the loan"]));
        choices.Add(new("Back", ShowSessionMenu, ["", "Return to the city menu"]));
        ShowPrompt("Treasury, tithes and loans",
            $"Tithes and rents {Fmt.Money(finance.Income)}/wk, services {Fmt.Money(finance.ServiceUpkeep)}, roads {Fmt.Money(finance.RoadUpkeep)}, " +
            $"usury {Fmt.Money(finance.Interest)}, net {Fmt.Money(finance.Net)}/wk. Fuel and water are always paid in full.",
            choices, columns: BudgetMenuColumns);
    }

    private static readonly IReadOnlyList<TableColumn> BudgetMenuColumns = ["ITEM", TableColumn.Right("NOW"), "SELECT TO"];

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
