namespace TermCity.Core.Simulation;

public static class AreaFalloff
{
    public const double Extent = 1.25;
    public const double CellAspect = TermCity.Core.Rendering.RoadCurves.CellHeight /
        (double)TermCity.Core.Rendering.RoadCurves.CellWidth;

    public static double Distance(int dx, int dy) =>
        Math.Sqrt((double)dx * dx + dy * CellAspect * dy * CellAspect);

    public static double Strength(double normalizedDistance)
    {
        if (!double.IsFinite(normalizedDistance) || normalizedDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(normalizedDistance));
        double t = Math.Clamp((normalizedDistance - 1) / (Extent - 1), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }
}
