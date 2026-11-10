using System.Numerics;
using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public enum BuildingAccessKind { Door, Gate, OpenBay, PublicSpace }

public readonly record struct ModelEntrance(Vector3 Threshold, Vector2 Direction, float Width, BuildingAccessKind Kind);
public sealed record BuildingEntrance(Vector3 Threshold, Vector2 Direction, float Width, BuildingAccessKind Kind,
    Vector2 Boundary, IReadOnlyList<Vector2> Approach);
public sealed record EntranceAccess(int Property, int Start, BuildingEntrance Entrance, Vector2 Contact);

public static class BuildingEntranceLayout
{
    public const float GateWidth = .28f;
    public const float GateFrontOffset = .1175f;

    public static (float Width, float Depth, float Height) Body(int width, int depth, int level) =>
        (Math.Max(.55f, width * .84f), Math.Max(.55f, depth * .78f), .45f + level * .2f);

    public static float ModelScale(BuildingType type, CellRect area, int rotation)
    {
        float width = rotation % 2 == 0 ? area.Width : area.Height;
        float depth = rotation % 2 == 0 ? area.Height : area.Width;
        return Math.Min(type.ModelScale, Math.Min(width / type.ModelWidth, depth / type.ModelDepth));
    }

    public static Vector3 Center(GameMap map, CellRect area, BuildingType type)
    {
        float ground = area.Cells().Max(p => TerrainSprite.Elevation(map, p.X, p.Y));
        var center = new Vector3(area.X + area.Width / 2f, ground, area.Y + area.Height / 2f);
        if (type.Name == "Wall House" && map.SettlementBounds is { } bounds)
            center += new Vector3(area.X == bounds.X + 1 ? -.35f : area.Right == bounds.Right - 1 ? .35f : 0,
                0, area.Y == bounds.Y + 1 ? -.35f : area.Bottom == bounds.Bottom - 1 ? .35f : 0);
        return center;
    }

    public static IReadOnlyList<ModelEntrance> Model(BuildingType type, int width, int depth, Pos anchor)
    {
        var body = Body(width, depth, type.Level);
        ModelEntrance Entry(float z, float y, float span, BuildingAccessKind kind = BuildingAccessKind.Door) =>
            new(new(0, y, z), Vector2.UnitY, span, kind);
        if (type.Zone == ZoneType.Residential)
            return [HouseAppearance.At(type, anchor.X, anchor.Y).EntranceOnPlot(width, depth, type.Level)];
        if (type.Zone is ZoneType.Commercial or ZoneType.Industrial)
            return [WorkplaceAppearance.At(type, anchor.X, anchor.Y, width, depth).Entrance];
        if (type.Name == "Woodlot") return [WoodlotArtwork.Entrance(width, depth)];
        if (type.Name is "Archer Barracks" or "Guard Post")
            return [Entry(body.Depth * .35f, BuildingDetailLayout.HalfTimberBeamHeight, BuildingDetailLayout.DoorWidth)];
        if (type.Service == ServiceKind.Defence || type.Name == "Gaol")
            return [Entry(body.Depth / 2 + GateFrontOffset, .02f, GateWidth, BuildingAccessKind.Gate)];
        if (type.Name == "Town Well")
            return [Entry(.29f, 0, .30f, BuildingAccessKind.PublicSpace)];
        if (type.Name == "Market Cross")
            return [Entry(.28f, 0, .30f, BuildingAccessKind.PublicSpace)];
        if (type.IsMonument)
            return [Entry(2.32f, 0, .40f, BuildingAccessKind.PublicSpace)];
        if (type.Name is "Village Green" or "Charcoal Burners" or "Aqueduct")
            return Enumerable.Range(0, 4).Select(rotation =>
            {
                var direction = Rotate(Vector2.UnitY, rotation);
                float radius = direction.X == 0 ? depth / 2f - .03f : width / 2f - .03f;
                return new ModelEntrance(new(direction.X * radius, 0, direction.Y * radius),
                    direction, .20f, BuildingAccessKind.PublicSpace);
            }).ToArray();
        return [Entry(body.Depth / 2, BuildingDetailLayout.HalfTimberBeamHeight +
            (type.Name == "Granary" ? .15f : 0), BuildingDetailLayout.DoorWidth)];
    }

