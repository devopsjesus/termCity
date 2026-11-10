using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public readonly record struct WallUpgradeBlock(Vector3 Center, Vector3 Size, int Color);

public static class WallUpgradeArtwork
{
    public const float CourseHeight = 0.3f;

    public static IEnumerable<WallUpgradeBlock> Blocks(SettlementState settlement, WallComponent wall)
    {
        double progress = settlement.ProgressFor(wall);
        var target = settlement.UpgradeTargetMaterial;
        if (settlement.MaterialFor(wall) == WallMaterial.ReinforcedStone)
        {
            target = WallMaterial.ReinforcedStone;
            progress = 1;
        }
        if (progress <= 0 || target is null || wall.Health <= 0 ||
            progress >= 1 && target != WallMaterial.ReinforcedStone) yield break;
        float laidHeight = (WallDamageLayout.NominalHeight(wall) + 0.4f) * (float)progress;
        bool stone = target == WallMaterial.Stone;
        foreach (var part in WallDamageLayout.Fragments(wall))
        {
            if (part.Kind is not (WallFragmentKind.Body or WallFragmentKind.Merlon)) continue;
            float bottom = part.Center.Y - part.Size.Y / 2;
            float top = Math.Min(laidHeight, part.Center.Y + part.Size.Y / 2);
            for (float y = bottom; y < top - 0.001f; y += CourseHeight)
            {
                float height = Math.Min(CourseHeight, top - y);
                int row = (int)MathF.Floor(y / CourseHeight);
                float blockWidth = stone ? 0.38f : 0.2f;
                int columns = Math.Max(1, (int)Math.Ceiling(part.Size.X / blockWidth));
                for (int x = 0; x < columns; x++)
                {
                    if (!stone && row % 3 != 0 && x != 0 && x != columns - 1) continue;
                    float width = part.Size.X / columns;
                    int color = stone ? (row + x) % 2 == 0 ? 0xa4a599 : 0x929589 : 0x586b71;
                    foreach (int side in new[] { -1, 1 })
                        yield return new(new(part.Center.X - part.Size.X / 2 + (x + 0.5f) * width,
                            y + height / 2, part.Center.Z + side * (part.Size.Z / 2 + 0.035f)),
                            new(Math.Max(0.02f, width - 0.015f), Math.Max(0.01f, height - 0.012f), 0.07f), color);
                }
                if (stone || row % 3 == 0)
                    foreach (int side in new[] { -1, 1 })
                    {
                        if (wall.IsGate && part.Center.X * side < 0) continue;
                        yield return new(new(part.Center.X + side * (part.Size.X / 2 + 0.035f),
                            y + height / 2, part.Center.Z),
                            new(0.07f, Math.Max(0.01f, height - 0.012f), part.Size.Z + 0.1f),
                            stone ? row % 2 == 0 ? 0xa4a599 : 0x929589 : 0x586b71);
                    }
            }
        }
    }

    public static int VisualStep(SettlementState settlement, WallComponent wall) =>
        (int)Math.Floor(settlement.ProgressFor(wall) * 32);
}
