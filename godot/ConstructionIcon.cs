using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public partial class ConstructionIcon : Control
{
    public string Glyph { get; init; } = "";
    public ServiceKind Kind { get; init; }
    public Color MainColor { get; init; }
    public MapOverlay? Overlay { get; init; }
    internal float Glow { get; private set; }
    internal float WiggleAngle { get; private set; }
    private Tween? _pulse;
    private Tween? _wiggle;

    internal static float WiggleAngleAt(float seconds)
    {
        if (seconds is <= 0 or >= 0.75f) return 0;
        float progress = seconds * 4;
        int step = (int)progress;
        float blend = (1 - Mathf.Cos((progress - step) * Mathf.Pi)) / 2;
        float start = step switch { 1 => -6, 2 => 6, _ => 0 };
        float end = step switch { 0 => -6, 1 => 6, _ => 0 };
        return Mathf.DegToRad(Mathf.Lerp(start, end, blend));
    }

    public void Hover()
    {
        Pulse();
        _wiggle?.Kill();
        WiggleAngle = 0;
        _wiggle = CreateTween();
        _wiggle.TweenMethod(Callable.From<float>(seconds =>
        {
            WiggleAngle = WiggleAngleAt(seconds);
            QueueRedraw();
        }), 0f, 0.75f, 0.75);
    }

    public void Pulse()
    {
        _pulse?.Kill();
        Glow = 1;
        QueueRedraw();
        _pulse = CreateTween();
        _pulse.TweenMethod(Callable.From<float>(value => { Glow = value; QueueRedraw(); }), 1f, 0f, 0.65);
        _pulse.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
    }

    public override void _Draw()
    {
        var center = Size / 2;
        float scale = Math.Min(Size.X, Size.Y) / 40;
        if (Glow > 0)
        {
            for (int ring = 3; ring >= 1; ring--)
                DrawCircle(center, (14 + ring * 3) * scale,
                    new Color(MainColor, Glow * 0.06f));
        }
        DrawSetTransform(center, WiggleAngle, Vector2.One * scale);
        var shadow = MainColor.Darkened(0.55f);
        var light = MainColor.Lightened(0.5f);
        if (Overlay == MapOverlay.Off)
        {
            DrawArc(Vector2.Zero, 15, 0, Mathf.Tau, 32, light, 2);
            DrawLine(new(-10, -10), new(10, 10), MainColor, 4);
            DrawLine(new(10, -10), new(-10, 10), MainColor, 4);
        }
        else if (Overlay == MapOverlay.Pollution)
        {
            DrawRect(new Rect2(-14, 3, 28, 14), shadow);
            DrawRect(new Rect2(-10, -5, 7, 22), MainColor);
            DrawRect(new Rect2(3, -1, 7, 18), MainColor);
            DrawCircle(new(-7, -10), 5, light);
            DrawCircle(new(0, -14), 6, new Color("#a0a0a0"));
            DrawCircle(new(9, -16), 5, new Color("#777777"));
        }
        else if (Overlay == MapOverlay.LandValue)
        {
            DrawColoredPolygon([new(-16, -2), new(0, -16), new(16, -2)], light);
            DrawRect(new Rect2(-11, -2, 22, 18), MainColor);
            DrawRect(new Rect2(-3, 6, 6, 10), shadow);
            DrawCircle(new(10, 10), 8, new Color("#ffd166"));
            DrawArc(new(10, 10), 5, 0, Mathf.Tau, 20, new Color("#a87545"), 1);
        }
        else if (Overlay == MapOverlay.Happiness)
        {
            DrawCircle(Vector2.One, 17, shadow);
            DrawCircle(Vector2.Zero, 16, MainColor);
            DrawCircle(new(-5, -4), 2, Colors.Black);
            DrawCircle(new(5, -4), 2, Colors.Black);
            DrawArc(new(0, 1), 9, 0.2f, Mathf.Pi - 0.2f, 16, Colors.Black, 2);
            DrawArc(Vector2.Zero, 13, Mathf.Pi, Mathf.Pi * 1.6f, 16, light, 1);
        }
        else if (Kind == ServiceKind.Power)
        {
            DrawLine(new(-13, 13), new(12, 7), shadow, 5);
            DrawLine(new(-12, 7), new(13, 13), MainColor, 5);
            DrawLine(new(-10, 6), new(12, 11), light, 1);
            DrawColoredPolygon([new(-12, 6), new(-14, -3), new(-7, -11), new(-5, -4),
                new(2, -18), new(9, -9), new(13, -1), new(10, 7), new(0, 11)], MainColor);
            DrawColoredPolygon([new(-6, 6), new(-4, -2), new(2, -9), new(7, 1),
                new(4, 8), new(0, 10)], light);
            DrawColoredPolygon([new(-2, 8), new(1, 0), new(4, 7), new(1, 10)], Colors.White);
        }
        else if (Kind == ServiceKind.Fire)
        {
            var timber = new Color("#a87545");
            var highlight = new Color("#e0bc83");
            var flag = new Color("#d9564b");
            DrawLine(new(-7, -3), new(-11, 17), timber, 3);
            DrawLine(new(7, -3), new(11, 17), timber, 3);
            DrawLine(new(-8, 4), new(10, 15), timber, 2);
            DrawLine(new(8, 4), new(-10, 15), timber, 2);
            DrawRect(new Rect2(-11, -10, 22, 8), timber);
            DrawRect(new Rect2(-7, -8, 14, 5), shadow);
            DrawLine(new(-11, -2), new(11, -2), highlight, 2);
            DrawColoredPolygon([new(-14, -10), new(0, -15), new(14, -10)], highlight);
            DrawLine(new(0, -15), new(0, -19), timber, 1);
            DrawColoredPolygon([new(1, -19), new(11, -17), new(1, -14)], flag);
        }
        else if (Kind == ServiceKind.Police)
        {
            var star = Enumerable.Range(0, 12).Select(i =>
            {
                float angle = i * Mathf.Pi / 6 - Mathf.Pi / 2;
                return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (i % 2 == 0 ? 17 : 8);
            }).ToArray();
            DrawColoredPolygon(star.Select(p => p + new Vector2(1, 2)).ToArray(), shadow);
            DrawColoredPolygon(star, MainColor);
            DrawCircle(Vector2.Zero, 5, light);
            DrawCircle(new(-1, -1), 2, Colors.White);
        }
        else if (Kind == ServiceKind.None)
        {
            var along = new Vector2(Mathf.Cos(Mathf.Pi / 6), 0.5f);
            var across = new Vector2(-along.X, along.Y);
            Vector2[] RoadSurface(float start, float end, float left, float right) =>
                [along * start + across * left, along * end + across * left,
                    along * end + across * right, along * start + across * right];
            DrawColoredPolygon(RoadSurface(-16, 16, -9, 9), MainColor);
            DrawColoredPolygon(RoadSurface(-16, 16, 0, 9), MainColor.Darkened(0.2f));
            DrawColoredPolygon([along * -16 - across * 9, along * 16 - across * 9,
                along * 12 - across * 6, along * -13 - across * 6], light);
            var divider = new Color("#f5dfaa");
            for (int dash = -1; dash <= 1; dash++)
                DrawColoredPolygon(RoadSurface(dash * 10 - 2.5f, dash * 10 + 2.5f, -0.8f, 0.8f), divider);
        }
        else if (Kind == ServiceKind.Water)
        {
            var drop = new[] { new Vector2(0, -18), new(-7, -8), new(-12, 1), new(-12, 8),
                new(-7, 14), new(0, 17), new(7, 14), new(12, 8), new(12, 1), new(7, -8) };
            DrawColoredPolygon(drop, MainColor);
            DrawColoredPolygon([new(0, -18), new(7, -8), new(12, 1), new(12, 8),
                new(7, 14), new(0, 17), new(4, 6), new(4, -4)], shadow);
            DrawLine(new(-7, 1), new(-7, 7), light, 3);
            DrawCircle(new(-5, 10), 2, Colors.White);
        }
        else if (Kind == ServiceKind.Health)
        {
            Vector2[] cross = [new(-5, -16), new(5, -16), new(5, -5), new(16, -5),
                new(16, 5), new(5, 5), new(5, 16), new(-5, 16), new(-5, 5),
                new(-16, 5), new(-16, -5), new(-5, -5)];
            DrawColoredPolygon(cross.Select(p => p + new Vector2(2, 2)).ToArray(), shadow);
            DrawColoredPolygon(cross, MainColor);
            DrawLine(new(-4, -14), new(-4, -4), light, 2);
            DrawLine(new(-14, -4), new(-4, -4), light, 2);
        }
        else if (Kind == ServiceKind.Education)
        {
            var paper = new Color("#f5dfaa");
            var bookmark = new Color("#d9564b");
            DrawColoredPolygon([new(-17, -12), new(-3, -9), new(0, -6), new(3, -9),
                new(17, -12), new(17, 13), new(3, 16), new(0, 13), new(-3, 16), new(-17, 13)], shadow);
            DrawColoredPolygon([new(-15, -14), new(-3, -11), new(0, -8), new(0, 11),
                new(-3, 13), new(-15, 10)], paper);
            DrawColoredPolygon([new(0, -8), new(3, -11), new(15, -14), new(15, 10),
                new(3, 13), new(0, 11)], paper);
            for (int y = -6; y <= 6; y += 6)
            {
                DrawLine(new(-12, y), new(-4, y + 2), MainColor, 1);
                DrawLine(new(4, y + 2), new(12, y), MainColor, 1);
            }
            DrawColoredPolygon([new(8, -12), new(11, -13), new(11, 3), new(9.5f, 1), new(8, 4)], bookmark);
        }
        else if (Kind == ServiceKind.Recreation)
        {
            DrawRect(new Rect2(-3, 3, 6, 15), new Color("#a87545"));
            DrawLine(new(-1, 6), new(-1, 16), light, 1);
            DrawCircle(new(-8, -3), 9, shadow);
            DrawCircle(new(8, -3), 9, MainColor);
            DrawCircle(new(0, -10), 10, MainColor);
            DrawArc(new(0, -10), 7, Mathf.Pi, Mathf.Pi * 1.7f, 12, light, 2);
        }
        else if (Kind == ServiceKind.Defence)
        {
            DrawColoredPolygon([new(-15, -15), new(0, -18), new(15, -15), new(13, 6),
                new(7, 13), new(0, 18), new(-7, 13), new(-13, 6)], shadow);
            DrawColoredPolygon([new(-12, -12), new(0, -15), new(12, -12), new(10, 5),
                new(0, 14), new(-10, 5)], MainColor);
            DrawColoredPolygon([new(-12, -12), new(0, -15), new(0, 14), new(-10, 5)], light);
            DrawLine(new(0, -10), new(0, 9), shadow, 2);
            DrawLine(new(-6, -3), new(6, -3), shadow, 2);
        }
        else if (Kind == ServiceKind.Faith)
        {
            DrawRect(new Rect2(-13, -1, 26, 18), MainColor);
            DrawRect(new Rect2(6, -1, 7, 18), shadow);
            DrawColoredPolygon([new(-17, 0), new(0, -12), new(17, 0)], new Color("#d9564b"));
            DrawRect(new Rect2(-4, 7, 8, 10), shadow);
            DrawRect(new Rect2(-2, -4, 4, 5), new Color("#70d7ff"));
            DrawLine(new(0, -19), new(0, -11), MainColor, 2);
            DrawLine(new(-3, -16), new(3, -16), MainColor, 2);
        }
        else if (Kind == ServiceKind.Trade)
        {
            DrawRect(new Rect2(-13, -3, 3, 20), shadow);
            DrawRect(new Rect2(10, -3, 3, 20), shadow);
            DrawRect(new Rect2(-15, 7, 30, 9), MainColor);
            DrawRect(new Rect2(-15, 7, 30, 2), light);
            var awningRed = new Color("#d9564b");
            DrawColoredPolygon([new(-13, -14), new(13, -14), new(17, -3), new(-17, -3)], awningRed);
            for (int x = -12; x <= 8; x += 10)
                DrawColoredPolygon([new(x, -14), new(x + 4, -14), new(x + 6, -3), new(x - 2, -3)], light);
            DrawCircle(new(-6, 4), 3, light);
            DrawCircle(new(3, 4), 3, shadow);
        }
        else if (Kind == ServiceKind.Granary)
        {
            var stalk = new Color("#79ad55");
            var tie = new Color("#a87545");
            void Kernel(Vector2 position, float angle, Color color)
            {
                var points = Enumerable.Range(0, 12).Select(i =>
                {
                    float phase = i * Mathf.Tau / 12;
                    return position + new Vector2(Mathf.Cos(phase) * 2.3f,
                        Mathf.Sin(phase) * 3.6f).Rotated(angle);
                }).ToArray();
                DrawColoredPolygon(points, color);
            }
            for (int stem = -1; stem <= 1; stem++)
            {
                float x = stem * 8;
                float top = Math.Abs(stem) * 4 - 16;
                var tip = new Vector2(x, top);
                float angle = Mathf.Atan2(x, 17 - top);
                DrawLine(tip, new(0, 17), stalk, 1.5f);
                Kernel(tip + new Vector2(0, -1).Rotated(angle), angle, light);
                for (int level = 0; level < 3; level++)
                {
                    float y = level * 5 + 3;
                    Kernel(tip + new Vector2(-2.8f, y).Rotated(angle), angle - 0.55f, MainColor);
                    Kernel(tip + new Vector2(2.8f, y).Rotated(angle), angle + 0.55f, light);
                }
            }
            DrawLine(new(-5, 12), new(5, 12), tie, 2);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
