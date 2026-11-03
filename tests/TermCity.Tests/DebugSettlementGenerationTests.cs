using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DebugSettlementGenerationTests
{
    private static GameConfig Config(int seed = 12) => new()
    {
        Seed = seed, MapWidth = 128, MapHeight = 128, StartingMoney = 200_000,
    };

    private static CityGame Flat(GameConfig config) => TestCity.Flat(config.Seed, config, config.Rules);

    private static CityGame Water(GameConfig config)
    {
        var game = Flat(config);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        return game;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsuitableMapsAreDiscardedAndRegeneratedAtTheExactDefaultSize(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var seeds = new List<int>();
        CityGame? rejected = null;
        string? before = null;
        var result = SettlementDebugSetup.Create(Config(), settings =>
        {
            seeds.Add(settings.Seed);
            if (seeds.Count != 1) return Flat(settings);
            rejected = Water(settings);
            before = SaveGameStore.Serialize(rejected);
            return rejected;
        }, siege, BaileyGateSide.Bottom);
        Assert.Equal(new[] { 12, 13 }, seeds);
        Assert.Equal(1, result.RegeneratedMaps);
        Assert.Equal(13, result.Game.Config.Seed);
        Assert.Equal(64, result.Game.Settlement!.Bounds.Width);
        Assert.Equal(64, result.Game.Settlement.Bounds.Height);
        Assert.Equal(siege ? 100 : 0, result.Game.Stats.Population);
        Assert.Equal(siege ? 24 : 0, result.Game.Settlement.Enemies.Count);
        Assert.Equal(before, SaveGameStore.Serialize(rejected!));
        if (siege)
            foreach (string name in new[] { "Guard Post", "Archer Barracks" })
                Assert.Single(result.Game.Map.BuildingFootprints.Values,
                    area => result.Game.Map.BuildingAt(area.X, area.Y)?.Name == name);
        string saved = SaveGameStore.Serialize(result.Game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Fact]
    public void SuitableMapsKeepTheRequestedSeedAndCustomDimensions()
    {
        if (!CityGame.DebugToolsAvailable) return;
        int calls = 0;
        var result = SettlementDebugSetup.Create(Config(42), settings =>
        {
            calls++;
            return Flat(settings);
        }, siege: true, wallWidth: 48, wallHeight: 64);
        Assert.Equal(1, calls);
        Assert.Equal(0, result.RegeneratedMaps);
        Assert.Equal(42, result.Game.Config.Seed);
        Assert.Equal(48, result.Game.Settlement!.Bounds.Width);
        Assert.Equal(64, result.Game.Settlement.Bounds.Height);
    }

    [Fact]
    public void RetryingCanWrapSignedSeedsWithoutLosingDeterminism()
    {
        if (!CityGame.DebugToolsAvailable) return;
        int calls = 0;
        var result = SettlementDebugSetup.Create(Config(int.MaxValue),
            settings => ++calls == 1 ? Water(settings) : Flat(settings));
        Assert.Equal(int.MinValue, result.Game.Config.Seed);
        Assert.Equal(1, result.RegeneratedMaps);
    }

    [Theory]
    [InlineData(64, 64, 1)]
    [InlineData(161, 64, 200_000)]
    [InlineData(36, 36, 200_000)]
    public void ImpossibleSettingsFailExplicitlyInsteadOfRegeneratingForever(int width, int height, int money)
    {
        if (!CityGame.DebugToolsAvailable) return;
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Create(Config() with { StartingMoney = money },
            settings => { calls++; return Water(settings); }, siege: true, wallWidth: width, wallHeight: height));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RepeatedGenerationIsDeterministicForTheSameRejectedSeedSequence()
    {
        if (!CityGame.DebugToolsAvailable) return;
        string Generate()
        {
            int calls = 0;
            var result = SettlementDebugSetup.Create(Config(), settings => ++calls == 1 ? Water(settings) : Flat(settings),
                siege: true);
            return SaveGameStore.Serialize(result.Game);
        }
        Assert.Equal(Generate(), Generate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void RealRandomMapsProduceCompleteDebugSiegesAtTheExactDefaultSize(int seed)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var result = SettlementDebugSetup.Create(new GameConfig { Seed = seed, MapWidth = 160, MapHeight = 96 },
            siege: true, gateSide: BaileyGateSide.Bottom);
        Assert.Equal(64, result.Game.Settlement!.Bounds.Width);
        Assert.Equal(64, result.Game.Settlement.Bounds.Height);
        Assert.Equal(100, result.Game.Stats.Population);
        Assert.Equal(24, result.Game.Settlement.Enemies.Count);
        Assert.Equal(unchecked(seed + result.RegeneratedMaps), result.Game.Config.Seed);
        Console.WriteLine($"DEBUG_REGEN_TEST initial={seed} selected={result.Game.Config.Seed} rejected={result.RegeneratedMaps}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshDefaultsKeepTheWorldSquareAndTheDebugSettlementAtSixtyFourCells(bool siege)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var result = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, siege, BaileyGateSide.Bottom);
        Assert.Equal(320, result.Game.Map.Width);
        Assert.Equal(320, result.Game.Map.Height);
        Assert.Equal(64, result.Game.Settlement!.Bounds.Width);
        Assert.Equal(64, result.Game.Settlement.Bounds.Height);
        Assert.Equal(siege ? 100 : 0, result.Game.Stats.Population);
        Assert.Equal(siege ? 24 : 0, result.Game.Settlement.Enemies.Count);
    }

    [Fact]
    public void GuardedRestartRegeneratesUnsuitableTerrainBeforeReplacingTheCurrentCity()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var config = new GameConfig { Seed = 42, MapWidth = 160, MapHeight = 96 };
        var original = Flat(config);
        SettlementDebugSetup.Apply(original, siege: true, gateSide: BaileyGateSide.Bottom,
            wallWidth: 64, wallHeight: 64);
        var session = new GameSession(original, debugSiegeMode: true);
        string saved = SaveGameStore.Serialize(original);
        session.RequestDebugSiegeRestart();
        Assert.Same(original, session.Game);
        Assert.NotNull(session.Prompt);
        session.SelectPrompt(1);
        Assert.NotSame(original, session.Game);
        Assert.Equal(44, session.Game.Config.Seed);
        Assert.Equal(64, session.Game.Settlement!.Bounds.Width);
        Assert.Equal(64, session.Game.Settlement.Bounds.Height);
        Assert.Equal(100, session.Game.Stats.Population);
        Assert.Equal(24, session.Game.Settlement.Enemies.Count);
        Assert.Equal(MessageKind.Success, session.MessageKind);
        Assert.Contains("Regenerated 2 map(s); seed 44", session.Message);
        Assert.Equal(saved, SaveGameStore.Serialize(original));
    }

    [Fact]
    public void UnexpectedGeneratorFailuresAreNotSwallowedOrRetried()
    {
        if (!CityGame.DebugToolsAvailable) return;
        int calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => SettlementDebugSetup.Create(Config(),
            _ => { calls++; throw new InvalidOperationException("generator fault"); }));
        Assert.Equal(1, calls);
        Assert.Equal("generator fault", error.Message);
    }

    [Fact]
    public void CancelledGenerationStopsBeforeCreatingAnotherMap()
    {
        if (!CityGame.DebugToolsAvailable) return;
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => SettlementDebugSetup.Create(Config(), settings =>
        {
            calls++;
            cancellation.Cancel();
            return Water(settings);
        }, cancellationToken: cancellation.Token));
        Assert.Equal(1, calls);
    }
}
