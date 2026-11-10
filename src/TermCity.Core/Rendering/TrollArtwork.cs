using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public readonly record struct TrollPart(Vector3 Center, Vector3 Size, int Color);

public static class TrollArtwork
{
    public static IReadOnlyList<TrollPart> Parts(SiegeEnemy enemy)
    {
        if (!enemy.Definition.IsTroll) throw new ArgumentException("A troll species is required.", nameof(enemy));
        int body = enemy.Kind switch
        {
            SiegeEnemyKind.TreeTroll => 0x68503b,
            SiegeEnemyKind.StoneTroll => 0x85877d,
            _ => 0x76604a,
        };
        int shade = enemy.Kind switch
        {
            SiegeEnemyKind.TreeTroll => 0x42633b,
            SiegeEnemyKind.StoneTroll => 0x64695f,
            _ => 0x554936,
        };
        float width = (float)enemy.Definition.WidthMultiplier;
        var parts = new List<TrollPart>();
        void Part(Vector3 center, Vector3 size, int color) =>
            parts.Add(new(center * new Vector3(width, 1, width), size * new Vector3(width, 1, width), color));
        bool tree = enemy.Kind == SiegeEnemyKind.TreeTroll;
        bool mud = enemy.Kind == SiegeEnemyKind.MudTroll;
        Part(new(0, 0.70f, 0), new(tree ? 0.45f : 0.78f, 0.66f, mud ? 0.64f : 0.46f), body);
        Part(new(0, 1.17f, -0.07f), new(tree ? 0.38f : 0.58f, 0.36f, 0.40f), body);
        foreach (int side in new[] { -1, 1 })
        {
            Part(new(side * 0.21f, 0.19f, 0), new(0.25f, 0.38f, 0.34f), shade);
            Part(new(side * (tree ? 0.40f : 0.48f), 0.58f, -0.02f),
                new(tree ? 0.18f : 0.24f, tree ? 0.80f : 0.90f, 0.24f), body);
            Part(new(side * 0.13f, 1.20f, -0.285f), new(0.075f, 0.045f, 0.035f), 0xd4b465);
            if (tree)
            {
                Part(new(side * 0.31f, 1.15f, 0.06f), new(0.14f, 0.30f, 0.14f), body);
                Part(new(side * 0.35f, 1.28f, 0.08f), new(0.28f, 0.14f, 0.28f), shade);
            }
            else
                Part(new(side * 0.34f, 0.86f, 0.13f), new(0.30f, 0.32f, mud ? 0.38f : 0.25f), shade);
        }
        Part(new(0.08f, 0.66f, -0.25f), new(0.26f, 0.28f, 0.07f), shade);
        return parts;
    }
}
