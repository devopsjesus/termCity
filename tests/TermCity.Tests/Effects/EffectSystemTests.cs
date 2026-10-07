using TermCity.Core.Effects;
using TermCity.Core.Util;

namespace TermCity.Tests.Effects;

public class EffectSystemTests
{
    private static readonly Rgb Red = new(200, 30, 30);

    private static GhostCell Ghost(int x, int y) => new(new Pos(x, y), "█", Red);

    private static void Run(EffectSystem system, double seconds, double dt = 1.0 / 60)
    {
        for (double t = 0; t < seconds; t += dt)
        {
            system.Update(dt);
        }
    }

    [Fact]
    public void IdentityIsNotTheDefaultStruct()
    {
        Assert.True(CellEffect.Identity.IsIdentity);
        Assert.Equal(1f, CellEffect.Identity.Scale);
        Assert.Equal(1f, CellEffect.Identity.Alpha);
        Assert.Equal(0f, default(CellEffect).Scale);
    }

    [Fact]
    public void CombineMultipliesScaleAndAlphaAndAddsOffsets()
    {
        var a = new CellEffect(Scale: 0.5f, OffsetX: 1f, Alpha: 0.5f);
        var b = new CellEffect(Glyph: "x", Scale: 0.5f, OffsetX: 2f, OffsetY: 1f, Alpha: 0.5f, Overlay: true);
        var c = CellEffect.Combine(a, b);
        Assert.Equal(0.25f, c.Scale);
        Assert.Equal(0.25f, c.Alpha);
        Assert.Equal(3f, c.OffsetX);
        Assert.Equal(1f, c.OffsetY);
        Assert.Equal("x", c.Glyph);
        Assert.True(c.Overlay);
    }

    [Fact]
    public void CombineKeepsTheStrongerTint()
    {
        var weak = new CellEffect(Tint: new Rgb(1, 1, 1), TintAmount: 0.2f);
        var strong = new CellEffect(Tint: new Rgb(9, 9, 9), TintAmount: 0.8f);
        Assert.Equal(new Rgb(9, 9, 9), CellEffect.Combine(weak, strong).Tint);
        Assert.Equal(new Rgb(9, 9, 9), CellEffect.Combine(strong, weak).Tint);
    }

