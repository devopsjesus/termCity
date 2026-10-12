using TermCity.Core.Util;

namespace TermCity.Core.Roads;

public sealed record RoadTemplate(string Name, int Width, int Height, string RoadName, IReadOnlyList<Pos> Offsets)
{
    public static RoadTemplate TrafficCircle { get; } = new("Traffic Circle", 5, 5, DefaultRoads.CobbledName,
        Enumerable.Range(0, 5).Select(x => new Pos(x, 0))
            .Concat(Enumerable.Range(1, 4).Select(y => new Pos(4, y)))
            .Concat(Enumerable.Range(0, 4).Reverse().Select(x => new Pos(x, 4)))
            .Concat(Enumerable.Range(1, 3).Reverse().Select(y => new Pos(0, y))).ToArray());

    public IReadOnlyList<Pos> Cells(Pos origin) => Offsets.Select(p => p.Offset(origin.X, origin.Y)).ToArray();
}
