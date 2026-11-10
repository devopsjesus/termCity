using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public readonly record struct DefenseStation(Vector2 Position, Vector2 Forward);
public readonly record struct DefensePart(Vector3 Center, Vector3 Size, Quaternion Rotation, int Color,
    int Station = -1, bool Moving = false, Vector3 Pivot = default, Vector3 Axis = default);
public readonly record struct DefenseFlowSegment(Vector3 Start, Vector3 End, float Width);
public readonly record struct OilPoolPatch(Vector2 Center, Vector2 Size);
public readonly record struct OilSheetSection(Vector3 Left, Vector3 Ridge, Vector3 Right, float Thickness);

public static class TowerDefenseArtwork
{
    public const float Gravity = 32.2f * WorldScale.UnitsPerFoot;
    public const float WindupSeconds = .3f;
    public const float PourSeconds = 1.6f;
    public const float ImpactSeconds = .4f;
    public const int StairCount = 4;
    public const int MaximumStations = 5;

    public static IReadOnlyList<DefenseStation> Stations(WallComponent? wall, int? facing = null)
    {
        var directions = new List<Vector2>();
        if (wall is { Start: not null })
            directions.Add(new Vector2(-wall.Tangent.Y, wall.Tangent.X) * (facing ?? wall.DefenseFacing));
        else if (wall?.Settlement is { } owner)
        {
            if (wall.Cell.Y == owner.Bounds.Y) directions.Add(new(0, -1));
            if (wall.Cell.X == owner.Bounds.Right) directions.Add(new(1, 0));
            if (wall.Cell.Y == owner.Bounds.Bottom) directions.Add(new(0, 1));
            if (wall.Cell.X == owner.Bounds.X) directions.Add(new(-1, 0));
        }
        else directions.Add(new(0, -1));
        float width = wall is null ? WorldScale.TowerWidth : WallDamageLayout.Width(wall);
        float depth = wall is null ? WorldScale.TowerWidth : WallDamageLayout.Depth(wall);
        var stations = new List<DefenseStation>();
        foreach (var forward in directions)
        {
            var right = new Vector2(forward.Y, -forward.X);
            float along = (width / 2 - .25f) / 2;
            foreach (int side in wall is { IsTower: false, IsGate: false } ? new[] { 0 } : new[] { -1, 1 })
                stations.Add(new(forward * (depth / 2 - .25f) + right * (along * side), forward));
        }
        if (wall is null || wall.IsTower)
            foreach (var forward in directions)
            {
                var right = new Vector2(forward.Y, -forward.X);
                foreach (var side in new[] { right, -right })
                {
                    if (directions.Any(face => Vector2.Dot(face, side) > .999f) ||
                        wall is not null && Vector2.Dot(WallDamageLayout.LadderInward(wall), side) > .999f) continue;
                    var position = side * (depth / 2 - .25f) + forward * ((width / 2 - .25f) / 2);
                    if (!stations.Any(station => Vector2.DistanceSquared(station.Position, position) < .000001f))
                        stations.Add(new(position, side));
                }
            }
        return stations;
    }

    public static int NearestStation(WallComponent wall, Vector2 target)
    {
        var relative = target - wall.Center;
        var stations = Stations(wall);
        return Enumerable.Range(0, stations.Count).MinBy(index =>
            Vector2.DistanceSquared(stations[index].Position, relative));
    }

    public static float Rise(WallComponent? wall, GameMap? map = null) => wall is null
        ? WorldScale.Feet(3) : Math.Max(0, WallDamageLayout.TopHeight(wall) +
            (!wall.IsTower && !wall.IsGate ? .5f : 0) - WallDamageLayout.ActorWalkHeight(wall, map));

