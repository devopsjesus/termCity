using TermCity.Core.Util;
using System.Numerics;

namespace TermCity.Core.World;

public static class WorldScale
{
    public const float UnitsPerFoot = 0.225f;
    public const float GoblinHeight = 4.5f * UnitsPerFoot;
    public const float HumanHeight = 6 * UnitsPerFoot;
    public const float HumanRadius = 0.28f;
    public const int ArchitectureScale = 5;
    public const float FortificationClearance = 2;
    public const float TowerWidth = 16 * UnitsPerFoot;
    public const float GateWidth = 3.75f;
    public const int TowerSpacing = 15;
    public const float NaturalTreeSpacing = 18 * UnitsPerFoot;

    public static bool HasDryTowerFootprint(GameMap map, Vector2 center)
    {
        float half = TowerWidth / 2;
        int left = (int)MathF.Floor(center.X - half), right = (int)MathF.Floor(center.X + half);
        int top = (int)MathF.Floor(center.Y - half), bottom = (int)MathF.Floor(center.Y + half);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (!map.InBounds(x, y) || !map.TerrainAt(x, y).Buildable) return false;
        return true;
    }

    public static bool TowerPosition(int position, int start, int end) =>
        position == start || position == end ||
        (position - start) % TowerSpacing == 0 &&
        position - start >= MathF.Ceiling(TowerWidth + 0.04f) &&
        end - position >= MathF.Ceiling(TowerWidth + 0.04f);

    public static float Feet(float feet)
    {
        if (!float.IsFinite(feet) || feet < 0) throw new ArgumentOutOfRangeException(nameof(feet));
        return feet * UnitsPerFoot;
    }

    public static (int Original, int Adjusted) GateTower(int start, int end)
    {
        int gate = start + (end - start + 1) / 2;
        int index = Math.Clamp((int)Math.Round((gate - start) / (double)TowerSpacing),
            0, (end - start) / TowerSpacing);
        int original = start + index * TowerSpacing;
        int clearance = (int)Math.Ceiling((GateWidth + TowerWidth) / 2);
        int adjusted = original != start && original != end && Math.Abs(original - gate) < clearance
            ? gate + (original <= gate ? -clearance : clearance) : original;
        return (original, adjusted);
    }

    public static bool FortificationReserved(CellRect bounds, Pos cell)
    {
        bool nearX = cell.X >= bounds.X - FortificationClearance &&
            cell.X <= bounds.Right + FortificationClearance;
        bool nearY = cell.Y >= bounds.Y - FortificationClearance &&
            cell.Y <= bounds.Bottom + FortificationClearance;
        if (nearX && nearY &&
            (Math.Abs(cell.X - bounds.X) <= FortificationClearance ||
             Math.Abs(cell.X - bounds.Right) <= FortificationClearance ||
             Math.Abs(cell.Y - bounds.Y) <= FortificationClearance ||
             Math.Abs(cell.Y - bounds.Bottom) <= FortificationClearance)) return true;
        if (!bounds.Contains(cell)) return false;
        int access = (int)Math.Ceiling(TowerWidth / 2 + HumanRadius);
        bool NearTower(int along, int start, int end)
        {
            int index = Math.Clamp((int)Math.Round((along - start) / (double)TowerSpacing),
                0, (end - start) / TowerSpacing);
            int periodic = start + index * TowerSpacing;
            return TowerPosition(periodic, start, end) && Math.Abs(along - periodic) <= access ||
                Math.Abs(along - end) <= access ||
                Math.Abs(along - GateTower(start, end).Adjusted) <= access;
        }
        return (Math.Abs(cell.X - bounds.X) <= access || Math.Abs(cell.X - bounds.Right) <= access) &&
                NearTower(cell.Y, bounds.Y, bounds.Bottom) ||
            (Math.Abs(cell.Y - bounds.Y) <= access || Math.Abs(cell.Y - bounds.Bottom) <= access) &&
                NearTower(cell.X, bounds.X, bounds.Right);
    }
}
