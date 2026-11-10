using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public readonly record struct PalisadePart(Vector3 Center, Vector3 Size, int Color);
public readonly record struct PalisadeWallPart(WallFragment Fragment, int Color);

public static class PalisadeArtwork
{
    public const int BarkColor = 0x60432f;
    public const int BarkShadeColor = 0x513824;
    public const int BarkLightColor = 0x6a4b33;
    public const int WeatheredColor = 0x6f5840;
    public const int DarkBarkColor = 0x453526;
    public const int CutColor = 0x806044;
    public const int CutShadeColor = 0x6d5037;
    public const int BraceColor = 0x493423;
    public const int CharredColor = 0x302a23;
    public const int TextureSize = 32;
    public const float WallLogPitch = .75f;
    public const float TowerLogDepth = .65f;

    public static bool IsBarkColor(int rgb) =>
        rgb is BarkColor or BarkShadeColor or BarkLightColor or WeatheredColor or DarkBarkColor or CharredColor;

    public static bool IsTexturedColor(int rgb) =>
        IsBarkColor(rgb) || rgb is CutColor or CutShadeColor or BraceColor;

    public static int BarkAt(int index, int face, int seed = 0) =>
        CellHash.Pick(index + seed, face + 31, 5) switch
        {
            0 => BarkShadeColor,
            1 => BarkColor,
            2 => BarkLightColor,
            3 => WeatheredColor,
            _ => DarkBarkColor,
        };

    public static byte[] MaterialTexture(int rgb)
    {
        if (!IsTexturedColor(rgb)) throw new ArgumentOutOfRangeException(nameof(rgb));
        var pixels = new byte[TextureSize * TextureSize * 4];
        var color = Rgb.Hex(rgb);
        for (int y = 0; y < TextureSize; y++)
            for (int x = 0; x < TextureSize; x++)
            {
                int across = (rgb == BraceColor ? y : x) / 4;
                int down = (rgb == BraceColor ? x : y) / 4;
                double shade = 0.76 + CellHash.Pick(across, rgb, 7) * 0.03;
                int scarStart = CellHash.Pick(across + 13, rgb, 6);
                if (down >= scarStart && down <= scarStart + 2) shade -= 0.10;
                int knotX = CellHash.Pick(rgb, 17, 5) + 1, knotY = CellHash.Pick(rgb, 41, 5) + 1;
                int dx = across - knotX, dy = down - knotY;
                if (dx * dx * 2 + dy * dy < 5) shade -= 0.08;
                if (rgb is CutColor or CutShadeColor)
                    shade = 0.78 + CellHash.Pick(across / 2 + rgb, down / 2, 5) * 0.04;
                var patch = color.Scale(shade);
                int pixel = (y * TextureSize + x) * 4;
                pixels[pixel] = patch.R;
                pixels[pixel + 1] = patch.G;
                pixels[pixel + 2] = patch.B;
                pixels[pixel + 3] = 255;
            }
        return pixels;
    }

