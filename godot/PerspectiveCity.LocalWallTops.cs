using Godot;
using TermCity.Core.Rendering;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly record struct TopBounds(int Height, NVector2 Min, NVector2 Max, int Left, int Right, int Back, int Front);
    private readonly Dictionary<WallTopFace, TopBounds> _localTopBounds = [];
    private readonly Dictionary<(int Height, int X, int Y), List<WallTopFace>> _localTopIndex = [];
    private readonly Dictionary<WallTopFace, int> _localTopOrder = [];
    private readonly Dictionary<WallTopFace, (Vector3 Point, Vector2 Uv)[]> _localTopVertices = [];

    private void SyncWallTops(int firstFace = 0, IReadOnlyDictionary<int, int>? previousBatches = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_wallOpenCube is null) SyncWallTopsReference();
        SyncSiegeBatches(_fortifications, _extensionWallInstances, _extensionWallBatches, _wallOpenCube,
            previousBatches, buffered: true);
        WallExtensionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ordered = _wallTopFaces.OrderBy(face => face.Extension).ToArray();
        var live = ordered.ToHashSet();
        _localTopOrder.Clear();
        for (int i = 0; i < ordered.Length; i++) _localTopOrder[ordered[i]] = i;
        var dirty = new HashSet<WallTopFace>();
        var colors = new HashSet<int>();
        foreach (var face in _localTopBounds.Keys.Where(face => !live.Contains(face)).ToArray())
        {
            var bounds = _localTopBounds[face];
            MarkNeighbors(bounds, dirty);
            Visit(bounds, key =>
            {
                if (_localTopIndex.TryGetValue(key, out var values)) values.Remove(face);
            });
            _localTopBounds.Remove(face);
            _localTopVertices.Remove(face);
            if (face.Extension) colors.Add(face.Color);
        }
        foreach (var face in ordered)
        {
            if (_localTopBounds.ContainsKey(face)) continue;
            var min = face.Polygon.Aggregate(NVector2.Min);
            var max = face.Polygon.Aggregate(NVector2.Max);
            var bounds = new TopBounds((int)MathF.Round(face.Height * 10000), min, max,
                (int)MathF.Floor(min.X / 4), (int)MathF.Floor(max.X / 4),
                (int)MathF.Floor(min.Y / 4), (int)MathF.Floor(max.Y / 4));
            _localTopBounds[face] = bounds;
            Visit(bounds, key =>
            {
                if (!_localTopIndex.TryGetValue(key, out var values)) _localTopIndex[key] = values = [];
                values.Add(face);
            });
            MarkNeighbors(bounds, dirty);
            if (face.Extension) dirty.Add(face);
        }
        foreach (var face in dirty.Where(live.Contains))
        {
            if (!face.Extension) continue;
            var bounds = _localTopBounds[face];
            var candidates = new HashSet<WallTopFace>();
            Visit(bounds, key =>
            {
                if (_localTopIndex.TryGetValue(key, out var values)) candidates.UnionWith(values);
            });
            var cutters = candidates.Where(candidate => _localTopOrder[candidate] < _localTopOrder[face] &&
                _localTopBounds[candidate].Max.X > bounds.Min.X && _localTopBounds[candidate].Min.X < bounds.Max.X &&
                _localTopBounds[candidate].Max.Y > bounds.Min.Y && _localTopBounds[candidate].Min.Y < bounds.Max.Y)
                .OrderBy(candidate => _localTopOrder[candidate]).Select(candidate => candidate.Polygon);
            var inverse = face.Transform.AffineInverse();
            var vertices = new List<(Vector3 Point, Vector2 Uv)>();
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
            _localTopVertices[face] = vertices.ToArray();
            colors.Add(face.Color);
        }
        WallClippingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        WallTopUploadsLastSync = 0;
        foreach (int color in colors)
        {
            if (!_wallTopVertices.TryGetValue(color, out var vertices)) _wallTopVertices[color] = vertices = [];
            vertices.Clear();
            foreach (var face in ordered.Where(face => face.Extension && face.Color == color))
                if (_localTopVertices.TryGetValue(face, out var values)) vertices.AddRange(values);
            if (_uploadedWallTops.TryGetValue(color, out var previous) && vertices.SequenceEqual(previous)) continue;
            if (!_wallTopMeshes.TryGetValue(color, out var instance))
            {
                instance = new MeshInstance3D { MaterialOverride = Material(color, solid: true) };
                _fortifications.AddChild(instance);
                _wallTopMeshes[color] = instance;
            }
            ArrayMesh? mesh = null;
            if (vertices.Count > 0)
            {
                var points = new Vector3[vertices.Count];
                var normals = new Vector3[vertices.Count];
                var uvs = new Vector2[vertices.Count];
                for (int i = 0; i < vertices.Count; i++)
                {
                    points[i] = vertices[i].Point;
                    normals[i] = Vector3.Up;
                    uvs[i] = vertices[i].Uv;
                }
                using var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = points;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
                mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
            var retired = instance.Mesh;
            instance.Mesh = mesh;
            retired?.Dispose();
            instance.Visible = instance.Mesh is not null;
            _uploadedWallTops[color] = vertices.ToArray();
            WallTopUploadsLastSync++;
        }
        WallTopUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void MarkNeighbors(TopBounds bounds, HashSet<WallTopFace> dirty) => Visit(bounds, key =>
    {
        if (_localTopIndex.TryGetValue(key, out var values))
            foreach (var face in values) if (face.Extension) dirty.Add(face);
    });

    private static void Visit(TopBounds bounds, Action<(int Height, int X, int Y)> action)
    {
        for (int y = bounds.Back; y <= bounds.Front; y++)
            for (int x = bounds.Left; x <= bounds.Right; x++) action((bounds.Height, x, y));
    }

    internal void VerifyLocalWallTopParity()
    {
        VerifyNativeWallTops();
        var before = WallTopGeometry.ToArray();
        SyncWallTopsReference();
        var reference = WallTopGeometry.ToArray();
        if (before.Length != reference.Length || !before.SequenceEqual(reference))
            throw new InvalidOperationException("Local wall-top clipping differs from the full reference traversal.");
    }

    private void VerifyNativeWallTops()
    {
        foreach (var (color, expected) in _wallTopVertices)
        {
            if (expected.Count == 0) continue;
            if (!_wallTopMeshes.TryGetValue(color, out var instance) || instance.Mesh is not ArrayMesh mesh)
                throw new InvalidOperationException("A clipped wall top has no native mesh.");
            using var arrays = mesh.SurfaceGetArrays(0);
            var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (points.Length != expected.Count || normals.Length != expected.Count || uvs.Length != expected.Count)
                throw new InvalidOperationException("Native wall-top attribute counts differ from clipped geometry.");
            for (int i = 0; i < expected.Count; i++)
                if (!points[i].IsEqualApprox(expected[i].Point) || !uvs[i].IsEqualApprox(expected[i].Uv) ||
                    normals[i].DistanceSquaredTo(Vector3.Up) > .000001f)
                    throw new InvalidOperationException("Native wall-top positions, UVs or normals differ from clipped geometry.");
        }
    }
}
