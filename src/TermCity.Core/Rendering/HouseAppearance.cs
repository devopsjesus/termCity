using System.Numerics;
using TermCity.Core.Util;
using TermCity.Core.Buildings;

namespace TermCity.Core.Rendering;

public enum HouseStyle
{
    ThatchCottage,
    TimberGable,
    StoneCroft,
    Burgage,
    Longhouse,
    WeaversHouse,
    Courtyard,
    Shanty,
    WallHouse,
}

public sealed record HouseAppearance(float Width, float Depth, float Height, float RoofRise,
    int WallColor, int RoofColor, HouseStyle Style, bool CrosswiseRoof, bool Wing, bool Chimney)
{
    public const int TextureSize = 32;
    public const int TextureClusterSize = 4;
    public const float VoxelSpan = 0.20f;
    public const float WallCoreInset = 0.03f;
    public const int FrameColor = 0x4f3828;
    public const int TimberColor = 0x68503b;
    public const int StoneColor = 0x99917c;
    public const int SillColor = 0xb3a58a;
    public const int YardEarthColor = 0x76613f;
    public const int GardenSoilColor = 0x493b29;
    public const int GardenLeafColor = 0x426f37;
    public const int GardenCropColor = 0x829b45;
    public const int YardGrassColor = 0x608046;
    public const int YardGrassShadeColor = 0x536f3d;
    public const int YardGrassLightColor = 0x6b884b;
    public const int FlowerRoseColor = 0xbf786a;
    public const int FlowerGoldColor = 0xd4b465;
    public const int FlowerCreamColor = 0xe0d4ac;

    public int RoofCapColor => RoofColor switch
    {
        0xb2a273 => 0xc0ae7e, 0x9e875e => 0xad9364, 0x8d6450 => 0xa5775b, _ => 0x7b8985,
    };

    public int PlasterColor => BlockColor(WallColor, 0.96);
    public int RoofBlockColor => BlockColor(RoofColor, 0.96);
    public int RoofBlockShadeColor => BlockColor(RoofColor, 0.86);

    private static int BlockColor(int color, double shade)
    {
        var rgb = Rgb.Hex(color).Scale(shade);
        return rgb.R << 16 | rgb.G << 8 | rgb.B;
    }

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
        int floors = Math.Clamp(level, 1, 3);
        var style = (HouseStyle)shape;
        return new(shape switch { 0 => 0.66f, 1 => 0.64f, 2 => 0.70f, 3 => 0.66f, 4 => 0.72f, _ => 0.68f },
            shape switch { 0 => 0.64f, 1 => 0.72f, 2 => 0.64f, 3 => 0.70f, 4 => 0.60f, _ => 0.68f },
            (floors == 1 ? 0.44f : 0.64f + (floors - 2) * 0.04f) + (shape % 2) * 0.04f,
            shape is 1 or 3 or 5 ? 0.32f : 0.24f,
        wall, roof, style, shape is 3 or 5, shape is 2 or 5, level > 1 || shape is 3 or 4);
    }

    public static HouseAppearance At(BuildingType type, int x, int y)
    {
        var basis = At(x, y, type.Level);
        return type.Name switch
        {
            "Timber Home" => basis with { Style = HouseStyle.TimberGable, WallColor = 0xbaa98a },
            "Stone Home" => basis with { Style = HouseStyle.StoneCroft, WallColor = StoneColor, Chimney = true },
            "Courtyard House" => basis with
            {
                Style = HouseStyle.Courtyard, Width = 0.82f, Depth = 0.80f, Height = 0.53f,
                Wing = true, Chimney = true, RoofColor = 0x687574,
            },
            "Shanty Tower" => basis with
            {
                Style = HouseStyle.Shanty, Width = 0.80f, Depth = 0.78f, Height = 1.48f,
                RoofRise = 0.18f, WallColor = TimberColor, Wing = false, Chimney = false,
            },
            "Wall House" => basis with
            {
                Style = HouseStyle.WallHouse, Width = 0.82f, Depth = 0.82f, Height = 1.02f,
                RoofRise = 0.18f, WallColor = TimberColor, Wing = false, Chimney = false,
            },
            _ => basis,
        };
    }

    public HouseDimensions OnPlot(int width, int depth, int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        return new(width * Width, depth * Depth,
            Height, RoofRise, Style == HouseStyle.Shanty ? 4 : Style == HouseStyle.WallHouse ? 3 :
                level > 1 ? 2 : 1, Wing && width >= 2 && depth >= 2);
    }

    public IReadOnlyList<HouseBlock> BlocksOnPlot(int width, int depth, int level)
    {
        var dimensions = OnPlot(width, depth, level);
        if (Style == HouseStyle.Courtyard)
        {
            float wing = dimensions.Width * 0.27f;
            float back = dimensions.Depth * 0.30f;
            return [
                new(0, -(dimensions.Depth - back) / 2, dimensions.Width, back, Height, RoofRise, 1, false),
                new(-(dimensions.Width - wing) / 2, back / 2, wing, dimensions.Depth - back, Height, RoofRise, 1, true),
                new((dimensions.Width - wing) / 2, back / 2, wing, dimensions.Depth - back, Height, RoofRise, 1, true),
            ];
        }
        bool crosswise = depth > width || depth == width && CrosswiseRoof;
        if (!dimensions.Wing)
            return [new(0, 0, dimensions.Width, dimensions.Depth, dimensions.Height,
                dimensions.RoofRise, dimensions.Stories, crosswise)];

        float frontDepth = dimensions.Depth * 0.55f;
        float rearDepth = dimensions.Depth - frontDepth;
        float wingWidth = dimensions.Width * 0.5f;
        return [
            new(0, rearDepth / 2, dimensions.Width, frontDepth, dimensions.Height,
                dimensions.RoofRise, dimensions.Stories, false),
            new(-dimensions.Width / 2 + wingWidth / 2, -frontDepth / 2, wingWidth,
                rearDepth, 0.44f, 0.24f, 1, rearDepth > wingWidth),
        ];
    }

    public IReadOnlyList<HouseModelPart> Parts(HouseBlock block)
    {
        const float blockSpan = VoxelSpan, postWidth = BuildingDetailLayout.HalfTimberPostWidth;
        const float beamHeight = BuildingDetailLayout.HalfTimberBeamHeight;
        if (!float.IsFinite(block.Width) || block.Width < 0.4f ||
            !float.IsFinite(block.Depth) || block.Depth < 0.4f ||
            !float.IsFinite(block.Height) || block.Height <= 0 ||
            !float.IsFinite(block.RoofRise) || block.RoofRise < HouseBlock.RoofStepHeight ||
            block.Stories < 1 || block.Height / block.Stories <= beamHeight * 2)
            throw new ArgumentOutOfRangeException(nameof(block));
        var parts = new List<HouseModelPart>();
        int plaster = PlasterColor;
        int roofColor = RoofBlockColor, roofShade = RoofBlockShadeColor;
        foreach (int xSide in new[] { -1, 1 })
            foreach (int zSide in new[] { -1, 1 })
                parts.Add(new(new(xSide * (block.Width - postWidth) / 2, block.Height / 2,
                    zSide * (block.Depth - postWidth) / 2), new(postWidth, block.Height, postWidth), FrameColor, false));
        void Wall(bool alongX, int side)
        {
            float span = alongX ? block.Width : block.Depth;
            var bays = BuildingDetailLayout.HalfTimberFacade(span);
            Vector3 Position(float along, float y) => alongX
                ? new(along, y, side * (block.Depth - postWidth) / 2)
                : new(side * (block.Width - postWidth) / 2, y, along);
            Vector3 Size(float across, float tall, float thick) => alongX
                ? new(across, tall, thick) : new(thick, tall, across);
            for (int beam = 0; beam <= block.Stories; beam++)
            {
                float y = beam == 0 ? beamHeight / 2 : beam == block.Stories
                    ? block.Height - beamHeight / 2 : beam * block.Height / block.Stories;
                parts.Add(new(Position(0, y), Size(span - 2 * postWidth, beamHeight, postWidth), FrameColor, false));
            }
            for (int floor = 0; floor < block.Stories; floor++)
            {
                var panel = block.WallPanel(floor);
                float centerY = panel.Bottom + panel.Height / 2;
                for (int post = 1; post < bays.Count; post++)
                    parts.Add(new(Position(bays.Post(post), centerY),
                        Size(postWidth, panel.Height, postWidth), FrameColor, false));
                for (int bay = 0; bay < bays.Count; bay++)
                {
                    parts.Add(new(Position(bays.Center(bay), centerY),
                        Size(bays.Spacing - postWidth, panel.Height, 0.06f), plaster, false));
                }
            }
        }
        Wall(true, -1); Wall(true, 1); Wall(false, -1); Wall(false, 1);
        for (int step = 0; step < block.RoofSteps; step++)
        {
            var course = block.RoofCourse(step);
            float width = course.Size.X, depth = course.Size.Z;
            int columns = (int)MathF.Ceiling(width / blockSpan);
            int rows = (int)MathF.Ceiling(depth / blockSpan);
            float cellWidth = width / columns, cellDepth = depth / rows;
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    int color = step == block.RoofSteps - 1 ? RoofCapColor
                        : CellHash.Pick(column + step * 7, row + (int)Style * 11, 4) == 0
                            ? roofShade : roofColor;
                    parts.Add(new(new(-width / 2 + (column + 0.5f) * cellWidth,
                            course.Center.Y,
                            -depth / 2 + (row + 0.5f) * cellDepth),
                        new(cellWidth, HouseBlock.RoofStepHeight, cellDepth), color, true));
                }
        }
        return parts;
    }
}

