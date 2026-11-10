using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed class SiegePage(MultiMeshInstance3D node)
    {
        internal readonly MultiMeshInstance3D Node = node;
        internal MultiMesh Mesh => Node.Multimesh;
        internal readonly List<Transform3D> Transforms = [];
        internal readonly Aabb UnitBounds = node.Multimesh.Mesh.GetAabb();
        internal int PublishedCount;
        internal ulong LastUsed;
    }
    private sealed class SiegePages
    {
        internal readonly Dictionary<(int Color, int X, int Y), SiegePage> Pages = [];
        internal readonly Dictionary<(int Color, int Index), (SiegePage Page, int Index)> Slots = [];
    }
    private readonly Dictionary<Dictionary<int, MultiMeshInstance3D>, SiegePages> _siegePages = [];
    internal int SpatialSiegePageCount => _siegePages.Values.Sum(group => group.Pages.Count);

    private void SyncSpatialSiege(Node3D root, Dictionary<int, MultiMeshInstance3D> owner,
        Dictionary<int, List<Transform3D>> batches, Mesh? model, bool buffered)
    {
        if (!_siegePages.TryGetValue(owner, out var group)) _siegePages[owner] = group = new();
        ulong frame = Engine.GetProcessFrames();
        group.Slots.Clear();
        foreach (var page in group.Pages.Values)
        {
            page.Transforms.Clear();
        }
        foreach (var (color, batch) in batches)
            for (int i = 0; i < batch.Count; i++)
            {
                var transform = batch[i];
                var key = (color, (int)Math.Floor(transform.Origin.X / 32), (int)Math.Floor(transform.Origin.Z / 32));
                if (!group.Pages.TryGetValue(key, out var page))
                {
                    var node = new MultiMeshInstance3D
                    {
                        Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = model ?? _cube },
                        MaterialOverride = Material(color, solid: true),
                    };
                    root.AddChild(node);
                    group.Pages[key] = page = new(node);
                }
                page.LastUsed = frame;
                group.Slots[(color, i)] = (page, page.Transforms.Count);
                page.Transforms.Add(transform);
            }
        foreach (var page in group.Pages.Values)
        {
            int count = page.Transforms.Count;
            if (page.Node.Visible != (count > 0)) page.Node.Visible = count > 0;
            if (count == 0)
            {
                if (page.PublishedCount != 0) page.Mesh.VisibleInstanceCount = 0;
                page.PublishedCount = 0;
                continue;
            }
            bool resize = page.Mesh.InstanceCount < count ||
                !ReferenceEquals(root, _siegeActors) && page.Mesh.InstanceCount > Math.Max(1, count) * 2;
            if (resize)
                page.Mesh.InstanceCount = buffered
                    ? (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count) : count;
            bool upload = resize;
            if (!_siegeBuffers.TryGetValue(page.Mesh, out var data) || data.Length != page.Mesh.InstanceCount * 12)
            {
                _siegeBuffers[page.Mesh] = data = new float[page.Mesh.InstanceCount * 12];
                _siegeTransforms[page.Mesh] = new Transform3D[page.Mesh.InstanceCount];
                upload = true;
            }
            bool initialize = upload;
            var previous = _siegeTransforms[page.Mesh];
            bool boundsChanged = initialize || page.PublishedCount != count;
            for (int i = 0; i < count; i++)
            {
                var transform = page.Transforms[i];
                boundsChanged |= previous[i] != transform;
                if (initialize || !previous[i].IsEqualApprox(transform))
                {
                    previous[i] = transform;
                    WriteInstance(data, i * 12, transform);
                    upload = true;
                }
            }
            if (boundsChanged)
            {
                var bounds = page.Transforms[0] * page.UnitBounds;
                for (int i = 1; i < count; i++)
                    bounds = bounds.Merge(page.Transforms[i] * page.UnitBounds);
                page.Mesh.CustomAabb = bounds;
            }
            if (resize || page.PublishedCount != count) page.Mesh.VisibleInstanceCount = count;
            page.PublishedCount = count;
            if (upload)
            {
                page.Mesh.Buffer = data;
                if (ReferenceEquals(root, _siegeActors)) ActorBufferUploads++;
            }
        }
        foreach (var key in group.Pages.Where(pair => pair.Value.Transforms.Count == 0 &&
            frame - pair.Value.LastUsed > 120).Select(pair => pair.Key).ToArray())
        {
            var page = group.Pages[key];
            _siegeBuffers.Remove(page.Mesh);
            _siegeTransforms.Remove(page.Mesh);
            root.RemoveChild(page.Node);
            page.Node.QueueFree();
            page.Mesh.Dispose();
            group.Pages.Remove(key);
        }
    }

    private (MultiMesh Mesh, int Index) SiegeSlot(Dictionary<int, MultiMeshInstance3D> owner, int color, int index)
    {
        var slot = _siegePages[owner].Slots[(color, index)];
        return (slot.Page.Mesh, slot.Index);
    }

    private bool MatchSpatialSiege(Dictionary<int, List<Transform3D>> batches,
        Dictionary<int, MultiMeshInstance3D> owner, bool encoded, bool bounded = true)
    {
        if (!_siegePages.TryGetValue(owner, out var group)) return batches.Values.All(batch => batch.Count == 0);
        if (group.Slots.Count != batches.Values.Sum(batch => batch.Count)) return false;
        foreach (var page in group.Pages.Values.Where(page => page.Transforms.Count > 0))
            if (page.Mesh.VisibleInstanceCount != page.Transforms.Count ||
                bounded && page.Mesh.InstanceCount > page.Transforms.Count * 2) return false;
        foreach (var (color, batch) in batches)
            for (int i = 0; i < batch.Count; i++)
            {
                var slot = SiegeSlot(owner, color, i);
                Transform3D actual;
                if (encoded)
                {
                    var data = _siegeBuffers[slot.Mesh];
                    int offset = slot.Index * 12;
                    actual = new(new Basis(new(data[offset], data[offset + 4], data[offset + 8]),
                        new(data[offset + 1], data[offset + 5], data[offset + 9]),
                        new(data[offset + 2], data[offset + 6], data[offset + 10])),
                        new(data[offset + 3], data[offset + 7], data[offset + 11]));
                }
                else actual = slot.Mesh.GetInstanceTransform(slot.Index);
                if (!batch[i].IsEqualApprox(actual)) return false;
            }
        return true;
    }

    private void DisposeSiegePages()
    {
        foreach (var group in _siegePages.Values)
            foreach (var page in group.Pages.Values) page.Mesh.Dispose();
        _siegePages.Clear();
    }
}
