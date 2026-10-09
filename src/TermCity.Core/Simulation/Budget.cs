using TermCity.Core.Buildings;

namespace TermCity.Core.Simulation;

/// <summary>
/// What the city chooses to spend: a funding level (0-1) for each area service and for road upkeep, and any loan.
/// Funding scales both the weekly running cost and how well the service works, with diminishing returns:
/// half funding gives about two thirds of the benefit, so trimming a budget is tempting but not free.
/// </summary>
public sealed class Budget
{
    public const double LoanInterestPerWeek = 0.002;

    /// <summary>The most a city may owe, in units of its weekly income (a bank lends against what you earn).</summary>
    public const int LoanWeeksOfIncome = 40;

    private readonly double[] _funding = Enumerable.Repeat(1.0, ServiceKinds.Count).ToArray();

    /// <summary>Funding for road upkeep; the "service" slot of a road is <see cref="ServiceKind.None"/>.</summary>
    public double Roads
    {
        get => _funding[0];
        set => _funding[0] = Clamp(value);
    }

    public int Loan { get; internal set; }

    public double Funding(ServiceKind kind) => kind.IsUtility() ? 1 : _funding[(int)kind];

    public void SetFunding(ServiceKind kind, double level)
    {
        if (kind != ServiceKind.None && !kind.IsUtility())
        {
            _funding[(int)kind] = Clamp(level);
        }
    }

    /// <summary>How much of a service's full benefit it delivers at its funding level; unpaid bills halve it.</summary>
    public double Effective(ServiceKind kind, bool insolvent)
    {
        double funding = Funding(kind);
        double effect = funding <= 0 ? 0 : Math.Pow(funding, 0.6);
        return insolvent && !kind.IsUtility() ? effect * 0.5 : effect;
    }

    public static double Clamp(double value) => Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1);

    internal double[] Snapshot() => (double[])_funding.Clone();

    internal void Restore(IReadOnlyList<double> values)
    {
        if (values.Count != _funding.Length)
            throw new ArgumentException("Funding must contain one value per service kind.", nameof(values));
        for (int i = 0; i < _funding.Length; i++)
        {
            _funding[i] = Clamp(values[i]);
        }
    }
}
