using Godot;

namespace TermCity.GodotApp;

public partial class ResourceGauge : Control
{
    internal const int BarHeight = 14;
    internal const double MinimumLevel = 0.5, MaximumLevel = 1.5;
    private double _level;
    internal double Level => _level;
    internal double MarkerPosition => Math.Clamp(
        (_level - MinimumLevel) / (MaximumLevel - MinimumLevel), 0, 1);

    public ResourceGauge()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, BarHeight + 4);
    }

    internal void SetLevel(double level)
    {
        _level = level;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Size.X < 8) return;
        var bounds = new Rect2(0, (Size.Y - BarHeight) / 2, Size.X, BarHeight);
        DrawRect(bounds, Colors.Black);
        float interior = Size.X;
        foreach (var (from, to, color) in new[]
        {
            (MinimumLevel, 0.95, new Color("#ff7777")),
            (0.95, 1.05, new Color("#ffe066")),
            (1.05, MaximumLevel, new Color("#88ee99")),
        })
            DrawRect(new Rect2(interior * (float)((from - MinimumLevel) / (MaximumLevel - MinimumLevel)), bounds.Position.Y,
                interior * (float)((to - from) / (MaximumLevel - MinimumLevel)), BarHeight), color with { A = 0.6f });
        float markerX = (interior - 3) * (float)MarkerPosition;
        DrawRect(new Rect2(markerX, bounds.Position.Y - 2, 3, BarHeight + 4), Colors.White);
    }
}
