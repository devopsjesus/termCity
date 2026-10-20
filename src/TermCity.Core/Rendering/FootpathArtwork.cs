using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class FootpathArtwork
{
    public const int FreshColor = 0x79674e;
    public const int WornColor = 0x9d8258;
    public const int TextureSize = 32;
    public const int ClusterSize = 4;
    public const float UvScale = 1.5f;

    private static readonly string[] Patches =
    [
        "22333422",
        "22333442",
        "11223344",
        "11222344",
        "33221133",
        "33221123",
        "44332211",
        "44332221",
    ];

    public static bool IsTexturedColor(int rgb) => rgb is FreshColor or WornColor;
    public static int ColorForWear(int wear) => wear >= 3 ? WornColor : FreshColor;

    public static byte[] MaterialTexture(int rgb)
    {
        if (!IsTexturedColor(rgb)) throw new ArgumentOutOfRangeException(nameof(rgb));
        var pixels = new byte[TextureSize * TextureSize * 4];
        var color = Rgb.Hex(rgb);
        for (int y = 0; y < TextureSize; y++)
            for (int x = 0; x < TextureSize; x++)
            {
                int tone = Patches[y / ClusterSize][x / ClusterSize] - '1';
                var patch = color.Scale(0.82 + tone * 0.085);
                int pixel = (y * TextureSize + x) * 4;
                pixels[pixel] = patch.R;
                pixels[pixel + 1] = patch.G;
                pixels[pixel + 2] = patch.B;
                pixels[pixel + 3] = 255;
            }
        return pixels;
    }
}
