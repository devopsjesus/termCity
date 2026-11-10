using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal sealed record GroundTexturePacket(int Width, int Height, byte[] Terrain, byte[] Flow,
    byte[] Shore, bool RiverFlow)
{
    internal byte[] Variations { get; init; } = [];
    internal long Bytes => 128L + NativeBytes;
    internal long NativeBytes => Terrain.LongLength + Variations.LongLength + Flow.LongLength + Shore.LongLength;

    internal static GroundTexturePacket Prepare(GameMap map, int cx, int cy)
    {
        const int span = TerrainSprite.ChunkCells;
        var terrainPixels = WorldTerrainTexture.PrepareCellData(map, cx, cy);
        var flowPixels = new float[span * span * 4];
        bool hasRiverFlow = false;
        var flowField = TerrainSprite.Flow(map);
        for (int row = 0; row < span; row++)
            for (int column = 0; column < span; column++)
            {
                int x = cx * span + column, y = cy * span + row;
                int pixel = (row * span + column) * 4;
                flowPixels[pixel] = flowPixels[pixel + 1] = .5f;
                if (!map.InBounds(x, y) || map.TerrainAt(x, y).Name != TermCity.Core.Terrain.DefaultTerrains.WaterName)
                    continue;
                var flow = flowField.At(x, y);
                var track = RiverRippleLayout.At(map, flowField, x, y);
                flowPixels[pixel] = ((flow.Direction.X + 1) * 3 + track.Incoming.X + 1) / 8f;
                flowPixels[pixel + 1] = ((flow.Direction.Y + 1) * 3 + track.Incoming.Y + 1) / 8f;
                flowPixels[pixel + 2] = flow.River ? 1 : 0;
                flowPixels[pixel + 3] = flow.Distance;
                hasRiverFlow |= flow.River;
            }
        const int shoreSize = span + 2;
        var shorePixels = new float[shoreSize * shoreSize * 4];
        for (int row = 0; row < shoreSize; row++)
            for (int column = 0; column < shoreSize; column++)
            {
                float x = cx * span + column - .5f, y = cy * span + row - .5f;
                int pixel = (row * shoreSize + column) * 4;
                shorePixels[pixel] = ShorelineLayout.BankCell(map, cx * span + column - 1, cy * span + row - 1) ? 1 : 0;
                shorePixels[pixel + 1] = ShorelineLayout.CoastalCellDistance(map, cx * span + column - 1, cy * span + row - 1);
                shorePixels[pixel + 2] = ShorelineLayout.BeachStrength(x, y);
                shorePixels[pixel + 3] = 1;
            }
        return new(WorldTerrainTexture.DataPixels, WorldTerrainTexture.DataPixels, terrainPixels,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(flowPixels.AsSpan()).ToArray(),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(shorePixels.AsSpan()).ToArray(), hasRiverFlow)
        {
            Variations = WorldTerrainTexture.PrepareVariations(map, cx, cy),
        };
    }
}
