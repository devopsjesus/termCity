using Godot;

namespace TermCity.GodotApp;

public partial class TerminalFrame : PanelContainer
{
    public const int Inset = 12;

    public bool JoinAdjacent { get; init; } = true;
    internal float? VisibleContentPadding { get; init; }
    internal float? VisibleVerticalContentPadding { get; init; }

    private Rect2 _borderBounds;
    private bool _joinLeft, _joinRight, _joinTop, _joinBottom;
    private Rect2[] _branchGaps = [];

    internal Rect2 BorderBounds => _borderBounds;
    internal bool JoinLeft => _joinLeft;
    internal bool JoinRight => _joinRight;
    internal bool JoinTop => _joinTop;
    internal bool JoinBottom => _joinBottom;
    internal (float Left, float Right) HorizontalEndpoints(bool joinedEdge)
    {
        var bounds = _borderBounds.HasArea() ? _borderBounds : new Rect2(Vector2.Zero, Size);
        return (
            joinedEdge && !_joinLeft ? InnerBorderBounds.Position.X : bounds.Position.X + (_joinLeft ? -1.5f : 0.5f),
            joinedEdge && !_joinRight ? InnerBorderBounds.End.X : bounds.End.X + (_joinRight ? 2.5f : -0.5f));
    }
    internal Rect2 InnerBorderBounds
    {
        get
        {
            var bounds = _borderBounds.HasArea() ? _borderBounds : new Rect2(Vector2.Zero, Size);
            var start = bounds.Position + new Vector2(_joinLeft ? 2.5f : 4.5f, _joinTop ? 2.5f : 4.5f);
            var end = bounds.End - new Vector2(_joinRight ? 1.5f : 4.5f, _joinBottom ? 1.5f : 4.5f);
            return new Rect2(start, end - start);
        }
    }

