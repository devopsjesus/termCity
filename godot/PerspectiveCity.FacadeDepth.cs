using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private void FacadedBody(Vector3 center, float width, float depth, float height, int stories,
        int wallColor, bool timber, Pos anchor, bool plinth = true, bool entrance = true)
    {
        var appearance = new HouseAppearance(width, depth, height, .32f, wallColor, 0x956b50,
            timber ? HouseStyle.TimberGable : HouseStyle.StoneCroft, false, false, false);
        var block = new HouseBlock(0, 0, width, depth, height, .32f, Math.Clamp(stories, 1, 3), false);
        // Keep the plinth inside sill backs so exposed top faces never coincide.
        if (plinth)
            Box(center + Vector3.Up * .04f, new(width - .03f, .08f, depth - .03f),
                HouseAppearance.StoneColor, anchor);
        foreach (var part in DepthStudyArtwork.HouseParts(appearance, block, entrance, includeRoof: false))
        {
            int color = !timber && part.Color == appearance.PlasterColor ? wallColor :
                !timber && part.Color == HouseAppearance.FrameColor ? HouseAppearance.StoneColor :
                !timber && part.Color == HouseAppearance.SillColor ? 0xc6bf9c : part.Color;
            Box(center + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                new(part.Size.X, part.Size.Y, part.Size.Z), color, anchor);
        }
    }
}
