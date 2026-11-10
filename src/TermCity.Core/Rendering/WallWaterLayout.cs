using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public static class WallWaterLayout
{
    public const float GrateDepth = .65f;
    public const float ArchRise = .55f;
    public const float ArchClearance = .3f;

    public static bool WaterAt(GameMap map, Vector2 point) =>
        map.InBounds((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y)) &&
        map.TerrainAt((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y)).Name == DefaultTerrains.WaterName;

    public static float Surface(GameMap map, Vector2 point) =>
        TerrainSprite.Flow(map).At(Math.Clamp((int)point.X, 0, map.Width - 1),
            Math.Clamp((int)point.Y, 0, map.Height - 1)).Level;

    public static IReadOnlyList<WallFragment> Apply(WallComponent wall, GameMap map, IReadOnlyList<WallFragment> source)
    {
        if (wall.IsGate || wall.IsGateWing || wall.Health <= 0) return source;
        var tangent = wall.Tangent;
        var normal = new Vector2(-tangent.Y, tangent.X);
        float width = WallDamageLayout.Width(wall), depth = WallDamageLayout.Depth(wall);
        bool Wet(float x) => new[] { -.5f, 0, .5f }.Any(lane =>
            WaterAt(map, wall.Center + tangent * x + normal * (lane * depth)));
        int count = Math.Max(1, (int)MathF.Ceiling(width / .2f));
        float step = width / count;
        if (!Enumerable.Range(0, count).Any(i => Wet(-width / 2 + (i + .5f) * step))) return source;
        float ground = WallDamageLayout.GroundHeight(wall, map);
        var result = new List<WallFragment>();
        var openings = new List<(float X, float Bottom, float Top)>();
        for (int i = 0; i < count; i++)
        {
            float x = -width / 2 + (i + .5f) * step;
            bool wet = Wet(x);
            float floor = float.NegativeInfinity;
            if (wet)
            {
                float left = x, right = x;
                for (int j = 0; j < 800 && Wet(left - .2f); j++) left -= .2f;
                for (int j = 0; j < 800 && Wet(right + .2f); j++) right += .2f;
                float phase = (x - left + .1f) / (right - left + .2f);
                var p = wall.Center + tangent * x;
                float water = new[] { -.5f, 0, .5f }.Where(lane => WaterAt(map, p + normal * (lane * depth)))
                    .Select(lane => Surface(map, p + normal * (lane * depth))).Max();
                floor = Math.Min(WallDamageLayout.WalkHeight(wall) - .3f,
                    water - ground + ArchClearance + ArchRise * MathF.Sin(phase * MathF.PI));
                openings.Add((x, water - ground - GrateDepth, floor));
            }
            foreach (var part in source)
            {
                if (part.Kind is not (WallFragmentKind.Body or WallFragmentKind.Rubble))
                {
                    if (i == 0) result.Add(part);
                    continue;
                }
                float a = Math.Max(-width / 2 + i * step, part.Center.X - part.Size.X / 2);
                float b = Math.Min(-width / 2 + (i + 1) * step, part.Center.X + part.Size.X / 2);
                if (b <= a + .00001f) continue;
                float top = part.Center.Y + part.Size.Y / 2;
                float bottom = Math.Max(part.Center.Y - part.Size.Y / 2, floor);
                if (top > bottom)
                    result.Add(new(new((a + b) / 2, (top + bottom) / 2, part.Center.Z),
                        new(b - a, top - bottom, part.Size.Z), part.Kind));
            }
        }
        foreach (var opening in openings)
        {
            if (opening.Top <= opening.Bottom) continue;
            foreach (int side in new[] { -1, 1 })
            {
                float z = side * Math.Max(0, depth / 2 - .06f);
                result.Add(new(new(opening.X, (opening.Top + opening.Bottom) / 2, z),
                    new(.055f, opening.Top - opening.Bottom, .09f), WallFragmentKind.Grate));
                for (float y = opening.Bottom + .25f; y < opening.Top; y += .25f)
                    result.Add(new(new(opening.X, y, z), new(step, .045f, .09f), WallFragmentKind.Grate));
            }
        }
        return result;
    }
}