public readonly record struct HouseDimensions(float Width, float Depth, float Height,
    float RoofRise, int Stories, bool Wing);

public readonly record struct HouseModelPart(Vector3 Center, Vector3 Size, int Color, bool Roof);

public readonly record struct HouseBlock(float X, float Z, float Width, float Depth,
    float Height, float RoofRise, int Stories, bool CrosswiseRoof)
{
    public const float RoofStepHeight = 0.08f;
    public int RoofSteps => (int)MathF.Round(RoofRise / RoofStepHeight);

    public (float Bottom, float Height) WallPanel(int floor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(floor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(floor, Stories);
        const float beam = BuildingDetailLayout.HalfTimberBeamHeight;
        float storyHeight = Height / Stories;
        float bottom = floor * storyHeight + (floor == 0 ? beam : beam / 2);
        float top = (floor + 1) * storyHeight - (floor == Stories - 1 ? beam : beam / 2);
        return (bottom, top - bottom);
    }

    public (Vector3 Center, Vector3 Size) RoofCourse(int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(step, RoofSteps);
        float fraction = (RoofSteps - step) / (float)RoofSteps;
        return (new(0, Height + (step + 0.5f) * RoofStepHeight, 0),
            new((Width + 0.08f) * (CrosswiseRoof ? fraction : 1),
                RoofStepHeight, (Depth + 0.08f) * (CrosswiseRoof ? 1 : fraction)));
    }
}