    public TerminalFrame()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var style = new StyleBoxFlat { BgColor = Colors.Black };
        style.SetContentMarginAll(Inset);
        AddThemeStyleboxOverride("panel", style);
    }

    public override void _Ready()
    {
        ItemRectChanged += ScheduleBorderRefresh;
        VisibilityChanged += ScheduleBorderRefresh;
        ScheduleBorderRefresh();
    }

    private void ScheduleBorderRefresh() => Callable.From(RefreshConnectedBorders).CallDeferred();

    private void RefreshConnectedBorders()
    {
        if (!IsInsideTree()) return;
        var frames = Frames(GetTree().Root).Where(frame => frame.IsVisibleInTree()).ToArray();
        var rectangles = frames.Select(frame => frame.GetGlobalRect()).ToArray();
        var groups = frames.Select(BorderGroup).ToArray();
        for (int index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            var rect = rectangles[index];
            float left = rect.Position.X, right = rect.End.X, top = rect.Position.Y, bottom = rect.End.Y;
            bool joinLeft = false, joinRight = false, joinTop = false, joinBottom = false;
            for (int otherIndex = 0; otherIndex < rectangles.Length; otherIndex++)
            {
                if (!frame.JoinAdjacent || !frames[otherIndex].JoinAdjacent ||
                    otherIndex == index || groups[otherIndex] != groups[index]) continue;
                var other = rectangles[otherIndex];
                bool overlapY = Math.Min(rect.End.Y, other.End.Y) - Math.Max(rect.Position.Y, other.Position.Y) > Inset;
                bool overlapX = Math.Min(rect.End.X, other.End.X) - Math.Max(rect.Position.X, other.Position.X) > Inset;
                if (overlapY && Adjacent(rect.Position.X - other.End.X))
                {
                    left = (rect.Position.X + other.End.X) / 2;
                    joinLeft = true;
                }
                if (overlapY && Adjacent(other.Position.X - rect.End.X))
                {
                    right = (rect.End.X + other.Position.X) / 2;
                    joinRight = true;
                }
                if (overlapX && Adjacent(rect.Position.Y - other.End.Y))
                {
                    top = (rect.Position.Y + other.End.Y) / 2;
                    joinTop = true;
                }
                if (overlapX && Adjacent(other.Position.Y - rect.End.Y))
                {
                    bottom = (rect.End.Y + other.Position.Y) / 2;
                    joinBottom = true;
                }
            }
            var bounds = new Rect2(new Vector2(left, top) - rect.Position, new Vector2(right - left, bottom - top));
            if (frame._borderBounds == bounds && frame._joinLeft == joinLeft && frame._joinRight == joinRight &&
                frame._joinTop == joinTop && frame._joinBottom == joinBottom) continue;
            frame._borderBounds = bounds;
            frame._joinLeft = joinLeft;
            frame._joinRight = joinRight;
            frame._joinTop = joinTop;
            frame._joinBottom = joinBottom;
            if (frame.VisibleContentPadding is { } padding)
            {
                var inner = frame.InnerBorderBounds;
                var style = frame.GetThemeStylebox("panel");
                // Panel outlines occupy their final pixel inside the control bounds.
                style.ContentMarginLeft = inner.Position.X - 0.5f + padding;
                style.ContentMarginRight = frame.Size.X - inner.End.X - 0.5f + padding;
                style.ContentMarginTop = inner.Position.Y - 0.5f + padding;
                style.ContentMarginBottom = frame.Size.Y - inner.End.Y - 0.5f + padding;
            }
            if (frame.VisibleVerticalContentPadding is { } verticalPadding)
            {
                var inner = frame.InnerBorderBounds;
                var style = frame.GetThemeStylebox("panel");
                // Keep the minimum height stable while borders join during deferred layout.
                float totalMargin = verticalPadding * 2 + 5;
                float topMargin = Mathf.Ceil((totalMargin + inner.Position.Y -
                    (frame.Size.Y - inner.End.Y)) / 2 - 0.01f);
                float bottomMargin = totalMargin - topMargin;
                if (style.ContentMarginTop != topMargin) style.ContentMarginTop = topMargin;
                if (style.ContentMarginBottom != bottomMargin) style.ContentMarginBottom = bottomMargin;
            }
            frame.QueueRedraw();
        }
        var gaps = new List<(Node? Group, Rect2 Gap)>();
        foreach (var frame in frames)
        {
            var bounds = frame._borderBounds;
            foreach (bool topEdge in new[] { true, false })
            {
                if (topEdge ? !frame._joinTop : !frame._joinBottom) continue;
                float y = frame.GlobalPosition.Y + (topEdge ? bounds.Position.Y + 2.5f : bounds.End.Y - 1.5f);
                if (frame._joinLeft)
                    gaps.Add((BorderGroup(frame), new Rect2(frame.GlobalPosition.X + bounds.Position.X - 1.5f, y, 4, 0)));
                if (frame._joinRight)
                    gaps.Add((BorderGroup(frame), new Rect2(frame.GlobalPosition.X + bounds.End.X - 1.5f, y, 4, 0)));
            }
        }
        foreach (var frame in frames)
        {
            var group = BorderGroup(frame);
            var localGaps = gaps.Where(gap => gap.Group == group)
                .Select(gap => new Rect2(gap.Gap.Position - frame.GlobalPosition, gap.Gap.Size)).ToArray();
            if (frame._branchGaps.SequenceEqual(localGaps)) continue;
            frame._branchGaps = localGaps;
            frame.QueueRedraw();
        }

        static bool Adjacent(float gap) => gap >= -0.01f && gap <= Inset + 0.01f;
    }

    private static IEnumerable<TerminalFrame> Frames(Node node)
    {
        if (node is TerminalFrame frame) yield return frame;
        foreach (var child in node.GetChildren())
            foreach (var descendant in Frames(child)) yield return descendant;
    }

    private static Node? BorderGroup(TerminalFrame frame)
    {
        for (var parent = frame.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is TerminalFrame || parent is Control { MouseFilter: MouseFilterEnum.Stop } and not Container)
                return parent;
        return null;
    }

    internal static StyleBoxTexture CreateTooltipStyle()
    {
        const int size = 32;
        using var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(Colors.Black);
        foreach (int inset in new[] { 0, 4 })
        {
            int end = size - inset - 1;
            for (int pixel = inset; pixel <= end; pixel++)
            {
                image.SetPixel(pixel, inset, Colors.White);
                image.SetPixel(pixel, end, Colors.White);
                image.SetPixel(inset, pixel, Colors.White);
                image.SetPixel(end, pixel, Colors.White);
            }
        }
        var style = new StyleBoxTexture { Texture = ImageTexture.CreateFromImage(image) };
        style.SetContentMarginAll(Inset);
        foreach (var side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
            style.SetTextureMargin(side, Inset);
        return style;
    }

    public override void _Draw()
    {
        var bounds = _borderBounds.HasArea() ? _borderBounds : new Rect2(Vector2.Zero, Size);
        float left = bounds.Position.X + (_joinLeft ? -1.5f : 0.5f);
        var inner = InnerBorderBounds;
        float leftInner = inner.Position.X;
        float right = bounds.End.X + (_joinRight ? 2.5f : -0.5f);
        float rightInner = inner.End.X;
        float top = bounds.Position.Y + (_joinTop ? -1.5f : 0.5f);
        float topInner = inner.Position.Y;
        float bottom = bounds.End.Y + (_joinBottom ? 2.5f : -0.5f);
        float bottomInner = inner.End.Y;
        var topEnds = HorizontalEndpoints(_joinTop);
        var bottomEnds = HorizontalEndpoints(_joinBottom);
        DrawHorizontal(topEnds.Left, topEnds.Right, top);
        DrawHorizontal(leftInner, rightInner, topInner);
        DrawHorizontal(bottomEnds.Left, bottomEnds.Right, bottom);
        DrawHorizontal(leftInner, rightInner, bottomInner);
        DrawLine(new(left, _joinLeft ? topInner : top),
            new(left, _joinLeft ? bottomInner : bottom), Colors.White);
        DrawLine(new(leftInner, topInner), new(leftInner, bottomInner), Colors.White);
        DrawLine(new(right, _joinRight ? topInner : top),
            new(right, _joinRight ? bottomInner : bottom), Colors.White);
        DrawLine(new(rightInner, topInner), new(rightInner, bottomInner), Colors.White);
    }

    private void DrawHorizontal(float left, float right, float y)
    {
        float start = left;
        foreach (var gap in _branchGaps.Where(gap => Math.Abs(gap.Position.Y - y) < 0.01f)
            .OrderBy(gap => gap.Position.X))
        {
            if (gap.End.X <= start || gap.Position.X >= right) continue;
            if (gap.Position.X > start) DrawLine(new(start, y), new(gap.Position.X, y), Colors.White);
            start = Math.Max(start, gap.End.X);
        }
        if (start < right) DrawLine(new(start, y), new(right, y), Colors.White);
    }
}
