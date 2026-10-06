using TermCity.Core.Util;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace TermCity.App;

internal static class Colors
{
    public static Color ToColor(Rgb c) => new(c.R, c.G, c.B);

    public static Attribute Attr(Rgb foreground, Rgb background) => new(ToColor(foreground), ToColor(background));

    public static readonly Rgb PanelBackground = Rgb.Hex(0x14161c);
    public static readonly Rgb PanelText = Rgb.Hex(0xc8ccd4);
    public static readonly Rgb PanelDim = Rgb.Hex(0x7a808c);
    public static readonly Rgb Heading = Rgb.Hex(0xf2c94c);
    public static readonly Rgb Good = Rgb.Hex(0x58d068);
    public static readonly Rgb Bad = Rgb.Hex(0xff6b6b);
    public static readonly Rgb Accent = Rgb.Hex(0x58a6ff);
    public static readonly Rgb BarBackground = Rgb.Hex(0x2a2e38);
}
