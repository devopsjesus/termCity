using TermCity.Core.Effects;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.GodotApp;

namespace TermCity.Tests.Effects;

public class EffectDrawTests
{
    private static readonly Rgb Green = new(10, 200, 10);
    private static readonly Rgb Black = new(0, 0, 0);
    private static readonly Rgb Red = new(250, 0, 0);
    private static readonly CellVisual Visual = new("H", Green, Black);
    private const int W = 12, H = 22;
    private static readonly (float X, float Y) NoShake = (0f, 0f);

    [Fact]
    public void IdentityEffectDrawsTheCellGlyphAtItsCentre()
    {
        EffectDraw.PlanCell(CellEffect.Identity, Visual, 3, 2, W, H, NoShake, out var main, out var overlay);
        Assert.Null(overlay);
        Assert.NotNull(main);
        Assert.Equal("H", main!.Value.Glyph);
        Assert.Equal(3.5f * W, main.Value.CentreX);
        Assert.Equal(2.5f * H, main.Value.CentreY);
        Assert.Equal(1f, main.Value.Scale);
        Assert.Equal(Green, main.Value.Color);
    }

    [Fact]
    public void ScaleOffsetAlphaAndReplacementGlyphAreApplied()
    {
        var effect = new CellEffect(Glyph: "x", Scale: 0.5f, OffsetX: 0.25f, OffsetY: -0.5f, Alpha: 0.4f);
        EffectDraw.PlanCell(effect, Visual, 1, 1, W, H, NoShake, out var main, out _);
        Assert.Equal("x", main!.Value.Glyph);
        Assert.Equal(0.5f, main.Value.Scale);
        Assert.Equal(0.4f, main.Value.Alpha);
        Assert.Equal((1.5f + 0.25f) * W, main.Value.CentreX);
        Assert.Equal((1.5f - 0.5f) * H, main.Value.CentreY);
    }

    [Fact]
    public void ShakeMovesTheGlyphInPixels()
    {
        EffectDraw.PlanCell(CellEffect.Identity, Visual, 0, 0, W, H, (3f, -2f), out var main, out _);
        Assert.Equal(0.5f * W + 3f, main!.Value.CentreX);
        Assert.Equal(0.5f * H - 2f, main.Value.CentreY);
        Assert.Equal((6f, -11f), EffectDraw.ShakePixels((0.5f, -0.5f), W, H));
    }

    [Fact]
    public void TintBlendsTheForegroundOnlyWhenThereIsAnAmount()
    {
        var tinted = new CellEffect(Tint: Red, TintAmount: 1f);
        EffectDraw.PlanCell(tinted, Visual, 0, 0, W, H, NoShake, out var main, out _);
        Assert.Equal(Red, main!.Value.Color);

        var untinted = new CellEffect(Tint: Red, TintAmount: 0f);
        EffectDraw.PlanCell(untinted, Visual, 0, 0, W, H, NoShake, out main, out _);
        Assert.Equal(Green, main!.Value.Color);

        Assert.Equal(Green, EffectDraw.Tinted(Green, null, 1f));
        Assert.Equal(Red, EffectDraw.Tinted(Green, Red, 5f));
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.04f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(1f, 0.01f)]
    public void InvisibleGlyphsAreSkipped(float scale, float alpha)
    {
        var effect = new CellEffect(Scale: scale, Alpha: alpha);
        EffectDraw.PlanCell(effect, Visual, 0, 0, W, H, NoShake, out var main, out var overlay);
        Assert.Null(main);
        Assert.Null(overlay);
        Assert.False(EffectDraw.IsVisible(scale, alpha));
    }

    [Fact]
    public void OverlayEffectsKeepTheCellGlyphAndAddAGhost()
    {
        var effect = new CellEffect(Glyph: "*", Scale: 0.8f, OffsetY: -1f, Alpha: 0.5f, Overlay: true, Tint: Red, TintAmount: 1f);
        EffectDraw.PlanCell(effect, Visual, 4, 5, W, H, NoShake, out var main, out var overlay);
        Assert.Equal("H", main!.Value.Glyph);
        Assert.Equal(1f, main.Value.Alpha);
        Assert.Equal(4.5f * W, main.Value.CentreX);
        Assert.Equal(5.5f * H, main.Value.CentreY);
        Assert.Equal("*", overlay!.Value.Glyph);
        Assert.Equal(Red, overlay.Value.Color);
        Assert.Equal(4.5f * H, overlay.Value.CentreY);
    }

    [Fact]
    public void InvisibleOverlayStillLeavesTheBaseGlyph()
    {
        var effect = new CellEffect(Glyph: "*", Scale: 0f, Overlay: true);
        EffectDraw.PlanCell(effect, Visual, 0, 0, W, H, NoShake, out var main, out var overlay);
        Assert.NotNull(main);
        Assert.Null(overlay);
    }

