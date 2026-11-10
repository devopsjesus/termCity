using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class FootpathArtwork
{
    public const int FreshColor = 0x75634d;
    public const int WornColor = 0x8d795b;
    public const int TextureSize = 256;
    public const int ClusterSize = 4;
    public const float UvScale = 0.0875f;
    public const float SurfaceLift = 0.014f;
    public const float ReliefHeight = 0.015f;
    public const float ShoulderWidth = 0.12f;

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
                int column = x / ClusterSize, row = y / ClusterSize;
                int tone = CellHash.Pick(column / 2, row / 2, 3) + CellHash.Pick(column + 17, row + 29, 2);
                var patch = color.Scale(0.94 + tone * 0.04);
                int pixel = (y * TextureSize + x) * 4;
                pixels[pixel] = patch.R;
                pixels[pixel + 1] = patch.G;
                pixels[pixel + 2] = patch.B;
                pixels[pixel + 3] = 255;
            }
        return pixels;
    }
}
