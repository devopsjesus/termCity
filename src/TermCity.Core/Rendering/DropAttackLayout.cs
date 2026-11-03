using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public readonly record struct DropFootprint(Vector2 Center, Vector2 Forward, float Size, int Station)
{
    public Vector2 Right => new(Forward.Y, -Forward.X);
    public Vector2 AcquisitionOrigin => Center - Forward * (Size / 2 + .27f);
    public Vector2[] Polygon => [Center - Right * Size / 2 - Forward * Size / 2,
        Center + Right * Size / 2 - Forward * Size / 2, Center + Right * Size / 2 + Forward * Size / 2,
        Center - Right * Size / 2 + Forward * Size / 2];
    public bool Contains(Vector2 point)
    {
        var delta = point - Center;
        return Math.Abs(Vector2.Dot(delta, Right)) <= Size / 2 + WallNetwork.Tolerance &&
            Math.Abs(Vector2.Dot(delta, Forward)) <= Size / 2 + WallNetwork.Tolerance;
    }
}

public static class DropAttackLayout
{
    public static float Size(WallDefenseKind kind) => kind switch
    {
        WallDefenseKind.OilCauldron => 1.5f,
        WallDefenseKind.Rocks => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IReadOnlyList<DropFootprint> Footprints(WallComponent wall, WallDefenseKind kind, int? facing = null)
    {
        var stations = TowerDefenseArtwork.Stations(wall, facing);
        var result = new List<DropFootprint>();
        float size = Size(kind);
        for (int i = 0; i < stations.Count; i++)
        {
            var station = stations[i];
            var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
            float edge = Math.Abs(Vector2.Dot(station.Forward, wall.Tangent)) * WallDamageLayout.Width(wall) / 2 +
                Math.Abs(Vector2.Dot(station.Forward, normal)) * WallDamageLayout.Depth(wall) / 2;
            var right = new Vector2(station.Forward.Y, -station.Forward.X);
            var center = wall.Center + right * Vector2.Dot(station.Position, right) +
                station.Forward * (edge + size / 2 + .02f);
            result.Add(new(center, station.Forward, size, i));
        }
        return result;
    }
}
