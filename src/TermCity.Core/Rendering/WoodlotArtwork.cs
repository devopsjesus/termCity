using System.Numerics;

namespace TermCity.Core.Rendering;

public readonly record struct WoodlotPart(Vector3 Center, Vector3 Size, int Color);

/// <summary>Original block-built firewood cords in a low fenced working yard.</summary>
public static class WoodlotArtwork
{
    public const int EarthColor = 0x77654a;
    public const int PathColor = 0x998460;
    public const int BarkColor = 0x7b5136;
    public const int BarkShadeColor = 0x61402d;
    public const int CutEndColor = 0xc3a16b;
    public const int CutEndShadeColor = 0xaa8856;
    public const int FenceColor = HouseAppearance.TimberColor;

    public static IReadOnlyList<WoodlotPart> Parts(int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        var parts = new List<WoodlotPart>();
        float w = width - 0.18f, d = depth - 0.18f;
        void Add(float x, float y, float z, float sx, float sy, float sz, int color) =>
            parts.Add(new(new(x, y, z), new(sx, sy, sz), color));

        Add(0, 0.025f, 0, w, 0.05f, d, EarthColor);
        Add(0, 0.056f, d * 0.3f, 0.28f, 0.012f, d * 0.4f, PathColor);
        float fenceX = w / 2 - 0.025f, fenceZ = d / 2 - 0.025f;
        var sidePosts = BuildingDetailLayout.Centers(d - 0.05f, 0.055f, 0.45f);
        foreach (int side in new[] { -1, 1 })
        {
            foreach (float z in sidePosts)
                Add(side * fenceX, 0.23f, z, 0.055f, 0.36f, 0.055f, FenceColor);
            foreach (float y in new[] { 0.17f, 0.32f })
                Add(side * fenceX, y, 0, 0.035f, 0.055f, d, FenceColor);
        }
        foreach (float x in BuildingDetailLayout.Centers(w - 0.05f, 0.055f, 0.45f))
            Add(x, 0.23f, -fenceZ, 0.055f, 0.36f, 0.055f, FenceColor);
        foreach (float y in new[] { 0.17f, 0.32f })
            Add(0, y, -fenceZ, w, 0.055f, 0.035f, FenceColor);
        foreach (int side in new[] { -1, 1 })
        {
            Add(side * 0.18f, 0.23f, fenceZ, 0.065f, 0.36f, 0.065f, FenceColor);
            float railWidth = fenceX - 0.18f;
            foreach (float y in new[] { 0.17f, 0.32f })
                Add(side * (fenceX + 0.18f) / 2, y, fenceZ, railWidth, 0.055f, 0.035f, FenceColor);
        }
        Add(0, 0.22f, fenceZ, 0.31f, 0.045f, 0.045f, FenceColor);

        int columns = width >= 2 ? 2 : 1, rows = depth >= 2 ? 2 : 1;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                float stackX = columns == 1 ? -0.1f : (column == 0 ? -1 : 1) * width * 0.24f;
                float stackZ = rows == 1 ? -0.13f : (row == 0 ? -1 : 1) * depth * 0.20f;
                Add(stackX, 0.073f, stackZ, 0.49f, 0.045f, 0.44f, FenceColor);
                for (int layer = 0; layer < 3; layer++)
                    for (int log = 0; log < 4 - layer; log++)
                    {
                        float x = stackX + (log - (3 - layer) / 2f) * 0.11f;
                        float y = 0.15f + layer * 0.10f;
                        int bark = (log + layer + row) % 2 == 0 ? BarkColor : BarkShadeColor;
                        Add(x, y, stackZ, 0.10f, 0.09f, 0.40f, bark);
                        Add(x, y, stackZ + 0.205f, 0.085f, 0.073f, 0.018f,
                            (log + column) % 2 == 0 ? CutEndColor : CutEndShadeColor);
                        Add(x, y, stackZ - 0.205f, 0.085f, 0.073f, 0.018f, CutEndShadeColor);
                    }
            }
        return parts;
    }
}
