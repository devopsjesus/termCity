using TermCity.Core.Util;
using TermCity.Core.Rendering;
using System.Numerics;

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
                if (wall.IsTower || wall.IsGate || wall.IsGateWing) { Finish(); continue; }
                group.Add(wall);
                if (group.Count == 3) Finish();
            }
            Finish();
        }
        var gate = WallList.Single(w => w.IsGate);
        var wings = WallList.Where(w => w.IsGateWing).ToArray();
        if (wings.Length > 0)
        {
            double maximum = savedGrouped ? gate.MaxHealth : gate.MaxHealth + wings.Sum(w => w.MaxHealth);
            double fraction = wings.Append(gate).Min(w => w.Health / w.MaxHealth);
            var cells = wings.Append(gate).Select(w => w.Cell).ToArray();
            var bounds = new CellRect(cells.Min(p => p.X), cells.Min(p => p.Y),
                cells.Max(p => p.X) - cells.Min(p => p.X) + 1,
                cells.Max(p => p.Y) - cells.Min(p => p.Y) + 1);
            foreach (var wing in wings)
            {
                wing.SharedSection = gate;
                wing.SectionBounds = bounds;
            }
            gate.SectionBounds = bounds;
            gate.MaxHealth = maximum;
            gate.Health = savedGrouped ? gate.Health : maximum * fraction;
        }
    }

    internal WallComponent? AttackableWall(SiegeEnemy goblin, bool checkCrowd = true)
    {
        WallComponent? result = null;
        double reach = goblin.Kind == SiegeEnemyKind.Orc ? OrcCombatAnimation.WallReach : goblin.CollisionRadius + .35;
        double closest = reach * reach;
        foreach (var wall in WallsNear(new((float)goblin.X, (float)goblin.Y), (float)reach + .05f))
            {
                if (wall.Health <= 0 || wall.IsGateWing) continue;
                var contact = WallAttackPoint(wall, goblin.X, goblin.Y);
                double ax = contact.X, ay = contact.Y;
                double distance = Math.Pow(goblin.X - ax, 2) + Math.Pow(goblin.Y - ay, 2);
                if (distance > closest) continue;
                double vx = ax - goblin.X, vy = ay - goblin.Y, length = vx * vx + vy * vy;
                if (checkCrowd && EnemyList.Any(other =>
                {
                    if (other == goblin || other.Health <= 0 || length < 1e-8) return false;
                    double t = ((other.X - goblin.X) * vx + (other.Y - goblin.Y) * vy) / length;
                    return t > 0 && t < 1 &&
                        Math.Pow(goblin.X + vx * t - other.X, 2) +
                        Math.Pow(goblin.Y + vy * t - other.Y, 2) < other.CollisionRadius * other.CollisionRadius;
                })) continue;
                closest = distance;
                result = wall;
            }
        return result;
    }

    internal static Vector2 WallAttackPoint(WallComponent wall, double x, double y)
    {
        var tangent = wall.Tangent;
        double dx = x - wall.Center.X, dy = y - wall.Center.Y;
        double along = Math.Clamp(dx * tangent.X + dy * tangent.Y, -WallDamageLayout.Width(wall) / 2, WallDamageLayout.Width(wall) / 2);
        double across = Math.Clamp(-dx * tangent.Y + dy * tangent.X, -WallDamageLayout.Depth(wall) / 2, WallDamageLayout.Depth(wall) / 2);
        return new((float)(wall.Center.X + along * tangent.X - across * tangent.Y),
            (float)(wall.Center.Y + along * tangent.Y + across * tangent.X));
    }
}
