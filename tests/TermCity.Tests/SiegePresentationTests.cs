using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class SiegePresentationTests
{
    [Fact]
    public void PresentationAdvancesWhileAuthoritativeCombatWaitsAndDoesNotChangeSavedState()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        var unit = AddDefender(game);
        unit.PreviousX = unit.X - .35;
        game.Speed = GameSpeed.Medium;
        var presentation = new SiegePresentation();
        string saved = SaveGameStore.Serialize(game);
        double previous = -1;
        for (int frame = 0; frame < 6; frame++)
        {
            presentation.Advance(game, 1d / 60, running: true);
            Assert.True(presentation.Fraction > previous);
            previous = presentation.Fraction;
        }
        Assert.Equal(saved, SaveGameStore.Serialize(game));
        presentation.Advance(game, .5, running: true);
        Assert.Equal(1, presentation.Fraction);
        Assert.NotSame(game.Settlement, presentation.Current);
    }

    [Fact]
    public void NewCommitStartsFromDisplayedPositionWithoutSnappingBack()
    {
        var game = SettlementDebugSetup.Create(new GameConfig { Seed = 42 }, true, BaileyGateSide.Right).Game;
        var unit = AddDefender(game);
        unit.PreviousX = unit.X - .35;
        game.Speed = GameSpeed.Medium;
        var presentation = new SiegePresentation();
        presentation.Advance(game, .05, true);
        var visual = presentation.Current!.Defenders.First();
        double displayed = visual.PreviousX + (visual.X - visual.PreviousX) * presentation.Fraction;
        unit.PreviousX = unit.X;
        unit.X += .35;
        game.Settlement!.Revision++;
        presentation.Advance(game, 0, true);
        Assert.Equal(displayed, presentation.Current.Defenders.First().PreviousX, 8);
        Assert.Equal(0, presentation.Fraction);
        double seconds = presentation.Current.VisualSeconds;
        presentation.Advance(game, .5, false);
        Assert.Equal(0, presentation.Fraction);
        Assert.Equal(seconds, presentation.Current.VisualSeconds);
    }

    private static SettlementDefender AddDefender(CityGame game)
    {
        var home = game.Map.BuildingFootprints.Values.First(area =>
            SettlementState.RosterCapacity(game.Map.BuildingAt(area.X, area.Y)!) > 0);
        var unit = new SettlementDefender { Id = 1, Home = new(home.X, home.Y),
            Kind = DefenderKind.Archer, X = home.X - .5, Y = home.Y - .5 };
        game.Settlement!.DefenderList.Add(unit);
        return unit;
    }
}