    [Fact]
    public void UntouchedCellsReportIdentity()
    {
        var system = new EffectSystem(1);
        system.Update(0.016);
        Assert.True(system.CellAt(3, 3).IsIdentity);
        Assert.False(system.TryGetCell(3, 3, out _));
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void EffectsFinishAndLeaveNothingBehind()
    {
        var system = new EffectSystem(1);
        system.Spawn(new DemolishEffect([Ghost(5, 5), Ghost(6, 5), Ghost(7, 6)], (6.5, 5.5), 1));
        system.Spawn(new ConstructEffect([new TintedCell(new Pos(2, 2), Red)], ConstructStyle.BuildUp, 2));
        system.Spawn(new ConfettiEffect((10, 10), 30, 3));
        system.Spawn(new CoinsEffect([new Pos(1, 1), new Pos(2, 2)], 4));
        system.Spawn(new QuakeEffect([new Pos(1, 1)], 5));
        system.Spawn(new FloodEffect([new Pos(3, 3)], 6));
        system.Spawn(new FireEffect([Ghost(8, 8)], 7), 0.2);
        system.Spawn(new CollapseEffect([new Pos(8, 8)], 8), 3);
        system.Spawn(new FadeEffect([new Pos(4, 4)], Red));
        system.Spawn(new ZoneRippleEffect([new TintedCell(new Pos(0, 0), Red), new TintedCell(new Pos(9, 0), Red)], (0, 0)));
        system.Spawn(new RoadStrokeEffect([new RoadCell(new Pos(0, 9), Red), new RoadCell(new Pos(1, 9), Red)]));
        Assert.True(system.ActiveEffects > 0);

        Run(system, 8);

        Assert.Equal(0, system.ActiveEffects);
        Assert.Equal(0, system.CellCount);
        Assert.Equal(0, system.SpriteCount);
        Assert.False(system.HasVisuals);
        Assert.Equal((0f, 0f), system.Shake);
        Assert.Empty(system.ActiveCells());
        Assert.True(system.CellAt(5, 5).IsIdentity);
    }

    [Fact]
    public void UpdateReportsOneLastRedrawWhenVisualsVanish()
    {
        var system = new EffectSystem(1);
        system.Spawn(new CoinsEffect([new Pos(1, 1)], 1));
        bool sawRedraw = false, last = false;
        for (int i = 0; i < 400; i++)
        {
            system.Update(0.016);
            sawRedraw |= system.NeedsRedraw;
            last = system.NeedsRedraw;
        }

        Assert.True(sawRedraw);
        Assert.False(last);
    }

    [Fact]
    public void DemolishShrinksMonotonicallyToZero()
    {
        var system = new EffectSystem(1);
        var demolish = new DemolishEffect([Ghost(10, 10), Ghost(14, 10), Ghost(12, 13)], (12.5, 11.5), 9);
        system.Spawn(demolish);
        var last = new Dictionary<Pos, float>();
        bool reachedZero = false;
        for (int i = 0; i < 200; i++)
        {
            system.Update(1.0 / 60);
            foreach (var p in new[] { new Pos(10, 10), new Pos(14, 10), new Pos(12, 13) })
            {
                float scale = system.TryGetCell(p.X, p.Y, out var effect) ? effect.Scale : 0f;
                if (last.TryGetValue(p, out float before))
                {
                    Assert.True(scale <= before + 1e-6f, $"{p} grew from {before} to {scale}");
                }

                last[p] = scale;
                reachedZero |= scale == 0f;
            }
        }

        Assert.True(reachedZero);
        Assert.All(last.Values, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void DemolishStartsAtFullSizeAndDriftsTowardTheCentre()
    {
        var system = new EffectSystem(1);
        system.Spawn(new DemolishEffect([Ghost(10, 10)], (14.5, 10.5), 1));
        system.Update(0.001);
        var start = system.CellAt(10, 10);
        Assert.Equal(1f, start.Scale, 2);
        Assert.Equal("█", start.Glyph);
        Assert.True(start.Overlay);

        system.Update(0.4);
        var mid = system.CellAt(10, 10);
        Assert.True(mid.Scale < 1f);
        Assert.True(mid.OffsetX > 0f, "glyph moves toward the centre (to the right)");
        Assert.True(Math.Abs(mid.OffsetY) < 1e-4f, "centre is on the same row");
        Assert.True(mid.OffsetX <= 4f);
    }

    [Fact]
    public void DemolishDriftsTowardTheCentreFromEveryDirection()
    {
        var system = new EffectSystem(1);
        var cells = new[] { Ghost(8, 10), Ghost(16, 10), Ghost(12, 6), Ghost(12, 14) };
        system.Spawn(new DemolishEffect(cells, (12.5, 10.5), 1));
        system.Update(0.35);
        Assert.True(system.CellAt(8, 10).OffsetX > 0);
        Assert.True(system.CellAt(16, 10).OffsetX < 0);
        Assert.True(system.CellAt(12, 6).OffsetY > 0);
        Assert.True(system.CellAt(12, 14).OffsetY < 0);
    }

    [Fact]
    public void DemolishFarCellsStartBeforeNearOnes()
    {
        var system = new EffectSystem(1);
        var demolish = new DemolishEffect([Ghost(0, 0), Ghost(10, 10), Ghost(20, 20)], (10.5, 10.5), 1);
        system.Spawn(demolish);
        system.Update(0.05);
        Assert.True(demolish.CellProgress(0) > demolish.CellProgress(1));
        Assert.Equal(demolish.CellProgress(0), demolish.CellProgress(2), 6);
    }

    [Fact]
    public void DemolishSpawnsDustThatSpiralsInward()
    {
        var system = new EffectSystem(1);
        system.Spawn(new DemolishEffect([Ghost(0, 0), Ghost(1, 0), Ghost(0, 1)], (10, 10), 1));
        double firstDistance = 0;
        for (int i = 0; i < 60 && firstDistance == 0; i++)
        {
            system.Update(0.02);
            if (system.Sprites.Count > 0)
            {
                var s = system.Sprites[0];
                firstDistance = Math.Sqrt((s.X - 10) * (s.X - 10) + (s.Y - 10) * (s.Y - 10));
            }
        }

        Assert.True(firstDistance > 0, "dust appeared");
        double later = firstDistance;
        for (int i = 0; i < 20; i++)
        {
            system.Update(0.02);
        }

        var tracked = system.Sprites.OrderBy(s => (s.X - 10) * (s.X - 10) + (s.Y - 10) * (s.Y - 10)).ToList();
        Assert.NotEmpty(tracked);
        Assert.True(Math.Sqrt((tracked[0].X - 10) * (tracked[0].X - 10) + (tracked[0].Y - 10) * (tracked[0].Y - 10)) < later);
    }

    [Fact]
    public void ConstructHidesTheCellThenRevealsItAtFullSize()
    {
        var system = new EffectSystem(1);
        var effect = new ConstructEffect([new TintedCell(new Pos(5, 5), Red)], ConstructStyle.Grow, 3);
        system.Spawn(effect);
        float last = -1;
        float peak = 0;
        for (int i = 0; i < 150; i++)
        {
            system.Update(1.0 / 60);
            float scale = system.TryGetCell(5, 5, out var c) ? c.Scale : 1f;
            Assert.True(scale >= last - 1e-6f, "grow never shrinks");
            last = scale;
            peak = Math.Max(peak, scale);
        }

        Assert.Equal(1f, last);
        Assert.Equal(0, system.ActiveEffects);
        Assert.True(peak <= 1f);
    }

    [Fact]
    public void BuildUpClimbsTheBlockCharacters()
    {
        var system = new EffectSystem(1);
        system.Spawn(new ConstructEffect([new TintedCell(new Pos(5, 5), Red)], ConstructStyle.BuildUp, 3));
        var seen = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            system.Update(1.0 / 60);
            if (system.TryGetCell(5, 5, out var c) && c.Glyph is { } g && (seen.Count == 0 || seen[^1] != g))
            {
                seen.Add(g);
            }
        }

        Assert.True(seen.Count >= 4);
        var order = seen.Select(g => EffectGlyphs.BuildBars.IndexOf(g[0])).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void RoadStrokeRevealsCellsInOrder()
    {
        var system = new EffectSystem(1);
        var cells = Enumerable.Range(0, 6).Select(i => new RoadCell(new Pos(i, 0), Red)).ToList();
        var stroke = new RoadStrokeEffect(cells);
        system.Spawn(stroke);
        system.Update(stroke.RevealTime(3) + 0.01);
        Assert.True(system.CellAt(5, 0).Alpha == 0f, "later cells are still hidden");
        Assert.True(system.CellAt(4, 0).Alpha == 0f);
        Assert.True(system.CellAt(0, 0).IsIdentity || system.CellAt(0, 0).Alpha > 0f);
        Assert.True(system.CellAt(3, 0).TintAmount > 0f, "the pen head is bright");
        Assert.NotEmpty(system.Sprites);
        Run(system, 3);
        Assert.Equal(0, system.CellCount);
    }

    [Fact]
    public void OrderPathFollowsAStraightStroke()
    {
        var shuffled = new[] { new Pos(3, 0), new Pos(0, 0), new Pos(2, 0), new Pos(1, 0), new Pos(4, 0) };
        var path = RoadStrokeEffect.OrderPath(shuffled);
        Assert.Equal(5, path.Count);
        for (int i = 1; i < path.Count; i++)
        {
            Assert.Equal(1, Math.Abs(path[i].X - path[i - 1].X) + Math.Abs(path[i].Y - path[i - 1].Y));
        }

        Assert.True(path[0].X is 0 or 4);
    }

    [Fact]
    public void OrderPathHandlesGapsAndHugeInputs()
    {
        var gap = RoadStrokeEffect.OrderPath([new Pos(0, 0), new Pos(1, 0), new Pos(9, 0)]);
        Assert.Equal(3, gap.Count);
        var big = RoadStrokeEffect.OrderPath(Enumerable.Range(0, RoadStrokeEffect.MaxOrdered + 50).Select(i => new Pos(i, 0)));
        Assert.Equal(RoadStrokeEffect.MaxOrdered + 50, big.Count);
        Assert.Empty(RoadStrokeEffect.OrderPath([]));
    }

    [Fact]
    public void ZoneRippleSweepsOutwardFromTheOrigin()
    {
        var system = new EffectSystem(1);
        var cells = Enumerable.Range(0, 20).Select(i => new TintedCell(new Pos(i, 0), Red)).ToList();
        system.Spawn(new ZoneRippleEffect(cells, (0, 0.5)));
        system.Update(0.3);
        Assert.True(system.CellAt(1, 0).BackgroundAmount > 0f);
        Assert.True(system.CellAt(19, 0).IsIdentity, "the ripple has not reached the far end yet");
    }

    [Fact]
    public void FireDrawsFlamesAndFadesTheGhost()
    {
        var system = new EffectSystem(1);
        system.Spawn(new FireEffect([Ghost(4, 4)], 1, 2));
        system.Update(1.0);
        Assert.NotEmpty(system.Sprites);
        Assert.Contains(system.Sprites, s => EffectGlyphs.Flame.Contains(s.Glyph));
        Assert.True(system.CellAt(4, 4).Overlay);
        Run(system, 2);
        Assert.Equal(0, system.SpriteCount);
    }

    [Fact]
    public void QuakeShakesThenSettles()
    {
        var system = new EffectSystem(1);
        system.Spawn(new QuakeEffect([new Pos(2, 2)], 1, 2));
        float peak = 0;
        for (int i = 0; i < 20; i++)
        {
            system.Update(1.0 / 60);
            peak = Math.Max(peak, Math.Abs(system.Shake.X) + Math.Abs(system.Shake.Y));
        }

        Assert.True(peak > 0.01f);
        Run(system, 3);
        Assert.Equal((0f, 0f), system.Shake);
    }

    [Fact]
    public void QuakeAmplitudeScalesWithIntensity()
    {
        float Peak(double intensity)
        {
            var system = new EffectSystem(1, new EffectSettings { Intensity = intensity });
            system.Spawn(new QuakeEffect([], 1, 1));
            float peak = 0;
            for (int i = 0; i < 30; i++)
            {
                system.Update(1.0 / 60);
                peak = Math.Max(peak, Math.Abs(system.Shake.X));
            }

            return peak;
        }

        Assert.True(Peak(0.5) < Peak(1));
    }

    [Fact]
    public void ConfettiFallsBackDownUnderGravity()
    {
        var confetti = new ConfettiEffect((20, 20), 10, 1);
        var (_, y1) = confetti.PositionAt(0, 0.2);
        var (_, y2) = confetti.PositionAt(0, 2.5);
        Assert.True(y1 < 20, "launches upward");
        Assert.True(y2 > y1, "gravity pulls it back down");
    }

    [Fact]
    public void CoinsRiseAndFade()
    {
        var system = new EffectSystem(1);
        system.Spawn(new CoinsEffect([new Pos(5, 10)], 1));
        float firstY = float.NaN;
        float lastY = float.NaN;
        for (int i = 0; i < 200; i++)
        {
            system.Update(0.01);
            if (system.Sprites.Count > 0)
            {
                lastY = system.Sprites[0].Y;
                if (float.IsNaN(firstY))
                {
                    firstY = lastY;
                }

                Assert.Contains(system.Sprites[0].Glyph, EffectGlyphs.Coin);
            }
        }

        Assert.False(float.IsNaN(firstY));
        Assert.True(lastY < firstY);
    }

    [Fact]
    public void FadeWithGhostsFadesTheGhostOut()
    {
        var system = new EffectSystem(1);
        system.Spawn(new FadeEffect([], Red, 2, [Ghost(3, 3)]));
        system.Update(0.01);
        float early = system.CellAt(3, 3).Alpha;
        system.Update(1.6);
        float late = system.TryGetCell(3, 3, out var c) ? c.Alpha : 0f;
        Assert.True(early > late);
    }

    [Fact]
    public void SpawnWithDelayWaitsBeforeShowing()
    {
        var system = new EffectSystem(1);
        system.Spawn(new FloodEffect([new Pos(1, 1)], 1), 1.0);
        system.Update(0.2);
        Assert.False(system.HasVisuals);
        Assert.Equal(1, system.ActiveEffects);
        Run(system, 0.6);
        Assert.False(system.HasVisuals);
        Run(system, 0.5);
        Assert.True(system.HasVisuals);
    }

    [Fact]
    public void ScheduledCallbacksRunOnceInOrderAtTheirTime()
    {
        var system = new EffectSystem(1);
        var log = new List<string>();
        system.Schedule(0.5, _ => log.Add("b"));
        system.Schedule(0.2, _ => log.Add("a"));
        system.Schedule(0.5, s =>
        {
            log.Add("c");
            s.Spawn(new CoinsEffect([new Pos(1, 1)], 1));
        });
        system.Update(0.1);
        Assert.Empty(log);
        system.Update(0.15);
        Assert.Equal(["a"], log);
        system.Update(0.25);
        system.Update(0.1);
        Assert.Equal(["a", "b", "c"], log);
        system.Update(0.25);
        Assert.Equal(["a", "b", "c"], log);
        Assert.Equal(0, system.PendingCallbacks);
        Assert.Equal(1, system.ActiveEffects);
    }

    [Fact]
    public void ScheduleChainsASequence()
    {
        var system = new EffectSystem(1);
        system.Schedule(0.1, s => s.Spawn(new FloodEffect([new Pos(1, 1)], 1, 0.3)));
        system.Schedule(0.5, s => s.Spawn(new FadeEffect([new Pos(1, 1)], Red, 0.3)));
        Run(system, 3);
        Assert.Equal(0, system.ActiveEffects);
        Assert.Equal(0, system.PendingCallbacks);
    }

    [Fact]
    public void SpawningTheSameEffectTwiceIsRejected()
    {
        var system = new EffectSystem(1);
        var effect = new CoinsEffect([new Pos(1, 1)], 1);
        Assert.True(system.Spawn(effect));
        Assert.Throws<InvalidOperationException>(() => system.Spawn(effect));
    }

    [Fact]
    public void BadArgumentsAreRejected()
    {
        var system = new EffectSystem(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Update(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Update(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Spawn(new CoinsEffect([], 1), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Schedule(double.PositiveInfinity, _ => { }));
        Assert.Throws<ArgumentNullException>(() => system.Spawn(null!));
    }

    [Fact]
    public void HugeTimeStepsAreClamped()
    {
        var system = new EffectSystem(1);
        system.Update(1000);
        Assert.Equal(EffectSystem.MaxStep, system.Time, 6);
        var demolish = new DemolishEffect([Ghost(0, 0)], (0.5, 0.5), 1);
        system.Spawn(demolish);
        system.Update(1000);
        Assert.Equal(1, system.ActiveEffects);
    }

    [Fact]
    public void ClearRemovesEverythingImmediately()
    {
        var system = new EffectSystem(1);
        system.Spawn(new DemolishEffect([Ghost(0, 0)], (0.5, 0.5), 1));
        system.Schedule(5, _ => { });
        system.Update(0.1);
        Assert.True(system.HasVisuals);
        system.Clear();
        Assert.Equal(0, system.ActiveEffects);
        Assert.Equal(0, system.PendingCallbacks);
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void SeedsAreReproducible()
    {
        var a = new EffectSystem(77);
        var b = new EffectSystem(77);
        var c = new EffectSystem(78);
        var sa = Enumerable.Range(0, 5).Select(_ => a.NextSeed()).ToList();
        Assert.Equal(sa, Enumerable.Range(0, 5).Select(_ => b.NextSeed()).ToList());
        Assert.NotEqual(sa, Enumerable.Range(0, 5).Select(_ => c.NextSeed()).ToList());
    }

    private static List<string> Trace(int seed)
    {
        var system = new EffectSystem(seed);
        system.Spawn(new DemolishEffect([Ghost(3, 3), Ghost(5, 4), Ghost(4, 7)], (4.5, 5), system.NextSeed()));
        system.Spawn(new ConfettiEffect((9, 9), 25, system.NextSeed()));
        system.Spawn(new FireEffect([Ghost(1, 1)], system.NextSeed()));
        system.Spawn(new CoinsEffect([new Pos(2, 2), new Pos(7, 7)], system.NextSeed()));
        var log = new List<string>();
        for (int i = 0; i < 120; i++)
        {
            system.Update(1.0 / 30);
            foreach (var s in system.Sprites)
            {
                log.Add($"{i}:{s.Glyph}:{s.X:F4}:{s.Y:F4}:{s.Alpha:F3}");
            }

            foreach (var (x, y, e) in system.ActiveCells().OrderBy(c => c.Y).ThenBy(c => c.X))
            {
                log.Add($"{i}:{x},{y}:{e.Scale:F4}:{e.OffsetX:F4}:{e.OffsetY:F4}:{e.Glyph}");
            }
        }

        return log;
    }

    [Fact]
    public void SameSeedGivesIdenticalFrames()
    {
        var a = Trace(5);
        Assert.NotEmpty(a);
        Assert.Equal(a, Trace(5));
        Assert.NotEqual(a, Trace(6));
    }

    [Fact]
    public void FrameRateDoesNotChangeWhereAnEffectEnds()
    {
        float Final(double dt)
        {
            var system = new EffectSystem(1);
            system.Spawn(new DemolishEffect([Ghost(0, 0), Ghost(4, 0)], (2.5, 0.5), 1));
            for (double t = 0; t < 0.5; t += dt)
            {
                system.Update(dt);
            }

            return system.CellAt(0, 0).Scale;
        }

        Assert.Equal(Final(1.0 / 30), Final(1.0 / 120), 1);
    }
}
