using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WorldTerrainTextureTests
{
    [Fact]
    public void SharedAtlasAndCompactDataReconstructEverySquareTextureTexelExactly()
    {
        var map = new GameMap(20, 20, new GameContent());
        map.SetTerrain(16, 16, map.Content.Terrains.Get("Water"));
        map.SetTerrain(17, 16, map.Content.Terrains.Get("Hill"));
        var atlas = WorldTerrainTexture.PrepareAtlas();
        Assert.Equal(40, atlas.Length);
        long compactMedium = 900L * (16 * 16 * 4 + 32 * 32) + atlas.Length;
        Assert.Equal(1_843_240, compactMedium);
        Assert.True(compactMedium < 78_643_200 * .025);
        foreach (var key in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
        {
            var cells = WorldTerrainTexture.PrepareCellData(map, key.Item1, key.Item2);
            var variations = WorldTerrainTexture.PrepareVariations(map, key.Item1, key.Item2);
            var reference = WorldTerrainTexture.Prepare(map, key.Item1, key.Item2);
            Assert.Equal(1024, cells.Length);
            Assert.Equal(1024, variations.Length);
            Assert.Equal(cells, WorldTerrainTexture.PrepareCellData(map, key.Item1, key.Item2));
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    int cell = ((y / 8) * 16 + x / 8) * 4;
                    int variation = variations[(y / 4) * 32 + x / 4];
                    Assert.InRange(variation, 0, 4);
                    int category = cells[cell + 3];
                    int palette = ((category == 2 ? 1 : 0) * 5 + variation) * 4;
                    int pixel = (y * 128 + x) * 4;
                    for (int channel = 0; channel < 3; channel++)
                        Assert.Equal(reference[pixel + channel], category == 0 ? (byte)0 :
                            category == 3 ? cells[cell + channel] : atlas[palette + channel]);
                    Assert.Equal(reference[pixel + 3], category == 0 ? (byte)0 : (byte)255);
                }
        }
    }

    [Fact]
    public void SquareTexturesHaveExactMediumMemoryReductionAndBroadOpaquePatches()
    {
        var map = new GameMap(32, 32, new GameContent());
        var pixels = WorldTerrainTexture.Prepare(map, 0, 0);
        Assert.Equal(128, WorldTerrainTexture.ChunkPixels);
        Assert.Equal(65_536, pixels.Length);
        Assert.Equal(pixels, WorldTerrainTexture.Prepare(map, 0, 0));
        long oldBytes = 900L * 192 * 352 * 4 * 4 / 3;
        long newBytes = 900L * pixels.Length * 4 / 3;
        Assert.Equal(78_643_200, newBytes);
        Assert.True(newBytes < oldBytes * .25);
        Assert.All(Enumerable.Range(0, pixels.Length / 4), i => Assert.Equal(255, pixels[i * 4 + 3]));
        for (int y = 0; y < 128; y += WorldTerrainTexture.PatchPixels)
            for (int x = 0; x < 128; x += WorldTerrainTexture.PatchPixels)
            {
                var color = pixels.AsSpan((y * 128 + x) * 4, 4).ToArray();
                Assert.Equal(color, pixels.AsSpan((y * 128 + x + 1) * 4, 4).ToArray());
                Assert.Equal(color, pixels.AsSpan(((y + 1) * 128 + x) * 4, 4).ToArray());
                Assert.Equal(color, pixels.AsSpan(((y + 3) * 128 + x + 3) * 4, 4).ToArray());
            }
        Assert.NotEqual(pixels, WorldTerrainTexture.Prepare(map, 1, 0));
    }

    [Fact]
    public void WaterAndHillCategoriesStayInsideTheirLogicalCellsAndMapIsUnchanged()
    {
        var map = new GameMap(20, 20, new GameContent());
        map.SetTerrain(16, 16, map.Content.Terrains.Get("Water"));
        map.SetTerrain(17, 16, map.Content.Terrains.Get("Hill"));
        int version = map.TerrainVersion;
        var pixels = WorldTerrainTexture.Prepare(map, 1, 1);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
            {
                int offset = (y * 128 + x) * 4;
                Assert.True(x < 8 ? pixels[offset + 2] > pixels[offset + 1] : pixels[offset + 1] > pixels[offset + 2]);
            }
        Assert.Equal(0, pixels[(40 * 128 + 40) * 4 + 3]);
        Assert.Equal(version, map.TerrainVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldTerrainTexture.Prepare(map, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldTerrainTexture.Prepare(map, 2, 0));
    }
}
