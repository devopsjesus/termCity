using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public enum WallFragmentKind { Body, Trim, Merlon, Door, Rubble, DoorBrace, DoorStud, Grate }
public readonly record struct WallFragment(Vector3 Center, Vector3 Size, WallFragmentKind Kind);
public readonly record struct WallFlameSurface(Vector3 Position, float Width, float Depth);
public readonly record struct WallFlamePart(Vector3 Center, Vector3 Size, int Color);
public readonly record struct TowerWalkwayLadder(Vector2 Position, Vector2 Direction, WallComponent Walkway);

public static class WallDamageLayout
{
    public const double FireThreshold = 0.33;
    public const double VisualDamageThreshold = 0.85;
    public const float WalkwayWidth = 1.3f;
    public const float LadderWallOffset = .25f;
    public const float LadderClimberOffset = .36f;
    public const float LadderRungSpacing = .2f;
    public static Vector2 LadderInward(WallComponent wall) =>
        wall.Settlement?.GroundLadder(wall)?.Inward ??
        new Vector2(wall.Tangent.Y, -wall.Tangent.X) * ExteriorSide(wall);
    public static Vector2 LadderPosition(WallComponent wall) =>
        wall.Settlement?.GroundLadder(wall)?.Position ??
        wall.Center + LadderInward(wall) * (Depth(wall) / 2 + LadderWallOffset);
    public static Vector2 LadderClimbPosition(WallComponent wall) =>
        LadderPosition(wall) + LadderInward(wall) * LadderClimberOffset;
    public static float ExteriorSide(WallComponent wall) => wall.Settlement is { } owner
        ? wall.IsOriginal
            ? wall.Cell.Y == owner.Bounds.Y ? -1 : wall.Cell.Y == owner.Bounds.Bottom ? 1 :
                wall.Cell.X == owner.Bounds.X ? -1 : 1
            : owner.WallExteriorSide(wall)
        : wall.DefenseFacing;
    public static float WalkwayOffset(WallComponent wall) => wall.IsTower || wall.IsGate ? 0 :
        -ExteriorSide(wall) * Math.Max(0, (WalkwayWidth + WorldScale.Feet(1) - Depth(wall)) / 2);

    public static IReadOnlyList<TowerWalkwayLadder> WalkwayLadders(WallComponent tower)
        => tower.Settlement?.TowerWalkwayLadders(tower) ?? [];

    internal static IReadOnlyList<TowerWalkwayLadder> ComputeWalkwayLadders(WallComponent tower)
    {
        if (!tower.IsTower || tower.Health <= 0 || tower.Settlement is not { } settlement) return [];
        var result = new List<TowerWalkwayLadder>();
        var normal = new Vector2(-tower.Tangent.Y, tower.Tangent.X);
        float half = Depth(tower) / 2;
        foreach (var wall in settlement.Walls.Where(w => !w.IsTower && !w.IsGateWing && w.Health > 0 &&
            Vector2.DistanceSquared(w.Center, tower.Center) < MathF.Pow(half + 3, 2)))
        {
            var delta = wall.Center - tower.Center;
            if (delta.LengthSquared() < .01f) continue;
            var along = wall.Tangent * (Vector2.Dot(delta, wall.Tangent) >= 0 ? 1 : -1);
            float tx = Vector2.Dot(along, tower.Tangent), tz = Vector2.Dot(along, normal);
            var direction = Math.Abs(tx) >= Math.Abs(tz) ? tower.Tangent * MathF.Sign(tx) : normal * MathF.Sign(tz);
            var across = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
            var lane = wall.Center + across * WalkwayOffset(wall);
            float denominator = Vector2.Dot(wall.Tangent, direction);
            if (Math.Abs(denominator) < .001f) continue;
            float t = (half - Vector2.Dot(lane - tower.Center, direction)) / denominator;
            var onFace = lane + wall.Tangent * t;
            var side = new Vector2(-direction.Y, direction.X);
            if (Math.Abs(Vector2.Dot(onFace - tower.Center, side)) > half - .35f ||
                !ContainsWalkway(wall, onFace.X, onFace.Y) ||
                WalkHeight(tower) <= WalkHeight(wall) + .1f) continue;
            var position = onFace + direction * LadderWallOffset;
            if (result.Any(other => Vector2.DistanceSquared(other.Position, position) < .36f)) continue;
            result.Add(new(position, direction, wall));
        }
        return result;
    }

