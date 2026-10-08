using System.Buffers.Binary;
using TermCity.Core.Effects;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests.Effects;

public class EffectDirectorTests
{
    private static readonly CellRect Block = new(30, 14, 4, 4);

    /// <summary>Flat city with a built-up residential block (row 20 is the road). The director starts watching afterwards.</summary>
    private static (CityGame Game, EffectSystem System, EffectDirector Director) Setup(int seed = 1, bool ambient = false)
    {
        var game = TestCity.Flat(seed);
        game.Designate(Block, ZoneType.Residential);
        foreach (var p in Block.Cells())
        {
            game.Map.SetBuilding(p.X, p.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
        }

        game.Touch();
        var system = new EffectSystem(seed);
        var director = new EffectDirector(system) { Ambient = ambient };
        director.Attach(game);
        director.Update(0.016);
        Assert.Equal(0, director.TotalSpawned);
        return (game, system, director);
    }

    private static void Run(EffectDirector director, double seconds, double dt = 1.0 / 30)
    {
        for (double t = 0; t < seconds; t += dt)
        {
            director.Update(dt);
        }
    }

    [Fact]
    public void DemolishingASelectionSpawnsAVortexAndCleansUp()
    {
        var (game, system, director) = Setup();
        Assert.True(game.Demolish(Block).Success);
        director.Update(0.05);

        var demolish = Assert.Single(system.Effects.OfType<DemolishEffect>());
        Assert.Equal(16, demolish.CellsCount);
        Assert.Equal((32.0, 16.0), demolish.Centre);
        Assert.True(system.CellCount > 0);

        director.Update(0.25);
        var corner = system.CellAt(30, 14);
        Assert.True(corner.Scale < 1f && corner.OffsetX > 0f && corner.OffsetY > 0f, "the corner is pulled toward the centre");

        Run(director, 4);
        Assert.Equal(0, system.ActiveEffects);
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void DemolishedGlyphsAreTheOnesThatWereThere()
    {
        var (game, system, director) = Setup();
        string expected = game.Map.BuildingAt(30, 14)!.GlyphAt(30, 14);
        game.Demolish(new CellRect(30, 14, 1, 1));
        director.Update(0.01);
        Assert.Equal(expected, system.CellAt(30, 14).Glyph);
    }

    [Fact]
    public void DemolishingARoadAlsoAnimates()
    {
        var (game, system, director) = Setup();
        game.Demolish(new CellRect(40, 20, 6, 1));
        director.Update(0.05);
        Assert.Single(system.Effects.OfType<DemolishEffect>());
        Assert.NotNull(system.CellAt(40, 20).Glyph);
    }

    [Fact]
    public void ScatteredDemolitionsFormSeparateVortices()
    {
        var (game, system, director) = Setup();
        game.Demolish(new CellRect(30, 14, 1, 1));
        director.Update(0.016);
        game.Demolish(new CellRect(33, 17, 1, 1));
        director.Update(0.016);
        Assert.Equal(2, system.Effects.OfType<DemolishEffect>().Count());
    }

    [Fact]
    public void NoChangeSpawnsNothing()
    {
        var (game, _, director) = Setup();
        game.Touch();
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Equal(0, director.SpawnedLastUpdate);
    }

    [Fact]
    public void FailedActionsSpawnNothing()
    {
        var (game, _, director) = Setup();
        Assert.False(game.Demolish(new CellRect(100, 60, 3, 3)).Success);
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
    }

    [Fact]
    public void ZoningRipplesAndBuildingRoadsDrawsTheStroke()
    {
        var (game, system, director) = Setup();
        Assert.True(game.Designate(new CellRect(60, 14, 5, 3), ZoneType.Commercial).Success);
        director.Update(0.016);
        Assert.Single(system.Effects.OfType<ZoneRippleEffect>());

        Assert.True(game.BuildRoad(new CellRect(70, 10, 1, 8)).Success);
        director.Update(0.016);
        var stroke = Assert.Single(system.Effects.OfType<RoadStrokeEffect>());
        Assert.True(stroke.Duration > 0.2);

        Run(director, 5);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void NewBuildingsGrowInZonesAndScaffoldWhenPlaced()
    {
        var (game, system, director) = Setup();
        game.Designate(new CellRect(60, 14, 2, 1), ZoneType.Commercial);
        director.Update(0.016);
        Run(director, 3);
        Assert.Equal(0, system.ActiveEffects);

        game.Map.SetBuilding(60, 14, game.Map.Content.Buildings.ForZone(ZoneType.Commercial)!);
        game.Touch();
        director.Update(0.016);
        var grow = Assert.Single(system.Effects.OfType<ConstructEffect>());
        Assert.Equal(ConstructStyle.Grow, grow.Style);
        Assert.Equal(0, system.CellAt(60, 14).Scale, 3);
        Run(director, 3);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void FireDestroysWithFlamesThenSmoke()
    {
        var (game, system, director) = Setup();
        var p = new Pos(31, 15);
        game.Map.SetBuilding(p.X, p.Y, null);
        game.Report(new CityEvent(game.Week, EventKind.Fire, "Fire", [game.Map.Index(p.X, p.Y)]));
        game.Touch();
        director.Update(0.5);

        Assert.Single(system.Effects.OfType<FireEffect>());
        Assert.Single(system.Effects.OfType<CollapseEffect>());
        Assert.Empty(system.Effects.OfType<DemolishEffect>());
        Assert.Contains(system.Sprites, s => EffectGlyphs.Flame.Contains(s.Glyph));

        Run(director, 8);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void EarthquakeShakesTheView()
    {
        var (game, system, director) = Setup();
        var lost = new List<int>();
        foreach (var p in new[] { new Pos(30, 14), new Pos(31, 14) })
        {
            game.Map.SetBuilding(p.X, p.Y, null);
            lost.Add(game.Map.Index(p.X, p.Y));
        }

        game.Report(new CityEvent(game.Week, EventKind.Earthquake, "Quake", lost));
        game.Touch();
        float peak = 0;
        for (int i = 0; i < 20; i++)
        {
            director.Update(1.0 / 60);
            peak = Math.Max(peak, Math.Abs(system.Shake.X));
        }

        Assert.True(peak > 0);
        Assert.NotEmpty(system.Effects.OfType<QuakeEffect>());
        Assert.NotEmpty(system.Effects.OfType<CollapseEffect>());
        Run(director, 5);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void FloodAbandonmentAndOutbreakHaveTheirOwnLooks()
    {
        var (game, system, director) = Setup();
        game.Map.SetBuilding(30, 14, null);
        game.Report(new CityEvent(game.Week, EventKind.Flood, "Flood", [game.Map.Index(30, 14)]));
        game.Map.SetBuilding(33, 17, null);
        game.Report(new CityEvent(game.Week, EventKind.Abandonment, "Abandoned", [game.Map.Index(33, 17)]));
        game.Report(new CityEvent(game.Week, EventKind.Outbreak, "Outbreak", []));
        game.Touch();
        director.Update(0.1);

        Assert.Single(system.Effects.OfType<FloodEffect>());
        Assert.True(system.Effects.OfType<FadeEffect>().Count() >= 3, "flood ghost, abandonment ghost and outbreak pulse");
        Run(director, 6);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void MilestonesThrowConfettiButTaxDaySpawnsNoMoneyAnimations()
    {
        var (game, system, director) = Setup();
        system.Settings.Celebrations = true;
        game.HighestMilestone = 1000;
        director.Update(0.016);
        Assert.Single(system.Effects.OfType<ConfettiEffect>());

        game.LastReport = new WeekReport(5, 120, 0, 0, 0);
        director.Update(0.016);
        Assert.Empty(system.Effects.OfType<CoinsEffect>());

        director.Update(0.016);
        Assert.Single(system.Effects.OfType<ConfettiEffect>());
        Assert.Empty(system.Effects.OfType<CoinsEffect>());
        Run(director, 6);
        Assert.Equal(0, system.ActiveEffects);
    }

    [Fact]
    public void CelebrationsAreOffByDefaultAndDoNotReplayWhenEnabled()
    {
        var (game, system, director) = Setup();
        Assert.False(system.Settings.Celebrations);
        game.HighestMilestone = 1000;
        director.Update(0.016);
        Assert.Empty(system.Effects.OfType<ConfettiEffect>());
        system.Settings.Celebrations = true;
        director.Update(0.016);
        Assert.Empty(system.Effects.OfType<ConfettiEffect>());
        game.HighestMilestone = 5000;
        director.Update(0.016);
        Assert.Single(system.Effects.OfType<ConfettiEffect>());
        game.LastReport = new WeekReport(5, 120, 0, 0, 0);
        director.Update(0.016);
        system.Settings.Celebrations = false;
        system.ClearCelebrations();
        Assert.Empty(system.Effects.OfType<ConfettiEffect>());
        Assert.Empty(system.Effects.OfType<CoinsEffect>());
        game.HighestMilestone = 10000;
        director.Update(0.016);
        Assert.Empty(system.Effects.OfType<ConfettiEffect>());
    }

    [Fact]
    public void LossMakingWeeksDropNoCoins()
    {
        var (game, system, director) = Setup();
        game.LastReport = new WeekReport(5, 0, 0, 0, 0);
        director.Update(0.016);
        Assert.Empty(system.Effects.OfType<CoinsEffect>());
    }

    [Fact]
    public void WeeklyTaxesStillArriveWithoutMoneyAnimations()
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        game.Map.SetZone(30, 19, ZoneType.Residential);
        game.Map.SetBuilding(30, 19, type);
        game.Map.SetHousehold(30, 19, new Household(2, 2, 0));
        game.Touch();
        var system = new EffectSystem(1);
        using var director = new EffectDirector(system) { Ambient = false };
        director.Attach(game);
        int money = game.Money;
        var report = game.AdvanceWeek();
        director.Update(0.5);
        Assert.True(report.Income > 0);
        Assert.Equal(money + report.Income, game.Money);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Empty(system.Sprites);
    }

    [Fact]
    public void WholesaleMapChangesResyncInsteadOfAnimating()
    {
        var (game, system, director) = Setup();
        var map = game.Map;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                map.SetZone(x, y, ZoneType.Residential);
            }
        }

        game.Touch();
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Equal(1, director.Resyncs);

        // And the next small change animates normally.
        game.Demolish(new CellRect(5, 5, 1, 1));
        director.Update(0.1);
        Assert.Single(system.Effects.OfType<DemolishEffect>());
    }

    [Fact]
    public void DisabledEffectsSpawnNothingAndDoNotReplayWhenReEnabled()
    {
        var (game, system, director) = Setup();
        system.Settings.Enabled = false;
        game.Demolish(Block);
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
        system.Settings.Enabled = true;
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Equal(0, system.ActiveEffects);
        game.Demolish(new CellRect(40, 20, 2, 1));
        director.Update(0.1);
        Assert.Equal(1, director.TotalSpawned);
    }

    [Fact]
    public void ReducedMotionKeepsTheDirectorQuiet()
    {
        var (game, system, director) = Setup(ambient: true);
        system.Settings.ReducedMotion = true;
        game.Demolish(Block);
        game.HighestMilestone = 5000;
        Run(director, 1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Equal(0, system.ActiveEffects);
        Assert.False(system.HasVisuals);
    }

    [Fact]
    public void DisposeStopsListeningToTheGame()
    {
        var (game, system, director) = Setup();
        director.Dispose();
        game.Demolish(Block);
        director.Update(0.1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Equal(0, system.ActiveEffects);
    }

    // ---- Sessions ---------------------------------------------------------------------------------------------

    private static (GameSession Session, EffectSystem System, EffectDirector Director) SessionSetup()
    {
        var game = TestCity.Flat();
        game.Designate(Block, ZoneType.Residential);
        foreach (var p in Block.Cells())
        {
            game.Map.SetBuilding(p.X, p.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
        }

        game.Touch();
        var session = new GameSession(game, Path.Combine(Path.GetTempPath(), "termcity-fx-" + Guid.NewGuid().ToString("N") + ".json"));
        session.SetViewport(80, 40);
        session.CenterOn(new Pos(32, 16));
        var system = new EffectSystem(1);
        var director = new EffectDirector(system) { Ambient = false };
        director.Attach(session);
        director.Update(0.016);
        return (session, system, director);
    }

    [Fact]
    public void SessionDemolishUsesTheSelectionAsTheVortexCentre()
    {
        var (session, system, director) = SessionSetup();
        session.PlaceCursor(new Pos(30, 14));
        session.BeginDrag(new Pos(30, 14));
        session.UpdateDrag(new Pos(33, 17));
        session.EndSelection();
        Assert.True(session.Demolish().Success);
        director.Update(0.05);
        var demolish = Assert.Single(system.Effects.OfType<DemolishEffect>());
        Assert.Equal((32.0, 16.0), demolish.Centre);
    }

    [Fact]
    public void ChangesOutsideTheViewAreNotAnimated()
    {
        var (session, system, director) = SessionSetup();
        session.Game.Demolish(new CellRect(150, 80, 1, 1));
        session.Game.Map.SetRoad(150, 80, true);
        session.Game.Touch();
        director.Update(0.05);
        Assert.Equal(0, director.TotalSpawned);
    }

    [Fact]
    public void ZoomedOutViewsSkipAnimationAndAmbientLife()
    {
        var (session, system, director) = SessionSetup();
        director.Ambient = true;
        session.SetZoom(-1);
        session.Game.Demolish(Block);
        Run(director, 1);
        Assert.Equal(0, director.TotalSpawned);
        Assert.Empty(system.Effects);
    }

    [Fact]
    public void LoadingOrStartingAGameResyncsAndClearsEffects()
    {
        var (session, system, director) = SessionSetup();
        session.Game.Demolish(Block);
        director.Update(0.1);
        Assert.True(system.ActiveEffects > 0);

        session.NewGame(new GameConfig { Seed = 7, MapWidth = 64, MapHeight = 48 });
        director.Update(0.016);
        Assert.Equal(0, system.ActiveOneShots);
        int before = director.TotalSpawned;
        Run(director, 1);
        Assert.Equal(before, director.TotalSpawned);

        session.Game.Demolish(new CellRect(0, 0, 64, 48));
        director.Update(0.05);
        Assert.True(system.ActiveEffects >= 0);
    }

    // ---- Ambient via the director -----------------------------------------------------------------------------

    [Fact]
    public void AmbientLifeComesAndGoesWithItsToggle()
    {
        var (_, system, director) = Setup(ambient: true);
        Run(director, 6);
        Assert.Contains(system.Effects, e => e is AmbientLife);
        Assert.True(system.SpriteCount > 0);

        director.Ambient = false;
        director.Update(0.05);
        Assert.DoesNotContain(system.Effects, e => e is AmbientLife);
        Assert.Equal(0, system.SpriteCount);

        director.Ambient = true;
        Run(director, 3);
        Assert.Contains(system.Effects, e => e is AmbientLife);
    }

    [Fact]
    public void InitiallyPausedCitiesDoNotSpawnTrafficOrBirdsUntilResumed()
    {
        var game = TestCity.Flat();
        game.Paused = true;
        var system = new EffectSystem(1);
        using var director = new EffectDirector(system);
        director.Attach(game);
        Run(director, 10);
        var life = Assert.Single(system.Effects.OfType<AmbientLife>());
        Assert.True(life.TrafficAndBirdsPaused);
        Assert.Equal(0, life.Cars);
        Assert.Equal(0, life.Birds);
        game.Paused = false;
        Run(director, 10);
        Assert.False(life.TrafficAndBirdsPaused);
        Assert.True(life.Cars > 0 && life.Birds > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GamePauseFreezesExistingTrafficAndBirdsWithoutClearingThem(bool attachSession)
    {
        var (game, system, director) = Setup(ambient: true);
        using (director)
        {
            if (attachSession)
            {
                var session = new GameSession(game);
                session.CenterOn(new Pos(40, 20));
                director.Attach(session);
            }
            Run(director, 10);
            var life = Assert.Single(system.Effects.OfType<AmbientLife>());
            Assert.True(life.Cars > 0 && life.Birds > 0);
            var before = system.Sprites.ToArray();
            game.Paused = true;
            for (int i = 0; i < 120; i++)
            {
                director.Update(1.0 / 30);
                Assert.Same(life, Assert.Single(system.Effects.OfType<AmbientLife>()));
                Assert.True(life.TrafficAndBirdsPaused);
                Assert.Equal(before, system.Sprites.ToArray());
            }
            game.Paused = false;
            director.Update(0.1);
            Assert.False(life.TrafficAndBirdsPaused);
            Assert.False(before.SequenceEqual(system.Sprites));
        }
    }

    [Fact]
    public void WhalesSurfaceOnlyInTheOpenSeaNeverInRivers()
    {
        var (game, system, director) = Setup(ambient: true);
        var water = game.Map.Content.Terrains.Get(TermCity.Core.Terrain.DefaultTerrains.WaterName);
        for (int y = 0; y < game.Map.Height; y++)
        {
            for (int x = 0; x < game.Map.Width; x++)
            {
                if (x < 20 || x == 40 || x == 41)
                {
                    game.Map.SetTerrain(x, y, water);
                }
            }
        }

        game.Touch();
        int whales = 0, riverFish = 0;
        for (double t = 0; t < 240; t += 1.0 / 30)
        {
            director.Update(1.0 / 30);
            foreach (var s in system.Sprites)
            {
                if (s.Glyph == EffectGlyphs.WhaleBack)
                {
                    whales++;
                    Assert.True(s.X < 20, $"whale at {s.X},{s.Y}");
                }
                else if (s.Glyph is EffectGlyphs.FishLeft or EffectGlyphs.FishRight && s.X >= 38)
                {
                    riverFish++;
                }
            }
        }

        Assert.True(whales > 0, "no whales surfaced in the sea");
        Assert.True(riverFish > 0, "no fish jumped in the river");
    }

    [Fact]
    public void AmbientLifeReturnsAfterEffectsAreReEnabled()
    {
        var (_, system, director) = Setup(ambient: true);
        Run(director, 2);
        system.Settings.Enabled = false;
        Run(director, 1);
        Assert.Equal(0, system.ActiveEffects);
        system.Settings.Enabled = true;
        Run(director, 1);
        Assert.Contains(system.Effects, e => e is AmbientLife);
    }

    // ---- Determinism and bounds -------------------------------------------------------------------------------

    private static List<string> Trace(int seed)
    {
        var (game, system, director) = Setup(seed, ambient: true);
        var log = new List<string>();
        for (int i = 0; i < 90; i++)
        {
            if (i == 10)
            {
                game.Demolish(Block);
            }

            if (i == 30)
            {
                game.BuildRoad(new CellRect(70, 10, 1, 8));
            }

            director.Update(1.0 / 30);
            log.AddRange(system.Sprites.Select(s => $"{i}:{s.Glyph}:{s.X:F3}:{s.Y:F3}"));
            log.AddRange(system.ActiveCells().OrderBy(c => c.Y).ThenBy(c => c.X).Select(c => $"{i}:{c.X},{c.Y}:{c.Effect.Scale:F3}"));
        }

        return log;
    }

    [Fact]
    public void WholeScenesAreDeterministicForASeed()
    {
        var a = Trace(3);
        Assert.NotEmpty(a);
        Assert.Equal(a, Trace(3));
        Assert.NotEqual(a, Trace(4));
    }

    [Fact]
    public void ManyBigChangesNeverBreakTheBudgets()
    {
        var (game, system, director) = Setup(ambient: true);
        for (int round = 0; round < 40; round++)
        {
            var area = new CellRect(round * 3 % 100, 10 + round % 20, 12, 12);
            game.Designate(area, ZoneType.Residential);
            foreach (var p in area.Cells().Where(c => game.Map.ZoneAt(c.X, c.Y) == ZoneType.Residential).Take(60))
            {
                game.Map.SetBuilding(p.X, p.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
            }

            game.Touch();
            director.Update(0.02);
            game.Demolish(area);
            director.Update(0.02);
            Assert.True(system.ActiveOneShots <= system.Settings.MaxEffects);
            Assert.True(system.CellCount <= system.Settings.MaxCellEffects);
            Assert.True(system.SpriteCount <= system.Settings.MaxSprites);
        }

        Run(director, 10);
        Assert.Equal(0, system.ActiveOneShots);
        Assert.Equal(0, system.PendingCallbacks);
    }

    // ---- Clustering and snapshot ------------------------------------------------------------------------------

    [Fact]
    public void ClusterGroupsNearbyCellsAndSeparatesDistantOnes()
    {
        var clusters = EffectDirector.Cluster([new Pos(0, 0), new Pos(1, 1), new Pos(3, 1), new Pos(50, 50), new Pos(51, 50)]);
        Assert.Equal(2, clusters.Count);
        Assert.Equal([3, 2], clusters.Select(c => c.Count).OrderByDescending(c => c));
    }

    [Fact]
    public void ClusterMergesTheTailWhenThereAreTooMany()
    {
        var cells = Enumerable.Range(0, 40).Select(i => new Pos(i * 10, 0)).ToList();
        var clusters = EffectDirector.Cluster(cells);
        Assert.True(clusters.Count <= 12);
        Assert.Equal(40, clusters.Sum(c => c.Count));
    }

    [Fact]
    public void ClusterCentreIsTheMiddleOfTheBoundingBox()
    {
        Assert.Equal((32.0, 16.0), EffectDirector.ClusterCentre([new Pos(30, 14), new Pos(33, 17)]));
        Assert.Equal((5.5, 7.5), EffectDirector.ClusterCentre([new Pos(5, 7)]));
    }

    [Fact]
    public void SnapshotComparesAndDescribesCells()
    {
        var (game, _, _) = Setup();
        var snap = new MapSnapshot();
        snap.Capture(game.Map);
        Assert.True(snap.Matches(game.Map));
        Assert.Equal(CellChange.None, snap.Compare(game.Map, 31, 15));
        string glyph = snap.Describe(game.Map, 31, 15).Glyph;
        game.Map.SetBuilding(31, 15, null);
        Assert.True((snap.Compare(game.Map, 31, 15) & CellChange.Lost) != 0);
        Assert.Equal(glyph, snap.Describe(game.Map, 31, 15).Glyph);
        game.Map.SetRoad(5, 5, true);
        Assert.True((snap.Compare(game.Map, 5, 5) & CellChange.RoadGained) != 0);
    }
}

public class EffectGlyphFontTests
{
    private static string? FindFont()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "godot", "Assets", "DejaVuSansMono.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Reads the Unicode code points the font covers from its cmap (formats 4 and 12).</summary>
    internal static HashSet<int> CoveredCodePoints(byte[] d)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        int cmap = -1;
        for (int i = 0; i < tables; i++)
        {
            int rec = 12 + i * 16;
            if (System.Text.Encoding.ASCII.GetString(d, rec, 4) == "cmap")
            {
                cmap = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 8));
            }
        }

        Assert.True(cmap >= 0, "font has a cmap table");
        var covered = new HashSet<int>();
        int subtables = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(cmap + 2));
        for (int i = 0; i < subtables; i++)
        {
            int start = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(cmap + 4 + i * 8 + 4));
            int format = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(start));
            if (format == 4)
            {
                int segs = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(start + 6)) / 2;
                int ends = start + 14, starts = ends + segs * 2 + 2;
                for (int s = 0; s < segs; s++)
                {
                    int lo = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(starts + s * 2));
                    int hi = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(ends + s * 2));
                    for (int c = lo; c <= hi && c < 0xFFFF; c++)
                    {
                        covered.Add(c);
                    }
                }
            }
            else if (format == 12)
            {
                uint groups = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(start + 12));
                for (int g = 0; g < groups; g++)
                {
                    int lo = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(start + 16 + g * 12));
                    int hi = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(start + 20 + g * 12));
                    for (int c = lo; c <= hi; c++)
                    {
                        covered.Add(c);
                    }
                }
            }
        }

        return covered;
    }

    [Fact]
    public void EveryEffectGlyphIsInTheBundledFont()
    {
        string? path = FindFont();
        Assert.NotNull(path);
        var covered = CoveredCodePoints(File.ReadAllBytes(path!));
        Assert.Contains((int)'A', covered);
        var missing = EffectGlyphs.All().Where(g => !covered.Contains(char.ConvertToUtf32(g, 0))).Distinct().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void GlyphTablesAreNonEmptySingleCharacters()
    {
        var all = EffectGlyphs.All().ToList();
        Assert.True(all.Count > 20);
        Assert.All(all, g => Assert.Equal(1, new System.Globalization.StringInfo(g).LengthInTextElements));
    }

    [Theory]
    [InlineData(CityScenario.Chicago, false)]
    [InlineData(CityScenario.StLouis, false)]
    [InlineData(CityScenario.SanDiego, true)]
    public void OnlyOceanScenariosHaveOpenSeaForWhales(CityScenario scenario, bool hasSea)
    {
        var game = CityGame.New(new GameConfig { Scenario = scenario, MapWidth = 640, MapHeight = 384, Seed = 5 });
        var world = new EffectDirector.GameAmbientWorld(game);
        int water = 0, sea = 0;
        for (int y = 0; y < game.Map.Height; y++)
        {
            for (int x = 0; x < game.Map.Width; x++)
            {
                var kind = world.Classify(x, y);
                water += (kind & AmbientKind.Water) != 0 ? 1 : 0;
                sea += (kind & AmbientKind.Sea) != 0 ? 1 : 0;
            }
        }

        Assert.True(water > 0);
        Assert.Equal(hasSea, sea > 0);
    }
}
