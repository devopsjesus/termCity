using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class SiegeEnemyDefinitionTests
{
    [Theory]
    [InlineData(SiegeEnemyKind.Goblin)]
    [InlineData(SiegeEnemyKind.Orc)]
    public void DefinitionsPreserveExistingStatsAtEveryTierAndWaveCap(SiegeEnemyKind kind)
    {
        var definition = SiegeEnemies.Get(kind);
        bool orc = kind == SiegeEnemyKind.Orc;
        Assert.Same(definition, SiegeEnemies.Get(kind));
        Assert.Equal(orc ? "Orc" : "Goblin", definition.Name);
        Assert.Equal(1, definition.AttackIntervalSeconds);
        for (int tier = 1; tier <= 5; tier++)
            foreach (int wave in new[] { 1, 2, 30, 31, 32, 1_000_000 })
            {
                double health = orc ? 420 + 65 * (tier - 2) + 12 * Math.Min(30, wave - 1) :
                    96 + 36 * (tier - 1) + 6 * Math.Min(30, wave - 1);
                Assert.Equal(health, definition.MaxHealth(tier, wave));
                Assert.Equal(orc ? 46 + 8 * tier : 4 + 2 * tier, definition.AttackDamage(tier));
                Assert.Equal(orc ? 1.05 : 1.4 + 0.1 * tier, definition.MovementSpeed(tier));
                Assert.Equal(orc ? 1 : 0.75, definition.BodyScale(tier));
                Assert.Equal((orc ? 1 : 0.75) * 0.62,
                    definition.CollisionRadius(tier));
                var enemy = new SiegeEnemy { Kind = kind, Tier = tier };
                Assert.Same(definition, enemy.Definition);
                Assert.Equal(definition.BodyScale(tier), enemy.BodyScale);
                Assert.Equal(definition.CollisionRadius(tier), enemy.CollisionRadius);
            }
    }

    [Fact]
    public void UnknownKindsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SiegeEnemies.Get((SiegeEnemyKind)99));
    }

    [Fact]
    public void SaveSchemaRetainsLegacyNamesAndMissingKindDefaultsToGoblin()
    {
        var game = WorldScaleTests.Founded();
        Assert.True(game.Settlement!.SpawnWave(game, 24));
        string saved = SaveGameStore.Serialize(game);
        var json = JsonNode.Parse(saved)!;
        var settlement = json["Settlement"]!;
        Assert.NotNull(settlement["NextGoblinId"]);
        Assert.Null(settlement["NextEnemyId"]);
        Assert.Null(settlement["Enemies"]);
        var attackers = settlement["Goblins"]!.AsArray();
        Assert.Equal(24, attackers.Count);
        foreach (var attacker in attackers) attacker!.AsObject().Remove("Kind");
        var restored = SaveGameStore.Deserialize(json.ToJsonString());
        Assert.All(restored.Settlement!.Enemies, enemy =>
        {
            Assert.Equal(SiegeEnemyKind.Goblin, enemy.Kind);
            Assert.Same(SiegeEnemies.Goblin, enemy.Definition);
        });
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
    }
}
