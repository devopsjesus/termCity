using TermCity.Core.Util;

namespace TermCity.Core.World;

public enum ZoneType : byte
{
    None = 0,
    Residential = 1,
    Commercial = 2,
    Industrial = 3,
}

/// <summary>Display info for a zone. <c>WeeklyValue</c> is what a filled cell of this zone is worth per week before tax.</summary>
public sealed record ZoneInfo(
    ZoneType Type,
    string Name,
    char Letter,
    string EmptyGlyph,
    Rgb Foreground,
    Rgb Background,
    int WeeklyValue);

public static class Zones
{
    public static readonly ZoneInfo Residential = new(
        ZoneType.Residential, "Homesteads", 'R', "░", Rgb.Hex(0x58d068), Rgb.Hex(0x1f4a28), 200);

    public static readonly ZoneInfo Commercial = new(
        ZoneType.Commercial, "Marketplace", 'C', "░", Rgb.Hex(0x58a6ff), Rgb.Hex(0x1b3a66), 350);

    public static readonly ZoneInfo Industrial = new(
        ZoneType.Industrial, "Craftworks", 'I', "░", Rgb.Hex(0xf2c94c), Rgb.Hex(0x57481a), 500);

    public static readonly IReadOnlyList<ZoneType> Placeable = [ZoneType.Residential, ZoneType.Commercial, ZoneType.Industrial];

    public static ZoneInfo Get(ZoneType type) => type switch
    {
        ZoneType.Residential => Residential,
        ZoneType.Commercial => Commercial,
        ZoneType.Industrial => Industrial,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>People living in one residential cell.</summary>
public readonly record struct Household(byte Adults, byte Children, byte Seniors)
{
    public int Total => Adults + Children + Seniors;

    public bool IsEmpty => Total == 0;

    // Weights are tuned so the averages are 2.0 adults and 2.0 children per family,
    // with roughly one family in three having seniors (and two seniors not being unusual).
    private static readonly int[] AdultWeights = [0, 20, 60, 20];
    private static readonly int[] ChildWeights = [15, 20, 30, 20, 15];
    private static readonly int[] SeniorWeights = [65, 15, 20];

    // People who move to a growing city skew young: more adults and children, few seniors.
    private static readonly int[] MigrantAdultWeights = [0, 25, 60, 15];
    private static readonly int[] MigrantChildWeights = [25, 25, 30, 15, 5];
    private static readonly int[] MigrantSeniorWeights = [88, 9, 3];

    public static Household Migrant(GameRandom rng) => new(
        (byte)rng.Weighted(MigrantAdultWeights),
        (byte)rng.Weighted(MigrantChildWeights),
        (byte)rng.Weighted(MigrantSeniorWeights));

    /// <summary>The same mix of ages at another size; at least one person survives a positive factor.</summary>
    public Household Scaled(double factor)
    {
        if (factor <= 0 || IsEmpty)
        {
            return default;
        }

        int a = Round(Adults * factor), c = Round(Children * factor), s = Round(Seniors * factor);
        if (a + c + s == 0)
        {
            a = 1;
        }

        return new Household((byte)Math.Min(a, 255), (byte)Math.Min(c, 255), (byte)Math.Min(s, 255));

        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }

    public Household Plus(Household other) => new(
        (byte)Math.Min(255, Adults + other.Adults),
        (byte)Math.Min(255, Children + other.Children),
        (byte)Math.Min(255, Seniors + other.Seniors));

    /// <summary>Removes up to <paramref name="people"/> people, chosen at random across the ages present.</summary>
    public Household Without(int people, GameRandom rng)
    {
        int a = Adults, c = Children, s = Seniors;
        for (int n = 0; n < people && a + c + s > 0; n++)
        {
            int pick = rng.Next(a + c + s);
            if (pick < a)
            {
                a--;
            }
            else if (pick < a + c)
            {
                c--;
            }
            else
            {
                s--;
            }
        }

        return new Household((byte)a, (byte)c, (byte)s);
    }

    public static Household Random(GameRandom rng) => new(
        (byte)rng.Weighted(AdultWeights),
        (byte)rng.Weighted(ChildWeights),
        (byte)rng.Weighted(SeniorWeights));
}
