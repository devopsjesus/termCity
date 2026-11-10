using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed class InstanceBatch(MultiMesh mesh, MultiMeshInstance3D node, float[] buffer)
    {
        internal readonly MultiMesh Mesh = mesh;
        internal readonly MultiMeshInstance3D Node = node;
        internal float[] Buffer = buffer;
    }

    private sealed class RoofBatch(ArrayMesh mesh, MeshInstance3D node, List<SurfaceVertex> vertices)
    {
        internal ArrayMesh Mesh = mesh;
        internal readonly MeshInstance3D Node = node;
        internal List<SurfaceVertex> Vertices = vertices;
    }

    private readonly Node3D _retainedSolids = new();
    private readonly Dictionary<int, InstanceBatch> _retainedInstances = [];
    private readonly Dictionary<int, RoofBatch> _retainedRoofs = [];
    private readonly HashSet<int> _usedInstances = [], _usedRoofs = [];
    internal int StaticBatchesReusedLastBuild { get; private set; }
    internal int StaticBatchesUploadedLastBuild { get; private set; }

    private MultiMesh UploadSolidInstances(Node3D root, int key, float[] buffer, int count,
        bool colors, StandardMaterial3D material)
    {
        bool retain = ReferenceEquals(root, _world);
        if (retain)
        {
            _usedInstances.Add(key);
            if (_retainedInstances.TryGetValue(key, out var existing))
            {
                if (!existing.Buffer.AsSpan().SequenceEqual(buffer))
                {
                    if (existing.Mesh.InstanceCount != count) existing.Mesh.InstanceCount = count;
                    existing.Mesh.Buffer = buffer;
                    existing.Buffer = buffer;
                    StaticBatchesUploadedLastBuild++;
                }
                else StaticBatchesReusedLastBuild++;
                existing.Node.MaterialOverride = material;
                return existing.Mesh;
            }
        }
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = colors,
            Mesh = _cube, InstanceCount = count, Buffer = buffer,
        };
        var node = new MultiMeshInstance3D { Multimesh = mesh, MaterialOverride = material };
        (retain ? _retainedSolids : root).AddChild(node);
        if (retain)
        {
            _retainedInstances.Add(key, new(mesh, node, buffer));
            StaticBatchesUploadedLastBuild++;
        }
        return mesh;
    }

    private bool ReuseRoof(int color, List<SurfaceVertex> vertices)
    {
        _usedRoofs.Add(color);
        if (!_retainedRoofs.TryGetValue(color, out var existing) || !existing.Vertices.SequenceEqual(vertices))
            return false;
        StaticBatchesReusedLastBuild++;
        return true;
    }

    private void UploadRoof(int color, List<SurfaceVertex> vertices, ArrayMesh mesh)
    {
        StaticBatchesUploadedLastBuild++;
        if (_retainedRoofs.TryGetValue(color, out var existing))
        {
            existing.Node.Mesh = mesh;
            existing.Mesh.Dispose();
            existing.Mesh = mesh;
            existing.Vertices = vertices;
        }
        else
        {
            var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color) };
            _retainedSolids.AddChild(node);
            _retainedRoofs.Add(color, new(mesh, node, vertices));
        }
    }

    private void PruneStaticBatches()
    {
        foreach (int key in _retainedInstances.Keys.Where(key => !_usedInstances.Contains(key)).ToArray())
        {
            var batch = _retainedInstances[key];
            _retainedSolids.RemoveChild(batch.Node);
            batch.Node.QueueFree();
            batch.Mesh.Dispose();
            _retainedInstances.Remove(key);
        }
        foreach (int key in _retainedRoofs.Keys.Where(key => !_usedRoofs.Contains(key)).ToArray())
        {
            var batch = _retainedRoofs[key];
            _retainedSolids.RemoveChild(batch.Node);
            batch.Node.QueueFree();
            batch.Mesh.Dispose();
            _retainedRoofs.Remove(key);
        }
    }

    private void DisposeStaticBatches()
    {
        foreach (var batch in _retainedInstances.Values) batch.Mesh.Dispose();
        foreach (var batch in _retainedRoofs.Values) batch.Mesh.Dispose();
        _retainedInstances.Clear();
        _retainedRoofs.Clear();
    }
}
