using Godot;

namespace TermCity.GodotApp;

public partial class MnemonicButton : Button
{
    public int UnderlineIndex { get; set; } = -1;

    public override void _Draw()
    {
        if (UnderlineIndex < 0 || UnderlineIndex >= Text.Length) return;
        var font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        float left = GetThemeStylebox("normal").GetMargin(Side.Left);
        float available = Size.X - left - GetThemeStylebox("normal").GetMargin(Side.Right);
        float remaining = Math.Max(0, available - font.GetStringSize(Text, fontSize: size).X);
        left += Alignment switch
        {
            HorizontalAlignment.Center => remaining / 2,
            HorizontalAlignment.Right => remaining,
            _ => 0,
        };
        float x = left + font.GetStringSize(Text[..UnderlineIndex], fontSize: size).X;
        float width = font.GetStringSize(Text.Substring(UnderlineIndex, 1), fontSize: size).X;
        var style = GetThemeStylebox("normal");
        float top = style.GetMargin(Side.Top), bottom = style.GetMargin(Side.Bottom);
        float y = top + (Size.Y - top - bottom - font.GetHeight(size)) / 2 + font.GetAscent(size) + 1;
        float right = Size.X - style.GetMargin(Side.Right);
        if (!ClipText || x < right)
            DrawLine(new Vector2(x, y), new Vector2(ClipText ? Math.Min(x + width, right) : x + width, y),
                GetThemeColor("font_color"));
    }
}