    [Fact]
    public void BackgroundTintIsBlended()
    {
        var plain = new CellEffect();
        Assert.Equal(Black, EffectDraw.Background(Black, plain));
        var tinted = new CellEffect(BackgroundTint: Red, BackgroundAmount: 1f);
        Assert.Equal(Red, EffectDraw.Background(Black, tinted));
        var half = EffectDraw.Background(Black, new CellEffect(BackgroundTint: Red, BackgroundAmount: 0.5f));
        Assert.InRange(half.R, 100, 150);
    }

    [Fact]
    public void GlyphOriginPutsTheBaselineRelativeToTheCellCentre()
    {
        var (x, y) = EffectDraw.GlyphOrigin(W, H, 17f);
        Assert.Equal(-6f, x);
        Assert.Equal(6f, y);
    }

    [Fact]
    public void SpritesArePlacedRelativeToTheViewOrigin()
    {
        var sprite = new EffectSprite("o", 12.5f, 7.5f, Red, 0.9f, 0.7f);
        var draw = EffectDraw.PlanSprite(sprite, 10, 5, W, H, NoShake, 54, 22);
        Assert.NotNull(draw);
        Assert.Equal(2.5f * W, draw!.Value.CentreX);
        Assert.Equal(2.5f * H, draw.Value.CentreY);
        Assert.Equal(0.9f, draw.Value.Scale);
        Assert.Equal(0.7f, draw.Value.Alpha);
        Assert.Equal(Red, draw.Value.Color);
    }

    [Fact]
    public void OffScreenAndInvisibleSpritesAreCulled()
    {
        EffectSprite At(float x, float y, float scale = 1f, float alpha = 1f) => new("o", x, y, Red, scale, alpha);
        Assert.Null(EffectDraw.PlanSprite(At(-3, 5), 0, 0, W, H, NoShake, 54, 22));
        Assert.Null(EffectDraw.PlanSprite(At(5, -3), 0, 0, W, H, NoShake, 54, 22));
        Assert.Null(EffectDraw.PlanSprite(At(60, 5), 0, 0, W, H, NoShake, 54, 22));
        Assert.Null(EffectDraw.PlanSprite(At(5, 30), 0, 0, W, H, NoShake, 54, 22));
        Assert.Null(EffectDraw.PlanSprite(At(5, 5, scale: 0f), 0, 0, W, H, NoShake, 54, 22));
        Assert.Null(EffectDraw.PlanSprite(At(5, 5, alpha: 0f), 0, 0, W, H, NoShake, 54, 22));
        Assert.NotNull(EffectDraw.PlanSprite(At(-0.5f, 0.5f), 0, 0, W, H, NoShake, 54, 22));
    }

    [Fact]
    public void SpritesFollowTheShake()
    {
        var sprite = new EffectSprite("o", 1f, 1f, Red);
        var draw = EffectDraw.PlanSprite(sprite, 0, 0, W, H, (4f, 5f), 54, 22);
        Assert.Equal(W + 4f, draw!.Value.CentreX);
        Assert.Equal(H + 5f, draw.Value.CentreY);
    }

    [Fact]
    public void EveryEffectFrameYieldsPlansThatStayWithinSaneBounds()
    {
        // Drives real effects through the planner: nothing should come out NaN, hugely scaled or wildly off-screen.
        var system = new EffectSystem(7) { View = new CellRect(0, 0, 54, 22) };
        var ghosts = new List<GhostCell> { new(new Pos(5, 5), "█", Red), new(new Pos(6, 5), "█", Red) };
        system.Spawn(new DemolishEffect(ghosts, (5.5, 5.5), 1));
        system.Spawn(new FireEffect(ghosts, 2));
        system.Spawn(new QuakeEffect([new Pos(5, 5)], 3));
        system.Spawn(new ConfettiEffect((10, 10), 60, 4));
        for (int frame = 0; frame < 120; frame++)
        {
            system.Update(1.0 / 30);
            var shake = EffectDraw.ShakePixels(system.Shake, W, H);
            foreach (var cell in system.ActiveCells())
            {
                EffectDraw.PlanCell(cell.Effect, Visual, cell.X, cell.Y, W, H, shake, out var main, out var overlay);
                foreach (var plan in new[] { main, overlay })
                {
                    if (plan is not { } p) continue;
                    Assert.False(float.IsNaN(p.CentreX) || float.IsNaN(p.CentreY));
                    Assert.InRange(p.Scale, EffectDraw.MinScale, 2f);
                    Assert.InRange(p.Alpha, 0f, 1f);
                }
            }

            foreach (var sprite in system.Sprites)
            {
                if (EffectDraw.PlanSprite(sprite, 0, 0, W, H, shake, 54, 22) is { } p)
                {
                    Assert.False(float.IsNaN(p.CentreX) || float.IsNaN(p.CentreY));
                    Assert.InRange(p.Scale, EffectDraw.MinScale, 2f);
                }
            }
        }
    }
}
