using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public enum WallFragmentKind { Body, Trim, Merlon, Door, Rubble }
public readonly record struct WallFragment(Vector3 Center, Vector3 Size, WallFragmentKind Kind);
public readonly record struct WallFlameSurface(Vector3 Position, float Width, float Depth);
public readonly record struct WallFlamePart(Vector3 Center, Vector3 Size, int Color);

public static class WallDamageLayout
{
    public const double FireThreshold = 0.33;

    public static double HealthFraction(WallComponent wall)
    {
        if (!double.IsFinite(wall.Health) || !double.IsFinite(wall.MaxHealth) ||
            wall.MaxHealth <= 0 || wall.Health < 0 || wall.Health > wall.MaxHealth)
            throw new ArgumentException("Wall health must be finite and within its positive maximum.", nameof(wall));
        return wall.Health / wall.MaxHealth;
    }

    public static bool IsBurning(WallComponent wall) => HealthFraction(wall) < FireThreshold;

    public static IReadOnlyList<WallFlameSurface> FlameSurfaces(WallComponent wall)
    {
        if (!IsBurning(wall)) return [];
        var fragments = Fragments(wall);
        var candidates = fragments.Where(part => part.Kind != WallFragmentKind.Door)
            .Select(part => new WallFlameSurface(
                part.Center with { Y = part.Center.Y + part.Size.Y / 2 },
                Math.Min(0.18f, part.Size.X * 0.8f), Math.Min(0.18f, part.Size.Z * 0.8f)))
            .Where(surface => !fragments.Any(part =>
                part.Center.Y + part.Size.Y / 2 > surface.Position.Y + 0.001f &&
                Math.Abs(part.Center.X - surface.Position.X) < (part.Size.X + surface.Width) / 2 &&
                Math.Abs(part.Center.Z - surface.Position.Z) < (part.Size.Z + surface.Depth) / 2))
            .OrderByDescending(surface => surface.Position.Y).ThenBy(surface => surface.Position.X)
            .ThenBy(surface => surface.Position.Z).ToList();
        var selected = new List<WallFlameSurface>();
        foreach (var surface in candidates)
        {
            if (selected.Any(other => Vector2.Distance(new(other.Position.X, other.Position.Z),
                    new(surface.Position.X, surface.Position.Z)) < 0.25f)) continue;
            selected.Add(surface);
            if (selected.Count == (wall.IsTower ? 3 : 2)) break;
        }
        return selected;
    }

