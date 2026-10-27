using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class WallSectionTests
{
    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 72 });
        Assert.True(game.FoundSettlement(new(15, 25, 24, 24)).Success);
        return game;
    }

    [Fact]
    public void OrdinarySpansCoverOneToThreeCellsWithoutAbsorbingTowersOrGate()
    {
        var state = Founded().Settlement!;
        Assert.Contains(state.WallSections, w => w.SectionLength == 3);
        Assert.Equal(state.Walls.Count, state.WallSections.Sum(w => w.SectionLength));
        foreach (var section in state.WallSections)
        {
            Assert.InRange(section.SectionLength, 1, 3);
            Assert.True(section.SectionBounds.Width == 1 || section.SectionBounds.Height == 1);
            var members = state.Walls.Where(w => w.SectionAnchor == section.Cell).ToArray();
            Assert.Equal(section.SectionLength, members.Length);
            Assert.All(members, w => Assert.True(section.SectionBounds.Contains(w.Cell)));
            if (section.IsGate || section.IsTower) Assert.Single(members);
            else Assert.All(members, w => Assert.False(w.IsGate || w.IsTower));
            Assert.Equal(200 * section.SectionLength, section.MaxHealth);
        }
    }

    [Fact]
    public void AllCellsInSpanShareDamageBreachAndRepairButNotNeighboringSections()
    {
        var game = Founded();
        var state = game.Settlement!;
        var section = state.WallSections.First(w => w.SectionLength == 3);
        var members = state.Walls.Where(w => w.SectionAnchor == section.Cell).ToArray();
        members[1].Health -= 17;
        Assert.All(members, w => Assert.Equal(583, w.Health));
        Assert.Equal(7, game.WallRepairCost);
        Assert.All(state.WallSections.Where(w => w != section), w => Assert.Equal(w.MaxHealth, w.Health));
        members[2].Health = 0;
        Assert.All(members, w => Assert.Equal(0, w.Health));
        Assert.True(game.RepairSettlementWalls().Success);
        Assert.All(members, w => Assert.Equal(600, w.Health));
    }

    [Fact]
    public void SharedPoolsSurviveSaveLoadAndRejectContradictorySavedMembers()
    {
        var game = Founded();
        var section = game.Settlement!.WallSections.First(w => w.SectionLength == 3);
        section.Health = 123;
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        var members = restored.Settlement!.Walls.Where(w => w.SectionAnchor == section.Cell).ToArray();
        members[2].Health = 88;
        Assert.All(members, w => Assert.Equal(88, w.Health));
        var json = JsonNode.Parse(saved)!;
        var walls = json["Settlement"]!["Walls"]!.AsArray();
        walls.First(node => node!["Cell"]!["X"]!.GetValue<int>() == members[1].Cell.X &&
            node["Cell"]!["Y"]!.GetValue<int>() == members[1].Cell.Y)!["Health"] = 122;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void LegacySingleCellHealthMigratesWithoutClosingExistingBreaches()
    {
        var game = Founded();
        game.Settlement = new SettlementState(game.Settlement!.Bounds, groupWallSections: false);
        var damaged = game.Settlement.Walls.First(w => !w.IsTower && !w.IsGate);
        damaged.Health = 0;
        var json = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json["Settlement"]!.AsObject().Remove("GroupedWallSections");
        var restored = SaveGameStore.Deserialize(json.ToJsonString()).Settlement!;
        var member = restored.Walls.Single(w => w.Cell == damaged.Cell);
        Assert.Equal(0, member.Health);
        Assert.Equal(200 * member.SectionLength, member.MaxHealth);
        Assert.All(restored.Walls.Where(w => w.SectionAnchor == member.SectionAnchor), w => Assert.Equal(0, w.Health));
    }
}
