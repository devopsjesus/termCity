using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public sealed record HouseAppearance(float Width, float Depth, float Height, float RoofRise,
    int WallColor, int RoofColor, bool CrosswiseRoof, bool Wing, bool Chimney)
{
    public const int TextureSize = 32;
    public const int TextureClusterSize = 4;
    public const int TimberColor = 0x68503b;
    public const int StoneColor = 0x99917c;
    public const int SillColor = 0xb3a58a;

    public int RoofCapColor => RoofColor switch
    {
        0xb2a273 => 0xc0ae7e, 0x9e875e => 0xad9364, 0x8d6450 => 0xa5775b, _ => 0x7b8985,
    };

    public static bool IsWallColor(int rgb) => rgb is 0xc9bf9f or 0xbaa98a or 0xd7c9aa or 0x9d947f;
    public static bool IsRoofColor(int rgb) => rgb is 0xb2a273 or 0x9e875e or 0x8d6450 or 0x687574;
    public static bool IsTexturedColor(int rgb) => IsWallColor(rgb) || IsRoofColor(rgb) || rgb == StoneColor;

    public static byte[] MaterialTexture(int rgb)
    {
        if (!IsTexturedColor(rgb)) throw new ArgumentOutOfRangeException(nameof(rgb));
        const int size = TextureSize;
        var pixels = new byte[size * size * 4];
        var color = Rgb.Hex(rgb);
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                int across = column / TextureClusterSize, down = row / TextureClusterSize;
                double shade;
                if (IsWallColor(rgb))
                {
                    int tone = 3;
                    for (int patchIndex = 0; patchIndex < 9; patchIndex++)
                    {
                        int patchX = CellHash.Pick(patchIndex, rgb, 20) - 2;
                        int patchY = CellHash.Pick(rgb, patchIndex + 31, 20) - 2;
                        int deltaX = across * 2 - patchX, deltaY = down * 2 - patchY;
                        if (deltaX * deltaX * 2 + deltaY * deltaY * 3 < 28 + patchIndex % 3 * 12)
                            tone = CellHash.Pick(patchIndex + 13, rgb, 6);
                    }
                    shade = 0.8 + tone * 0.055 + (down < 2 ? 0.055 : 0);
                }
                else if (IsRoofColor(rgb))
                {
                    int slope = Math.Min(down, size / TextureClusterSize - 1 - down);
                    bool thatch = rgb is 0xb2a273 or 0x9e875e;
                    int course = slope / 2;
                    int staggered = across + course % 2;
                    int tile = staggered / (thatch ? 1 : 2);
                    int tone = CellHash.Pick(tile + rgb, course + 41, 5);
                    shade = 0.78 + tone * 0.075 + course * 0.045;
                }
                else
                {
                    int course = down / 2;
                    int staggered = across + course % 2;
                    shade = 0.78 + CellHash.Pick(staggered / 3, course, 6) * 0.07;
                }
                var patch = color.Scale(shade);
                int pixel = (row * size + column) * 4;
                pixels[pixel] = patch.R;
                pixels[pixel + 1] = patch.G;
                pixels[pixel + 2] = patch.B;
                pixels[pixel + 3] = 255;
            }
        return pixels;
    }

    public static HouseAppearance At(int x, int y, int level)
    {
        int shape = CellHash.Pick(x, y, 6);
        int wall = CellHash.Pick(x + 19, y - 7, 4) switch
        {
            0 => 0xc9bf9f, 1 => 0xbaa98a, 2 => 0xd7c9aa, _ => 0x9d947f,
        };
        int roof = CellHash.Pick(x - 11, y + 23, 4) switch
        {
            0 => 0xb2a273, 1 => 0x9e875e, 2 => 0x8d6450, _ => 0x687574,
        };
        return new(shape is 0 or 3 ? 0.66f : 0.8f, shape is 1 or 4 ? 0.64f : 0.78f,
            0.38f + level * 0.18f + (shape % 3) * 0.07f, shape % 2 == 0 ? 0.3f : 0.42f,
            wall, roof, shape >= 3, shape is 2 or 5, level > 1 || shape == 4);
    }
}