    private static void AppendLog(List<PalisadePart> parts, float x, float z, float sx, float sz,
        int index, int side, bool alongX, int seed, float verticalScale = 1, bool flatTop = false, bool grounded = false)
    {
        int face = side + (alongX ? 0 : 7);
        int Pick(int salt, int count) => CellHash.Pick(index + seed, face + salt, count);
        int color = BarkAt(index, face, seed);
        int endColor = Pick(19, 3) == 0 ? CutColor : CutShadeColor;
        float actualX = sx * (0.84f + Pick(23, 17) * 0.01f);
        float actualZ = sz * (0.88f + Pick(29, 13) * 0.01f);
        x += (sx - actualX) * (Pick(37, 11) - 5) / 10f;
        z += (sz - actualZ) * (Pick(43, 11) - 5) / 10f;
        float span = alongX ? actualX : actualZ, normal = alongX ? actualZ : actualX;
        float height = 0.70f + Pick(11, 25) * 0.005f;
        int crown = Pick(47, 5);
        void Add(float u, float bottom, float v, float du, float top, float dv, int rgb)
        {
            var center = alongX ? new Vector3(x + u, (bottom + top) / 2 * verticalScale, z + side * v)
                : new Vector3(x + side * v, (bottom + top) / 2 * verticalScale, z + u);
            var size = alongX ? new Vector3(du, (top - bottom) * verticalScale, dv)
                : new Vector3(dv, (top - bottom) * verticalScale, du);
            parts.Add(new(center, size, rgb));
        }
        for (int strip = -1; strip <= 1; strip++)
        {
            float u = strip * span * 0.41f;
            float du = span * (strip == 0 ? 0.64f : 0.18f);
            float dv = normal * (strip == 0 ? 1 : 0.72f);
            float rise = crown switch
            {
                0 => Pick(53 + strip, 4) * 0.008f,
                1 => (strip + 1) * 0.034f,
                2 => strip == 0 ? 0 : 0.055f + Pick(59 + strip, 3) * 0.008f,
                3 => strip == -1 ? 0 : 0.045f + Pick(61 + strip, 4) * 0.008f,
                _ => (1 - strip) * 0.028f + Pick(67 + strip, 3) * 0.006f,
            };
            float top = flatTop ? .91f : height + rise;
            float baseY = grounded ? 0 : .005f;
            bool chipped = strip == 0 && Pick(71, 3) == 0;
            if (chipped)
            {
                float notchBottom = 0.22f + Pick(73, 6) * 0.04f;
                float notchTop = notchBottom + 0.07f + Pick(79, 6) * 0.01f;
                Add(u, baseY, 0, du, notchBottom, dv, color);
                Add(u, notchBottom, -dv * 0.18f, du, notchTop, dv * 0.64f, color);
                Add(u, notchTop, 0, du, top - 0.008f, dv, color);
            }
            else Add(u, baseY, 0, du, top - 0.008f, dv, color);
            Add(u, top - 0.008f, 0, du, top, dv, endColor);
        }
    }