    public static IReadOnlyList<DefensePart> Parts(WallComponent? wall, WallDefenseKind kind, float rise, int? facing = null)
    {
        if (!float.IsFinite(rise) || rise < 0) throw new ArgumentOutOfRangeException(nameof(rise));
        if (kind is not (WallDefenseKind.OilCauldron or WallDefenseKind.Rocks))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var stations = Stations(wall, facing);
        var parts = new List<DefensePart>();
        void Box(Vector3 center, Vector3 size, int color, Quaternion rotation, int station = -1, bool moving = false)
        {
            Vector3 pivot = default, axis = default;
            if (moving)
            {
                var mount = stations[station];
                pivot = new Vector3(mount.Position.X, rise + .12f, mount.Position.Y) +
                    new Vector3(mount.Forward.X, 0, mount.Forward.Y) * .24f;
                axis = new(mount.Forward.Y, 0, -mount.Forward.X);
            }
            parts.Add(new(center, size, rotation, color, station, moving, pivot, axis));
        }
        var faces = stations.Select(station => station.Forward).Distinct().ToArray();
        float depth = wall is null ? WorldScale.TowerWidth : WallDamageLayout.Depth(wall);
        float width = wall is null ? WorldScale.TowerWidth : WallDamageLayout.Width(wall);
        for (int face = 0; face < faces.Length; face++)
        {
            var forward = faces[face];
            var right = new Vector2(forward.Y, -forward.X);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Y));
            float left = -width / 2 + .3f, end = width / 2 - .3f;
            for (int previous = 0; previous < face; previous++)
            {
                float toward = Vector2.Dot(right, faces[previous]);
                if (toward > .5f) end = Math.Min(end, width / 2 - .9f);
                else if (toward < -.5f) left = Math.Max(left, -width / 2 + .9f);
            }
            Vector3 Point(float x, float y, float z) => Vector3.Transform(new(x, y, z), rotation);
            float platform = depth / 2 - .6f;
            Box(Point((left + end) / 2, Math.Max(.04f, rise - .04f), platform),
                new(end - left, .08f, .6f), 0x79634b, rotation);
            if (face == 0 && rise > .01f)
            {
                float run = Math.Min(.9f, Math.Max(.3f, depth - .6f));
                for (int step = 0; step < StairCount; step++)
                {
                    float height = rise * (step + 1) / StairCount;
                    Box(Point(0, height / 2, platform - .3f - run + (step + .5f) * run / StairCount),
                        new(.7f, height, run / StairCount), 0xa69666, rotation);
                }
            }
        }
        for (int index = 0; index < stations.Count; index++)
        {
            var station = stations[index];
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
                MathF.Atan2(station.Forward.X, station.Forward.Y));
            var origin = new Vector3(station.Position.X, rise, station.Position.Y);
            void Part(Vector3 at, Vector3 size, int color, bool moving = false) =>
                Box(origin + Vector3.Transform(at, rotation), size, color, rotation, index, moving);
            Part(new(0, .04f, -.06f), new(.68f, .08f, .55f), 0x586b71);
            if (kind == WallDefenseKind.OilCauldron)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    Part(new(side * .31f, .18f, .22f), new(.06f, .28f, .1f), 0x79634b);
                    Part(new(side * .25f, .28f, 0), new(.06f, .38f, .5f), 0x343b3e, true);
                    Part(new(0, .28f, side * .22f), new(.44f, .38f, .06f), 0x343b3e, true);
                    Part(new(side * .26f, .49f, 0), new(.08f, .06f, .54f), 0x798486, true);
                    Part(new(0, .49f, side * .24f), new(.44f, .06f, .08f), 0x798486, true);
                }
                Part(new(0, .1f, 0), new(.44f, .06f, .4f), 0x343b3e, true);
                Part(new(0, .39f, 0), new(.42f, .02f, .36f), 0x8c6028, true);
                Part(new(0, .12f, .24f), new(.74f, .06f, .06f), 0x798486);
            }
            else
            {
                Part(new(0, .1f, .2f), new(.68f, .12f, .07f), 0x79634b);
                for (int rock = 0; rock < 3; rock++)
                    Part(new((rock - 1) * .19f, rock == 1 ? .44f : .2f, -.02f),
                        new(.25f, .24f, .28f), rock == 1 ? 0x79796e : 0xa4a599);
            }
        }
        return parts;
    }

    public static float TipAngle(double elapsed)
    {
        if (!double.IsFinite(elapsed)) throw new ArgumentOutOfRangeException(nameof(elapsed));
        static float Ease(float value) => value * value * (3 - 2 * value);
        if (elapsed < 0) return 0;
        if (elapsed < WindupSeconds) return Ease((float)elapsed / WindupSeconds) * 1.05f;
        if (elapsed < WindupSeconds + PourSeconds) return 1.05f;
        return (1 - Ease(Math.Clamp((float)(elapsed - WindupSeconds - PourSeconds) / .3f, 0, 1))) * 1.05f;
    }

    public static DefensePart Pose(DefensePart part, float angle)
    {
        if (!part.Moving || angle == 0) return part;
        var rotation = Quaternion.CreateFromAxisAngle(part.Axis, angle);
        return part with
        {
            Center = part.Pivot + Vector3.Transform(part.Center - part.Pivot, rotation),
            Rotation = Quaternion.Normalize(rotation * part.Rotation),
        };
    }

    public static Vector3 Lip(DefenseStation station, float rise, float angle)
    {
        var forward = new Vector3(station.Forward.X, 0, station.Forward.Y);
        var pivot = new Vector3(station.Position.X, rise + .12f, station.Position.Y) + forward * .24f;
        var rotation = Quaternion.CreateFromAxisAngle(new(station.Forward.Y, 0, -station.Forward.X), angle);
        return pivot + Vector3.Transform(new(0, .39f, 0), rotation);
    }

    public static float FallSeconds(float height)
    {
        if (!float.IsFinite(height)) throw new ArgumentOutOfRangeException(nameof(height));
        return MathF.Sqrt(2 * Math.Max(.1f, height) / Gravity);
    }

    public static Vector3 DropPoint(Vector3 start, Vector3 end, float progress)
    {
        if (!float.IsFinite(progress) || progress < 0 || progress > 1)
            throw new ArgumentOutOfRangeException(nameof(progress));
        if (progress == 0) return start;
        if (progress == 1) return end;
        var point = Vector3.Lerp(start, end, progress);
        return point with { Y = start.Y + (end.Y - start.Y) * progress * progress };
    }

    public static float OilPoolSpread(double age, bool animated = true)
    {
        if (!double.IsFinite(age)) throw new ArgumentOutOfRangeException(nameof(age));
        if (age < 0) return 0;
        if (!animated) return 1;
        float t = (float)Math.Clamp(age / .18, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static IReadOnlyList<OilPoolPatch> OilPool(float size, double age, bool animated = true)
    {
        if (!float.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        float span = size * OilPoolSpread(age, animated);
        if (span <= .00001f) return [];
        float[] widths = [.6f, .85f, 1f, .85f, .6f];
        return widths.Select((width, row) => new OilPoolPatch(new(0, (row - 2) * span / 5),
            new(span * width, span / 5))).ToArray();
    }

    public static IReadOnlyList<DefenseFlowSegment> OilFlow(Vector3 start, Vector3 end, double elapsed, float fallSeconds)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0 || !float.IsFinite(fallSeconds) || fallSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        float front = Math.Clamp((float)(elapsed - WindupSeconds) / fallSeconds, 0, 1);
        float tail = Math.Clamp((float)(elapsed - WindupSeconds - PourSeconds) / fallSeconds, 0, 1);
        if (front <= tail) return [];
        var horizontal = end - start;
        horizontal.Y = 0;
        var across = horizontal.LengthSquared() > .00001f
            ? Vector3.Normalize(new Vector3(horizontal.Z, 0, -horizontal.X)) : Vector3.UnitX;
        Vector3 Point(float t)
        {
            if (t <= 0) return start;
            if (t >= 1) return end;
            float envelope = MathF.Sin(t * MathF.PI);
            return DropPoint(start, end, t) + across *
                (MathF.Sin(t * 9 - (float)elapsed * 5) * .065f * envelope);
        }
        return Enumerable.Range(0, 16).Select(index =>
        {
            float a = tail + (front - tail) * index / 16;
            float b = tail + (front - tail) * (index + 1) / 16;
            float pulse = .9f + MathF.Sin(b * 13 - (float)elapsed * 7) * .15f;
            return new DefenseFlowSegment(Point(a), Point(b), (.32f + b * .26f) * pulse);
        }).ToArray();
    }

    public static IReadOnlyList<OilSheetSection> OilSheet(Vector3 start, Vector3 end, double elapsed, float fallSeconds)
    {
        var flow = OilFlow(start, end, elapsed, fallSeconds);
        if (flow.Count == 0) return [];
        var horizontal = end - start;
        horizontal.Y = 0;
        var across = horizontal.LengthSquared() > .00001f
            ? Vector3.Normalize(new Vector3(horizontal.Z, 0, -horizontal.X)) : Vector3.UnitX;
        var outward = Vector3.Cross(across, Vector3.UnitY);
        var sections = new OilSheetSection[flow.Count + 1];
        for (int i = 0; i < sections.Length; i++)
        {
            var center = i == 0 ? flow[0].Start : flow[i - 1].End;
            float width = i == 0 ? .32f : i == flow.Count ? flow[^1].Width :
                (flow[i - 1].Width + flow[i].Width) / 2;
            if (i == flow.Count && elapsed < WindupSeconds + fallSeconds) width = .10f;
            if (i == 0 && elapsed > WindupSeconds + PourSeconds) width = .08f;
            float fraction = i / (float)flow.Count;
            float fold = (.035f + MathF.Sin(fraction * 8 - (float)elapsed * 6) * .02f) * MathF.Sin(fraction * MathF.PI);
            if (i == 0 || i == flow.Count) fold = 0;
            float thickness = .08f / (1 + fraction * 2);
            sections[i] = new(center - across * width / 2, center + outward * fold,
                center + across * width / 2, thickness);
        }
        return sections;
    }
}
