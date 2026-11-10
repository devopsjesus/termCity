using Godot;
using TermCity.Core.Rendering;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record WallTopFace(Transform3D Transform, int Color, NVector2[] Polygon, float Height, bool Extension);
    private readonly List<WallTopFace> _wallTopFaces = [];
    private readonly record struct IndexedWallTop(int Order, WallTopFace Face, NVector2 Min, NVector2 Max);
    private sealed record ClippedWallTop(WallTopFace[] Cutters, (Vector3 Point, Vector2 Uv)[] Vertices);
    private readonly Dictionary<WallTopFace, ClippedWallTop> _clippedWallTops = [];
    private readonly Dictionary<int, (Vector3 Point, Vector2 Uv)[]> _uploadedWallTops = [];
    private readonly Dictionary<(int Height, int X, int Y), List<IndexedWallTop>> _wallTopSpatial = [];
    private readonly Dictionary<int, List<(Vector3 Point, Vector2 Uv)>> _wallTopVertices = [];
    private int _wallTopOrder;
    private readonly Dictionary<int, MultiMeshInstance3D> _extensionWallInstances = [];
    private readonly Dictionary<int, List<Transform3D>> _extensionWallBatches = [];
    private readonly Dictionary<int, MeshInstance3D> _wallTopMeshes = [];
    private ArrayMesh? _wallOpenCube;
    internal IReadOnlyList<(int Color, Vector3 Point, Vector2 Uv)> WallTopGeometry =>
        _wallTopVertices.SelectMany(pair => pair.Value.Select(vertex => (pair.Key, vertex.Point, vertex.Uv))).ToArray();
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

    private void SyncWallTopsReference(int firstFace = 0, IReadOnlyDictionary<int, int>? previousBatches = null)
    {
        bool append = previousBatches is not null;
        if (!append)
        {
            foreach (var values in _wallTopSpatial.Values) values.Clear();
            foreach (var values in _wallTopVertices.Values) values.Clear();
            var live = _wallTopFaces.ToHashSet();
            foreach (var retired in _clippedWallTops.Keys.Where(face => !live.Contains(face)).ToArray())
                _clippedWallTops.Remove(retired);
            _wallTopOrder = 0;
            foreach (var instance in _wallTopMeshes.Values) instance.Visible = false;
        }
        if (_extensionWallBatches.Values.All(batch => batch.Count == 0))
        {
            SyncSpatialSiege(_fortifications, _extensionWallInstances, _extensionWallBatches, _wallOpenCube, buffered: true);
            foreach (var instance in _extensionWallInstances.Values) instance.Visible = false;
            foreach (var instance in _wallTopMeshes.Values) instance.Visible = false;
            if (_wallTopFaces.Count == 0) return;
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
        SyncSiegeBatches(_fortifications, _extensionWallInstances, _extensionWallBatches, _wallOpenCube,
            previousBatches, buffered: true);
        var changedColors = new HashSet<int>();
        IEnumerable<WallTopFace> faces = append ? _wallTopFaces.Skip(firstFace) : _wallTopFaces.OrderBy(face => face.Extension);
        foreach (var face in faces)
        {
            int height = (int)MathF.Round(face.Height * 10000);
            var min = face.Polygon.Aggregate(NVector2.Min);
            var max = face.Polygon.Aggregate(NVector2.Max);
            int left = (int)MathF.Floor(min.X / 4), right = (int)MathF.Floor(max.X / 4);
            int top = (int)MathF.Floor(min.Y / 4), bottom = (int)MathF.Floor(max.Y / 4);
            if (face.Extension)
            {
                if (!_wallTopVertices.TryGetValue(face.Color, out var vertices))
                    _wallTopVertices.Add(face.Color, vertices = []);
                changedColors.Add(face.Color);
                var candidates = new HashSet<IndexedWallTop>();
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                        if (_wallTopSpatial.TryGetValue((height, x, y), out var earlier)) candidates.UnionWith(earlier);
                var cutterFaces = candidates.OrderBy(candidate => candidate.Order)
                    .Where(p => p.Max.X > min.X && p.Min.X < max.X && p.Max.Y > min.Y && p.Min.Y < max.Y)
                    .Select(p => p.Face).ToArray();
                if (_clippedWallTops.TryGetValue(face, out var cached) && cached.Cutters.SequenceEqual(cutterFaces))
                {
                    vertices.AddRange(cached.Vertices);
                }
                else
                {
                int firstVertex = vertices.Count;
                var cutters = cutterFaces.Select(cutter => cutter.Polygon);
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
                            vertices.Add((point, new(local.X + .5f, local.Z + .5f)));
                        }
                    }
                _clippedWallTops[face] = new(cutterFaces, vertices.Skip(firstVertex).ToArray());
                }
            }
            var indexed = new IndexedWallTop(_wallTopOrder++, face, min, max);
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                {
                    if (!_wallTopSpatial.TryGetValue((height, x, y), out var values))
                        _wallTopSpatial.Add((height, x, y), values = []);
                    values.Add(indexed);
                }
        }
        foreach (int color in changedColors)
        {
            var vertices = _wallTopVertices[color];
            if (_uploadedWallTops.TryGetValue(color, out var uploaded) && vertices.SequenceEqual(uploaded))
            {
                if (_wallTopMeshes.TryGetValue(color, out var retained)) retained.Visible = retained.Mesh is not null;
                continue;
            }
            using var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var vertex in vertices)
            {
                surface.SetNormal(Vector3.Up);
                surface.SetUV(vertex.Uv);
                surface.AddVertex(vertex.Point);
            }
            if (!_wallTopMeshes.TryGetValue(color, out var instance))
            {
                instance = new MeshInstance3D { MaterialOverride = Material(color, solid: true) };
                _fortifications.AddChild(instance);
                _wallTopMeshes.Add(color, instance);
            }
            instance.Mesh?.Dispose();
            instance.Mesh = vertices.Count > 0 ? surface.Commit() : null;
            _uploadedWallTops[color] = vertices.ToArray();
            instance.Visible = instance.Mesh is not null;
        }
    }

    private void DisposeWallTops()
    {
        foreach (var instance in _extensionWallInstances.Values) instance.Multimesh.Dispose();
        foreach (var instance in _wallTopMeshes.Values) instance.Mesh?.Dispose();
        _wallOpenCube?.Dispose();
    }
}
