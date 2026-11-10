using System.Numerics;

namespace TermCity.Core.Rendering;

public readonly record struct RockPart(Vector3 Center, Vector3 Size, int Color);

public static class RockArtwork
{
    public static IReadOnlyList<RockPart> Parts(int variant)
    {
        if (variant is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(variant));
        var result = new List<RockPart>();
        var stone = TerrainSprite.RockColor(variant);
        void Piece(float x, float y, float z, float w, float h, float d, double shade = 1)
        {
            var rgb = stone.Scale(shade);
            int color = rgb.R << 16 | rgb.G << 8 | rgb.B;
            if (w >= .3f && d >= .3f && h >= .2f)
            {
                result.Add(new(new(x, y + h * .4f, z), new(w, h * .8f, d), color));
                result.Add(new(new(x, y + h * .9f, z), new(w * .8f, h * .2f, d * .84f), color));
            }
            else result.Add(new(new(x, y + h / 2, z), new(w, h, d), color));
        }
        switch (variant)
        {
            case 0:
                Piece(0, 0, 0, .48f, .27f, .44f);
                Piece(-.07f, .27f, -.04f, .3f, .18f, .29f, 1.15);
                Piece(.23f, 0, .1f, .18f, .12f, .2f, .8);
                break;
            case 1:
                Piece(0, 0, 0, .72f, .12f, .48f);
                Piece(-.08f, .12f, -.04f, .5f, .07f, .34f, 1.2);
                break;
            case 2:
                Piece(0, 0, 0, .32f, .47f, .32f);
                Piece(.02f, .47f, -.02f, .22f, .2f, .23f, 1.15);
                break;
            case 3:
                Piece(-.2f, 0, .12f, .3f, .22f, .3f, .85);
                Piece(.2f, 0, -.08f, .33f, .4f, .32f);
                Piece(.18f, .4f, -.1f, .23f, .08f, .22f, 1.2);
                break;
            case 4:
                Piece(-.25f, 0, .2f, .2f, .1f, .22f);
                Piece(.08f, 0, .05f, .24f, .18f, .25f, 1.15);
                Piece(.27f, 0, -.2f, .18f, .12f, .21f, .85);
                Piece(-.18f, 0, -.23f, .19f, .15f, .18f);
                break;
            case 5:
                Piece(0, 0, 0, .72f, .15f, .32f, .85);
                Piece(-.05f, .15f, 0, .53f, .15f, .27f);
                Piece(-.12f, .3f, 0, .31f, .1f, .22f, 1.2);
                break;
        }
        return result;
    }
}
