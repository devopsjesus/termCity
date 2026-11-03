using Godot;
using TermCity.Core.Rendering;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record WallTopFace(Transform3D Transform, int Color, NVector2[] Polygon, float Height, bool Extension);
    private readonly List<WallTopFace> _wallTopFaces = [];
    private readonly Dictionary<int, MultiMeshInstance3D> _extensionWallInstances = [];
    private readonly Dictionary<int, List<Transform3D>> _extensionWallBatches = [];
    private readonly Dictionary<int, MeshInstance3D> _wallTopMeshes = [];
    private ArrayMesh? _wallOpenCube;
    internal bool WallTopWindingCorrect
    {
        get
        {
            bool found = false;
            foreach (var instance in _wallTopMeshes.Values)
            {
                if (instance.Mesh is not ArrayMesh mesh) continue;
                var vertices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int i = 0; i + 2 < vertices.Length; i += 3)
                {
                    found = true;
                    if ((vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]).Y > .000001f) return false;
                }
            }
            return found;
        }
    }

    private void RecordWallTop(Transform3D transform, int color, bool extension)
    {
        var corners = new[] { new Vector3(-.5f, .5f, -.5f), new Vector3(.5f, .5f, -.5f),
            new Vector3(.5f, .5f, .5f), new Vector3(-.5f, .5f, .5f) }.Select(p => transform * p).ToArray();
        _wallTopFaces.Add(new(transform, color, corners.Select(p => new NVector2(p.X, p.Z)).ToArray(), corners[0].Y, extension));
    }

    private void SyncWallTops()
    {
        if (_extensionWallBatches.Values.All(batch => batch.Count == 0))
        {
            foreach (var instance in _extensionWallInstances.Values) instance.Visible = false;
            foreach (var instance in _wallTopMeshes.Values) instance.Visible = false;
            return;
        }
        if (_wallOpenCube is null)
        {
            var arrays = _cube.GetMeshArrays();
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            using var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < indices.Length; i += 3)
            {
                if (normals[indices[i]].Y > .9f) continue;
                for (int j = 0; j < 3; j++)
                {
                    int vertex = indices[i + j];
                    surface.SetNormal(normals[vertex]);
                    surface.SetUV(uv[vertex]);
                    surface.AddVertex(vertices[vertex]);
                }
            }
            _wallOpenCube = surface.Commit();
        }
        SyncSiegeBatches(_fortifications, _extensionWallInstances, _extensionWallBatches, _wallOpenCube);
        var surfaces = new Dictionary<int, SurfaceTool>();
        var nonEmpty = new HashSet<int>();
        foreach (var instance in _wallTopMeshes.Values) instance.Visible = false;
        foreach (var group in _wallTopFaces.GroupBy(f => (int)MathF.Round(f.Height * 10000)))
        {
            var earlier = new List<NVector2[]>();
            foreach (var face in group.OrderBy(f => f.Extension))
            {
                if (face.Extension)
                {
                    if (!surfaces.TryGetValue(face.Color, out var surface))
                    {
                        surfaces.Add(face.Color, surface = new SurfaceTool());
                        surface.Begin(Mesh.PrimitiveType.Triangles);
                    }
                    var min = face.Polygon.Aggregate(NVector2.Min);
                    var max = face.Polygon.Aggregate(NVector2.Max);
                    var cutters = earlier.Where(p => p.Max(v => v.X) > min.X && p.Min(v => v.X) < max.X &&
                        p.Max(v => v.Y) > min.Y && p.Min(v => v.Y) < max.Y);
                    var inverse = face.Transform.AffineInverse();
                    foreach (var polygon in WallDamageLayout.ExposedTop(face.Polygon, cutters))
                        for (int i = 1; i < polygon.Length - 1; i++)
                        {
                            var a = new Vector3(polygon[0].X, face.Height, polygon[0].Y);
                            var b = new Vector3(polygon[i].X, face.Height, polygon[i].Y);
                            var c = new Vector3(polygon[i + 1].X, face.Height, polygon[i + 1].Y);
                            var normal = (b - a).Cross(c - a);
                            if (normal.LengthSquared() < 1e-12f) continue;
                            if (normal.Y > 0) (b, c) = (c, b);
                            foreach (var point in new[] { a, b, c })
                            {
                                var local = inverse * point;
                                surface.SetNormal(Vector3.Up);
                                surface.SetUV(new(local.X + .5f, local.Z + .5f));
                                surface.AddVertex(point);
                                nonEmpty.Add(face.Color);
                            }
                        }
                }
                earlier.Add(face.Polygon);
            }
        }
        foreach (var (color, surface) in surfaces)
        {
            if (!_wallTopMeshes.TryGetValue(color, out var instance))
            {
                instance = new MeshInstance3D { MaterialOverride = Material(color, solid: true) };
                _fortifications.AddChild(instance);
                _wallTopMeshes.Add(color, instance);
            }
            instance.Mesh?.Dispose();
            instance.Mesh = nonEmpty.Contains(color) ? surface.Commit() : null;
            instance.Visible = instance.Mesh is not null;
            surface.Dispose();
        }
    }

    private void DisposeWallTops()
    {
        foreach (var instance in _extensionWallInstances.Values) instance.Multimesh.Dispose();
        foreach (var instance in _wallTopMeshes.Values) instance.Mesh?.Dispose();
        _wallOpenCube?.Dispose();
    }
}
