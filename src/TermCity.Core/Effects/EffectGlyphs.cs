using TermCity.Core.Util;

namespace TermCity.Core.Effects;

/// <summary>
/// Every glyph and colour the effects draw with, in one place so the bundled-font check in the tests can verify them
/// all and a theme change touches one file. Everything is terminal-style text; there are no image assets.
/// </summary>
public static class EffectGlyphs
{
    /// <summary>Build-up bars, lowest to highest.</summary>
    public const string BuildBars = "▁▂▃▄▅▆▇█";

    public static readonly string[] Dust = ["·", "∙", "◦"];
    public static readonly string[] Sparkle = ["✦", "✧", "·"];
    public static readonly string[] Flame = ["^", "▲", "♦"];
    public static readonly string[] Smoke = ["∙", "◦", "○", "°"];
    public static readonly string[] Ripple = ["~", "≈", "∼"];
    public static readonly string[] Confetti = ["▪", "◆", "●", "✦", "◇", "▫"];
    public static readonly string[] Coin = ["$", "¢"];
    public static readonly string[] Bird = ["v", "^"];
    public const string FishRight = ">";
    public const string FishLeft = "<";
    public const string WhaleBack = "∩";
    public static readonly string[] Spout = ["°", "∙"];
    public static readonly string[] Bubble = ["◦", "○", "°"];
    public const string Person = "·";
    public const string Car = "▪";
    public const string Crack = "/";

    public static readonly Rgb DustColor = Rgb.Hex(0xb8b0a0);
    public static readonly Rgb ScaffoldColor = Rgb.Hex(0xe8c872);
    public static readonly Rgb SparkleColor = Rgb.Hex(0xfff2a8);
    public static readonly Rgb PenColor = Rgb.Hex(0xffffff);
    public static readonly Rgb Flame1 = Rgb.Hex(0xff5a1f);
    public static readonly Rgb Flame2 = Rgb.Hex(0xffb02e);
    public static readonly Rgb SmokeColor = Rgb.Hex(0x8a8a90);
    public static readonly Rgb WaterColor = Rgb.Hex(0x4f9fe8);
    public static readonly Rgb WaterBackground = Rgb.Hex(0x1d4f8a);
    public static readonly Rgb OutbreakColor = Rgb.Hex(0x7bd05a);
    public static readonly Rgb AbandonColor = Rgb.Hex(0x707078);
    public static readonly Rgb CoinColor = Rgb.Hex(0xf5d442);
    public static readonly Rgb CarColor = Rgb.Hex(0xe0e0e8);
    public static readonly Rgb PersonColor = Rgb.Hex(0xf0e0c0);
    public static readonly Rgb BirdColor = Rgb.Hex(0xc8c8d0);
    public static readonly Rgb FishColor = Rgb.Hex(0xd8ecff);
    public static readonly Rgb WhaleColor = Rgb.Hex(0xc4d4e4);
    public static readonly Rgb SpoutColor = Rgb.Hex(0xeaf6ff);
    public static readonly Rgb BubbleColor = Rgb.Hex(0xd0ebff);

    public static readonly Rgb[] ConfettiColors =
    [
        Rgb.Hex(0xff5d73), Rgb.Hex(0xffd166), Rgb.Hex(0x06d6a0), Rgb.Hex(0x4cc9f0), Rgb.Hex(0xc77dff), Rgb.Hex(0xffffff),
    ];

    /// <summary>Every glyph an effect can draw, for verifying them against the bundled font.</summary>
    public static IEnumerable<string> All()
    {
        foreach (char c in BuildBars)
        {
            yield return c.ToString();
        }

        foreach (var set in new[] { Dust, Sparkle, Flame, Smoke, Ripple, Confetti, Coin, Bird, Spout, Bubble, new[] { Person, Car, Crack, FishRight, FishLeft, WhaleBack } })
        {
            foreach (string glyph in set)
            {
                yield return glyph;
            }
        }
    }
}