    public static IReadOnlyList<WallFlamePart> FlameParts(WallFlameSurface surface, double seconds, int seed)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        int frame = (int)(seconds * 12 % int.MaxValue);
        int pulse = CellHash.Pick(seed, frame, 7);
        float height = 0.24f + pulse * 0.025f;
        float width = surface.Width * (0.78f + CellHash.Pick(seed + 11, frame, 4) * 0.07f);
        float depth = surface.Depth * (0.78f + CellHash.Pick(seed + 23, frame, 4) * 0.07f);
        float lean = (CellHash.Pick(seed + 37, frame, 5) - 2) * width * 0.06f;
        var root = surface.Position;
        return [
            new(root + new Vector3(0, height * 0.30f, 0), new(width, height * 0.60f, depth),
                pulse < 3 ? 0xf45b20 : 0xff7626),
            new(root + new Vector3(lean, height * 0.58f, 0), new(width * 0.68f, height * 0.52f, depth * 0.68f),
                pulse < 4 ? 0xffa52f : 0xffbd42),
            new(root + new Vector3(lean, height * 0.87f, 0), new(width * 0.34f, height * 0.30f, depth * 0.34f),
                pulse < 2 ? 0xffcc50 : 0xffdf75),
        ];
    }
    public static int DamageBand(WallComponent wall) =>
        Math.Clamp((int)Math.Ceiling((1 - HealthFraction(wall)) * 8 - 1e-10), 0, 8);

    public static float NominalHeight(WallComponent wall) => wall.IsTower ? 3.1f : 1.9f;

    private static float ColumnFraction(WallComponent wall, int column, int count)
    {
        float severity = DamageBand(wall) / 8f;
        if (severity == 0) return 1;
        float weakness = column == count / 2 ? 1.5f :
            0.25f + CellHash.Pick(wall.Cell.X * 13 + column, wall.Cell.Y * 7 + column * 3, 5) * 0.24f;
        return Math.Clamp(MathF.Ceiling((1 - severity * weakness) * 8) / 8, 0.125f, 1);
    }

    public static float TopHeight(WallComponent wall)
    {
        if (wall.Health == 0) return 0.2f;
        if (wall.IsGate)
            return NominalHeight(wall) * Math.Max(ColumnFraction(wall, 0, 5), ColumnFraction(wall, 4, 5));
        int count = wall.IsTower ? 9 : 5;
        float top = 0;
        for (int i = 0; i < count; i++) top = Math.Max(top, ColumnFraction(wall, i, count));
        return NominalHeight(wall) * top;
    }

    public static IReadOnlyList<WallFragment> Fragments(WallComponent wall)
    {
        int band = DamageBand(wall);
        float nominal = NominalHeight(wall);
        var parts = new List<WallFragment>();
        void Box(Vector3 center, Vector3 size, WallFragmentKind kind) => parts.Add(new(center, size, kind));
        if (wall.Health > 0)
        {
            if (wall.IsGate)
            {
                float left = nominal * ColumnFraction(wall, 0, 5);
                float right = nominal * ColumnFraction(wall, 4, 5);
                Box(new(-0.39f, left / 2, 0), new(0.22f, left, 0.85f), WallFragmentKind.Body);
                Box(new(0.39f, right / 2, 0), new(0.22f, right, 0.85f), WallFragmentKind.Body);
                float lintel = Math.Min(left, right);
                if (band < 4)
                    Box(new(0, lintel - 0.17f, 0), new(1, 0.34f, 0.85f), WallFragmentKind.Body);
                for (int i = 0; i < 3; i++)
                {
                    if (band >= 4 && i == 1) continue;
                    float height = nominal * 0.75f * ColumnFraction(wall, i + 1, 5);
                    Box(new((i - 1) * 0.19f, height / 2, 0), new(0.18f, height, 0.12f), WallFragmentKind.Door);
                }
            }
            else if (band == 0)
            {
                float width = wall.IsTower ? 1.35f : 1.05f, depth = wall.IsTower ? 1.35f : 0.42f;
                Box(new(0, nominal / 2, 0), new(width, nominal, depth), WallFragmentKind.Body);
                Box(new(0, nominal - 0.24f, 0), new(width + 0.1f, 0.15f, depth + 0.1f), WallFragmentKind.Trim);
            }
            else
            {
                int columns = wall.IsTower ? 3 : 5, rows = wall.IsTower ? 3 : 1;
                float width = wall.IsTower ? 1.35f : 1.05f, depth = wall.IsTower ? 1.35f : 0.42f;
                for (int z = 0; z < rows; z++)
                    for (int x = 0; x < columns; x++)
                    {
                        float height = nominal * ColumnFraction(wall, z * columns + x, columns * rows);
                        var center = new Vector3(((x + 0.5f) / columns - 0.5f) * width, height / 2,
                            ((z + 0.5f) / rows - 0.5f) * depth);
                        Box(center, new(width / columns, height, depth / rows), WallFragmentKind.Body);
                        Box(center with { Y = Math.Max(0.1f, height - 0.2f) },
                            new(width / columns + 0.02f, 0.12f, depth / rows + 0.04f), WallFragmentKind.Trim);
                    }
            }
            if (!wall.IsGate)
            {
                if (wall.IsTower)
                {
                    for (int z = -1; z <= 1; z++)
                        for (int x = -1; x <= 1; x++)
                        {
                            if (x == 0 && z == 0) continue;
                            int index = (z + 1) * 3 + x + 1;
                            if (band > CellHash.Pick(wall.Cell.X + index, wall.Cell.Y + 7, 9)) continue;
                            float top = nominal * ColumnFraction(wall, index, 9);
                            Box(new(x * 0.48f, top + 0.16f, z * 0.48f), new(0.25f, 0.32f, 0.25f), WallFragmentKind.Merlon);
                        }
                }
                else if (band < 3)
                    Box(new(0, nominal * ColumnFraction(wall, 2, 5) + 0.12f, 0),
                        new(0.3f, 0.24f, 0.3f), WallFragmentKind.Merlon);
            }
            if (wall.Defense != WallDefenseKind.None && band > 0)
                Box(new(0, TopHeight(wall) - 0.06f, 0), new(0.85f, 0.12f, 0.8f), WallFragmentKind.Trim);
        }
        int rubble = band == 0 ? 0 : 1 + band / 2;
        for (int i = 0; i < rubble; i++)
        {
            float spread = wall.IsTower ? 1.2f : 0.85f;
            Box(new(((i + 0.5f) / rubble - 0.5f) * spread, 0.07f + (i % 2) * 0.025f,
                    (i % 2 == 0 ? -1 : 1) * (wall.IsTower ? 0.75f : 0.34f)),
                new(0.22f + (i % 3) * 0.04f, 0.14f + (i % 2) * 0.05f, 0.2f), WallFragmentKind.Rubble);
        }
        return parts;
    }
}
