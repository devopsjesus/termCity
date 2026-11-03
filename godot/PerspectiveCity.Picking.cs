using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed class PickGroup(Pos owner, Aabb bounds)
    {
        internal Pos Owner { get; } = owner;
        internal Aabb Bounds = bounds;
        internal readonly List<Aabb> Boxes = [];
    }

    private readonly Dictionary<Pos, PickGroup> _structurePickGroups = [];
    private readonly Dictionary<Pos, PickGroup> _naturalPickGroups = [];
    internal IReadOnlyList<Aabb> NaturalVolumes(Pos owner) =>
        _naturalPickGroups.TryGetValue(owner, out var group) ? group.Boxes : [];

    public bool PickNaturalFeature(Vector2 pointer, out Pos owner)
    {
        var pixel = ViewportPoint(pointer);
        var origin = _camera.ProjectRayOrigin(pixel);
        return NaturalHit(origin, _camera.ProjectRayNormal(pixel), out owner, out float distance) &&
            distance <= origin.DistanceTo(GroundHit(pointer).Point) + .001f;
    }

    private static void RecordPickBox(Dictionary<Pos, PickGroup> groups, Pos owner, Aabb box)
    {
        if (!groups.TryGetValue(owner, out var group))
            groups.Add(owner, group = new(owner, box));
        else group.Bounds = group.Bounds.Merge(box);
        group.Boxes.Add(box);
    }

    private void BuildStructurePicking()
    {
        _structurePickGroups.Clear();
        foreach (var box in _pickBoxes.Concat(_yardBoxes))
            RecordPickBox(_structurePickGroups, box.Anchor, box.Bounds);
    }

    private IEnumerable<(Aabb Bounds, Pos Anchor)> StructureCandidates(Vector3 origin, Vector3 direction)
    {
        foreach (var group in _structurePickGroups.Values)
        {
            if (!RayBox(group.Bounds, origin, direction, out _)) continue;
            foreach (var box in group.Boxes) yield return (box, group.Owner);
        }
    }

    private static bool RayBox(Aabb box, Vector3 origin, Vector3 direction, out float distance)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(direction[axis]) < .000001f)
            {
                if (origin[axis] < box.Position[axis] || origin[axis] > box.End[axis])
                { distance = 0; return false; }
                continue;
            }
            float a = (box.Position[axis] - origin[axis]) / direction[axis];
            float b = (box.End[axis] - origin[axis]) / direction[axis];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
        }
        distance = near;
        return near <= far;
    }

    private bool NaturalHit(Vector3 origin, Vector3 direction, out Pos owner, out float distance)
    {
        distance = float.PositiveInfinity;
        owner = default;
        foreach (var group in _naturalPickGroups.Values)
        {
            if (!RayBox(group.Bounds, origin, direction, out float first) || first >= distance) continue;
            foreach (var box in group.Boxes)
                if (RayBox(box, origin, direction, out float hit) && hit < distance)
                { distance = hit; owner = group.Owner; }
        }
        return float.IsFinite(distance);
    }
}
