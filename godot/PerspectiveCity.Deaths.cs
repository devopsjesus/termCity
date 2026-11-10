using Godot;
using TermCity.Core.Simulation;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal int DeathsDrawn { get; private set; }
    private Transform3D? _deathActorTransform;
    private bool _deathCharred;
    private void DrawSiegeDeaths(Dictionary<int, List<Transform3D>> batches, SettlementState siege)
    {
        DeathsDrawn = 0;
        foreach (var death in siege.Deaths)
        {
            float t = SiegeAnimationEnabled ? (float)Math.Clamp(death.Seconds / death.Duration, 0, 1) : .85f;
            float collapse = Mathf.SmoothStep(0, 1, Math.Clamp(t * 1.7f, 0, 1));
            var forward = new Vector3(death.Direction.X, 0, death.Direction.Y);
            var basePoint = new Vector3(death.Position.X, 0, death.Position.Y);
            basePoint.Y = GroundLevel(basePoint) + death.Elevation;
            float distance = death.Cause == SiegeDeathCause.Crushing ? .7f : death.Cause == SiegeDeathCause.Arrow ? .3f : .15f;
            var at = basePoint + forward * (collapse * distance);
            var basis = Basis.LookingAt(forward, Vector3.Up) *
                new Basis(Vector3.Right, collapse * Mathf.Pi / 2);
            if (death.Cause == SiegeDeathCause.Fall) at.Y -= death.Elevation * collapse;
            if (!death.Friendly)
            {
                if (death.Enemy == SiegeEnemyKind.Orc)
                {
                    DrawOrc(batches, basePoint, new(death.Position, death.Direction, 0, 0, 0, 0),
                        death.EnemyAppearance, death, death.Seconds + siege.InterpolationFraction * .1);
                    DeathsDrawn++;
                    continue;
                }
                var facing = Basis.LookingAt(forward, Vector3.Up);
                var fallBasis = facing * new Basis(Vector3.Right, collapse * Mathf.Pi / 2) * facing.Inverse();
                if (death.Cause == SiegeDeathCause.Crushing)
                    fallBasis *= Basis.Identity.Scaled(new Vector3(1, Mathf.Lerp(1, .5f, collapse), 1));
                float settle = death.Cause == SiegeDeathCause.Crushing ? .20f : .15f;
                var pivot = new Vector3(0, death.Scale * settle, 0);
                _deathActorTransform = new Transform3D(fallBasis, at + pivot - fallBasis * (basePoint + pivot));
                _deathCharred = death.Cause == SiegeDeathCause.Oil;
                var enemy = death.EnemyAppearance;
                try
                {
                    DrawEnemy(batches, basePoint, new SiegeEnemyPose(death.Position, death.Direction, 0, 0, 0, 0), enemy);
                }
                finally { _deathActorTransform = null; _deathCharred = false; }
                DeathsDrawn++;
                continue;
            }
            float scale = death.Scale;
            float centerHeight = Mathf.Lerp(.43f, .12f, collapse) * scale;
            int coat = death.Friendly ? death.Defender == DefenderKind.Archer ? 0x59777d : 0x647875 :
                death.Enemy == SiegeEnemyKind.Goblin ? 0x64763b : 0x746147;
            if (death.Cause == SiegeDeathCause.Oil) coat = 0x493b29;
            void Part(Vector3 local, Vector3 size, int color) =>
                SiegeBox(batches, at + Vector3.Up * centerHeight + basis * (local * scale),
                    size * scale, color, orientation: basis);
            Part(Vector3.Zero, new(.26f, .32f, .18f), coat);
            Part(new(0, .26f, 0), new(.18f, .18f, .18f), death.Friendly ? 0xc6bf9c : 0x8b9255);
            foreach (int side in new[] { -1, 1 })
            {
                Part(new(side * .075f, -.25f, 0), new(.09f, .28f, .11f), 0x443d32);
                Part(new(side * .18f, -.04f, -.03f), new(.07f, .28f, .08f), death.Friendly ? 0xc6bf9c : 0x8b9255);
            }
            Part(new(0, .37f, 0), new(.22f, .07f, .22f), death.Defender == DefenderKind.Archer ? 0x79634b : 0xa4a599);
            Part(new(.22f, 0, -.1f), death.Defender == DefenderKind.Archer ? new(.035f, .40f, .035f) :
                new(.035f, .48f, .045f), death.Defender == DefenderKind.Archer ? 0xa69666 : 0xc8c8d0);
            DeathsDrawn++;
        }
    }
}