    public static IReadOnlyList<PalisadePart> Panel(float width, float depth, float height, int seed,
        bool roughTop, int interiorSide = -1, bool braces = true)
    {
        if (!float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(depth) || depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (interiorSide is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(interiorSide));
        var parts = new List<PalisadePart>();
        int count = Math.Max(1, (int)MathF.Ceiling(width / WallLogPitch));
        float pitch = width / count;
        for (int i = 0; i < count; i++)
            AppendLog(parts, (i - (count - 1) / 2f) * pitch, 0, pitch * .98f, depth * .84f,
                i, 1, true, seed, height / .91f, flatTop: !roughTop, grounded: true);
        if (braces && height > .6f && width > .5f)
            foreach (float fraction in new[] { .32f, .68f })
            {
                float thickness = Math.Min(.1f, depth * .16f);
                parts.Add(new(new(0, height * fraction, interiorSide * (depth - thickness) / 2),
                    new(width, Math.Min(.12f, height * .08f), thickness), BraceColor));
            }
        return parts;
    }

    public static IReadOnlyList<PalisadeWallPart> WallParts(WallComponent wall, IEnumerable<WallFragment> source)
    {
        if (wall.Material != WallMaterial.Wood)
            throw new ArgumentException("Palisade wall artwork requires timber defenses.", nameof(wall));
        int seed = CellHash.Pick(wall.Cell.X, wall.Cell.Y, 1_000_003);
        bool burned = WallDamageLayout.IsBurning(wall);
        int band = WallDamageLayout.DamageBand(wall);
        var result = new List<PalisadeWallPart>();
        void Add(WallFragment part, int color)
        {
            if (burned && part.Kind is not (WallFragmentKind.Grate or WallFragmentKind.DoorStud)) color = CharredColor;
            else if (band >= 4 && IsBarkColor(color))
                color = color == BarkLightColor ? BarkShadeColor : DarkBarkColor;
            result.Add(new(part, color));
        }
        foreach (var fragment in source)
        {
            if (fragment.Kind is not (WallFragmentKind.Body or WallFragmentKind.Merlon or WallFragmentKind.Door) ||
                fragment.Size.Y <= .18f)
            {
                Add(fragment, fragment.Kind switch
                {
                    WallFragmentKind.Trim => CutShadeColor,
                    WallFragmentKind.DoorBrace => BraceColor,
                    WallFragmentKind.DoorStud => 0xc6bf9c,
                    WallFragmentKind.Grate => 0x343b3e,
                    _ => BarkShadeColor,
                });
                continue;
            }
            float bottom = fragment.Center.Y - fragment.Size.Y / 2;
            bool rough = fragment.Kind == WallFragmentKind.Merlon;
            int panelSeed = seed + (int)MathF.Round(fragment.Center.X * 97 + fragment.Center.Z * 193);
            void PanelAt(Vector3 offset, float width, float depth, bool alongX, int side)
            {
                foreach (var part in Panel(width, depth, fragment.Size.Y, panelSeed, rough, side,
                    braces: fragment.Kind == WallFragmentKind.Body))
                {
                    var center = alongX ? part.Center : new Vector3(part.Center.Z, part.Center.Y, part.Center.X);
                    var size = alongX ? part.Size : new Vector3(part.Size.Z, part.Size.Y, part.Size.X);
                    Add(new(fragment.Center + offset + center - Vector3.UnitY * (fragment.Size.Y / 2),
                        size, fragment.Kind), fragment.Kind == WallFragmentKind.Door ? BarkShadeColor : part.Color);
                }
            }
            if (wall.IsTower && fragment.Kind == WallFragmentKind.Body &&
                fragment.Size.Z > .8f && bottom < WallDamageLayout.WalkHeight(wall) - .2f)
            {
                const float skin = TowerLogDepth;
                float half = WallDamageLayout.Width(wall) / 2;
                foreach (int side in new[] { -1, 1 })
                {
                    if (Math.Abs(fragment.Center.Z + side * fragment.Size.Z / 2 - side * half) < .001f)
                        PanelAt(new(0, 0, side * (half - skin / 2) - fragment.Center.Z),
                            fragment.Size.X, skin, true, -side);
                    if (Math.Abs(fragment.Center.X + side * fragment.Size.X / 2 - side * half) < .001f)
                    {
                        float a = Math.Max(fragment.Center.Z - fragment.Size.Z / 2, -half + skin);
                        float b = Math.Min(fragment.Center.Z + fragment.Size.Z / 2, half - skin);
                        if (b > a)
                        {
                            float thickness = Math.Min(skin, fragment.Size.X);
                            PanelAt(new(side * (half - thickness / 2) - fragment.Center.X, 0,
                                (a + b) / 2 - fragment.Center.Z), b - a, thickness, false, -side);
                        }
                    }
                }
            }
            else
            {
                bool alongX = !wall.IsTower || fragment.Size.X >= fragment.Size.Z;
                float coordinate = alongX ? fragment.Center.Z : fragment.Center.X;
                int inside = wall.IsTower ? coordinate < 0 ? 1 : -1 : -(int)WallDamageLayout.ExteriorSide(wall);
                PanelAt(Vector3.Zero, alongX ? fragment.Size.X : fragment.Size.Z,
                    alongX ? fragment.Size.Z : fragment.Size.X, alongX, inside);
            }
        }
        return result;
    }

    public static IReadOnlyList<PalisadePart> Parts(float width, float depth, int seed = 0)
    {
        if (!float.IsFinite(width) || width < 0.4f) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(depth) || depth < 0.4f) throw new ArgumentOutOfRangeException(nameof(depth));
        const float thickness = 0.18f;
        var parts = new List<PalisadePart>();
        void Log(float x, float z, float sx, float sz, int index, int side, bool alongX) =>
            AppendLog(parts, x, z, sx, sz, index, side, alongX, seed);
        int frontCount = (int)Math.Ceiling((width + thickness) / thickness);
        float frontPitch = (width + thickness) / frontCount;
        for (int i = 0; i < frontCount; i++)
            foreach (int side in new[] { -1, 1 })
                Log((i - (frontCount - 1) / 2f) * frontPitch, side * depth / 2,
                    frontPitch - 0.008f, thickness, i, side, true);
        int sideCount = (int)Math.Ceiling((depth - thickness) / thickness);
        float sidePitch = (depth - thickness) / sideCount;
        for (int i = 0; i < sideCount; i++)
            foreach (int side in new[] { -1, 1 })
                Log(side * width / 2, (i - (sideCount - 1) / 2f) * sidePitch,
                    thickness, sidePitch - 0.008f, i, side, false);
        foreach (float y in new[] { 0.26f, 0.60f })
            foreach (int side in new[] { -1, 1 })
            {
                parts.Add(new(new(0, y, side * (depth / 2 - 0.11f)), new(width - 0.27f, 0.065f, 0.05f), BraceColor));
                parts.Add(new(new(side * (width / 2 - 0.11f), y, 0), new(0.05f, 0.065f, depth - thickness), BraceColor));
            }
        return parts;
    }
}
