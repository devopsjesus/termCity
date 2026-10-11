using Godot;

namespace TermCity.GodotApp;

public partial class PauseStage : Control
{
    private int _fontSize = 16;
    private double _time = -1;
    internal IReadOnlyList<PauseGlyph> Glyphs { get; private set; } = [];

    public PauseStage()
    {
        Name = "PauseArt";
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
    }

    internal void SetTime(double seconds)
    {
        if (_time == seconds) return;
        _time = seconds;
        Glyphs = PauseRig.FrameAt(seconds);
        QueueRedraw();
    }

    internal void SetFontSize(int size)
    {
        if (_fontSize == size && CustomMinimumSize.X > 0) return;
        _fontSize = size;
        var font = GetThemeFont("font", "Label");
        CustomMinimumSize = new Vector2(
            font.GetStringSize("M", fontSize: size).X * PauseAnimation.Width,
            font.GetHeight(size) * PauseAnimation.Height);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = GetThemeFont("font", "Label");
        float cellWidth = font.GetStringSize("M", fontSize: _fontSize).X;
        float cellHeight = font.GetHeight(_fontSize);
        foreach (var glyph in Glyphs)
        {
            DrawSetTransform(new Vector2((float)glyph.X * cellWidth, (float)glyph.Y * cellHeight),
                (float)glyph.Rotation, Vector2.One * (float)glyph.Scale);
            float width = font.GetStringSize(glyph.Text, fontSize: _fontSize).X;
            DrawString(font, new Vector2(-width / 2, font.GetAscent(_fontSize) - cellHeight / 2), glyph.Text,
                fontSize: _fontSize, modulate: glyph.Part == PausePart.Caption ? new Color("#a0a0a0") : Colors.White);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
