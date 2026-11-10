using System.Numerics;
using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    private Vector2 WallRetreatDirection(WallComponent wall) => wall.IsOriginal || wall.BelongsToFoundingWall
        ? SideOf(wall.Cell) switch
        {
            BaileyGateSide.Top => -Vector2.UnitY,
            BaileyGateSide.Bottom => Vector2.UnitY,
            BaileyGateSide.Left => -Vector2.UnitX,
            _ => Vector2.UnitX,
        }
        : -Rendering.WallDamageLayout.LadderInward(wall);

    private bool MakeOrcAttackSpace(CityGame game, SiegeEnemy enemy, Vector2 target, double clearance,
        Vector2 wallOutward = default)
    {
        double dx = enemy.X - target.X, dy = enemy.Y - target.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance + 1e-6 >= clearance) return true;
        OrcCombatAnimation.Stop(enemy);
        if (distance < 1e-5 && wallOutward != Vector2.Zero) { dx = wallOutward.X; dy = wallOutward.Y; }
        bool centeredX = Math.Abs(enemy.X - Math.Floor(enemy.X) - .5) < 1e-6;
        bool centeredY = Math.Abs(enemy.Y - Math.Floor(enemy.Y) - .5) < 1e-6;
        bool moveX = centeredY && (!centeredX || Math.Abs(dx) > Math.Abs(dy));
        bool align = moveX ? !centeredX && Math.Abs(dy) > Math.Abs(dx) :
            !centeredY && Math.Abs(dx) > Math.Abs(dy);
        double away = moveX ? dx : dy;
        if (align)
        {
            double current = moveX ? enemy.X : enemy.Y;
            double center = Math.Floor(current) + .5;
            var centers = new[] { center, center + (current > center ? 1 : -1) }
                .OrderBy(value => Math.Abs(value - current));
            bool found = false;
            foreach (double goal in centers)
            {
                int samples = Math.Max(1, (int)Math.Ceiling(Math.Abs(goal - current) / .1));
                bool clear = true;
                for (int i = 1; i <= samples; i++)
                {
                    double offset = (goal - current) * i / samples;
                    double px = enemy.X + (moveX ? offset : 0), py = enemy.Y + (moveX ? 0 : offset);
                    if (!CrowdStepIsFree(enemy, px, py) || !CrowdGroundStepClear(game, enemy, px, py, retreating: true))
                    { clear = false; break; }
                }
                if (!clear) continue;
                away = goal - current;
                found = true;
                break;
            }
            if (!found) return false;
        }
        if (Math.Abs(away) < 1e-8)
        {
            // An old save can place a body inside its target wall; use the route's incoming axis.
            int previous = Math.Max(0, enemy.RouteIndex - 1);
            var cell = enemy.Route[previous];
            away = moveX ? enemy.X - cell.X - .5 : enemy.Y - cell.Y - .5;
            away = -away;
        }
        if (Math.Abs(away) < 1e-8) return false;
        int sign = Math.Sign(away);
        var waypoint = enemy.Route[enemy.RouteIndex];
        if (centeredX && centeredY &&
            (moveX ? Math.Abs(enemy.Y - waypoint.Y - .5) : Math.Abs(enemy.X - waypoint.X - .5)) > 1e-6)
            PrependOrcRetreatWaypoint(enemy, new((int)Math.Floor(enemy.X), (int)Math.Floor(enemy.Y)));
        double step = Math.Min(enemy.Definition.MovementSpeed(enemy.Tier) * StepSeconds,
            align ? Math.Abs(away) : clearance - distance + .01);
        double x = enemy.X + (moveX ? sign * step : 0), y = enemy.Y + (moveX ? 0 : sign * step);
        MoveCrowdMember(game, enemy, x, y, retreating: true);

        var next = enemy.Route[enemy.RouteIndex];
        if (Math.Abs(enemy.X - next.X - .5) + Math.Abs(enemy.Y - next.Y - .5) > 1.000001)
        {
            var back = next.Offset(moveX ? sign : 0, moveX ? 0 : sign);
            PrependOrcRetreatWaypoint(enemy, back);
        }
        return false;
    }

    private static void PrependOrcRetreatWaypoint(SiegeEnemy enemy, Pos cell)
    {
        if (enemy.RouteIndex > 0 && enemy.Route[enemy.RouteIndex - 1] == cell) { enemy.RouteIndex--; return; }
        if (enemy.Route.Count >= MaximumRouteLength && enemy.RouteIndex > 0)
        {
            enemy.Route = enemy.Route.Skip(enemy.RouteIndex).ToList();
            enemy.RouteIndex = 0;
        }
        if (enemy.Route.Count >= MaximumRouteLength)
            throw new InvalidOperationException("The orc retreat exceeds the maximum saved route length.");
        enemy.Route.Insert(enemy.RouteIndex, cell);
    }
}
