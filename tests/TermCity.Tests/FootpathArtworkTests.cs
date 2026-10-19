using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class FootpathArtworkTests
{
    [Theory]
    [InlineData(FootpathArtwork.FreshColor)]
    [InlineData(FootpathArtwork.WornColor)]
    public void DirtTextureHasStableOpaqueEarthClustersInsteadOfFlatColorOrSpeckles(int color)
    {
        var pixels = FootpathArtwork.MaterialTexture(color);
        Assert.Equal(pixels, FootpathArtwork.MaterialTexture(color));
        Assert.Equal(FootpathArtwork.TextureSize * FootpathArtwork.TextureSize * 4, pixels.Length);
        var tones = new HashSet<byte>();
        for (int y = 0; y < FootpathArtwork.TextureSize; y++)
            for (int x = 0; x < FootpathArtwork.TextureSize; x++)
            {
                int pixel = (y * FootpathArtwork.TextureSize + x) * 4;
                int cluster = (y / FootpathArtwork.ClusterSize * FootpathArtwork.ClusterSize *
                    FootpathArtwork.TextureSize + x / FootpathArtwork.ClusterSize * FootpathArtwork.ClusterSize) * 4;
                Assert.Equal(pixels.AsSpan(cluster, 4).ToArray(), pixels.AsSpan(pixel, 4).ToArray());
                Assert.Equal(255, pixels[pixel + 3]);
                Assert.True(pixels[pixel] > pixels[pixel + 1] && pixels[pixel + 1] > pixels[pixel + 2]);
                tones.Add(pixels[pixel]);
            }
        Assert.Equal(4, tones.Count);
        Assert.InRange(tones.Max() - tones.Min(), 25, 50);
        Assert.True(FootpathArtwork.ClusterSize >= 4);
    }

    [Fact]
    public void WearPalettesAreSharedAndInvalidMaterialColorsAreRejected()
    {
        Assert.Equal(FootpathArtwork.FreshColor, FootpathArtwork.ColorForWear(1));
        Assert.Equal(FootpathArtwork.WornColor, FootpathArtwork.ColorForWear(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => FootpathArtwork.MaterialTexture(0));
    }
}