    public static WallComponent Preview(Vector2 a, Vector2 b, WallMaterial material, bool gate = false)
    {
        float length = Vector2.Distance(a, b);
        if (!float.IsFinite(length) || length < .05f || !Enum.IsDefined(material))
            throw new ArgumentException("Choose a finite, nonzero wall preview.");
        var center = (a + b) / 2;
        var cell = new Pos((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y));
        return new WallComponent { Id = 1, Cell = cell, SectionBounds = CellRect.Single(cell), Start = a, End = b,
            BuiltMaterial = material, IsGate = gate, MaxHealth = SettlementState.MaterialHealth(material) * length,
            Health = SettlementState.MaterialHealth(material) * length };
    }

    public static WallComponent PreviewTower(Vector2 center, WallMaterial material)
    {
        var wall = Preview(center - Vector2.UnitX * .05f, center + Vector2.UnitX * .05f, material);
        wall.IsTower = true;
        wall.JunctionTower = true;
        wall.MaxHealth = SettlementState.MaterialHealth(material);
        wall.Health = wall.MaxHealth;
        return wall;
    }

    public static void OrientPreview(WallComponent preview, SettlementState settlement,
        IReadOnlyList<IReadOnlyList<Vector2>>? proposedEnclosures = null)
    {
        if (preview.Settlement is not null)
            throw new ArgumentException("Only unattached wall previews can be oriented.", nameof(preview));
        preview.DefenseFacing = (int)settlement.WallExteriorSide(preview, proposedEnclosures);
    }

    public static IReadOnlyList<Vector2[]> ExposedTop(Vector2[] source, IEnumerable<Vector2[]> cutters)
    {
        var pieces = new List<Vector2[]> { source };
        foreach (var cutter in cutters)
        {
            var remaining = new List<Vector2[]>();
            foreach (var piece in pieces) ZoneSurfaceLayout.Subtract(piece, cutter, remaining);
            pieces = remaining;
            if (pieces.Count == 0) break;
        }
        return pieces;
    }

    public static double HealthFraction(WallComponent wall)
    {
        if (!double.IsFinite(wall.Health) || !double.IsFinite(wall.MaxHealth) ||
            wall.MaxHealth <= 0 || wall.Health < 0 || wall.Health > wall.MaxHealth)
            throw new ArgumentException("Wall health must be finite and within its positive maximum.", nameof(wall));
        return wall.Health / wall.MaxHealth;
    }

    public static bool IsBurning(WallComponent wall) => HealthFraction(wall) < FireThreshold;

    public static IReadOnlyList<WallFlameSurface> FlameSurfaces(WallComponent wall)
        => FlameSurfaces(wall, Fragments(wall));

