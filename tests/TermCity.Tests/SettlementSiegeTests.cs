using System.Reflection;
using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using Xunit.Abstractions;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class SettlementSiegeTests(ITestOutputHelper output)
{
    private static readonly CellRect Enclosure = new(15, 25, 32, 32);

    private static CityGame Empty(int money = 50_000)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 80, MapHeight = 72, StartingMoney = money, DefaultTaxRate = 0.05,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0, FloodChancePerWeek = 0,
            OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray()) game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        return game;
    }

    private static CityGame Founded()
    {
        var game = Empty();
        Assert.True(game.FoundSettlement(Enclosure).Success);
        return game;
    }

    private static void Set(object value, string property, object data) =>
        value.GetType().GetProperty(property)!.SetValue(value, data);

    private static void Field(object value, string field, object data) =>
        value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);

    private static List<Goblin> Attackers(SettlementState state) =>
        (List<Goblin>)state.GetType().GetField("GoblinList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private static Goblin Attacker(int id, Pos wall, double x, double y, double health = 300)
    {
        var goblin = new Goblin();
        Set(goblin, "Id", id);
        Set(goblin, "X", x);
        Set(goblin, "Y", y);
        Set(goblin, "Health", health);
        Set(goblin, "MaxHealth", health);
        Set(goblin, "Tier", 1);
        Field(goblin, "Route", new List<Pos> { wall.Offset(0, -1), wall });
        return goblin;
    }

    private static void Populate(CityGame game, int population)
    {
        var map = game.Map;
        var home = map.Content.Buildings.ForZone(ZoneType.Residential)!;
        foreach (var plot in game.VacantPlots(ZoneType.Residential))
        {
            if (population == 0) break;
            int people = Math.Min(home.Capacity, population);
            map.SetBuildingFootprint(home, plot);
            map.SetHousehold(plot.X, plot.Y, new Household((byte)Math.Min(4, people),
                (byte)Math.Max(0, people - 4), 0));
            population -= people;
        }
        Assert.Equal(0, population);
        game.Touch();
    }

    [Fact]
    public void PaidFoundingIsAtomicAndContainsSeatSuppliesAndClosedReservedWalls()
    {
        var poor = Empty(1);
        string before = SaveGameStore.Serialize(poor);
        Assert.False(poor.FoundSettlement(Enclosure).Success);
        Assert.Equal(before, SaveGameStore.Serialize(poor));
        Assert.False(poor.FoundSettlement(new(-1, 2, 12, 12)).Success);
        Assert.False(poor.FoundSettlement(new(10, 10, 11, 12)).Success);
        Assert.Equal(before, SaveGameStore.Serialize(poor));

        var game = Empty();
        game.Paused = true;
        int money = game.Money;
        var quote = game.QuoteSettlement(Enclosure);
        var result = game.FoundSettlement(Enclosure);
        Assert.True(result.Success);
        Assert.True(game.Paused);
        Assert.Equal(quote.Cost, result.Cost);
        Assert.Equal(money - quote.Cost, game.Money);
        Assert.Equal(3, game.Map.BuildingFootprints.Count);
        Assert.Equal(new[] { "Motte and Bailey", "Town Well", "Woodlot" },
            game.Map.ServiceCells.Select(i => game.Map.BuildingAt(i % game.Map.Width, i / game.Map.Width)!.Name).Order());
        var state = game.Settlement!;
        Assert.Equal(124, state.Walls.Count);
        Assert.Equal(124, state.Walls.Select(w => w.Cell).Distinct().Count());
        Assert.Equal(new(Enclosure.Right, Enclosure.Y + Enclosure.Height / 2), Assert.Single(state.Walls, w => w.IsGate).Cell);
        Assert.Equal(4, state.Walls.Count(w => w.IsTower &&
            (w.Cell.X == Enclosure.X || w.Cell.X == Enclosure.Right) &&
            (w.Cell.Y == Enclosure.Y || w.Cell.Y == Enclosure.Bottom)));
        foreach (var edge in new[]
        {
            state.Walls.Where(w => w.IsTower && w.Cell.Y == Enclosure.Y).Select(w => w.Cell.X),
            state.Walls.Where(w => w.IsTower && w.Cell.Y == Enclosure.Bottom).Select(w => w.Cell.X),
            state.Walls.Where(w => w.IsTower && w.Cell.X == Enclosure.X).Select(w => w.Cell.Y),
            state.Walls.Where(w => w.IsTower && w.Cell.X == Enclosure.Right).Select(w => w.Cell.Y),
        })
        {
            var sorted = edge.Order().ToArray();
            Assert.All(sorted.Zip(sorted.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 1, 15));
        }
        var wall = state.Walls[0].Cell;
        Assert.False(game.CanBuildOn(wall.X, wall.Y));
        Assert.False(game.CanPlaceRoad(wall.X, wall.Y));
        Assert.False(game.Designate(CellRect.Single(wall), ZoneType.Residential).Success);
        Assert.False(game.Demolish(CellRect.Single(wall)).Success);
        Assert.False(game.FoundSettlement(new(2, 2, 12, 12)).Success);
    }

    [Fact]
    public void WaterRoadsAndOccupiedLandGiveExplicitAtomicErrorsButTreesCanBeCleared()
    {
        var game = Empty();
        var water = game.Map.Content.Terrains.Get("Water");
        game.Map.SetTerrain(Enclosure.X, Enclosure.Y, water);
        game.Touch();
        string before = SaveGameStore.Serialize(game);
        Assert.Contains("dry", game.SettlementPlacementError(Enclosure)!);
        Assert.False(game.FoundSettlement(Enclosure).Success);
        Assert.Equal(before, SaveGameStore.Serialize(game));
        game.Map.SetTerrain(Enclosure.X, Enclosure.Y, game.Map.Content.Terrains.Base);
        game.Map.SetRoad(Enclosure.X, Enclosure.Y, true);
        game.Touch();
        Assert.Contains("Roads", game.SettlementPlacementError(Enclosure)!);
        game.Map.SetRoad(Enclosure.X, Enclosure.Y, false);
        game.Map.SetZone(Enclosure.X + 2, Enclosure.Y + 2, ZoneType.Residential);
        game.Touch();
        Assert.Contains("zones", game.SettlementPlacementError(Enclosure)!);
    }

    [Fact]
    public void NormalFullRulesGrowToFirstSiegeWithoutRoadsOrInventedPopulation()
    {
        var game = Founded();
        Assert.Equal(0, game.Stats.Population);
        Assert.Equal(0, game.Map.RoadCount);
        for (int week = 0; week < 65 && game.HighestPopulation < 100; week++) game.AdvanceWeek();
        Assert.True(game.HighestPopulation >= 100,
            $"Population {game.Stats.Population}, attraction {game.Indicators.Attraction}, money {game.Money}, jobs {game.Indicators.Jobs}, water {game.Services.Water.Ratio}");
        Assert.True(game.Settlement!.WaveNumber >= 1);
        Assert.True(game.Stats.Commercial.Filled > 0);
        Assert.True(game.Stats.Industrial.Filled > 0);
        foreach (var zone in Zones.Placeable)
            Assert.All(game.Map.ZoneCells(zone), index => Assert.True(game.Settlement.ContainsInterior(game.Map.PosOf(index))));
        Assert.False(game.Designate(new(2, 2, 3, 3), ZoneType.Residential).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugPresetIsPaidExactAndDoesNotSimulateWeeks(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = Empty();
        var target = SettlementDebugSetup.Apply(game, siege);
        Assert.NotNull(game.Settlement);
        Assert.True(game.Settlement.Bounds.Contains(target));
        Assert.Equal(siege ? 100 : 0, game.Stats.Population);
        Assert.Equal(siege ? 1 : 0, game.Settlement.WaveNumber);
        Assert.Equal(0, game.Week);
        Assert.False(game.Paused);
        Assert.True(game.Money < game.Config.StartingMoney + (siege ? 5_000 : 0));
        if (siege)
        {
            Assert.Equal(game.SupportedCells(ZoneType.Commercial), game.Stats.Commercial.Filled);
            Assert.Equal(game.SupportedCells(ZoneType.Industrial), game.Stats.Industrial.Filled);
            Assert.True(game.Indicators.Happiness >= 50);
            Assert.Equal(0, game.Indicators.Unemployment);
            var homes = game.Map.ZoneCells(ZoneType.Residential)
                .Where(i => game.Map.HouseholdLayer[i].Total > 0).Select(game.Map.PosOf).ToArray();
            Assert.True(homes.Select(p => p.X).Distinct().Count() >= 4);
            Assert.True(homes.Select(p => p.Y).Distinct().Count() >= 4);
            Assert.True(homes.Max(p => p.Y) - homes.Min(p => p.Y) >= game.Settlement.Bounds.Height / 2);
            foreach (var zone in Zones.Placeable)
                foreach (int index in game.Map.ZoneCells(zone).Where(i => game.Map.BuildingLayer[i] != 0))
                {
                    Assert.True(game.Services.IsPowered(game.Map, index));
                    Assert.True(game.Services.IsWatered(game.Map, index));
                }
            var loadedSiege = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
            Assert.Equal(game.Indicators.Happiness, loadedSiege.Indicators.Happiness);
            Assert.Equal(game.Stats, loadedSiege.Stats);
        }
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Apply(game));
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(Empty()));
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Apply(loaded));
    }

    [Fact]
    public void FirstWaveUsesPermanentPopulationAndCombatDoesNotInvalidateMap()
    {
        var game = Founded();
        Assert.Empty(game.Settlement!.Goblins);
        Populate(game, 99);
        Assert.Equal(0, game.Settlement.WaveNumber);
        Populate(game, 1);
        Assert.Equal(1, game.Settlement.WaveNumber);
        Assert.NotEmpty(game.Settlement.Goblins);
        game.Speed = GameSpeed.Medium;
        Assert.All(game.Settlement.Goblins, g => Assert.True(game.Map.TerrainAt((int)g.X, (int)g.Y).Buildable));
        int mapVersion = game.MapVersion, revision = game.Settlement.Revision, changes = 0;
        game.SiegeChanged += () => changes++;
        game.Update(0.1);
        Assert.Equal(mapVersion, game.MapVersion);
        Assert.True(game.Settlement.Revision > revision);
        Assert.Equal(1, changes);
        game.Paused = true;
        revision = game.Settlement.Revision;
        game.Update(0.5);
        Assert.Equal(revision, game.Settlement.Revision);
        game.Paused = false;
        foreach (var speed in new[] { GameSpeed.Slow, GameSpeed.Medium, GameSpeed.Fast })
        {
            var copy = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
            copy.Speed = speed;
            copy.Update(0.3);
            Assert.InRange(game.Settlement.NextWaveSeconds - copy.Settlement!.NextWaveSeconds,
                speed == GameSpeed.Slow ? 0.09 : speed == GameSpeed.Medium ? 0.29 : 0.89,
                speed == GameSpeed.Slow ? 0.21 : speed == GameSpeed.Medium ? 0.31 : 0.91);
        }
    }

    [Theory]
    [InlineData(GameSpeed.Slow, 0.1)]
    [InlineData(GameSpeed.Medium, 0.2)]
    [InlineData(GameSpeed.Fast, 0.6)]
    public void NextWaveBarDecrementsWithSpeedAndPreservesPausedAndSavedProgress(GameSpeed speed, double elapsed)
    {
        var game = Founded();
        Assert.Equal("Attacks at 100 souls", SidebarReport.NextWaveStatus(game).Text);
        Populate(game, 100);
        game.Speed = speed;
        var siege = game.Settlement!;
        Assert.Equal(115, siege.NextWaveIntervalSeconds);
        Assert.Equal(1, siege.NextWaveFraction);
        Assert.Equal("Wave 2 in 1:55", SidebarReport.NextWaveStatus(game).Text);
        game.Update(0.2);
        Assert.Equal((115 - elapsed) / 115, siege.NextWaveFraction, 8);
        double fraction = siege.NextWaveFraction;
        game.Paused = true;
        game.Update(0.5);
        Assert.Equal(fraction, siege.NextWaveFraction);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(fraction, loaded.Settlement!.NextWaveFraction);
    }

    [Fact]
    public void NextWaveBarWaitsAtZeroDuringCombatAndRefillsForTheNewInterval()
    {
        var game = Founded();
        Populate(game, 100);
        var siege = game.Settlement!;
        Set(siege, "NextWaveSeconds", 0d);
        Assert.Equal(0, siege.NextWaveFraction);
        Assert.Equal("Waiting for battle's end", SidebarReport.NextWaveStatus(game).Text);
        Attackers(siege).Clear();
        game.Speed = GameSpeed.Fast;
        game.Update(0.1);
        Assert.Equal(2, siege.WaveNumber);
        Assert.Equal(110, siege.NextWaveIntervalSeconds);
        Assert.Equal(1, siege.NextWaveFraction);
        Assert.Equal("Wave 3 in 1:50", SidebarReport.NextWaveStatus(game).Text);
    }

    [Fact]
    public void TowersKillAttackersAndCooldownsPersist()
    {
        var game = Founded();
        Populate(game, 100);
        game.Speed = GameSpeed.Medium;
        int count = game.Settlement!.Goblins.Count;
        for (int i = 0; i < 80 && game.Settlement.Goblins.Count == count; i++) game.Update(0.1);
        Assert.True(game.Settlement.Goblins.Count < count);
        Assert.True(game.Settlement.Projectiles.Count > 0 || game.Settlement.Goblins.Count < count);
        string save = SaveGameStore.Serialize(game);
        var copy = SaveGameStore.Deserialize(save);
        Assert.Equal(save, SaveGameStore.Serialize(copy));
        for (int i = 0; i < 50; i++) { game.Update(0.1); copy.Update(0.1); }
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(copy));
    }

    [Fact]
    public void WeekAdvancementRunsCombatAndLaterWavesScaleWithoutConsumingSimulationRandomness()
    {
        var game = Founded();
        Populate(game, 100);
        var state = game.Settlement!;
        double countdown = state.NextWaveSeconds;
        var copy = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        game.AdvanceWeek();
        copy.AdvanceWeek();
        Assert.True(state.NextWaveSeconds < countdown);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(copy));
        Set(game, "HighestPopulation", 1_000);
        Attackers(state).Clear();
        Set(state, "NextWaveSeconds", 0d);
        ulong randomState = game.Rng.State;
        game.Speed = GameSpeed.Medium;
        game.Update(0.1);
        Assert.Equal(randomState, game.Rng.State);
        Assert.Equal(2, state.WaveNumber);
        Assert.All(state.Goblins, g => Assert.Equal(4, g.Tier));
        Assert.True(state.Goblins.Count > 7);
    }

    [Fact]
    public void OilSplashDamagesClusterAndBallistaHonorsMinimumRange()
    {
        var game = Founded();
        Populate(game, 100);
        game.Speed = GameSpeed.Medium;
        var state = game.Settlement!;
        foreach (var tower in state.Walls.Where(w => w.IsTower)) Set(tower, "IsTower", false);
        var wall = state.Walls.Single(w => w.Cell == new Pos(Enclosure.X + 10, Enclosure.Y));
        var attackers = Attackers(state);
        attackers.Clear();
        attackers.Add(Attacker(1, wall.Cell, wall.Cell.X + 0.5, wall.Cell.Y - 1.5));
        attackers.Add(Attacker(2, wall.Cell, wall.Cell.X + 1.5, wall.Cell.Y - 1.5));
        Assert.True(game.PlaceWallDefense(wall.Cell, WallDefenseKind.OilCauldron).Success);
        Field(wall, "DefenseCooldown", 0d);
        game.Update(0.1);
        Assert.All(attackers, g => Assert.Equal(282, g.Health));
        Assert.Contains(state.Projectiles, p => p.Kind == WallDefenseKind.OilCauldron);
        Set(game, "HighestPopulation", 250);
        Assert.True(game.PlaceWallDefense(wall.Cell, WallDefenseKind.Ballista).Success);
        Field(wall, "DefenseCooldown", 0d);
        game.Update(0.1);
        Assert.All(attackers, g => Assert.Equal(282, g.Health));
    }

    [Fact]
    public void BreachHasExplicitLossesAndRepairAndUpgradeArePaidAndLocked()
    {
        var game = Founded();
        Assert.False(game.UpgradeSettlementWalls().Success);
        var wall = game.Settlement!.Walls.Single(w => w.Cell == new Pos(Enclosure.X + 10, Enclosure.Y));
        Assert.NotNull(game.WallDefensePlacementError(wall.Cell, WallDefenseKind.Rocks));
        Populate(game, 100);
        var attackers = Attackers(game.Settlement);
        game.Speed = GameSpeed.Medium;
        attackers.Clear();
        attackers.Add(Attacker(999, wall.Cell, wall.Cell.X + 0.5, wall.Cell.Y - 0.5));
        Set(wall, "Health", 1d);
        int people = game.Stats.Population, money = game.Money;
        game.Update(0.1);
        Assert.Equal(0, wall.Health);
        Assert.Empty(game.Settlement.Goblins);
        Assert.True(game.Stats.Population < people);
        Assert.True(game.Money < money);
        Assert.Contains(game.Events, e => e.Message.Contains("breached"));
        money = game.Money;
        Set(game, "Money", 0);
        Assert.False(game.RepairSettlementWalls().Success);
        Assert.Equal(0, wall.Health);
        Set(game, "Money", money);
        var repair = game.RepairSettlementWalls();
        Assert.True(repair.Success);
        Assert.Equal(money - repair.Cost, game.Money);
        Assert.All(game.Settlement.Walls, w => Assert.Equal(w.MaxHealth, w.Health));
        Assert.False(game.RepairSettlementWalls().Success);
        Set(game, "HighestPopulation", 250);
        Assert.True(game.UpgradeSettlementWalls().Success);
        SettlementUpgradeTests.AdvanceUpgrade(game, SettlementState.UpgradeSecondsPerSection *
            game.Settlement.WallSections.Count());
        Assert.Equal(WallMaterial.Stone, game.Settlement.Material);
        Assert.All(game.Settlement.Walls, w => Assert.Equal(450 * w.SectionLength, w.MaxHealth));
        Assert.False(game.UpgradeSettlementWalls().Success);
        Set(game, "HighestPopulation", 1_000);
        Set(game, "Money", 50_000);
        Assert.True(game.UpgradeSettlementWalls().Success);
        SettlementUpgradeTests.AdvanceUpgrade(game, SettlementState.UpgradeSecondsPerSection *
            game.Settlement.WallSections.Count());
        Assert.Equal(WallMaterial.ReinforcedStone, game.Settlement.Material);
        Assert.All(game.Settlement.Walls, w => Assert.Equal(800 * w.SectionLength, w.MaxHealth));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    public void SmallEnclosuresRemainUsableAndPaid(int size)
    {
        var game = Empty();
        Assert.True(game.FoundSettlement(new(20, 30, size, size)).Success);
        Assert.True(game.VacantPlots(ZoneType.Residential).Count > 0);
        Assert.True(game.VacantPlots(ZoneType.Commercial).Count > 0);
        Assert.True(game.VacantPlots(ZoneType.Industrial).Count > 0);
        for (int i = 0; i < 40 && game.HighestPopulation < 100; i++) game.AdvanceWeek();
        Assert.True(game.HighestPopulation >= 100, $"Small enclosure stalled at {game.Stats.Population} souls.");
    }

    [Theory]
    [InlineData(42, GameSpeed.Slow, 40)]
    [InlineData(42, GameSpeed.Medium, 20)]
    [InlineData(42, GameSpeed.Fast, 6.5)]
    [InlineData(1, GameSpeed.Medium, 20)]
    public void StarterSiegeLeavesTimeToZoomInBeforeTheWaveEnds(int seed, GameSpeed speed, double minimumSeconds)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = seed });
        SettlementDebugSetup.Apply(game, siege: true, gateSide: BaileyGateSide.Bottom);
        game.Speed = speed;
        var state = game.Settlement!;
        int attackers = state.Goblins.Count;
        Assert.All(state.Goblins, goblin => Assert.Equal(96, goblin.MaxHealth));
        double elapsed = 0;
        while (state.Goblins.Count > 0 && elapsed < minimumSeconds * 3)
        {
            game.Update(0.1);
            elapsed += 0.1;
        }
        output.WriteLine($"Seed {seed}: {attackers} goblins; first siege {elapsed:0.0}s at {speed} speed.");
        Assert.Equal(24, attackers);
        Assert.True(elapsed >= minimumSeconds,
            $"First siege ended after {elapsed:0.0}s at {speed} speed; expected at least {minimumSeconds:0.0}s.");
        Assert.Empty(state.Goblins);
        Assert.Contains(state.Walls, wall => wall.Health < wall.MaxHealth);
    }

    [Fact]
    public void OldGoblinHealthMigratesWithDamagePercentagePreservedAndInvalidHealthIsRejected()
    {
        var game = Founded();
        Populate(game, 100);
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        var goblin = json["Settlement"]!["Goblins"]![0]!;
        goblin["MaxHealth"] = 32;
        goblin["Health"] = 16;
        var restored = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.Equal(96, restored.Settlement!.Goblins[0].MaxHealth);
        Assert.Equal(48, restored.Settlement.Goblins[0].Health);
        string migrated = SaveGameStore.Serialize(restored);
        Assert.Equal(migrated, SaveGameStore.Serialize(SaveGameStore.Deserialize(migrated)));
        goblin["MaxHealth"] = 95;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void LargerLaterWavesStillRespectTheSavedAttackerLimit()
    {
        var game = Founded();
        Populate(game, 100);
        var state = game.Settlement!;
        double firstHealth = state.Goblins[0].MaxHealth;
        Attackers(state).Clear();
        Set(game, "HighestPopulation", 2_500);
        Set(state, "WaveNumber", 99);
        Set(state, "NextWaveSeconds", 0d);
        game.Speed = GameSpeed.Fast;
        game.Update(0.1);
        Assert.Equal(120, state.Goblins.Count);
        Assert.Equal(GameSpeed.Slow, game.Speed);
        Assert.All(state.Goblins, goblin =>
        {
            Assert.Equal(5, goblin.Tier);
            Assert.True(goblin.MaxHealth > firstHealth);
        });
        string save = SaveGameStore.Serialize(game);
        Assert.Equal(save, SaveGameStore.Serialize(SaveGameStore.Deserialize(save)));
    }

    [Fact]
    public void DefenseDamageRangeAndUnlocksMatchTheirRoles()
    {
        var oil = WallDefenses.Get(WallDefenseKind.OilCauldron);
        var rocks = WallDefenses.Get(WallDefenseKind.Rocks);
        var ballista = WallDefenses.Get(WallDefenseKind.Ballista);
        var trebuchet = WallDefenses.Get(WallDefenseKind.Trebuchet);
        Assert.True(rocks.Damage > oil.Damage);
        Assert.True(rocks.ReloadSeconds > oil.ReloadSeconds);
        Assert.True(oil.SplashRadius > rocks.SplashRadius);
        Assert.True(ballista.RequiredPopulation > rocks.RequiredPopulation);
        Assert.True(trebuchet.RequiredPopulation > ballista.RequiredPopulation);
        foreach (var kind in Enum.GetValues<WallDefenseKind>().Where(kind => kind != WallDefenseKind.Trebuchet))
        {
            Assert.True(trebuchet.Damage > WallDefenses.Get(kind).Damage);
            Assert.True(trebuchet.Range > WallDefenses.Get(kind).Range);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalendarDatedFreshGamesAcceptDebugPresetsWithoutAdvancingTime(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var date = new DateTime(2026, 10, 20);
        var game = CityGame.New(new GameConfig { Seed = 42 }, startingDate: date);
        int initialWeek = game.Week;
        Assert.True(initialWeek > 0);
        SettlementDebugSetup.Apply(game, siege, gateSide: BaileyGateSide.Bottom);
        Assert.Equal(initialWeek, game.Week);
        Assert.Equal(0, game.Day);
        Assert.Equal(siege ? 100 : 0, game.Stats.Population);
        Assert.Equal(BaileyGateSide.Bottom, game.Settlement!.GateSide);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElapsedEmptyGamesRemainIneligibleEvenAfterUndoSnapshotRestoration(bool wholeWeek)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = Empty();
        if (wholeWeek) game.AdvanceWeek();
        else game.Update(0.1);
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Apply(game, siege: true));
        var restored = SaveGameStore.DeserializeSnapshot(SaveGameStore.Serialize(game));
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Apply(restored, siege: true));
    }

    [Fact]
    public void FreshDatedSnapshotRestorationPreservesEligibilityButUserLoadsDoNot()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 }, startingDate: new DateTime(2026, 10, 20));
        string snapshot = SaveGameStore.Serialize(game);
        var loaded = SaveGameStore.Deserialize(snapshot);
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Apply(loaded, siege: true));
        var restored = SaveGameStore.DeserializeSnapshot(snapshot);
        SettlementDebugSetup.Apply(restored, siege: true, gateSide: BaileyGateSide.Bottom);
        Assert.Equal(100, restored.Stats.Population);
        Assert.Equal(game.Week, restored.Week);
        Assert.Equal(BaileyGateSide.Bottom, restored.Settlement!.GateSide);
    }

    [Fact]
    public void PausedFoundingUndoRestoresFreshDatedEligibilityWithoutStaleIndexes()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = 42 }, startingDate: new DateTime(2026, 10, 20));
        var bounds = Enumerable.Range(1, game.Map.Height - 17)
            .SelectMany(y => Enumerable.Range(1, game.Map.Width - 17).Select(x => new CellRect(x, y, 16, 16)))
            .First(area => game.SettlementPlacementError(area) is null);
        game.Paused = true;
        var session = new GameSession(game);
        session.BeginSettlementPlacement();
        session.MoveFixedPlacement(new(bounds.X, bounds.Y));
        Assert.True(session.ConfirmPreview().Success);
        session.MoveFixedPlacement(new(bounds.Right, bounds.Bottom));
        Assert.True(session.ConfirmPreview().Success);
        Assert.NotNull(session.Game.Settlement);
        session.RequestUndo();
        session.SelectPrompt(0);
        var restored = session.Game;
        Assert.Null(restored.Settlement);
        Assert.Empty(restored.Map.ServiceCells);
        Assert.Empty(restored.Map.BuildingFootprints);
        Assert.All(restored.Map.BuildingLayer, id => Assert.Equal(0, id));
        Assert.All(Zones.Placeable, zone => Assert.Empty(restored.Map.ZoneCells(zone)));
        SettlementDebugSetup.Apply(restored, siege: true);
        Assert.Equal(100, restored.Stats.Population);
    }

    [Fact]
    public void LegacySavesRemainOptionalAndMalformedSettlementStateIsRejected()
    {
        var legacy = JsonNode.Parse(SaveGameStore.Serialize(Empty()))!.AsObject();
        legacy.Remove("Settlement");
        Assert.Null(SaveGameStore.Deserialize(legacy.ToJsonString()).Settlement);
        var game = Founded();
        Populate(game, 100);
        string saved = SaveGameStore.Serialize(game);
        foreach (var mutate in new Action<JsonObject>[]
        {
            s => s["Bounds"]!["Width"] = 11,
            s => s["Walls"]![0]!["Health"] = -1,
            s => s["Walls"]![0]!["MaxHealth"] = 999,
            s => s["Walls"]![0]!.AsObject().Remove("Health"),
            s => s["Walls"]![0]!["IsTower"] = false,
            s => s["Walls"]!.AsArray().RemoveAt(0),
            s => s["Goblins"]![0]!["X"] = -1,
            s => s["Goblins"]![0]!["Route"]!.AsArray().Clear(),
            s => s["Goblins"]![1]!["Id"] = s["Goblins"]![0]!["Id"]!.DeepClone(),
            s => s["Accumulator"] = -0.1,
            s => s["NextWaveSeconds"] = -1,
            s => s["WaveNumber"] = 0,
            s => s["GateSide"] = "Left",
            s => s.Remove("Walls"),
        })
        {
            var json = JsonNode.Parse(saved)!.AsObject();
            mutate(json["Settlement"]!.AsObject());
            Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        }
    }

    [Theory]
    [InlineData(BaileyGateSide.Right)]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Left)]
    [InlineData(BaileyGateSide.Top)]
    public void GateFacingRoundTripsAndRemainsCentered(BaileyGateSide side)
    {
        var game = Empty();
        Assert.True(game.FoundSettlement(Enclosure, side).Success);
        var expected = side switch
        {
            BaileyGateSide.Right => new Pos(Enclosure.Right, Enclosure.Y + Enclosure.Height / 2),
            BaileyGateSide.Left => new Pos(Enclosure.X, Enclosure.Y + Enclosure.Height / 2),
            BaileyGateSide.Bottom => new Pos(Enclosure.X + Enclosure.Width / 2, Enclosure.Bottom),
            _ => new Pos(Enclosure.X + Enclosure.Width / 2, Enclosure.Y),
        };
        Assert.Equal(expected, Assert.Single(game.Settlement!.Walls, w => w.IsGate).Cell);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game)).Settlement!;
        Assert.Equal(side, restored.GateSide);
        Assert.Equal(expected, Assert.Single(restored.Walls, w => w.IsGate).Cell);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void DebugPresetWorksOnUnmodifiedRandomTerrainAndAttackersStayOnDryLand(int seed)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var game = CityGame.New(new GameConfig { Seed = seed });
        SettlementDebugSetup.Apply(game, siege: true);
        Assert.True(game.Indicators.Happiness >= 50, $"Seed {seed}: {game.Indicators.Happiness} happiness.");
        Assert.Equal(0, game.Indicators.Unemployment);
        for (int i = 0; i < 120; i++)
        {
            game.Update(0.1);
            Assert.All(game.Settlement!.Goblins, goblin =>
                Assert.True(game.Map.TerrainAt((int)goblin.X, (int)goblin.Y).Buildable));
        }
        Assert.Equal(1, game.Settlement!.WaveNumber);
    }
}
