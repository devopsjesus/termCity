using TermCity.Core.Effects;
using TermCity.Core.Util;

namespace TermCity.Tests.Effects;

public class EffectBudgetTests
{
    private static readonly Rgb Red = new(200, 30, 30);

    private static List<GhostCell> Ghosts(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new GhostCell(new Pos(i % 100, i / 100), "█", Red))];

    [Fact]
    public void CellBudgetCapsTheFrame()
    {
        var settings = new EffectSettings { MaxCellEffects = 50 };
        var system = new EffectSystem(1, settings);
        system.Spawn(new DemolishEffect(Ghosts(400), (20, 2), 1));
        system.Update(0.2);
        Assert.Equal(50, system.CellCount);
        Assert.True(system.DroppedLastFrame > 0);
    }

    [Fact]
    public void SpriteBudgetCapsTheFrame()
    {
        var settings = new EffectSettings { MaxSprites = 25 };
        var system = new EffectSystem(1, settings);
        system.Spawn(new ConfettiEffect((10, 10), 400, 1));
        for (int i = 0; i < 60; i++)
        {
            system.Update(0.02);
            Assert.True(system.SpriteCount <= 25);
        }

        Assert.True(system.SpriteCount > 0);
    }

    [Fact]
    public void ZeroBudgetsDrawNothingAndDoNotThrow()
    {
        var settings = new EffectSettings { MaxSprites = 0, MaxCellEffects = 0 };
        var system = new EffectSystem(1, settings);
        system.Spawn(new DemolishEffect(Ghosts(10), (3, 0), 1));
        system.Spawn(new ConfettiEffect((1, 1), 20, 1));
        system.Update(0.2);
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void LiveEffectCapEvictsTheOldestOfEqualPriority()
    {
        var settings = new EffectSettings { MaxEffects = 3 };
        var system = new EffectSystem(1, settings);
        var first = new CoinsEffect([new Pos(0, 0)], 1);
        Assert.True(system.Spawn(first));
        Assert.True(system.Spawn(new CoinsEffect([new Pos(1, 0)], 1)));
        Assert.True(system.Spawn(new CoinsEffect([new Pos(2, 0)], 1)));
        Assert.True(system.Spawn(new CoinsEffect([new Pos(3, 0)], 1)));
        Assert.Equal(3, system.ActiveEffects);
        Assert.Equal(1, system.Evicted);
        Assert.DoesNotContain(first, system.Effects);
    }

    [Fact]
    public void LowPriorityNewcomersAreRejectedWhenFullOfImportantEffects()
    {
        var settings = new EffectSettings { MaxEffects = 2 };
        var system = new EffectSystem(1, settings);
        var ghosts = Ghosts(1);
        Assert.True(system.Spawn(new FireEffect(ghosts, 1)));
        Assert.True(system.Spawn(new FireEffect(ghosts, 2)));
        Assert.False(system.Spawn(new CoinsEffect([new Pos(0, 0)], 1)));
        Assert.Equal(1, system.Rejected);
        Assert.Equal(2, system.ActiveEffects);
        Assert.True(system.Spawn(new FireEffect(ghosts, 3)));
    }

    [Fact]
    public void FloodingTheSystemWithEffectsStaysBounded()
    {
        var system = new EffectSystem(1);
        for (int i = 0; i < 5000; i++)
        {
            system.Spawn(new DemolishEffect([new GhostCell(new Pos(i % 50, i / 50), "█", Red)], (i % 50 + 0.5, i / 50 + 0.5), i));
            if (i % 50 == 0)
            {
                system.Update(0.016);
            }
        }

        system.Update(0.016);
        Assert.True(system.ActiveOneShots <= system.Settings.MaxEffects);
        Assert.True(system.CellCount <= system.Settings.MaxCellEffects);
        Assert.True(system.SpriteCount <= system.Settings.MaxSprites);
    }

    [Fact]
    public void ScheduledCallbacksAreBounded()
    {
        var system = new EffectSystem(1);
        int accepted = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (system.Schedule(10, _ => { }))
            {
                accepted++;
            }
        }

        Assert.True(accepted < 2000);
        Assert.Equal(accepted, system.PendingCallbacks);
    }

    [Fact]
    public void DisabledSystemRefusesSpawnsAndShowsNothing()
    {
        var settings = new EffectSettings { Enabled = false };
        var system = new EffectSystem(1, settings);
        Assert.False(system.Spawn(new ConfettiEffect((1, 1), 10, 1)));
        Assert.False(system.Schedule(1, _ => { }));
        system.Update(0.1);
        Assert.Equal(0, system.ActiveEffects);
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void ReducedMotionDisablesEverythingAndClearsRunningEffects()
    {
        var settings = new EffectSettings();
        var system = new EffectSystem(1, settings);
        system.Spawn(new DemolishEffect(Ghosts(5), (2, 0), 1));
        system.Spawn(new QuakeEffect([new Pos(1, 1)], 1));
        system.Update(0.2);
        Assert.True(system.HasVisuals);

        settings.ReducedMotion = true;
        system.Update(0.016);
        Assert.False(settings.Active);
        Assert.Equal(0, system.ActiveEffects);
        Assert.False(system.HasVisuals);
        Assert.Equal((0f, 0f), system.Shake);
        Assert.Empty(system.Sprites);
        Assert.True(system.CellAt(0, 0).IsIdentity);
        Assert.False(system.Spawn(new ConfettiEffect((1, 1), 10, 1)));

        settings.ReducedMotion = false;
        Assert.True(system.Spawn(new ConfettiEffect((1, 1), 10, 1)));
    }

    [Fact]
    public void QueriesAreEmptyTheMomentEffectsAreDisabledEvenBeforeAnUpdate()
    {
        var settings = new EffectSettings();
        var system = new EffectSystem(1, settings);
        system.Spawn(new DemolishEffect(Ghosts(3), (1, 0), 1));
        system.Update(0.2);
        Assert.True(system.CellCount > 0);
        settings.Enabled = false;
        Assert.Equal(0, system.CellCount);
        Assert.True(system.CellAt(0, 0).IsIdentity);
        Assert.Empty(system.Sprites);
    }

    [Fact]
    public void IntensityScalesParticleCountsAndZeroDisables()
    {
        int Count(double intensity)
        {
            var system = new EffectSystem(1, new EffectSettings { Intensity = intensity });
            system.Spawn(new ConfettiEffect((10, 10), 80, 1));
            for (int i = 0; i < 20; i++)
            {
                system.Update(0.05);
            }

            return system.SpriteCount;
        }

        int full = Count(1), half = Count(0.5), none = Count(0);
        Assert.True(full > half && half > 0);
        Assert.Equal(0, none);
    }

    [Fact]
    public void IntensityIsClampedAndNanRejected()
    {
        var s = new EffectSettings { Intensity = 5 };
        Assert.Equal(1, s.Intensity);
        s.Intensity = -3;
        Assert.Equal(0, s.Intensity);
        Assert.False(s.Active);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Intensity = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.MaxEffects = -1);
    }

    [Fact]
    public void LevelMapsOntoSettings()
    {
        var s = new EffectSettings();
        Assert.Equal(EffectLevel.High, s.Level);
        s.Level = EffectLevel.Low;
        Assert.Equal(EffectLevel.Low, s.Level);
        Assert.Equal(0.5, s.Intensity);
        s.Level = EffectLevel.Off;
        Assert.Equal(EffectLevel.Off, s.Level);
        Assert.False(s.Active);
        s.Level = EffectLevel.High;
        Assert.True(s.Active);
        Assert.Equal(1, s.Intensity);
        s.ReducedMotion = true;
        Assert.Equal(EffectLevel.Off, s.Level);
    }

    [Fact]
    public void ScaledNeverRoundsAPositiveCountToZeroWhileActive()
    {
        var s = new EffectSettings { Intensity = 0.1 };
        Assert.Equal(1, s.Scaled(2));
        Assert.Equal(0, s.Scaled(0));
        s.Enabled = false;
        Assert.Equal(0, s.Scaled(10));
    }

    [Fact]
    public void RandomHelpersAreDeterministicAndInRange()
    {
        for (int i = 0; i < 200; i++)
        {
            double u = EffectRandom.Unit(3, i, i * 7);
            Assert.InRange(u, 0, 0.9999999);
            Assert.Equal(u, EffectRandom.Unit(3, i, i * 7));
            Assert.InRange(EffectRandom.Signed(3, i), -1, 1);
            Assert.InRange(EffectRandom.Pick(3, 5, i), 0, 4);
        }

        Assert.NotEqual(EffectRandom.Unit(1, 1), EffectRandom.Unit(2, 1));
        Assert.Equal(0, EffectRandom.Pick(1, 1, 5));
    }

    [Fact]
    public void EasingStaysInRangeAndHitsItsEndpoints()
    {
        foreach (var f in new Func<double, double>[]
                 { Easing.EaseIn, Easing.EaseOut, Easing.EaseInCubic, Easing.EaseOutCubic, Easing.SmoothStep, Easing.Bump })
        {
            Assert.Equal(0, f(-1), 9);
            Assert.InRange(f(0.37), 0, 1);
        }

        Assert.Equal(1, Easing.EaseIn(2), 9);
        Assert.Equal(1, Easing.EaseOutBack(1), 9);
        Assert.Equal(1, Easing.Bump(0.5), 9);
        Assert.Equal(0, Easing.Clamp01(double.NaN));
        Assert.Equal(0.5, Easing.Remap(5, 0, 10), 9);
        Assert.Equal(1, Easing.Remap(7, 5, 5));
        Assert.Equal(0, Easing.Remap(3, 5, 5));
        Assert.Equal(1, Easing.Envelope(0.5, 0.2, 0.8), 9);
    }

    // ---- Ambient life -----------------------------------------------------------------------------------------

    private sealed class FakeWorld : IAmbientWorld
    {
        public AmbientKind Classify(int x, int y)
        {
            if (x < 0 || y < 0 || x >= 60 || y >= 40)
            {
                return AmbientKind.None;
            }

            if (y == 10 || y == 20 || x == 10 || x == 30 || x == 50)
            {
                return AmbientKind.Road;
            }

            if (y == 11 || y == 9)
            {
                return AmbientKind.Home;
            }

            return x % 7 == 0 && y == 25 ? AmbientKind.Factory : AmbientKind.None;
        }
    }

    private static EffectSystem AmbientSystem(EffectSettings? settings = null, int seed = 1)
    {
        var system = new EffectSystem(seed, settings) { View = new CellRect(0, 0, 60, 40) };
        system.Spawn(new AmbientLife(new FakeWorld()));
        return system;
    }

    [Fact]
    public void AmbientLifeAppearsOnRoadsAndStaysWithinItsBudget()
    {
        var system = AmbientSystem();
        int peak = 0;
        for (int i = 0; i < 600; i++)
        {
            system.Update(1.0 / 30);
            Assert.True(system.SpriteCount <= system.Settings.MaxAmbientSprites);
            peak = Math.Max(peak, system.SpriteCount);
        }

        Assert.True(peak > 5, $"peak {peak}");
        var life = system.Effects.OfType<AmbientLife>().Single();
        Assert.True(life.Cars > 0);
        Assert.True(life.People > 0);
        Assert.True(life.SmokeSources > 0);
    }

    [Fact]
    public void AmbientCarsStayOnRoads()
    {
        var system = AmbientSystem();
        var world = new FakeWorld();
        for (int i = 0; i < 400; i++)
        {
            system.Update(1.0 / 30);
            foreach (var s in system.Sprites.Where(s => s.Glyph == EffectGlyphs.Car))
            {
                // A car is between two road cells, so its centre is within half a cell of a road cell centre line.
                int cx = (int)Math.Floor(s.X), cy = (int)Math.Floor(s.Y);
                bool nearRoad = Enumerable.Range(-1, 3).Any(dx => Enumerable.Range(-1, 3).Any(dy =>
                    (world.Classify(cx + dx, cy + dy) & AmbientKind.Road) != 0));
                Assert.True(nearRoad, $"car at {s.X},{s.Y}");
            }
        }
    }

    [Fact]
    public void AmbientLifeNeedsAView()
    {
        var system = new EffectSystem(1);
        system.Spawn(new AmbientLife(new FakeWorld()));
        for (int i = 0; i < 100; i++)
        {
            system.Update(0.05);
        }

        Assert.Equal(0, system.SpriteCount);
    }

    [Fact]
    public void AmbientLifeIsNeverFinishedAndDoesNotCountAgainstTheEffectCap()
    {
        var settings = new EffectSettings { MaxEffects = 1 };
        var system = AmbientSystem(settings);
        Assert.True(system.Spawn(new CoinsEffect([new Pos(1, 1)], 1)));
        for (int i = 0; i < 300; i++)
        {
            system.Update(0.1);
        }

        Assert.Equal(1, system.ActiveEffects);
        Assert.Equal(0, system.ActiveOneShots);
    }

    [Fact]
    public void AmbientDensityFollowsIntensityAndHonoursAmbientCap()
    {
        int Peak(double intensity, int cap)
        {
            var system = AmbientSystem(new EffectSettings { Intensity = intensity, MaxAmbientSprites = cap });
            int peak = 0;
            for (int i = 0; i < 600; i++)
            {
                system.Update(1.0 / 30);
                peak = Math.Max(peak, system.SpriteCount);
            }

            return peak;
        }

        Assert.True(Peak(1, 48) > Peak(0.4, 48));
        Assert.True(Peak(1, 12) <= 12);
        Assert.Equal(0, Peak(1, 0));
    }

    [Fact]
    public void AmbientActorsLeaveWhenTheViewMovesAway()
    {
        var system = AmbientSystem();
        for (int i = 0; i < 300; i++)
        {
            system.Update(1.0 / 30);
        }

        var life = system.Effects.OfType<AmbientLife>().Single();
        Assert.True(life.Cars > 0);
        system.View = new CellRect(500, 500, 40, 30);
        for (int i = 0; i < 60; i++)
        {
            system.Update(1.0 / 30);
        }

        Assert.Equal(0, life.Cars);
        Assert.Equal(0, life.People);
        Assert.Equal(0, life.SmokeSources);
    }

    [Fact]
    public void AmbientLifeIsDeterministicForASeed()
    {
        List<string> Trace(int seed)
        {
            var system = AmbientSystem(seed: seed);
            var log = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                system.Update(1.0 / 30);
                log.AddRange(system.Sprites.Select(s => $"{i}:{s.Glyph}:{s.X:F3}:{s.Y:F3}"));
            }

            return log;
        }

        Assert.Equal(Trace(4), Trace(4));
        Assert.NotEqual(Trace(4), Trace(9));
    }

    [Fact]
    public void ClearAmbientRemovesOnlyAmbientLife()
    {
        var system = AmbientSystem();
        system.Spawn(new CoinsEffect([new Pos(1, 1)], 1));
        system.Update(0.1);
        system.ClearAmbient();
        Assert.Equal(1, system.ActiveEffects);
        Assert.DoesNotContain(system.Effects, e => e.IsAmbient);
    }
}
