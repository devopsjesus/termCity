using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class PalisadeArtworkTests
{
    [Theory]
    [InlineData(0.55f, 0.55f)]
    [InlineData(1.68f, 0.78f)]
    [InlineData(3.82f, 2.82f)]
    [InlineData(5.82f, 4.82f)]
    public void LogsAreVariedSupportedAndStayInsideTheOriginalWalls(float width, float depth)
    {
        var parts = PalisadeArtwork.Parts(width, depth);
        Assert.Equal(parts.ToArray(), PalisadeArtwork.Parts(width, depth).ToArray());
        var wood = parts.Where(p => p.Color != PalisadeArtwork.BraceColor).ToArray();
        var logs = wood.Where(p => PalisadeArtwork.IsBarkColor(p.Color) &&
            Math.Abs(p.Center.Y - p.Size.Y / 2 - 0.005f) < 0.00001f).ToArray();
        Assert.True(logs.Select(p => p.Color).Distinct().Count() >= 2);
        Assert.True(logs.Select(p => p.Size.Y).Distinct().Count() >= 6);
        Assert.True(logs.Select(p => p.Size.X).Distinct().Count() >= 6);
        foreach (var part in parts)
        {
            Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
            Assert.True(Math.Abs(part.Center.X) + part.Size.X / 2 <= width / 2 + 0.09001f);
            Assert.True(Math.Abs(part.Center.Z) + part.Size.Z / 2 <= depth / 2 + 0.09001f);
            Assert.InRange(part.Center.Y - part.Size.Y / 2, 0f, 0.91f);
            Assert.True(part.Center.Y + part.Size.Y / 2 <= 0.91f);
        }
        foreach (var part in wood)
        {
            float bottom = part.Center.Y - part.Size.Y / 2;
            if (bottom < 0.00501f) continue;
            Assert.Contains(wood, support =>
                Math.Abs(support.Center.Y + support.Size.Y / 2 - bottom) < 0.00001f &&
                Math.Abs(support.Center.X - part.Center.X) < (support.Size.X + part.Size.X) / 2 - 0.00001f &&
                Math.Abs(support.Center.Z - part.Center.Z) < (support.Size.Z + part.Size.Z) / 2 - 0.00001f);
        }
        Assert.Contains(wood, part => PalisadeArtwork.IsBarkColor(part.Color) &&
            part.Center.Y - part.Size.Y / 2 > 0.2f && part.Size.Y > 0.1f);
        var ends = wood.Where(p => p.Color is PalisadeArtwork.CutColor or PalisadeArtwork.CutShadeColor).ToArray();
        Assert.NotEmpty(ends);
        Assert.All(ends, end => Assert.Equal(0.008f, end.Size.Y, 5));
        Assert.True(ends.Max(p => p.Center.Y) - ends.Min(p => p.Center.Y) > 0.10f);
        for (int i = 0; i < wood.Length; i++)
            for (int j = i + 1; j < wood.Length; j++)
                Assert.True(Math.Abs(wood[i].Center.X - wood[j].Center.X) + 0.00001f >=
                    (wood[i].Size.X + wood[j].Size.X) / 2 ||
                    Math.Abs(wood[i].Center.Y - wood[j].Center.Y) + 0.00001f >=
                    (wood[i].Size.Y + wood[j].Size.Y) / 2 ||
                    Math.Abs(wood[i].Center.Z - wood[j].Center.Z) + 0.00001f >=
                    (wood[i].Size.Z + wood[j].Size.Z) / 2);
        var braces = parts.Where(p => p.Color == PalisadeArtwork.BraceColor).ToArray();
        for (int i = 0; i < braces.Length; i++)
            for (int j = i + 1; j < braces.Length; j++)
                Assert.True(Math.Abs(braces[i].Center.Y - braces[j].Center.Y) > 0.001f ||
                    Math.Abs(braces[i].Center.X - braces[j].Center.X) + 0.00001f >=
                    (braces[i].Size.X + braces[j].Size.X) / 2 ||
                    Math.Abs(braces[i].Center.Z - braces[j].Center.Z) + 0.00001f >=
                    (braces[i].Size.Z + braces[j].Size.Z) / 2);
    }

    [Fact]
    public void BarkIsDarkerThanTheOldWallsAndHasCoarseVerticalGrain()
    {
        var old = Rgb.Hex(0x79634b);
        foreach (int color in new[] { PalisadeArtwork.BarkColor, PalisadeArtwork.BarkShadeColor,
            PalisadeArtwork.BarkLightColor, PalisadeArtwork.WeatheredColor, PalisadeArtwork.DarkBarkColor })
        {
            var bark = Rgb.Hex(color);
            Assert.True(bark.R < old.R && bark.G < old.G && bark.B < old.B);
        }
        var pixels = PalisadeArtwork.MaterialTexture(PalisadeArtwork.BarkColor);
        Assert.Equal(pixels, PalisadeArtwork.MaterialTexture(PalisadeArtwork.BarkColor));
        Assert.Equal(32 * 32 * 4, pixels.Length);
        Assert.True(pixels.Where((_, i) => i % 4 == 0).Distinct().Count() >= 4);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int i = (y * 32 + x) * 4, cluster = ((y / 4 * 4) * 32 + x / 4 * 4) * 4;
                Assert.Equal(pixels[cluster], pixels[i]);
                Assert.True(pixels[i] > pixels[i + 1] && pixels[i + 1] > pixels[i + 2]);
                Assert.True(pixels[i] <= Rgb.Hex(PalisadeArtwork.BarkColor).R);
                Assert.Equal(255, pixels[i + 3]);
            }
        Assert.Throws<ArgumentOutOfRangeException>(() => PalisadeArtwork.MaterialTexture(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PalisadeArtwork.Parts(float.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PalisadeArtwork.Parts(1, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(5183)]
    public void BuildingSeedChangesGirthChinksAndCrownProfilesButNeverOnRedraw(int seed)
    {
        var parts = PalisadeArtwork.Parts(1.68f, 0.78f, seed).ToArray();
        Assert.Equal(parts, PalisadeArtwork.Parts(1.68f, 0.78f, seed).ToArray());
        Assert.False(parts.SequenceEqual(PalisadeArtwork.Parts(1.68f, 0.78f, seed + 1)));
        var bark = parts.Where(p => PalisadeArtwork.IsBarkColor(p.Color)).ToArray();
        Assert.True(bark.Select(p => p.Color).Distinct().Count() >= 4);
        Assert.Contains(bark, part => part.Center.Y - part.Size.Y / 2 > 0.2f && part.Size.Y > 0.1f);
    }
}