    public static IReadOnlyList<WallFlameSurface> FlameSurfaces(WallComponent wall, IReadOnlyList<WallFragment> fragments)
    {
        if (!IsBurning(wall)) return [];
        var candidates = fragments.Where(part => part.Kind is not
                (WallFragmentKind.Door or WallFragmentKind.DoorBrace or WallFragmentKind.DoorStud))
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
    public static int DamageBand(WallComponent wall)
    {
        double health = HealthFraction(wall);
        return health > VisualDamageThreshold ? 0 :
            Math.Clamp((int)Math.Ceiling((1 - health) * 8 - 1e-10), 0, 8);
    }

    public static float NominalHeight(WallComponent wall) => WorldScale.Feet(wall.IsTower
        ? wall.Material switch { WallMaterial.Wood => 30, WallMaterial.Stone => 36, _ => 40 }
        : wall.Material switch { WallMaterial.Wood => 20, WallMaterial.Stone => 26, _ => 30 });

    public static float Width(WallComponent wall) => wall.IsTower ? WorldScale.TowerWidth :
        wall.IsGate ? Math.Max(WorldScale.GateWidth, wall.Length) : wall.Start is not null ? wall.Length : 1.01f;

    public static float Depth(WallComponent wall) => wall.IsTower ? WorldScale.TowerWidth :
        WorldScale.Feet(wall.Material switch { WallMaterial.Wood => 3, WallMaterial.Stone => 8, _ => 10 });

    public static bool HasAccessLadder(WallComponent wall) =>
        wall.IsTower && !wall.IsGate && !wall.IsGateWing && wall.Health > 0 &&
        (wall.Settlement is null || wall.Settlement.GroundLadder(wall) is not null);

    public static bool ContainsWalkway(WallComponent wall, double x, double y)
    {
        if (wall.IsGateWing && wall.SharedSection is { IsGate: true } gate) return ContainsWalkway(gate, x, y);
        if (wall.Health <= 0 || WalkHeight(wall) <= 0) return false;
        var tangent = wall.Tangent;
        double dx = x - wall.Center.X, dy = y - wall.Center.Y;
        double along = dx * tangent.X + dy * tangent.Y;
        double across = -dx * tangent.Y + dy * tangent.X - WalkwayOffset(wall);
        float depth = wall.IsTower ? Depth(wall) : Math.Max(Depth(wall), WalkwayWidth + WorldScale.Feet(1));
        return Math.Abs(along) <= Width(wall) / 2 + WallNetwork.Tolerance &&
            Math.Abs(across) <= (depth + .04f) / 2 + WallNetwork.Tolerance;
    }

    public static float WalkHeight(WallComponent wall)
    {
        if (wall.IsGateWing && wall.SharedSection is { IsGate: true } gate) return WalkHeight(gate);
        if (wall.IsGate)
        {
            if (wall.Health <= 0 || DamageBand(wall) >= 4) return 0;
            return Math.Max(0, NominalHeight(wall) *
                Math.Min(ColumnFraction(wall, 0, 5), ColumnFraction(wall, 4, 5)) - WorldScale.Feet(3));
        }
        return Math.Max(0, TopHeight(wall) - WorldScale.Feet(3));
    }

    public static Vector2 HalfSize(WallComponent wall)
    {
        var tangent = wall.Tangent;
        return new((Math.Abs(tangent.X) * Width(wall) + Math.Abs(tangent.Y) * Depth(wall)) / 2,
            (Math.Abs(tangent.Y) * Width(wall) + Math.Abs(tangent.X) * Depth(wall)) / 2);
    }

    public static bool ContainsFootprint(WallComponent wall, double x, double y, double padding = 0)
    {
        var center = wall.Center;
        var tangent = wall.Tangent;
        double dx = x - center.X, dy = y - center.Y;
        return Math.Abs(dx * tangent.X + dy * tangent.Y) <= Width(wall) / 2 + padding &&
            Math.Abs(-dx * tangent.Y + dy * tangent.X) <= Depth(wall) / 2 + padding;
    }

    public static float GroundHeight(WallComponent wall, GameMap map)
    {
        if (wall.Start is not { } a || wall.End is not { } b)
            return WallWaterLayout.WaterAt(map, wall.Center) ? WallWaterLayout.Surface(map, wall.Center) :
                TerrainSprite.Elevation(map, wall.Cell.X, wall.Cell.Y);
        int steps = Math.Max(1, (int)Math.Ceiling(wall.Length * 4));
        float height = 0;
        var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
        for (int i = 0; i <= steps; i++)
            foreach (int side in new[] { -1, 0, 1 })
            {
                var p = Vector2.Lerp(a, b, i / (float)steps) + normal * (side * Depth(wall) / 2);
                height = Math.Max(height, WallWaterLayout.WaterAt(map, p) ? WallWaterLayout.Surface(map, p) :
                    TerrainSprite.Elevation(map, Math.Clamp((int)p.X, 0, map.Width - 1),
                        Math.Clamp((int)p.Y, 0, map.Height - 1)));
            }
        return height;
    }

    public static IEnumerable<WallFragment> Foundations(WallComponent wall, GameMap map)
    {
        if (wall.Start is not { } a || wall.End is not { } b) yield break;
        float top = GroundHeight(wall, map);
        int steps = Math.Max(1, (int)Math.Ceiling(wall.Length * 4));
        int lanes = Math.Max(1, (int)Math.Ceiling(Depth(wall) * 2));
        var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
        for (int i = 0; i < steps; i++)
            for (int lane = 0; lane < lanes; lane++)
            {
                float across = (lane + .5f) / lanes * Depth(wall) - Depth(wall) / 2;
                var p = Vector2.Lerp(a, b, (i + .5f) / steps) + normal * across;
                if (new[] { -.5f, 0, .5f }.Any(offset => WallWaterLayout.WaterAt(map,
                    Vector2.Lerp(a, b, (i + .5f) / steps) + normal * (offset * Depth(wall))))) continue;
                float ground = float.PositiveInfinity;
                foreach (int side in new[] { -1, 1 })
                    foreach (int end in new[] { -1, 1 })
                    {
                        var corner = p + normal * (side * Depth(wall) / lanes / 2) +
                            wall.Tangent * (end * wall.Length / steps / 2);
                        ground = Math.Min(ground, TerrainSprite.Elevation(map, Math.Clamp((int)corner.X, 0, map.Width - 1),
                            Math.Clamp((int)corner.Y, 0, map.Height - 1)));
                    }
                if (top - ground <= .00001f) continue;
                yield return new(new((i + .5f) / steps * wall.Length - wall.Length / 2, (ground - top) / 2, across),
                    new(wall.Length / steps, top - ground, Depth(wall) / lanes), WallFragmentKind.Body);
            }
    }

    public static bool BlocksGround(WallComponent wall, double x, double y, double radius, bool throughGate = false)
    {
        if (wall.Health <= 0 || wall.IsGateWing) return false;
        var tangent = wall.Tangent;
        double dx0 = x - wall.Center.X, dy0 = y - wall.Center.Y;
        double along = dx0 * tangent.X + dy0 * tangent.Y;
        if (throughGate && wall.IsGate && Math.Abs(along) + radius <= (Width(wall) - 0.75) / 2) return false;
        double dx = Math.Max(0, Math.Abs(along) - Width(wall) / 2);
        double dy = Math.Max(0, Math.Abs(-dx0 * tangent.Y + dy0 * tangent.X) - Depth(wall) / 2);
        return dx * dx + dy * dy < radius * radius - 1e-8;
    }

    public static float ActorWalkHeight(WallComponent wall, GameMap? map = null)
    {
        var target = wall.IsGateWing && wall.SharedSection is { IsGate: true } gate ? gate : wall;
        float Surface(WallComponent support)
        {
            var delta = wall.Center - support.Center;
            float x = Vector2.Dot(delta, support.Tangent);
            float z = -delta.X * support.Tangent.Y + delta.Y * support.Tangent.X;
            float height = Fragments(support).Where(part => part.Kind is WallFragmentKind.Body or WallFragmentKind.Trim &&
                    Math.Abs(x - part.Center.X) <= part.Size.X / 2 + 0.00001f &&
                    Math.Abs(z - part.Center.Z) <= part.Size.Z / 2 + 0.00001f)
                .Select(part => part.Center.Y + part.Size.Y / 2).DefaultIfEmpty(0).Max();
            return map is null ? height : height + GroundHeight(support, map) -
                TerrainSprite.Elevation(map, wall.Cell.X, wall.Cell.Y);
        }
        float height = Surface(target);
        if (wall.Settlement is { } settlement)
            for (int y = wall.Cell.Y - 1; y <= wall.Cell.Y + 1; y++)
                for (int x = wall.Cell.X - 1; x <= wall.Cell.X + 1; x++)
                    if (settlement.WallAt(new(x, y)) is { IsTower: true, Health: > 0 } tower)
                        height = Math.Max(height, Surface(tower));
        return height;
    }

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
        if (wall.IsGateWing && wall.SharedSection is { IsGate: true } gate) return TopHeight(gate);
        if (wall.Health == 0) return 0.2f;
        if (wall.IsGate)
            return NominalHeight(wall) * Math.Max(ColumnFraction(wall, 0, 5), ColumnFraction(wall, 4, 5));
        int count = wall.IsTower ? 9 : 5;
        float top = 0;
        for (int i = 0; i < count; i++) top = Math.Max(top, ColumnFraction(wall, i, count));
        return NominalHeight(wall) * top;
    }

