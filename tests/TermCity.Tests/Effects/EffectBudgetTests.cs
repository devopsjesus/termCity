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
    public void TrafficAndBirdsFreezeIncludingWingFramesWhileSmokeContinues()
    {
        var system = AmbientSystem();
        var life = Assert.Single(system.Effects.OfType<AmbientLife>());
        for (int i = 0; i < 300; i++) system.Update(1.0 / 30);
        Assert.True(life.Cars > 0 && life.People > 0 && life.Birds > 0);
        EffectSprite[] Actors() => system.Sprites.Where(sprite =>
            sprite.Color == EffectGlyphs.CarColor || sprite.Color == EffectGlyphs.PersonColor ||
            sprite.Color == EffectGlyphs.BirdColor).ToArray();
        EffectSprite[] Smoke() => system.Sprites.Where(sprite => sprite.Color == EffectGlyphs.SmokeColor).ToArray();
        var actors = Actors();
        var smoke = Smoke();
        Assert.NotEmpty(smoke);
        life.TrafficAndBirdsPaused = true;
        for (int i = 0; i < 120; i++)
        {
            system.Update(1.0 / 30);
            Assert.Equal(actors, Actors());
        }
        Assert.False(smoke.SequenceEqual(Smoke()));
        life.TrafficAndBirdsPaused = false;
        system.Update(0.1);
        Assert.False(actors.SequenceEqual(Actors()));
    }

    [Fact]
    public void PausedTrafficAndBirdsDoNotSpawnButSmokeStillAppears()
    {
        var system = AmbientSystem();
        var life = Assert.Single(system.Effects.OfType<AmbientLife>());
        life.TrafficAndBirdsPaused = true;
        for (int i = 0; i < 300; i++) system.Update(1.0 / 30);
        Assert.Equal(0, life.Cars);
        Assert.Equal(0, life.People);
        Assert.Equal(0, life.Birds);
        Assert.True(life.SmokeSources > 0);
        Assert.NotEmpty(system.Sprites);
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

    // A road laid as a staircase of cells whose drawn curve is the straight line y = x - 0.5.
    private sealed class StairWorld : IAmbientWorld
    {
        public AmbientKind Classify(int x, int y) =>
            x >= 0 && y >= 0 && x < 60 && y < 40 && (x == y || x == y + 1) ? AmbientKind.Road : AmbientKind.None;

        public (double X, double Y) Snap(double x, double y)
        {
            double along = (x + y + 0.5) / 2;
            return (along, along - 0.5);
        }
    }

    [Fact]
    public void CarsAndWalkersFollowTheDrawnCurveOfAnAngledRoad()
    {
        var system = new EffectSystem(3) { View = new CellRect(0, 0, 40, 40) };
        system.Spawn(new AmbientLife(new StairWorld()));
        int seen = 0;
        for (int i = 0; i < 900; i++)
        {
            system.Update(1.0 / 30);
            foreach (var s in system.Sprites.Where(s => s.Glyph == EffectGlyphs.Car || s.Glyph == EffectGlyphs.Person))
            {
                Assert.Equal(s.X - 0.5, s.Y, 3);
                seen++;
            }
        }

        Assert.True(seen > 50, $"only {seen} sprites seen");
    }

    [Fact]
    public void AmbientSpritesMoveSlowly()
    {
        Assert.InRange(AmbientLife.CarSpeed.Max, 0.1, 2.5);
        Assert.InRange(AmbientLife.PersonSpeed.Max, 0.1, 0.7);
        Assert.InRange(AmbientLife.BirdSpeed.Max, 0.1, 2.0);
        Assert.True(AmbientLife.PersonSpeed.Max < AmbientLife.CarSpeed.Min);
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

    // Rows 0-14 are open sea; a river two cells wide runs down column 30-31 below it. Everything else is land.
    private sealed class WaterWorld(bool sea) : IAmbientWorld
    {
        public AmbientKind Classify(int x, int y)
        {
            if (x < 0 || y < 0 || x >= 60 || y >= 40)
            {
                return AmbientKind.None;
            }

            if (sea && y < 15)
            {
                return AmbientKind.Water | AmbientKind.Sea;
            }

            return y >= 15 && (x == 30 || x == 31) ? AmbientKind.Water : AmbientKind.None;
        }
    }

    private static (EffectSystem System, AmbientLife Life) WaterSystem(bool sea, int seed = 3)
    {
        var system = new EffectSystem(seed) { View = new CellRect(0, 0, 60, 40) };
        var life = new AmbientLife(new WaterWorld(sea));
        system.Spawn(life);
        return (system, life);
    }

    [Fact]
    public void FishJumpAndWhalesSurfaceInOpenSea()
    {
        var (system, life) = WaterSystem(sea: true);
        int fish = 0, whales = 0;
        for (int i = 0; i < 1800; i++)
        {
            system.Update(1.0 / 30);
            Assert.True(system.SpriteCount <= system.Settings.MaxAmbientSprites);
            fish = Math.Max(fish, life.Fish);
            whales = Math.Max(whales, life.Whales);
        }

        Assert.True(fish > 0 && whales > 0, $"fish {fish}, whales {whales}");
    }

    [Fact]
    public void WaterLifeContinuesWhileTrafficAndBirdsArePaused()
    {
        var (system, life) = WaterSystem(sea: true);
        life.TrafficAndBirdsPaused = true;
        int fish = 0, whales = 0;
        for (int i = 0; i < 1800; i++)
        {
            system.Update(1.0 / 30);
            fish = Math.Max(fish, life.Fish);
            whales = Math.Max(whales, life.Whales);
            Assert.Equal(0, life.Birds);
        }
        Assert.True(fish > 0 && whales > 0);
    }

    [Fact]
    public void RiversAndLakesHaveFishButNeverWhales()
    {
        var (system, life) = WaterSystem(sea: false);
        int fish = 0;
        for (int i = 0; i < 3000; i++)
        {
            system.Update(1.0 / 30);
            fish = Math.Max(fish, life.Fish);
            Assert.Equal(0, life.Whales);
            Assert.DoesNotContain(system.Sprites, s => s.Glyph == EffectGlyphs.WhaleBack);
        }

        Assert.True(fish > 0);
    }

    [Fact]
    public void RiversAndLakesGetBubblesToo()
    {
        var (system, life) = WaterSystem(sea: false);
        int bubbles = 0;
        for (int i = 0; i < 3000; i++)
        {
            system.Update(1.0 / 30);
            bubbles = Math.Max(bubbles, life.Bubbles);
            foreach (var s in system.Sprites.Where(s => EffectGlyphs.Bubble.Contains(s.Glyph) && s.Glyph != EffectGlyphs.Spout[0]))
            {
                Assert.True(Math.Floor(s.X) is >= 29 and <= 32, $"bubble at {s.X},{s.Y}");
            }
        }

        Assert.True(bubbles > 0);
    }

    [Fact]
    public void AWideSeaDoesNotCrowdOutTheRiverFish()
    {
        var (system, _) = WaterSystem(sea: true);
        int riverFish = 0;
        for (int i = 0; i < 3000; i++)
        {
            system.Update(1.0 / 30);
            riverFish += system.Sprites.Count(s => s.Glyph is EffectGlyphs.FishLeft or EffectGlyphs.FishRight && s.Y >= 15);
        }

        Assert.True(riverFish > 0, $"river fish sprite-frames {riverFish}");
    }

    [Fact]
    public void WaterLifeStaysOverWater()
    {
        var (system, _) = WaterSystem(sea: false);
        var world = new WaterWorld(false);
        for (int i = 0; i < 1500; i++)
        {
            system.Update(1.0 / 30);
            foreach (var s in system.Sprites.Where(s => s.Glyph is EffectGlyphs.FishLeft or EffectGlyphs.FishRight))
            {
                int cx = (int)Math.Floor(s.X);
                Assert.True(cx is >= 29 and <= 32, $"fish at {s.X},{s.Y}");
                Assert.True(s.Y >= 14, $"fish at {s.X},{s.Y}");
            }
        }
    }

    [Fact]
    public void WaterLifeIsOffWithoutAmbientBudget()
    {
        var system = new EffectSystem(3, new EffectSettings { MaxAmbientSprites = 0 }) { View = new CellRect(0, 0, 60, 40) };
        var life = new AmbientLife(new WaterWorld(true));
        system.Spawn(life);
        for (int i = 0; i < 600; i++)
        {
            system.Update(1.0 / 30);
        }

        Assert.Equal(0, life.Fish + life.Whales);
        Assert.Equal(0, system.SpriteCount);
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
