using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public IEnumerable<WallComponent> WallSections => WallList.Where(w => w.Cell == w.SectionAnchor);

    internal void GroupWallSections(bool legacy = false, bool savedGrouped = false)
    {
        var sides = new[]
        {
            WallList.Where(w => w.Cell.Y == Bounds.Y).OrderBy(w => w.Cell.X),
            WallList.Where(w => w.Cell.Y == Bounds.Bottom).OrderBy(w => w.Cell.X),
            WallList.Where(w => w.Cell.X == Bounds.X && w.Cell.Y > Bounds.Y && w.Cell.Y < Bounds.Bottom).OrderBy(w => w.Cell.Y),
            WallList.Where(w => w.Cell.X == Bounds.Right && w.Cell.Y > Bounds.Y && w.Cell.Y < Bounds.Bottom).OrderBy(w => w.Cell.Y),
        };
        foreach (var side in sides)
        {
            var group = new List<WallComponent>(3);
            void Finish()
            {
                if (group.Count == 0) return;
                var anchor = group[0];
                if (!legacy && group.Any(w => w.Health != anchor.Health || w.MaxHealth != anchor.MaxHealth))
                    throw new InvalidDataException("A shared wall section has inconsistent saved health.");
                double fraction = group.Min(w => w.Health / w.MaxHealth);
                double maximum = savedGrouped ? anchor.MaxHealth : group.Sum(w => w.MaxHealth);
                double health = savedGrouped ? anchor.Health : fraction * maximum;
                var end = group[^1].Cell;
                var bounds = new CellRect(anchor.Cell.X, anchor.Cell.Y,
                    end.X - anchor.Cell.X + 1, end.Y - anchor.Cell.Y + 1);
                foreach (var wall in group)
                {
                    wall.SectionBounds = bounds;
                    if (wall != anchor) wall.SharedSection = anchor;
                }
                anchor.MaxHealth = maximum;
                anchor.Health = health;
                group.Clear();
            }
            foreach (var wall in side)
            {
                if (wall.IsTower || wall.IsGate) { Finish(); continue; }
                group.Add(wall);
                if (group.Count == 3) Finish();
            }
            Finish();
        }
    }

    internal WallComponent? AttackableWall(Goblin goblin)
    {
        var cell = new Pos((int)Math.Floor(goblin.X), (int)Math.Floor(goblin.Y));
        WallComponent? result = null;
        double closest = 1.05 * 1.05;
        for (int y = cell.Y - 1; y <= cell.Y + 1; y++)
            for (int x = cell.X - 1; x <= cell.X + 1; x++)
            {
                var wall = WallAt(new(x, y));
                if (wall is null || wall.Health <= 0) continue;
                double distance = Math.Pow(goblin.X - x - 0.5, 2) + Math.Pow(goblin.Y - y - 0.5, 2);
                if (distance > closest) continue;
                closest = distance;
                result = wall;
            }
        return result;
    }
}
