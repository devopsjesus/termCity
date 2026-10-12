using TermCity.Core.Features;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Simulation;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TermCity.Core.Rendering;

public sealed record TerrainSprite(int Width, int Height, int Lift, byte[] Pixels)
{
    public const int ChunkCells = 16;
    public const float HillBaseHeight = 0.3f;
    public const float HillStepHeight = 0.35f;
    public const float MaximumHillHeight = HillBaseHeight + 3 * HillStepHeight;
    public bool AnimatedWater { get; init; }
    public static WaterFlowField Flow(GameMap map) => map.WaterFlow ??= new WaterFlowField(map);

    public static float WaterCornerLevel(GameMap map, int x, int y)
    {
        float sum = 0;
        int count = 0;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = Math.Clamp(x + dx, 0, map.Width - 1), cy = Math.Clamp(y + dy, 0, map.Height - 1);
                if (map.TerrainAt(cx, cy).Name != DefaultTerrains.WaterName) continue;
                sum += Flow(map).At(cx, cy).Level;
                count++;
            }
        return count > 0 ? sum / count : -0.16f;
    }

    public static float Elevation(GameMap map, int x, int y)
    {
        var terrain = map.TerrainAt(x, y);
        if (!terrain.Buildable) return -0.04f;
        if (terrain.BuildCostModifier <= 1) return 0;
        float level = HillBaseHeight + (HillLevel(map, x, y) - 1) * HillStepHeight;
        map.CoastalHillCaps ??= new byte[map.Width * map.Height];
        int index = map.Index(x, y);
        if (map.CoastalHillCaps[index] != 0) return map.CoastalHillCaps[index] == 2 ? 0 : level;
        if (WaterDensity(map, x, y) == 0)
        {
            bool coast = false;
            for (int dy = -1; dy <= 1 && !coast; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (WaterDensity(map, x + dx, y + dy) > 0) { coast = true; break; }
            map.CoastalHillCaps[index] = coast ? (byte)1 : (byte)3;
            return level;
        }
        if (map.CoastalHillCaps[index] == 0)
        {
            int supports = 0;
            foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
                if (map.InBounds(x + dx, y + dy) && map.TerrainAt(x + dx, y + dy).BuildCostModifier > 1) supports++;
            var land = ShorelineLayout.Cell(map, x, y).Where(p => !p.Water).ToArray();
            bool thin = land.Length == 0 || land.All(p => RoadMeshLayout.IsThinRemnant(p.Polygon));
            map.CoastalHillCaps[index] = supports == 0 || thin ? (byte)2 : (byte)1;
        }
        return map.CoastalHillCaps[index] == 2 ? 0 : level;
    }

    public static int HillLevel(GameMap map, int x, int y)
    {
        if (map.HillLevels is null)
        {
            var levels = new byte[map.Width * map.Height];
            var queue = new Queue<int>();
            bool Hill(int cx, int cy) => map.InBounds(cx, cy) &&
                map.TerrainAt(cx, cy).Buildable && map.TerrainAt(cx, cy).BuildCostModifier > 1;
            for (int cy = 0; cy < map.Height; cy++)
                for (int cx = 0; cx < map.Width; cx++)
                    if (Hill(cx, cy) && (!Hill(cx - 1, cy) || !Hill(cx + 1, cy) ||
                        !Hill(cx, cy - 1) || !Hill(cx, cy + 1)))
                    {
                        int cell = map.Index(cx, cy);
                        levels[cell] = 1;
                        queue.Enqueue(cell);
                    }
            while (queue.TryDequeue(out int cell))
            {
                int cx = cell % map.Width, cy = cell / map.Width;
                foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!Hill(nx, ny)) continue;
                    int next = map.Index(nx, ny);
                    if (levels[next] != 0) continue;
                    levels[next] = (byte)Math.Min(4, levels[cell] + 1);
                    queue.Enqueue(next);
                }
            }
            map.HillLevels = levels;
        }
        return map.HillLevels[map.Index(x, y)];
    }

    public static double WaterSurface(GameMap map, double x, double y)
    {
        int left = (int)Math.Floor(x - 0.5), top = (int)Math.Floor(y - 0.5);
        double tx = x - 0.5 - left, ty = y - 0.5 - top;
        double a = WaterDensity(map, left, top), b = WaterDensity(map, left + 1, top);
        double c = WaterDensity(map, left, top + 1), d = WaterDensity(map, left + 1, top + 1);
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

    public static double WaterDensity(GameMap map, int x, int y)
    {
        x = Math.Clamp(x, -1, map.Width);
        y = Math.Clamp(y, -1, map.Height);
        if (map.WaterDensities is null)
        {
            map.WaterDensities = new double[(map.Width + 2) * (map.Height + 2)];
            Array.Fill(map.WaterDensities, double.NaN);
        }
        int index = (y + 1) * (map.Width + 2) + x + 1;
        if (!double.IsNaN(map.WaterDensities[index])) return map.WaterDensities[index];
        double sum = 0;
        int nearbyWater = 0;
        byte waterId = map.Content.Terrains.Get(DefaultTerrains.WaterName).Id;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, map.Width - 1), sy = Math.Clamp(y + dy, 0, map.Height - 1);
                if (map.TerrainLayer[map.Index(sx, sy)] == waterId)
                {
                    sum += dx == 0 && dy == 0 ? 8 : dx == 0 || dy == 0 ? 2 : 1;
                    nearbyWater++;
                }
            }
        double narrowDensity = sum / 20;
        ReadOnlySpan<int> weights = [1, 4, 6, 4, 1];
        sum = 0;
        int broadWater = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, map.Width - 1), sy = Math.Clamp(y + dy, 0, map.Height - 1);
                if (map.TerrainLayer[map.Index(sx, sy)] == waterId)
                {
                    sum += weights[dx + 2] * weights[dy + 2];
                    broadWater++;
                }
            }
        if (nearbyWater <= 3 && broadWater <= 5)
            return map.WaterDensities[index] = narrowDensity;
        return map.WaterDensities[index] = sum / 256;
    }

    public static float SurfaceElevation(GameMap map, int x, int y)
    {
        double water = WaterSurface(map, x, y);
        int cells = 0;
        float hills = 0;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, map.Width - 1), sy = Math.Clamp(y + dy, 0, map.Height - 1);
                if (map.TerrainAt(sx, sy).BuildCostModifier > 1) hills += Elevation(map, sx, sy);
                cells++;
            }
        return water > 0 ? WaterCornerLevel(map, x, y) * (float)Math.Clamp((water - 0.25) / 0.5, 0, 1) : hills / cells;
    }

    public static bool TryShoreCornerHeight(GameMap map, int x, int y, out float height)
    {
        height = 0;
        if (WaterSurface(map, x, y) <= 0) return false;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (map.InBounds(cx, cy) && map.TerrainAt(cx, cy).Buildable &&
                    map.TerrainAt(cx, cy).BuildCostModifier > 1) return false;
            }
        height = SurfaceElevation(map, x, y);
        return true;
    }

    public static bool TryRoadCornerHeight(GameMap map, int x, int y, out float height)
    {
        height = 0;
        int count = 0;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (!map.InBounds(cx, cy) || !map.HasRoad(cx, cy) || !map.TerrainAt(cx, cy).Buildable) continue;
                height += Elevation(map, cx, cy);
                count++;
            }
        if (count > 0)
        {
            height /= count;
            return true;
        }
        return false;
    }

    private static Rgb MeadowColor(int worldPixelX, int worldPixelY)
    {
        // Quantized sampling keeps each patch block-sized rather than a smooth gradient or tiny flecks.
        double x = (worldPixelX / 4 * 4 + 2) / 24d, y = (worldPixelY / 8 * 8 + 4) / 44d;
        int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
        double tx = x - left, ty = y - top;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        double a = CellHash.Pick(left, top, 17) / 16d, b = CellHash.Pick(left + 1, top, 17) / 16d;
        double c = CellHash.Pick(left, top + 1, 17) / 16d, d = CellHash.Pick(left + 1, top + 1, 17) / 16d;
        double shade = (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
        int block = CellHash.Pick(worldPixelX / 4, worldPixelY / 8, 5);
        shade = Math.Clamp(shade * 0.65 + block / 4d * 0.35, 0, 1);
        shade = Math.Round(shade * 4) / 4;
        return Rgb.Blend(Rgb.Hex(0x385c2f), Rgb.Hex(0x50783c), shade);
    }

    private static Rgb WaterColor(int worldPixelX, int worldPixelY, int phase)
    {
        int block = CellHash.Pick(worldPixelX / 4 + phase, worldPixelY / 8, 5);
        return Rgb.Blend(Rgb.Hex(0x205776), Rgb.Hex(0x30758e), block / 4d);
    }

    public static byte[] FoliageTexture(int rgb)
    {
        const int size = 16, blockSize = 4;
        var pixels = new byte[size * size * 4];
        var baseColor = Rgb.Hex(rgb);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int patch = CellHash.Pick(x / blockSize + 17, y / blockSize + 29, 5);
                var color = baseColor.Scale(0.8 + patch * 0.07);
                int i = (y * size + x) * 4;
                pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; pixels[i + 3] = 255;
            }
        return pixels;
    }

    public static TerrainSprite Chunk(GameMap map, int chunkX, int chunkY, int stride, int phase, bool hillEdges = true,
        bool shorelineBlend = true)
    {
        if (stride is not (1 or 2 or 4) || chunkX < 0 || chunkY < 0)
            throw new ArgumentOutOfRangeException(nameof(stride), "Terrain chunks require non-negative coordinates and stride 1, 2 or 4.");
        int width = ChunkCells * RoadCurves.CellWidth, height = ChunkCells * RoadCurves.CellHeight;
        var pixels = new byte[width * height * 4];
        bool animated = false;
        for (int ty = 0; ty < ChunkCells; ty++)
            for (int tx = 0; tx < ChunkCells; tx++)
            {
                int x = (chunkX * ChunkCells + tx) * stride;
                int y = (chunkY * ChunkCells + ty) * stride;
                if (!map.InBounds(x, y)) continue;
                animated |= map.TerrainAt(x, y).Name == DefaultTerrains.WaterName;
                var tile = Ground(map, x, y, phase, hillEdges, shorelineBlend);
                for (int row = 0; row < tile.Height; row++)
                    tile.Pixels.AsSpan(row * tile.Width * 4, tile.Width * 4).CopyTo(
                        pixels.AsSpan(((ty * tile.Height + row) * width + tx * tile.Width) * 4, tile.Width * 4));
            }
        return new(width, height, 0, pixels) { AnimatedWater = animated };
    }

    public static TerrainSprite Ground(GameMap map, int x, int y, int phase = 0, bool hillEdges = true,
        bool shorelineBlend = true)
    {
        const int width = RoadCurves.CellWidth, height = RoadCurves.CellHeight;
        var pixels = new byte[width * height * 4];
        var terrain = map.TerrainAt(x, y);
        bool water = terrain.Name == DefaultTerrains.WaterName;
        bool hill = terrain.Name == DefaultTerrains.HillName;
        int Different(int dx, int dy) => map.InBounds(x + dx, y + dy) &&
            map.TerrainAt(x + dx, y + dy) != terrain ? 1 : 0;
        int north = hill && hillEdges ? Different(0, -1) : 0, east = hill && hillEdges ? Different(1, 0) : 0;
        int south = hill && hillEdges ? Different(0, 1) : 0, west = hill && hillEdges ? Different(-1, 0) : 0;
        float level = !hillEdges ? Elevation(map, x, y) : 0;
        float NeighborHeight(int dx, int dy) => map.InBounds(x + dx, y + dy)
            ? Elevation(map, x + dx, y + dy) : level;
        float northHeight = !hillEdges && !water ? NeighborHeight(0, -1) : 0;
        float westHeight = !hillEdges && !water ? NeighborHeight(-1, 0) : 0;
        var waterSamples = shorelineBlend ? new double[3, 3] : null;
        bool nearWater = false;
        bool openWater = water;
        if (waterSamples is not null && !hill) for (int sy = 0; sy < 3; sy++)
            for (int sx = 0; sx < 3; sx++)
            {
                waterSamples[sx, sy] = WaterDensity(map, x + sx - 1, y + sy - 1);
                nearWater |= waterSamples[sx, sy] > 0;
                openWater &= waterSamples[sx, sy] >= 0.38;
            }
        Span<Rgb> meadowPatches = stackalloc Rgb[12];
        Span<Rgb> waterPatches = stackalloc Rgb[12];
        int blockY = y * height / 8;
        for (int by = 0; by < 4; by++)
            for (int bx = 0; bx < 3; bx++)
            {
                int wx = x * width + bx * 4, wy = (blockY + by) * 8;
                if (!openWater) meadowPatches[by * 3 + bx] = MeadowColor(wx, wy);
                if (nearWater || water) waterPatches[by * 3 + bx] = WaterColor(wx, wy, phase);
            }
        double Shade(int px, int py)
        {
            double northShadow = Math.Clamp((northHeight - level) / HillStepHeight, 0, 1) *
                Math.Max(0, 1 - py / 4 / 3d);
            double westShadow = Math.Clamp((westHeight - level) / HillStepHeight, 0, 1) *
                Math.Max(0, 1 - px / 4 / 2d);
            return (hill ? 1.06 : 1) * (1 - 0.24 * Math.Max(northShadow, westShadow));
        }
        if ((!nearWater || openWater) && !hillEdges)
        {
            var packed = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            for (int py = 0; py < height;)
            {
                int rows = Math.Min(height - py, Math.Min(4 - py % 4, 8 - (y * height + py) % 8));
                for (int px = 0; px < width; px += 4)
                {
                    int patch = ((y * height + py) / 8 - blockY) * 3 + px / 4;
                    var color = openWater ? waterPatches[patch] :
                        (hill || terrain.Name == DefaultTerrains.GrassName
                            ? meadowPatches[patch] : terrain.Background).Scale(Shade(px, py));
                    uint rgba = color.R | (uint)color.G << 8 | (uint)color.B << 16 | 255u << 24;
                    if (!BitConverter.IsLittleEndian) rgba = BinaryPrimitives.ReverseEndianness(rgba);
                    for (int row = 0; row < rows; row++) packed.Slice((py + row) * width + px, 4).Fill(rgba);
                }
                py += rows;
            }
            return new(width, height, 0, pixels);
        }
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                int wx = x * width + px, wy = y * height + py;
                int patch = (wy / 8 - blockY) * 3 + px / 4;
                var meadow = meadowPatches[patch];
                Rgb color;
                if (water)
                {
                    color = waterPatches[patch];
                }
                else
                {
                    color = hill || terrain.Name == DefaultTerrains.GrassName ? meadow : terrain.Background;
                    if (hill && hillEdges)
                    {
                        if (north != 0 && py < 2 || west != 0 && px < 2) color = Rgb.Hex(0x92916c);
                        if (south != 0 && py >= height - 6)
                            color = Rgb.Hex(py % 3 == 0 ? 0x725b40 : 0x655237);
                        if (east != 0 && px >= width - 3) color = Rgb.Hex(0x5b5036);
                    }
                }
                if (nearWater && !hill && waterSamples is not null)
                {
                    double fx = (px + 0.5) / width + 0.5, fy = (py + 0.5) / height + 0.5;
                    int sx = (int)fx, sy = (int)fy;
                    double tx = fx - sx, ty = fy - sy;
                    double density = (waterSamples[sx, sy] * (1 - tx) + waterSamples[sx + 1, sy] * tx) * (1 - ty) +
                        (waterSamples[sx, sy + 1] * (1 - tx) + waterSamples[sx + 1, sy + 1] * tx) * ty;
                    var land = water ? meadow : color;
                    var blue = waterPatches[patch];
                    double blend = Math.Clamp((density - 0.34) / 0.04, 0, 1);
                    double sand = Math.Clamp(1 - Math.Abs(density - 0.34) / 0.035, 0, 1);
                    color = Rgb.Blend(Rgb.Blend(land, Rgb.Hex(0xa19470), sand * 0.65), blue, blend);
                }
                if (!hillEdges && !water)
                {
                    // Fixed northwest light casts broad green shadows onto the lower terrace.
                    color = color.Scale(Shade(px, py));
                }
                int i = (py * width + px) * 4;
                pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; pixels[i + 3] = 255;
            }
        return new(width, height, 0, pixels);
    }

    public static bool NaturalFeatureVisible(CityGame game, int x, int y) =>
        game.Map.FeatureAt(x, y) is not null && ShorelineLayout.NaturalFeatureFits(game.Map, x, y) &&
        game.Map.BuildingAt(x, y) is null &&
        !game.Map.HasRoad(x, y) && !game.Network.RoadOverlapsCell(game.Map.Index(x, y));

    public static Rgb RockColor(int variant) => Rgb.Hex(((variant % 6 + 6) % 6) switch
    {
        0 => 0x858a83,
        1 => 0x707f8c,
        2 => 0x9b917c,
        3 => 0x79747c,
        4 => 0xa39782,
        _ => 0x697a76,
    });

    public static TerrainSprite Feature(FeatureType type, int variant)
    {
        const int width = 18, height = 30, lift = 10;
        var pixels = new byte[width * height * 4];
        void Rect(int x, int y, int w, int h, Rgb color, byte alpha = 255)
        {
            for (int row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
                for (int col = Math.Max(0, x); col < Math.Min(width, x + w); col++)
                {
                    int i = (row * width + col) * 4;
                    pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; pixels[i + 3] = alpha;
                }
        }
        Rect(4, 26, 12, 2, Rgb.Hex(0x202a24), 110);
        if (type.Name == DefaultFeatures.TreeName)
        {
            Rect(8, 16, 3, 11, Rgb.Hex(0x795439));
            Rect(8, 17, 1, 10, Rgb.Hex(0xb08450));
            if (variant % 3 == 0)
            {
                for (int row = 0; row < 20; row++)
                {
                    int tier = row % 7;
                    int half = Math.Min(8, tier / 2 + row / 7 + 1);
                    Rect(9 - half, 2 + row, half * 2, 1, Rgb.Hex(row % 7 == 6 ? 0x2c4c38 : 0x446e48));
                    Rect(9 - half, 2 + row, half, 1, Rgb.Hex(0x668456));
                }
            }
            else
            {
                Rect(4, 4, 10, 15, Rgb.Hex(0x34563c));
                Rect(2, 7, 14, 10, Rgb.Hex(0x34563c));
                Rect(3, 5, 10, 12, Rgb.Hex(0x4e7646));
                Rect(5, 3, 6, 14, Rgb.Hex(0x5e864c));
                Rect(4, 6, 5, 4, Rgb.Hex(0x819657));
                Rect(3, 12, 3, 3, Rgb.Hex(0x6d8c50));
                Rect(12, 10, 3, 5, Rgb.Hex(0x2d5038));
            }
        }
        else if (type.Name == DefaultFeatures.RockName)
        {
            var stone = RockColor(variant);
            void Boulder(int x, int y, int w, int h)
            {
                Rect(x, y, w, h, stone);
                Rect(x, y, Math.Max(2, w - 2), 2, stone.Scale(1.2));
                Rect(x + w - 2, y + 2, 2, Math.Max(1, h - 2), stone.Scale(0.7));
            }
            switch ((variant % 6 + 6) % 6)
            {
                case 0: Boulder(4, 12, 11, 13); Boulder(7, 8, 6, 7); break;
                case 1: Boulder(2, 21, 14, 5); Boulder(4, 18, 9, 4); break;
                case 2: Boulder(6, 7, 7, 19); Boulder(8, 3, 4, 7); break;
                case 3: Boulder(2, 17, 7, 9); Boulder(10, 12, 6, 14); break;
                case 4:
                    Boulder(2, 23, 4, 4); Boulder(7, 19, 5, 5);
                    Boulder(12, 23, 4, 3); Boulder(4, 15, 4, 4);
                    break;
                case 5: Boulder(2, 19, 14, 7); Boulder(4, 14, 10, 6); Boulder(6, 11, 6, 4); break;
            }
        }
        else
        {
            throw new ArgumentException("No pixel artwork registered for this feature.", nameof(type));
        }
        if (type.Name == DefaultFeatures.TreeName)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (pixels[i + 3] != 255 || pixels[i + 1] <= pixels[i]) continue;
                    double shade = 0.88 + CellHash.Pick(x / 3 + variant, y / 3, 4) * 0.06;
                    pixels[i] = (byte)Math.Clamp(pixels[i] * shade, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] * shade, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] * shade, 0, 255);
                }
        return new(width, height, lift, pixels);
    }

    public static int EdgeMask(GameMap map, int x, int y)
    {
        var terrain = map.TerrainAt(x, y);
        int mask = 0, bit = 1;
        foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
        {
            if (map.InBounds(x + dx, y + dy) && map.TerrainAt(x + dx, y + dy) != terrain) mask |= bit;
            bit <<= 1;
        }
        return mask;
    }
}