    public static IReadOnlyList<WallFragment> Fragments(WallComponent wall)
        => Fragments(wall, wall.Settlement?.AttachedMap);

    public static IReadOnlyList<WallFragment> Fragments(WallComponent wall, GameMap? map)
    {
        if (wall.IsGateWing) return [];
        int band = DamageBand(wall);
        float nominal = NominalHeight(wall);
        var parts = new List<WallFragment>();
        float exterior = ExteriorSide(wall);
        var openings = wall.IsTower ? WalkwayLadders(wall).Select(ladder => ladder.Position).ToList() : [];
        if (HasAccessLadder(wall)) openings.Add(LadderPosition(wall));
        void Box(Vector3 center, Vector3 size, WallFragmentKind kind, float deckThickness = 0)
        {
            if (kind != WallFragmentKind.Body || wall.IsGate)
            {
                parts.Add(new(center, size, kind));
                return;
            }
            float parapet = Math.Min(WorldScale.Feet(3), size.Y);
            float floor = Math.Max(0, size.Y - parapet - deckThickness);
            if (floor > 0)
                parts.Add(new(center - Vector3.UnitY * ((parapet + deckThickness) / 2),
                    size with { Y = floor }, kind));
            float thickness = Math.Min(WorldScale.Feet(1), size.Z);
            float y = center.Y + size.Y / 2 - parapet / 2;
            if (wall.IsTower && band == 0)
            {
                var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
                foreach (var direction in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY })
                {
                    bool side = Math.Abs(direction.X) > .5f;
                    float length = side ? size.Z : size.X;
                    float edge = (side ? size.X : size.Z) / 2 - thickness / 2;
                    var cuts = openings.Select(position =>
                    {
                        var delta = position - wall.Center;
                        var local = new Vector2(Vector2.Dot(delta, wall.Tangent), Vector2.Dot(delta, normal));
                        return (Point: local, Along: side ? local.Y : local.X);
                    }).Where(opening => Vector2.Dot(opening.Point, direction) > edge - .1f &&
                        Math.Abs(side ? opening.Point.Y : opening.Point.X) <= length / 2)
                        .Select(opening => (Start: opening.Along - .45f, End: opening.Along + .45f)).OrderBy(cut => cut.Start);
                    float start = -length / 2;
                    void Rail(float end)
                    {
                        if (end <= start + .001f) return;
                        var position = direction * edge + (side ? Vector2.UnitY : Vector2.UnitX) * ((start + end) / 2);
                        parts.Add(new(new(position.X, y, position.Y),
                            side ? new(thickness, parapet, end - start) : new(end - start, parapet, thickness), kind));
                    }
                    foreach (var cut in cuts)
                    {
                        Rail(Math.Clamp(cut.Start, -length / 2, length / 2));
                        start = Math.Max(start, Math.Min(length / 2, cut.End));
                    }
                    Rail(length / 2);
                }
                return;
            }
            parts.Add(new(new(center.X, y, center.Z + exterior * (size.Z - thickness) / 2),
                new(size.X, parapet, thickness), kind));
            if (wall.IsTower && band == 0)
            {
                float opening = .9f;
                float shoulder = (size.X - opening) / 2;
                foreach (int side in new[] { -1, 1 })
                    parts.Add(new(new(center.X + side * (opening + shoulder) / 2, y,
                        center.Z - exterior * (size.Z - thickness) / 2), new(shoulder, parapet, thickness), kind));
                foreach (int side in new[] { -1, 1 })
                    parts.Add(new(new(center.X + side * (size.X - thickness) / 2, y, center.Z),
                        new(thickness, parapet, Math.Max(thickness, size.Z - thickness * 2)), kind));
            }
        }
        if (wall.Health > 0)
        {
            if (wall.IsGate)
            {
                float left = nominal * ColumnFraction(wall, 0, 5);
                float right = nominal * ColumnFraction(wall, 4, 5);
                float depth = Depth(wall), width = Width(wall);
                const float post = 0.375f;
                Box(new(-(width - post) / 2, left / 2, 0), new(post, left, depth), WallFragmentKind.Body);
                Box(new((width - post) / 2, right / 2, 0), new(post, right, depth), WallFragmentKind.Body);
                float lintel = Math.Min(left, right);
                if (band < 4)
                {
                    float walk = WalkHeight(wall);
                    Box(new(0, walk - 0.08f, WalkwayOffset(wall)),
                        new(width, 0.16f, Math.Max(depth, WalkwayWidth + WorldScale.Feet(1)) + .04f), WallFragmentKind.Body);
                    Box(new(0, lintel - WorldScale.Feet(1.5f),
                        exterior * (depth - WorldScale.Feet(1)) / 2),
                        new(width, WorldScale.Feet(3), WorldScale.Feet(1)), WallFragmentKind.Body);
                }
                for (int i = 0; i < 3; i++)
                {
                    if (band >= 4 && i == 1) continue;
                    float height = nominal * 0.75f * ColumnFraction(wall, i + 1, 5);
                    float leaf = (width - post * 2) / 3;
                    float along = (i - 1) * leaf;
                    Box(new(along, height / 2, 0), new(leaf, height, 0.16f), WallFragmentKind.Door);
                    foreach (int side in new[] { -1, 1 })
                        foreach (float fraction in new[] { .2f, .5f, .8f })
                        {
                            float braceHeight = Math.Min(WorldScale.Feet(.7f), height * .12f);
                            Box(new(along, height * fraction, side * .14f),
                                new(leaf * .88f, braceHeight, .12f), WallFragmentKind.DoorBrace);
                            float stud = Math.Min(WorldScale.Feet(.3f), braceHeight * .65f);
                            foreach (int edge in new[] { -1, 1 })
                                Box(new(along + edge * leaf * .32f, height * fraction, side * .22f),
                                    new(stud, stud, .04f), WallFragmentKind.DoorStud);
                        }
                }
            }
            else if (band == 0)
            {
                float width = Width(wall), depth = Depth(wall);
                Box(new(0, nominal / 2, 0), new(width, nominal, depth), WallFragmentKind.Body, 0.16f);
                float walkDepth = wall.IsTower ? depth : Math.Max(depth, WalkwayWidth + WorldScale.Feet(1));
                Box(new(0, WalkHeight(wall) - 0.08f, wall.IsTower ? 0 : WalkwayOffset(wall)),
                    new(width, 0.16f, walkDepth + 0.04f), WallFragmentKind.Trim);
            }
            else
            {
                int columns = wall.IsTower ? 3 : 5, rows = wall.IsTower ? 3 : 1;
                float width = Width(wall), depth = Depth(wall);
                for (int z = 0; z < rows; z++)
                    for (int x = 0; x < columns; x++)
                    {
                        float height = nominal * ColumnFraction(wall, z * columns + x, columns * rows);
                        var center = new Vector3(((x + 0.5f) / columns - 0.5f) * width, height / 2,
                            ((z + 0.5f) / rows - 0.5f) * depth);
                        Box(center, new(width / columns, height, depth / rows), WallFragmentKind.Body, 0.12f);
                        Box(center with { Y = Math.Max(0.06f, height - WorldScale.Feet(3) - 0.06f),
                            Z = center.Z + (wall.IsTower ? 0 : WalkwayOffset(wall)) },
                            new(width / columns, 0.12f, (wall.IsTower ? depth / rows :
                                Math.Max(depth, WalkwayWidth + WorldScale.Feet(1))) + 0.04f), WallFragmentKind.Trim);
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
                            var merlon = wall.Center + wall.Tangent * (x * (Width(wall) / 2 - .25f)) +
                                new Vector2(-wall.Tangent.Y, wall.Tangent.X) * (z * (Depth(wall) / 2 - .25f));
                            if (openings.Any(opening => Vector2.DistanceSquared(opening, merlon) < .7f * .7f)) continue;
                            int index = (z + 1) * 3 + x + 1;
                            if (band > CellHash.Pick(wall.Cell.X + index, wall.Cell.Y + 7, 9)) continue;
                            float top = nominal * ColumnFraction(wall, index, 9);
                            Box(new(x * (Width(wall) / 2 - 0.25f), top + 0.25f,
                                z * (Depth(wall) / 2 - 0.25f)), new(0.5f, 0.5f, 0.5f), WallFragmentKind.Merlon);
                        }
                }
                else if (band < 3)
                    Box(new(0, nominal * ColumnFraction(wall, 2, 5) + 0.25f,
                            exterior * (Depth(wall) - WorldScale.Feet(1)) / 2),
                        new(0.45f, 0.5f, WorldScale.Feet(1)), WallFragmentKind.Merlon);
            }
            if (wall.Defense != WallDefenseKind.None && band > 0)
                Box(new(0, TopHeight(wall) - 0.06f, 0),
                    new(wall.IsGate ? Width(wall) : 0.85f, 0.12f, wall.IsGate ? Depth(wall) : 0.8f),
                    WallFragmentKind.Trim);
        }
        int rubble = band == 0 ? 0 : 1 + band / 2;
        for (int i = 0; i < rubble; i++)
        {
            float spread = wall.IsTower ? 1.2f : 0.85f;
            Box(new(((i + 0.5f) / rubble - 0.5f) * spread, 0.07f + (i % 2) * 0.025f,
                    (i % 2 == 0 ? -1 : 1) * (wall.IsTower ? 0.75f : 0.34f)),
                new(0.22f + (i % 3) * 0.04f, 0.14f + (i % 2) * 0.05f, 0.2f), WallFragmentKind.Rubble);
        }
        return map is null ? parts : WallWaterLayout.Apply(wall, map, parts);
    }
}
