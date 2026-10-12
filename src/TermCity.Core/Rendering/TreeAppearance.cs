using System.Numerics;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public enum TreeShape { Oak, Rounded, Tall, Spreading, Pine, TallPine }

public readonly record struct TreePart(Vector3 Center, Vector3 Size, int Color);

public readonly record struct TreeAppearance(TreeShape Shape, float WidthScale, float HeightScale, int Palette)
{
    public const int TrunkColor = 0x79634b;
    public bool IsPine => Shape is TreeShape.Pine or TreeShape.TallPine;

    public static TreeAppearance At(int x, int y) => new(
        (TreeShape)CellHash.Pick(x, y, 6),
        0.9f + CellHash.Pick(x + 71, y - 23, 5) * 0.05f,
        0.9f + CellHash.Pick(x - 37, y + 89, 5) * 0.05f,
        CellHash.Pick(x + 113, y + 53, 3));

    public static bool IsFoliageColor(int rgb) => rgb is
        0x365f3f or 0x4e7646 or 0x73854e or
        0x42633b or 0x648044 or 0x8a9456 or
        0x3d654c or 0x587e54 or 0x80915b or
        0x294b3e or 0x3b6450 or 0x597957;

    public IReadOnlyList<TreePart> Parts()
    {
        var parts = new List<TreePart>();
        var scale = new Vector3(WidthScale, HeightScale, WidthScale);
        (int Dark, int Mid, int Light) colors = IsPine ? (0x294b3e, 0x3b6450, 0x597957) : Palette switch
        {
            1 => (0x42633b, 0x648044, 0x8a9456),
            2 => (0x3d654c, 0x587e54, 0x80915b),
            _ => (0x365f3f, 0x4e7646, 0x73854e),
        };
        void Piece(float x, float y, float z, float width, float height, float depth, int color) =>
            parts.Add(new(new Vector3(x, y, z) * scale, new Vector3(width, height, depth) * scale, color));
        Piece(0, 0.32f, 0, Shape == TreeShape.Spreading ? 0.15f : 0.11f, 0.64f, 0.11f, TrunkColor);
        switch (Shape)
        {
            case TreeShape.Pine:
            case TreeShape.TallPine:
                int tiers = Shape == TreeShape.TallPine ? 5 : 4;
                for (int tier = 0; tier < tiers; tier++)
                {
                    float width = (Shape == TreeShape.TallPine ? 0.64f : 0.78f) * (tiers - tier) / tiers;
                    Piece(0, 0.48f + tier * 0.19f, 0, width, 0.27f, width * 0.9f,
                        tier == 0 ? colors.Dark : tier == tiers - 1 ? colors.Light : colors.Mid);
                }
                break;
            case TreeShape.Rounded:
                Piece(0, 0.58f, 0, 0.56f, 0.32f, 0.55f, colors.Dark);
                Piece(0.02f, 0.76f, 0, 0.66f, 0.27f, 0.59f, colors.Mid);
                Piece(-0.05f, 0.94f, 0.01f, 0.42f, 0.18f, 0.4f, colors.Light);
                break;
            case TreeShape.Tall:
                Piece(0, 0.64f, 0, 0.4f, 0.36f, 0.38f, colors.Dark);
                Piece(-0.04f, 0.9f, 0.01f, 0.5f, 0.35f, 0.43f, colors.Mid);
                Piece(0.02f, 1.16f, 0, 0.32f, 0.23f, 0.3f, colors.Light);
                break;
            case TreeShape.Spreading:
                Piece(0, 0.63f, 0, 0.68f, 0.25f, 0.64f, colors.Dark);
                Piece(-0.23f, 0.79f, 0.06f, 0.48f, 0.24f, 0.49f, colors.Mid);
                Piece(0.23f, 0.84f, -0.05f, 0.46f, 0.25f, 0.46f, colors.Mid);
                Piece(-0.03f, 0.98f, 0, 0.56f, 0.2f, 0.5f, colors.Light);
                break;
            default:
                Piece(0, 0.65f, 0, 0.59f, 0.35f, 0.55f, colors.Mid);
                Piece(-0.25f, 0.66f, 0.07f, 0.3f, 0.26f, 0.37f, colors.Dark);
                Piece(0.22f, 0.76f, -0.13f, 0.33f, 0.28f, 0.34f, colors.Mid);
                Piece(-0.07f, 0.91f, 0, 0.46f, 0.26f, 0.43f, colors.Light);
                Piece(0.05f, 1.06f, 0.04f, 0.26f, 0.13f, 0.27f, colors.Light);
                break;
        }
        return parts;
    }
}
