using Godot;

namespace TermCity.GodotApp;

public partial class TerminalFrame : PanelContainer
{
    private const string Frames = "terminal_frames";

    public TerminalFrame()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var style = new StyleBoxFlat { BgColor = Colors.Black };
        style.SetContentMarginAll(12);
        AddThemeStyleboxOverride("panel", style);
    }

    public override void _Ready()
    {
        AddToGroup(Frames);
        Resized += RefreshBorders;
    }

    private void RefreshBorders()
    {
        foreach (var node in GetTree().GetNodesInGroup(Frames))
            if (node is TerminalFrame frame) frame.QueueRedraw();
    }

    private float Adjacent(Side side)
    {
        var bounds = GetGlobalRect();
        foreach (var node in GetTree().GetNodesInGroup(Frames))
        {
            if (node is not TerminalFrame other || other == this || !other.IsVisibleInTree() ||
                IsAncestorOf(other) || other.IsAncestorOf(this)) continue;
            var peer = other.GetGlobalRect();
            bool vertical = side is Side.Left or Side.Right;
            float overlap = vertical
                ? Math.Min(bounds.End.Y, peer.End.Y) - Math.Max(bounds.Position.Y, peer.Position.Y)
                : Math.Min(bounds.End.X, peer.End.X) - Math.Max(bounds.Position.X, peer.Position.X);
            float gap = side switch
            {
                Side.Left => bounds.Position.X - peer.End.X,
                Side.Right => peer.Position.X - bounds.End.X,
                Side.Top => bounds.Position.Y - peer.End.Y,
                _ => peer.Position.Y - bounds.End.Y,
            };
            if (overlap > 1 && gap is >= -0.5f and <= 8.5f) return Math.Max(0, gap);
        }
        return -1;
    }

    public override void _Draw()
    {
        float left = Adjacent(Side.Left), right = Adjacent(Side.Right);
        float top = Adjacent(Side.Top), bottom = Adjacent(Side.Bottom);
        float x0 = left >= 0 ? -2 : 0.5f, x1 = right >= 0 ? Size.X + 2 : Size.X - 0.5f;
        float y0 = top >= 0 ? -2 : 0.5f, y1 = bottom >= 0 ? Size.Y + 2 : Size.Y - 0.5f;
        void H(float y) => DrawLine(new Vector2(x0, y), new Vector2(x1, y), Colors.White);
        void V(float x) => DrawLine(new Vector2(x, y0), new Vector2(x, y1), Colors.White);
        if (top < 0) { H(0.5f); H(4.5f); } else H(2);
        if (bottom < 0) { H(Size.Y - 0.5f); H(Size.Y - 4.5f); } else H(Size.Y - 2);
        if (left < 0) { V(0.5f); V(4.5f); } else if (left < 1) V(2);
        if (right < 0) { V(Size.X - 0.5f); V(Size.X - 4.5f); }
        else if (right < 1) V(Size.X - 2);
        else { V(Size.X + 2); V(Size.X + right - 2); }
    }
}