    public static IReadOnlyList<BuildingEntrance> ForBuilding(GameMap map, CellRect area, BuildingType type,
        int? previewRotation = null)
    {
        int rotation = previewRotation ?? map.BuildingRotations.GetValueOrDefault(map.Index(area.X, area.Y));
        float scale = ModelScale(type, area, rotation);
        int width = Math.Max(1, (int)((rotation % 2 == 0 ? area.Width : area.Height) / scale));
        int depth = Math.Max(1, (int)((rotation % 2 == 0 ? area.Height : area.Width) / scale));
        var center = Center(map, area, type);
        return Model(type, width, depth, new(area.X, area.Y)).Select(model =>
        {
            var offset = Rotate(new(model.Threshold.X, model.Threshold.Z), rotation) * scale;
            var threshold = center + new Vector3(offset.X, model.Threshold.Y * scale, offset.Y);
            var direction = Rotate(model.Direction, rotation);
            var boundary = direction.X == 0
                ? new Vector2(threshold.X, direction.Y > 0 ? area.Bottom + 1 : area.Y)
                : new Vector2(direction.X > 0 ? area.Right + 1 : area.X, threshold.Z);
            IReadOnlyList<Vector2> approach = new[] { boundary, new Vector2(threshold.X, threshold.Z) };
            if (map.HouseYardCells.TryGetValue(map.Index(area.X, area.Y), out int yardIndex))
            {
                var yard = map.PosOf(yardIndex);
                var yardCenter = new Vector2(yard.X + .5f, yard.Y + .5f);
                var across = new Vector2(-direction.Y, direction.X);
                if (Math.Abs(Vector2.Dot(yardCenter - boundary, direction) - .5f) < .001f &&
                    Math.Abs(Vector2.Dot(yardCenter - boundary, across)) <= .5f + model.Width * scale / 2)
                {
                    float inset = Math.Min(.35f, Math.Max(.01f,
                        Vector2.Dot(boundary - new Vector2(threshold.X, threshold.Z), direction) * .3f));
                    approach = new[] { yardCenter + direction * .5f, yardCenter - direction * (.5f + inset),
                        boundary - direction * inset, new Vector2(threshold.X, threshold.Z) };
                }
            }
            return new BuildingEntrance(threshold, direction, model.Width * scale, model.Kind, boundary, approach);
        }).ToArray();
    }

    public static IReadOnlyList<EntranceAccess> Accesses(GameMap map, CellRect area)
    {
        int owner = map.Index(area.X, area.Y);
        map.EntranceAccesses ??= [];
        if (map.EntranceAccesses.TryGetValue(owner, out var cached)) return cached;
        var type = map.BuildingAt(area.X, area.Y) ??
            throw new InvalidOperationException("Entrance access requires a building footprint.");
        var result = new List<EntranceAccess>();
        foreach (var entrance in ForBuilding(map, area, type))
        {
            bool vertical = entrance.Direction.X == 0;
            float along = vertical ? entrance.Boundary.X : entrance.Boundary.Y;
            int minimum = Math.Max(vertical ? area.X : area.Y, (int)MathF.Floor(along - entrance.Width / 2));
            int maximum = Math.Min(vertical ? area.Right : area.Bottom, (int)MathF.Floor(along + entrance.Width / 2));
            for (int position = minimum; position <= maximum; position++)
            {
                var inside = vertical ? new Pos(position, entrance.Direction.Y > 0 ? area.Bottom : area.Y) :
                    new Pos(entrance.Direction.X > 0 ? area.Right : area.X, position);
                var direction = new Pos((int)entrance.Direction.X, (int)entrance.Direction.Y);
                var outside = inside.Offset(direction.X, direction.Y);
                var contact = entrance.Boundary;
                if (!map.InBounds(outside)) continue;
                if (map.HouseYardOwner(map.Index(outside.X, outside.Y)) == owner)
                {
                    inside = outside;
                    outside = outside.Offset(direction.X, direction.Y);
                    contact = new(inside.X + .5f + direction.X * .5f, inside.Y + .5f + direction.Y * .5f);
                }
                if (!map.InBounds(outside) || map.BuildingAt(outside.X, outside.Y) is not null ||
                    map.IsHouseYardCell(map.Index(outside.X, outside.Y)) ||
                    !TravelRoutes.CanFootpathStep(map, map.Index(inside.X, inside.Y), map.Index(outside.X, outside.Y))) continue;
                result.Add(new(map.Index(inside.X, inside.Y), map.Index(outside.X, outside.Y), entrance, contact));
            }
        }
        map.EntranceAccesses[owner] = result;
        return result;
    }

    public static Vector2 Contact(GameMap map, int property, int outside)
    {
        int owner = map.BuildingLayer[property] != 0 ? map.BuildingAnchor(property) : map.HouseYardOwner(property);
        if (owner >= 0 && map.BuildingFootprints.TryGetValue(owner, out var area))
        {
            var access = Accesses(map, area).FirstOrDefault(candidate => candidate.Property == property && candidate.Start == outside);
            if (access is not null) return access.Contact;
        }
        return new((property % map.Width + outside % map.Width + 1) / 2f,
            (property / map.Width + outside / map.Width + 1) / 2f);
    }

    public static Vector2 Rotate(Vector2 point, int rotation) => rotation switch
    {
        0 => point, 1 => new(-point.Y, point.X), 2 => -point, 3 => new(point.Y, -point.X),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
    };
}
