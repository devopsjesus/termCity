using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>Square, world-proportioned color data for perspective terrain.</summary>
public static class WorldTerrainTexture
{
    public const int PixelsPerCell = 8;
    public const int PatchPixels = 4;
    public const int ChunkPixels = TerrainSprite.ChunkCells * PixelsPerCell;
    public const int DataPixels = TerrainSprite.ChunkCells;
    public const int VariationPixels = ChunkPixels / PatchPixels;
    public const int AtlasWidth = 5;
    public const int AtlasHeight = 2;

    public static byte[] PrepareAtlas()
    {
        var pixels = new byte[AtlasWidth * AtlasHeight * 4];
        for (int category = 0; category < AtlasHeight; category++)
            for (int variant = 0; variant < AtlasWidth; variant++)
                Write(pixels, (category * AtlasWidth + variant) * 4, Palette(category == 1, variant), 255);
        return pixels;
    }

    public static byte[] PrepareCellData(GameMap map, int chunkX, int chunkY)
    {
        Validate(map, chunkX, chunkY);
        var data = new byte[DataPixels * DataPixels * 4];
        for (int y = 0; y < DataPixels; y++)
            for (int x = 0; x < DataPixels; x++)
            {
                int wx = chunkX * DataPixels + x, wy = chunkY * DataPixels + y;
                if (!map.InBounds(wx, wy)) continue;
                var terrain = map.TerrainAt(wx, wy);
                byte category = terrain.Name == DefaultTerrains.WaterName ? (byte)2 :
                    terrain.Name is DefaultTerrains.GrassName or DefaultTerrains.HillName ? (byte)1 : (byte)3;
                Write(data, (y * DataPixels + x) * 4, terrain.Background, category);
            }
        return data;
    }

    public static byte[] PrepareVariations(GameMap map, int chunkX, int chunkY)
    {
        Validate(map, chunkX, chunkY);
        var data = new byte[VariationPixels * VariationPixels];
        for (int y = 0; y < VariationPixels; y++)
            for (int x = 0; x < VariationPixels; x++)
                data[y * VariationPixels + x] = (byte)CellHash.Pick(
                    chunkX * VariationPixels + x, chunkY * VariationPixels + y, AtlasWidth);
        return data;
    }

    private static Rgb Palette(bool water, int variation) => water
        ? Rgb.Blend(Rgb.Hex(0x205776), Rgb.Hex(0x30758e), variation / 4d)
        : Rgb.Blend(Rgb.Hex(0x385c2f), Rgb.Hex(0x50783c), .3 + variation / 4d * .4);

    private static void Write(byte[] pixels, int offset, Rgb color, byte alpha)
    {
        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
        pixels[offset + 3] = alpha;
    }

    private static void Validate(GameMap map, int chunkX, int chunkY)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (chunkX < 0 || chunkY < 0 || chunkX * 16L >= map.Width || chunkY * 16L >= map.Height)
            throw new ArgumentOutOfRangeException(nameof(chunkX), "Terrain texture chunks must intersect the map.");
    }

    public static byte[] Prepare(GameMap map, int chunkX, int chunkY)
    {
        Validate(map, chunkX, chunkY);
        var pixels = new byte[ChunkPixels * ChunkPixels * 4];
        for (int y = 0; y < ChunkPixels; y += PatchPixels)
            for (int x = 0; x < ChunkPixels; x += PatchPixels)
            {
                int worldX = chunkX * TerrainSprite.ChunkCells + x / PixelsPerCell;
                int worldY = chunkY * TerrainSprite.ChunkCells + y / PixelsPerCell;
                if (!map.InBounds(worldX, worldY)) continue;
                var terrain = map.TerrainAt(worldX, worldY);
                int patchX = chunkX * ChunkPixels / PatchPixels + x / PatchPixels;
                int patchY = chunkY * ChunkPixels / PatchPixels + y / PatchPixels;
                int variation = CellHash.Pick(patchX, patchY, 5);
                Rgb color = terrain.Name == DefaultTerrains.WaterName
                    ? Palette(true, variation)
                    : terrain.Name is DefaultTerrains.GrassName or DefaultTerrains.HillName
                        ? Palette(false, variation)
                        : terrain.Background;
                for (int dy = 0; dy < PatchPixels; dy++)
                    for (int dx = 0; dx < PatchPixels; dx++)
                    {
                        int offset = ((y + dy) * ChunkPixels + x + dx) * 4;
                        pixels[offset] = color.R;
                        pixels[offset + 1] = color.G;
                        pixels[offset + 2] = color.B;
                        pixels[offset + 3] = 255;
                    }
            }
        return pixels;
    }
}
