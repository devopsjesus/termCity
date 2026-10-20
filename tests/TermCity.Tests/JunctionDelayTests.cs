using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class JunctionDelayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcuteMinorRoadApproachesAddDelayButOrdinaryRightAnglesDoNot(bool acute)
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var king = map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        var track = map.Content.Roads.Default;
        for (int x = 5; x < 45; x++) map.SetRoad(x, 10, king);
        foreach (var p in acute
            ? new[] { new Pos(20, 11), new(20, 12), new(21, 12), new(22, 12), new(23, 12), new(24, 12), new(25, 12) }
            : Enumerable.Range(11, 10).Select(y => new Pos(20, y)).ToArray())
            map.SetRoad(p.X, p.Y, track);
        game.Touch();
        if (acute)
            map.TravelRoadPaths =
            [
                new RoadPath(king, true, false, [5 * 12.0, 44 * 12.0], [10.5 * 22, 10.5 * 22]),
                new RoadPath(track, false, false, [20.5 * 12, 26.5 * 12], [10.5 * 22, 12.5 * 22]),
            ];
        int junction = map.Index(20, 10);
        double delay = TravelRoutes.JunctionDelayUnits(map, junction);
        Assert.Equal(acute, delay > 0);
        Assert.InRange(delay, 0, 0.75);
        Assert.Equal(delay, TravelRoutes.JunctionDelayUnits(map, junction));
        Assert.Equal(1.0 / king.Rank + delay, TravelRoutes.StepCost(map, junction), 8);
        if (acute) Assert.Contains("Inefficient junction", CellInspector.Summary(game, new(20, 10)));
        map.SetRoad(20, 11, false);
        Assert.Equal(0, TravelRoutes.JunctionDelayUnits(map, junction));
    }

    [Fact]
    public void TrafficCircleUsesOrdinaryRoadsWithoutInventingRampPenalties()
    {
        var game = TestCity.Flat();
        Assert.True(game.BuildRoadTemplate(RoadTemplate.TrafficCircle, new(10, 5)).Success);
        game.Map.SetRoad(12, 4, game.Map.Content.Roads.Get(DefaultRoads.CobbledName));
        foreach (var p in RoadTemplate.TrafficCircle.Cells(new(10, 5)))
            Assert.Equal(0, TravelRoutes.JunctionDelayUnits(game.Map, game.Map.Index(p.X, p.Y)));
    }

    [Fact]
    public void CircleConnectionToKingsRoadUsesItsLocalTangentNotALoopChord()
    {
        var game = TestCity.Flat();
        Assert.True(game.BuildRoadTemplate(RoadTemplate.TrafficCircle, new(10, 5)).Success);
        var king = game.Map.Content.Roads.Get(DefaultRoads.KingsRoadName);
        for (int x = 14; x < 24; x++) game.Map.SetRoad(x, 7, king);
        Assert.InRange(TravelRoutes.JunctionDelayUnits(game.Map, game.Map.Index(14, 7)), 0, 0.01);
    }
}
