namespace TermCity.Core.Rendering;

public static class ZoneOverlayArtwork
{
    public const int TextureSize = 8;
    public const int CheckerSize = 2;
    public const float Across = 1;
    public const float Down = 1;
    public const float BackgroundCoverage = 0.1875f;

    public static byte[] Pixels(CellVisual visual)
    {
        var pixels = new byte[TextureSize * TextureSize * 4];
        for (int row = 0; row < TextureSize; row++)
            for (int column = 0; column < TextureSize; column++)
            {
                bool ink = (row / CheckerSize + column / CheckerSize) % 2 == 0;
                var color = ink ? visual.Foreground : visual.Background;
                int index = row * TextureSize + column;
                pixels[index * 4] = color.R;
                pixels[index * 4 + 1] = color.G;
                pixels[index * 4 + 2] = color.B;
                pixels[index * 4 + 3] = ink ? (byte)255 : (byte)Math.Round(255 * BackgroundCoverage);
            }
        return pixels;
    }
}
