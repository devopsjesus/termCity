using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class MapMaterialArtwork
{
    private static readonly HashSet<int> StoneColors = Enumerable.Range(0, 6)
        .SelectMany(RockArtwork.Parts).Select(part => part.Color)
        .Concat([0xa4a599, 0xc6bf9c, 0x586b71, 0x89877b, 0x969d8f, 0xb2b5a7, 0xc7c7b5, 0xa3ac9d]).ToHashSet();
    public static bool IsStoneColor(int rgb) => StoneColors.Contains(rgb);
    public static bool IsTimberColor(int rgb) => rgb is
        0x79634b or 0x806d4d or 0x967a56 or 0x594a35 or 0x68503b or 0xb3a58a or 0x8b7252 or
        0x4f3828 or 0x514637 or 0x584738;

    public static byte[] Texture(int rgb)
    {
        bool timber = IsTimberColor(rgb);
        if (!timber && !IsStoneColor(rgb)) throw new ArgumentOutOfRangeException(nameof(rgb));
        var pixels = new byte[16 * 16 * 4];
        var color = Rgb.Hex(rgb);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int row = y / (timber ? 8 : 4), column = x / 4;
                int tone = CellHash.Pick(column + (timber ? 0 : row % 2), timber ? rgb : row + rgb, 5);
                var patch = color.Scale(.82 + tone * .045);
                int i = (y * 16 + x) * 4;
                pixels[i] = patch.R; pixels[i + 1] = patch.G; pixels[i + 2] = patch.B; pixels[i + 3] = 255;
            }
        return pixels;
    }
}
