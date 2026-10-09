using Godot;

namespace TermCity.GodotApp;

public partial class TerminalFrame : PanelContainer
{
    public const int Inset = 12;

    public TerminalFrame()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var style = new StyleBoxFlat { BgColor = Colors.Black };
        style.SetContentMarginAll(Inset);
        AddThemeStyleboxOverride("panel", style);
    }

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        DrawRect(new Rect2(new Vector2(0.5f, 0.5f), Size - Vector2.One), Colors.White, filled: false);
        DrawRect(new Rect2(new Vector2(4.5f, 4.5f), Size - Vector2.One * 9), Colors.White, filled: false);
    }
}
