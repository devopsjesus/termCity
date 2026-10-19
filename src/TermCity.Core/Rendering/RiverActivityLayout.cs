using System.Numerics;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class RiverActivityLayout
{
    public const int Period = 32;
    public readonly record struct Activity(bool Fish, float Progress, Vector2 Position);

    public static Activity? At(Pos cell, double clock)
    {
        int epoch = (int)Math.Floor(clock / Period);
        int seed = CellHash.Pick(cell.X, cell.Y, 10000);
        if (CellHash.Pick(seed, epoch, 100) < 30) return null;
        bool fish = CellHash.Pick(seed + 1, epoch, 100) < 35;
        double start = 2 + CellHash.Pick(seed + 2, epoch, 2700) / 100.0;
        double age = clock - epoch * Period - start;
        double duration = fish ? 1.8 : 0.9;
        if (age < 0 || age >= duration) return null;
        var position = new Vector2(cell.X + (0.35f + CellHash.Pick(seed + 3, epoch, 31) / 100f),
            cell.Y + (0.35f + CellHash.Pick(seed + 4, epoch, 31) / 100f));
        return new(fish, (float)(age / duration), position);
    }
}
